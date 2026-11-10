using System.Drawing;
using System.Windows.Forms;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<InteractionMode, (Icon Icon, nint Handle)> _icons = [];
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _startAtSignIn;
    private readonly string _hotkey;

    public TrayIcon(AssistantHost host, string hotkey, string pasteHotkey, string languageHotkey, OverlayWindow overlay, Action<AppPage> open, Action exit)
    {
        _hotkey = hotkey;
        _icons[InteractionMode.Paused] = MakeIcon(Color.FromArgb(154, 160, 166));
        _icons[InteractionMode.Standby] = MakeIcon(null);
        _icons[InteractionMode.Command] = MakeIcon(Color.FromArgb(76, 141, 246));
        _icons[InteractionMode.Dictation] = MakeIcon(Color.FromArgb(234, 67, 53));

        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggle = new ToolStripMenuItem($"Start listening\t{hotkey}", null, (_, _) => host.ToggleListening());
        _startAtSignIn = new ToolStripMenuItem("Start when I sign in", null, (_, _) => StartupRegistration.Set(!StartupRegistration.IsEnabled));
        var showPill = new ToolStripMenuItem("Always show status pill", null, (_, _) => overlay.Minimized = !overlay.Minimized);
        var english = new ToolStripMenuItem("English", null, (_, _) => host.SetDictationLanguage(hinglish: false));
        var hinglish = new ToolStripMenuItem("Hindi + English — fast (typed in Roman letters)", null, (_, _) => host.SetDictationLanguage(hinglish: true, accurate: false));
        var hinglishAccurate = new ToolStripMenuItem("Hindi + English — accurate (slower; 830 MB download)", null, (_, _) => host.SetDictationLanguage(hinglish: true, accurate: true));

        // The hotkey switches between English and whichever Hindi + English mode was chosen last.
        var language = new ToolStripMenuItem($"Dictation language\t{languageHotkey}");
        language.DropDownItems.AddRange([english, hinglish, hinglishAccurate]);

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            showPill.Checked = !overlay.Minimized;
            _startAtSignIn.Checked = StartupRegistration.IsEnabled;
            english.Checked = !host.HinglishDictation;
            hinglish.Checked = host.HinglishDictation && !host.HinglishAccurate;
            hinglishAccurate.Checked = host.HinglishDictation && host.HinglishAccurate;
        };
        var openApp = new ToolStripMenuItem("Open Voice Assistant", null, (_, _) => open(AppPage.Home));
        openApp.Font = new Font(openApp.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(openApp);
        menu.Items.Add(new ToolStripMenuItem("Idea board", null, (_, _) => open(AppPage.Ideas)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_status);
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripMenuItem($"Paste last dictation\t{pasteHotkey}", null, (_, _) => host.PasteLastTranscript()));
        menu.Items.Add(language);
        menu.Items.Add(new ToolStripMenuItem("Copy text that wasn't typed", null, (_, _) => host.CopyUntypedText()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("What you can say", null, (_, _) => open(AppPage.Help)));
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => open(AppPage.Settings)));
        menu.Items.Add(showPill);
        menu.Items.Add(_startAtSignIn);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new NotifyIcon
        {
            Icon = _icons[InteractionMode.Paused].Icon,
            Text = "Voice Assistant",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open(AppPage.Home);
            }
        };
    }

    public void Update(InteractionMode mode)
    {
        _icon.Icon = _icons[mode].Icon;
        var label = mode switch
        {
            InteractionMode.Dictation => "Typing",
            InteractionMode.Command => "Listening for commands",
            InteractionMode.Standby => "Waiting for “Hey Jarvis”",
            _ => "Paused (microphone off)",
        };
        _status.Text = label;
        _icon.Text = $"Voice Assistant — {label}";
        _toggle.Text = (mode == InteractionMode.Paused ? "Start listening" : "Pause listening") + $"\t{_hotkey}";
    }

    public void ShowBalloon(string message, bool warning = true) =>
        _icon.ShowBalloonTip(4000, "Voice Assistant", message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var (icon, handle) in _icons.Values)
        {
            icon.Dispose();
            Win32.DestroyIcon(handle);
        }
    }

    /// <summary>The app logo with a status dot in the corner (no dot while waiting for the wake word).</summary>
    private static (Icon, nint) MakeIcon(Color? dot)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        using var logo = new Icon(stream, 32, 32);
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(logo, new Rectangle(0, 0, 32, 32));
            if (dot is { } color)
            {
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, 17, 17, 15, 15);
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, 19, 19, 11, 11);
            }
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }
}
