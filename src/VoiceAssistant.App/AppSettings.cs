using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceAssistant.Core;

namespace VoiceAssistant.App;

internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant");

    public static string ModelsDir => Path.Combine(DataDir, "models");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string SettingsFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceAssistant", "settings.json");
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

    public bool DiscoverStartMenuApps { get; set; } = true;

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
            FocusComposer = true,
        },
        new AppEntry
        {
            Name = "Notepad",
            Launch = "notepad.exe",
            ProcessNames = ["Notepad"],
            SubmitKeys = string.Empty,
            NewLineKeys = "Enter",
        },
    ];

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
