using System.Text.Json;
using VoiceAssistant.Core;

namespace VoiceAssistant.App;

/// <summary>Persists <see cref="PhraseMemory"/> to %APPDATA%\VoiceAssistant\learned.json (local only).</summary>
internal static class LearnedStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Load(PhraseMemory memory)
    {
        try
        {
            if (!File.Exists(AppPaths.LearnedFile))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(AppPaths.LearnedFile), Options);
            memory.Load(data?.Phrases, data?.AppUses);
            Log.Info($"learned memory loaded: {memory.PhraseCount} phrases");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"learned memory unreadable: {ex.GetType().Name}");
        }
    }

    public static void Save(PhraseMemory memory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LearnedFile)!);
            var data = new Data(new Dictionary<string, string>(memory.PhrasesInOrder()), new Dictionary<string, int>(memory.AppUses));
            var temp = AppPaths.LearnedFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, AppPaths.LearnedFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"learned memory not saved: {ex.GetType().Name}");
        }
    }

    private sealed record Data(Dictionary<string, string>? Phrases, Dictionary<string, int>? AppUses);

    /// <summary>Removes learned data from disk regardless of whether learning is currently enabled.</summary>
    public static void Delete()
    {
        foreach (var path in new[] { AppPaths.LearnedFile, AppPaths.LearnedFile + ".tmp" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"learned memory not deleted: {ex.GetType().Name}");
            }
        }
    }
}
