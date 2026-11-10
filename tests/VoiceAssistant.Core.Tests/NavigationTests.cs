using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

/// <summary>Navigation: name cleaning, matching, grammar, and the controller flows from docs/specs/navigation.md.</summary>
public class NavigationTests
{
    private static readonly WindowRef Chrome = new(10, 1, "chrome", "Lofi beats - YouTube - Google Chrome");
    private static readonly WindowRef Edge = new(20, 2, "msedge", "Work items - Boards - Microsoft\u200B Edge");
    private static readonly WindowRef Copilot = new(30, 3, "github", "GitHub Copilot");

    private static ScreenRect R(double x, double y, double w = 100, double h = 30) => new(x, y, w, h);

    // ---- name cleaning (inputs are real accessibility names seen on the development laptop) ----

    [Theory]
    [InlineData("(53) WhatsApp - Pinned - Memory usage - 120 MB", "WhatsApp")]
    [InlineData("Model Chat - Memory usage - 54.3 MB", "Model Chat")]
    [InlineData("what is fleurs in ai - Google Search - Memory usage - 80 MB", "what is fleurs in ai - Google Search")]
    [InlineData("Lofi beats - YouTube - Google Chrome", "Lofi beats - YouTube")]
    [InlineData("Work items - Boards - Microsoft\u200B Edge", "Work items - Boards")]
    [InlineData("Speech To Text Arena - a Hugging Face Space", "Speech To Text Arena - a Hugging Face Space")]
    public void Tab_titles_lose_counts_status_and_browser_names(string title, string expected) =>
        Assert.Equal(expected, NameMatcher.CleanTabTitle(title));

    [Theory]
    [InlineData("New project, Ctrl + Alt + Shift + N", "New project")]
    [InlineData("Resolving an Unspecified Issue, unread", "Resolving an Unspecified Issue")]
    [InlineData("Session review request. Status: Done.", "Session review request")]
    [InlineData("Pull requests", "Pull requests")]
    public void Element_names_lose_shortcuts_and_states(string name, string expected) =>
        Assert.Equal(expected, NameMatcher.CleanElementName(name));

    [Theory]
    [InlineData("(3) WhatsApp - Google Chrome", "WhatsApp")]
    [InlineData("ChatGPT - Google Chrome", "ChatGPT")]
    [InlineData("Amazon.in: Shoes - Google Chrome", "Amazon")]
    [InlineData("Inbox (3) - me@example.com - Gmail - Google Chrome", "Gmail")]
    [InlineData("weather - Google Search - Google Chrome", "Google")]
    [InlineData("Google - Google Chrome", "Google")]
    [InlineData("Lofi beats - YouTube - Personal - Microsoft\u200B Edge", "YouTube")]
    [InlineData("Microsoft Copilot: Your AI companion - Google Chrome", "Copilot")]
    [InlineData("Some article - Google Chrome", null)]
    [InlineData("Budget - Google Docs - Google Chrome", null)]
    public void Site_profiles_match_the_page_not_the_browser(string title, string? site) =>
        Assert.Equal(site, SiteProfile.ForTitle(title)?.Name);

    [Theory]
    [InlineData("library", "Library", NameMatcher.Exact)]
    [InlineData("pull request", "Pull requests", NameMatcher.Exact - 5)]
    [InlineData("git hub", "GitHub", NameMatcher.Exact - 5)]
    [InlineData("new", "New project", NameMatcher.Strong - 1)]
    [InlineData("libary", "Library", NameMatcher.Fuzzy)]
    [InlineData("issues", "Pull requests", 0)]
    [InlineData("vivash morang", "Bibhash Morang aise bante hai richest", NameMatcher.Fuzzy)]
    [InlineData("bibhash morang", "Bibhash Morang", NameMatcher.Exact)]
    [InlineData("shivam", "Sivam", NameMatcher.Fuzzy)]
    [InlineData("send", "Sent items", 0)]
    [InlineData("settings", "Getting started", 0)]
    [InlineData("delete", "Deleted Items", 0)]
    [InlineData("new", "Now playing", 0)]
    [InlineData("vivash", "Prabhatee", 0)]
    public void Scores(string spoken, string name, int expected) => Assert.Equal(expected, NameMatcher.Score(spoken, name));

    [Theory]
    [InlineData("vivash", "bibas")]
    [InlineData("bibhash", "bibas")]
    [InlineData("phool", "ful")]
    [InlineData("shivam", "sibam")]
    public void Sound_keys_merge_common_recognition_swaps(string word, string key) => Assert.Equal(key, NameMatcher.SoundKey(word));

    // Real WhatsApp Web chat-list names (each row is reported as a row and a name cell).
    [Fact]
    public void Chat_rows_lose_unread_counts_and_times()
    {
        Assert.Equal("Bibhash Morang aise bante hai richest", NameMatcher.CleanElementName("1 unread message Bibhash Morang 11:27 am aise bante hai richest"));
        Assert.Equal("Family", NameMatcher.CleanElementName("10 unread messages Family 11:25 am"));
        Assert.Equal("Standup at 10 am", NameMatcher.CleanElementName("Standup at 10 am"));
        Assert.Equal("9:30 AM", NameMatcher.CleanElementName("9:30 AM"));
        Assert.Equal("7:00 AM, Good morning", NameMatcher.CleanElementName("7:00 AM, Good morning"));
    }

    [Theory]
    [InlineData("vivash morang")]
    [InlineData("bibhash morang")]
    [InlineData("bibhash")]
    public void A_chat_row_and_its_name_cell_resolve_to_one_target(string spoken)
    {
        UiElement[] rows =
        [
            new(1, "1 unread message Bibhash Morang 11:27 am aise bante hai richest", ElementRole.ListItem, R(80, 540, 570, 90)),
            new(2, "1 unread message Bibhash Morang 11:27 am aise bante hai richest", ElementRole.ListItem, R(80, 540, 570, 90)),
            new(3, "1 unread message Bibhash Morang 11:27 am", ElementRole.ListItem, R(160, 550, 480, 30)),
            new(4, "10 unread messages Family 11:25 am Papa: https://www.facebook.com/share", ElementRole.ListItem, R(80, 632, 570, 90)),
            new(5, "Prabhatee 11:19 am Sticker", ElementRole.ListItem, R(80, 816, 570, 90)),
        ];
        var result = TargetResolver.ResolveElement(rows, new ElementTarget(spoken), new ScreenRect(0, 0, 2000, 1180));
        Assert.Equal(3, Assert.IsType<Resolution<UiElement>.Single>(result).Item.Id);
    }

    // ---- resolving elements ----

    private static readonly UiElement[] CopilotSidebar =
    [
        new(1, "New", ElementRole.ListItem, R(15, 101, 300)),
        new(2, "New", ElementRole.Button, R(15, 101, 300)),
        new(3, "Pull requests", ElementRole.ListItem, R(15, 161, 300)),
        new(4, "Pull requests", ElementRole.Button, R(15, 161, 300)),
        new(5, "Issues", ElementRole.Button, R(15, 221, 300)),
        new(6, "New project, Ctrl + Alt + Shift + N", ElementRole.Button, R(336, 491, 30)),
        new(7, "New chat", ElementRole.Button, R(1500, 693, 80)),
        new(8, "New", ElementRole.Button, R(1500, 120, 60)),
    ];

    private static readonly ScreenRect CopilotWindow = new(0, 0, 1800, 1200);

    [Fact]
    public void Duplicate_list_item_and_button_collapse_to_the_button()
    {
        var kept = TargetResolver.Deduplicate(CopilotSidebar);
        Assert.Single(kept, e => e.Name == "Pull requests");
        Assert.Equal(ElementRole.Button, kept.Single(e => e.Name == "Pull requests").Role);
    }

    [Fact]
    public void Two_news_are_ambiguous_until_left_narrows_it()
    {
        Assert.IsType<Resolution<UiElement>.Ambiguous>(TargetResolver.ResolveElement(CopilotSidebar, new ElementTarget("new"), CopilotWindow));
        var left = TargetResolver.ResolveElement(CopilotSidebar, new ElementTarget("new", ScreenRegion.Left), CopilotWindow);
        Assert.Equal(2, Assert.IsType<Resolution<UiElement>.Single>(left).Item.Id);
    }

    [Fact]
    public void Exact_name_beats_prefix_matches()
    {
        var result = TargetResolver.ResolveElement(CopilotSidebar, new ElementTarget("issues"), CopilotWindow);
        Assert.Equal(5, Assert.IsType<Resolution<UiElement>.Single>(result).Item.Id);
    }

    [Fact]
    public void Ordinal_picks_in_reading_order()
    {
        var second = TargetResolver.ResolveElement(CopilotSidebar, new ElementTarget("new", Ordinal: 2), CopilotWindow);
        Assert.Equal(8, Assert.IsType<Resolution<UiElement>.Single>(second).Item.Id);
    }

    [Fact]
    public void Unknown_name_resolves_to_none() =>
        Assert.IsType<Resolution<UiElement>.None>(TargetResolver.ResolveElement(CopilotSidebar, new ElementTarget("settings"), CopilotWindow));

    [Theory]
    [InlineData("Delete repository", true)]
    [InlineData("Merge pull request", true)]
    [InlineData("Sign out", true)]
    [InlineData("Discard changes", true)]
    [InlineData("Pull requests", false)]
    [InlineData("Display settings", false)]
    [InlineData("Library", false)]
    public void Destructive_names_need_confirmation(string name, bool expected) => Assert.Equal(expected, TargetResolver.IsDestructive(name));

    // ---- resolving tabs ----

    private static readonly BrowserTab[] Tabs =
    [
        new(101, "(53) WhatsApp - Pinned - Memory usage - 120 MB", 1, false, Chrome, R(0, 0)),
        new(102, "Lofi beats - YouTube", 2, true, Chrome, R(100, 0)),
        new(103, "Inbox (3) - Gmail", 3, false, Chrome, R(200, 0)),
        new(201, "Work items - Boards", 1, true, Edge, R(0, 0)),
        new(202, "YouTube Music", 2, false, Edge, R(100, 0)),
    ];

    [Fact]
    public void First_and_last_tab_mean_the_front_browser_window()
    {
        Assert.Equal(101, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget(string.Empty, Ordinal: 1))).Item.Id);
        Assert.Equal(103, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget(string.Empty, Ordinal: -1))).Item.Id);
        Assert.IsType<Resolution<BrowserTab>.None>(TargetResolver.ResolveTab(Tabs, new ElementTarget(string.Empty, Ordinal: 7)));
    }

    [Fact]
    public void Named_tab_matches_a_title_segment_across_windows()
    {
        Assert.Equal(103, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget("gmail"))).Item.Id);
        Assert.Equal(101, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget("whatsapp"))).Item.Id);
        Assert.Equal(201, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget("boards"))).Item.Id);
    }

    [Fact]
    public void Two_youtube_tabs_prefer_the_exact_segment()
    {
        // "Lofi beats - YouTube" has a segment exactly "YouTube"; "YouTube Music" only starts with it.
        Assert.Equal(102, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(Tabs, new ElementTarget("youtube"))).Item.Id);
    }

    [Fact]
    public void Same_site_in_two_windows_prefers_the_front_window()
    {
        BrowserTab[] tabs =
        [
            new(1, "Song A - YouTube", 1, true, Chrome, R(0, 0)),
            new(2, "Song B - YouTube", 1, true, Edge, R(0, 0)),
        ];
        Assert.Equal(1, Assert.IsType<Resolution<BrowserTab>.Single>(TargetResolver.ResolveTab(tabs, new ElementTarget("youtube"))).Item.Id);
        BrowserTab[] sameWindow = [tabs[0], tabs[1] with { Window = Chrome, Index = 2 }];
        Assert.IsType<Resolution<BrowserTab>.Ambiguous>(TargetResolver.ResolveTab(sameWindow, new ElementTarget("youtube")));
    }

    // ---- grammar ----

    private static VoiceCommand One(string phrase)
    {
        var parsed = new CommandParser().Parse(phrase, InteractionMode.Command);
        return Assert.Single(parsed.Commands);
    }

    [Theory]
    [InlineData("Go to the first tab.", "", 1)]
    [InlineData("first tab", "", 1)]
    [InlineData("Switch to the second tab", "", 2)]
    [InlineData("go to tab 3", "", 3)]
    [InlineData("tab number four", "", 4)]
    [InlineData("go to the last tab", "", -1)]
    [InlineData("Go to the YouTube tab", "youtube", null)]
    [InlineData("go to the tab where YouTube is open", "youtube", null)]
    [InlineData("switch to the tab with Gmail", "gmail", null)]
    [InlineData("open the WhatsApp tab", "whatsapp", null)]
    public void Tab_phrases(string phrase, string name, int? ordinal)
    {
        var command = One(phrase);
        Assert.Equal(CommandKind.SelectTab, command.Kind);
        Assert.Equal(name, command.Target!.Name);
        Assert.Equal(ordinal, command.Target.Ordinal);
    }

    [Theory]
    [InlineData("new tab", "Ctrl+T")]
    [InlineData("next tab", "Ctrl+Tab")]
    [InlineData("close tab", "Ctrl+W")]
    public void Tab_shortcuts_still_press_keys(string phrase, string chord)
    {
        var command = One(phrase);
        Assert.Equal(CommandKind.PressKeys, command.Kind);
        Assert.Equal(chord, command.Argument);
    }

    [Theory]
    [InlineData("Click on New on the left side.", "new", ScreenRegion.Left, null)]
    [InlineData("click Library", "library", null, null)]
    [InlineData("Select Library", "library", null, null)]
    [InlineData("press the Issues button", "issues", null, null)]
    [InlineData("click the second session", "session", null, 2)]
    [InlineData("tap pull requests in the sidebar", "pull requests", ScreenRegion.Left, null)]
    [InlineData("expand sessions", "sessions", null, null)]
    public void Click_phrases(string phrase, string name, ScreenRegion? region, int? ordinal)
    {
        var command = One(phrase);
        Assert.Equal(CommandKind.Click, command.Kind);
        Assert.Equal(name, command.Target!.Name);
        Assert.Equal(region, command.Target.Region);
        Assert.Equal(ordinal, command.Target.Ordinal);
    }

    [Theory]
    [InlineData("select all", CommandKind.PressKeys)]
    [InlineData("click the search box", CommandKind.FocusSearch)]
    [InlineData("press enter", CommandKind.PressKeys)]
    [InlineData("show numbers", CommandKind.ShowNumbers)]
    [InlineData("hide numbers", CommandKind.HideNumbers)]
    [InlineData("yes", CommandKind.Confirm)]
    [InlineData("cancel", CommandKind.Cancel)]
    public void Existing_and_utility_phrases(string phrase, CommandKind kind) => Assert.Equal(kind, One(phrase).Kind);

    [Theory]
    [InlineData("3", 3)]
    [InlineData("Seven.", 7)]
    [InlineData("click 12", 12)]
    [InlineData("number two", 2)]
    [InlineData("choose four", 4)]
    [InlineData("the second one", 2)]
    public void Number_choices(string phrase, int number)
    {
        var command = One(phrase);
        Assert.Equal(CommandKind.Choose, command.Kind);
        Assert.Equal(number.ToString(System.Globalization.CultureInfo.InvariantCulture), command.Argument);
    }

    [Fact]
    public void Start_typing_in_an_app_or_site_switches_then_types()
    {
        var parsed = new CommandParser().Parse("Start typing in YouTube", InteractionMode.Command);
        Assert.Equal([CommandKind.SwitchToApp, CommandKind.StartTyping], parsed.Commands.Select(c => c.Kind));
        Assert.Equal("youtube", parsed.Commands[0].Argument);
    }

    [Fact]
    public void Start_typing_in_a_tab_selects_it_first()
    {
        var parsed = new CommandParser().Parse("start typing in the first tab", InteractionMode.Command);
        Assert.Equal([CommandKind.SelectTab, CommandKind.StartTyping], parsed.Commands.Select(c => c.Kind));
    }

    [Theory]
    [InlineData("start typing in the message box", CommandKind.FocusField, "message")]
    [InlineData("type in the comment box", CommandKind.FocusField, "comment")]
    [InlineData("start typing in the search box", CommandKind.FocusSearch, null)]
    [InlineData("start typing in the address bar", CommandKind.FocusAddressBar, null)]
    [InlineData("start typing in the text box", CommandKind.StartTyping, null)]
    public void Start_typing_in_a_field(string phrase, CommandKind kind, string? field)
    {
        var command = One(phrase);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(field, command.Target?.Name);
    }

    [Fact]
    public void Tab_or_click_can_chain_start_typing()
    {
        var parsed = new CommandParser().Parse("go to the YouTube tab and start typing", InteractionMode.Command);
        Assert.Equal([CommandKind.SelectTab, CommandKind.StartTyping], parsed.Commands.Select(c => c.Kind));
    }

    [Fact]
    public void Open_app_and_start_typing_is_unchanged()
    {
        var parsed = new CommandParser().Parse("open notepad and start typing", InteractionMode.Command);
        Assert.Equal([CommandKind.OpenApp, CommandKind.StartTyping], parsed.Commands.Select(c => c.Kind));
    }

    [Fact]
    public void While_typing_navigation_needs_the_wake_word()
    {
        var parser = new CommandParser();
        Assert.NotNull(parser.Parse("go to the first tab", InteractionMode.Dictation).DictationText);
        Assert.Equal(CommandKind.SelectTab, Assert.Single(parser.Parse("Hey Jarvis, go to the first tab", InteractionMode.Dictation).Commands).Kind);
        Assert.NotNull(parser.Parse("click New", InteractionMode.Dictation).DictationText);
        Assert.NotNull(parser.Parse("two", InteractionMode.Dictation).DictationText);
    }

    // ---- controller flows ----

    private sealed class NavDesktop : IDesktop
    {
        public WindowRef? Foreground { get; set; }

        public List<string> Actions { get; } = [];

        public List<BrowserTab> Tabs { get; } = [];

        public Dictionary<nint, List<UiElement>> Clickable { get; } = [];

        public Dictionary<nint, List<UiElement>> Boxes { get; } = [];

        public Dictionary<string, WindowRef> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public WindowRef? GetForegroundWindow() => Foreground;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken)
        {
            Actions.Add($"open:{app.Name}");
            Foreground = Apps[app.Name];
            return Task.FromResult(DesktopResult.Ok("ok", Foreground));
        }

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken)
        {
            Actions.Add($"prepare:{window.ProcessName}");
            return Task.FromResult(new TextTargetResult(true, string.Empty, false, FocusIsEditable: false));
        }

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(true, string.Empty));

        public FieldCheck CheckFocusedField() => FieldCheck.Safe;

        public bool? FocusedTextEndsWithNonWhitespace() => false;

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");

        public void Backspace(int count) => Actions.Add($"backspace:{count}");

        public ScreenRect? GetWindowRect(WindowRef window) => new(0, 0, 1800, 1200);

        /// <summary>Scans that return nothing before the page's elements appear (a just-activated browser tab).</summary>
        public int EmptyScansBeforePage { get; set; }

        public int ClickableScans { get; private set; }

        public Task<IReadOnlyList<UiElement>> ListClickableAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UiElement>>(++ClickableScans <= EmptyScansBeforePage ? [] : Clickable.GetValueOrDefault(window.Handle) ?? []);

        public Task<IReadOnlyList<UiElement>> ListTextBoxesAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UiElement>>(Boxes.GetValueOrDefault(window.Handle) ?? []);

        public Task<IReadOnlyList<BrowserTab>> ListTabsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowserTab>>(Tabs);

        public Task<DesktopResult> SelectTabAsync(BrowserTab tab, CancellationToken cancellationToken)
        {
            Actions.Add($"tab:{tab.Id}");
            Foreground = tab.Window with { Title = NameMatcher.CleanTabTitle(tab.Title) + " - Google Chrome" };
            return Task.FromResult(DesktopResult.Ok("ok", Foreground));
        }

        public Task<DesktopResult> ClickAsync(UiElement element, CancellationToken cancellationToken)
        {
            Actions.Add($"click:{element.Id}");
            return Task.FromResult(DesktopResult.Ok("ok"));
        }

        public Task<TextTargetResult> FocusTextBoxAsync(UiElement box, CancellationToken cancellationToken)
        {
            Actions.Add($"focus:{box.Id}");
            return Task.FromResult(new TextTargetResult(true, string.Empty, false, true));
        }
    }

    private static readonly AppEntry ChromeApp = new()
    {
        Name = "Google Chrome",
        Aliases = ["chrome"],
        Launch = "chrome.exe",
        ProcessNames = ["chrome"],
        SearchKeys = "Ctrl+L",
        AddressBarKeys = "Ctrl+L",
    };

    private static readonly AppEntry CopilotApp = new()
    {
        Name = "GitHub Copilot",
        Launch = "copilot",
        ProcessNames = ["github"],
        WindowTitleContains = "GitHub Copilot",
    };

    private readonly NavDesktop _desktop = new();
    private readonly List<StatusUpdate> _statuses = [];
    private readonly List<IReadOnlyList<ChoiceBadge>?> _badges = [];
    private readonly InteractionController _controller;
    private DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public NavigationTests()
    {
        _desktop.Apps["Google Chrome"] = Chrome;
        _desktop.Apps["GitHub Copilot"] = Copilot;
        _desktop.Tabs.AddRange(Tabs);
        _desktop.Clickable[Copilot.Handle] = [.. CopilotSidebar];
        _desktop.Boxes[Copilot.Handle] = [new UiElement(900, "Message", ElementRole.TextBox, R(400, 1100, 1000, 60))];
        _desktop.Boxes[Chrome.Handle] =
        [
            new UiElement(910, "Address and search bar", ElementRole.TextBox, R(200, 50, 1200, 30)),
            new UiElement(911, "Search", ElementRole.TextBox, R(500, 120, 600, 40)),
        ];
        _controller = new InteractionController(
            _desktop,
            new AppCatalog([ChromeApp, CopilotApp]),
            new CommandParser(),
            new ControllerOptions { WakeWordEnabled = false, Clock = () => _now });
        _controller.Status += _statuses.Add;
        _controller.ChoicesChanged += _badges.Add;
        _controller.SetListening(true);
        _desktop.Foreground = Copilot;
    }

    private Task Say(string text) => _controller.HandleTranscriptAsync(text, 0.9f);

    [Fact]
    public async Task Click_in_a_just_activated_browser_tab_waits_for_the_page()
    {
        _desktop.Foreground = Chrome;
        _desktop.Clickable[Chrome.Handle] = [new UiElement(500, "Library", ElementRole.Button, R(20, 300))];
        _desktop.EmptyScansBeforePage = 2;
        await Say("Click Library.");
        Assert.Equal(["click:500"], _desktop.Actions);
        Assert.Equal(3, _desktop.ClickableScans);
    }

    [Fact]
    public async Task Click_outside_browsers_does_not_rescan()
    {
        await Say("Click Settings.");
        Assert.Empty(_desktop.Actions);
        Assert.Equal(1, _desktop.ClickableScans);
    }

    [Fact]
    public async Task Open_chrome_first_tab_then_start_typing()
    {
        await Say("Open Chrome.");
        await Say("Go to the first tab.");
        Assert.Equal(["open:Google Chrome", "tab:101"], _desktop.Actions);
        Assert.Equal("Google Chrome › WhatsApp", _controller.Place);
        Assert.Equal("Google Chrome › WhatsApp", _statuses[^1].Place);
    }

    [Fact]
    public async Task YouTube_tab_then_its_search_box()
    {
        await Say("Go to the tab where YouTube is open.");
        await Say("Start typing.");
        Assert.Equal(["tab:102", "prepare:chrome", "focus:911"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        Assert.Equal("Google Chrome › Lofi beats - YouTube › Search", _controller.Place);
        await Say("lofi music");
        Assert.Contains("type:Lofi music", _desktop.Actions);
    }

    [Fact]
    public async Task Start_typing_in_youtube_does_both()
    {
        await Say("Start typing in YouTube.");
        Assert.Equal(["tab:102", "prepare:chrome", "focus:911"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Copilot_switch_click_new_on_the_left_then_type()
    {
        _desktop.Foreground = Chrome;
        await Say("Switch to GitHub Copilot.");
        await Say("Click on New on the left.");
        await Say("Start typing.");
        Assert.Equal(["open:GitHub Copilot", "click:2", "prepare:github", "focus:900"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        Assert.Same(Copilot, _controller.Target);
    }

    [Fact]
    public async Task Browser_page_with_two_unnamed_boxes_asks_which()
    {
        var mail = Chrome with { Handle = 11, Title = "Lab Inbox - Mail - Google Chrome" };
        _desktop.Foreground = mail;
        _desktop.Boxes[mail.Handle] =
        [
            new UiElement(920, "Address and search bar", ElementRole.TextBox, R(200, 50, 1200, 30)),
            new UiElement(921, "To", ElementRole.TextBox, R(100, 200, 400, 30)),
            new UiElement(922, "Subject", ElementRole.TextBox, R(100, 250, 400, 30)),
        ];
        await Say("Start typing.");
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<ChoiceBadge>>(_badges[^1]).Count);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("Two.");
        Assert.Equal(["prepare:chrome", "focus:922"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Browser_page_without_boxes_uses_the_address_bar()
    {
        var article = Chrome with { Handle = 12, Title = "Some article - Google Chrome" };
        _desktop.Foreground = article;
        _desktop.Boxes[article.Handle] = [new UiElement(930, "Address and search bar", ElementRole.TextBox, R(200, 50, 1200, 30))];
        await Say("Start typing.");
        Assert.Equal(["prepare:chrome", "keys:Ctrl+L"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Ambiguous_click_shows_numbers_and_a_number_clicks()
    {
        await Say("Click New.");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("click:", StringComparison.Ordinal));
        var badges = Assert.IsAssignableFrom<IReadOnlyList<ChoiceBadge>>(_badges[^1]);
        Assert.Equal(2, badges.Count);
        await Say("Two.");
        Assert.Equal(["click:8"], _desktop.Actions);
        Assert.Null(_badges[^1]);
    }

    [Fact]
    public async Task Show_numbers_then_pick_a_text_box_starts_typing()
    {
        await Say("Show numbers.");
        var badges = Assert.IsAssignableFrom<IReadOnlyList<ChoiceBadge>>(_badges[^1]);
        var message = badges.Single(b => b.Label == "Message");
        await Say(message.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(["focus:900"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Choices_expire_and_other_commands_clear_them()
    {
        await Say("Click New.");
        _now = _now.AddSeconds(31);
        _controller.Tick();
        Assert.Null(_badges[^1]);
        await Say("Two.");
        Assert.Empty(_desktop.Actions);
        Assert.Contains("nothing to choose", _statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Destructive_click_waits_for_yes()
    {
        _desktop.Clickable[Copilot.Handle].Add(new UiElement(50, "Delete session", ElementRole.Button, R(900, 400)));
        await Say("Click delete session.");
        Assert.Empty(_desktop.Actions);
        Assert.Contains("say “yes”", _statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        await Say("Yes.");
        Assert.Equal(["click:50"], _desktop.Actions);
    }

    [Fact]
    public async Task Destructive_click_is_dropped_on_no_or_another_command()
    {
        _desktop.Clickable[Copilot.Handle].Add(new UiElement(50, "Delete session", ElementRole.Button, R(900, 400)));
        await Say("Click delete session.");
        await Say("No.");
        await Say("Yes.");
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public async Task Click_refuses_when_focus_moved_before_confirmation()
    {
        _desktop.Clickable[Copilot.Handle].Add(new UiElement(50, "Delete session", ElementRole.Button, R(900, 400)));
        await Say("Click delete session.");
        _desktop.Foreground = Chrome;
        await Say("Yes.");
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public async Task Go_to_a_button_name_clicks_it_when_no_app_or_tab_matches()
    {
        await Say("Go to pull requests.");
        Assert.Equal(["click:4"], _desktop.Actions);
    }

    [Fact]
    public async Task Unknown_go_to_reports_what_was_tried()
    {
        await Say("Go to settings.");
        Assert.Empty(_desktop.Actions);
        Assert.Contains("app, tab or button", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wake_word_navigation_while_typing_stops_typing_first()
    {
        await Say("Start typing.");
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        _desktop.Actions.Clear();
        await _controller.HandleTranscriptAsync("Hey Jarvis, go to the first tab", 0.9f);
        Assert.Equal(["tab:101"], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("this should not be typed");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Hey Jarvis, next tab", "keys:Ctrl+Tab")]
    [InlineData("Hey Jarvis, go back", "keys:Alt+Left")]
    [InlineData("Hey Jarvis, close tab", "keys:Ctrl+W")]
    public async Task Wake_word_shortcuts_while_typing_stop_typing(string phrase, string action)
    {
        await Say("Start typing.");
        _desktop.Actions.Clear();
        await _controller.HandleTranscriptAsync(phrase, 0.9f);
        Assert.Equal([action], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("send");
        Assert.DoesNotContain("keys:Enter", _desktop.Actions);
    }

    [Fact]
    public async Task Press_key_while_typing_keeps_typing()
    {
        await Say("Start typing.");
        await Say("press tab");
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Choices_offered_while_typing_leave_typing_so_numbers_are_not_typed()
    {
        _desktop.Boxes[Copilot.Handle].Add(new UiElement(901, "Message", ElementRole.TextBox, R(400, 100, 600, 40)));
        await Say("Start typing.");
        _desktop.Actions.Clear();
        await _controller.HandleTranscriptAsync("Hey Jarvis, start typing in the message box", 0.9f);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("Two.");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
        Assert.Single(_desktop.Actions, a => a.StartsWith("focus:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unrelated_speech_drops_a_pending_confirmation()
    {
        _desktop.Clickable[Copilot.Handle].Add(new UiElement(50, "Delete session", ElementRole.Button, R(900, 400)));
        await Say("Click delete session.");
        await Say("What a nice day it is outside.");
        await Say("Yeah.");
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public async Task Everyday_words_in_standby_stay_silent()
    {
        var controller = new InteractionController(_desktop, new AppCatalog([ChromeApp, CopilotApp]), new CommandParser());
        var statuses = new List<StatusUpdate>();
        controller.SetListening(true);
        controller.Status += statuses.Add;
        foreach (var word in new[] { "Yes.", "No.", "Two.", "Yeah." })
        {
            await controller.HandleTranscriptAsync(word, 0.9f);
        }

        Assert.Empty(statuses);
        await controller.HandleTranscriptAsync("Go to the first tab.", 0.9f);
        Assert.Contains("first", Assert.Single(statuses).Message, StringComparison.Ordinal);
    }
}
