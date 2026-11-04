namespace VoiceAssistant.Core;

public enum InteractionMode
{
    Paused,

    /// <summary>Listening only for the wake word (“Hey Jarvis”). Other speech is ignored silently.</summary>
    Standby,

    /// <summary>Awake: commands work without the wake word until a short timeout.</summary>
    Command,

    Dictation,
}

public enum CommandKind
{
    OpenApp,
    StartTyping,
    Send,
    ScratchThat,
    ClearBox,
    NewLine,
    NewParagraph,
    StopTyping,
    PauseListening,

    /// <summary>Focus the window's search box (browsers: the address bar) and start typing there.</summary>
    FocusSearch,
    FocusAddressBar,

    /// <summary>Focus search, type the argument, press Enter.</summary>
    SearchFor,

    /// <summary>Press the key chord in the argument (e.g. "Ctrl+T").</summary>
    PressKeys,
}

public sealed record VoiceCommand(CommandKind Kind, string? Argument = null);

public sealed record ParsedUtterance
{
    public static readonly ParsedUtterance Empty = new();

    public IReadOnlyList<VoiceCommand> Commands { get; init; } = [];

    public string? DictationText { get; init; }

    /// <summary>Normalized text that looked like a command attempt but matched nothing.</summary>
    public string? Unrecognized { get; init; }

    /// <summary>The utterance started with the wake word.</summary>
    public bool WokeUp { get; init; }

    public bool IsWakeOnly => WokeUp && Commands.Count == 0 && DictationText is null && Unrecognized is null;

    public bool IsEmpty => !WokeUp && Commands.Count == 0 && DictationText is null && Unrecognized is null;
}

public sealed class CommandParser
{
    public static readonly IReadOnlyList<string> DefaultWakeWords =
        ["hey jarvis", "hi jarvis", "hello jarvis", "ok jarvis", "okay jarvis", "a jarvis", "hey jervis", "hey javis", "jarvis"];

    private static readonly HashSet<string> SendPhrases = ["send", "send it", "submit", "send message", "send the message", "search", "search it"];
    private static readonly HashSet<string> StartTypingPhrases = ["start typing", "start dictation", "resume typing", "begin typing", "type", "start writing"];
    private static readonly HashSet<string> PausePhrases = ["pause listening", "stop listening", "go to sleep", "pause"];
    private static readonly HashSet<string> StopTypingPhrases = ["stop typing", "stop dictation", "command mode", "stop writing"];
    private static readonly HashSet<string> ScratchPhrases = ["scratch that", "undo that", "delete that"];
    private static readonly HashSet<string> ClearPhrases = ["clear box", "clear the box", "clear text", "clear all", "clear message", "clear the message"];
    private static readonly HashSet<string> NewLinePhrases = ["new line", "next line"];
    private static readonly HashSet<string> NewParagraphPhrases = ["new paragraph"];
    private static readonly string[] OpenVerbs = ["open", "launch", "switch to", "bring up"];
    private static readonly string[] ChainedStartTyping = [" and start typing", " and start dictation", " and type"];

    private static readonly HashSet<string> SearchBoxPhrases =
    [
        "go to the search box", "go to search box", "go to the search bar", "go to search bar", "go to search",
        "search box", "search bar", "focus search", "focus the search box", "click the search box", "click search",
        "click on the search box", "go to the search field", "type in the search box",
    ];

    private static readonly HashSet<string> AddressBarPhrases =
        ["go to the address bar", "go to address bar", "address bar", "focus the address bar", "click the address bar", "go to the url bar", "url bar"];

    private static readonly Dictionary<string, string> PressableKeys = new(StringComparer.Ordinal)
    {
        ["enter"] = "Enter", ["return"] = "Enter", ["tab"] = "Tab", ["escape"] = "Esc", ["backspace"] = "Backspace",
        ["delete"] = "Delete", ["space"] = "Space", ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["up arrow"] = "Up", ["down arrow"] = "Down", ["left arrow"] = "Left", ["right arrow"] = "Right",
        ["page up"] = "PageUp", ["page down"] = "PageDown", ["home"] = "Home", ["end"] = "End",
        ["shift tab"] = "Shift+Tab",
    };

    /// <summary>Editing and navigation shortcuts available while waiting for a command.</summary>
    private static readonly Dictionary<string, string> Shortcuts = new(StringComparer.Ordinal)
    {
        ["select all"] = "Ctrl+A", ["copy"] = "Ctrl+C", ["copy that"] = "Ctrl+C", ["paste"] = "Ctrl+V", ["paste that"] = "Ctrl+V",
        ["cut"] = "Ctrl+X", ["cut that"] = "Ctrl+X", ["undo"] = "Ctrl+Z", ["redo"] = "Ctrl+Y", ["save"] = "Ctrl+S", ["save file"] = "Ctrl+S",
        ["new tab"] = "Ctrl+T", ["open a new tab"] = "Ctrl+T", ["open new tab"] = "Ctrl+T", ["close tab"] = "Ctrl+W", ["close the tab"] = "Ctrl+W",
        ["next tab"] = "Ctrl+Tab", ["previous tab"] = "Ctrl+Shift+Tab", ["go back"] = "Alt+Left", ["go forward"] = "Alt+Right",
        ["refresh"] = "F5", ["reload"] = "F5", ["reload page"] = "F5", ["refresh page"] = "F5",
        ["scroll down"] = "PageDown", ["scroll up"] = "PageUp",
    };

    private readonly string _prefix;
    private readonly bool _requirePrefixWhileTyping;
    private readonly IReadOnlyList<string> _wakeWords;

    public CommandParser(string commandPrefix = "computer", bool requirePrefixWhileTyping = false, IEnumerable<string>? wakeWords = null)
    {
        _prefix = TranscriptText.Normalize(commandPrefix);
        _requirePrefixWhileTyping = requirePrefixWhileTyping;
        _wakeWords = (wakeWords ?? DefaultWakeWords)
            .Select(TranscriptText.Normalize)
            .Where(w => w.Length > 0)
            .Distinct()
            .OrderByDescending(w => w.Length)
            .ToList();
    }

    /// <summary>
    /// Parses one complete, pause-delimited utterance. Commands must match the whole utterance,
    /// so "please send me the logs" is dictation while a standalone "Send." is a command.
    /// </summary>
    public ParsedUtterance Parse(string? transcript, InteractionMode mode)
    {
        var cleaned = TranscriptText.Clean(transcript);
        var normalized = TranscriptText.Normalize(cleaned);
        if (normalized.Length == 0 || mode == InteractionMode.Paused)
        {
            return ParsedUtterance.Empty;
        }

        var woke = TryStripWakeWord(ref normalized);
        var prefixed = woke || TryStripPrefix(ref normalized);

        switch (mode)
        {
            case InteractionMode.Standby when !woke:
                return ParsedUtterance.Empty;

            case InteractionMode.Dictation:
                if (normalized.Length == 0)
                {
                    return new ParsedUtterance { WokeUp = woke };
                }

                if (_requirePrefixWhileTyping && !prefixed)
                {
                    return new ParsedUtterance { DictationText = cleaned };
                }

                var command = MatchDictationCommand(normalized);
                if (command is not null)
                {
                    return new ParsedUtterance { Commands = [command], WokeUp = woke };
                }

                return prefixed
                    ? new ParsedUtterance { Unrecognized = normalized, WokeUp = woke }
                    : new ParsedUtterance { DictationText = cleaned };

            default:
                if (normalized.Length == 0)
                {
                    return new ParsedUtterance { WokeUp = woke };
                }

                var commands = MatchCommandMode(normalized);
                return commands is null
                    ? new ParsedUtterance { Unrecognized = normalized, WokeUp = woke }
                    : new ParsedUtterance { Commands = commands, WokeUp = woke };
        }
    }

    private bool TryStripWakeWord(ref string normalized)
    {
        foreach (var wake in _wakeWords)
        {
            if (normalized == wake)
            {
                normalized = string.Empty;
                return true;
            }

            if (normalized.StartsWith(wake + " ", StringComparison.Ordinal))
            {
                normalized = normalized[(wake.Length + 1)..].Trim();
                return true;
            }
        }

        return false;
    }

    private bool TryStripPrefix(ref string normalized)
    {
        if (_prefix.Length == 0)
        {
            return false;
        }

        foreach (var candidate in new[] { _prefix + " ", "hey " + _prefix + " " })
        {
            if (normalized.StartsWith(candidate, StringComparison.Ordinal))
            {
                normalized = normalized[candidate.Length..].Trim();
                return true;
            }
        }

        return false;
    }

    private static VoiceCommand? MatchDictationCommand(string n)
    {
        if (SendPhrases.Contains(n)) return new VoiceCommand(CommandKind.Send);
        if (ScratchPhrases.Contains(n)) return new VoiceCommand(CommandKind.ScratchThat);
        if (ClearPhrases.Contains(n)) return new VoiceCommand(CommandKind.ClearBox);
        if (NewLinePhrases.Contains(n)) return new VoiceCommand(CommandKind.NewLine);
        if (NewParagraphPhrases.Contains(n)) return new VoiceCommand(CommandKind.NewParagraph);
        if (StopTypingPhrases.Contains(n)) return new VoiceCommand(CommandKind.StopTyping);
        if (PausePhrases.Contains(n)) return new VoiceCommand(CommandKind.PauseListening);
        return MatchPress(n);
    }

    private static IReadOnlyList<VoiceCommand>? MatchCommandMode(string n)
    {
        if (StartTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StartTyping)];
        if (SendPhrases.Contains(n)) return [new VoiceCommand(CommandKind.Send)];
        if (PausePhrases.Contains(n)) return [new VoiceCommand(CommandKind.PauseListening)];
        if (StopTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StopTyping)];
        if (SearchBoxPhrases.Contains(n)) return [new VoiceCommand(CommandKind.FocusSearch)];
        if (AddressBarPhrases.Contains(n)) return [new VoiceCommand(CommandKind.FocusAddressBar)];
        if (Shortcuts.TryGetValue(n, out var shortcut)) return [new VoiceCommand(CommandKind.PressKeys, shortcut)];
        if (MatchPress(n) is { } press) return [press];

        foreach (var prefix in new[] { "search for ", "search the web for ", "google ", "look up " })
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length)
            {
                return [new VoiceCommand(CommandKind.SearchFor, n[prefix.Length..].Trim())];
            }
        }

        foreach (var verb in OpenVerbs)
        {
            if (!n.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                continue;
            }

            var app = n[(verb.Length + 1)..].Trim();
            var startTyping = false;
            foreach (var suffix in ChainedStartTyping)
            {
                if (app.EndsWith(suffix, StringComparison.Ordinal))
                {
                    app = app[..^suffix.Length].Trim();
                    startTyping = true;
                    break;
                }
            }

            if (app.StartsWith("the ", StringComparison.Ordinal))
            {
                app = app[4..];
            }

            if (app.Length == 0)
            {
                return null;
            }

            return startTyping
                ? [new VoiceCommand(CommandKind.OpenApp, app), new VoiceCommand(CommandKind.StartTyping)]
                : [new VoiceCommand(CommandKind.OpenApp, app)];
        }

        return null;
    }

    private static VoiceCommand? MatchPress(string n)
    {
        foreach (var prefix in new[] { "press ", "hit ", "press the " })
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal))
            {
                var key = n[prefix.Length..].Trim();
                if (key.EndsWith(" key", StringComparison.Ordinal))
                {
                    key = key[..^4];
                }

                if (key.StartsWith("the ", StringComparison.Ordinal))
                {
                    key = key[4..];
                }

                return PressableKeys.TryGetValue(key, out var chord) ? new VoiceCommand(CommandKind.PressKeys, chord) : null;
            }
        }

        return null;
    }
}
