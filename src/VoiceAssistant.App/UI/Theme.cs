using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;

namespace VoiceAssistant.App.UI;

/// <summary>Shared look for the app window: colours, cards, headings and buttons (light, Windows 11-like).</summary>
internal static class Theme
{
    public static readonly WpfBrush Background = Hex("#F3F3F3");
    public static readonly WpfBrush Sidebar = Hex("#EBEBEB");
    public static readonly WpfBrush Card = Hex("#FFFFFF");
    public static readonly WpfBrush Border = Hex("#E0E0E0");
    public static readonly WpfBrush Text = Hex("#1B1B1B");
    public static readonly WpfBrush Muted = Hex("#5F6368");
    public static readonly WpfBrush Accent = Hex("#5B4BDB");
    public static readonly WpfBrush AccentSoft = Hex("#ECEAFD");
    public static readonly WpfBrush Danger = Hex("#C42B1C");

    public static readonly IReadOnlyDictionary<VoiceAssistant.Board.BoardList, WpfBrush> ListColours = new Dictionary<VoiceAssistant.Board.BoardList, WpfBrush>
    {
        [VoiceAssistant.Board.BoardList.Ideas] = Hex("#FFF4B8"),
        [VoiceAssistant.Board.BoardList.Brainstorm] = Hex("#D9F2FF"),
        [VoiceAssistant.Board.BoardList.Todo] = Hex("#DDF5E3"),
        [VoiceAssistant.Board.BoardList.Reminders] = Hex("#FFE1E1"),
    };

    public static string ListName(VoiceAssistant.Board.BoardList list) => list switch
    {
        VoiceAssistant.Board.BoardList.Ideas => "Ideas",
        VoiceAssistant.Board.BoardList.Brainstorm => "Brainstorm",
        VoiceAssistant.Board.BoardList.Todo => "To-do",
        _ => "Reminders",
    };

    public static SolidColorBrush Hex(string hex)
    {
        var brush = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static TextBlock Heading(string text, double size = 24) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Text,
        Margin = new Thickness(0, 0, 0, 12),
    };

    public static TextBlock Label(string text, double size = 13, WpfBrush? brush = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static Border CardOf(UIElement child, WpfBrush? background = null, Thickness? padding = null) => new()
    {
        Background = background ?? Card,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child,
    };

    public static System.Windows.Controls.Button Button(string text, RoutedEventHandler onClick, bool primary = false)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? Accent : Card,
            Foreground = primary ? Brushes.White : Text,
            BorderBrush = primary ? Accent : Border,
        };
        button.Click += onClick;
        return button;
    }
}
