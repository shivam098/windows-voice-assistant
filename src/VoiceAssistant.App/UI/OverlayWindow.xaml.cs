using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

/// <summary>
/// Status pill that never takes keyboard focus from the app being dictated into. Minimized, it hides while idle
/// and reappears for the wake word, dictation, commands, and messages.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly bool _commandModeIsIdle;
    private InteractionMode _mode = InteractionMode.Paused;
    private string? _activity;
    private bool _minimized;

    /// <param name="commandModeIsIdle">True when there is no wake word, so "listening for a command" is the resting state.</param>
    public OverlayWindow(bool commandModeIsIdle = false)
    {
        InitializeComponent();
        _commandModeIsIdle = commandModeIsIdle;
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            MessageText.Visibility = Visibility.Collapsed;
            HeardText.Visibility = Visibility.Collapsed;
            PlaceText.Visibility = Visibility.Collapsed;
            UpdateVisibility();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => Reposition();
        MinimizeButton.Click += (_, _) =>
        {
            Minimized = true;
            if (IsVisible)
            {
                Hide();
            }
        };
    }

    /// <summary>Raised when <see cref="Minimized"/> changes (from the pill's button or the tray menu).</summary>
    public event Action<bool>? MinimizedChanged;

    public bool Minimized
    {
        get => _minimized;
        set
        {
            if (_minimized == value)
            {
                return;
            }

            _minimized = value;
            UpdateVisibility();
            MinimizedChanged?.Invoke(value);
        }
    }

    public void ShowMode(InteractionMode mode, string? activity = null)
    {
        _mode = mode;
        _activity = activity;
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
            Dot.Fill = IsDictating(activity) ? Brush("#EA4335") : Brush("#FBBC04");
        }

        UpdateVisibility();
    }

    public void ShowStatus(StatusUpdate update)
    {
        // A recording stays on screen until it ends, even if a status (e.g. the wake window expiring) arrives.
        ShowMode(update.Mode, IsDictating(_activity) ? _activity : null);
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

        // The place shows with the message that put us there, so it is never stale.
        PlaceText.Text = string.IsNullOrEmpty(update.Place) ? string.Empty : "📍 " + update.Place;
        PlaceText.Visibility = string.IsNullOrEmpty(update.Place) ? Visibility.Collapsed : Visibility.Visible;

        _fade.Stop();
        _fade.Start();
        UpdateVisibility();
    }

    /// <summary>Shows the "HI + EN" badge while dictation uses Hindi + English (“HI + EN · accurate” for the larger model).</summary>
    public void SetDictationLanguage(bool hinglish, bool accurate)
    {
        LanguageBadge.Visibility = hinglish ? Visibility.Visible : Visibility.Collapsed;
        LanguageBadgeText.Text = accurate ? "HI + EN · accurate" : "HI + EN";
    }

    /// <summary>Shows the live transcript while dictation records; null hides it. Long text keeps its latest words.</summary>
    public void ShowPreview(string? text)
    {
        const int MaxChars = 150;
        if (string.IsNullOrEmpty(text))
        {
            LiveText.Text = string.Empty;
            LiveText.Visibility = Visibility.Collapsed;
            UpdateVisibility();
            return;
        }

        if (text.Length > MaxChars)
        {
            var start = text.Length - MaxChars;
            var space = text.IndexOf(' ', start);
            text = "…" + text[(space > 0 && space < text.Length - 1 ? space + 1 : start)..];
        }

        LiveText.Text = text;
        LiveText.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
        HeardText.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    /// <summary>Always shown unless minimized; minimized, shown only while there is activity or a message.</summary>
    private void UpdateVisibility()
    {
        var busy = _mode == InteractionMode.Dictation
            || (_mode == InteractionMode.Command && !_commandModeIsIdle)
            || _activity is not null
            || LiveText.Visibility == Visibility.Visible
            || MessageText.Visibility == Visibility.Visible
            || HeardText.Visibility == Visibility.Visible;
        if (!_minimized || busy)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - 16;
    }

    private static bool IsDictating(string? activity) => activity?.StartsWith("Dictating", StringComparison.Ordinal) == true;

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
