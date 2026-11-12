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
    /// <summary>Open the app; for apps with a new-window shortcut (Notepad), opens a fresh window.</summary>
    OpenApp,

    /// <summary>Bring an existing window of the app to the front (launch it if it isn't running).</summary>
    SwitchToApp,
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

    /// <summary>Select a browser tab by position or name (<see cref="VoiceCommand.Target"/>).</summary>
    SelectTab,

    /// <summary>Click an element in the front window by name, with optional region/ordinal hints.</summary>
    Click,

    /// <summary>Focus a text box by name (“message box”) and start typing there.</summary>
    FocusField,

    /// <summary>Show numbers on everything clickable in the front window.</summary>
    ShowNumbers,

    HideNumbers,

    /// <summary>Pick numbered choice <see cref="VoiceCommand.Argument"/>.</summary>
    Choose,

    /// <summary>“yes”: perform the action waiting for confirmation.</summary>
    Confirm,

    /// <summary>“no” / “cancel”: drop pending choices and confirmations.</summary>
    Cancel,

    /// <summary>“delete the last 3 words”, “delete the word very” (<see cref="VoiceCommand.Edit"/>).</summary>
    DeleteText,

    /// <summary>“replace Monday with Tuesday” (<see cref="VoiceCommand.Edit"/>).</summary>
    ReplaceText,

    /// <summary>Idea board: add a note or reminder, show a list, tick an item off (<see cref="VoiceCommand.Board"/>).</summary>
    Board,
}

public sealed record VoiceCommand(CommandKind Kind, string? Argument = null)
{
    /// <summary>For <see cref="CommandKind.SelectTab"/>, <see cref="CommandKind.Click"/> and <see cref="CommandKind.FocusField"/>.</summary>
    public ElementTarget? Target { get; init; }

    /// <summary>For <see cref="CommandKind.DeleteText"/> and <see cref="CommandKind.ReplaceText"/>.</summary>
    public TextEdit? Edit { get; init; }

    /// <summary>For <see cref="CommandKind.Board"/>.</summary>
    public VoiceAssistant.Board.BoardIntent? Board { get; init; }
}

public sealed record ParsedUtterance
{
    public static readonly ParsedUtterance Empty = new();

    public IReadOnlyList<VoiceCommand> Commands { get; init; } = [];

    public string? DictationText { get; init; }

    /// <summary>Simplified text that looked like a command attempt but matched nothing.</summary>
    public string? Unrecognized { get; init; }

    /// <summary>The simplified phrase that matched (used to learn rephrasings).</summary>
    public string? CommandText { get; init; }

    /// <summary>The command came from a phrase the user taught earlier.</summary>
    public bool FromLearnedPhrase { get; init; }

    /// <summary>The utterance started with the wake word.</summary>
    public bool WokeUp { get; init; }

    /// <summary>A full command said with the wake word while typing: typing stops before it runs.</summary>
    public bool LeavesTyping { get; init; }

    public bool IsWakeOnly => WokeUp && Commands.Count == 0 && DictationText is null && Unrecognized is null;

    public bool IsEmpty => !WokeUp && Commands.Count == 0 && DictationText is null && Unrecognized is null;
}

public sealed partial class CommandParser
{
    public static readonly IReadOnlyList<string> DefaultWakeWords =
        ["hey jarvis", "hi jarvis", "hello jarvis", "ok jarvis", "okay jarvis", "a jarvis", "hey jervis", "hey javis", "jarvis"];

    // Conversational padding around a command: "can you please open notepad for me" → "open notepad".
    private static readonly string[] PolitePrefixes =
    [
        "can you please", "could you please", "would you please", "will you please", "can you", "could you", "would you",
        "will you", "would you mind", "please", "kindly", "i want you to", "i want to", "i would like to", "id like to",
        "i need you to", "i need to", "i wanna", "lets", "let us", "go ahead and", "now", "just", "okay", "ok", "so", "and",
        "then", "hey", "um", "uh", "alright", "all right",
    ];

    private static readonly string[] PoliteSuffixes =
        ["please", "for me", "right now", "now", "thanks", "thank you", "if you can", "again", "quickly"];

    // While dictating without the wake word, only these exact phrases are commands: everything else is the
    // user's text. Keep this list short and unusual — "Post it." or "That's all." must be typed, not executed.
    private static readonly HashSet<string> StrictSend = ["send", "send it", "submit", "send message", "send the message", "search", "search it"];
    private static readonly HashSet<string> StrictScratch = ["scratch that", "undo that", "delete that"];
    private static readonly HashSet<string> StrictClear = ["clear box", "clear the box", "clear text", "clear all", "clear message", "clear the message"];
    private static readonly HashSet<string> StrictNewLine = ["new line", "next line"];
    private static readonly HashSet<string> StrictNewParagraph = ["new paragraph"];
    private static readonly HashSet<string> StrictStopTyping = ["stop typing", "stop dictation", "command mode", "stop writing"];
    private static readonly HashSet<string> StrictPause = ["pause listening", "stop listening", "go to sleep", "pause"];

    // Broader phrasings, accepted only when waiting for a command or after the wake word.
    private static readonly HashSet<string> SendPhrases = [.. StrictSend, "send that", "submit it", "post it"];

    private static readonly HashSet<string> StartTypingPhrases =
    [
        "start typing", "start dictation", "resume typing", "begin typing", "type", "start writing", "start dictating",
        "let me type", "type here", "type in it", "type something", "start typing here", "take dictation", "dictate",
        "begin dictation", "write", "start typing in it", "i want to type", "lets type", "continue typing",
    ];

    private static readonly HashSet<string> PausePhrases = [.. StrictPause, "be quiet"];
    private static readonly HashSet<string> StopTypingPhrases = [.. StrictStopTyping, "stop dictating", "done typing", "im done", "that is all", "thats all", "end dictation"];
    private static readonly HashSet<string> ScratchPhrases = [.. StrictScratch, "remove that", "erase that"];
    private static readonly HashSet<string> ClearPhrases = [.. StrictClear, "clear everything", "delete everything"];
    private static readonly HashSet<string> NewLinePhrases = [.. StrictNewLine, "line break"];
    private static readonly HashSet<string> NewParagraphPhrases = [.. StrictNewParagraph, "next paragraph"];

    private static readonly string[] OpenVerbs = ["open up", "open", "launch", "start", "run", "fire up", "pull up", "load", "get me", "give me"];
    private static readonly string[] SwitchVerbs = ["switch to", "switch over to", "go to", "go back to", "bring up", "show me", "show", "focus", "jump to", "take me to"];
    private static readonly string[] ChainedStartTyping =
        [" and start typing", " and start dictation", " and type", " and let me type", " and start writing", " to type", " so i can type"];

    private static readonly string[] AppNameNoise = ["app", "application", "window", "program", "browser"];

    private static readonly string[] SearchTargets = ["search box", "search bar", "search field", "search"];
    private static readonly string[] AddressTargets = ["address bar", "url bar", "address box", "location bar"];
    private static readonly string[] FocusVerbs = ["go to", "click", "click on", "focus", "focus on", "select", "move to", "put the cursor in", "type in", "jump to", "open"];

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
        ["paste it"] = "Ctrl+V", ["cut"] = "Ctrl+X", ["cut that"] = "Ctrl+X", ["undo"] = "Ctrl+Z", ["redo"] = "Ctrl+Y",
        ["save"] = "Ctrl+S", ["save file"] = "Ctrl+S", ["save it"] = "Ctrl+S", ["save this"] = "Ctrl+S", ["save the file"] = "Ctrl+S",
        ["new tab"] = "Ctrl+T", ["open a new tab"] = "Ctrl+T", ["open new tab"] = "Ctrl+T", ["open a tab"] = "Ctrl+T",
        ["close tab"] = "Ctrl+W", ["close the tab"] = "Ctrl+W", ["close this tab"] = "Ctrl+W",
        ["next tab"] = "Ctrl+Tab", ["previous tab"] = "Ctrl+Shift+Tab", ["go back"] = "Alt+Left", ["go forward"] = "Alt+Right",
        ["back"] = "Alt+Left", ["forward"] = "Alt+Right",

        // Back/forward in browsers and File Explorer. "Go to the previous page" must not be read as an app called
        // "previous page"; pagination links ("Next" under search results) are clicked with “click next”.
        ["previous page"] = "Alt+Left", ["go to previous page"] = "Alt+Left", ["go to the previous page"] = "Alt+Left",
        ["go back to the previous page"] = "Alt+Left", ["go back a page"] = "Alt+Left", ["go back one page"] = "Alt+Left",
        ["back page"] = "Alt+Left", ["navigate back"] = "Alt+Left", ["take me back"] = "Alt+Left", ["move back"] = "Alt+Left",
        ["next page"] = "Alt+Right", ["go to next page"] = "Alt+Right", ["go to the next page"] = "Alt+Right",
        ["go forward a page"] = "Alt+Right", ["go forward one page"] = "Alt+Right", ["navigate forward"] = "Alt+Right",
        ["move forward"] = "Alt+Right",

        // File Explorer: the folder that contains this one.
        ["go up"] = "Alt+Up", ["go up a level"] = "Alt+Up", ["go up one level"] = "Alt+Up", ["up one level"] = "Alt+Up",
        ["parent folder"] = "Alt+Up", ["go to parent folder"] = "Alt+Up", ["go to the parent folder"] = "Alt+Up",
        ["open parent folder"] = "Alt+Up", ["open the parent folder"] = "Alt+Up",
        ["refresh"] = "F5", ["reload"] = "F5", ["reload page"] = "F5", ["refresh page"] = "F5", ["refresh the page"] = "F5", ["reload the page"] = "F5",
        ["scroll down"] = "PageDown", ["scroll up"] = "PageUp", ["page down"] = "PageDown", ["page up"] = "PageUp",
    };

    private readonly string _prefix;
    private readonly bool _requirePrefixWhileTyping;
    private readonly IReadOnlyList<string> _wakeWords;
    private readonly List<string> _wakeNames;
    private readonly bool _wakeUncheckable;
    private readonly PhraseMemory? _memory;

    public CommandParser(string commandPrefix = "computer", bool requirePrefixWhileTyping = false, IEnumerable<string>? wakeWords = null, PhraseMemory? memory = null)
    {
        _prefix = TranscriptText.Normalize(commandPrefix);
        _requirePrefixWhileTyping = requirePrefixWhileTyping;
        _memory = memory;
        _wakeWords = (wakeWords ?? DefaultWakeWords)
            .Select(TranscriptText.Normalize)
            .Where(w => w.Length > 0)
            .Distinct()
            .OrderByDescending(w => w.Length)
            .ToList();

        // The name in each wake phrase (its last word: "hey jarvis" → "jarvis"), for the standby pre-check.
        _wakeNames = _wakeWords.Select(w => w.Split(' ')[^1]).Where(w => w.Length >= 3).Distinct().ToList();

        // A wake phrase without a usable name ("hey pc") can't be pre-checked: every phrase then gets a full transcription.
        _wakeUncheckable = _wakeWords.Any(w => w.Split(' ')[^1].Length < 3);
    }

    /// <summary>The clock for reminder times (“tomorrow at 5”). Tests set it.</summary>
    public Func<DateTimeOffset> BoardClock { get; init; } = () => DateTimeOffset.Now;

    private VoiceCommand? MatchBoard(string spoken) =>
        VoiceAssistant.Board.BoardGrammar.Parse(spoken, BoardClock()) is { } intent ? new VoiceCommand(CommandKind.Board) { Board = intent } : null;

    private static int WordCount(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Drops the transcript's first words that normalising turned into <paramref name="normalizedWords"/> words (the wake
    /// word or prefix), keeping the rest exactly as said.
    /// </summary>
    private static string DropLeadingWords(string cleaned, int normalizedWords)
    {
        if (normalizedWords <= 0)
        {
            return cleaned;
        }

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var used = 0;
        var taken = 0;
        while (taken < tokens.Length && used < normalizedWords)
        {
            used += Math.Max(1, WordCount(TranscriptText.Normalize(tokens[taken])));
            taken++;
        }

        return string.Join(' ', tokens.Skip(taken)).TrimStart(',', '.', ' ', ':', ';', '!');
    }

    /// <summary>
    /// Removes conversational padding (“can you please …”, “… for me”). Exposed for learning rephrasings.
    /// </summary>
    public static string Simplify(string normalized)
    {
        var text = normalized;
        bool changed;
        do
        {
            changed = false;
            foreach (var prefix in PolitePrefixes)
            {
                if (text.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    text = text[(prefix.Length + 1)..].TrimStart();
                    changed = true;
                }
            }

            foreach (var suffix in PoliteSuffixes)
            {
                if (text.EndsWith(" " + suffix, StringComparison.Ordinal))
                {
                    text = text[..^(suffix.Length + 1)].TrimEnd();
                    changed = true;
                }
            }
        }
        while (changed && text.Length > 0);

        return text;
    }

    /// <summary>
    /// Parses one complete, pause-delimited utterance. Commands must match the whole utterance (after
    /// polite padding is removed), so "please send me the logs" is dictation while "Send." is a command.
    /// </summary>
    public ParsedUtterance Parse(string? transcript, InteractionMode mode)
    {
        var cleaned = TranscriptText.Clean(transcript);
        var normalized = TranscriptText.Normalize(cleaned);
        if (normalized.Length == 0 || mode == InteractionMode.Paused)
        {
            return ParsedUtterance.Empty;
        }

        var normalizedBefore = normalized;
        var woke = TryStripWakeWord(ref normalized);
        var prefixed = woke || TryStripPrefix(ref normalized);

        // The idea board keeps the note's own capitals and punctuation, so it reads the transcript as said.
        var spoken = DropLeadingWords(cleaned, WordCount(normalizedBefore) - WordCount(normalized));

        switch (mode)
        {
            case InteractionMode.Standby when !woke:
                return ParsedUtterance.Empty;

            case InteractionMode.Dictation:
            {
                if (normalized.Length == 0)
                {
                    return new ParsedUtterance { WokeUp = woke };
                }

                if (prefixed && MatchBoard(spoken) is { } boardWhileTyping)
                {
                    // "Hey Jarvis, add to my ideas …" while typing: filed on the board; typing carries on.
                    return new ParsedUtterance { Commands = [boardWhileTyping], WokeUp = woke, CommandText = Simplify(normalized) };
                }

                if (_requirePrefixWhileTyping && !prefixed)
                {
                    return new ParsedUtterance { DictationText = cleaned };
                }

                // Unprefixed speech while typing stays strict: "please send it to Sam" is text, not a command.
                var text = prefixed ? Simplify(normalized) : normalized;
                var command = MatchDictationCommand(text, lenient: prefixed);
                if (command is null && prefixed && WithUntrimmedEdit(null, normalized) is [{ } untrimmed])
                {
                    // "delete the word now": the trimmed text lost the phrase.
                    command = untrimmed;
                }

                var learned = false;
                if (command is null && prefixed && _memory?.TryGet(text, out var taught) == true)
                {
                    command = MatchDictationCommand(taught, lenient: true);
                    learned = command is not null;
                    text = learned ? taught : text;
                }

                if (command is not null)
                {
                    return new ParsedUtterance { Commands = WithSpokenReplacement(prefixed ? WithUntrimmedEdit([command], normalized)! : [command], cleaned), WokeUp = woke, CommandText = text, FromLearnedPhrase = learned };
                }

                if (prefixed && MatchCommandMode(text) is { } full)
                {
                    // "Hey Jarvis, go to the first tab" while typing: any command works after the wake word, and
                    // typing stops first so nothing lands in the new place.
                    return new ParsedUtterance { Commands = WithSpokenReplacement(WithUntrimmedEdit(full, normalized)!, cleaned), WokeUp = woke, CommandText = text, LeavesTyping = true };
                }

                return prefixed
                    ? new ParsedUtterance { Unrecognized = text, WokeUp = woke }
                    : new ParsedUtterance { DictationText = cleaned };
            }

            default:
            {
                var text = Simplify(normalized);
                if (text.Length == 0)
                {
                    return new ParsedUtterance { WokeUp = woke };
                }

                if (MatchBoard(spoken) is { } board)
                {
                    return new ParsedUtterance { Commands = [board], WokeUp = woke, CommandText = text };
                }

                var commands = MatchCommandMode(text) ?? WithUntrimmedEdit(null, normalized);
                var learned = false;
                if (commands is null && _memory?.TryGet(text, out var taught) == true)
                {
                    commands = MatchCommandMode(taught);
                    learned = commands is not null;
                    // Learn rephrasings against the real command, not another learned phrase.
                    text = learned ? taught : text;
                }

                return commands is null
                    ? new ParsedUtterance { Unrecognized = text, WokeUp = woke }
                    : new ParsedUtterance { Commands = WithSpokenReplacement(learned ? commands : WithUntrimmedEdit(commands, normalized)!, cleaned), WokeUp = woke, CommandText = text, FromLearnedPhrase = learned };
            }
        }
    }

    /// <summary>
    /// Cheap pre-check for standby: could this rough transcript contain the wake word? Lenient on purpose (a false yes
    /// only costs a full transcription; a false no ignores the user): any word, or two adjacent words joined, within a
    /// small edit distance of a wake word's name (“jarvis”, “jervis”, “jar vis”, “jarvi”).
    /// </summary>
    public bool MayContainWakeWord(string transcript)
    {
        if (_wakeUncheckable)
        {
            return true;
        }

        var words = TranscriptText.Normalize(transcript).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        foreach (var name in _wakeNames)
        {
            var allowed = name.Length >= 5 ? 2 : name.Length >= 3 ? 1 : 0;
            for (var i = 0; i < words.Length; i++)
            {
                if (EditDistance(words[i], name) <= allowed
                    || (i + 1 < words.Length && EditDistance(words[i] + words[i + 1], name) <= allowed))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // First words of commands, for a quick transcript that was cut short ("open the downloads folder in…").
    private static readonly HashSet<string> CommandStarts = new(StringComparer.Ordinal)
    {
        "open", "launch", "start", "switch", "go", "click", "select", "press", "search", "show", "close", "new", "type",
        "scroll", "pause", "stop", "remind", "add", "note", "mark", "delete", "replace", "send", "expand", "refresh",
        "copy", "paste", "undo", "save", "hide", "focus", "bring", "pull", "fire", "please", "can", "could",
    };

    /// <summary>Whether a transcript cut short could be the start of a command (its first word starts one).</summary>
    public static bool MayStartCommand(string transcript) =>
        TranscriptText.Normalize(transcript).Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && CommandStarts.Contains(first);

    private static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return 3;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
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

    private static VoiceCommand? MatchDictationCommand(string n, bool lenient)
    {
        if ((lenient ? SendPhrases : StrictSend).Contains(n)) return new VoiceCommand(CommandKind.Send);
        if ((lenient ? ScratchPhrases : StrictScratch).Contains(n)) return new VoiceCommand(CommandKind.ScratchThat);
        if ((lenient ? ClearPhrases : StrictClear).Contains(n)) return new VoiceCommand(CommandKind.ClearBox);
        if ((lenient ? NewLinePhrases : StrictNewLine).Contains(n)) return new VoiceCommand(CommandKind.NewLine);
        if ((lenient ? NewParagraphPhrases : StrictNewParagraph).Contains(n)) return new VoiceCommand(CommandKind.NewParagraph);
        if ((lenient ? StopTypingPhrases : StrictStopTyping).Contains(n)) return new VoiceCommand(CommandKind.StopTyping);
        if ((lenient ? PausePhrases : StrictPause).Contains(n)) return new VoiceCommand(CommandKind.PauseListening);
        if (MatchEdit(n, lenient) is { } edit) return edit;
        return MatchPress(n);
    }

    private static IReadOnlyList<VoiceCommand>? MatchCommandMode(string n)
    {
        if (StartTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StartTyping)];
        if (SendPhrases.Contains(n)) return [new VoiceCommand(CommandKind.Send)];
        if (PausePhrases.Contains(n)) return [new VoiceCommand(CommandKind.PauseListening)];
        if (StopTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StopTyping)];
        if (MatchFocusTarget(n, AddressTargets)) return [new VoiceCommand(CommandKind.FocusAddressBar)];
        if (MatchFocusTarget(n, SearchTargets)) return [new VoiceCommand(CommandKind.FocusSearch)];
        if (Shortcuts.TryGetValue(n, out var shortcut)) return [new VoiceCommand(CommandKind.PressKeys, shortcut)];
        if (MatchPress(n) is { } press) return [press];
        if (MatchNavigation(n) is { } navigation) return navigation;
        if (MatchEdit(n, lenient: true) is { } edit) return [edit];

        foreach (var prefix in new[] { "search for ", "search the web for ", "search google for ", "google ", "look up ", "search " })
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length)
            {
                var query = n[prefix.Length..].Trim();
                foreach (var tail in new[] { " on google", " on the web", " online" })
                {
                    if (query.EndsWith(tail, StringComparison.Ordinal))
                    {
                        query = query[..^tail.Length];
                    }
                }

                return [new VoiceCommand(CommandKind.SearchFor, query)];
            }
        }

        return MatchApp(n, OpenVerbs, CommandKind.OpenApp) ?? MatchApp(n, SwitchVerbs, CommandKind.SwitchToApp);
    }

    private static IReadOnlyList<VoiceCommand>? MatchApp(string n, string[] verbs, CommandKind kind)
    {
        foreach (var verb in verbs)
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

            app = CleanAppName(app);
            if (app.Length == 0)
            {
                return null;
            }

            return startTyping
                ? [new VoiceCommand(kind, app), new VoiceCommand(CommandKind.StartTyping)]
                : [new VoiceCommand(kind, app)];
        }

        return null;
    }

    /// <summary>"up a new notepad window" → "notepad".</summary>
    private static string CleanAppName(string app)
    {
        foreach (var article in new[] { "up ", "the ", "my ", "a ", "an ", "new ", "a new ", "another " })
        {
            if (app.StartsWith(article, StringComparison.Ordinal))
            {
                app = app[article.Length..];
            }
        }

        foreach (var noise in AppNameNoise)
        {
            if (app.EndsWith(" " + noise, StringComparison.Ordinal) && app.Length > noise.Length + 1)
            {
                app = app[..^(noise.Length + 1)];
            }
        }

        return Simplify(app.Trim());
    }

    /// <summary>"go to the search box", "click on the search bar", "search box", "focus search" …</summary>
    private static bool MatchFocusTarget(string n, string[] targets)
    {
        foreach (var target in targets)
        {
            foreach (var article in new[] { string.Empty, "the " })
            {
                var phrase = article + target;
                if (n == phrase && target.Contains(' '))
                {
                    return true;
                }

                foreach (var verb in FocusVerbs)
                {
                    if (n == $"{verb} {phrase}")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static VoiceCommand? MatchPress(string n)
    {
        foreach (var prefix in new[] { "press the ", "press ", "hit the ", "hit ", "tap " })
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal))
            {
                var key = n[prefix.Length..].Trim();
                if (key.EndsWith(" key", StringComparison.Ordinal))
                {
                    key = key[..^4];
                }

                if (key.EndsWith(" button", StringComparison.Ordinal))
                {
                    key = key[..^7];
                }

                return PressableKeys.TryGetValue(key, out var chord) ? new VoiceCommand(CommandKind.PressKeys, chord) : null;
            }
        }

        return null;
    }
}
