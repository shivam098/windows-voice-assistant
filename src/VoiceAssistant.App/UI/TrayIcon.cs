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
        _icons[InteractionMode.Command] = MakeIcon(Color.FromArgb(76, 141, 246));
        _icons[InteractionMode.Dictation] = MakeIcon(Color.FromArgb(234, 67, 53));

        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggle = new ToolStripMenuItem($"Start listening\t{hotkey}", null, (_, _) => host.ToggleListening());
        _startAtSignIn = new ToolStripMenuItem("Start when I sign in", null, (_, _) => ToggleStartAtSignIn()) { Checked = IsStartAtSignIn() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggle);
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

    private static (Icon, nint) MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 13, 8, 6, 11);
            g.FillEllipse(white, 13, 5, 6, 6);
            g.FillEllipse(white, 13, 16, 6, 6);
            using var pen = new Pen(Color.White, 2);
            g.DrawArc(pen, 9, 11, 14, 12, 0, 180);
            g.DrawLine(pen, 16, 23, 16, 27);
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static void ShowCommands() => System.Windows.MessageBox.Show(
        """
        Listening for a command (blue):
          “Open GitHub Copilot”  /  “Switch to Notepad”
          “Open GitHub Copilot and start typing”
          “Start typing”
          “Pause listening”

        Typing (red) — pause briefly before each command:
          “Send”  /  “Send it”  /  “Submit”
          “Scratch that”  (removes the last phrase)
          “Clear box”
          “New line”  /  “New paragraph”
          “Stop typing”  (back to commands, nothing sent)

        Anything else you say while typing is typed as text.
        Prefix with “Computer, …” to force a command.
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
