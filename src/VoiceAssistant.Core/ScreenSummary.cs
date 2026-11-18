namespace VoiceAssistant.Core;

public enum WindowShow
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>A top-level app window: where it is and how it's shown, for the brain's view of the screen.</summary>
public sealed record DesktopWindow(WindowRef Window, WindowShow Show, ScreenRect? Rect = null, ScreenRect? WorkArea = null);

/// <summary>
/// The brain's view of the screen, in the layout the prompt was measured with: the windows front first, then what can
/// be clicked or typed into in a few of them. Pages list hundreds of items, so only those that share words with the
/// request, look like content (titles, messages) or take text are kept: a short prompt is both faster and clearer.
/// </summary>
public static class ScreenSummary
{
    // About 300 prompt tokens in all: reading the prompt costs ~4 ms a token on a Snapdragon X (docs/design/ai-brain.md).
    public const int MaxWindows = 6;
    public const int MaxScannedWindows = 2;
    public const int MaxItemsPerWindow = 14;
    public const int MaxItems = 28;
    public const int MaxNameLength = 64;

    private static readonly HashSet<string> CommonWords =
    [
        "the", "and", "for", "with", "this", "that", "these", "those", "from", "into", "onto", "about", "please", "can",
        "you", "your", "my", "me", "its", "it", "on", "in", "at", "to", "of", "a", "an", "is", "are", "be", "do", "go",
        "open", "click", "press", "show", "find", "get", "put", "make", "now", "then", "hey", "jarvis", "some", "one",

        // Generic places: “this window” or “the tab” doesn't name one.
        "window", "tab", "page", "app", "screen", "button", "link", "box", "field",
    ];

    /// <summary>How the window sits on its screen: “maximized”, “minimized”, “left half”, “right half”, or empty.</summary>
    public static string Placement(DesktopWindow window)
    {
        if (window.Show == WindowShow.Minimized)
        {
            return "minimized";
        }

        if (window.Show == WindowShow.Maximized)
        {
            return "maximized";
        }

        if (window.Rect is not { } r || window.WorkArea is not { IsEmpty: false } area)
        {
            return string.Empty;
        }

        var width = r.Width / area.Width;
        var height = r.Height / area.Height;
        if (width >= 0.95 && height >= 0.95)
        {
            return "maximized";
        }

        if (width is >= 0.4 and <= 0.6 && height >= 0.85)
        {
            return r.CenterX < area.CenterX ? "left half" : "right half";
        }

        return string.Empty;
    }

    /// <summary>
    /// The windows whose items are listed: the front one, then those the request seems to be about, then the next ones
    /// behind (minimized windows can't be read).
    /// </summary>
    public static IReadOnlyList<DesktopWindow> ChooseWindowsToScan(IReadOnlyList<DesktopWindow> windows, string request, Func<WindowRef, string> appName)
    {
        var words = RequestWords(request);
        var visible = windows.Take(MaxWindows).Where(w => w.Show != WindowShow.Minimized).ToList();
        var chosen = visible.Take(1).ToList();
        chosen.AddRange(visible.Skip(1).Where(w => Mentions(words, w.Window.Title + " " + appName(w.Window))));
        chosen.AddRange(visible.Skip(1));
        return chosen.Distinct().Take(MaxScannedWindows).ToList();
    }

    /// <summary>The screen text, starting with “Windows (front first):”.</summary>
    public static string Format(
        IReadOnlyList<DesktopWindow> windows,
        IReadOnlyDictionary<nint, IReadOnlyList<UiElement>> items,
        string request,
        Func<WindowRef, string> appName)
    {
        var lines = new List<string> { "Windows (front first):" };
        var listed = windows.Take(MaxWindows).ToList();
        if (listed.Count == 0)
        {
            lines.Add("(no windows open)");
        }

        for (var i = 0; i < listed.Count; i++)
        {
            var w = listed[i];
            var placement = Placement(w);
            var where = i == 0 && w.Show != WindowShow.Minimized
                ? (placement.Length == 0 ? "in front" : placement + ", in front")
                : (placement.Length == 0 ? "behind" : placement);
            lines.Add($"{i + 1}. \"{Title(w.Window)}\" ({BrainSafety.CleanText(appName(w.Window), 40)}), {where}");
        }

        var budget = MaxItems;
        foreach (var w in listed)
        {
            if (budget <= 0 || !items.TryGetValue(w.Window.Handle, out var found))
            {
                continue;
            }

            var picked = PickItems(found, request, Math.Min(MaxItemsPerWindow, budget));
            budget -= picked.Count;
            lines.Add(picked.Count == 0
                ? $"Items in \"{Title(w.Window)}\": (none found)"
                : $"Items in \"{Title(w.Window)}\": {string.Join("; ", picked.Select(e => $"{RoleWord(e.Role)} \"{BrainSafety.CleanText(NameMatcher.CleanElementName(e.Name), MaxNameLength)}\""))}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// At most <paramref name="max"/> items, in reading order: those naming words of the request first, then text
    /// boxes and content-like names (three or more words: titles, messages, files), then the rest.
    /// </summary>
    public static IReadOnlyList<UiElement> PickItems(IReadOnlyList<UiElement> elements, string request, int max)
    {
        if (max <= 0)
        {
            return [];
        }

        var words = RequestWords(request);
        var roleWanted = RoleWanted(TranscriptText.Normalize(request).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var ordered = TargetResolver.Deduplicate(elements.Where(e => NameMatcher.CleanElementName(e.Name).Length > 0 || e.Role == ElementRole.TextBox));
        return ordered
            .Select((e, index) => (Element: e, Index: index, Score: Score(e, words, roleWanted)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(max)
            .OrderBy(x => x.Index)
            .Select(x => x.Element)
            .ToList();
    }

    private static int Score(UiElement element, IReadOnlyList<string> words, ElementRole? roleWanted)
    {
        var name = TranscriptText.Normalize(NameMatcher.CleanElementName(element.Name));
        var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var score = 10 * words.Count(w => nameWords.Any(n => n == w || (w.Length >= 4 && n.StartsWith(w, StringComparison.Ordinal)) || (n.Length >= 4 && w.StartsWith(n, StringComparison.Ordinal))));
        if (element.Role == ElementRole.TextBox)
        {
            score += 3;
        }

        if (nameWords.Length >= 3 || name.Length >= 24)
        {
            // Titles, messages and file names rather than menus.
            score += 2;
        }

        if (roleWanted == element.Role)
        {
            score += 5;
        }

        return score;
    }

    private static ElementRole? RoleWanted(IReadOnlyList<string> words) =>
        words.Contains("tab") ? ElementRole.Tab :
        words.Contains("button") ? ElementRole.Button :
        words.Contains("link") ? ElementRole.Link :
        words.Any(w => w is "box" or "field") ? ElementRole.TextBox : null;

    private static List<string> RequestWords(string request) =>
        TranscriptText.Normalize(request).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !CommonWords.Contains(w))
            .Distinct()
            .ToList();

    private static bool Mentions(IReadOnlyList<string> words, string text)
    {
        var known = TranscriptText.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => known.Any(k => k == w || (w.Length >= 4 && k.StartsWith(w, StringComparison.Ordinal))));
    }

    private static string Title(WindowRef window) => BrainSafety.CleanText(window.Title.Replace('\u200B', ' '), 100);

    private static string RoleWord(ElementRole role) => role switch
    {
        ElementRole.Button => "button",
        ElementRole.Link => "link",
        ElementRole.Tab => "tab",
        ElementRole.MenuItem => "menuitem",
        ElementRole.TreeItem => "treeitem",
        ElementRole.ListItem => "listitem",
        ElementRole.CheckBox => "checkbox",
        ElementRole.RadioButton => "radio",
        ElementRole.ComboBox => "combobox",
        ElementRole.TextBox => "edit",
        _ => "item",
    };
}
