using System.Text.RegularExpressions;

namespace VoiceAssistant.Core;

/// <summary>A rectangle in screen pixels (physical, as the platform's accessibility API reports them).</summary>
public sealed record ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(ScreenRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

public enum ElementRole
{
    Button,
    Link,
    Tab,
    MenuItem,
    TreeItem,
    ListItem,
    CheckBox,
    RadioButton,
    ComboBox,
    TextBox,
    Other,
}

/// <summary>
/// Something on screen the user can act on, as reported by the platform's accessibility API. <see cref="Id"/> is an
/// opaque handle the desktop implementation maps back to the live element; it is only valid for a short time.
/// </summary>
public sealed record UiElement(int Id, string Name, ElementRole Role, ScreenRect Rect);

/// <summary>A browser tab. <see cref="Index"/> is 1-based within its window; windows are listed front-most first.</summary>
public sealed record BrowserTab(int Id, string Title, int Index, bool IsSelected, WindowRef Window, ScreenRect Rect);

public enum ScreenRegion
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>What the user named: “the second Issues on the left” = Name “issues”, Ordinal 2, Region Left. Ordinal −1 = last.</summary>
public sealed record ElementTarget(string Name, ScreenRegion? Region = null, int? Ordinal = null)
{
    public override string ToString() => Name;
}

/// <summary>A number shown next to a candidate so the user can pick it by saying the number.</summary>
public sealed record ChoiceBadge(int Number, ScreenRect Rect, string Label);

/// <summary>
/// Per-website defaults: which text box “start typing” means on that site. Matched against the browser window's
/// cleaned title: a title segment (split on “ - ”) must equal a token, or start with it when the token ends in “*”.
/// </summary>
public sealed record SiteProfile(string Name, IReadOnlyList<string> TitleContains, IReadOnlyList<string> TextBoxNames, string? FocusKeys = null)
{
    public static readonly IReadOnlyList<SiteProfile> Defaults =
    [
        new("YouTube", ["YouTube"], ["Search"], "/"),
        new("Google", ["Google", "Google Search"], ["Search"]),
        new("Bing", ["Bing"], ["Search", "Enter your search"]),
        new("Wikipedia", ["Wikipedia"], ["Search Wikipedia", "Search"]),
        new("WhatsApp", ["WhatsApp", "WhatsApp Web"], ["Type a message", "message"]),
        new("Gmail", ["Gmail"], ["Search mail"]),
        new("ChatGPT", ["ChatGPT"], ["Ask anything", "Message", "prompt"]),
        new("Copilot", ["Microsoft Copilot*", "Copilot"], ["Message Copilot", "Ask", "message"]),
        new("Amazon", ["Amazon*"], ["Search Amazon", "Search"]),
    ];

    public static SiteProfile? ForTitle(string? windowTitle)
    {
        var segments = NameMatcher.CleanTabTitle(windowTitle).Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Defaults.FirstOrDefault(p => p.TitleContains.Any(token => segments.Any(s => Matches(s, token))));

        static bool Matches(string segment, string token)
        {
            if (!token.EndsWith('*'))
            {
                return string.Equals(segment, token, StringComparison.OrdinalIgnoreCase);
            }

            var prefix = token[..^1];
            return segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   (segment.Length == prefix.Length || !char.IsLetter(segment[prefix.Length]));
        }
    }
}

/// <summary>
/// Cleans accessibility names and scores how well a spoken phrase matches them. Platform-independent: the same rules
/// should be used on every platform so “click New on the left” behaves identically everywhere.
/// </summary>
public static partial class NameMatcher
{
    public const int Exact = 100;
    public const int Strong = 80;
    public const int Words = 70;
    public const int Partial = 60;
    public const int Fuzzy = 50;

    /// <summary>
    /// Drops keyboard-shortcut and state suffixes that apps append to accessible names:
    /// “New project, Ctrl + Alt + Shift + N” → “New project”; “Fix login, unread” → “Fix login”.
    /// </summary>
    public static string CleanElementName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = ShortcutSuffixRegex().Replace(name.Trim(), string.Empty);
        text = StateSuffixRegex().Replace(text, string.Empty);

        // Chat lists: “1 unread message Bibhash Morang 11:27 am aise bante…” → “Bibhash Morang aise bante…”.
        text = UnreadPrefixRegex().Replace(text, string.Empty);
        text = ClockTimeRegex().Replace(text, " ");
        text = MultiSpaceRegex().Replace(text, " ");
        return text.Trim().TrimEnd(',', ';').Trim();
    }

    /// <summary>
    /// Browser tab names carry extras: “(53) WhatsApp - Pinned - Memory usage - 120 MB” → “WhatsApp”;
    /// “Inbox - Gmail - Google Chrome” → “Inbox - Gmail”.
    /// </summary>
    public static string CleanTabTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = title.Replace('\u200B', ' ').Trim();
        text = NotificationCountRegex().Replace(text, string.Empty);
        var parts = text.Split(" - ").Select(p => p.Trim()).ToList();
        var kept = new List<string>();
        foreach (var part in parts)
        {
            if (TabStatusRegex().IsMatch(part))
            {
                // Status segments ("Memory usage - 54 MB") end the useful part of the title.
                break;
            }

            kept.Add(part);
        }

        while (kept.Count > 1 && BrowserNameRegex().IsMatch(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join(" - ", kept).Trim();
    }

    /// <summary>0 (no match) to 100 (exact). Plurals, spacing, hyphens and small recognition slips are tolerated.</summary>
    public static int Score(string spoken, string name)
    {
        var s = TranscriptText.Normalize(spoken);
        var n = TranscriptText.Normalize(name);
        if (s.Length == 0 || n.Length == 0)
        {
            return 0;
        }

        if (s == n)
        {
            return Exact;
        }

        var sc = Singular(s.Replace(" ", string.Empty, StringComparison.Ordinal));
        var nc = Singular(n.Replace(" ", string.Empty, StringComparison.Ordinal));
        if (sc == nc)
        {
            return Exact - 5;
        }

        var sw = s.Split(' ').Select(Singular).ToArray();
        var nw = n.Split(' ').Select(Singular).ToArray();
        if (nw.Length > sw.Length && nw.Take(sw.Length).SequenceEqual(sw))
        {
            // "new" vs "new project": a prefix of the name, shorter names score higher.
            return Strong - Math.Min(10, nw.Length - sw.Length);
        }

        if (sw.All(w => nw.Contains(w)))
        {
            return Words - Math.Min(10, nw.Length - sw.Length);
        }

        if (nw.Length >= 1 && n.Length >= 3 && nw.All(w => sw.Contains(w)))
        {
            // The user said more than the name: "the pull requests page" vs "Pull requests".
            return Partial;
        }

        var allowed = AllowedSlips(sc.Length);
        if (allowed > 0 && Levenshtein(sc, nc) <= allowed)
        {
            return Fuzzy;
        }

        // Every spoken word sounds like a word of the name: “vivash morang” vs “Bibhash Morang” (Whisper often swaps
        // v/b, drops or adds the h of bh/sh/th, and doubles letters in names).
        var nameKeys = nw.Select(SoundKey).ToArray();
        var spokenKeys = sw.Select(SoundKey).ToArray();
        if (spokenKeys.Any(k => k.Length >= 3) && spokenKeys.All(k => nameKeys.Contains(k)))
        {
            return Fuzzy;
        }

        return 0;
    }

    private static int AllowedSlips(int length) => length switch { < 4 => 0, <= 7 => 1, _ => 2 };

    /// <summary>
    /// A rough sound-alike key for one word: v/w → b, z → j, q → k, ph → f, ee → i, oo → u, an h after a consonant is
    /// dropped (bh, sh, th, kh…), and doubled letters collapse. “vivash” and “bibhash” both become “bibas”.
    /// </summary>
    public static string SoundKey(string word)
    {
        var w = word.Replace("ph", "f", StringComparison.Ordinal).Replace("ck", "k", StringComparison.Ordinal)
            .Replace("ee", "i", StringComparison.Ordinal).Replace("oo", "u", StringComparison.Ordinal);
        var key = new System.Text.StringBuilder(w.Length);
        foreach (var ch in w)
        {
            var c = ch switch { 'v' or 'w' => 'b', 'z' => 'j', 'q' => 'k', _ => ch };
            if (key.Length > 0 && ((c == 'h' && !"aeiou".Contains(key[^1], StringComparison.Ordinal)) || key[^1] == c))
            {
                continue;
            }

            key.Append(c);
        }

        return key.ToString();
    }

    private static string Singular(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;

    internal static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
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

    [GeneratedRegex(@",\s*((Ctrl|Control|Alt|Shift|Win|Windows|Cmd|Command)\s*\+.*|(Ctrl|Alt|Shift)\s*\w)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShortcutSuffixRegex();

    [GeneratedRegex(@"([,.]\s*(unread|read|selected|not selected|expanded|collapsed|checked|unchecked|pinned|new|draft|status:.*|\d+ unread.*))+$", RegexOptions.IgnoreCase)]
    private static partial Regex StateSuffixRegex();

    [GeneratedRegex(@"^\d+\s+unread\s+messages?[,.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex UnreadPrefixRegex();

    // Only after other text, so time-slot buttons (“9:30 AM”, “7:00 AM, Good morning”) keep their names.
    [GeneratedRegex(@"(?<=\S\s+)\b\d{1,2}:\d{2}(\s*[ap]\.?m\.?)?(?=\s|$|[,.])", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTimeRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^\(\d+\+?\)\s*")]
    private static partial Regex NotificationCountRegex();

    [GeneratedRegex(@"^(Pinned|Memory usage.*|Audio playing|Muted|Network error|Crashed|Sharing your screen|Camera in use|Microphone in use|Recording.*|Loading.*|Bluetooth.*|\d+(\.\d+)?\s*(KB|MB|GB))$", RegexOptions.IgnoreCase)]
    private static partial Regex TabStatusRegex();

    [GeneratedRegex(@"^(Google Chrome|Microsoft\s*Edge|Mozilla Firefox|Firefox|Brave|Opera|Vivaldi|Personal|Work|Profile \d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserNameRegex();
}

public abstract record Resolution<T>
{
    public sealed record Single(T Item) : Resolution<T>;

    public sealed record Ambiguous(IReadOnlyList<T> Items) : Resolution<T>;

    public sealed record None : Resolution<T>;
}

/// <summary>Picks the element or tab the user meant. The same rules apply on every platform.</summary>
public static class TargetResolver
{
    private static readonly Dictionary<ElementRole, int> RolePriority = new()
    {
        [ElementRole.Button] = 0, [ElementRole.Link] = 0, [ElementRole.Tab] = 0, [ElementRole.MenuItem] = 0,
        [ElementRole.TreeItem] = 1, [ElementRole.CheckBox] = 1, [ElementRole.RadioButton] = 1, [ElementRole.ComboBox] = 1,
        [ElementRole.TextBox] = 1, [ElementRole.ListItem] = 2, [ElementRole.Other] = 3,
    };

    /// <summary>
    /// Merges the same control reported twice (e.g. a list item wrapping a button of the same name, as in GitHub
    /// Copilot's sidebar), keeping the most clickable role.
    /// </summary>
    public static IReadOnlyList<UiElement> Deduplicate(IEnumerable<UiElement> elements)
    {
        var kept = new List<UiElement>();
        foreach (var element in elements.OrderBy(e => RolePriority.GetValueOrDefault(e.Role, 3)))
        {
            var name = NameMatcher.CleanElementName(element.Name);
            var duplicate = kept.Any(k =>
                string.Equals(NameMatcher.CleanElementName(k.Name), name, StringComparison.OrdinalIgnoreCase) &&
                (k.Rect.Contains(element.Rect.CenterX, element.Rect.CenterY) || element.Rect.Contains(k.Rect.CenterX, k.Rect.CenterY)));
            if (!duplicate)
            {
                kept.Add(element);
            }
        }

        return SortReadingOrder(kept);
    }

    /// <summary>Top-to-bottom, then left-to-right (rows grouped within half an element's height).</summary>
    public static IReadOnlyList<T> SortReadingOrder<T>(IEnumerable<T> items, Func<T, ScreenRect> rect)
    {
        var list = items.ToList();
        list.Sort((a, b) =>
        {
            var ra = rect(a);
            var rb = rect(b);
            var row = Math.Max(4, Math.Min(ra.Height, rb.Height) / 2);
            if (Math.Abs(ra.Y - rb.Y) > row)
            {
                return ra.Y.CompareTo(rb.Y);
            }

            return ra.X.CompareTo(rb.X);
        });
        return list;
    }

    public static IReadOnlyList<UiElement> SortReadingOrder(IEnumerable<UiElement> items) => SortReadingOrder(items, e => e.Rect);

    public static bool InRegion(ScreenRect rect, ScreenRegion region, ScreenRect window) => region switch
    {
        ScreenRegion.Left => rect.CenterX < window.X + (window.Width * 0.35),
        ScreenRegion.Right => rect.CenterX > window.X + (window.Width * 0.65),
        ScreenRegion.Top => rect.CenterY < window.Y + (window.Height * 0.3),
        ScreenRegion.Bottom => rect.CenterY > window.Y + (window.Height * 0.7),
        _ => true,
    };

    /// <summary>
    /// Elements whose cleaned name best matches the target (score ≥ <paramref name="minScore"/>), narrowed by region and
    /// ordinal. One clear winner → Single; ties → Ambiguous (the caller shows numbers); nothing → None.
    /// </summary>
    public static Resolution<UiElement> ResolveElement(IEnumerable<UiElement> elements, ElementTarget target, ScreenRect? window, int minScore = NameMatcher.Fuzzy)
    {
        var candidates = Deduplicate(elements);
        if (target.Region is { } region && window is not null)
        {
            candidates = candidates.Where(e => InRegion(e.Rect, region, window)).ToList();
        }

        if (target.Name.Length > 0)
        {
            var scored = candidates
                .Select(e => (Element: e, Score: NameMatcher.Score(target.Name, NameMatcher.CleanElementName(e.Name))))
                .Where(x => x.Score >= minScore)
                .ToList();
            if (scored.Count == 0)
            {
                return new Resolution<UiElement>.None();
            }

            var best = scored.Max(x => x.Score);
            candidates = SortReadingOrder(CollapseNested(scored.Where(x => x.Score == best).Select(x => x.Element).ToList()));
        }

        return Pick(candidates, target.Ordinal);
    }

    /// <summary>
    /// Equally good matches where one sits inside the other are the same thing on screen (a chat row and its name
    /// cell): keep the innermost.
    /// </summary>
    private static IReadOnlyList<UiElement> CollapseNested(IReadOnlyList<UiElement> tied) =>
        tied.Where(outer => !tied.Any(inner =>
                !ReferenceEquals(inner, outer) &&
                inner.Rect.Width * inner.Rect.Height < outer.Rect.Width * outer.Rect.Height &&
                outer.Rect.Contains(inner.Rect.CenterX, inner.Rect.CenterY)))
            .ToList();

    /// <summary>
    /// Tabs by position (“first tab” = in the front-most browser window) or by cleaned title. Titles are long, so a
    /// name must match whole words (“YouTube” in “Lofi beats - YouTube”).
    /// </summary>
    public static Resolution<BrowserTab> ResolveTab(IReadOnlyList<BrowserTab> tabs, ElementTarget target)
    {
        if (tabs.Count == 0)
        {
            return new Resolution<BrowserTab>.None();
        }

        if (target.Name.Length == 0)
        {
            var front = tabs[0].Window;
            var inFront = tabs.Where(t => t.Window.SameWindow(front)).OrderBy(t => t.Index).ToList();
            return Pick(inFront, target.Ordinal ?? 1);
        }

        var scored = tabs
            .Select(t => (Tab: t, Score: TabScore(target.Name, t.Title)))
            .Where(x => x.Score >= NameMatcher.Words - 10)
            .ToList();
        if (scored.Count == 0)
        {
            return new Resolution<BrowserTab>.None();
        }

        var best = scored.Max(x => x.Score);
        var top = scored.Where(x => x.Score == best).Select(x => x.Tab).ToList();
        if (top.Count > 1 && target.Ordinal is null)
        {
            // The same site open in several windows: the front-most browser window wins.
            var front = top.Where(t => t.Window.SameWindow(tabs[0].Window)).ToList();
            if (front.Count == 1)
            {
                return new Resolution<BrowserTab>.Single(front[0]);
            }
        }

        return Pick(top, target.Ordinal);
    }

    /// <summary>Best score of the spoken name against the whole cleaned title and each “ - ” segment of it.</summary>
    public static int TabScore(string spoken, string title)
    {
        var clean = NameMatcher.CleanTabTitle(title);
        var best = NameMatcher.Score(spoken, clean);
        foreach (var segment in clean.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
        {
            best = Math.Max(best, NameMatcher.Score(spoken, segment));
        }

        return best;
    }

    private static Resolution<T> Pick<T>(IReadOnlyList<T> candidates, int? ordinal)
    {
        if (candidates.Count == 0)
        {
            return new Resolution<T>.None();
        }

        if (ordinal is { } n)
        {
            var index = n == -1 ? candidates.Count - 1 : n - 1;
            return index >= 0 && index < candidates.Count ? new Resolution<T>.Single(candidates[index]) : new Resolution<T>.None();
        }

        return candidates.Count == 1 ? new Resolution<T>.Single(candidates[0]) : new Resolution<T>.Ambiguous(candidates);
    }

    /// <summary>Clicking these changes or loses data, sends money, or leaves something: ask before clicking.</summary>
    public static bool IsDestructive(string name)
    {
        var n = TranscriptText.Normalize(NameMatcher.CleanElementName(name));
        string[] words =
        [
            "delete", "remove", "merge", "discard", "uninstall", "erase", "format", "reset", "sign out", "log out", "logout",
            "signout", "unsubscribe", "deactivate", "close account", "leave", "block", "empty trash", "empty recycle bin",
            "pay", "buy", "purchase", "place order", "send money", "transfer", "dont save", "do not save", "close without saving",
            "force push", "revoke", "terminate", "shut down", "restart",
        ];
        return words.Any(w => n == w || n.StartsWith(w + " ", StringComparison.Ordinal) || n.EndsWith(" " + w, StringComparison.Ordinal) || n.Contains(" " + w + " ", StringComparison.Ordinal));
    }
}
