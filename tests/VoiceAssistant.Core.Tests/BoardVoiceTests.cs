using VoiceAssistant.Board;
using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

/// <summary>Idea board by voice (docs/specs/idea-board.md): commands, push-to-talk routing, and what stays typed.</summary>
public class BoardVoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 30, 0, TimeSpan.FromHours(5.5));
    private static readonly WindowRef Editor = new(10, 1, "Notepad", "Untitled - Notepad");

    private readonly TypingDesktop _desktop = new();
    private readonly BoardStore _board = new(null, () => Now);
    private readonly List<StatusUpdate> _statuses = [];
    private readonly List<BoardList?> _shown = [];
    private readonly InteractionController _controller;

    public BoardVoiceTests()
    {
        _controller = new InteractionController(
            _desktop,
            new AppCatalog([new AppEntry { Name = "Notepad", Launch = "notepad.exe", ProcessNames = ["Notepad"], SubmitKeys = string.Empty, NewLineKeys = "Enter" }]),
            new CommandParser { BoardClock = () => Now },
            new ControllerOptions { WakeWordEnabled = false })
        {
            Board = _board,
        };
        _controller.Status += _statuses.Add;
        _controller.ShowBoardRequested += _shown.Add;
        _controller.SetListening(true);
    }

    private Task Say(string text) => _controller.HandleTranscriptAsync(text, 0.9f);

    [Fact]
    public async Task Add_to_my_ideas_files_a_note()
    {
        await Say("Add to my ideas, a voice game for kids.");
        var item = Assert.Single(_board.Items);
        Assert.Equal(BoardList.Ideas, item.List);
        Assert.Equal("A voice game for kids", item.Text);
        Assert.Contains("Ideas", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remind_me_sets_a_reminder_with_its_time()
    {
        await Say("Remind me to call mom tomorrow at 5 pm.");
        var item = Assert.Single(_board.Items);
        Assert.Equal(BoardList.Reminders, item.List);
        Assert.Equal("Call mom", item.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 17, 0, 0, Now.Offset), item.Due);
        Assert.Contains("tomorrow 5:00 PM", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_my_reminders_asks_the_app_to_open_the_list()
    {
        await Say("Show my reminders.");
        Assert.Equal([BoardList.Reminders], _shown);
    }

    [Fact]
    public async Task Show_my_ideas_is_not_a_browser_tab_switch()
    {
        await Say("Go to my ideas.");
        Assert.Equal([BoardList.Ideas], _shown);
        Assert.Empty(_desktop.Typed);
    }

    [Fact]
    public async Task Mark_done_ticks_the_item_off()
    {
        _board.Add(BoardList.Todo, "Renew passport");
        await Say("Mark renew passport as done.");
        Assert.True(_board.Items.Single().Done);
    }

    [Fact]
    public void Push_to_talk_naming_a_list_goes_to_the_board_not_the_text_box()
    {
        _controller.InsertDictation("Add to my to-do list, renew passport.");
        Assert.Equal("Renew passport", Assert.Single(_board.Items).Text);
        Assert.Empty(_desktop.Typed);
    }

    [Theory]
    [InlineData("Please remind me to send the report.")]
    [InlineData("Note down that the build failed.")]
    public void Push_to_talk_without_a_list_name_is_typed(string spoken)
    {
        _controller.InsertDictation(spoken);
        Assert.Empty(_board.Items);
        Assert.NotEmpty(_desktop.Typed);
    }

    [Fact]
    public async Task While_typing_the_wake_word_files_a_note_and_typing_continues()
    {
        await Say("Start typing.");
        await Say("Hey Jarvis, add to my ideas, offline mode.");
        Assert.Equal("Offline mode", Assert.Single(_board.Items).Text);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        await Say("Add to my ideas, this sentence is just typed.");
        Assert.Single(_board.Items);
        Assert.Contains(_desktop.Typed, t => t.Contains("this sentence is just typed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_a_board_the_command_says_so()
    {
        _controller.Board = null;
        await Say("Add to my ideas, something.");
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    private sealed class TypingDesktop : IDesktop
    {
        public List<string> Typed { get; } = [];

        public WindowRef? GetForegroundWindow() => Editor;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken) =>
            Task.FromResult(DesktopResult.Ok("ok", Editor));

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(true, string.Empty, false, true));

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(false, "no"));

        public FieldCheck CheckFocusedField() => FieldCheck.Safe;

        public bool? FocusedTextEndsWithNonWhitespace() => false;

        public void TypeText(string text) => Typed.Add(text);

        public void PressKeys(string chord)
        {
        }

        public void Backspace(int count)
        {
        }
    }
}
