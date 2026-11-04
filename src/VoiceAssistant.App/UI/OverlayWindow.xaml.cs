using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

/// <summary>Click-through status pill that never takes keyboard focus from the app being dictated into.</summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(6) };

    public OverlayWindow()
    {
        InitializeComponent();
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            MessageText.Visibility = Visibility.Collapsed;
            HeardText.Visibility = Visibility.Collapsed;
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
        };
        SizeChanged += (_, _) => Reposition();
    }

    public void ShowMode(InteractionMode mode, string? activity = null)
    {
        (ModeText.Text, Dot.Fill) = mode switch
        {
            InteractionMode.Dictation => ("Typing — say “stop typing” when done", Brush("#EA4335")),
            InteractionMode.Command => ("Listening for a command", Brush("#4C8DF6")),
            InteractionMode.Standby => ("Say “Hey Jarvis” · hold Ctrl+Win to dictate", Brush("#8AB4F8")),
            _ => ("Paused · hold Ctrl+Win to dictate", Brush("#9AA0A6")),
        };

        if (activity is not null)
        {
            ModeText.Text = activity;
            Dot.Fill = activity.StartsWith("Dictating", StringComparison.Ordinal) ? Brush("#EA4335") : Brush("#FBBC04");
        }
    }

    public void ShowStatus(StatusUpdate update)
    {
        ShowMode(update.Mode);
        MessageText.Text = update.Message;
        MessageText.Foreground = update.Kind switch
        {
            StatusKind.Warning => Brush("#FDD663"),
            StatusKind.Error => Brush("#F28B82"),
            StatusKind.Success => Brush("#81C995"),
            _ => Brush("#E8EAED"),
        };
        MessageText.Visibility = string.IsNullOrEmpty(update.Message) ? Visibility.Collapsed : Visibility.Visible;
        HeardText.Text = string.IsNullOrEmpty(update.Heard) ? string.Empty : $"Heard: “{update.Heard}”";
        HeardText.Visibility = string.IsNullOrEmpty(update.Heard) ? Visibility.Collapsed : Visibility.Visible;
        _fade.Stop();
        _fade.Start();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - 16;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
