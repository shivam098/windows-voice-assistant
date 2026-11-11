using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoiceAssistant.Board;

namespace VoiceAssistant.App.UI;

public enum AppPage
{
    Home,
    Ideas,
    Settings,
    Help,
}

/// <summary>
/// The app window: Home, Idea board, Settings, Help. Closing it hides it (the assistant keeps running in the tray);
/// Exit in the tray quits.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly Dictionary<AppPage, (System.Windows.Controls.Button Button, UIElement Page)> _pages = [];
    private readonly ContentControl _content = new();
    private readonly IdeasPage _ideas;
    private bool _exiting;
    private bool _toldAboutTray;

    public MainWindow(AssistantHost host, AppSettings settings, BoardStore board, Action restart, Action<string> balloon)
    {
        Title = "Voice Assistant";
        Width = 1180;
        Height = 760;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Background;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        _ideas = new IdeasPage(board);
        var home = new HomePage(host, board, list => ShowBoard(list));
        var settingsPage = new SettingsPage(settings, host, restart);
        var help = new HelpPage();

        var nav = new StackPanel { Margin = new Thickness(12, 16, 12, 12) };
        var brand = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 20) };
        brand.Children.Add(new System.Windows.Controls.Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo-64.png")), Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "Voice Assistant", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        nav.Children.Add(brand);
        foreach (var (page, label, element) in new (AppPage, string, UIElement)[]
        {
            (AppPage.Home, "🏠  Home", home),
            (AppPage.Ideas, "💡  Idea board", _ideas),
            (AppPage.Settings, "⚙  Settings", settingsPage),
            (AppPage.Help, "❔  What you can say", help),
        })
        {
            var button = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            Theme.Animate(button, NavRest, Theme.Color("#E0E0E0"), Theme.Color("#D6D6D6"));
            button.Click += (_, _) => ShowPage(page);
            nav.Children.Add(button);
            _pages[page] = (button, element);
        }

        var privacy = Theme.Label("Runs on this PC. Speech, notes and settings never leave it.", 11, Theme.Muted);
        privacy.Margin = new Thickness(8, 24, 8, 0);
        nav.Children.Add(privacy);

        var layout = new DockPanel();
        var sidebar = new Border { Background = Theme.Sidebar, Width = 230, Child = nav };
        DockPanel.SetDock(sidebar, Dock.Left);
        layout.Children.Add(sidebar);
        layout.Children.Add(_content);
        Content = layout;
        UseLayoutRounding = true;

        // Fade the window's contents in each time it opens (from the tray, a second launch, or a voice command).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Motion.FadeIn(layout, 200);
            }
        };
        Closing += (_, e) => OnClosing(e, balloon);
        ShowPage(AppPage.Home);
    }

    /// <summary>Lets the window really close (app exit).</summary>
    public void AllowClose() => _exiting = true;

    public void ShowPage(AppPage page)
    {
        foreach (var (key, (button, _)) in _pages)
        {
            Theme.SetRestColor(button, key == page ? NavSelected : NavRest);
            button.FontWeight = key == page ? FontWeights.SemiBold : FontWeights.Normal;
        }

        var element = _pages[page].Page;
        if (!ReferenceEquals(_content.Content, element))
        {
            _content.Content = element;
            Motion.FadeIn(element, 180, rise: 10);
        }
    }

    private static readonly System.Windows.Media.Color NavRest = System.Windows.Media.Color.FromArgb(0, 0xEB, 0xEB, 0xEB);
    private static readonly System.Windows.Media.Color NavSelected = Theme.Color("#ECEAFD");

    /// <summary>Opens the window on the idea board, highlighting a list.</summary>
    public void ShowBoard(BoardList? list)
    {
        Bring();
        ShowPage(AppPage.Ideas);
        if (list is { } l)
        {
            Dispatcher.BeginInvoke(() => _ideas.Highlight(l), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Shows and brings the window forward (restoring it if minimised).</summary>
    public void Bring()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // A voice command or a second launch isn't input to this window, so Windows may refuse a plain Activate.
        if (!Activate())
        {
            VoiceAssistant.App.Platform.WindowsDesktop.TrySetForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        Focus();
    }

    private void OnClosing(CancelEventArgs e, Action<string> balloon)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            balloon("Voice Assistant is still running in the tray. Right-click its icon › Exit to quit.");
        }
    }
}
