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
        var normal = primary ? Color("#5B4BDB") : Color("#FFFFFF");
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = primary ? Brushes.White : Text,
            BorderBrush = primary ? Accent : Border,
        };
        Animate(button, normal, primary ? Color("#4C3DC7") : Color("#F3F3F3"), primary ? Color("#3F31B0") : Color("#E6E6E6"));
        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// A flat, rounded look (no default Windows chrome) whose background eases between its normal, hover and pressed
    /// colours. <paramref name="normal"/> can be changed later through <see cref="SetRestColor"/> (selected nav item).
    /// </summary>
    public static void Animate(System.Windows.Controls.Button button, WpfColor normal, WpfColor hover, WpfColor pressed)
    {
        var brush = Motion.Live(normal);
        button.Background = brush;
        button.Template = FlatButton;
        button.Tag = normal;
        WpfColor Rest() => button.Tag is WpfColor c ? c : normal;
        button.MouseEnter += (_, _) => Motion.ToColor(brush, hover);
        button.MouseLeave += (_, _) => Motion.ToColor(brush, Rest());
        button.PreviewMouseLeftButtonDown += (_, _) => Motion.ToColor(brush, pressed, 60);
        button.PreviewMouseLeftButtonUp += (_, _) => Motion.ToColor(brush, button.IsMouseOver ? hover : Rest(), 120);
    }

    /// <summary>Changes the colour a button rests at (and eases to it unless the mouse is over it).</summary>
    public static void SetRestColor(System.Windows.Controls.Button button, WpfColor color)
    {
        button.Tag = color;
        if (!button.IsMouseOver && button.Background is SolidColorBrush brush && !brush.IsFrozen)
        {
            Motion.ToColor(brush, color);
        }
    }

    public static WpfColor Color(string hex) => (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static readonly ControlTemplate FlatButton = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.5" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
}
