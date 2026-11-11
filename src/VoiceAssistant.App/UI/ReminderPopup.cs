using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Board;

namespace VoiceAssistant.App.UI;

/// <summary>
/// A reminder that is due: a card in the bottom-right corner with Done / Snooze / Open board. It never takes keyboard
/// focus, so it can't steal what you're typing, and stays until you answer it.
/// </summary>
internal sealed class ReminderPopup : Window
{
    public ReminderPopup(BoardItem item, BoardStore board, Action openBoard)
    {
        Title = "Reminder";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Theme.Button("Done", (_, _) =>
        {
            board.SetDone(item.Id, true);
            Close();
        }, primary: true));
        buttons.Children.Add(Theme.Button("Snooze 10 min", (_, _) =>
        {
            board.Snooze(item.Id, TimeSpan.FromMinutes(10));
            Close();
        }));
        buttons.Children.Add(Theme.Button("Open board", (_, _) =>
        {
            openBoard();
            Close();
        }));

        var panel = new StackPanel { Width = 320 };
        panel.Children.Add(Theme.Label("⏰ Reminder", 12, Theme.Muted));
        panel.Children.Add(new TextBlock { Text = item.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(buttons);
        Content = new Border
        {
            Background = Theme.ListColours[BoardList.Reminders],
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = panel,
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        };

        Loaded += (_, _) => Motion.FadeIn(this, 260);

        // Off screen until the app stacks it with the other open cards (it needs the card's height first).
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
    }
}
