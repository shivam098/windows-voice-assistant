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
}

/// <summary>
/// Owns the command/dictation state machine. Not thread-safe: callers must invoke it from one serial queue.
/// </summary>
public sealed class InteractionController
{
    private readonly IDesktop _desktop;
    private readonly AppCatalog _apps;
    private readonly CommandParser _parser;
    private readonly ControllerOptions _options;
    private readonly Stack<int> _segments = new();
    private WindowRef? _target;
    private AppEntry? _targetProfile;
    private bool _boxHadText;
    private bool _needsLeadingSpace;

    public InteractionController(IDesktop desktop, AppCatalog apps, CommandParser parser, ControllerOptions? options = null)
    {
        _desktop = desktop;
        _apps = apps;
        _parser = parser;
        _options = options ?? new ControllerOptions();
    }

    public event Action<StatusUpdate>? Status;

    /// <summary>Raised when a voice command asks to stop listening; the host must turn the microphone off.</summary>
    public event Action? PauseRequested;

    public InteractionMode Mode { get; private set; } = InteractionMode.Paused;

    /// <summary>Dictated text that could not be typed because focus moved. Kept only in memory.</summary>
    public string? UntypedText { get; private set; }

    public WindowRef? Target => _target;

    public void SetListening(bool listening)
    {
        if (listening)
        {
            if (Mode == InteractionMode.Paused)
            {
                Mode = InteractionMode.Command;
                Report(StatusKind.Info, "Listening. Say “open GitHub Copilot” or “start typing”.");
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

    /// <param name="followsForcedCut">
    /// The audio continues a sentence that was cut at the length cap, so it is typed (or ignored) but never run as a command.
    /// </param>
    public async Task HandleTranscriptAsync(string transcript, float confidence, bool followsForcedCut = false, CancellationToken cancellationToken = default)
    {
        if (Mode == InteractionMode.Paused)
        {
            return;
        }

        var heard = TranscriptText.Clean(transcript);
        if (followsForcedCut)
        {
            if (Mode == InteractionMode.Dictation && heard.Length > 0)
            {
                Type(heard, heard);
            }

            return;
        }

        var parsed = _parser.Parse(transcript, Mode);
        if (parsed.IsEmpty)
        {
            return;
        }

        if (parsed.Commands.Count > 0 && confidence < _options.MinCommandConfidence)
        {
            Report(StatusKind.Warning, "Not sure I heard that right — ignored.", heard);
            return;
        }

        if (parsed.Unrecognized is not null)
        {
            Report(StatusKind.Warning, "Not a command I know.", heard);
            return;
        }

        if (parsed.DictationText is not null)
        {
            Type(parsed.DictationText, heard);
            return;
        }

        foreach (var command in parsed.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ExecuteAsync(command, heard, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<bool> ExecuteAsync(VoiceCommand command, string heard, CancellationToken ct)
    {
        switch (command.Kind)
        {
            case CommandKind.OpenApp:
                return await OpenAsync(command.Argument ?? string.Empty, heard, ct).ConfigureAwait(false);

            case CommandKind.StartTyping:
                return await StartTypingAsync(heard, ct).ConfigureAwait(false);

            case CommandKind.Send:
                return Send(heard);

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
                Report(StatusKind.Success, "Cleared the message box.", heard);
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
                    _desktop.PressKeys(_targetProfile?.NewLineKeys ?? "Shift+Enter");
                }

                _segments.Push(lines);
                _needsLeadingSpace = false;
                Report(StatusKind.Success, lines == 1 ? "New line." : "New paragraph.", heard);
                return true;

            case CommandKind.StopTyping:
                if (Mode == InteractionMode.Dictation)
                {
                    Mode = InteractionMode.Command;
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

    private async Task<bool> OpenAsync(string spokenName, string heard, CancellationToken ct)
    {
        switch (_apps.Resolve(spokenName))
        {
            case AppResolution.Found found:
                Report(StatusKind.Info, $"Opening {found.App.Name}…", heard);
                var result = await _desktop.OpenOrActivateAsync(found.App, ct).ConfigureAwait(false);
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

        _target = window;
        _targetProfile = profile;
        _segments.Clear();
        _boxHadText = prepared.HasTrailingText == true;
        _needsLeadingSpace = _boxHadText;
        Mode = InteractionMode.Dictation;
        Report(StatusKind.Success, $"Typing into {profile?.Name ?? DisplayName(window)}. Pause, then say “send”.", heard);
        return true;
    }

    private bool Send(string heard)
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
        if (Mode == InteractionMode.Dictation && _options.ReturnToCommandModeAfterSend)
        {
            Mode = InteractionMode.Command;
        }

        Report(StatusKind.Success, "Sent.", heard);
        return true;
    }

    private void Type(string text, string heard)
    {
        if (!EnsureTargetFocused(heard, text))
        {
            return;
        }

        switch (_desktop.CheckFocusedField())
        {
            case FieldCheck.Password:
                Report(StatusKind.Warning, "That's a password field — I won't type there.", heard);
                return;
            case FieldCheck.Unknown:
                UntypedText = string.IsNullOrEmpty(UntypedText) ? text : UntypedText + " " + text;
                Report(StatusKind.Warning, "I couldn't check the focused field, so nothing was typed. The text is in the tray menu.", heard);
                return;
        }

        var toType = _needsLeadingSpace ? " " + text : text;
        _desktop.TypeText(toType);
        _segments.Push(new StringInfo(toType).LengthInTextElements);
        _needsLeadingSpace = !char.IsWhiteSpace(toType[^1]);
        Report(StatusKind.Success, "Typed.", heard);
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
            Mode = InteractionMode.Command;
        }

        Report(
            StatusKind.Warning,
            pendingText is null
                ? "Focus moved to another window, so I didn't do that. Say “start typing” to continue."
                : "Focus moved to another window, so nothing was typed. Say “start typing” to continue.",
            heard);
        return false;
    }

    private static string DisplayName(WindowRef window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;

    private void Report(StatusKind kind, string message, string? heard = null) =>
        Status?.Invoke(new StatusUpdate(Mode, kind, message, heard));
}
