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
    public void Push_to_talk_with_no_window_saves_the_dictation_to_the_board()
    {
        _desktop.Foreground = null;
        _controller.InsertDictation("I have to draft a reply to Tata Neu about the refund.");
        var item = Assert.Single(_board.Items);
        Assert.Equal(BoardList.Todo, item.List);
        Assert.Equal("Draft a reply to Tata Neu about the refund", item.Text);
        Assert.StartsWith("No text box", _statuses[^1].Message, StringComparison.Ordinal);
        Assert.Equal(StatusKind.Success, _statuses[^1].Kind);
    }

    [Theory]
    [InlineData("A voice game where kids guess animal sounds.", BoardList.Ideas, null)]
    [InlineData("Call the bank tomorrow at 11 am.", BoardList.Reminders, "Call the bank")]
    [InlineData("I need to renew the car insurance.", BoardList.Todo, "Renew the car insurance")]
    public void Push_to_talk_on_something_that_is_not_a_text_box_saves_to_the_right_list(string spoken, BoardList list, string? text)
    {
        _desktop.Field = FieldCheck.NotText;
        _controller.InsertDictation(spoken);
        var item = Assert.Single(_board.Items);
        Assert.Equal(list, item.List);
        if (text is not null)
        {
            Assert.Equal(text, item.Text);
        }

        Assert.Empty(_desktop.Typed);
    }

    [Theory]
    [InlineData(FieldCheck.Unknown)]
    [InlineData(FieldCheck.Safe)]
    public void Push_to_talk_into_a_field_that_might_take_text_still_types(FieldCheck field)
    {
        _desktop.Field = field;
        _controller.InsertDictation("I have to draft a reply.");
        Assert.Empty(_board.Items);
        Assert.NotEmpty(_desktop.Typed);
    }

    [Fact]
    public void Saying_only_new_line_with_no_text_box_saves_nothing()
    {
        _desktop.Foreground = null;
        _controller.InsertDictation("new line");
        Assert.Empty(_board.Items);
    }

    [Fact]
    public void Paste_last_dictation_with_no_window_does_not_file_it_again()
    {
        _controller.InsertDictation("Hello there.");
        _desktop.Foreground = null;
        _controller.RepeatLastTranscript();
        Assert.Empty(_board.Items);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public void Without_a_board_no_window_still_warns()
    {
        _controller.Board = null;
        _desktop.Foreground = null;
        _controller.InsertDictation("Something to remember.");
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
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

        public WindowRef? Foreground { get; set; } = Editor;

        public FieldCheck Field { get; set; } = FieldCheck.Safe;

        public WindowRef? GetForegroundWindow() => Foreground;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken) =>
            Task.FromResult(DesktopResult.Ok("ok", Editor));

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(true, string.Empty, false, true));

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(false, "no"));

        public FieldCheck CheckFocusedField() => Field;

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
