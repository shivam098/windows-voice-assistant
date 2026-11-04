namespace VoiceAssistant.Core;

public enum InteractionMode
{
    Paused,
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
}

public sealed record VoiceCommand(CommandKind Kind, string? Argument = null);

public sealed record ParsedUtterance
{
    public static readonly ParsedUtterance Empty = new();

    public IReadOnlyList<VoiceCommand> Commands { get; init; } = [];

    public string? DictationText { get; init; }

    /// <summary>Normalized text that looked like a command attempt but matched nothing.</summary>
    public string? Unrecognized { get; init; }

    public bool IsEmpty => Commands.Count == 0 && DictationText is null && Unrecognized is null;
}

public sealed class CommandParser
{
    private static readonly HashSet<string> SendPhrases = ["send", "send it", "submit", "send message", "send the message"];
    private static readonly HashSet<string> StartTypingPhrases = ["start typing", "start dictation", "resume typing", "begin typing", "type"];
    private static readonly HashSet<string> PausePhrases = ["pause listening", "stop listening", "go to sleep", "pause"];
    private static readonly HashSet<string> StopTypingPhrases = ["stop typing", "stop dictation", "command mode"];
    private static readonly HashSet<string> ScratchPhrases = ["scratch that", "undo that", "delete that"];
    private static readonly HashSet<string> ClearPhrases = ["clear box", "clear the box", "clear text", "clear all", "clear message", "clear the message"];
    private static readonly HashSet<string> NewLinePhrases = ["new line", "next line"];
    private static readonly HashSet<string> NewParagraphPhrases = ["new paragraph"];
    private static readonly string[] OpenVerbs = ["open", "launch", "switch to", "go to", "bring up"];
    private static readonly string[] ChainedStartTyping = [" and start typing", " and start dictation", " and type"];

    private readonly string _prefix;
    private readonly bool _requirePrefixWhileTyping;

    public CommandParser(string commandPrefix = "computer", bool requirePrefixWhileTyping = false)
    {
        _prefix = TranscriptText.Normalize(commandPrefix);
        _requirePrefixWhileTyping = requirePrefixWhileTyping;
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

        var prefixed = TryStripPrefix(ref normalized);
        if (mode == InteractionMode.Dictation)
        {
            if (_requirePrefixWhileTyping && !prefixed)
            {
                return new ParsedUtterance { DictationText = cleaned };
            }

            var command = MatchDictationCommand(normalized);
            if (command is not null)
            {
                return new ParsedUtterance { Commands = [command] };
            }

            return prefixed
                ? new ParsedUtterance { Unrecognized = normalized }
                : new ParsedUtterance { DictationText = cleaned };
        }

        var commands = MatchCommandMode(normalized);
        return commands is null
            ? new ParsedUtterance { Unrecognized = normalized }
            : new ParsedUtterance { Commands = commands };
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
        return null;
    }

    private static IReadOnlyList<VoiceCommand>? MatchCommandMode(string n)
    {
        if (StartTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StartTyping)];
        if (SendPhrases.Contains(n)) return [new VoiceCommand(CommandKind.Send)];
        if (PausePhrases.Contains(n)) return [new VoiceCommand(CommandKind.PauseListening)];
        if (StopTypingPhrases.Contains(n)) return [new VoiceCommand(CommandKind.StopTyping)];

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
}
