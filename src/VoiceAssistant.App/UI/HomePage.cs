using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VoiceAssistant.Board;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

/// <summary>Home: is it listening, in which language, what's coming up, and what just happened.</summary>
internal sealed class HomePage : UserControl
{
    private const int MaxActivity = 30;

    private readonly AssistantHost _host;
    private readonly BoardStore _board;
    private readonly SolidColorBrush _dotColor = Motion.Live(Theme.Color("#9AA0A6"));
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mode = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _modeHint = Theme.Label(string.Empty, 13, Theme.Muted);
    private readonly System.Windows.Controls.Button _toggle;
    private readonly System.Windows.Controls.ComboBox _language = new() { Width = 320, FontSize = 13, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    private readonly StackPanel _reminders = new();
    private readonly StackPanel _activity = new();
    private bool _updatingLanguage;

    public HomePage(AssistantHost host, BoardStore board, Action<BoardList?> openBoard)
    {
        _host = host;
        _board = board;
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(Theme.Heading("Voice Assistant"));

        // Listening status.
        var statusRow = new DockPanel();
        _toggle = Theme.Button("Pause listening", (_, _) => _host.ToggleListening(), primary: true);
        DockPanel.SetDock(_toggle, Dock.Right);
        statusRow.Children.Add(_toggle);
        var title = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        title.Children.Add(_dot);
        title.Children.Add(_mode);
        statusRow.Children.Add(title);
        var status = new StackPanel();
        status.Children.Add(statusRow);
        _modeHint.Margin = new Thickness(24, 6, 0, 0);
        status.Children.Add(_modeHint);
        stack.Children.Add(Theme.CardOf(status));

        // Dictation language.
        _language.Items.Add("English");
        _language.Items.Add("Hindi + English: fast (typed in Roman letters)");
        _language.Items.Add("Hindi + English: accurate (slower, 830 MB download)");
        _language.SelectionChanged += (_, _) =>
        {
            if (!_updatingLanguage)
            {
                _host.SetDictationLanguage(_language.SelectedIndex > 0, _language.SelectedIndex == 2);
            }
        };
        var language = new StackPanel();
        language.Children.Add(new TextBlock { Text = "Dictation language", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        language.Children.Add(_language);
        language.Children.Add(Theme.Label("Alt+Shift+H switches between English and Hindi + English. Voice commands are always English.", 12, Theme.Muted));
        stack.Children.Add(Theme.CardOf(language));

        // Coming up.
        var upcoming = new StackPanel();
        var upcomingHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var openReminders = Theme.Button("Open board", (_, _) => openBoard(null));
        DockPanel.SetDock(openReminders, Dock.Right);
        upcomingHeader.Children.Add(openReminders);
        upcomingHeader.Children.Add(new TextBlock { Text = "Coming up", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        upcoming.Children.Add(upcomingHeader);
        upcoming.Children.Add(_reminders);
        stack.Children.Add(Theme.CardOf(upcoming));

        // Quick start.
        var tips = new StackPanel();
        tips.Children.Add(new TextBlock { Text = "Quick start", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var tip in new[]
        {
            "Hold Ctrl+Win, speak, release: your words are typed where the cursor is.",
            "Say “Hey Jarvis, open Chrome”, then “go to the search box”.",
            "Hold Ctrl+Win and say “Add to my ideas, …” to save a thought without typing it anywhere.",
            "“Hey Jarvis, remind me to stretch in 30 minutes.”",
        })
        {
            tips.Children.Add(Theme.Label("•  " + tip, 13));
        }

        stack.Children.Add(Theme.CardOf(tips));

        // Activity (this session only; never saved).
        var activity = new StackPanel();
        activity.Children.Add(new TextBlock { Text = "Recent activity", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        activity.Children.Add(_activity);
        stack.Children.Add(Theme.CardOf(activity));

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Motion.SmoothScroll(scroll);
        Content = scroll;
        Background = Theme.Background;

        ShowMode(_host.Mode);
        ShowLanguage(_host.HinglishDictation, _host.HinglishAccurate);
        _host.Status += OnStatus;
        _host.DictationLanguageChanged += ShowLanguage;
        _board.Changed += () => Dispatcher.BeginInvoke(RefreshReminders);
        RefreshReminders();
        _activity.Children.Add(Theme.Label("Nothing yet this session.", 12, Theme.Muted));
    }

    private void OnStatus(StatusUpdate update)
    {
        ShowMode(update.Mode);
        if (string.IsNullOrWhiteSpace(update.Message))
        {
            return;
        }

        if (_activity.Children.Count == 1 && _activity.Children[0] is TextBlock { Text: "Nothing yet this session." })
        {
            _activity.Children.Clear();
        }

        var line = Theme.Label($"{DateTime.Now:HH:mm}   {update.Message}", 12, update.Kind switch
        {
            StatusKind.Error => Theme.Danger,
            StatusKind.Warning => Theme.Hex("#8A5A00"),
            _ => Theme.Text,
        });
        line.Margin = new Thickness(0, 0, 0, 4);
        _activity.Children.Insert(0, line);
        Motion.FadeIn(line, 220);
        while (_activity.Children.Count > MaxActivity)
        {
            _activity.Children.RemoveAt(_activity.Children.Count - 1);
        }
    }

    private void ShowMode(InteractionMode mode)
    {
        string colour;
        (_mode.Text, _modeHint.Text, colour) = mode switch
        {
            InteractionMode.Dictation => ("Typing", "Speaking types into the chosen text box. Say “stop typing” when done.", "#EA4335"),
            InteractionMode.Command => ("Listening for a command", "Say a command now, e.g. “open Notepad”.", "#4C8DF6"),
            InteractionMode.Standby => ("Listening for “Hey Jarvis”", "Hold Ctrl+Win any time to dictate.", "#34A853"),
            _ => ("Paused", "The microphone is off. Hold Ctrl+Win still dictates.", "#9AA0A6"),
        };
        _dot.Fill = _dotColor;
        Motion.ToColor(_dotColor, Theme.Color(colour), 220);
        _toggle.Content = mode == InteractionMode.Paused ? "Start listening" : "Pause listening";
    }

    private void ShowLanguage(bool hinglish, bool accurate)
    {
        _updatingLanguage = true;
        _language.SelectedIndex = !hinglish ? 0 : accurate ? 2 : 1;
        _updatingLanguage = false;
    }

    private void RefreshReminders()
    {
        var now = DateTimeOffset.Now;
        var next = _board.Items.Where(i => i.List == BoardList.Reminders && !i.Done)
            .OrderBy(i => i.Due ?? DateTimeOffset.MaxValue)
            .Take(4)
            .ToList();
        _reminders.Children.Clear();
        if (next.Count == 0)
        {
            _reminders.Children.Add(Theme.Label("No reminders. Try “Hey Jarvis, remind me to call mom tomorrow at 5 pm”.", 12, Theme.Muted));
            return;
        }

        foreach (var item in next)
        {
            var when = item.Due is { } due ? InteractionController.DescribeDue(due, now) : "no time";
            var line = Theme.Label($"⏰ {when}   {item.Text}", 13, item.Due <= now ? Theme.Danger : Theme.Text);
            line.Margin = new Thickness(0, 0, 0, 4);
            _reminders.Children.Add(line);
        }
    }
}
