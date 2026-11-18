using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Core;

/// <summary>Whether the on-device AI brain can take a request now (docs/design/ai-brain.md).</summary>
public enum BrainAvailability
{
    /// <summary>Switched off in Settings: requests the fixed commands don't know get the usual “not a command I know”.</summary>
    Off,

    /// <summary>Switched on, but its files aren't on this PC yet (Settings downloads them).</summary>
    NotInstalled,

    /// <summary>Installed but not loaded (it unloads after a while unused): the next request loads it first.</summary>
    Sleeping,

    Loading,

    Ready,

    /// <summary>It couldn't start; the next request tries again.</summary>
    Failed,
}

public sealed record ChatTurn(string Role, string Content);

/// <summary>The language model that turns a request and a screen summary into a plan. llama.cpp in the app; fakes in tests.</summary>
public interface IBrainModel
{
    BrainAvailability Availability { get; }

    /// <summary>
    /// The model's reply to the conversation, constrained to <see cref="BrainPrompt.Grammar"/>. Loads the model first
    /// when it is sleeping. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken);

    /// <summary>
    /// A request is probably coming (“Hey Jarvis” on its own): if the model is sleeping, start loading it in the
    /// background so the request waits less. Never throws.
    /// </summary>
    void Prepare()
    {
    }
}

/// <summary>The tools the brain may use. Each one is an ability the assistant already has.</summary>
public enum BrainTool
{
    FocusWindow,
    OpenApp,
    OpenUrl,
    WebSearch,
    Click,
    TypeText,
    Press,
    Scroll,
    Ask,
}

public sealed record BrainStep(BrainTool Tool, string Target, string Text);

/// <summary>What the brain decided: steps to run in order, and one short sentence to show.</summary>
public sealed record BrainPlan(IReadOnlyList<BrainStep> Steps, string Say)
{
    public const int MaxSteps = 8;
    public const int MaxFieldLength = 300;

    public static readonly IReadOnlyDictionary<string, BrainTool> ToolNames = new Dictionary<string, BrainTool>(StringComparer.OrdinalIgnoreCase)
    {
        ["focus_window"] = BrainTool.FocusWindow, ["open_app"] = BrainTool.OpenApp, ["open_url"] = BrainTool.OpenUrl,
        ["web_search"] = BrainTool.WebSearch, ["click"] = BrainTool.Click, ["type_text"] = BrainTool.TypeText,
        ["press"] = BrainTool.Press, ["scroll"] = BrainTool.Scroll, ["ask"] = BrainTool.Ask,
    };

    public static string NameOf(BrainTool tool) => ToolNames.First(t => t.Value == tool).Key;

    private static readonly HashSet<string> NewTabChords = new(StringComparer.OrdinalIgnoreCase) { "Ctrl+T", "Ctrl+N" };

    private static readonly HashSet<string> NewTabButtons = new(StringComparer.Ordinal) { "new tab", "new window", "open new tab", "open a new tab" };

    /// <summary>
    /// The steps as they will run: open_url and web_search open their own tab, so a step that only opens an empty tab or
    /// window right before one (Ctrl+T, clicking “New Tab”) is dropped rather than leaving it behind.
    /// </summary>
    public static IReadOnlyList<BrainStep> Tidy(IReadOnlyList<BrainStep> steps) =>
        steps.Where((step, i) => !(i + 1 < steps.Count && steps[i + 1].Tool is BrainTool.OpenUrl or BrainTool.WebSearch && OpensEmptyTab(step))).ToList();

    /// <summary>
    /// For “open X in a new tab” and tab names that match no tab: only the steps that open a page, as nothing else was
    /// asked for. Null when the plan opens no page (“a new GitHub tab” planned as switching to the GitHub app and clicking
    /// there; an answer with no steps), so the request falls back to the fixed handling; a question is kept.
    /// </summary>
    public static IReadOnlyList<BrainStep>? OnlyPages(IReadOnlyList<BrainStep> steps)
    {
        var pages = steps.Where(s => s.Tool is BrainTool.OpenUrl or BrainTool.WebSearch).ToList();
        return pages.Count > 0 ? pages : steps.Count > 0 && steps.All(s => s.Tool == BrainTool.Ask) ? steps : null;
    }

    private static bool OpensEmptyTab(BrainStep step) => step.Tool switch
    {
        BrainTool.Press => BrainSafety.NormalizeChord(step.Target.Length > 0 ? step.Target : step.Text) is { } chord && NewTabChords.Contains(chord),
        BrainTool.Click => NewTabButtons.Contains(TranscriptText.Normalize(NameMatcher.CleanElementName(step.Target.Length > 0 ? step.Target : step.Text))),
        _ => false,
    };

    /// <summary>
    /// Reads the model's reply leniently: the JSON object inside it, missing fields empty. Null when it isn't a usable
    /// plan, with a reason that never contains the reply (it is logged).
    /// </summary>
    public static BrainPlan? Parse(string? reply, out string? error)
    {
        error = null;
        var start = reply?.IndexOf('{') ?? -1;
        var end = reply?.LastIndexOf('}') ?? -1;
        if (reply is null || start < 0 || end <= start)
        {
            error = "no JSON object";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not an object";
                return null;
            }

            var steps = new List<BrainStep>();
            if (root.TryGetProperty("steps", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array)
                {
                    error = "steps is not a list";
                    return null;
                }

                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !ToolNames.TryGetValue(Text(item, "tool", 40), out var tool))
                    {
                        error = "unknown tool";
                        return null;
                    }

                    steps.Add(new BrainStep(tool, Text(item, "target", MaxFieldLength), Text(item, "text", MaxFieldLength)));
                }
            }

            if (steps.Count > MaxSteps)
            {
                error = "too many steps";
                return null;
            }

            return new BrainPlan(steps, Text(root, "say", 200));
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return null;
        }
    }

    private static string Text(JsonElement element, string name, int max) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? BrainSafety.CleanText(value.GetString(), max, keepQuotes: true) : string.Empty;
}

/// <summary>
/// The brain's limits, enforced in code whatever the model says: which keys it may press, which links it may open,
/// which windows it may type into, and what needs a “yes” first.
/// </summary>
public static class BrainSafety
{
    /// <summary>Longer text needs a “yes” first.</summary>
    public const int MaxTypedWithoutAsking = 200;

    private static readonly Dictionary<string, string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Ctrl", ["control"] = "Ctrl", ["alt"] = "Alt", ["option"] = "Alt", ["shift"] = "Shift",
        ["win"] = "Win", ["windows"] = "Win", ["super"] = "Win", ["meta"] = "Win", ["cmd"] = "Win",
        ["enter"] = "Enter", ["return"] = "Enter", ["tab"] = "Tab", ["esc"] = "Esc", ["escape"] = "Esc",
        ["backspace"] = "Backspace", ["delete"] = "Delete", ["del"] = "Delete", ["space"] = "Space", ["spacebar"] = "Space",
        ["home"] = "Home", ["end"] = "End", ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["uparrow"] = "Up", ["downarrow"] = "Down", ["leftarrow"] = "Left", ["rightarrow"] = "Right",
        ["arrowup"] = "Up", ["arrowdown"] = "Down", ["arrowleft"] = "Left", ["arrowright"] = "Right",
        ["pageup"] = "PageUp", ["pgup"] = "PageUp", ["pagedown"] = "PageDown", ["pgdn"] = "PageDown",
    };

    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    /// <summary>Win shortcuts that only arrange windows. Any other Win shortcut (Win+R runs commands) is refused.</summary>
    private static readonly HashSet<string> AllowedWinChords = new(StringComparer.OrdinalIgnoreCase)
        { "Win+Up", "Win+Down", "Win+Left", "Win+Right", "Win+D", "Win+M", "Shift+Win+Left", "Shift+Win+Right" };

    /// <summary>
    /// Other shortcuts the brain never presses: Ctrl+Esc opens Start; clipboard keys would paste text past the typing
    /// checks (type_text types instead); menu keys open menus whose letters act with no name to check (it clicks menu
    /// items by name instead).
    /// </summary>
    private static readonly HashSet<string> ClipboardChords = new(StringComparer.OrdinalIgnoreCase)
        { "Ctrl+C", "Ctrl+X", "Ctrl+V", "Ctrl+Shift+V", "Ctrl+Alt+V" };

    private static readonly HashSet<string> MenuChords = new(StringComparer.OrdinalIgnoreCase)
        { "Alt", "F10", "Shift+F10", "Ctrl+F10", "Alt+Space" };

    /// <summary>Keys that close, quit or delete: a “yes” first.</summary>
    private static readonly HashSet<string> ConfirmChords = new(StringComparer.OrdinalIgnoreCase)
        { "Alt+F4", "Delete", "Shift+Delete", "Ctrl+D", "Ctrl+Q", "Ctrl+Shift+Q", "Ctrl+Shift+W" };

    // Alt+letter opens menus and ribbon commands (Alt+H, D deletes a mail in Outlook): a “yes” first.
    private static readonly System.Text.RegularExpressions.Regex AltAccessKey = new(
        @"^Alt\+(Shift\+)?[A-Z0-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Keys that submit what's in a box or press the focused button: Enter with any modifier (Alt+Enter and Shift+Enter
    /// open an address in a new tab), Space and Alt+S.
    /// </summary>
    private static readonly HashSet<string> SubmitChords = new(StringComparer.OrdinalIgnoreCase)
        { "Enter", "Space", "Alt+S" };

    /// <summary>
    /// Terminals, the Run box, Task Manager's “Run new task”, launchers, the Start menu's search and File Explorer (its
    /// address bar starts programs, its keys open and delete files, the Run box is its dialog) run what they get: the
    /// brain never types or presses keys there (screen text could steer the model). Dictating there yourself still works.
    /// </summary>
    private static readonly HashSet<string> CommandProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "OpenConsole", "conhost", "wt", "wsl", "wslhost",
        "bash", "mintty", "putty", "ssh", "regedit", "mmc", "Taskmgr", "SearchHost", "SearchApp", "StartMenuExperienceHost",
        "ShellExperienceHost", "PowerToys.PowerLauncher", "Microsoft.CmdPal.UI", "explorer",
    };

    private static readonly string[] SendWords =
    [
        "send", "post", "submit", "publish", "tweet", "share", "comment", "reply all", "forward", "install", "allow", "accept",
        "agree", "approve", "confirm", "run", "end task", "end process", "run anyway", "keep anyway", "repost", "retweet",
    ];

    private static readonly string[] SearchBoxWords = ["search", "find", "filter", "query"];

    /// <summary>
    /// Web browsers, by process, whether or not the app list has them: their own bars open what is typed, so the brain
    /// types only into the page.
    /// </summary>
    public static readonly IReadOnlySet<string> BrowserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "chromium", "thorium", "librewolf", "waterfox",
        "floorp", "zen", "iexplore", "browser", "DuckDuckGo", "AvastBrowser", "AVGBrowser", "CCleanerBrowser", "whale",
        "maxthon", "slimjet", "iron", "seamonkey", "palemoon", "basilisk", "mullvadbrowser",
    };

    // Host names that only exist on a local network (and reserved ones): never opened, typed or searched as addresses.
    private static readonly HashSet<string> LocalSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { "local", "localhost", "lan", "home", "internal", "intranet", "corp", "localdomain", "arpa", "test", "invalid" };

    /// <summary>
    /// Endings that make a spoken word a web address (“instagram.com”) rather than a file (“report.docx”, “notes.md”,
    /// “setup.py”): common generic and country domains only.
    /// </summary>
    private static readonly HashSet<string> WebDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gov", "mil", "int", "io", "co", "ai", "app", "dev", "me", "tv", "info", "biz", "xyz",
        "in", "uk", "us", "ca", "au", "nz", "de", "fr", "es", "it", "nl", "se", "no", "dk", "fi", "ch", "at", "be", "ie",
        "pt", "br", "mx", "ar", "jp", "kr", "cn", "hk", "tw", "sg", "my", "id", "ph", "th", "vn", "pk", "bd", "lk", "np",
        "ae", "sa", "za", "ng", "ke", "eu", "asia", "gg", "ly", "fm", "so", "to", "news", "blog", "site", "online", "store",
        "shop", "tech", "cloud", "page", "studio", "live", "today", "world", "space", "website", "club", "top",
    };

    /// <summary>Boxes inside other apps that run what is typed: VS Code's terminal, a browser's developer console.</summary>
    private static readonly string[] CommandBoxWords = ["terminal", "console", "command prompt", "command line", "powershell", "bash", "shell"];

    // What a browser box would open as an address rather than search for: other schemes, paths, local and numeric hosts.
    private static readonly System.Text.RegularExpressions.Regex AddressText = new(
        @"://|^\s*(javascript|vbscript|data|file|blob|about|chrome|edge|brave|opera|vivaldi|view-source|filesystem|search-ms|shell|ms-[a-z-]+)\s*:|^\s*\\\\|^\s*[a-z]:[\\/]|\blocalhost\b|\b\d{1,3}(\.\d{1,3}){3}\b|\b0x[0-9a-f]+\.",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The extension ends the name (or comes before a comma, as Explorer adds details): "Node.js tutorial" isn't a file.
    private static readonly System.Text.RegularExpressions.Regex ProgramFile = new(
        @"\.(exe|bat|cmd|com|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|msi|msix|msixbundle|appx|appxbundle|scr|hta|jar|reg|lnk|cpl|inf|url|application)\s*(,|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// One line without control characters, at most <paramref name="max"/> characters. Double quotes become single ones
    /// unless kept, so on-screen names can't break the prompt's layout.
    /// </summary>
    public static string CleanText(string? text, int max, bool keepQuotes = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c == '"' && !keepQuotes ? '\'' : c);
            if (sb.Length > max)
            {
                break;
            }
        }

        var clean = sb.ToString();
        return clean.Length <= max ? clean : clean[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>“ctrl + w” → “Ctrl+W”, “win+up” → “Win+Up”, “m” → “M”. Null if a key isn't one the assistant can press.</summary>
    public static string? NormalizeChord(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return null;
        }

        var parts = keys.Trim().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0))
        {
            return null;
        }

        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (var raw in parts)
        {
            var part = raw.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
            string? name = KeyNames.GetValueOrDefault(part);
            if (name is null && part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                name = char.ToUpperInvariant(part[0]).ToString();
            }
            else if (name is null && part.Length is 2 or 3 && part[0] is 'F' or 'f' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
            {
                name = "F" + f;
            }

            if (name is null)
            {
                return null;
            }

            if (ModifierOrder.Contains(name) && parts.Length > 1 && key is null && raw != parts[^1])
            {
                modifiers.Add(name);
            }
            else if (key is null)
            {
                key = name;
            }
            else
            {
                // Two non-modifier keys ("ctrl+a+c"): not a chord.
                return null;
            }
        }

        if (key is null)
        {
            return null;
        }

        return string.Join('+', ModifierOrder.Where(modifiers.Contains).Append(key));
    }

    /// <summary>Why the brain never presses <paramref name="chord"/>, or null if it may.</summary>
    public static string? RefusalReason(string chord) =>
        chord.Equals("Ctrl+Esc", StringComparison.OrdinalIgnoreCase) || (chord.Split('+').Contains("Win") && !AllowedWinChords.Contains(chord)) ? "it can run commands"
        : ClipboardChords.Contains(chord) ? "I type text myself rather than use the clipboard"
        : MenuChords.Contains(chord) ? "I click menu items by name instead"
        : null;

    /// <summary>Win shortcuts other than window arranging (Win+R, Win+X, the Start menu…), Ctrl+Esc, clipboard and menu keys.</summary>
    public static bool IsBlockedChord(string chord) => RefusalReason(chord) is not null;

    public static bool NeedsConfirmation(string chord) => ConfirmChords.Contains(chord) || AltAccessKey.IsMatch(chord);

    public static bool IsSubmitChord(string chord) => SubmitChords.Contains(chord) || chord.EndsWith("+Enter", StringComparison.OrdinalIgnoreCase);

    /// <summary>A key that types a character when a text box has the focus (“M”, “Shift+7”): typing, as far as the limits go.</summary>
    public static bool IsCharacterKey(string chord) =>
        chord.Length == 1 || (chord.Length == 7 && chord.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A window where text or keys can run a command: terminals, the Run box, Task Manager, launchers, the Start menu
    /// and search, File Explorer and the desktop (by process; the desktop may add windows it recognises).
    /// </summary>
    public static bool IsCommandWindow(WindowRef window) => CommandProcesses.Contains(window.ProcessName);

    /// <summary>A file or folder in File Explorer or on the desktop: opening it needs a “yes”, as known extensions are hidden.</summary>
    public static bool IsShellItem(WindowRef window, ElementRole role) =>
        role == ElementRole.ListItem && string.Equals(window.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Clicking these sends, publishes, installs, runs or agrees to something: a “yes” first (on top of <see cref="TargetResolver.IsDestructive"/>).</summary>
    public static bool IsSendLike(string name)
    {
        var n = TranscriptText.Normalize(NameMatcher.CleanElementName(name));
        return SendWords.Any(w => n == w || n.StartsWith(w + " ", StringComparison.Ordinal) || n.EndsWith(" " + w, StringComparison.Ordinal));
    }

    /// <summary>A file that starts a program or script when opened (“setup.exe”, “run.bat”): opening it needs a “yes”.</summary>
    public static bool RunsProgram(string name) => ProgramFile.IsMatch(name);

    /// <summary>A search or filter box: pressing Enter there searches rather than sends.</summary>
    public static bool IsSearchBox(string? name) => HasWord(name, SearchBoxWords);

    /// <summary>A box inside an app that runs what is typed (a terminal or console): the brain never types there.</summary>
    public static bool IsCommandBox(string? name) => HasWord(name, CommandBoxWords);

    public static bool IsBrowser(WindowRef window) => BrowserProcesses.Contains(window.ProcessName);

    /// <summary>
    /// Text a browser's search box would open as an address that open_url refuses: another scheme, a path, a local or
    /// numeric host. One word with a dot, slash or colon is opened rather than searched for, so it must be a web host.
    /// </summary>
    public static bool IsRefusedAddress(string? text)
    {
        var t = text?.Trim() ?? string.Empty;
        return AddressText.IsMatch(t) ||
            (t.Length > 0 && !t.Any(char.IsWhiteSpace) && t.IndexOfAny(['.', '/', ':', '\\']) >= 0 && NormalizeUrl(t) is null);
    }

    private static bool HasWord(string? name, string[] words)
    {
        var n = TranscriptText.Normalize(name);
        return n.Length > 0 && words.Any(w => (" " + n + " ").Contains(" " + w, StringComparison.Ordinal));
    }

    /// <summary>
    /// An http(s) web address, adding https:// to a bare one (“youtube.com”). Other schemes (files, scripts, settings)
    /// and local or numeric hosts are refused.
    /// </summary>
    public static Uri? NormalizeUrl(string? target)
    {
        var text = target?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (text.Contains(':', StringComparison.Ordinal))
            {
                // "javascript:…", "file:…", "ms-settings:…"
                return null;
            }

            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        // A trailing dot ("localhost.") still names the same host, and browsers read a host ending in a number
        // ("0x7f.1.", "10.0.0.1.") as an IP address.
        var host = uri.IdnHost.TrimEnd('.');
        var last = host[(host.LastIndexOf('.') + 1)..];
        if (uri.HostNameType != UriHostNameType.Dns || !host.Contains('.', StringComparison.Ordinal) ||
            host.Contains("..", StringComparison.Ordinal) || LocalSuffixes.Contains(last) ||
            !host.Any(char.IsLetter) || uri.UserInfo.Length > 0 || last.All(char.IsAsciiDigit) ||
            (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && last[2..].All(char.IsAsciiHexDigit)) ||
            System.Net.IPAddress.TryParse(host, out _))
        {
            return null;
        }

        return uri;
    }

    /// <summary>
    /// A web address as said, like “instagram.com” or “github.com/shivam098”: one word ending in a common web domain
    /// (.com, .org, .in…) or starting with www. or http, that <see cref="NormalizeUrl"/> allows. File names
    /// (“report.docx”, “notes.md”) aren't addresses; neither are local or numeric hosts.
    /// </summary>
    public static Uri? WebAddress(string? text)
    {
        var t = text?.Trim().TrimEnd('.', ',', '!', '?', ';', ':') ?? string.Empty;
        if (t.Length == 0 || t.Any(char.IsWhiteSpace) || !t.Contains('.', StringComparison.Ordinal) || NormalizeUrl(t) is not { } uri)
        {
            return null;
        }

        var host = uri.IdnHost.TrimEnd('.');
        return WebDomains.Contains(host[(host.LastIndexOf('.') + 1)..]) ||
            t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    /// <summary>The site's own search results page; Google for sites without one.</summary>
    public static (Uri Url, string Site) SearchUrl(string? site, string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var s = TranscriptText.Compact(site);
        return s switch
        {
            "youtube" => (new Uri($"https://www.youtube.com/results?search_query={q}"), "YouTube"),
            "bing" => (new Uri($"https://www.bing.com/search?q={q}"), "Bing"),
            "wikipedia" => (new Uri($"https://en.wikipedia.org/w/index.php?search={q}"), "Wikipedia"),
            "github" => (new Uri($"https://github.com/search?q={q}"), "GitHub"),
            "maps" or "googlemaps" => (new Uri($"https://www.google.com/maps/search/{q}"), "Google Maps"),
            "" or "google" or "web" or "internet" => (new Uri($"https://www.google.com/search?q={q}"), "Google"),
            _ => (new Uri($"https://www.google.com/search?q={Uri.EscapeDataString(CleanText(site, 40) + " " + query.Trim())}"), "Google"),
        };
    }
}
