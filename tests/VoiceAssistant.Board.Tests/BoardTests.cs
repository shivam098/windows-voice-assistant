using VoiceAssistant.Board;

namespace VoiceAssistant.Board.Tests;

public class BoardTests
{
    // Wednesday 2026-11-11 14:30 (+05:30)
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 14, 30, 0, TimeSpan.FromHours(5.5));

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Now.Offset);

    // ---- grammar ----

    [Theory]
    [InlineData("Add to my ideas, a voice game for kids.", BoardList.Ideas, "A voice game for kids")]
    [InlineData("Add to my ideas: build a sticky note board", BoardList.Ideas, "Build a sticky note board")]
    [InlineData("Add to brainstorming, offline voice search", BoardList.Brainstorm, "Offline voice search")]
    [InlineData("Put this on my to-do list, renew passport", BoardList.Todo, "Renew passport")]
    [InlineData("Add renew passport to my to do list.", BoardList.Todo, "Renew passport")]
    [InlineData("Add buy milk to my todos", BoardList.Todo, "Buy milk")]
    [InlineData("Save to my brainstorm board: pricing ideas", BoardList.Brainstorm, "Pricing ideas")]
    [InlineData("Add to my reminders, pay rent", BoardList.Reminders, "Pay rent")]
    public void Explicit_list_requests_work_everywhere(string spoken, BoardList list, string text)
    {
        foreach (var explicitOnly in new[] { true, false })
        {
            var intent = BoardGrammar.Parse(spoken, Now, explicitOnly);
            Assert.NotNull(intent);
            Assert.Equal(BoardAction.Add, intent.Action);
            Assert.Equal(list, intent.List);
            Assert.Equal(text, intent.Text);
        }
    }

    [Theory]
    [InlineData("Remind me to call mom tomorrow at 5 pm.")]
    [InlineData("Note down that the demo needs subtitles.")]
    [InlineData("New idea, a voice-controlled kitchen timer.")]
    [InlineData("Show my ideas.")]
    [InlineData("Mark renew passport as done.")]
    public void Looser_forms_need_the_wake_word(string spoken)
    {
        Assert.Null(BoardGrammar.Parse(spoken, Now, explicitOnly: true));
        Assert.NotNull(BoardGrammar.Parse(spoken, Now));
    }

    [Theory]
    [InlineData("Please send the report to my manager.")]
    [InlineData("I added the tests to the pipeline.")]
    [InlineData("Let's put the meeting notes in the wiki.")]
    [InlineData("The board meeting is at 5.")]
    public void Ordinary_dictation_is_not_a_board_request(string spoken)
    {
        Assert.Null(BoardGrammar.Parse(spoken, Now, explicitOnly: true));
    }

    [Fact]
    public void Remind_me_files_a_reminder_with_its_time()
    {
        var intent = BoardGrammar.Parse("Remind me to call mom tomorrow at 5 pm.", Now)!;
        Assert.Equal(BoardList.Reminders, intent.List);
        Assert.Equal("Call mom", intent.Text);
        Assert.Equal(At(12, 17), intent.Due);
    }

    [Theory]
    [InlineData("Show my ideas.", BoardList.Ideas)]
    [InlineData("Open my to-do list", BoardList.Todo)]
    [InlineData("Show me my reminders", BoardList.Reminders)]
    [InlineData("Show the brainstorming list", BoardList.Brainstorm)]
    public void Show_names_a_list(string spoken, BoardList list) =>
        Assert.Equal(new BoardIntent(BoardAction.Show, list), BoardGrammar.Parse(spoken, Now));

    [Fact]
    public void Show_my_board_opens_the_whole_board() =>
        Assert.Equal(new BoardIntent(BoardAction.Show, null), BoardGrammar.Parse("Open my board.", Now));

    [Fact]
    public void Mark_done_names_the_item()
    {
        var intent = BoardGrammar.Parse("Mark renew passport as done.", Now)!;
        Assert.Equal(BoardAction.Complete, intent.Action);
        Assert.Equal("renew passport", intent.Text);
    }

    [Theory]
    [InlineData("Tick off renew passport", "renew passport")]
    [InlineData("Cross pay rent off.", "pay rent")]
    [InlineData("Mark buy milk done", "buy milk")]
    public void Other_ways_to_tick_off(string spoken, string text) =>
        Assert.Equal(new BoardIntent(BoardAction.Complete, null, text), BoardGrammar.Parse(spoken, Now));

    // These are commands for the app on screen; with the wake word the board must not swallow them.
    [Theory]
    [InlineData("Tick the checkbox")]
    [InlineData("Complete the form")]
    [InlineData("Finish typing")]
    [InlineData("Mark this email as read")]
    [InlineData("Open notes")]
    [InlineData("Open tasks")]
    [InlineData("Go to to do")]
    public void App_commands_are_not_board_requests(string spoken) =>
        Assert.Null(BoardGrammar.Parse(spoken, Now));

    // ---- reminder times ----

    [Theory]
    [InlineData("and add to my reminder, I have to draft a reply to Tata", BoardList.Reminders, "I have to draft a reply to Tata")]
    [InlineData("Um, add to my ideas, a voice game", BoardList.Ideas, "A voice game")]
    [InlineData("Also add renew passport to my to-do list.", BoardList.Todo, "Renew passport")]
    [InlineData("Can you please add to my brainstorm list, pricing", BoardList.Brainstorm, "Pricing")]
    [InlineData("So, and add to my reminders: pay rent", BoardList.Reminders, "Pay rent")]
    public void Leading_filler_words_are_ignored(string spoken, BoardList list, string text)
    {
        var intent = BoardGrammar.Parse(spoken, Now, explicitOnly: true);
        Assert.Equal(list, intent?.List);
        Assert.Equal(text, intent?.Text);
    }

    [Theory]
    [InlineData("Please add the action items to the notes.")]
    [InlineData("Put the agenda on the board.")]
    [InlineData("Write your name on the board.")]
    [InlineData("Add these to the tasks.")]
    [InlineData("Can you add this to the to-do list for the sprint")]
    public void Push_to_talk_types_sentences_meant_for_someone_else(string spoken) =>
        Assert.Null(BoardGrammar.Parse(spoken, Now, explicitOnly: true));

    [Theory]
    [InlineData("Put the agenda on my board.", BoardList.Ideas)]
    [InlineData("Add these to our tasks.", BoardList.Todo)]
    [InlineData("Add pricing to the brainstorm list", BoardList.Brainstorm)]
    public void Push_to_talk_files_requests_for_my_board(string spoken, BoardList list) =>
        Assert.Equal(list, BoardGrammar.Parse(spoken, Now, explicitOnly: true)?.List);

    [Theory]
    [InlineData("stretch in a couple of hours", 2 * 60)]
    [InlineData("stretch in a few minutes", 3)]
    [InlineData("stretch in  a  few  minutes", 3)]
    [InlineData("stretch in couple of hours", 2 * 60)]
    public void Vague_amounts_count(string spoken, int minutes) =>
        Assert.Equal(Now.AddMinutes(minutes), ReminderTime.Parse(spoken, Now).Due);

    [Fact]
    public void A_reminder_past_a_daylight_saving_change_keeps_its_clock_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var friday = new DateTimeOffset(2027, 3, 12, 15, 0, 0, TimeSpan.FromHours(-8)); // clocks go forward on 14 March
        var (_, due) = ReminderTime.Parse("standup on Monday at 9 am", friday, zone);
        Assert.Equal(new DateTimeOffset(2027, 3, 15, 9, 0, 0, TimeSpan.FromHours(-7)), due);
    }

    [Theory]
    [InlineData(9, "back up the laptop at midnight", 12, 0)]
    [InlineData(9, "lunch at noon", 11, 12)]
    [InlineData(23, "lunch at noon", 12, 12)]
    public void Noon_and_midnight_are_never_swapped(int nowHour, string spoken, int day, int hour)
    {
        var now = new DateTimeOffset(2026, 11, 11, nowHour, 0, 0, Now.Offset);
        Assert.Equal(At(day, hour), ReminderTime.Parse(spoken, now).Due);
    }

    [Theory]
    [InlineData("call mom tomorrow at 5 pm", "call mom", 12, 17, 0)]
    [InlineData("call mom tomorrow at 5", "call mom", 12, 17, 0)]
    [InlineData("standup tomorrow at 9", "standup", 12, 9, 0)]
    [InlineData("pay rent at 6", "pay rent", 11, 18, 0)]
    [InlineData("pay rent at 2", "pay rent", 12, 2, 0)]
    [InlineData("water plants at 5:30 p.m.", "water plants", 11, 17, 30)]
    [InlineData("water plants 17:45", "water plants", 11, 17, 45)]
    [InlineData("check the oven at noon", "check the oven", 12, 12, 0)]
    [InlineData("take medicine tonight", "take medicine", 11, 20, 0)]
    [InlineData("take medicine tomorrow morning", "take medicine", 12, 9, 0)]
    [InlineData("send the invoice tomorrow", "send the invoice", 12, 9, 0)]
    [InlineData("gym in the evening", "gym", 11, 18, 0)]
    [InlineData("review the deck on Friday at 10 am", "review the deck", 13, 10, 0)]
    [InlineData("team lunch next Wednesday", "team lunch", 18, 9, 0)]
    public void Times_are_found_and_removed(string spoken, string task, int day, int hour, int minute)
    {
        var (t, due) = ReminderTime.Parse(spoken, Now);
        Assert.Equal(task, t);
        Assert.Equal(At(day, hour, minute), due);
    }

    [Theory]
    [InlineData("stretch in 10 minutes", 10)]
    [InlineData("stretch in ten minutes", 10)]
    [InlineData("check the build in an hour", 60)]
    [InlineData("check the build in half an hour", 30)]
    [InlineData("follow up in 2 days", 2 * 24 * 60)]
    public void Relative_times(string spoken, int minutes) =>
        Assert.Equal(Now.AddMinutes(minutes), ReminderTime.Parse(spoken, Now).Due);

    [Theory]
    [InlineData("call 3 people about the launch")]
    [InlineData("buy a gift")]
    public void No_time_means_no_due_date(string spoken)
    {
        var (task, due) = ReminderTime.Parse(spoken, Now);
        Assert.Null(due);
        Assert.Equal(spoken, task);
    }

    // ---- store ----

    [Fact]
    public void Items_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BoardStore(path, () => Now);
            var idea = store.Add(BoardList.Ideas, "  voice game   for kids. ");
            store.Add(BoardList.Reminders, "call mom", At(12, 17));
            store.SetDone(idea.Id, true);

            var reloaded = new BoardStore(path, () => Now);
            Assert.Equal(2, reloaded.Items.Count);
            var item = reloaded.Items.Single(i => i.List == BoardList.Ideas);
            Assert.Equal("Voice game for kids", item.Text);
            Assert.True(item.Done);
            Assert.Equal(At(12, 17), reloaded.Items.Single(i => i.List == BoardList.Reminders).Due);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unreadable_file_is_kept_and_a_fresh_board_starts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var store = new BoardStore(path);
            Assert.Empty(store.Items);
            Assert.Single(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + ".unreadable-*"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            {
                File.Delete(f);
            }
        }
    }

    [Fact]
    public void Due_reminders_notify_once_and_snooze_brings_them_back()
    {
        var now = Now;
        var store = new BoardStore(null, () => now);
        var reminder = store.Add(BoardList.Reminders, "call mom", Now.AddMinutes(5));
        Assert.Empty(store.DueReminders());
        now = Now.AddMinutes(6);
        Assert.Single(store.DueReminders());
        store.MarkNotified(reminder.Id);
        Assert.Empty(store.DueReminders());
        store.Snooze(reminder.Id, TimeSpan.FromMinutes(10));
        now = now.AddMinutes(11);
        Assert.Single(store.DueReminders());
    }

    [Fact]
    public void Complete_matching_ticks_the_closest_open_item()
    {
        var store = new BoardStore(null, () => Now);
        store.Add(BoardList.Todo, "Renew passport");
        store.Add(BoardList.Todo, "Buy milk");
        var done = store.CompleteMatching("renew the passport");
        Assert.Equal("Renew passport", done?.Text);
        Assert.True(store.Items.Single(i => i.Text == "Renew passport").Done);
        Assert.Null(store.CompleteMatching("call the plumber"));
    }

    [Fact]
    public void Changed_fires_after_each_change()
    {
        var store = new BoardStore(null, () => Now);
        var count = 0;
        store.Changed += () => count++;
        var item = store.Add(BoardList.Ideas, "x");
        store.Move(item.Id, BoardList.Brainstorm);
        store.Remove(item.Id);
        Assert.Equal(3, count);
    }
}
