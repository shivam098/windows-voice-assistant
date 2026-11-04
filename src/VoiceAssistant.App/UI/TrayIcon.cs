using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.UI;

internal sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "VoiceAssistant";

    private readonly NotifyIcon _icon;
    private readonly Dictionary<InteractionMode, (Icon Icon, nint Handle)> _icons = [];
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _startAtSignIn;
    private readonly string _hotkey;

    public TrayIcon(AssistantHost host, string hotkey, Action exit)
    {
        _hotkey = hotkey;
        _icons[InteractionMode.Paused] = MakeIcon(Color.FromArgb(154, 160, 166));
        _icons[InteractionMode.Standby] = MakeIcon(null);
        _icons[InteractionMode.Command] = MakeIcon(Color.FromArgb(76, 141, 246));
        _icons[InteractionMode.Dictation] = MakeIcon(Color.FromArgb(234, 67, 53));

        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggle = new ToolStripMenuItem($"Start listening\t{hotkey}", null, (_, _) => host.ToggleListening());
        _startAtSignIn = new ToolStripMenuItem("Start when I sign in", null, (_, _) => ToggleStartAtSignIn()) { Checked = IsStartAtSignIn() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripMenuItem("Paste last dictation\tAlt+Shift+Z", null, (_, _) => host.PasteLastTranscript()));
        menu.Items.Add(new ToolStripMenuItem("Copy text that wasn't typed", null, (_, _) => host.CopyUntypedText()));
        menu.Items.Add(new ToolStripMenuItem("Voice commands…", null, (_, _) => ShowCommands()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startAtSignIn);
        menu.Items.Add(new ToolStripMenuItem("Open settings file", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => Open(AppPaths.LogsDir)));
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
                host.ToggleListening();
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

    public void ShowBalloon(string message) => _icon.ShowBalloonTip(4000, "Voice Assistant", message, ToolTipIcon.Warning);

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

    private static void ShowCommands() => System.Windows.MessageBox.Show(
        """
        DICTATE ANYWHERE (like Wispr Flow)
          Hold Ctrl+Win, speak, release → text is typed where your cursor is
          Ctrl+Win+Space → hands-free dictation; press again to finish
          Esc → cancel   ·   Alt+Shift+Z → paste last dictation
          Say “comma”, “question mark”, “new line”; “um/uh” are removed;
          “…scratch that…” drops the phrase before it.

        VOICE COMMANDS — start with “Hey Jarvis” (then 8 s without it)
          “Hey Jarvis, open Notepad”  ·  “…open Chrome”  ·  “…open GitHub Copilot”
          “Start typing”  (Notepad: document; Copilot: message box)
          “Go to the search box” / “Go to the address bar”
          “Search for weather in Bangalore”
          “New tab”, “Go back”, “Refresh”, “Copy”, “Paste”, “Undo”, “Save”
          “Press enter / tab / escape / down …”
          “Pause listening”

        WHILE TYPING (red) — pause briefly before each command
          “Send” / “Search”     “Scratch that”     “Clear box”
          “New line”            “Stop typing”      “Press tab”
        """,
        "Voice Assistant — commands");

    private static void OpenSettings()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            new AppSettings().Save();
        }

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.SettingsFile}\"") { UseShellExecute = true })?.Dispose();
        System.Windows.MessageBox.Show("Restart Voice Assistant after saving changes.", "Voice Assistant");
    }

    private static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private static bool IsStartAtSignIn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void ToggleStartAtSignIn()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartAtSignIn())
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }

        _startAtSignIn.Checked = IsStartAtSignIn();
    }
}
