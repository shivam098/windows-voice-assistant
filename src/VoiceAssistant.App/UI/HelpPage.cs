using System.Windows;
using System.Windows.Controls;

namespace VoiceAssistant.App.UI;

/// <summary>Everything you can say, with a search box (“remind”, “tab”, “delete”).</summary>
internal sealed class HelpPage : UserControl
{
    private readonly StackPanel _sections = new();

    public HelpPage()
    {
        var root = new DockPanel { Margin = new Thickness(24) };
        var top = new StackPanel();
        top.Children.Add(Theme.Heading("What you can say"));
        var search = new System.Windows.Controls.TextBox { Width = 360, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, ToolTip = "Filter, e.g. “remind” or “tab”" };
        search.TextChanged += (_, _) => Show(search.Text.Trim());
        top.Children.Add(search);
        top.Children.Add(new Border { Height = 12 });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Background = Theme.Background;
        Show(string.Empty);
    }

    private void Show(string filter)
    {
        _sections.Children.Clear();
        foreach (var (title, lines) in HelpText.Sections)
        {
            var matches = filter.Length == 0 || title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                ? lines
                : lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var line in matches)
            {
                var text = Theme.Label("•  " + line, 13);
                text.Margin = new Thickness(0, 0, 0, 4);
                panel.Children.Add(text);
            }

            _sections.Children.Add(Theme.CardOf(panel));
        }

        if (_sections.Children.Count == 0)
        {
            _sections.Children.Add(Theme.Label("Nothing matches. Try a single word like “tab” or “remind”.", 13, Theme.Muted));
        }
    }
}
