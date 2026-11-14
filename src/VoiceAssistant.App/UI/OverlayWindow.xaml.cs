using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private static readonly double[] BarWeights = [0.55, 0.8, 1.0, 0.8, 0.55];
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _dots = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Random _jitter = new();
    private readonly bool _commandModeIsIdle;
    private InteractionMode _mode = InteractionMode.Paused;
    private string? _activity;
    private bool _dictating;
    private double _level;
    private int _dotCount;
    private bool _minimized;
    private bool _hiding;

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
            HideSmoothly();
        };
        HintText.Text = DictationLabels.ListeningHint;
        _dots.Tick += (_, _) =>
        {
            _dotCount = (_dotCount + 1) % 4;
            ModeDots.Text = new string('.', _dotCount);
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

    /// <summary>
    /// Shows the mode, or <paramref name="activity"/> instead when there is one. During Ctrl+Win dictation,
    /// <see cref="DictationLabels.Listening"/> shows the level meter and how to finish, then
    /// <see cref="DictationLabels.Transcribing"/> animates until the text is typed.
    /// </summary>
    public void ShowMode(InteractionMode mode, string? activity = null)
    {
        if (_dictating && activity is not (null or DictationLabels.Listening or DictationLabels.Transcribing))
        {
            // A Ctrl+Win dictation owns the pill until its text is typed; voice activity must not hide its meter.
            activity = _activity;
        }

        var wasListening = _activity == DictationLabels.Listening;
        var wasDictationTranscribing = _dictating && _activity == DictationLabels.Transcribing;
        _mode = mode;
        _activity = activity;
        var listening = activity == DictationLabels.Listening;
        var transcribing = activity == DictationLabels.Transcribing;

        // Only the Transcribing that follows a dictation recording belongs to it (commands are transcribed too).
        _dictating = listening || (transcribing && _dictating);
        LevelMeter.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        Dot.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;
        HintText.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        if (listening && !wasListening)
        {
            _level = 0;
            SetBars(0);
        }

        (ModeText.Text, Dot.Fill) = mode switch
        {
            InteractionMode.Dictation => ("Typing — say “stop typing” when done", Brush("#EA4335")),
            InteractionMode.Command => ("Listening for a command", Brush("#4C8DF6")),
            InteractionMode.Standby => ("Say “Hey Jarvis” · tap Ctrl+Win to dictate", Brush("#8AB4F8")),
            _ => ("Paused · tap Ctrl+Win to dictate", Brush("#9AA0A6")),
        };

        if (activity is not null)
        {
            ModeText.Text = transcribing ? activity.TrimEnd('…') : activity;
            Dot.Fill = listening ? Brush("#EA4335") : Brush("#FBBC04");
        }

        SetTranscribing(transcribing);
        if (wasDictationTranscribing && !_dictating)
        {
            // The words heard while recording stayed on screen during transcription; the typed text replaces them.
            LiveText.Text = string.Empty;
            LiveText.Visibility = Visibility.Collapsed;
        }

        UpdateVisibility();
    }

    /// <summary>Moves the level meter while Ctrl+Win dictation records (<paramref name="level"/> from 0 to 1).</summary>
    public void ShowInputLevel(float level)
    {
        if (_activity != DictationLabels.Listening)
        {
            return;
        }

        // Rise at once, fall back gently, like a VU meter.
        _level = level > _level ? level : (_level * 0.6) + (level * 0.4);
        SetBars(_level);
    }

    public void ShowStatus(StatusUpdate update)
    {
        // A recording stays on screen until it is typed, even if a status (e.g. the wake window expiring) arrives.
        ShowMode(update.Mode, _dictating ? _activity : null);
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
            if (_dictating && _activity == DictationLabels.Transcribing)
            {
                // Keep the last preview on screen until the transcription is typed.
                return;
            }

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
        LiveText.Opacity = _activity == DictationLabels.Transcribing ? 0.6 : 1;
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
            if (!IsVisible || _hiding)
            {
                _hiding = false;
                if (!IsVisible)
                {
                    Show();
                }

                Motion.FadeIn(this, 140);
            }
        }
        else
        {
            HideSmoothly();
        }
    }

    private void HideSmoothly()
    {
        if (!IsVisible || _hiding)
        {
            return;
        }

        _hiding = true;
        Motion.FadeOut(this, () =>
        {
            if (_hiding)
            {
                _hiding = false;
                Hide();
            }
        }, 220);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - ActualWidth) / 2);
        Top = area.Bottom - ActualHeight - 16;
    }

    private void SetBars(double level)
    {
        var bars = LevelMeter.Children;
        for (var i = 0; i < bars.Count && i < BarWeights.Length; i++)
        {
            var wobble = level > 0.05 ? 0.8 + (_jitter.NextDouble() * 0.4) : 1;
            ((FrameworkElement)bars[i]).Height = 4 + (14 * Math.Clamp(level * BarWeights[i] * wobble, 0, 1));
        }
    }

    private void SetTranscribing(bool transcribing)
    {
        ModeDots.Visibility = transcribing ? Visibility.Visible : Visibility.Collapsed;
        if (transcribing)
        {
            LiveText.Opacity = 0.6;
            if (!_dots.IsEnabled)
            {
                _dotCount = 0;
                ModeDots.Text = string.Empty;
                _dots.Start();
                Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else if (_dots.IsEnabled)
        {
            _dots.Stop();
            Dot.BeginAnimation(OpacityProperty, null);
            LiveText.Opacity = 1;
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
