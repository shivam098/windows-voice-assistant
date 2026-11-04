using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

/// <summary>
/// Click-through, never-focused layer over other apps: numbered badges for “show numbers” / ambiguous targets, and a
/// short outline on whatever was just clicked, selected or focused. Positions come in physical screen pixels.
/// </summary>
internal sealed class MarkerOverlay : Window
{
    private static readonly Brush BadgeFill = new SolidColorBrush(Color.FromRgb(0xFD, 0xD6, 0x63));
    private static readonly Brush BadgeInk = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
    private static readonly Brush FlashStroke = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));

    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private ScreenRect? _area;
    private IReadOnlyList<ChoiceBadge>? _badges;
    private ScreenRect? _flash;

    public MarkerOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
        };
        DpiChanged += (_, _) => Render();
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flash = null;
            Render();
        };
    }

    /// <summary>Shows numbers on the candidates (null hides them).</summary>
    public void ShowBadges(IReadOnlyList<ChoiceBadge>? badges)
    {
        _badges = badges is { Count: > 0 } ? badges : null;
        Render();
    }

    /// <summary>Outlines a rectangle for under a second.</summary>
    public void Flash(ScreenRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        _flash = rect;
        _flashTimer.Stop();
        _flashTimer.Start();
        Render();
    }

    private void Render()
    {
        var rects = (_badges?.Select(b => b.Rect) ?? []).Concat(_flash is null ? [] : [_flash]).ToList();
        if (rects.Count == 0)
        {
            _canvas.Children.Clear();
            Hide();
            return;
        }

        // Cover just the area in use, with room for badges that sit above/left of their element.
        const int Margin = 40;
        var left = rects.Min(r => r.X) - Margin;
        var top = rects.Min(r => r.Y) - Margin;
        var area = new ScreenRect(left, top, rects.Max(r => r.Right) + Margin - left, rects.Max(r => r.Bottom) + Margin - top);
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (_area != area)
        {
            _area = area;
            Win32.SetWindowPos(hwnd, 0, (int)area.X, (int)area.Y, (int)area.Width, (int)area.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double X(double px) => (px - area.X) / scale;
        double Y(double py) => (py - area.Y) / scale;

        _canvas.Children.Clear();
        if (_flash is { } flash)
        {
            var outline = new Border
            {
                Width = (flash.Width / scale) + 8,
                Height = (flash.Height / scale) + 8,
                BorderBrush = FlashStroke,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(6),
            };
            Canvas.SetLeft(outline, X(flash.X) - 4);
            Canvas.SetTop(outline, Y(flash.Y) - 4);
            _canvas.Children.Add(outline);
            outline.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(850)) { BeginTime = TimeSpan.FromMilliseconds(150) });
        }

        foreach (var badge in _badges ?? [])
        {
            var label = new Border
            {
                Background = BadgeFill,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 0, 5, 1),
                MinWidth = 20,
                BorderBrush = BadgeInk,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = badge.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Foreground = BadgeInk,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    TextAlignment = TextAlignment.Center,
                },
            };
            Canvas.SetLeft(label, X(badge.Rect.X) - 6);
            Canvas.SetTop(label, Y(badge.Rect.Y) - 6);
            _canvas.Children.Add(label);
        }
    }
}
