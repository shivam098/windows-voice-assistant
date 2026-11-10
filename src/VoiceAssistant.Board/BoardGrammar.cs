using System.Text.RegularExpressions;

namespace VoiceAssistant.Board;

public enum BoardAction
{
    Add,
    Show,
    Complete,
}

/// <summary>A spoken board request: add Text to List (with Due for reminders), show List, or tick off Text.</summary>
public sealed record BoardIntent(BoardAction Action, BoardList? List, string Text = "", DateTimeOffset? Due = null);

/// <summary>
/// The board's voice grammar (docs/specs/idea-board.md). Works on the transcript as spoken (capitals and punctuation
/// kept for the note). <c>explicitOnly</c> is for push-to-talk, where everything else is typed: only requests that name
/// a list (“add to my ideas …”, “add … to my to-do list”) go to the board; “remind me to …”, “note down …” and the rest
/// need the wake word, because people also dictate such sentences to others.
/// </summary>
public static partial class BoardGrammar
{
    public static BoardIntent? Parse(string spoken, DateTimeOffset now, bool explicitOnly = false)
    {
        var text = spoken.Trim().Replace('’', '\'');

        // "Can you add the slides to our tasks" with push-to-talk is a request to a colleague, typed as dictation.
        if (explicitOnly && AskingSomeoneRegex().IsMatch(text))
        {
            return null;
        }

        var m = AddToListFirstRegex().Match(text);
        if (!m.Success)
        {
            m = AddToListLastRegex().Match(text);
        }

        // Push-to-talk types everything else, so a sentence for someone else ("put the agenda on the board", "add these
        // to the tasks") must stay dictation: there the list needs "my"/"our" or a name only the board uses.
        if (m.Success && ListFrom(m.Groups["list"].Value) is { } list && (!explicitOnly || m.Groups["det"].Success || BoardOnlyName(m.Groups["list"].Value)))
        {
            return AddIntent(list, m.Groups["text"].Value, now);
        }

        if (explicitOnly)
        {
            return null;
        }

        m = RemindRegex().Match(text);
        if (m.Success)
        {
            return AddIntent(BoardList.Reminders, m.Groups["text"].Value, now);
        }

        m = NoteRegex().Match(text);
        if (m.Success)
        {
            return AddIntent(BoardList.Ideas, m.Groups["text"].Value, now);
        }

        m = ShowRegex().Match(text);
        if (m.Success && ListFrom(m.Groups["list"].Value) is { } shown && (m.Groups["det"].Success || NamesTheBoard(m.Groups["list"].Value)))
        {
            // "show my board" is the whole board; a list name opens that list.
            return new BoardIntent(BoardAction.Show, IsWholeBoard(m.Groups["list"].Value) ? null : shown);
        }

        if (ShowBoardRegex().IsMatch(text))
        {
            return new BoardIntent(BoardAction.Show, null);
        }

        m = CompleteRegex().Match(text);
        if (m.Success)
        {
            var what = m.Groups["text"].Value.Trim(' ', '.', ',');
            return what.Length == 0 ? null : new BoardIntent(BoardAction.Complete, null, what);
        }

        return null;
    }

    /// <summary>
    /// Where dictation goes when there's no text box to type it into, or null if there's nothing to keep. A request that
    /// names a list or says “remind me” / “note down” is honoured. Otherwise only text that is clearly a task is filed as
    /// one: “I have to / need to / should …”, “don't forget to …”, “to-do: …”, or an instruction starting with a common
    /// action verb (“call the bank …”). A task with a future time is a reminder; without one, a to-do. Everything else,
    /// including sentences that just mention a time (“the demo today went well”), is an idea, kept word for word.
    /// </summary>
    public static BoardIntent? ForDictation(string text, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        text = text.Trim().Replace('’', '\'');
        if (BoardStore.Tidy(text).Length == 0)
        {
            return null;
        }

        if (Parse(text, now) is { Action: BoardAction.Add } asked)
        {
            return asked;
        }

        var lead = TodoRegex().Match(text);
        var task = lead.Success ? lead.Groups["text"].Value : ActionRegex().IsMatch(text) ? text : null;
        if (task is null || BoardStore.Tidy(task).Length == 0)
        {
            return new BoardIntent(BoardAction.Add, BoardList.Ideas, BoardStore.Tidy(text));
        }

        var (withoutTime, due) = ReminderTime.Parse(task, now, zone);
        if (due > now && BoardStore.Tidy(withoutTime).Length > 0)
        {
            return new BoardIntent(BoardAction.Add, BoardList.Reminders, BoardStore.Tidy(withoutTime), due);
        }

        return new BoardIntent(BoardAction.Add, BoardList.Todo, BoardStore.Tidy(task));
    }

    [GeneratedRegex(Lead + @"(?:(?:i|we)(?:\s+(?:have\s+to|need\s+to|must|should|ought\s+to|gotta|got\s+to|have\s+got\s+to)|'ve\s+got\s+to|'ll\s+have\s+to)|(?:don't|do\s+not)\s+forget(?:\s+to)?|remember\s+to|to[\s-]?do\s*[,:;.\-–—])\s*(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TodoRegex();

    // An instruction to yourself: starts with a common action verb ("call the bank", "buy milk", "email Priya").
    [GeneratedRegex(Lead + @"(?:call|phone|ring|email|e-mail|mail|text|message|ping|reply|respond|send|pay|buy|order|book|schedule|reschedule|cancel|renew|submit|file|review|check|finish|complete|fix|update|prepare|draft|write|read|clean|wash|water|pick\s+up|drop\s+off|collect|return|visit|meet|ask|tell|follow\s+up|confirm|sign|print|backup|back\s+up|charge|recharge|register|apply|transfer|deposit|withdraw)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionRegex();

    [GeneratedRegex(@"^(?:(?:please|okay|ok|so|and|also|now|then|um|uh|oh|hey|alright|right)[\s,.]+)*(?:can|could|would|will)\s+you\b", RegexOptions.IgnoreCase)]
    private static partial Regex AskingSomeoneRegex();
    /// <summary>The open item whose words best cover what was said (most recent on a tie); null below 60 %.</summary>
    public static BoardItem? BestMatch(IEnumerable<BoardItem> items, string spoken)
    {
        var said = Words(spoken);
        if (said.Count == 0)
        {
            return null;
        }

        return items
            .Select(i => (Item: i, Score: (double)said.Count(Words(i.Text).Contains) / said.Count))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.Created)
            .Select(x => x.Item)
            .FirstOrDefault();
    }

    public static BoardList? ListFrom(string spoken)
    {
        var s = spoken.ToLowerInvariant();
        return s.Contains("brainstorm", StringComparison.Ordinal) ? BoardList.Brainstorm
            : s.Contains("remind", StringComparison.Ordinal) ? BoardList.Reminders
            : s.Contains("do", StringComparison.Ordinal) || s.Contains("task", StringComparison.Ordinal) ? BoardList.Todo
            : s.Contains("idea", StringComparison.Ordinal) || s.Contains("board", StringComparison.Ordinal) || s.Contains("note", StringComparison.Ordinal) ? BoardList.Ideas
            : null;
    }

    private static bool IsWholeBoard(string list) =>
        list.Trim().ToLowerInvariant() is "board" or "idea board" || list.Trim().ToLowerInvariant().StartsWith("idea board", StringComparison.Ordinal);

    private static bool BoardOnlyName(string list) =>
        list.Contains("idea", StringComparison.OrdinalIgnoreCase) || list.Contains("brain", StringComparison.OrdinalIgnoreCase) || list.Contains("remind", StringComparison.OrdinalIgnoreCase);

    // "open notes" / "open tasks" / "open to do" may be an app; "open my notes" or "open ideas" is the board.
    private static bool NamesTheBoard(string list) =>
        list.Contains("idea", StringComparison.OrdinalIgnoreCase) || list.Contains("brain", StringComparison.OrdinalIgnoreCase)
        || list.Contains("remind", StringComparison.OrdinalIgnoreCase) || list.Contains("list", StringComparison.OrdinalIgnoreCase);

    private static BoardIntent? AddIntent(BoardList list, string text, DateTimeOffset now)
    {
        DateTimeOffset? due = null;
        if (list == BoardList.Reminders)
        {
            (text, due) = ReminderTime.Parse(text, now);
        }

        text = BoardStore.Tidy(LeadingLinkRegex().Replace(text, string.Empty));
        return text.Length == 0 ? null : new BoardIntent(BoardAction.Add, list, text, due);
    }

    private static HashSet<string> Words(string text) =>
        [.. WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 1 && w is not ("the" or "my" or "to" or "a" or "an" or "and"))];

    private const string List = @"(?<list>idea\s+board|ideas?(?:\s+list)?|board|brain\s?storm(?:ing)?(?:\s+(?:list|board|ideas))?|to[\s-]?do(?:\s+list)?|to[\s-]?dos|todos?|task\s+list|tasks|reminders?(?:\s+list)?|notes?)";
    // Recognisers often start a phrase with a joining or filler word ("and add to my reminders …", "um, can you …").
    private const string Lead = @"^(?:(?:please|okay|ok|so|and|also|now|then|um|uh|oh|hey|alright|right)[\s,.]+|(?:can|could|would|will)\s+you(?:\s+please)?[\s,]+)*";
    private const string Sep = @"[\s,:;.\-–—]+";

    // "add to my ideas: a voice game", "put this on my to-do list, renew passport"
    [GeneratedRegex(Lead + @"(?:add|put|save|write|note|jot)(?:\s+(?:this|it|that))?(?:\s+down)?\s+(?:to|in|into|on|onto|under)\s+(?:(?<det>my|our)\s+|the\s+)?" + List + Sep + @"(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AddToListFirstRegex();

    // "add renew passport to my to-do list"
    [GeneratedRegex(Lead + @"(?:add|put|save|write)\s+(?<text>.+?)\s+(?:to|in|into|on|onto|under)\s+(?:(?<det>my|our)\s+|the\s+)?" + List + @"[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex AddToListLastRegex();

    [GeneratedRegex(Lead + @"(?:remind\s+me|set\s+(?:a\s+)?reminder|add\s+(?:a\s+)?reminder|create\s+(?:a\s+)?reminder)(?:\s+(?:to|about|that|for|of))?" + Sep + @"(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RemindRegex();

    [GeneratedRegex(Lead + @"(?:note\s+down|jot\s+down|make\s+a\s+note|take\s+a\s+note|new\s+idea|i\s+have\s+an\s+idea)(?:\s+(?:that|this|of))?" + Sep + @"(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteRegex();

    [GeneratedRegex(Lead + @"(?:show|open|display|go\s+to)(?:\s+me)?\s+(?<det>my\s+|the\s+)?" + List + @"[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowRegex();

    [GeneratedRegex(Lead + @"(?:show|open)(?:\s+me)?\s+(?:my\s+|the\s+)?(?:idea\s+)?board[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowBoardRegex();

    // Needs "done" after "mark", or "off" with tick/check/cross: a bare "tick the checkbox" or "complete the form" is
    // a command for the app on screen, not the board.
    [GeneratedRegex(Lead + @"(?:mark\s+(?<text>.+?)\s+(?:as\s+)?(?:done|complete|completed|finished)|(?:tick|check|cross)\s+off\s+(?<text>.+?)|(?:tick|check|cross)\s+(?<text>.+?)\s+off)[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteRegex();

    [GeneratedRegex(@"^\s*(?:to|that|about|for)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingLinkRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
