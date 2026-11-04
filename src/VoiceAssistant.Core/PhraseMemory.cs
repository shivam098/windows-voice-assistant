namespace VoiceAssistant.Core;

/// <summary>
/// What the assistant has learned from the user, kept locally:
/// <list type="bullet">
/// <item>Rephrasings: a phrase it didn't understand, followed shortly by a command it did, becomes a synonym
/// for that command — but only when both share a meaningful word (e.g. the app name), so unrelated chatter
/// before a command is never learned.</item>
/// <item>App usage counts, used to prefer the apps the user actually opens when a name is ambiguous.</item>
/// </list>
/// Not thread-safe: used from the controller's serial queue.
/// </summary>
public sealed class PhraseMemory
{
    public const int MaxPhrases = 300;

    // Words that don't identify *what* the user wants (verbs, articles, padding).
    private static readonly HashSet<string> NonDistinctive = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "to", "it", "me", "my", "i", "you", "can", "could", "would", "will", "please", "for", "up",
        "open", "launch", "start", "run", "go", "switch", "get", "give", "show", "bring", "pull", "fire", "let", "lets",
        "on", "in", "into", "and", "now", "just", "want", "need", "like", "this", "that", "some", "new", "app", "window",
        "hey", "jarvis", "ok", "okay", "do", "make", "with", "of", "is", "be", "of", "what", "how",
    };

    private readonly Dictionary<string, string> _phrases = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, int> _appUses = new(StringComparer.OrdinalIgnoreCase);

    public int PhraseCount => _phrases.Count;

    public IReadOnlyDictionary<string, string> Phrases => _phrases;

    public IReadOnlyDictionary<string, int> AppUses => _appUses;

    public bool TryGet(string phrase, out string command) => _phrases.TryGetValue(phrase, out command!);

    /// <summary>Remembers <paramref name="phrase"/> as meaning <paramref name="command"/>. Returns false if it isn't a safe pairing.</summary>
    public bool Learn(string phrase, string command)
    {
        phrase = phrase.Trim();
        command = command.Trim();
        if (phrase.Length == 0 || command.Length == 0 || phrase == command || !SharesDistinctiveWord(phrase, command))
        {
            return false;
        }

        Store(phrase, command);
        return true;
    }

    public bool Forget(string phrase)
    {
        _order.Remove(phrase);
        return _phrases.Remove(phrase);
    }

    public void RecordAppUse(string appName) => _appUses[appName] = _appUses.GetValueOrDefault(appName) + 1;

    public int UsesOf(string appName) => _appUses.GetValueOrDefault(appName);

    public void Clear()
    {
        _phrases.Clear();
        _order.Clear();
        _appUses.Clear();
    }

    /// <summary>Loads phrases (oldest first) and app usage counts.</summary>
    public void Load(IEnumerable<KeyValuePair<string, string>>? phrases, IReadOnlyDictionary<string, int>? appUses)
    {
        Clear();
        foreach (var (phrase, command) in phrases ?? [])
        {
            if (phrase.Length > 0 && command.Length > 0)
            {
                Store(phrase, command);
            }
        }

        foreach (var (app, uses) in appUses ?? new Dictionary<string, int>())
        {
            _appUses[app] = Math.Max(0, uses);
        }
    }

    /// <summary>Phrases oldest first, for saving.</summary>
    public IEnumerable<KeyValuePair<string, string>> PhrasesInOrder() =>
        _order.Select(p => new KeyValuePair<string, string>(p, _phrases[p]));

    private void Store(string phrase, string command)
    {
        if (_phrases.ContainsKey(phrase))
        {
            _order.Remove(phrase);
        }
        else if (_phrases.Count >= MaxPhrases && _order.First is { } oldest)
        {
            _phrases.Remove(oldest.Value);
            _order.RemoveFirst();
        }

        _phrases[phrase] = command;
        _order.AddLast(phrase);
    }

    public static bool SharesDistinctiveWord(string a, string b)
    {
        var words = Distinctive(b);
        return Distinctive(a).Any(words.Contains);
    }

    private static HashSet<string> Distinctive(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !NonDistinctive.Contains(w)).ToHashSet(StringComparer.Ordinal);
}
