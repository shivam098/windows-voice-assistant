using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

/// <summary>
/// Spoken edits (docs/specs/voice-commands.md, “Editing what you typed”): the grammar, the keystroke plans, and the
/// controller against a simulated text box that really applies Left/Right/Backspace/typing to a buffer.
/// </summary>
public class EditingTests
{
    // ---- grammar ----

    private static VoiceCommand? Parse(string heard, InteractionMode mode = InteractionMode.Dictation) =>
        new CommandParser().Parse(heard, mode).Commands.SingleOrDefault();

    [Theory]
    [InlineData("Delete the last word.", EditUnit.Word, 1)]
    [InlineData("Delete the last three words.", EditUnit.Word, 3)]
    [InlineData("Delete the last 3 words.", EditUnit.Word, 3)]
    [InlineData("Erase the previous sentence.", EditUnit.Sentence, 1)]
    [InlineData("Delete the last two sentences.", EditUnit.Sentence, 2)]
    [InlineData("Remove the last line.", EditUnit.Line, 1)]
    [InlineData("Delete last paragraph.", EditUnit.Paragraph, 1)]
    [InlineData("Delete the last few words.", EditUnit.Word, 3)]
    [InlineData("Delete the last couple of words.", EditUnit.Word, 2)]
    [InlineData("Delete three words.", EditUnit.Word, 3)]
    [InlineData("Delete that sentence.", EditUnit.Sentence, 1)]
    [InlineData("Scratch the last line.", EditUnit.Line, 1)]
    [InlineData("Delete the last 5 characters.", EditUnit.Character, 5)]
    public void Counted_deletes_are_commands_while_typing(string heard, EditUnit unit, int count)
    {
        var command = Parse(heard);
        Assert.Equal(CommandKind.DeleteText, command?.Kind);
        Assert.Equal(new TextEdit(unit, count), command!.Edit);
    }

    [Fact]
    public void Delete_the_word_names_a_phrase()
    {
        var command = Parse("Delete the word very.");
        Assert.Equal(CommandKind.DeleteText, command?.Kind);
        Assert.Equal("very", command!.Edit!.Phrase);
    }

    [Fact]
    public void Replace_the_word_keeps_the_spoken_replacement()
    {
        var command = Parse("Replace the word Monday with GitHub Actions.");
        Assert.Equal(CommandKind.ReplaceText, command?.Kind);
        Assert.Equal("monday", command!.Edit!.Phrase);
        Assert.Equal("GitHub Actions", command.Edit.Replacement);
    }

    [Theory]
    [InlineData("Hey Jarvis, replace Monday with Tuesday.", "monday", "Tuesday")]
    [InlineData("Hey Jarvis, change the meeting to the review.", "the meeting", "the review")]
    public void Replace_without_the_word_needs_the_wake_word(string heard, string phrase, string replacement)
    {
        var command = Parse(heard);
        Assert.Equal(CommandKind.ReplaceText, command?.Kind);
        Assert.Equal(phrase, command!.Edit!.Phrase);
        Assert.Equal(replacement, command.Edit.Replacement);
    }

    [Theory]
    [InlineData("Replace the battery with a new one.")]
    [InlineData("Change the plan to Friday.")]
    [InlineData("Delete the file when you are done.")]
    [InlineData("Remove the last line of the config.")]
    [InlineData("Delete words.")]
    [InlineData("Delete 500 words.")]
    public void Ordinary_sentences_are_still_typed(string heard)
    {
        var parsed = new CommandParser().Parse(heard, InteractionMode.Dictation);
        Assert.Empty(parsed.Commands);
        Assert.NotNull(parsed.DictationText);
    }

    [Fact]
    public void After_the_wake_word_delete_names_a_phrase()
    {
        var command = Parse("Hey Jarvis, delete really.");
        Assert.Equal(CommandKind.DeleteText, command?.Kind);
        Assert.Equal("really", command!.Edit!.Phrase);
    }

    [Theory]
    [InlineData("Delete that.", CommandKind.ScratchThat)]
    [InlineData("Remove numbers.", CommandKind.HideNumbers)]
    public void Existing_commands_keep_their_meaning(string heard, CommandKind kind)
    {
        var mode = kind == CommandKind.HideNumbers ? InteractionMode.Command : InteractionMode.Dictation;
        Assert.Equal(kind, Parse(heard, mode)?.Kind);
    }

    // ---- plans ----

    [Theory]
    [InlineData("Hello there my friend", EditUnit.Word, 1, "Hello there my")]
    [InlineData("Hello there my friend.", EditUnit.Word, 2, "Hello there")]
    [InlineData("Hello there my friend   ", EditUnit.Word, 1, "Hello there my")]
    [InlineData("One. Two is here. Three is next.", EditUnit.Sentence, 1, "One. Two is here.")]
    [InlineData("One. Two is here. Three is next.", EditUnit.Sentence, 2, "One.")]
    [InlineData("One. Two is here? Three", EditUnit.Sentence, 1, "One. Two is here?")]
    [InlineData("Version 1.5 is out", EditUnit.Sentence, 1, "")]
    [InlineData("first line\nsecond line\nthird", EditUnit.Line, 1, "first line\nsecond line")]
    [InlineData("first line\nsecond line\nthird", EditUnit.Line, 2, "first line")]
    [InlineData("first line\n", EditUnit.Line, 1, "first line")]
    [InlineData("Para one.\n\nPara two. More.", EditUnit.Paragraph, 1, "Para one.")]
    [InlineData("abc😀", EditUnit.Character, 1, "abc")]
    [InlineData("word", EditUnit.Word, 5, "")]
    public void Delete_last_units(string before, EditUnit unit, int count, string expected)
    {
        var plan = TextEditing.DeleteLast(before, unit, count);
        Assert.NotNull(plan);
        Assert.Equal(expected, plan.TextAfterEdit);
        Assert.Equal(0, plan.KeepAfter);
    }

    [Fact]
    public void Nothing_to_delete_is_null() => Assert.Null(TextEditing.DeleteLast("   ", EditUnit.Word, 1));

    [Theory]
    [InlineData("It is very very good", "very", null, "It is very good")]
    [InlineData("Very good idea", "very", null, "good idea")]
    [InlineData("See you on Monday. Monday works.", "monday", "Tuesday", "See you on Monday. Tuesday works.")]
    [InlineData("I dont know. I don't know", "dont", "do not", "I dont know. I do not know")]
    [InlineData("Ship it on monday please", "monday", "tuesday", "Ship it on tuesday please")]
    [InlineData("Ship ASAP please", "asap", "now", "Ship NOW please")]
    [InlineData("Mondays are long", "monday", "Tuesday", null)]
    [InlineData("as soon as possible, then", "as soon as possible", "ASAP", "ASAP, then")]
    public void Replace_or_delete_the_last_occurrence(string before, string phrase, string? replacement, string? expected)
    {
        var plan = TextEditing.ReplaceLast(before, phrase, replacement);
        Assert.Equal(expected, plan?.TextAfterEdit);
    }

    [Fact]
    public void Replace_moves_left_over_the_text_after_it_and_back()
    {
        var plan = TextEditing.ReplaceLast("go on monday then", "monday", "friday")!;
        Assert.Equal(" then".Length, plan.KeepAfter);
        Assert.Equal("monday".Length, plan.Delete);
        Assert.Equal("friday", plan.Insert);
    }

    // ---- controller against a simulated text box ----

    private static readonly WindowRef Editor = new(10, 1, "Notepad", "Untitled - Notepad");

    private readonly TextBoxDesktop _box = new();
    private readonly List<StatusUpdate> _statuses = [];
    private readonly InteractionController _controller;

    public EditingTests()
    {
        _controller = new InteractionController(
            _box,
            new AppCatalog([new AppEntry { Name = "Notepad", Launch = "notepad.exe", ProcessNames = ["Notepad"], SubmitKeys = string.Empty, NewLineKeys = "Enter" }]),
            new CommandParser(),
            new ControllerOptions { WakeWordEnabled = false });
        _controller.Status += _statuses.Add;
        _controller.SetListening(true);
    }

    private Task Say(string text) => _controller.HandleTranscriptAsync(text, 0.9f);

    [Fact]
    public async Task Delete_the_last_words_then_keep_typing()
    {
        await Say("Start typing.");
        await Say("Hello there, my good friend and family.");
        await Say("Delete the last two words.");
        Assert.Equal("Hello there, my good friend", _box.Text);
        await Say("and colleagues.");
        Assert.Equal("Hello there, my good friend and colleagues.", _box.Text);
    }

    [Fact]
    public async Task Delete_the_last_sentence()
    {
        await Say("Start typing.");
        await Say("This stays.");
        await Say("This one goes.");
        await Say("Delete the last sentence.");
        Assert.Equal("This stays.", _box.Text);
        Assert.Contains("sentence", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_a_word_in_the_middle_and_return_to_the_end()
    {
        await Say("Start typing.");
        await Say("The review is on Monday at noon.");
        await Say("Replace the word Monday with Tuesday.");
        Assert.Equal("The review is on Tuesday at noon.", _box.Text);
        Assert.Equal(_box.Text.Length, _box.Caret);
        await Say("Bring snacks.");
        Assert.Equal("The review is on Tuesday at noon. Bring snacks.", _box.Text);
    }

    [Fact]
    public async Task Delete_a_word_in_the_middle()
    {
        await Say("Start typing.");
        await Say("It is very very good.");
        await Say("Delete the word very.");
        Assert.Equal("It is very good.", _box.Text);
    }

    [Fact]
    public async Task Missing_phrase_changes_nothing()
    {
        await Say("Start typing.");
        await Say("Hello world.");
        await Say("Delete the word banana.");
        Assert.Equal("Hello world.", _box.Text);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public async Task Unreadable_box_still_deletes_words_with_ctrl_backspace()
    {
        _box.Readable = false;
        await Say("Start typing.");
        await Say("One two three.");
        await Say("Delete the last two words.");
        Assert.Equal(2, _box.CtrlBackspaces);
        await Say("Delete the last sentence.");
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public async Task Selected_text_blocks_edits()
    {
        await Say("Start typing.");
        await Say("Hello world.");
        _box.SelectionLength = 3;
        await Say("Delete the last word.");
        Assert.Equal("Hello world.", _box.Text);
        Assert.Contains("selected", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_text_is_not_edited()
    {
        _controller.SetListening(true);
        _box.Editable = false;
        _box.Load("Some page text with the word banana.");
        await Say("Delete banana.");
        Assert.Equal("Some page text with the word banana.", _box.Text);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public async Task Value_only_box_is_trusted_while_it_ends_with_what_was_typed()
    {
        _box.CaretKnown = false;
        await Say("Start typing.");
        await Say("One two three four.");
        await Say("Delete the last two words.");
        Assert.Equal("One two", _box.Text);
        // The user typed more at the end by hand: the value no longer ends with what was typed, so it isn't trusted.
        _box.TypeText(" five");
        await Say("Delete the last sentence.");
        Assert.Equal("One two five", _box.Text);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public async Task Command_mode_needs_a_checked_field()
    {
        _box.Load("Draft text here");
        _box.Check = FieldCheck.Unknown;
        await Say("Delete the last word.");
        Assert.Equal("Draft text here", _box.Text);
    }

    [Fact]
    public void Paragraphs_split_by_single_line_breaks() =>
        Assert.Equal("A.\nB.", TextEditing.DeleteLast("A.\nB.\nC.", EditUnit.Paragraph, 1)!.TextAfterEdit);

    [Fact]
    public void Deletes_never_reach_further_than_the_limit() =>
        Assert.Null(TextEditing.DeleteLast(new string('a', TextEditing.MaxReach + 10), EditUnit.Sentence, 1));

    [Fact]
    public void Only_spaces_before_a_line_does_not_throw() =>
        Assert.Equal(string.Empty, TextEditing.DeleteLast("  \nfoo", EditUnit.Line, 2)!.TextAfterEdit);

    [Theory]
    [InlineData("Hey Jarvis, delete see you again.", "see you again")]
    [InlineData("Hey Jarvis, delete the word now.", "now")]
    public void Polite_words_stay_in_edit_phrases(string heard, string phrase) =>
        Assert.Equal(phrase, Parse(heard, InteractionMode.Command)!.Edit!.Phrase);

    /// <summary>
    /// A text box: typing inserts at the cursor (replacing a selection); Left/Right move (and collapse a selection);
    /// Shift+Left extends the selection; Backspace deletes the selection or the character before the cursor.
    /// </summary>
    private sealed class TextBoxDesktop : IDesktop
    {
        public string Text { get; private set; } = string.Empty;

        public int Caret { get; private set; }

        /// <summary>Characters selected to the left of the cursor.</summary>
        public int SelectionLength { get; set; }

        public bool Readable { get; set; } = true;

        public bool CaretKnown { get; set; } = true;

        public bool Editable { get; set; } = true;

        public FieldCheck Check { get; set; } = FieldCheck.Safe;

        public int CtrlBackspaces { get; private set; }

        public void Load(string text)
        {
            Text = text;
            Caret = text.Length;
            SelectionLength = 0;
        }

        public void MoveCaretToStart()
        {
            Caret = 0;
            SelectionLength = 0;
        }

        public WindowRef? GetForegroundWindow() => Editor;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken) =>
            Task.FromResult(DesktopResult.Ok("ok", Editor));

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(true, string.Empty, Text.Length > 0 && !char.IsWhiteSpace(Text[^1]), true));

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken) =>
            Task.FromResult(new TextTargetResult(false, "no"));

        public FieldCheck CheckFocusedField() => Check;

        public bool? FocusedTextEndsWithNonWhitespace() => Text.Length > 0 && !char.IsWhiteSpace(Text[^1]);

        public CaretText? ReadTextBeforeCaret() =>
            !Readable ? null
            : CaretKnown ? new CaretText(Text[..Caret], true, SelectionLength > 0, Editable)
            : new CaretText(Text, false, false, Editable);

        public void TypeText(string text)
        {
            DeleteSelection();
            Text = Text[..Caret] + text + Text[Caret..];
            Caret += text.Length;
        }

        public void PressKeys(string chord)
        {
            switch (chord)
            {
                case "Left":
                    Caret = SelectionLength > 0 ? Caret - SelectionLength : Math.Max(0, Caret - 1);
                    SelectionLength = 0;
                    break;
                case "Right":
                    Caret = Math.Min(Text.Length, Caret + (SelectionLength > 0 ? 0 : 1));
                    SelectionLength = 0;
                    break;
                case "Shift+Left":
                    if (Caret - SelectionLength > 0)
                    {
                        SelectionLength++;
                    }

                    break;
                case "Enter":
                    TypeText("\n");
                    break;
                case "Ctrl+Backspace":
                    CtrlBackspaces++;
                    break;
            }
        }

        public void Backspace(int count)
        {
            if (SelectionLength > 0)
            {
                DeleteSelection();
                count--;
            }

            var n = Math.Min(count, Caret);
            Text = Text[..(Caret - n)] + Text[Caret..];
            Caret -= n;
        }

        private void DeleteSelection()
        {
            if (SelectionLength == 0)
            {
                return;
            }

            var start = Caret - SelectionLength;
            Text = Text[..start] + Text[Caret..];
            Caret = start;
            SelectionLength = 0;
        }
    }
}