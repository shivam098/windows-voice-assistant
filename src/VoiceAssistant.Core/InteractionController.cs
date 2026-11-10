using System.Globalization;

namespace VoiceAssistant.Core;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <param name="Place">Where the assistant is acting, e.g. “Chrome › YouTube › Search”, after a navigation command.</param>
public sealed record StatusUpdate(InteractionMode Mode, StatusKind Kind, string Message, string? Heard = null, string? Place = null);

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
public sealed partial class InteractionController
{
    private readonly IDesktop _desktop;
    private readonly AppCatalog _apps;
    private readonly CommandParser _parser;
    private readonly DictationFormatter _formatter;
    private readonly ControllerOptions _options;
    private readonly PhraseMemory? _memory;
    private readonly Stack<int> _segments = new();

    /// <summary>The end of what was last typed (or left after an edit), to trust a field's text only while it still ends this way.</summary>
    private string? _typedTail;
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

    /// <summary>The idea board voice commands add to; null turns them off.</summary>
    public VoiceAssistant.Board.IBoard? Board { get; set; }

    /// <summary>“show my ideas”: the host opens the board (on that list, or the whole board for null).</summary>
    public event Action<VoiceAssistant.Board.BoardList?>? ShowBoardRequested;

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
        ExpirePending();
        if (ExpireAwake())
        {
            Report(StatusKind.Info, IdleMode);
        }
    }

    /// <param name="followsForcedCut">
    /// The audio continues a sentence that was cut at the length cap, so it is typed (or ignored) but never run as a command.
    /// </param>
    /// <param name="dictationTranscript">
    /// While typing, supplies the text to type from a second (e.g. Hindi + English) model. The command check still uses
    /// <paramref name="transcript"/>; this is only called when the utterance is to be typed.
    /// </param>
    public async Task HandleTranscriptAsync(
        string transcript,
        float confidence,
        bool followsForcedCut = false,
        CancellationToken cancellationToken = default,
        Func<Task<string>>? dictationTranscript = null)
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
            if (dictationTranscript is not null)
            {
                heard = TranscriptText.Clean(await dictationTranscript().ConfigureAwait(false));
            }

            if (heard.Length > 0)
            {
                TypeDictated(heard, heard);
            }

            return;
        }

        var parsed = _parser.Parse(transcript, Mode);
        if (dictationTranscript is not null && Mode == InteractionMode.Dictation && (parsed.IsEmpty || parsed.DictationText is not null))
        {
            // Not a command: what the English model heard may be empty or garbled (e.g. Hindi), so type the second model's text.
            var text = TranscriptText.Clean(await dictationTranscript().ConfigureAwait(false));
            if (text.Length > 0)
            {
                TypeDictated(text, text);
            }

            return;
        }

        if (_confirm is not null && !(parsed.Commands.Count == 1 && parsed.Commands[0].Kind == CommandKind.Confirm))
        {
            // A destructive click waits only for the very next utterance to be "yes"; anything else drops it.
            _confirm = null;
        }

        if (parsed.IsEmpty)
        {
            if (Mode == InteractionMode.Standby &&
                _parser.Parse(transcript, InteractionMode.Command).Commands.Any(c => c.Kind is not (CommandKind.Confirm or CommandKind.Cancel or CommandKind.Choose or CommandKind.ShowNumbers or CommandKind.HideNumbers)))
            {
                // A real command without the wake word: say why nothing happened instead of staying silent.
                // ("yes", "no", "two" are everyday words: stay silent for those.)
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

        if (parsed.LeavesTyping && Mode == InteractionMode.Dictation)
        {
            // "Hey Jarvis, next tab" while typing: stop typing first, so dictation never lands in the new place.
            Awake();
        }

        var allSucceeded = true;
        foreach (var command in parsed.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Kind is not (CommandKind.Choose or CommandKind.Confirm or CommandKind.Cancel or CommandKind.HideNumbers))
            {
                // Any other command makes numbered choices and pending confirmations stale.
                ClearPending();
            }

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
        // Push-to-talk that names a list ("add to my ideas …") is filed on the board instead of typed.
        if (Board is not null && VoiceAssistant.Board.BoardGrammar.Parse(TranscriptText.Clean(transcript), BoardNow(), explicitOnly: true) is { Action: VoiceAssistant.Board.BoardAction.Add } intent)
        {
            ExecuteBoard(intent, TranscriptText.Clean(transcript));
            return;
        }

        var formatted = _formatter.Format(transcript);
        if (formatted.Length == 0)
        {
            Report(StatusKind.Info, "I didn't catch anything to type.");
            return;
        }

        InsertIntoFocused(formatted, TranscriptText.Clean(transcript), fileIfNoTextBox: true);
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
        if (Mode == InteractionMode.Dictation && command.Kind is CommandKind.OpenApp or CommandKind.SwitchToApp or CommandKind.SelectTab or CommandKind.Click or CommandKind.ShowNumbers)
        {
            // "Hey Jarvis, go to the first tab" while typing: stop typing first so nothing lands in the new place.
            Awake();
        }

        if (MovesAway(command))
        {
            // The page or app under the typing target may change: "send" must not press Enter in the new place.
            Unbind();
        }

        switch (command.Kind)
        {
            case CommandKind.OpenApp:
                return await NavigateAsync(command.Argument ?? string.Empty, newWindow: true, heard, ct).ConfigureAwait(false);

            case CommandKind.SwitchToApp:
                return await NavigateAsync(command.Argument ?? string.Empty, newWindow: false, heard, ct).ConfigureAwait(false);

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
                _typedTail = null;
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
                _typedTail = null;
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

            case CommandKind.SelectTab:
                return await SelectTabAsync(command.Target ?? new ElementTarget(command.Argument ?? string.Empty), heard, ct).ConfigureAwait(false);

            case CommandKind.Click:
                return await ClickAsync(command.Target ?? new ElementTarget(command.Argument ?? string.Empty), heard, minScore: NameMatcher.Fuzzy, ct).ConfigureAwait(false);

            case CommandKind.FocusField:
                return await FocusFieldAsync(command.Target ?? new ElementTarget(command.Argument ?? string.Empty), heard, ct).ConfigureAwait(false);

            case CommandKind.ShowNumbers:
                return await ShowNumbersAsync(heard, ct).ConfigureAwait(false);

            case CommandKind.HideNumbers:
                ClearPending();
                Report(StatusKind.Info, "Numbers hidden.", heard);
                return true;

            case CommandKind.Choose:
                return await ChooseAsync(int.Parse(command.Argument ?? "0", CultureInfo.InvariantCulture), heard, ct).ConfigureAwait(false);

            case CommandKind.Confirm:
                return await ConfirmAsync(heard, ct).ConfigureAwait(false);

            case CommandKind.Cancel:
                var hadPending = _choices.Count > 0 || _confirm is not null;
                ClearPending();
                Report(StatusKind.Info, hadPending ? "Cancelled." : "Nothing to cancel.", heard);
                return hadPending;

            case CommandKind.DeleteText:
            case CommandKind.ReplaceText:
                return EditText(command.Edit ?? new TextEdit(), heard);

            case CommandKind.Board:
                return command.Board is { } intent && ExecuteBoard(intent, heard);

            default:
                return false;
        }
    }

    /// <summary>
    /// “delete the last 3 words”, “delete the word very”, “replace Monday with Tuesday” in the field being typed in
    /// (or, when not typing, the focused field). The text before the cursor decides exactly what to erase; without it,
    /// only “last N words” works (Ctrl+Backspace), the rest say why.
    /// </summary>
    private bool EditText(TextEdit edit, string heard)
    {
        var typing = Mode == InteractionMode.Dictation;
        if (typing ? !EnsureTargetFocused(heard, null) : _desktop.GetForegroundWindow() is null)
        {
            if (!typing)
            {
                Report(StatusKind.Warning, "There's no window in front.", heard);
            }

            return false;
        }

        // Outside typing, the focused field must be positively checked: keys sent to a file list or a page do other things.
        if (!CheckField(heard, null, allowUnknown: typing))
        {
            return false;
        }

        var caret = _desktop.ReadTextBeforeCaret();
        if (caret is { HasSelection: true })
        {
            Report(StatusKind.Warning, "Some text is selected. Click where you want to edit (or say “press right”), then say it again.", heard);
            return false;
        }

        if (caret is { Editable: false })
        {
            Report(StatusKind.Warning, "That isn't a text box I can edit.", heard);
            return false;
        }

        // When only the whole text is readable, trust "the cursor is at the end" only if it ends with what was just typed.
        var before = caret is null ? null
            : caret.CaretKnown ? caret.Before
            : typing && _typedTail is { Length: > 0 } tail && TextEditing.NormalizeNewLines(caret.Before).TrimEnd().EndsWith(tail, StringComparison.Ordinal) ? caret.Before
            : null;
        if (before is null)
        {
            if (typing && edit.Phrase is null && edit.Unit == EditUnit.Word)
            {
                _desktop.PressKeysRepeated("Ctrl+Backspace", edit.Count);
                ForgetTypedLengths();
                Report(StatusKind.Success, edit.Count == 1 ? "Deleted the last word." : $"Deleted the last {edit.Count} words.", heard);
                return true;
            }

            if (typing && edit.Phrase is null && edit.Unit == EditUnit.Character)
            {
                _desktop.Backspace(edit.Count);
                ForgetTypedLengths();
                Report(StatusKind.Success, edit.Count == 1 ? "Deleted the last character." : $"Deleted the last {edit.Count} characters.", heard);
                return true;
            }

            Report(StatusKind.Warning, "I can't read the text in this box, so I can't find that. Say “delete the last 3 words” or “press backspace”.", heard);
            return false;
        }

        var plan = edit.Phrase is null
            ? TextEditing.DeleteLast(before, edit.Unit, edit.Count)
            : TextEditing.ReplaceLast(before, edit.Phrase, edit.Replacement);
        if (plan is null)
        {
            Report(
                StatusKind.Warning,
                edit.Phrase is null ? "There's nothing before the cursor to delete (or it's too far back)." : $"I couldn't find “{Short(edit.Phrase, 40)}” before the cursor.",
                heard);
            return false;
        }

        if (plan.KeepAfter > 0)
        {
            _desktop.PressKeysRepeated("Left", plan.KeepAfter);
        }

        // Select, then delete or type over the selection: the arrow keys move in the app's own units.
        _desktop.PressKeysRepeated("Shift+Left", plan.Delete);
        if (plan.Insert.Length > 0)
        {
            _desktop.TypeText(plan.Insert);
        }
        else
        {
            _desktop.Backspace(1);
        }

        if (plan.KeepAfter > 0)
        {
            _desktop.PressKeysRepeated("Right", plan.KeepAfter);
        }

        ForgetTypedLengths();
        var rest = plan.TextAfterEdit;
        _typedTail = Tail(rest);
        _boxHadText = rest.Length > 0;
        _needsLeadingSpace = rest.Length > 0 && !char.IsWhiteSpace(rest[^1]);
        _endsSentence = rest.TrimEnd(' ').Length == 0 || rest.TrimEnd(' ')[^1] is '.' or '!' or '?' or '\n' or ':';
        Report(StatusKind.Success, DescribeEdit(edit), heard);
        return true;
    }

    /// <summary>Lengths of earlier phrases no longer match the text, so "scratch that" can't undo by length any more.</summary>
    private void ForgetTypedLengths()
    {
        _segments.Clear();
        _typedTail = null;
    }

    /// <summary>The end of the text, trimmed, to recognise “the cursor is still where I typed” later.</summary>
    private static string? Tail(string text)
    {
        var trimmed = TextEditing.NormalizeNewLines(text).TrimEnd();
        return trimmed.Length == 0 ? null : trimmed[Math.Max(0, trimmed.Length - 40)..];
    }

    private DateTimeOffset BoardNow() => _parser.BoardClock();

    /// <summary>Adds to the idea board, opens it, or ticks an item off; says what happened.</summary>
    private bool ExecuteBoard(VoiceAssistant.Board.BoardIntent intent, string heard, bool noTextBox = false)
    {
        if (Board is null)
        {
            Report(StatusKind.Warning, "The idea board isn't available.", heard);
            return false;
        }

        switch (intent.Action)
        {
            case VoiceAssistant.Board.BoardAction.Add:
                var list = intent.List ?? VoiceAssistant.Board.BoardList.Ideas;
                var item = Board.Add(list, intent.Text, intent.Due);
                Trace?.Invoke($"board add {list}{(item.Due is null ? string.Empty : " with time")}{(noTextBox ? " (no text box)" : string.Empty)}");
                var lead = noTextBox ? "No text box, so I saved it. " : string.Empty;
                Report(StatusKind.Success, lead + (item.Due is { } due
                    ? $"Reminder {(due > BoardNow() && due - BoardNow() < TimeSpan.FromHours(1) ? string.Empty : "for ")}{DescribeDue(due, BoardNow())}: “{Short(item.Text, 50)}”."
                    : $"Added to {ListName(list)}: “{Short(item.Text, 50)}”."), heard);
                return true;

            case VoiceAssistant.Board.BoardAction.Show:
                ShowBoardRequested?.Invoke(intent.List);
                Report(StatusKind.Info, intent.List is { } shown ? $"Opening {ListName(shown)}." : "Opening your idea board.", heard);
                return true;

            default:
                var done = Board.CompleteMatching(intent.Text, intent.List);
                Report(
                    done is null ? StatusKind.Warning : StatusKind.Success,
                    done is null ? $"I couldn't find “{Short(intent.Text, 40)}” on your board." : $"Done: “{Short(done.Text, 50)}”.",
                    heard);
                return done is not null;
        }
    }

    private static string ListName(VoiceAssistant.Board.BoardList list) => list switch
    {
        VoiceAssistant.Board.BoardList.Ideas => "Ideas",
        VoiceAssistant.Board.BoardList.Brainstorm => "Brainstorm",
        VoiceAssistant.Board.BoardList.Todo => "To-do",
        _ => "Reminders",
    };

    /// <summary>“today 5:00 PM”, “tomorrow 9:00 AM”, “Fri 13 Nov 10:00 AM”, “in 10 min”.</summary>
    public static string DescribeDue(DateTimeOffset due, DateTimeOffset now)
    {
        var time = due.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (due > now && due - now < TimeSpan.FromHours(1))
        {
            return $"in {Math.Max(1, (int)Math.Round((due - now).TotalMinutes))} min";
        }

        return (due.Date - now.Date).Days switch
        {
            0 => $"today {time}",
            1 => $"tomorrow {time}",
            _ => due.ToString("ddd d MMM ", CultureInfo.InvariantCulture) + time,
        };
    }

    private static string DescribeEdit(TextEdit edit)
    {
        if (edit.Phrase is not null)
        {
            return edit.Replacement is null
                ? $"Deleted “{Short(edit.Phrase, 40)}”."
                : $"Replaced “{Short(edit.Phrase, 30)}” with “{Short(edit.Replacement, 30)}”.";
        }

        var unit = edit.Unit.ToString().ToLowerInvariant();
        return edit.Count == 1 ? $"Deleted the last {unit}." : $"Deleted the last {edit.Count} {unit}s.";
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

                if (result.Success && result.Window is { } opened)
                {
                    SetPlace(opened, PlaceOf(opened, _apps.FindProfileForProcess(opened.ProcessName, opened.Title)));
                }

                Report(result.Success ? StatusKind.Success : StatusKind.Warning, result.Message, heard, result.Success ? _place : null);
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
            // Browsers: a known site's box ("start typing" on YouTube = its search box), then the page's own text
            // boxes (the only one, or ask which), else the address/search bar.
            if (SiteProfile.ForTitle(window.Title) is { } site && await FocusSiteBoxAsync(window, site, heard, ct).ConfigureAwait(false))
            {
                return true;
            }

            if (await PickTextBoxAsync(window, heard, ct, browser: true).ConfigureAwait(false) is { } pickedInPage)
            {
                return pickedInPage;
            }

            return await FocusSearchAsync(heard, addressBar: false, ct).ConfigureAwait(false);
        }

        if (profile?.TextArea is null && prepared.FocusIsEditable == false)
        {
            // Nothing editable has focus in an app we have no profile for: pick its text box, or ask which.
            var picked = await PickTextBoxAsync(window, heard, ct).ConfigureAwait(false);
            if (picked is not null)
            {
                return picked.Value;
            }
        }

        BindTarget(window, profile, prepared.HasTrailingText == true, searchField: false);
        SetPlace(window, PlaceOf(window, profile));
        Report(StatusKind.Success, $"Typing into “{profile?.Name ?? DisplayName(window)}”. Say “stop typing” when done.", heard, _place);
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
        SetPlace(window, $"{PlaceOf(window, profile)} › {(addressBar ? "Address bar" : "Search")}");
        Report(StatusKind.Success, addressBar ? "In the address bar. Say where to go, then “search”." : "In the search box. Say what to search for, then “search”.", heard, _place);
        return true;
    }

    private void BindTarget(WindowRef window, AppEntry? profile, bool boxHadText, bool searchField)
    {
        ClearPending();
        _target = window;
        _targetProfile = profile;
        _segments.Clear();
        _typedTail = null;
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
        _typedTail = null;
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
        _typedTail = null;
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
        _typedTail = Tail(toType) ?? _typedTail;
        _needsLeadingSpace = !char.IsWhiteSpace(toType[^1]);
        _endsSentence = toType[^1] is '.' or '!' or '?' or '\n' or ':';
        LastTranscript = formatted;
        Report(StatusKind.Success, "Typed.", heard);
        return true;
    }

    private void InsertIntoFocused(string formatted, string? heard, bool fileIfNoTextBox = false)
    {
        var window = _desktop.GetForegroundWindow();
        var check = window is null ? (FieldCheck?)null : _desktop.CheckFocusedField();
        if (fileIfNoTextBox && Board is not null && (window is null || check == FieldCheck.NotText))
        {
            // Nowhere to type: keep the thought on the idea board rather than lose it.
            if (VoiceAssistant.Board.BoardGrammar.ForDictation(formatted, BoardNow()) is not { } note)
            {
                Report(StatusKind.Info, "I didn't catch anything to save.", heard);
                return;
            }

            ExecuteBoard(note, heard ?? formatted, noTextBox: true);
            LastTranscript = formatted;
            return;
        }

        if (window is null)
        {
            Report(StatusKind.Warning, "There's no window to type into.", heard);
            return;
        }

        if (!CheckField(heard ?? string.Empty, formatted, allowUnknown: true, known: check))
        {
            return;
        }

        var profile = _apps.FindProfileForProcess(window.ProcessName, window.Title);
        var leadingSpace = _desktop.FocusedTextEndsWithNonWhitespace() == true && formatted[0] != '\n';
        TypeWithNewLines(leadingSpace ? " " + formatted : formatted, profile?.NewLineKeys ?? "Shift+Enter");

        // Push-to-talk text isn't part of the voice “scratch that” history.
        _segments.Clear();
        _typedTail = null;
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
    private bool CheckField(string heard, string? pendingText, bool allowUnknown, FieldCheck? known = null)
    {
        var check = known ?? _desktop.CheckFocusedField();
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

    private void Report(StatusKind kind, string message, string? heard = null, string? place = null) =>
        Status?.Invoke(new StatusUpdate(Mode, kind, message, heard, place));
}
