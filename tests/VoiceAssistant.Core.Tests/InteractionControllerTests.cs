using VoiceAssistant.Core;

namespace VoiceAssistant.Core.Tests;

public class InteractionControllerTests
{
    private static readonly WindowRef Copilot = new(100, 1, "github", "GitHub Copilot");
    private static readonly WindowRef Other = new(200, 2, "msedge", "Browser");

    private static readonly AppEntry CopilotApp = new()
    {
        Name = "GitHub Copilot",
        Launch = "copilot",
        ProcessNames = ["github"],
        WindowTitleContains = "GitHub Copilot",
        TextArea = "Bottom",
    };

    private static readonly ControllerOptions NoWake = new() { WakeWordEnabled = false };

    private static readonly AppEntry NotepadApp = new()
    {
        Name = "Notepad",
        Launch = "notepad.exe",
        ProcessNames = ["Notepad"],
        SubmitKeys = string.Empty,
        NewLineKeys = "Enter",
    };

    private readonly FakeDesktop _desktop = new();
    private readonly List<StatusUpdate> _statuses = [];
    private readonly InteractionController _controller;

    public InteractionControllerTests()
    {
        _desktop.OpenResults["GitHub Copilot"] = Copilot;
        _controller = new InteractionController(_desktop, new AppCatalog([CopilotApp, NotepadApp]), new CommandParser(), NoWake);
        _controller.Status += _statuses.Add;
        _controller.SetListening(true);
    }

    private Task Say(string text, float confidence = 0.9f, bool continuation = false) => _controller.HandleTranscriptAsync(text, confidence, continuation);

    [Fact]
    public async Task Full_copilot_flow_opens_types_and_sends()
    {
        _desktop.Foreground = Other;
        await Say("Open GitHub Co-Pilot.");
        await Say("Start typing.");
        await Say("Fix the failing test.");
        await Say("And add a regression check.");
        await Say("Send.");

        Assert.Equal(
            ["open:GitHub Copilot", "prepare:github:Bottom", "type:Fix the failing test.", "type: And add a regression check.", "keys:Enter"],
            _desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Open_and_start_typing_in_one_utterance()
    {
        _desktop.Foreground = Other;
        await Say("Open GitHub Copilot and start typing.");
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
        Assert.Same(Copilot, _controller.Target);
    }

    [Fact]
    public async Task Sentence_containing_send_is_typed_not_sent()
    {
        await StartTypingInCopilot();
        await Say("Please send me the logs.");
        Assert.DoesNotContain("keys:Enter", _desktop.Actions);
        Assert.Contains("type:Please send me the logs.", _desktop.Actions);
    }

    [Fact]
    public async Task Focus_change_blocks_typing_and_keeps_text()
    {
        await StartTypingInCopilot();
        _desktop.Foreground = Other;
        await Say("Secret plans.");
        await Say("Send.");

        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal) || a.StartsWith("keys:", StringComparison.Ordinal));
        Assert.Equal("Secret plans.", _controller.UntypedText);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Untyped_text_survives_restarting_typing_until_copied()
    {
        await StartTypingInCopilot();
        _desktop.Foreground = Other;
        await Say("Keep me.");
        _desktop.Foreground = Copilot;
        await Say("Start typing.");
        Assert.Equal("Keep me.", _controller.UntypedText);
        _controller.ClearUntypedText();
        Assert.Null(_controller.UntypedText);
    }

    [Fact]
    public async Task Scratch_that_removes_only_the_last_phrase()
    {
        await StartTypingInCopilot();
        await Say("Hello.");
        await Say("Wrong words.");
        await Say("Scratch that.");
        Assert.Equal("backspace:13", _desktop.Actions[^1]);
        await Say("Scratch that.");
        Assert.Equal("backspace:6", _desktop.Actions[^1]);
        await Say("Scratch that.");
        Assert.Equal("backspace:6", _desktop.Actions[^1]);
    }

    [Fact]
    public async Task Existing_text_in_box_gets_a_leading_space()
    {
        _desktop.TrailingText = true;
        await StartTypingInCopilot();
        await Say("More.");
        Assert.Equal("type: More.", _desktop.Actions[^1]);
    }

    [Fact]
    public async Task New_line_resets_spacing_and_can_be_scratched()
    {
        await StartTypingInCopilot();
        await Say("One.");
        await Say("New line.");
        await Say("Two.");
        Assert.Equal(["keys:Shift+Enter", "type:Two."], _desktop.Actions[^2..]);
        await Say("Scratch that.");
        await Say("Scratch that.");
        Assert.Equal("backspace:1", _desktop.Actions[^1]);
    }

    [Fact]
    public async Task Clear_box_selects_all_and_deletes()
    {
        await StartTypingInCopilot();
        await Say("Draft.");
        await Say("Clear box.");
        Assert.Equal(["keys:Ctrl+A", "backspace:1"], _desktop.Actions[^2..]);
        await Say("Fresh.");
        Assert.Equal("type:Fresh.", _desktop.Actions[^1]);
    }

    [Fact]
    public async Task Send_in_command_mode_requires_a_target()
    {
        _desktop.Foreground = Copilot;
        await Say("Send.");
        Assert.DoesNotContain("keys:Enter", _desktop.Actions);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public async Task Send_after_stop_typing_works_when_target_still_in_front()
    {
        await StartTypingInCopilot();
        await Say("Draft.");
        await Say("Stop typing.");
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("Send.");
        Assert.Equal("keys:Enter", _desktop.Actions[^1]);
    }

    [Fact]
    public async Task App_without_send_action_refuses_send()
    {
        _desktop.Foreground = new WindowRef(300, 3, "Notepad", "Untitled - Notepad");
        await Say("Start typing.");
        await Say("Note.");
        await Say("Send.");
        Assert.DoesNotContain("keys:Enter", _desktop.Actions);
        Assert.Contains("no send action", _statuses[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_field_is_never_typed_into()
    {
        await StartTypingInCopilot();
        _desktop.Field = FieldCheck.Password;
        await Say("hunter2");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Low_confidence_command_is_ignored_entirely()
    {
        await StartTypingInCopilot();
        await Say("Send.", confidence: 0.2f);
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("keys:", StringComparison.Ordinal) || a.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_preparation_stays_in_command_mode()
    {
        _desktop.Foreground = Copilot;
        _desktop.PrepareSucceeds = false;
        await Say("Start typing.");
        Assert.Equal(InteractionMode.Command, _controller.Mode);
        await Say("Hello.");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chained_start_typing_is_skipped_when_open_fails()
    {
        await Say("Open Photoshop and start typing.");
        Assert.Empty(_desktop.Actions);
        Assert.Equal(InteractionMode.Command, _controller.Mode);
    }

    [Fact]
    public async Task Pause_command_raises_pause_and_ignores_further_speech()
    {
        var paused = false;
        _controller.PauseRequested += () => paused = true;
        await StartTypingInCopilot();
        await Say("Pause listening.");
        Assert.True(paused);
        Assert.Equal(InteractionMode.Paused, _controller.Mode);
        await Say("Hello.");
        Assert.DoesNotContain("type:Hello.", _desktop.Actions);
    }

    [Fact]
    public async Task Status_messages_never_include_dictated_text_except_heard_field()
    {
        await StartTypingInCopilot();
        await Say("Confidential roadmap.");
        Assert.All(_statuses, s => Assert.DoesNotContain("Confidential", s.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unverifiable_field_is_not_typed_into_and_text_is_kept()
    {
        await StartTypingInCopilot();
        _desktop.Field = FieldCheck.Unknown;
        await Say("Maybe a password.");
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("type:", StringComparison.Ordinal));
        Assert.Equal("Maybe a password.", _controller.UntypedText);
    }

    [Fact]
    public async Task Unconfirmed_open_does_not_chain_into_start_typing()
    {
        var catalog = new AppCatalog([CopilotApp]);
        catalog.SetDiscoveredApps([new AppEntry { Name = "Outlook", Launch = "outlook" }]);
        var controller = new InteractionController(_desktop, catalog, new CommandParser(), NoWake);
        controller.SetListening(true);
        _desktop.Foreground = Other;
        _desktop.UnconfirmedOpen = true;

        await controller.HandleTranscriptAsync("Open Outlook and start typing.", 0.9f);

        Assert.Equal(["open:Outlook"], _desktop.Actions);
        Assert.Equal(InteractionMode.Command, controller.Mode);
        Assert.Null(controller.Target);
    }

    [Theory]
    [InlineData("Send.")]
    [InlineData("Pause listening.")]
    [InlineData("Clear box.")]
    public async Task Continuation_after_forced_cut_is_typed_never_executed(string tail)
    {
        await StartTypingInCopilot();
        await Say(tail, continuation: true);
        Assert.Equal([$"type:{tail}"], _desktop.Actions);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Continuation_outside_typing_is_parsed_normally()
    {
        // Only dictation needs protecting from a sentence tail; a stuck flag must not swallow commands.
        _desktop.Foreground = Copilot;
        await Say("Start typing.", continuation: true);
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    [Fact]
    public async Task Cancellation_stops_a_chained_command()
    {
        _desktop.Foreground = Other;
        using var cts = new CancellationTokenSource();
        _desktop.OnOpen = cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _controller.HandleTranscriptAsync("Open GitHub Copilot and start typing.", 0.9f, false, cts.Token));
        Assert.DoesNotContain(_desktop.Actions, a => a.StartsWith("prepare:", StringComparison.Ordinal));
    }

    private async Task StartTypingInCopilot()
    {
        _desktop.Foreground = Copilot;
        await Say("Start typing.");
        _desktop.Actions.Clear();
        Assert.Equal(InteractionMode.Dictation, _controller.Mode);
    }

    private sealed class FakeDesktop : IDesktop
    {
        public List<string> Actions { get; } = [];

        public Dictionary<string, WindowRef> OpenResults { get; } = [];

        public WindowRef? Foreground { get; set; }

        public bool PrepareSucceeds { get; set; } = true;

        public bool TrailingText { get; set; }

        public FieldCheck Field { get; set; } = FieldCheck.Safe;

        public bool UnconfirmedOpen { get; set; }

        public Action? OnOpen { get; set; }

        public WindowRef? GetForegroundWindow() => Foreground;

        public Task<DesktopResult> OpenOrActivateAsync(AppEntry app, CancellationToken cancellationToken)
        {
            Actions.Add($"open:{app.Name}");
            OnOpen?.Invoke();
            if (UnconfirmedOpen)
            {
                return Task.FromResult(DesktopResult.Ok("Asked Windows to open it."));
            }

            if (OpenResults.TryGetValue(app.Name, out var window))
            {
                Foreground = window;
                return Task.FromResult(DesktopResult.Ok("ok", window));
            }

            return Task.FromResult(DesktopResult.Fail("nope"));
        }

        public Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken)
        {
            Actions.Add($"prepare:{window.ProcessName}:{profile?.TextArea ?? "none"}");
            return Task.FromResult(new TextTargetResult(PrepareSucceeds, PrepareSucceeds ? string.Empty : "no box", TrailingText));
        }

        public FieldCheck CheckFocusedField() => Field;

        public bool? TrailingNonWhitespace { get; set; }

        public bool SearchBoxFound { get; set; } = true;

        public bool? FocusedTextEndsWithNonWhitespace() => TrailingNonWhitespace;

        public Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken)
        {
            Actions.Add($"searchbox:{window.ProcessName}");
            return Task.FromResult(new TextTargetResult(SearchBoxFound, SearchBoxFound ? string.Empty : "no search box"));
        }

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");

        public void Backspace(int count) => Actions.Add($"backspace:{count}");
    }
}
