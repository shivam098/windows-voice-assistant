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

    /// <summary>When true, "start typing" searches the window for its message box if focus is elsewhere.</summary>
    public bool FocusComposer { get; init; }
}

public abstract record AppResolution
{
    public sealed record Found(AppEntry App) : AppResolution;

    public sealed record Ambiguous(IReadOnlyList<string> Names) : AppResolution;

    public sealed record NotFound : AppResolution;
}

/// <summary>Resolves spoken app names against configured apps first, then installed Start menu apps.</summary>
public sealed class AppCatalog
{
    private readonly IReadOnlyList<AppEntry> _configured;
    private IReadOnlyList<AppEntry> _discovered = [];

    public AppCatalog(IEnumerable<AppEntry> configured)
    {
        _configured = configured.ToList();
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

        var exact = _discovered.Where(a => Keys(a).Contains(key)).DistinctBy(a => a.Launch).ToList();
        if (exact.Count == 1)
        {
            return new AppResolution.Found(exact[0]);
        }

        if (exact.Count > 1)
        {
            return new AppResolution.Ambiguous(exact.Select(a => a.Name).ToList());
        }

        // "teams" -> "Microsoft Teams": allow a missing vendor prefix only when it is unique.
        var vendorless = _discovered
            .Where(a => TranscriptText.Compact(StripVendor(a.Name)) == key)
            .DistinctBy(a => a.Launch)
            .ToList();
        return vendorless.Count switch
        {
            1 => new AppResolution.Found(vendorless[0]),
            > 1 => new AppResolution.Ambiguous(vendorless.Select(a => a.Name).ToList()),
            _ => new AppResolution.NotFound(),
        };
    }

    /// <summary>Returns the configured profile (submit keys, composer focus) for a running process.</summary>
    public AppEntry? FindProfileForProcess(string processName, string? windowTitle) =>
        _configured.FirstOrDefault(a =>
            a.ProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)) &&
            (a.WindowTitleContains is null || (windowTitle?.Contains(a.WindowTitleContains, StringComparison.OrdinalIgnoreCase) ?? false)));

    private static HashSet<string> Keys(AppEntry app)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { TranscriptText.Compact(app.Name) };
        foreach (var alias in app.Aliases)
        {
            keys.Add(TranscriptText.Compact(alias));
        }

        return keys;
    }

    private static string StripVendor(string name) =>
        name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? name["Microsoft ".Length..] : name;
}
