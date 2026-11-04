using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class CommandParserTests
{
    private readonly CommandParser _parser = new();

    [Theory]
    [InlineData("Send.")]
    [InlineData("send it")]
    [InlineData(" Submit! ")]
    [InlineData("Computer, send.")]
    public void Standalone_send_while_typing_is_a_command(string heard)
    {
        var parsed = _parser.Parse(heard, InteractionMode.Dictation);
        Assert.Equal([new VoiceCommand(CommandKind.Send)], parsed.Commands);
        Assert.Null(parsed.DictationText);
    }

    [Theory]
    [InlineData("Please send me the latest build logs.")]
    [InlineData("Send it to the team after review.")]
    [InlineData("Click send when ready.")]
    [InlineData("I will send")]
    public void Send_inside_a_sentence_is_typed(string heard)
    {
        var parsed = _parser.Parse(heard, InteractionMode.Dictation);
        Assert.Empty(parsed.Commands);
        Assert.Equal(heard.Trim(), parsed.DictationText);
    }

    [Theory]
    [InlineData("Scratch that.", CommandKind.ScratchThat)]
    [InlineData("Clear box", CommandKind.ClearBox)]
    [InlineData("New line.", CommandKind.NewLine)]
    [InlineData("New paragraph", CommandKind.NewParagraph)]
    [InlineData("Stop typing.", CommandKind.StopTyping)]
    [InlineData("Pause listening.", CommandKind.PauseListening)]
    public void Dictation_commands(string heard, CommandKind kind)
    {
        Assert.Equal(kind, Assert.Single(_parser.Parse(heard, InteractionMode.Dictation).Commands).Kind);
    }

    [Theory]
    [InlineData("Open GitHub Co-Pilot.", "github copilot")]
    [InlineData("open github copilot", "github copilot")]
    [InlineData("Launch Microsoft Teams", "microsoft teams")]
    public void Open_app_in_command_mode(string heard, string app)
    {
        var command = Assert.Single(_parser.Parse(heard, InteractionMode.Command).Commands);
        Assert.Equal(CommandKind.OpenApp, command.Kind);
        Assert.Equal(app, command.Argument);
    }

    [Fact]
    public void Open_and_start_typing_is_chained()
    {
        var parsed = _parser.Parse("Open GitHub Co-pilot and start typing.", InteractionMode.Command);
        Assert.Equal(
            [new VoiceCommand(CommandKind.OpenApp, "github copilot"), new VoiceCommand(CommandKind.StartTyping)],
            parsed.Commands);
    }

    [Fact]
    public void Open_in_dictation_mode_is_typed_not_executed()
    {
        var parsed = _parser.Parse("Open GitHub Copilot.", InteractionMode.Dictation);
        Assert.Empty(parsed.Commands);
        Assert.Equal("Open GitHub Copilot.", parsed.DictationText);
    }

    [Fact]
    public void Start_typing_is_not_parsed_as_opening_an_app_named_typing()
    {
        Assert.Equal(CommandKind.StartTyping, Assert.Single(_parser.Parse("Start typing.", InteractionMode.Command).Commands).Kind);
    }

    [Theory]
    [InlineData("What's the weather like")]
    [InlineData("open")]
    public void Unknown_command_mode_speech_is_unrecognized_not_typed(string heard)
    {
        var parsed = _parser.Parse(heard, InteractionMode.Command);
        Assert.Empty(parsed.Commands);
        Assert.Null(parsed.DictationText);
        Assert.NotNull(parsed.Unrecognized);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(upbeat music)")]
    [InlineData(" . ")]
    [InlineData("")]
    public void Non_speech_is_ignored(string heard)
    {
        Assert.True(_parser.Parse(heard, InteractionMode.Dictation).IsEmpty);
        Assert.True(_parser.Parse(heard, InteractionMode.Command).IsEmpty);
    }

    [Fact]
    public void Paused_ignores_everything()
    {
        Assert.True(_parser.Parse("Send.", InteractionMode.Paused).IsEmpty);
    }

    [Fact]
    public void Strict_prefix_mode_types_bare_command_words()
    {
        var strict = new CommandParser(requirePrefixWhileTyping: true);
        Assert.Equal("Send.", strict.Parse("Send.", InteractionMode.Dictation).DictationText);
        Assert.Equal(CommandKind.Send, Assert.Single(strict.Parse("Computer, send.", InteractionMode.Dictation).Commands).Kind);
    }

    [Fact]
    public void Prefixed_unknown_command_is_not_typed()
    {
        var parsed = _parser.Parse("Computer, make coffee.", InteractionMode.Dictation);
        Assert.Null(parsed.DictationText);
        Assert.Equal("make coffee", parsed.Unrecognized);
    }

    [Fact]
    public void Annotations_are_removed_from_dictation()
    {
        Assert.Equal("Hello there.", _parser.Parse("[MUSIC] Hello there.", InteractionMode.Dictation).DictationText);
    }
}
