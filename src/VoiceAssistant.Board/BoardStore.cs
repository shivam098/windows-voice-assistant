using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAssistant.Board;

/// <summary>The board's lists. Shown in this order.</summary>
public enum BoardList
{
    Ideas,
    Brainstorm,
    Todo,
    Reminders,
}

/// <summary>One note on the board. <see cref="Due"/> is set for reminders.</summary>
public sealed record BoardItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public BoardList List { get; init; }

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset? Due { get; init; }

    public bool Done { get; init; }

    /// <summary>The reminder popup was shown (so it isn't shown again after a restart).</summary>
    public bool Notified { get; init; }
}

/// <summary>Everything the board can do, for voice commands and the UI alike.</summary>
public interface IBoard
{
    IReadOnlyList<BoardItem> Items { get; }

    BoardItem Add(BoardList list, string text, DateTimeOffset? due = null);

    /// <summary>Marks the best-matching open item done. Null if nothing matches well enough.</summary>
    BoardItem? CompleteMatching(string spoken, BoardList? list = null);
}

/// <summary>
/// The board, kept in memory and saved as JSON after every change (written to a temporary file, then swapped in, so a
/// crash never leaves a half-written file). Thread-safe; <see cref="Changed"/> is raised outside the lock.
/// </summary>
public sealed class BoardStore : IBoard
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private List<BoardItem> _items = [];

    /// <param name="path">JSON file; null keeps the board in memory only (tests).</param>
    public BoardStore(string? path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
        if (path is not null && File.Exists(path))
        {
            try
            {
                _items = JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(path), JsonOptions)?.Items ?? [];
            }
            catch (JsonException)
            {
                // Keep the unreadable file for the user, start a fresh board next to it.
                File.Copy(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
                _items = [];
            }
        }
    }

    /// <summary>Raised after any change (on the thread that made it).</summary>
    public event Action? Changed;

    public IReadOnlyList<BoardItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public BoardItem Add(BoardList list, string text, DateTimeOffset? due = null)
    {
        var item = new BoardItem { List = list, Text = Tidy(text), Created = _clock(), Due = due };
        Mutate(items => items.Add(item));
        return item;
    }

    public void Update(BoardItem item) => Mutate(items =>
    {
        var index = items.FindIndex(i => i.Id == item.Id);
        if (index >= 0)
        {
            items[index] = item with { Text = Tidy(item.Text) };
        }
    });

    public void Remove(string id) => Mutate(items => items.RemoveAll(i => i.Id == id));

    public void SetDone(string id, bool done) => Change(id, i => i with { Done = done });

    /// <summary>Changes only the text, so an edit can't undo a tick, move or snooze made while it was open.</summary>
    public void SetText(string id, string text) => Change(id, i => i with { Text = Tidy(text) });

    public void Move(string id, BoardList list) => Change(id, i => i with { List = list });

    public void MarkNotified(string id) => Change(id, i => i with { Notified = true });

    /// <summary>Moves a reminder's due time (snooze) and lets it notify again.</summary>
    public void Snooze(string id, TimeSpan by) => Change(id, i => i with { Due = _clock() + by, Notified = false });

    /// <summary>Reminders whose time has come and that haven't been shown.</summary>
    public IReadOnlyList<BoardItem> DueReminders() =>
        Items.Where(i => i.Due is { } due && due <= _clock() && !i.Done && !i.Notified).ToList();

    public BoardItem? CompleteMatching(string spoken, BoardList? list = null)
    {
        var match = BoardGrammar.BestMatch(Items.Where(i => !i.Done && (list is null || i.List == list)), spoken);
        if (match is not null)
        {
            SetDone(match.Id, true);
        }

        return match;
    }

    /// <summary>Notes are short: no stray spaces, a capital first letter, no full stop at the end.</summary>
    public static string Tidy(string text)
    {
        var t = string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        t = t.TrimEnd('.', ',', ';', ':').Trim();
        return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
    }

    private void Change(string id, Func<BoardItem, BoardItem> change) => Mutate(items =>
    {
        var index = items.FindIndex(i => i.Id == id);
        if (index >= 0)
        {
            items[index] = change(items[index]);
        }
    });

    private void Mutate(Action<List<BoardItem>> change)
    {
        lock (_gate)
        {
            change(_items);
            Save();
        }

        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new BoardFile { Items = _items }, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class BoardFile
    {
        public int Version { get; set; } = 1;

        public List<BoardItem> Items { get; set; } = [];
    }
}
