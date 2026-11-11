using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VoiceAssistant.Board;
using WpfBrush = System.Windows.Media.Brush;

namespace VoiceAssistant.App.UI;

/// <summary>
/// The idea board: one column of sticky notes per list. Notes come from voice (“add to my ideas …”) or the box at the
/// top of each column; tick them off, double-click to edit, right-click to move or delete.
/// </summary>
internal sealed class IdeasPage : UserControl
{
    private readonly BoardStore _board;
    private readonly Dictionary<BoardList, (Border Column, StackPanel Notes, TextBlock Count)> _columns = [];
    private readonly System.Windows.Controls.CheckBox _showDone;
    private readonly Dictionary<string, (Border Card, DockPanel Row, StackPanel Body)> _cards = [];
    private int _editing; // open editors

    // Where each note was last drawn, so only new or moved notes fade in (null before the first draw).
    private Dictionary<string, (BoardList List, bool Done)>? _drawn;

    public IdeasPage(BoardStore board)
    {
        _board = board;
        var root = new DockPanel { Margin = new Thickness(24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _showDone = new System.Windows.Controls.CheckBox { Content = "Show done", VerticalAlignment = VerticalAlignment.Center, IsChecked = true };
        _showDone.Click += (_, _) => Refresh();
        DockPanel.SetDock(_showDone, Dock.Right);
        header.Children.Add(_showDone);
        header.Children.Add(Theme.Heading("Idea board"));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var tip = Theme.Label("Hold Ctrl+Win and say “Add to my ideas, …” or “Add … to my to-do list”. After “Hey Jarvis”: “remind me to call mom tomorrow at 5 pm”. Everything stays on this PC.", 12, Theme.Muted);
        tip.Margin = new Thickness(0, -6, 0, 14);
        DockPanel.SetDock(tip, Dock.Top);
        root.Children.Add(tip);

        var grid = new Grid();
        foreach (var list in Enum.GetValues<BoardList>())
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var column = BuildColumn(list);
            Grid.SetColumn(column, (int)list);
            grid.Children.Add(column);
        }

        root.Children.Add(grid);
        Content = root;
        Background = Theme.Background;
        _board.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    /// <summary>Scrolls to and briefly highlights a list (“show my reminders”): its outline glows, then fades back.</summary>
    public void Highlight(BoardList list)
    {
        var column = _columns[list].Column;
        column.BringIntoView();
        var glow = Motion.Live(Theme.Color("#5B4BDB"));
        column.BorderBrush = glow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Motion.ToColor(glow, Theme.Color("#E0E0E0"), 900);
        };
        timer.Start();
    }

    private Border BuildColumn(BoardList list)
    {
        var panel = new DockPanel();
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var count = Theme.Label(string.Empty, 12, Theme.Muted, wrap: false);
        DockPanel.SetDock(count, Dock.Right);
        title.Children.Add(count);
        title.Children.Add(new TextBlock { Text = Theme.ListName(list), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var add = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
            ToolTip = list == BoardList.Reminders ? "Type a reminder with a time (“call mom tomorrow 5 pm”) and press Enter" : "Type a note and press Enter",
        };
        var hint = new TextBlock
        {
            Text = list == BoardList.Reminders ? "Add a reminder… (with a time)" : "Add a note…",
            Foreground = Theme.Muted,
            Margin = new Thickness(9, 5, 0, 0),
            IsHitTestVisible = false,
            FontSize = 13,
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "Add to " + Theme.ListName(list));
        add.TextChanged += (_, _) => hint.Visibility = add.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        add.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || add.Text.Trim().Length == 0)
            {
                return;
            }

            var text = add.Text.Trim();
            DateTimeOffset? due = null;
            if (list == BoardList.Reminders)
            {
                (text, due) = ReminderTime.Parse(text, DateTimeOffset.Now);
            }

            if (text.Length > 0)
            {
                _board.Add(list, text, due);
            }

            add.Clear();
        };
        var addBox = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        addBox.Children.Add(add);
        addBox.Children.Add(hint);
        DockPanel.SetDock(addBox, Dock.Top);
        panel.Children.Add(addBox);

        var notes = new StackPanel();
        var scroll = new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Motion.SmoothScroll(scroll);
        panel.Children.Add(scroll);

        var column = new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 0),
            Child = panel,
        };
        _columns[list] = (column, notes, count);
        return column;
    }

    private void Refresh()
    {
        // Redrawing would remove the editor mid-edit; the edit's end redraws instead.
        if (_editing > 0)
        {
            return;
        }

        var items = _board.Items;
        var now = DateTimeOffset.Now;
        _cards.Clear();
        foreach (var (list, (_, notes, count)) in _columns)
        {
            var inList = items.Where(i => i.List == list).ToList();
            var open = inList.Where(i => !i.Done)
                .OrderBy(i => list == BoardList.Reminders ? i.Due ?? DateTimeOffset.MaxValue : DateTimeOffset.MinValue)
                .ThenByDescending(i => i.Created);
            var done = _showDone.IsChecked == true ? inList.Where(i => i.Done).OrderByDescending(i => i.Created) : Enumerable.Empty<BoardItem>();
            notes.Children.Clear();
            foreach (var item in open.Concat(done))
            {
                var card = Note(item, now);
                notes.Children.Add(card);
                if (_drawn is not null && (!_drawn.TryGetValue(item.Id, out var was) || was != (item.List, item.Done)))
                {
                    Motion.FadeIn(card, 260, rise: 6);
                }
            }

            if (notes.Children.Count == 0)
            {
                notes.Children.Add(Theme.Label(list == BoardList.Reminders ? "No reminders yet." : "Nothing here yet.", 12, Theme.Muted));
            }

            var openCount = inList.Count(i => !i.Done);
            count.Text = openCount == 0 ? string.Empty : openCount.ToString(CultureInfo.CurrentCulture);
        }

        _drawn = items.ToDictionary(i => i.Id, i => (i.List, i.Done));
    }

    private Border Note(BoardItem item, DateTimeOffset now)
    {
        var check = new System.Windows.Controls.CheckBox { IsChecked = item.Done, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Done" };
        check.Click += (_, _) => _board.SetDone(item.Id, check.IsChecked == true);

        var text = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = item.Done ? Theme.Muted : Theme.Text,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
        };
        var body = new StackPanel();
        body.Children.Add(text);
        if (item.Due is { } due)
        {
            var overdue = !item.Done && due <= now;
            body.Children.Add(Theme.Label("⏰ " + DueText(due, now), 12, overdue ? Theme.Danger : Theme.Muted));
        }

        var row = new DockPanel();
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        row.Children.Add(body);

        var card = new Border
        {
            Background = item.Done ? Theme.Background : Theme.ListColours[item.List],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
            ToolTip = $"Added {item.Created.LocalDateTime:g}. Double-click to edit, right-click for more.",
        };
        card.ContextMenu = Menu(item);
        _cards[item.Id] = (card, row, body);
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                BeginEdit(card, row, body, item);
                e.Handled = true;
            }
        };
        return card;
    }

    private ContextMenu Menu(BoardItem item)
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit" };
        edit.Click += (_, _) => EditFromMenu(item);
        menu.Items.Add(edit);
        var move = new MenuItem { Header = "Move to" };
        foreach (var list in Enum.GetValues<BoardList>().Where(l => l != item.List))
        {
            var target = new MenuItem { Header = Theme.ListName(list) };
            target.Click += (_, _) => _board.Move(item.Id, list);
            move.Items.Add(target);
        }

        menu.Items.Add(move);
        if (item.List == BoardList.Reminders && item.Due is not null && !item.Done)
        {
            var snooze = new MenuItem { Header = "Snooze 10 minutes" };
            snooze.Click += (_, _) => _board.Snooze(item.Id, TimeSpan.FromMinutes(10));
            menu.Items.Add(snooze);
        }

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _board.Remove(item.Id);
        menu.Items.Add(delete);
        return menu;
    }

    private void EditFromMenu(BoardItem item)
    {
        if (_cards.TryGetValue(item.Id, out var c))
        {
            BeginEdit(c.Card, c.Row, c.Body, item);
        }
    }

    private void BeginEdit(Border card, DockPanel row, StackPanel body, BoardItem item)
    {
        var editor = new System.Windows.Controls.TextBox { Text = item.Text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, FontSize = 14 };
        _editing++;
        row.Children.Remove(body);
        row.Children.Add(editor);
        editor.Focus();
        editor.SelectAll();
        var finished = false;
        void Finish(bool save)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            _editing--;
            if (save && editor.Text.Trim().Length > 0 && editor.Text.Trim() != item.Text)
            {
                _board.SetText(item.Id, editor.Text);
            }

            Refresh();
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Finish(true);
            }
            else if (e.Key == Key.Escape)
            {
                Finish(false);
            }
        };
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        card.BorderBrush = Theme.Accent;
    }

    private static string DueText(DateTimeOffset due, DateTimeOffset now) =>
        VoiceAssistant.Core.InteractionController.DescribeDue(due, now) + (due <= now ? " (due)" : string.Empty);
}
