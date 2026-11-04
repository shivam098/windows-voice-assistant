using VoiceAssistant.Core;

namespace VoiceAssistant.App.Platform;

/// <summary>Enumerates installed Start menu apps (the same list as Get-StartApps) via the shell AppsFolder.</summary>
internal static class StartMenuApps
{
    public static IReadOnlyList<AppEntry> Discover()
    {
        IReadOnlyList<AppEntry> result = [];
        var thread = new Thread(() => result = DiscoverOnSta()) { IsBackground = true, Name = "StartMenuApps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(20)) ? result : [];
    }

    private static IReadOnlyList<AppEntry> DiscoverOnSta()
    {
        var apps = new List<AppEntry>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return apps;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (var item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                {
                    apps.Add(new AppEntry { Name = name, Launch = @"shell:AppsFolder\" + id });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"start menu discovery failed: {ex.GetType().Name}");
        }

        return apps;
    }
}
