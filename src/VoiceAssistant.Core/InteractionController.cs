using System.Globalization;

namespace VoiceAssistant.Core;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusUpdate(InteractionMode Mode, StatusKind Kind, string Message, string? Heard = null);

public sealed record ControllerOptions
{
    public bool ReturnToCommandModeAfterSend { get; init; } = true;

    /// <summary>Commands below this average token probability are ignored rather than executed or typed.</summary>
    public float MinCommandConfidence { get; init; } = 0.3f;

    /// <summary>When true, listening starts in <see cref="InteractionMode.Standby"/> and commands need the wake word.</summary>
    public bool WakeWordEnabled { get; init; } = true;

    /// <summary>How long commands work without repeating the wake word (refreshed by each command).</summary>
    public TimeSpan AwakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A rephrasing within this time after an unrecognised phrase is learned as its meaning.</summary>
    public TimeSpan LearnWindow { get; init; } = TimeSpan.FromSeconds(30);

    public string WakeWordLabel { get; init; } = "Hey Jarvis";

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Owns the standby/command/dictation state machine. Not thread-safe: callers must invoke it from one serial queue.
/// </summary>
public sealed class InteractionController
{
    private readonly IDesktop _desktop;
    private readonly AppCatalog _apps;
    private readonly CommandParser _parser;
    private readonly DictationFormatter _formatter;
    private readonly ControllerOptions _options;
    private readonly PhraseMemory? _memory;
    private readonly Stack<int> _segments = new();
    private WindowRef? _target;
    private AppEntry? _targetProfile;
    private bool _boxHadText;
    private bool _needsLeadingSpace;
    private bool _endsSentence = true;
    private bool _searchField;
    private DateTime _awakeSince;
    private (string Phrase, DateTime At)? _unrecognized;

    public InteractionController(IDesktop desktop, AppCatalog apps, CommandParser parser, ControllerOptions? options = null, DictationFormatter? formatter = null, PhraseMemory? memory = null)
    {
        _desktop = desktop;
        _apps = apps;
        _parser = parser;
        _options = options ?? new ControllerOptions();
        _formatter = formatter ?? new DictationFormatter();
        _memory = memory;
    }

    public event Action<StatusUpdate>? Status;

    /// <summary>Raised when a voice command asks to stop listening; the host must turn the microphone off.</summary>
    public event Action? PauseRequested;

    /// <summary>Raised after something was learned (rephrasing or app use); the host persists the memory.</summary>
    public event Action? MemoryChanged;

    /// <summary>Content-free diagnostics (command kinds and outcomes, never transcripts) for the log.</summary>
    public event Action<string>? Trace;

    public InteractionMode Mode { get; private set; } = InteractionMode.Paused;

    /// <summary>Dictated text that could not be typed (focus moved, unverifiable field). Kept only in memory.</summary>
    public string? UntypedText { get; private set; }

    /// <summary>The last text typed by dictation, for “paste last transcript”. Kept only in memory.</summary>
    public string? LastTranscript { get; private set; }

    public WindowRef? Target => _target;

    private string IdleMode => _options.WakeWordEnabled ? $"Say “{_options.WakeWordLabel}” to give a command." : "Listening for a command.";

    public void SetListening(bool listening)
    {
        if (listening)
        {
            if (Mode == InteractionMode.Paused)
            {
                Mode = _options.WakeWordEnabled ? InteractionMode.Standby : InteractionMode.Command;
                Report(StatusKind.Info, IdleMode);
            }

            return;
        }

        if (Mode != InteractionMode.Paused)
        {
            Mode = InteractionMode.Paused;
            Report(StatusKind.Info, "Paused. Microphone is off.");
        }
    }

    public void ClearUntypedText() => UntypedText = null;

    /// <summary>Call periodically: returns to standby when the wake word's command window has expired.</summary>
    public void Tick()
    {
        if (ExpireAwake())
        {
            Report(StatusKind.Info, IdleMode);
        }
    }

    /// <param name="followsForcedCut">
    /// The audio continues a sentence that was cut at the length cap, so it is typed (or ignored) but never run as a command.
    /// </param>
    public async Task HandleTranscriptAsync(string transcript, float confidence, bool followsForcedCut = false, CancellationToken cancellationToken = default)
    {
        if (Mode == InteractionMode.Paused)
        {
            return;
        }

        ExpireAwake();
        var heard = TranscriptText.Clean(transcript);
        if (followsForcedCut && Mode == InteractionMode.Dictation)
        {
            // The tail of a sentence cut at the length cap: type it, never treat it as "send"/"stop typing".
            if (heard.Length > 0)
            {
                TypeDictated(heard, heard);
            }

            return;
        }

        var parsed = _parser.Parse(transcript, Mode);
        if (parsed.IsEmpty)
        {
            if (Mode == InteractionMode.Standby && _parser.Parse(transcript, InteractionMode.Command).Commands.Count > 0)
            {
                // A real command without the wake word: say why nothing happened instead of staying silent.
                Trace?.Invoke("standby: command without wake word");
                Report(StatusKind.Info, $"Say “{_options.WakeWordLabel}” first, then the command.", heard);
            }

            return;
        }

        if ((parsed.Commands.Count > 0 || parsed.IsWakeOnly) && confidence < _options.MinCommandConfidence)
        {
            Trace?.Invoke("low confidence command ignored");
            Report(StatusKind.Warning, "Not sure I heard that right — ignored.", heard);
            return;
        }

        if (parsed.IsWakeOnly)
        {
            if (Mode != InteractionMode.Dictation)
            {
                Awake();
                Trace?.Invoke("wake");
                Report(StatusKind.Info, "Yes? Say a command.", heard);
            }

            return;
        }

        if (parsed.Unrecognized is not null)
        {
            if (Mode == InteractionMode.Standby)
            {
                Awake();
            }

            _unrecognized = (parsed.Unrecognized, _options.Clock());
            Trace?.Invoke("unrecognized command");
            Report(StatusKind.Warning, "Not a command I know. Say it another way and I'll remember.", heard);
            return;
        }

        if (parsed.DictationText is not null)
        {
            TypeDictated(parsed.DictationText, heard);
            return;
        }

        if (Mode == InteractionMode.Standby)
        {
            Awake();
        }

        var allSucceeded = true;
        foreach (var command in parsed.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = await ExecuteAsync(command, heard, cancellationToken).ConfigureAwait(false);
            Trace?.Invoke($"command {command.Kind} {(ok ? "ok" : "failed")}{(parsed.FromLearnedPhrase ? " (learned phrase)" : string.Empty)}");
            if (!ok)
            {
                allSucceeded = false;
                break;
            }
        }

        LearnRephrasing(parsed, allSucceeded, heard);

        if (Mode == InteractionMode.Command)
        {
            _awakeSince = _options.Clock();
        }
    }

    /// <summary>
    /// If the user just said something unrecognised and now rephrased it as a working command, remember
    /// the first phrase as meaning the second (PhraseMemory only accepts pairs sharing a meaningful word).
    /// </summary>
    private void LearnRephrasing(ParsedUtterance parsed, bool succeeded, string heard)
    {
        var pending = _unrecognized;
        _unrecognized = null;
        if (_memory is null || !succeeded || parsed.CommandText is null || pending is null ||
            _options.Clock() - pending.Value.At > _options.LearnWindow)
        {
            return;
        }

        if (_memory.Learn(pending.Value.Phrase, parsed.CommandText))
        {
            Trace?.Invoke("learned a rephrasing");
            MemoryChanged?.Invoke();
            Report(StatusKind.Info, $"Got it — next time “{pending.Value.Phrase}” will do that too.", heard);
        }
    }

    /// <summary>
    /// Push-to-talk / hands-free dictation: formats the transcript and types it wherever the cursor is.
    /// The user's explicit key press is the consent, so only password and administrator windows are refused.
    /// </summary>
    public void InsertDictation(string transcript)
    {
        var formatted = _formatter.Format(transcript);
        if (formatted.Length == 0)
        {
            Report(StatusKind.Info, "I didn't catch anything to type.");
            return;
        }

        InsertIntoFocused(formatted, TranscriptText.Clean(transcript));
    }

    /// <summary>Types the last dictated text again into the focused field.</summary>
    public void RepeatLastTranscript()
    {
        if (string.IsNullOrEmpty(LastTranscript))
        {
            Report(StatusKind.Info, "There's no previous dictation yet.");
            return;
        }

        InsertIntoFocused(LastTranscript, null);
    }

    private async Task<bool> ExecuteAsync(VoiceCommand command, string heard, CancellationToken ct)
    {
        switch (command.Kind)
        {
            case CommandKind.OpenApp:
                return await OpenAsync(command.Argument ?? string.Empty, newWindow: true, heard, ct).ConfigureAwait(false);

            case CommandKind.SwitchToApp:
                return await OpenAsync(command.Argument ?? string.Empty, newWindow: false, heard, ct).ConfigureAwait(false);

            case CommandKind.StartTyping:
                return await StartTypingAsync(heard, ct).ConfigureAwait(false);

            case CommandKind.FocusSearch:
                return await FocusSearchAsync(heard, addressBar: false, ct).ConfigureAwait(false);

            case CommandKind.FocusAddressBar:
                return await FocusSearchAsync(heard, addressBar: true, ct).ConfigureAwait(false);

            case CommandKind.SearchFor:
                if (!await FocusSearchAsync(heard, addressBar: false, ct).ConfigureAwait(false))
                {
                    return false;
                }

                if (!TypeDictated(command.Argument ?? string.Empty, heard))
                {
                    return false;
                }

                return Send(heard, "Searching.");

            case CommandKind.PressKeys:
                return PressKeys(command.Argument ?? string.Empty, heard);

            case CommandKind.Send:
                return Send(heard, "Sent.");

            case CommandKind.ScratchThat:
                if (!EnsureTargetFocused(heard, null))
                {
                    return false;
                }

                if (_segments.Count == 0)
                {
                    Report(StatusKind.Warning, "Nothing to undo.", heard);
                    return false;
                }

                _desktop.Backspace(_segments.Pop());
                _needsLeadingSpace = _segments.Count > 0 || _boxHadText;
                Report(StatusKind.Success, "Removed the last phrase.", heard);
                return true;

            case CommandKind.ClearBox:
                if (!EnsureTargetFocused(heard, null))
                {
                    return false;
                }

                _desktop.PressKeys("Ctrl+A");
                _desktop.Backspace(1);
                _segments.Clear();
                _boxHadText = false;
                _needsLeadingSpace = false;
                _endsSentence = true;
                Report(StatusKind.Success, "Cleared.", heard);
                return true;

            case CommandKind.NewLine:
            case CommandKind.NewParagraph:
                if (!EnsureTargetFocused(heard, null))
                {
                    return false;
                }

                var lines = command.Kind == CommandKind.NewLine ? 1 : 2;
                for (var i = 0; i < lines; i++)
                {
                    _desktop.PressKeys(NewLineKeys);
                }

                _segments.Push(lines);
                _needsLeadingSpace = false;
                _endsSentence = true;
                Report(StatusKind.Success, lines == 1 ? "New line." : "New paragraph.", heard);
                return true;

            case CommandKind.StopTyping:
                if (Mode == InteractionMode.Dictation)
                {
                    Awake();
                    Report(StatusKind.Info, "Stopped typing. Nothing was sent.", heard);
                }

                return true;

            case CommandKind.PauseListening:
                Mode = InteractionMode.Paused;
                Report(StatusKind.Info, "Paused. Microphone is off.", heard);
                PauseRequested?.Invoke();
                return false;

            default:
                return false;
        }
    }

    private string NewLineKeys => _targetProfile?.NewLineKeys ?? "Shift+Enter";

    private async Task<bool> OpenAsync(string spokenName, bool newWindow, string heard, CancellationToken ct)
    {
        switch (_apps.Resolve(spokenName))
        {
            case AppResolution.Found found:
                Report(StatusKind.Info, $"Opening {found.App.Name}…", heard);
                var result = await _desktop.OpenOrActivateAsync(found.App, newWindow, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    _memory?.RecordAppUse(found.App.Name);
                    MemoryChanged?.Invoke();
                }

                if (result.Success && result.Window is null)
                {
                    // Launched, but we can't tell which window is the app, so don't chain "start typing" into a guess.
                    Report(StatusKind.Info, $"{result.Message} Say “start typing” when it's ready.", heard);
                    return false;
                }

                Report(result.Success ? StatusKind.Success : StatusKind.Warning, result.Message, heard);
                return result.Success;

            case AppResolution.Ambiguous ambiguous:
                Report(StatusKind.Warning, $"Which one? {string.Join(", ", ambiguous.Names.Take(4))}", heard);
                return false;

            default:
                Report(StatusKind.Warning, $"I couldn't find an app called “{spokenName}”.", heard);
                return false;
        }
    }

    private async Task<bool> StartTypingAsync(string heard, CancellationToken ct)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return false;
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var prepared = await _desktop.PrepareTextTargetAsync(window, profile, ct).ConfigureAwait(false);
        if (!prepared.Success)
        {
            Report(StatusKind.Warning, prepared.Message, heard);
            return false;
        }

        if (profile?.SearchKeys is not null && profile.TextArea is null && prepared.FocusIsEditable == false)
        {
            // Browsers: if the cursor isn't in a text box on the page, "start typing" means the address/search bar.
            return await FocusSearchAsync(heard, addressBar: false, ct).ConfigureAwait(false);
        }

        BindTarget(window, profile, prepared.HasTrailingText == true, searchField: false);
        Report(StatusKind.Success, $"Typing into {profile?.Name ?? DisplayName(window)}. Say “stop typing” when done.", heard);
        return true;
    }

    private async Task<bool> FocusSearchAsync(string heard, bool addressBar, CancellationToken ct)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return false;
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var name = profile?.Name ?? DisplayName(window);
        var keys = addressBar ? profile?.AddressBarKeys : profile?.SearchKeys;
        if (keys is not null)
        {
            if (!CheckField(heard, null, allowUnknown: true))
            {
                return false;
            }

            _desktop.PressKeys(keys);
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        else if (addressBar)
        {
            Report(StatusKind.Warning, $"{name} doesn't have an address bar I know.", heard);
            return false;
        }
        else
        {
            var focused = await _desktop.FocusSearchBoxAsync(window, ct).ConfigureAwait(false);
            if (!focused.Success)
            {
                Report(StatusKind.Warning, focused.Message, heard);
                return false;
            }
        }

        BindTarget(window, profile, boxHadText: false, searchField: true);
        Report(StatusKind.Success, addressBar ? "In the address bar. Say where to go, then “search”." : "In the search box. Say what to search for, then “search”.", heard);
        return true;
    }

    private void BindTarget(WindowRef window, AppEntry? profile, bool boxHadText, bool searchField)
    {
        _target = window;
        _targetProfile = profile;
        _segments.Clear();
        _boxHadText = boxHadText;
        _needsLeadingSpace = boxHadText;
        _endsSentence = true;
        _searchField = searchField;
        Mode = InteractionMode.Dictation;
    }

    private bool PressKeys(string chord, string heard)
    {
        if (Mode == InteractionMode.Dictation)
        {
            if (!EnsureTargetFocused(heard, null))
            {
                return false;
            }
        }
        else if (_desktop.GetForegroundWindow() is null)
        {
            Report(StatusKind.Warning, "There's no window in front.", heard);
            return false;
        }

        if (!CheckField(heard, null, allowUnknown: true))
        {
            return false;
        }

        _desktop.PressKeys(chord);

        // Keys can move the caret or change the text, so earlier phrases can no longer be undone by length.
        _segments.Clear();
        Report(StatusKind.Success, $"Pressed {chord}.", heard);
        return true;
    }

    private bool Send(string heard, string doneMessage)
    {
        if (_target is null)
        {
            Report(StatusKind.Warning, "Say “start typing” first.", heard);
            return false;
        }

        if (!EnsureTargetFocused(heard, null))
        {
            return false;
        }

        var submitKeys = _targetProfile is null ? "Enter" : _targetProfile.SubmitKeys;
        if (string.IsNullOrWhiteSpace(submitKeys))
        {
            Report(StatusKind.Warning, $"{_targetProfile!.Name} has no send action.", heard);
            return false;
        }

        _desktop.PressKeys(submitKeys);
        _segments.Clear();
        _boxHadText = false;
        _needsLeadingSpace = false;
        _endsSentence = true;
        if (Mode == InteractionMode.Dictation && _options.ReturnToCommandModeAfterSend)
        {
            Awake();
        }

        Report(StatusKind.Success, doneMessage, heard);
        return true;
    }

    /// <summary>Formats and types dictated text into the bound target. Returns false if nothing was typed.</summary>
    private bool TypeDictated(string text, string heard)
    {
        if (!EnsureTargetFocused(heard, text))
        {
            return false;
        }

        if (!CheckField(heard, text, allowUnknown: false))
        {
            return false;
        }

        var formatted = _formatter.Format(text, continuesSentence: !_endsSentence, searchField: _searchField);
        if (formatted.Length == 0)
        {
            return false;
        }

        var toType = _needsLeadingSpace && formatted[0] != '\n' ? " " + formatted : formatted;
        _segments.Push(TypeWithNewLines(toType, NewLineKeys));
        _needsLeadingSpace = !char.IsWhiteSpace(toType[^1]);
        _endsSentence = toType[^1] is '.' or '!' or '?' or '\n' or ':';
        LastTranscript = formatted;
        Report(StatusKind.Success, "Typed.", heard);
        return true;
    }

    private void InsertIntoFocused(string formatted, string? heard)
    {
        var window = _desktop.GetForegroundWindow();
        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return;
        }

        if (!CheckField(heard ?? string.Empty, formatted, allowUnknown: true))
        {
            return;
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var leadingSpace = _desktop.FocusedTextEndsWithNonWhitespace() == true && formatted[0] != '\n';
        TypeWithNewLines(leadingSpace ? " " + formatted : formatted, profile?.NewLineKeys ?? "Shift+Enter");

        // Push-to-talk text isn't part of the voice “scratch that” history.
        _segments.Clear();
        LastTranscript = formatted;
        Report(StatusKind.Success, "Typed.", heard);
    }

    private int TypeWithNewLines(string text, string newLineKeys)
    {
        var count = 0;
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                _desktop.PressKeys(newLineKeys);
                count++;
            }

            if (parts[i].Length > 0)
            {
                _desktop.TypeText(parts[i]);
                count += new StringInfo(parts[i]).LengthInTextElements;
            }
        }

        return count;
    }

    /// <summary>Refuses password fields and administrator windows (and, unless allowed, fields that can't be checked).</summary>
    private bool CheckField(string heard, string? pendingText, bool allowUnknown)
    {
        var check = _desktop.CheckFocusedField();
        var refusal = check switch
        {
            FieldCheck.Password => "That's a password field — I won't type there.",
            FieldCheck.Elevated => "That window runs as administrator; Windows won't let me type into it.",
            FieldCheck.Unknown when !allowUnknown => "I couldn't check the focused field, so nothing was typed. The text is in the tray menu.",
            _ => null,
        };

        if (refusal is null)
        {
            return true;
        }

        if (pendingText is not null && check == FieldCheck.Unknown)
        {
            UntypedText = string.IsNullOrEmpty(UntypedText) ? pendingText : UntypedText + " " + pendingText;
        }

        Report(StatusKind.Warning, refusal, heard);
        return false;
    }

    /// <summary>Refuses to act if the bound window is no longer in front, keeping any dictated text in memory.</summary>
    private bool EnsureTargetFocused(string heard, string? pendingText)
    {
        var foreground = _desktop.GetForegroundWindow();
        if (_target is not null && _target.SameWindow(foreground))
        {
            return true;
        }

        if (pendingText is not null)
        {
            UntypedText = string.IsNullOrEmpty(UntypedText) ? pendingText : UntypedText + " " + pendingText;
        }

        if (Mode == InteractionMode.Dictation)
        {
            Awake();
        }

        Report(
            StatusKind.Warning,
            pendingText is null
                ? "Focus moved to another window, so I didn't do that. Say “start typing” to continue."
                : "Focus moved to another window, so nothing was typed. Say “start typing” to continue.",
            heard);
        return false;
    }

    private void Awake()
    {
        Mode = InteractionMode.Command;
        _awakeSince = _options.Clock();
    }

    private bool ExpireAwake()
    {
        if (_options.WakeWordEnabled && Mode == InteractionMode.Command && _options.Clock() - _awakeSince > _options.AwakeTimeout)
        {
            Mode = InteractionMode.Standby;
            return true;
        }

        return false;
    }

    private static string DisplayName(WindowRef window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;

    private void Report(StatusKind kind, string message, string? heard = null) =>
        Status?.Invoke(new StatusUpdate(Mode, kind, message, heard));
}
