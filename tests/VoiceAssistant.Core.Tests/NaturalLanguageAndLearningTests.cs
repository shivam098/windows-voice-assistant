using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class NaturalLanguageAndLearningTests
{
    private static readonly WindowRef Chrome = new(400, 4, "chrome", "Google - Google Chrome");
    private static readonly WindowRef NotepadWindow = new(300, 3, "Notepad", "Untitled - Notepad");

    private static readonly AppEntry ChromeApp = new()
    {
        Name = "Google Chrome",
        Aliases = ["chrome"],
        Launch = "chrome.exe",
        ProcessNames = ["chrome"],
        SearchKeys = "Ctrl+L",
        AddressBarKeys = "Ctrl+L",
    };

    private static readonly AppEntry NotepadApp = new()
    {
        Name = "Notepad",
        Launch = "notepad.exe",
        ProcessNames = ["Notepad"],
        SubmitKeys = string.Empty,
        NewLineKeys = "Enter",
        TextArea = "Largest",
        NewWindowKeys = "Ctrl+Shift+N",
    };

    private readonly CommandParser _parser = new();

    [Theory]
    [InlineData("Can you open Notepad?", "notepad")]
    [InlineData("Could you please open Chrome for me.", "chrome")]
    [InlineData("Can you please open up a new notepad window", "notepad")]
    [InlineData("I want to open notepad", "notepad")]
    [InlineData("Fire up Chrome.", "chrome")]
    [InlineData("Launch the Chrome browser please", "chrome")]
    [InlineData("Okay so just open notepad now", "notepad")]
    [InlineData("Start Notepad.", "notepad")]
    public void Polite_and_varied_open_phrasing(string heard, string app)
    {
        var command = Assert.Single(_parser.Parse(heard, InteractionMode.Command).Commands);
        Assert.Equal(new VoiceCommand(CommandKind.OpenApp, app), command);
    }

    [Theory]
    [InlineData("Switch to Notepad.")]
    [InlineData("Can you go to Notepad?")]
    [InlineData("Go back to notepad")]
    [InlineData("Show me notepad")]
    public void Switching_to_an_open_app(string heard)
    {
        Assert.Equal(new VoiceCommand(CommandKind.SwitchToApp, "notepad"), Assert.Single(_parser.Parse(heard, InteractionMode.Command).Commands));
    }

    [Theory]
    [InlineData("Can you start typing?", CommandKind.StartTyping)]
    [InlineData("Let me type.", CommandKind.StartTyping)]
    [InlineData("I want to start typing", CommandKind.StartTyping)]
    [InlineData("Could you go to the search box?", CommandKind.FocusSearch)]
    [InlineData("Click on the search bar", CommandKind.FocusSearch)]
    [InlineData("Go to the address bar please", CommandKind.FocusAddressBar)]
    [InlineData("Could you save this?", CommandKind.PressKeys)]
    [InlineData("Press the enter key.", CommandKind.PressKeys)]
    [InlineData("Can you refresh the page", CommandKind.PressKeys)]
    public void Polite_commands(string heard, CommandKind kind)
    {
        Assert.Equal(kind, _parser.Parse(heard, InteractionMode.Command).Commands[0].Kind);
    }

    [Fact]
    public void Open_and_start_typing_variants()
    {
        var parsed = _parser.Parse("Can you open notepad so I can type?", InteractionMode.Command);
        Assert.Equal([new VoiceCommand(CommandKind.OpenApp, "notepad"), new VoiceCommand(CommandKind.StartTyping)], parsed.Commands);
    }

    [Fact]
    public void Search_for_strips_on_google()
    {
        var command = Assert.Single(_parser.Parse("Please search for weather in Bangalore on Google.", InteractionMode.Command).Commands);
        Assert.Equal(new VoiceCommand(CommandKind.SearchFor, "weather in bangalore"), command);
    }

    [Theory]
    [InlineData("Please send it to Sam.")]
    [InlineData("Can you stop typing")]
    [InlineData("Could you send it")]
    [InlineData("That's all.")]
    [InlineData("I'm done.")]
    [InlineData("Post it.")]
    [InlineData("Send that.")]
    [InlineData("Delete everything.")]
    [InlineData("Be quiet.")]
    [InlineData("Erase that.")]
    public void Dictation_stays_strict_without_the_wake_word(string heard)
    {
        var parsed = _parser.Parse(heard, InteractionMode.Dictation);
        Assert.Empty(parsed.Commands);
        Assert.NotNull(parsed.DictationText);
    }

    [Fact]
    public void Broader_phrasings_work_after_the_wake_word_while_typing()
    {
        Assert.Equal(CommandKind.StopTyping, Assert.Single(_parser.Parse("Hey Jarvis, that's all.", InteractionMode.Dictation).Commands).Kind);
        Assert.Equal(CommandKind.Send, Assert.Single(_parser.Parse("Hey Jarvis, post it.", InteractionMode.Dictation).Commands).Kind);
    }

    [Fact]
    public void A_phrase_taught_via_another_learned_phrase_maps_to_the_real_command()
    {
        var memory = new PhraseMemory();
        memory.Learn("get notepad going", "open notepad");
        var parser = new CommandParser(memory: memory);
        var parsed = parser.Parse("Get notepad going.", InteractionMode.Command);
        Assert.True(parsed.FromLearnedPhrase);
        Assert.Equal("open notepad", parsed.CommandText);
    }

    [Fact]
    public void Full_memory_evicts_the_oldest_phrase()
    {
        var memory = new PhraseMemory();
        for (var i = 0; i < PhraseMemory.MaxPhrases + 2; i++)
        {
            Assert.True(memory.Learn($"notepad thing {i}", "open notepad"));
        }

        Assert.Equal(PhraseMemory.MaxPhrases, memory.PhraseCount);
        Assert.False(memory.TryGet("notepad thing 0", out _));
        Assert.False(memory.TryGet("notepad thing 1", out _));
        Assert.True(memory.TryGet("notepad thing 2", out _));
        Assert.True(memory.TryGet($"notepad thing {PhraseMemory.MaxPhrases}", out _));
        Assert.True(memory.TryGet($"notepad thing {PhraseMemory.MaxPhrases + 1}", out _));
    }

    [Fact]
    public void Wake_word_makes_polite_dictation_commands_work()
    {
        Assert.Equal(CommandKind.StopTyping, Assert.Single(_parser.Parse("Hey Jarvis, can you please stop typing?", InteractionMode.Dictation).Commands).Kind);
    }

    [Fact]
    public void Bare_stop_does_not_pause_listening()
    {
        Assert.Empty(_parser.Parse("Stop.", InteractionMode.Command).Commands);
    }

    [Theory]
    [InlineData("notpad", "Notepad")]
    [InlineData("note pad", "Notepad")]
    [InlineData("crome", "Google Chrome")]
    [InlineData("outlook", "Outlook (new)")]
    [InlineData("teams", "Microsoft Teams")]
    [InlineData("visual studio", "Visual Studio 2022")]
    public void App_names_tolerate_mishearing_and_decoration(string spoken, string expected)
    {
        var catalog = new AppCatalog([ChromeApp, NotepadApp]);
        catalog.SetDiscoveredApps(
        [
            new AppEntry { Name = "Outlook (new)", Launch = "olk" },
            new AppEntry { Name = "Microsoft Teams", Launch = "teams" },
            new AppEntry { Name = "Visual Studio 2022", Launch = "vs" },
        ]);
        Assert.Equal(expected, Assert.IsType<AppResolution.Found>(catalog.Resolve(spoken)).App.Name);
    }

    [Fact]
    public void Ambiguous_names_prefer_the_app_used_most()
    {
        var memory = new PhraseMemory();
        var catalog = new AppCatalog([], memory);
        catalog.SetDiscoveredApps([new AppEntry { Name = "Terminal", Launch = "a" }, new AppEntry { Name = "Terminal Preview", Launch = "b" }, new AppEntry { Name = "Terminal", Launch = "c" }]);
        Assert.IsType<AppResolution.Ambiguous>(catalog.Resolve("terminal"));
        memory.RecordAppUse("Terminal");
        Assert.IsType<AppResolution.Ambiguous>(catalog.Resolve("terminal"));
    }

    [Fact]
    public void Memory_only_learns_pairs_that_share_a_meaningful_word()
    {
        var memory = new PhraseMemory();
        Assert.True(memory.Learn("get notepad going", "open notepad"));
        Assert.False(memory.Learn("what time is it", "open notepad"));
        Assert.False(memory.Learn("open it up", "open notepad"));
        Assert.True(memory.TryGet("get notepad going", out var command));
        Assert.Equal("open notepad", command);
    }

    [Fact]
    public async Task Rephrasing_after_an_unknown_phrase_is_learned_and_reused()
    {
        var (controller, desktop, statuses, memory) = Create();
        await controller.HandleTranscriptAsync("Hey Jarvis, get notepad going.", 0.9f);
        Assert.Empty(desktop.Actions);
        await controller.HandleTranscriptAsync("Open notepad.", 0.9f);
        Assert.Contains(statuses, s => s.Message.StartsWith("Got it", StringComparison.Ordinal));
        Assert.Equal(1, memory.PhraseCount);

        desktop.Actions.Clear();
        await controller.HandleTranscriptAsync("Get notepad going.", 0.9f);
        Assert.Equal(["open:Notepad"], desktop.Actions);
    }

    [Fact]
    public async Task Unrelated_phrase_before_a_command_is_not_learned()
    {
        var (controller, _, _, memory) = Create();
        await controller.HandleTranscriptAsync("Hey Jarvis, what time is it?", 0.9f);
        await controller.HandleTranscriptAsync("Open notepad.", 0.9f);
        Assert.Equal(0, memory.PhraseCount);
    }

    [Fact]
    public async Task Successful_opens_are_counted()
    {
        var (controller, _, _, memory) = Create();
        await controller.HandleTranscriptAsync("Hey Jarvis, open notepad.", 0.9f);
        await controller.HandleTranscriptAsync("Switch to notepad.", 0.9f);
        Assert.Equal(2, memory.UsesOf("Notepad"));
    }

    [Fact]
    public async Task Open_asks_for_a_new_window_but_switch_does_not()
    {
        var (controller, desktop, _, _) = Create();
        await controller.HandleTranscriptAsync("Hey Jarvis, can you open notepad?", 0.9f);
        await controller.HandleTranscriptAsync("Switch to notepad.", 0.9f);
        Assert.Equal([true, false], desktop.NewWindowRequests);
    }

    [Fact]
    public async Task Command_without_wake_word_gets_a_hint_instead_of_silence()
    {
        var (controller, desktop, statuses, _) = Create();
        var before = statuses.Count;
        await controller.HandleTranscriptAsync("Can you open notepad?", 0.9f);
        Assert.Empty(desktop.Actions);
        Assert.Equal(before + 1, statuses.Count);
        Assert.Contains("Hey Jarvis", statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_speech_in_standby_stays_silent()
    {
        var (controller, _, statuses, _) = Create();
        var before = statuses.Count;
        await controller.HandleTranscriptAsync("I'll grab lunch at one.", 0.9f);
        Assert.Equal(before, statuses.Count);
    }

    [Fact]
    public async Task Start_typing_in_a_browser_page_goes_to_the_address_bar()
    {
        var (controller, desktop, _, _) = Create();
        desktop.Foreground = Chrome;
        desktop.FocusIsEditable = false;
        await controller.HandleTranscriptAsync("Hey Jarvis, start typing.", 0.9f);
        Assert.Equal(["prepare:chrome", "keys:Ctrl+L"], desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, controller.Mode);
    }

    [Fact]
    public async Task Start_typing_in_a_web_text_box_types_there()
    {
        var (controller, desktop, _, _) = Create();
        desktop.Foreground = Chrome;
        desktop.FocusIsEditable = true;
        await controller.HandleTranscriptAsync("Hey Jarvis, start typing.", 0.9f);
        Assert.Equal(["prepare:chrome"], desktop.Actions);
    }

    [Fact]
    public async Task Command_window_lasts_thirty_seconds_by_default()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var memory = new PhraseMemory();
        var desktop = new LearningDesktop();
        var controller = new InteractionController(desktop, new AppCatalog([ChromeApp, NotepadApp], memory), new CommandParser(memory: memory), new ControllerOptions { Clock = () => now }, memory: memory);
        controller.SetListening(true);
        await controller.HandleTranscriptAsync("Hey Jarvis.", 0.9f);
        now = now.AddSeconds(25);
        await controller.HandleTranscriptAsync("Open notepad.", 0.9f);
        Assert.Equal(["open:Notepad"], desktop.Actions);
    }

    private static (InteractionController, LearningDesktop, List<StatusUpdate>, PhraseMemory) Create()
    {
        var memory = new PhraseMemory();
        var desktop = new LearningDesktop();
        var statuses = new List<StatusUpdate>();
        var controller = new InteractionController(desktop, new AppCatalog([ChromeApp, NotepadApp], memory), new CommandParser(memory: memory), memory: memory);
        controller.Status += statuses.Add;
        controller.SetListening(true);
        return (controller, desktop, statuses, memory);
    }

    private sealed class LearningDesktop : IDesktop
    {
        public List<string> Actions { get; } = [];

        public List<bool> NewWindowRequests { get; } = [];

        public WindowRef? Foreground { get; set; }

        public bool? FocusIsEditable { get; set; } = true;

        public WindowRef? GetForegroundWindow() => Foreground;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken)
        {
            Actions.Add($"open:{app.Name}");
            NewWindowRequests.Add(newWindow);
            Foreground = app.Name == "Notepad" ? NotepadWindow : Chrome;
            return Task.FromResult(DesktopResult.Ok("ok", Foreground));
        }

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken)
        {
            Actions.Add($"prepare:{window.ProcessName}");
            return Task.FromResult(new TextTargetResult(true, string.Empty, null, FocusIsEditable));
        }

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(true, string.Empty));

        public FieldCheck CheckFocusedField() => FieldCheck.Safe;

        public bool? FocusedTextEndsWithNonWhitespace() => null;

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");

        public void Backspace(int count) => Actions.Add($"backspace:{count}");
    }
}
