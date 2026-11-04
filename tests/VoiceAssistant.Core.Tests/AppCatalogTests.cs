using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class AppCatalogTests
{
    private static readonly AppEntry GitHubCopilot = new()
    {
        Name = "GitHub Copilot",
        Launch = @"shell:AppsFolder\com.github.githubapp",
        ProcessNames = ["github"],
        WindowTitleContains = "GitHub Copilot",
        TextArea = "Bottom",
    };

    private static AppCatalog Catalog()
    {
        var catalog = new AppCatalog([GitHubCopilot]);
        catalog.SetDiscoveredApps(
        [
            new AppEntry { Name = "Copilot", Launch = @"shell:AppsFolder\Microsoft.Copilot" },
            new AppEntry { Name = "Microsoft Teams", Launch = @"shell:AppsFolder\MSTeams" },
            new AppEntry { Name = "Outlook (new)", Launch = @"shell:AppsFolder\Outlook" },
            new AppEntry { Name = "Microsoft Edge", Launch = @"shell:AppsFolder\MSEdge" },
            new AppEntry { Name = "Edge", Launch = @"shell:AppsFolder\OtherEdge" },
        ]);
        return catalog;
    }

    [Theory]
    [InlineData("github copilot")]
    [InlineData("git hub copilot")]
    [InlineData("github co pilot")]
    public void Spoken_variants_resolve_to_configured_app(string spoken)
    {
        var found = Assert.IsType<AppResolution.Found>(Catalog().Resolve(spoken));
        Assert.Same(GitHubCopilot, found.App);
    }

    [Fact]
    public void Plain_copilot_is_the_start_menu_app_named_copilot_not_github()
    {
        var found = Assert.IsType<AppResolution.Found>(Catalog().Resolve("copilot"));
        Assert.Equal("Copilot", found.App.Name);
    }

    [Fact]
    public void Vendor_prefix_can_be_omitted_when_unique()
    {
        Assert.Equal("Microsoft Teams", Assert.IsType<AppResolution.Found>(Catalog().Resolve("teams")).App.Name);
    }

    [Fact]
    public void Exact_name_beats_vendor_stripped_match()
    {
        Assert.Equal("Edge", Assert.IsType<AppResolution.Found>(Catalog().Resolve("edge")).App.Name);
    }

    [Fact]
    public void Unknown_app_is_not_found()
    {
        Assert.IsType<AppResolution.NotFound>(Catalog().Resolve("photoshop"));
    }

    [Fact]
    public void Duplicate_names_are_ambiguous()
    {
        var catalog = new AppCatalog([]);
        catalog.SetDiscoveredApps(
        [
            new AppEntry { Name = "Terminal", Launch = "a" },
            new AppEntry { Name = "Terminal", Launch = "b" },
        ]);
        Assert.Equal(2, Assert.IsType<AppResolution.Ambiguous>(catalog.Resolve("terminal")).Names.Count);
    }

    [Fact]
    public void Profile_requires_process_and_title_match()
    {
        var catalog = Catalog();
        Assert.Same(GitHubCopilot, catalog.FindProfileForProcess("GitHub", "GitHub Copilot"));
        Assert.Null(catalog.FindProfileForProcess("github", "GitHub Desktop"));
        Assert.Null(catalog.FindProfileForProcess("notepad", "GitHub Copilot"));
    }
}
