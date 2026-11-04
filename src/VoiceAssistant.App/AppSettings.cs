using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceAssistant.Core;

namespace VoiceAssistant.App;

internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant");

    /// <summary>
    /// A "models" folder next to the exe (portable zip with models included) wins; otherwise per-user data,
    /// where the installer or the first-run download puts them.
    /// </summary>
    public static string ModelsDir { get; } = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "models"))
        ? Path.Combine(AppContext.BaseDirectory, "models")
        : Path.Combine(DataDir, "models");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string SettingsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceAssistant", "settings.json");

    /// <summary>Learned rephrasings and app usage counts (local only).</summary>
    public static string LearnedFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceAssistant", "learned.json");
}

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ModelFile { get; set; } = "ggml-small.en-q8_0.bin";

    /// <summary>Silero voice-activity model: drops music/noise and splits merged phrases. Optional but strongly recommended.</summary>
    public string VadModelFile { get; set; } = "ggml-silero-v6.2.0.bin";

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount * 2 / 3, 2, 8);

    /// <summary>768 tokens covers 15.36 s of audio and is ~3x faster than the default 30 s window.</summary>
    public int AudioContextSize { get; set; } = 768;

    /// <summary>Optional vocabulary hint. Off by default: Whisper can echo prompt text on near-silent input.</summary>
    public string? VocabularyPrompt { get; set; }

    /// <summary>-1 uses the Windows default recording device.</summary>
    public int MicrophoneDevice { get; set; } = -1;

    public int EndSilenceMs { get; set; } = 600;

    /// <summary>
    /// While waiting for a command, longer phrases (including the ~0.9 s of padding) are not transcribed.
    /// Saves CPU during meetings and conversations; the longest command is about 4 s.
    /// </summary>
    public int MaxCommandUtteranceMs { get; set; } = 6000;

    public bool ListenOnStartup { get; set; } = true;

    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    public string CommandPrefix { get; set; } = "computer";

    public bool RequireCommandPrefixWhileTyping { get; set; }

    public bool ReturnToCommandModeAfterSend { get; set; } = true;

    /// <summary>Average token probability below which a command is ignored. Clean speech scores 0.5–1.0.</summary>
    public float MinCommandConfidence { get; set; } = 0.3f;

    /// <summary>
    /// "Paste": insert dictated text via the clipboard (reliable everywhere; kept out of Clipboard History and
    /// your previous clipboard is restored). "Type": synthesized keystrokes (some apps drop characters).
    /// </summary>
    public string InsertMethod { get; set; } = "Paste";

    public bool DiscoverStartMenuApps { get; set; } = true;

    /// <summary>When true, commands need “Hey Jarvis” first (then work without it for <see cref="AwakeSeconds"/>).</summary>
    public bool WakeWordEnabled { get; set; } = true;

    /// <summary>Accepted wake phrases (Whisper's common spellings of “Hey Jarvis” are included).</summary>
    public List<string> WakeWords { get; set; } = [.. CommandParser.DefaultWakeWords];

    public int AwakeSeconds { get; set; } = 30;

    /// <summary>Short tones when the wake word is heard, typing starts/stops, and push-to-talk starts/ends.</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>Learn rephrasings of commands and which apps you open most (stored in learned.json).</summary>
    public bool LearnFromUsage { get; set; } = true;

    /// <summary>Hold Ctrl+Win to dictate into any app (release to type). Ctrl+Win+Space toggles hands-free dictation.</summary>
    public bool PushToTalk { get; set; } = true;

    public string PasteLastHotkey { get; set; } = "Alt+Shift+Z";

    public bool RemoveFillerWords { get; set; } = true;

    public bool SpokenPunctuation { get; set; } = true;

    /// <summary>Whole-word fixes applied to dictated text (personal dictionary), e.g. { "git hub": "GitHub" }.</summary>
    public Dictionary<string, string> Replacements { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git hub"] = "GitHub",
        ["co-pilot"] = "Copilot",
        ["co pilot"] = "Copilot",
    };

    /// <summary>Say the trigger (or “insert &lt;trigger&gt;”) to type the text, e.g. { "my signature": "Thanks,\nShivam" }.</summary>
    public Dictionary<string, string> Snippets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AppEntry> Apps { get; set; } = DefaultApps();

    public static List<AppEntry> DefaultApps() =>
    [
        new AppEntry
        {
            Name = "GitHub Copilot",
            Aliases = ["github copilot", "copilot app"],
            Launch = @"shell:AppsFolder\com.github.githubapp",
            ProcessNames = ["github"],
            WindowTitleContains = "GitHub Copilot",
            SubmitKeys = "Enter",
            NewLineKeys = "Shift+Enter",
            TextArea = "Bottom",
            TextAreaName = "Message",
        },
        new AppEntry
        {
            Name = "Notepad",
            Launch = "notepad.exe",
            ProcessNames = ["Notepad"],
            SubmitKeys = string.Empty,
            NewLineKeys = "Enter",
            TextArea = "Largest",
            NewWindowKeys = "Ctrl+Shift+N",
            NewDocumentKeys = "Ctrl+N",
        },
        Browser("Google Chrome", ["chrome", "google chrome"], "chrome.exe", "chrome"),
        Browser("Microsoft Edge", ["edge", "microsoft edge"], "msedge.exe", "msedge"),
        Browser("Firefox", ["firefox", "mozilla firefox"], "firefox.exe", "firefox"),
    ];

    private static AppEntry Browser(string name, IReadOnlyList<string> aliases, string exe, string process) => new()
    {
        Name = name,
        Aliases = aliases,
        Launch = exe,
        ProcessNames = [process],
        SubmitKeys = "Enter",
        NewLineKeys = "Shift+Enter",
        SearchKeys = "Ctrl+L",
        AddressBarKeys = "Ctrl+L",
    };

    public static AppSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.GetType().Name}");
            return new AppSettings();
        }

        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }
}
