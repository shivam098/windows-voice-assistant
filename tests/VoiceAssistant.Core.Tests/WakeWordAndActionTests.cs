using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class WakeWordAndActionTests
{
    private static readonly WindowRef Chrome = new(400, 4, "chrome", "New Tab - Google Chrome");
    private static readonly WindowRef Outlook = new(500, 5, "olk", "Inbox - Outlook");
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
    };

    private readonly ActionDesktop _desktop = new();
    private readonly List<StatusUpdate> _statuses = [];
    private DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly InteractionController _controller;

    public WakeWordAndActionTests()
    {
        _desktop.OpenResults["Google Chrome"] = Chrome;
        _desktop.OpenResults["Notepad"] = NotepadWindow;
        _controller = new InteractionController(
            _desktop,
            new AppCatalog([ChromeApp, NotepadApp]),
            new CommandParser(),
            new ControllerOptions { Clock = () => _now, AwakeTimeout = TimeSpan.FromSeconds(8) });
        _controller.Status += _statuses.Add;
        _controller.SetListening(true);
    }

    private Task Say(string text) => _controller.HandleTranscriptAsync(text, 0.9f);

    [Fact]
    public void Listening_starts_in_standby()
    {
        Assert.Equal(InteractionMode.Standby, _controller.Mode);
        Assert.Contains("Hey Jarvis", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standby_ignores_speech_without_the_wake_word_silently()
    {
        var before = _statuses.Count;
        await Say("Open Notepad.");
        await Say("What's for lunch?");
        Assert.Empty(_desktop.Actions);
        Assert.Equal(before, _statuses.Count);
        Assert.Equal(InteractionMode.Standby, _controller.Mode);
    }

    [Theory]
    [InlineData("Hey Jarvis, open Notepad.")]
    [InlineData("Hey, Jarvis! Open Notepad")]
    [InlineData("Jarvis open notepad")]
    [InlineData("Hi Jarvis, open notepad.")]
    public async Task Wake_word_with_command_runs_it(string heard)
    {
        await Say(heard);
        Assert.Equal(["open:Notepad"], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Wake_word_alone_then_command_without_it()
    {
        await Say("Hey Jarvis.");
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        _now = _now.AddSeconds(5);
        await Say("Open Chrome.");
        Assert.Equal(["open:Google Chrome"], _desktop.Actions);
    }

    [Fact]
    public async Task Command_window_expires_back_to_standby()
    {
        await Say("Hey Jarvis.");
        _now = _now.AddSeconds(9);
        await Say("Open Chrome.");
        Assert.Empty(_desktop.Actions);
        Assert.Equal(InteractionMode.Standby, _controller.Mode);
    }

    [Fact]
    public async Task Each_command_extends_the_window()
    {
        await Say("Hey Jarvis, open Notepad.");
        _now = _now.AddSeconds(6);
        await Say("Start typing.");
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Tick_reports_return_to_standby()
    {
        await Say("Hey Jarvis.");
        _now = _now.AddSeconds(9);
        _controller.Tick();
        Assert.Equal(InteractionMode.Standby, _statuses[^1].Mode);
    }

    [Fact]
    public async Task Dictation_needs_no_wake_word_and_stays_until_stop_typing()
    {
        await Say("Hey Jarvis, open Notepad and start typing.");
        _now = _now.AddMinutes(5);
        await Say("Dear team.");
        await Say("Stop typing.");
        Assert.Equal(["open:Notepad", "prepare:Notepad:Largest", "type:Dear team.", ], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Wake_word_inside_dictation_forces_a_command()
    {
        await Say("Hey Jarvis, open Notepad and start typing.");
        await Say("Hey Jarvis, stop typing.");
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chrome_search_box_flow_types_query_and_searches()
    {
        await Say("Hey Jarvis, open Chrome.");
        await Say("Go to the search box.");
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        await Say("Weather in Bangalore.");
        await Say("Search.");

        Assert.Equal(["open:Google Chrome", "keys:Ctrl+L", "type:Weather in Bangalore", "keys:Enter"], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Search_for_does_everything_in_one_command()
    {
        _desktop.Foreground = Chrome;
        await Say("Hey Jarvis, search for best pizza near me.");
        Assert.Equal(["keys:Ctrl+L", "type:Best pizza near me", "keys:Enter"], _desktop.Actions);
    }

    [Fact]
    public async Task Search_box_in_other_apps_uses_accessibility_lookup()
    {
        _desktop.Foreground = Outlook;
        await Say("Hey Jarvis, go to the search box.");
        Assert.Equal(["searchbox:olk"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Missing_search_box_stays_in_command_mode()
    {
        _desktop.Foreground = Outlook;
        _desktop.SearchBoxFound = false;
        await Say("Hey Jarvis, go to the search box.");
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Address_bar_needs_a_browser()
    {
        _desktop.Foreground = NotepadWindow;
        await Say("Hey Jarvis, go to the address bar.");
        Assert.Empty(_desktop.Actions);
        Assert.Contains("address bar", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("New tab.", "Ctrl+T")]
    [InlineData("Go back.", "Alt+Left")]
    [InlineData("Press enter.", "Enter")]
    [InlineData("Press the tab key.", "Tab")]
    [InlineData("Copy that.", "Ctrl+C")]
    [InlineData("Scroll down.", "PageDown")]
    public async Task Shortcuts_press_keys(string heard, string chord)
    {
        _desktop.Foreground = Chrome;
        await Say("Hey Jarvis, " + heard);
        Assert.Equal([$"keys:{chord}"], _desktop.Actions);
    }

    [Fact]
    public async Task Press_key_while_typing_targets_the_bound_window()
    {
        await Say("Hey Jarvis, open Notepad and start typing.");
        await Say("Press tab.");
        Assert.Equal("keys:Tab", _desktop.Actions[^1]);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Keys_are_refused_for_administrator_windows()
    {
        _desktop.Foreground = Chrome;
        _desktop.Field = FieldCheck.Elevated;
        await Say("Hey Jarvis, new tab.");
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public async Task Notepad_new_line_uses_enter_and_dictation_is_formatted()
    {
        await Say("Hey Jarvis, open Notepad and start typing.");
        await Say("Um, shopping list colon milk new line eggs");
        Assert.Equal(["type:Shopping list: milk", "keys:Enter", "type:Eggs"], _desktop.Actions[^3..]);
    }

    [Fact]
    public async Task Push_to_talk_types_into_whatever_is_focused()
    {
        _desktop.Foreground = Outlook;
        _desktop.Field = FieldCheck.Unknown;
        _desktop.TrailingNonWhitespace = true;
        _controller.InsertDictation("uh thanks for the update new line talk soon");
        Assert.Equal(["type: Thanks for the update", "keys:Shift+Enter", "type:Talk soon"], _desktop.Actions);
        Assert.Equal("Thanks for the update\nTalk soon", _controller.LastTranscript);
    }

    [Fact]
    public void Push_to_talk_refuses_password_fields()
    {
        _desktop.Foreground = Outlook;
        _desktop.Field = FieldCheck.Password;
        _controller.InsertDictation("hunter2");
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public void Paste_last_transcript_retypes_it()
    {
        _desktop.Foreground = Outlook;
        _controller.InsertDictation("Hello there.");
        _desktop.Actions.Clear();
        _controller.RepeatLastTranscript();
        Assert.Equal(["type:Hello there."], _desktop.Actions);
    }

    private sealed class ActionDesktop : IDesktop
    {
        public List<string> Actions { get; } = [];

        public Dictionary<string, WindowRef> OpenResults { get; } = [];

        public WindowRef? Foreground { get; set; }

        public FieldCheck Field { get; set; } = FieldCheck.Safe;

        public bool SearchBoxFound { get; set; } = true;

        public bool? TrailingNonWhitespace { get; set; }

        public WindowRef? GetForegroundWindow() => Foreground;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, CancellationToken cancellationToken)
        {
            Actions.Add($"open:{app.Name}");
            Foreground = OpenResults[app.Name];
            return Task.FromResult(DesktopResult.Ok("ok", Foreground));
        }

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken)
        {
            Actions.Add($"prepare:{window.ProcessName}:{profile?.TextArea ?? "none"}");
            return Task.FromResult(new TextTargetResult(true, string.Empty));
        }

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken)
        {
            Actions.Add($"searchbox:{window.ProcessName}");
            return Task.FromResult(new TextTargetResult(SearchBoxFound, SearchBoxFound ? string.Empty : "no search box"));
        }

        public FieldCheck CheckFocusedField() => Field;

        public bool? FocusedTextEndsWithNonWhitespace() => TrailingNonWhitespace;

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");

        public void Backspace(int count) => Actions.Add($"backspace:{count}");
    }
}
