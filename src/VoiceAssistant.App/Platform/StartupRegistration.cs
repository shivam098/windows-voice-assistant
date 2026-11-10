using Microsoft.Win32;

namespace VoiceAssistant.App.Platform;

/// <summary>
/// “Start when I sign in”: a per-user Run entry that launches the app with <c>--background</c>, so signing in puts it
/// in the tray without opening the window.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "VoiceAssistant";
    public const string BackgroundArgument = "--background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(RunValue, Command);
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    /// <summary>Entries written by older versions start the app without <c>--background</c>; update them once.</summary>
    public static void UpgradeOldEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is string value && !value.Contains(BackgroundArgument, StringComparison.Ordinal))
            {
                key.SetValue(RunValue, Command);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"couldn't update the sign-in entry: {ex.GetType().Name}");
        }
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";
}
