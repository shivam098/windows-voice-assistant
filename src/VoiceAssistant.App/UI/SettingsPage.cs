using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VoiceAssistant.App.Platform;

namespace VoiceAssistant.App.UI;

/// <summary>
/// The settings file as a form. Saving re-reads %APPDATA%\VoiceAssistant\settings.json and changes only the fields
/// edited here, so hand edits to the file (apps, anything not on this page) survive. Most changes apply after a restart
/// (one click). “Start when I sign in” applies at once.
/// </summary>
internal sealed class SettingsPage : UserControl
{
    private readonly AppSettings _settings;
    private readonly Action _restart;
    private readonly AssistantHost _host;
    private readonly List<Setting> _fields = [];
    private readonly TextBlock _saved = Theme.Label(string.Empty, 12, Theme.Muted);

    public SettingsPage(AppSettings settings, AssistantHost host, Action restart)
    {
        _settings = settings;
        _host = host;
        _restart = restart;
        var stack = new StackPanel { Margin = new Thickness(24), MaxWidth = 760, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        stack.Children.Add(Theme.Heading("Settings"));

        stack.Children.Add(Section("General",
            StartAtSignIn(),
            Check("Start listening for “Hey Jarvis” when the app starts", settings.ListenOnStartup, (s, v) => s.ListenOnStartup = v),
            Check("Short sounds when listening starts and stops", settings.SoundFeedback, (s, v) => s.SoundFeedback = v),
            Check("Learn my rephrasings and favourite apps", settings.LearnFromUsage, (s, v) => s.LearnFromUsage = v)));

        stack.Children.Add(Section("Dictation",
            Check("Hold Ctrl+Win to dictate into any app", settings.PushToTalk, (s, v) => s.PushToTalk = v),
            Check("Show my words in the status pill while I speak", settings.LivePreview, (s, v) => s.LivePreview = v),
            Check("Remove filler words (“um”, “basically”, “thik hai na”, “haan to like”…)", settings.RemoveFillerWords, (s, v) => s.RemoveFillerWords = v),
            Check("Say punctuation (“comma”, “new line”)", settings.SpokenPunctuation, (s, v) => s.SpokenPunctuation = v),
            Choice("How text is inserted", ["Paste", "Type"], settings.InsertMethod, (s, v) => s.InsertMethod = v,
                "Paste works everywhere (your clipboard is restored); Type sends keystrokes."),
            Number("Pause that ends a phrase while voice typing (ms)", settings.DictationEndSilenceMs, 600, 3000, (s, v) => s.DictationEndSilenceMs = v,
                "Longer keeps sentences together when you pause to think; commands wait this long too."),
            Lines("Words I use often (one per line)", settings.DictationVocabulary ?? [], (s, v) => s.DictationVocabulary = v,
                "Helps the accurate Hindi + English model spell names and jargon."),
            Pairs("Personal dictionary (heard = typed, one per line)", settings.Replacements, (s, v) => s.Replacements = v,
                "e.g. git hub = GitHub"),
            Pairs("Snippets (say = type, one per line; \\n for a new line, \\\\ for a backslash)", settings.Snippets, (s, v) => s.Snippets = v,
                "e.g. my signature = Thanks,\\nShivam")));

        stack.Children.Add(Section("Voice commands",
            Check("Commands need “Hey Jarvis” first", settings.WakeWordEnabled, (s, v) => s.WakeWordEnabled = v),
            Number("Keep listening for commands after “Hey Jarvis” (seconds)", settings.AwakeSeconds, 2, 120, (s, v) => s.AwakeSeconds = v, null),
            Text("Pause / resume listening hotkey", settings.Hotkey, (s, v) => s.Hotkey = v),
            Text("Paste last dictation hotkey", settings.PasteLastHotkey, (s, v) => s.PasteLastHotkey = v),
            Text("Switch dictation language hotkey", settings.DictationLanguageHotkey, (s, v) => s.DictationLanguageHotkey = v)));

        stack.Children.Add(Section("Microphone", Microphone()));

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        buttons.Children.Add(Theme.Button("Save and restart", (_, _) =>
        {
            if (Save())
            {
                _restart();
            }
        }, primary: true));
        buttons.Children.Add(Theme.Button("Save", (_, _) => Save()));
        buttons.Children.Add(Theme.Button("Open settings file", (_, _) => OpenFile(AppPaths.SettingsFile)));
        buttons.Children.Add(Theme.Button("Open logs folder", (_, _) => OpenFile(AppPaths.LogsDir)));
        buttons.Children.Add(Theme.Button("Forget what I've learned", (_, _) => _host.ForgetLearned()));
        stack.Children.Add(buttons);
        stack.Children.Add(Theme.Label("Saving changes only the fields you edited here; other entries in the file (such as apps) are kept. Comments in the file are removed.", 12, Theme.Muted));
        stack.Children.Add(_saved);

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Motion.SmoothScroll(scroll);
        Content = scroll;
        Background = Theme.Background;
    }

    /// <summary>A setting on the page: its value as shown now, as last saved, and how to write it.</summary>
    private sealed class Setting(Func<string> current, Action<AppSettings> apply)
    {
        public Func<string> Current { get; } = current;
        public Action<AppSettings> Apply { get; } = apply;
        public string Saved { get; set; } = current();
    }

    private bool Save()
    {
        var changed = _fields.Where(f => f.Current() != f.Saved).ToList();
        if (changed.Count == 0)
        {
            Show("Nothing to save.", Theme.Muted);
            return true;
        }

        var file = AppSettings.LoadForEdit(out var error);
        if (file is null)
        {
            Show($"Not saved: {error}. Fix it with Open settings file, or delete the file to start over.", Theme.Danger);
            return false;
        }

        try
        {
            foreach (var field in changed)
            {
                field.Apply(file);
            }

            file.Save();
        }
        catch (FormatException ex)
        {
            Show("Not saved: " + ex.Message, Theme.Danger);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show("Couldn't save: " + ex.Message, Theme.Danger);
            return false;
        }

        foreach (var field in changed)
        {
            field.Saved = field.Current();
        }

        Show($"Saved at {DateTime.Now:HH:mm}. Restart to apply dictation, hotkey and microphone changes.", Theme.Muted);
        return true;
    }

    private void Show(string message, System.Windows.Media.Brush brush)
    {
        _saved.Text = message;
        _saved.Foreground = brush;
    }

    private static Border Section(string title, params UIElement[] rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var row in rows)
        {
            if (row is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 0, 0, 10);
            }

            panel.Children.Add(row);
        }

        return Theme.CardOf(panel);
    }

    /// <summary>A registry entry, not a setting: applied when clicked, and re-read whenever the page is shown.</summary>
    private UIElement StartAtSignIn()
    {
        var box = new System.Windows.Controls.CheckBox { Content = "Start when I sign in (in the tray, window closed)", IsChecked = StartupRegistration.IsEnabled, FontSize = 13 };
        box.Click += (_, _) =>
        {
            StartupRegistration.Set(box.IsChecked == true);
            box.IsChecked = StartupRegistration.IsEnabled;
        };
        IsVisibleChanged += (_, _) => box.IsChecked = StartupRegistration.IsEnabled;
        return box;
    }

    private UIElement Check(string label, bool value, Action<AppSettings, bool> set)
    {
        var box = new System.Windows.Controls.CheckBox { Content = label, IsChecked = value, FontSize = 13 };
        _fields.Add(new Setting(() => (box.IsChecked == true).ToString(), s => set(s, box.IsChecked == true)));
        return box;
    }

    private UIElement Choice(string label, string[] options, string value, Action<AppSettings, string> set, string? hint)
    {
        var combo = new System.Windows.Controls.ComboBox { Width = 160, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (var option in options)
        {
            combo.Items.Add(option);
        }

        combo.SelectedItem = options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        _fields.Add(new Setting(() => (string)combo.SelectedItem, s => set(s, (string)combo.SelectedItem)));
        return Field(label, combo, hint);
    }

    private UIElement Number(string label, int value, int min, int max, Action<AppSettings, int> set, string? hint)
    {
        var box = new System.Windows.Controls.TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _fields.Add(new Setting(() => box.Text.Trim(), s =>
        {
            if (!int.TryParse(box.Text.Trim(), out var n) || n < min || n > max)
            {
                throw new FormatException($"“{label}” must be a number from {min} to {max}.");
            }

            set(s, n);
        }));
        return Field(label, box, hint);
    }

    private UIElement Text(string label, string value, Action<AppSettings, string> set)
    {
        var box = new System.Windows.Controls.TextBox { Text = value, Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _fields.Add(new Setting(() => box.Text.Trim(), s => set(s, box.Text.Trim())));
        return Field(label, box, null);
    }

    private UIElement Lines(string label, IEnumerable<string> value, Action<AppSettings, List<string>> set, string? hint)
    {
        var box = MultiLine(string.Join(Environment.NewLine, value));
        List<string> Read() => box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _fields.Add(new Setting(() => string.Join('\n', Read()), s => set(s, Read())));
        return Field(label, box, hint);
    }

    private UIElement Pairs(string label, IDictionary<string, string> value, Action<AppSettings, Dictionary<string, string>> set, string? hint)
    {
        var box = MultiLine(string.Join(Environment.NewLine, value.Select(kv => $"{kv.Key} = {Escape(kv.Value)}")));
        string[] Rows() => box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        _fields.Add(new Setting(() => string.Join('\n', Rows()), s =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Rows())
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    throw new FormatException($"“{line}” in “{label}” needs the form: heard = typed.");
                }

                map[line[..eq].Trim()] = Unescape(line[(eq + 1)..].Trim());
            }

            set(s, map);
        }));
        return Field(label, box, hint);
    }

    private UIElement Microphone()
    {
        var combo = new System.Windows.Controls.ComboBox { Width = 360, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        combo.Items.Add("Windows default microphone");
        var count = NAudio.Wave.WaveInEvent.DeviceCount;
        for (var i = 0; i < count; i++)
        {
            combo.Items.Add(NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName);
        }

        combo.SelectedIndex = _settings.MicrophoneDevice >= 0 && _settings.MicrophoneDevice < count ? _settings.MicrophoneDevice + 1 : 0;
        _fields.Add(new Setting(() => combo.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), s => s.MicrophoneDevice = combo.SelectedIndex - 1));
        return Field("Recording device", combo, null);
    }

    /// <summary>One line per value: a backslash is shown as \\ and a line break as \n.</summary>
    internal static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    internal static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or 'n')
            {
                sb.Append(value[i + 1] == 'n' ? '\n' : '\\');
                i++;
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private static System.Windows.Controls.TextBox MultiLine(string text) => new()
    {
        Text = text,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 70,
        MaxHeight = 160,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Width = 460,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
    };

    private static UIElement Field(string label, UIElement input, string? hint)
    {
        var panel = new StackPanel();
        panel.Children.Add(Theme.Label(label, 13));
        if (input is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 4, 0, 2);
        }

        panel.Children.Add(input);
        if (hint is not null)
        {
            panel.Children.Add(Theme.Label(hint, 12, Theme.Muted));
        }

        return panel;
    }

    private void OpenFile(string path)
    {
        if (path == AppPaths.SettingsFile && !File.Exists(path))
        {
            _settings.Save();
        }

        Directory.CreateDirectory(path == AppPaths.LogsDir ? path : Path.GetDirectoryName(path)!);
        var start = path == AppPaths.SettingsFile ? new ProcessStartInfo("notepad.exe", $"\"{path}\"") : new ProcessStartInfo(path);
        start.UseShellExecute = true;
        Process.Start(start)?.Dispose();
    }
}
