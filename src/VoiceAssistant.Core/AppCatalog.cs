namespace VoiceAssistant.Core;

/// <summary>An application the assistant can open and type into.</summary>
public sealed record AppEntry
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>"shell:AppsFolder\&lt;AppID&gt;", an executable name, or a full path.</summary>
    public required string Launch { get; init; }

    /// <summary>Process names (without .exe) used to find an already-running window.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    /// <summary>Optional substring the main window title must contain.</summary>
    public string? WindowTitleContains { get; init; }

    public string SubmitKeys { get; init; } = "Enter";

    public string NewLineKeys { get; init; } = "Shift+Enter";

    /// <summary>
    /// How “start typing” finds the text area when focus is elsewhere: "Bottom" (chat message box at the
    /// bottom of the window) or "Largest" (document editor). Null: type wherever focus already is.
    /// </summary>
    public string? TextArea { get; init; }

    /// <summary>
    /// Accessible name (or help-text fragment) identifying the right text area when the window has several,
    /// e.g. GitHub Copilot's "Message" box versus its side-panel editor. Focus elsewhere is moved to it.
    /// </summary>
    public string? TextAreaName { get; init; }

    /// <summary>Keys that focus the app's search box (browsers: "Ctrl+L" for the address/search bar).</summary>
    public string? SearchKeys { get; init; }

    /// <summary>Keys that focus the address bar (browsers).</summary>
    public string? AddressBarKeys { get; init; }

    /// <summary>When “open” finds the app already running, press these to get a fresh window (Notepad: Ctrl+Shift+N).</summary>
    public string? NewWindowKeys { get; init; }

    /// <summary>After launching the app, press these for a blank document (Notepad restores old tabs: Ctrl+N).</summary>
    public string? NewDocumentKeys { get; init; }
}

public abstract record AppResolution
{
    public sealed record Found(AppEntry App) : AppResolution;

    public sealed record Ambiguous(IReadOnlyList<string> Names) : AppResolution;

    public sealed record NotFound : AppResolution;
}

/// <summary>
/// Resolves spoken app names: configured apps, then installed Start menu apps — exact, then relaxed
/// ("teams" → "Microsoft Teams", "outlook" → "Outlook (new)"), then near-misses ("notpad", "crome").
/// Ties go to the app the user opens most.
/// </summary>
public sealed class AppCatalog
{
    private readonly IReadOnlyList<AppEntry> _configured;
    private readonly PhraseMemory? _memory;
    private IReadOnlyList<AppEntry> _discovered = [];

    public AppCatalog(IEnumerable<AppEntry> configured, PhraseMemory? memory = null)
    {
        _configured = configured.ToList();
        _memory = memory;
    }

    public void SetDiscoveredApps(IEnumerable<AppEntry> discovered) => _discovered = discovered.ToList();

    public AppResolution Resolve(string spokenName)
    {
        var key = TranscriptText.Compact(spokenName);
        if (key.Length == 0)
        {
            return new AppResolution.NotFound();
        }

        var configured = _configured.Where(a => Keys(a).Contains(key)).ToList();
        if (configured.Count > 0)
        {
            return new AppResolution.Found(configured[0]);
        }

        var all = _configured.Concat(_discovered).DistinctBy(a => a.Launch).ToList();
        var tiers = new Func<AppEntry, bool>[]
        {
            a => Keys(a).Contains(key),
            a => RelaxedKeys(a).Contains(key),
            a => key.Length >= 4 && RelaxedKeys(a).Any(k => k.StartsWith(key, StringComparison.Ordinal) && k.Length - key.Length <= 8),
        };

        foreach (var tier in tiers)
        {
            var matches = all.Where(tier).ToList();
            if (matches.Count > 0)
            {
                return Pick(matches);
            }
        }

        // Near-misses from speech recognition or pronunciation: allow 1–2 letter differences on longer names.
        var allowed = key.Length switch { < 4 => 0, <= 7 => 1, _ => 2 };
        if (allowed > 0)
        {
            var scored = all
                .Select(a => (App: a, Distance: RelaxedKeys(a).Min(k => Levenshtein(k, key))))
                .Where(x => x.Distance <= allowed)
                .ToList();
            if (scored.Count > 0)
            {
                var best = scored.Min(x => x.Distance);
                return Pick(scored.Where(x => x.Distance == best).Select(x => x.App).ToList());
            }
        }

        return new AppResolution.NotFound();
    }

    /// <summary>Returns the configured profile (submit keys, composer focus) for a running process.</summary>
    public AppEntry? FindProfileForProcess(string processName, string? windowTitle) =>
        _configured.FirstOrDefault(a =>
            a.ProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)) &&
            (a.WindowTitleContains is null || (windowTitle?.Contains(a.WindowTitleContains, StringComparison.OrdinalIgnoreCase) ?? false)));

    private AppResolution Pick(List<AppEntry> matches)
    {
        if (matches.Count == 1)
        {
            return new AppResolution.Found(matches[0]);
        }

        // Configured apps (with tuned behaviour) win; then the one the user opens most, if it clearly leads.
        var configured = matches.Where(m => _configured.Contains(m)).ToList();
        if (configured.Count == 1)
        {
            return new AppResolution.Found(configured[0]);
        }

        if (_memory is not null)
        {
            var ranked = matches.OrderByDescending(m => _memory.UsesOf(m.Name)).ToList();
            if (_memory.UsesOf(ranked[0].Name) > _memory.UsesOf(ranked[1].Name))
            {
                return new AppResolution.Found(ranked[0]);
            }
        }

        return new AppResolution.Ambiguous(matches.Select(a => a.Name).ToList());
    }

    private static HashSet<string> Keys(AppEntry app)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { TranscriptText.Compact(app.Name) };
        foreach (var alias in app.Aliases)
        {
            keys.Add(TranscriptText.Compact(alias));
        }

        return keys;
    }

    /// <summary>Name without vendor prefix and parenthetical: "Microsoft Outlook (new)" → "outlook".</summary>
    private static HashSet<string> RelaxedKeys(AppEntry app)
    {
        var keys = Keys(app);
        var name = System.Text.RegularExpressions.Regex.Replace(app.Name, @"\([^)]*\)", " ");
        keys.Add(TranscriptText.Compact(name));
        keys.Add(TranscriptText.Compact(StripVendor(name.Trim())));
        keys.Remove(string.Empty);
        return keys;
    }

    private static string StripVendor(string name)
    {
        foreach (var vendor in new[] { "Microsoft ", "Google ", "Mozilla ", "Adobe " })
        {
            if (name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            {
                return name[vendor.Length..];
            }
        }

        return name;
    }

    private static int Levenshtein(string a, string b)
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
}
