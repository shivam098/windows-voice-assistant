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
/// top of each column; tick them off, drag them to reorder or to another list, double-click to edit, right-click for more.
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

    private const string DragFormat = "VoiceAssistant.BoardItem";
    private readonly Border _dropLine = new() { Height = 3, CornerRadius = new CornerRadius(2), Background = Theme.Accent, Margin = new Thickness(2, 0, 2, 8), IsHitTestVisible = false };
    private System.Windows.Point? _pressAt;
    private BoardItem? _pressed;
    private Border? _pressedCard;
    private DragGhost? _ghost;
    private BoardList? _dropList;

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

        var tip = Theme.Label("Tap Ctrl+Win and say “Add to my ideas, …” or “Add … to my to-do list”, then tap Ctrl+Win again. After “Hey Jarvis”: “remind me to call mom tomorrow at 5 pm”. Everything stays on this PC.", 12, Theme.Muted);
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
        AllowDrop = true;
        // Tunnelling, so a column's "Add a note…" box (which refuses card drags) can't swallow them.
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
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
            var open = BoardStore.Arrange(inList.Where(i => !i.Done), list);
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
            ToolTip = $"Added {item.Created.LocalDateTime:g}. Drag to move, double-click to edit, right-click for more.",
            Tag = item,
            Cursor = System.Windows.Input.Cursors.SizeAll,
        };
        card.ContextMenu = Menu(item);
        _cards[item.Id] = (card, row, body);
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Not from the tick box or an open editor: those keep their own clicks.
            if (e.ClickCount == 1 && !IsInside<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource) && !IsInside<System.Windows.Controls.TextBox>(e.OriginalSource))
            {
                _pressAt = e.GetPosition(this);
                _pressed = item;
                _pressedCard = card;
            }
        };
        card.PreviewMouseLeftButtonUp += (_, _) => _pressAt = null;
        card.PreviewMouseMove += (_, e) =>
        {
            if (_pressAt is not { } start || _pressedCard != card || e.LeftButton != MouseButtonState.Pressed || _editing > 0)
            {
                return;
            }

            var moved = e.GetPosition(this) - start;
            if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                StartDrag(card, item, e.GetPosition(card));
            }
        };
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
        if (!item.Done && item.List != BoardList.Reminders)
        {
            // The keyboard way to reorder (dragging needs a mouse).
            var siblings = BoardStore.Arrange(_board.Items.Where(i => i.List == item.List && !i.Done), item.List).ToList();
            var at = siblings.FindIndex(i => i.Id == item.Id);
            var up = new MenuItem { Header = "Move up", IsEnabled = at > 0 };
            up.Click += (_, _) => _board.Place(item.Id, item.List, at - 1);
            var down = new MenuItem { Header = "Move down", IsEnabled = at >= 0 && at < siblings.Count - 1 };
            down.Click += (_, _) => _board.Place(item.Id, item.List, at + 1);
            menu.Items.Add(up);
            menu.Items.Add(down);
        }

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

    private void StartDrag(Border card, BoardItem item, System.Windows.Point grab)
    {
        _pressAt = null;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(this);
        _ghost = layer is null ? null : new DragGhost(this, card, grab);
        if (_ghost is not null)
        {
            layer!.Add(_ghost);
        }

        card.BeginAnimation(OpacityProperty, null); // a held fade-in would override the dimming
        card.Opacity = 0.35;
        try
        {
            DragDrop.DoDragDrop(card, new System.Windows.DataObject(DragFormat, item.Id), System.Windows.DragDropEffects.Move);
        }
        finally
        {
            card.Opacity = 1;
            if (_ghost is not null)
            {
                layer?.Remove(_ghost);
                _ghost = null;
            }

            HideDropTarget();
        }
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            // Text from another app can still be dropped into an "Add a note…" box; anywhere else it can't land.
            if (!IsInside<System.Windows.Controls.TextBox>(e.OriginalSource))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
            }

            return;
        }

        e.Handled = true;

        _ghost?.MoveTo(e.GetPosition(this));
        if (DropTarget(e) is not { } target)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            HideDropTarget();
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        ShowDropTarget(target.List, target.Child);
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            return;
        }

        e.Handled = true;
        var target = DropTarget(e);
        HideDropTarget();
        if (target is { } t && e.Data.GetData(DragFormat) is string id)
        {
            _board.Place(id, t.List, t.Index);
        }
    }

    /// <summary>
    /// Which list the pointer is over, the position among its open notes (0 = top), and where the drop line goes among
    /// the column's children. Reminders are kept in time order, so only the list matters there.
    /// </summary>
    private (BoardList List, int Index, int Child)? DropTarget(System.Windows.DragEventArgs e)
    {
        foreach (var (list, (column, notes, _)) in _columns)
        {
            var p = e.GetPosition(column);
            if (p.X < 0 || p.Y < 0 || p.X > column.ActualWidth || p.Y > column.ActualHeight)
            {
                continue;
            }

            var dragged = e.Data.GetData(DragFormat) as string;
            var y = e.GetPosition(notes).Y;
            if (notes.Parent is ScrollViewer scroll)
            {
                // Measured against what's visible: over the title or the add box means the top of the list (scrolled to),
                // below the visible notes means after the last one you can see.
                var inView = e.GetPosition(scroll).Y;
                if (inView < 24 && scroll.VerticalOffset > 0)
                {
                    scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset - 16));
                }
                else if (inView > scroll.ViewportHeight - 24 && inView <= scroll.ViewportHeight)
                {
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 16);
                }

                if (inView < 0)
                {
                    y = double.NegativeInfinity;
                }
                else if (inView > scroll.ViewportHeight)
                {
                    y = Math.Min(y, scroll.VerticalOffset + scroll.ViewportHeight);
                }
            }
            var index = 0;
            var child = 0;
            for (var c = 0; c < notes.Children.Count; c++)
            {
                if (notes.Children[c] is not Border { Tag: BoardItem item } card || item.Done)
                {
                    if (notes.Children[c] is Border { Tag: BoardItem { Done: true } })
                    {
                        break; // done notes sit below the open ones
                    }

                    continue;
                }

                if (item.Id == dragged)
                {
                    child = c + 1;
                    continue;
                }

                var mid = card.TranslatePoint(new System.Windows.Point(0, card.ActualHeight / 2), notes).Y;
                if (y < mid)
                {
                    child = c;
                    break;
                }

                index++;
                child = c + 1;
            }

            return (list, list == BoardList.Reminders ? 0 : index, child);
        }

        return null;
    }

    private void ShowDropTarget(BoardList list, int child)
    {
        if (_dropList != list)
        {
            HideDropTarget();
            _dropList = list;
            _columns[list].Column.BorderBrush = Theme.Accent;
        }

        if (list == BoardList.Reminders)
        {
            return;
        }

        var notes = _columns[list].Notes;
        var current = notes.Children.IndexOf(_dropLine);
        if (current >= 0 && current < child)
        {
            child--; // the line itself is above the target
        }

        if (current != child)
        {
            if (current >= 0)
            {
                notes.Children.RemoveAt(current);
            }

            notes.Children.Insert(Math.Min(child, notes.Children.Count), _dropLine);
        }
    }

    private void HideDropTarget()
    {
        if (_dropList is { } list)
        {
            _columns[list].Column.BorderBrush = Theme.Border;
        }

        _dropList = null;
        (_dropLine.Parent as System.Windows.Controls.Panel)?.Children.Remove(_dropLine);
    }

    private static bool IsInside<T>(object source)
        where T : DependencyObject
    {
        for (var node = source as DependencyObject; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A see-through copy of the card that follows the pointer while it is dragged.</summary>
    private sealed class DragGhost : System.Windows.Documents.Adorner
    {
        private readonly VisualBrush _look;
        private readonly System.Windows.Size _size;
        private readonly System.Windows.Point _grab;
        private System.Windows.Point _at;

        public DragGhost(UIElement page, Border card, System.Windows.Point grab)
            : base(page)
        {
            _look = new VisualBrush(card) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            _size = new System.Windows.Size(card.ActualWidth, card.ActualHeight);
            _grab = grab;
            _at = card.TranslatePoint(grab, page);
            IsHitTestVisible = false;
            Opacity = 0.9;
        }

        public void MoveTo(System.Windows.Point at)
        {
            _at = at;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var box = new Rect(new System.Windows.Point(_at.X - _grab.X, _at.Y - _grab.Y), _size);
            dc.PushTransform(new RotateTransform(-1.5, box.X + _grab.X, box.Y + _grab.Y));
            dc.DrawRoundedRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0)), null, new Rect(box.X + 2, box.Y + 4, box.Width, box.Height), 6, 6);
            dc.DrawRectangle(_look, null, box);
            dc.Pop();
        }
    }

    private static string DueText(DateTimeOffset due, DateTimeOffset now) =>
        VoiceAssistant.Core.InteractionController.DescribeDue(due, now) + (due <= now ? " (due)" : string.Empty);
}
