using System.Diagnostics;
using System.Windows.Automation;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.Platform;

internal sealed class WindowsDesktop : IDesktop
{
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(4);
    private readonly int _ownProcessId = Environment.ProcessId;

    public WindowRef? GetForegroundWindow()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != 0)
        {
            hwnd = root;
        }

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownProcessId)
        {
            return null;
        }

        return new WindowRef(hwnd, (int)pid, ProcessName((int)pid), Win32.GetWindowTitle(hwnd));
    }

    public async Task<DesktopResult> OpenOrActivateAsync(AppEntry app, CancellationToken cancellationToken)
    {
        if (app.ProcessNames.Count > 0 && FindAppWindow(app) is { } existing)
        {
            return await ActivateAsync(existing, app.Name, cancellationToken).ConfigureAwait(false);
        }

        var before = GetForegroundWindow();
        try
        {
            if (app.Launch.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{app.Launch}\"") { UseShellExecute = false })?.Dispose();
            }
            else
            {
                Process.Start(new ProcessStartInfo(app.Launch) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"launch failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return DesktopResult.Fail($"Couldn't start {app.Name}.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (app.ProcessNames.Count > 0)
            {
                if (FindAppWindow(app) is { } started)
                {
                    return await ActivateAsync(started, app.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (GetForegroundWindow() is { } now && !now.SameWindow(before))
            {
                // Some window came to the front, but we can't prove it belongs to the app: report without a window.
                return DesktopResult.Ok($"Opened {app.Name}.");
            }
        }

        return app.ProcessNames.Count > 0
            ? DesktopResult.Fail($"{app.Name} is starting, but its window didn't appear.")
            : DesktopResult.Ok($"Asked Windows to open {app.Name}.");
    }

    public async Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken)
    {
        if (Win32.IsProcessElevated(window.ProcessId) == true)
        {
            return new TextTargetResult(false, "That window runs as administrator; Windows won't let me type into it.");
        }

        try
        {
            return await Task.Run(() => PrepareTextTarget(window, profile), cancellationToken).WaitAsync(UiaTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("uia prepare timed out");
            var name = profile?.Name ?? (string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title);
            return new TextTargetResult(false, $"{name} isn't responding, so I can't check where I'd be typing. Try again in a moment.");
        }
    }

    public FieldCheck CheckFocusedField()
    {
        try
        {
            var check = Task.Run(() => Check(Focused()));
            return check.Wait(800) ? check.Result : FieldCheck.Unknown;
        }
        catch (AggregateException)
        {
            return FieldCheck.Unknown;
        }
    }

    public void TypeText(string text) => KeyboardInput.TypeUnicode(text.Replace("\r\n", " ").Replace('\n', ' '));

    public void PressKeys(string chord) => KeyboardInput.PressChord(chord);

    public void Backspace(int count)
    {
        if (count > 0)
        {
            KeyboardInput.Repeat(0x08, count);
        }
    }

    private static TextTargetResult PrepareTextTarget(WindowRef window, AppEntry? profile)
    {
        var focused = Focused();
        if (profile?.FocusComposer == true && !IsEditable(focused))
        {
            var composer = FindComposer(window.Handle);
            if (composer is null)
            {
                // Chromium builds its accessibility tree on first request; give it a moment.
                Thread.Sleep(400);
                composer = FindComposer(window.Handle);
            }

            if (composer is null)
            {
                return new TextTargetResult(false, $"I couldn't find the message box in {profile.Name}. Click it once, then say “start typing”.");
            }

            try
            {
                composer.SetFocus();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
            {
                Log.Warn($"composer focus failed: {ex.GetType().Name}");
            }

            Thread.Sleep(150);
            focused = Focused();
            if (!IsEditable(focused))
            {
                return new TextTargetResult(false, $"I couldn't focus the message box in {profile.Name}. Click it once, then say “start typing”.");
            }
        }

        switch (Check(focused))
        {
            case FieldCheck.Password:
                return new TextTargetResult(false, "That's a password field — I won't type there.");
            case FieldCheck.Unknown:
                return new TextTargetResult(false, "I couldn't check the focused field. Click into the text box, then say “start typing”.");
        }

        return new TextTargetResult(true, string.Empty, HasTrailingText(focused));
    }

    private static FieldCheck Check(AutomationElement? element)
    {
        if (element is null)
        {
            return FieldCheck.Unknown;
        }

        try
        {
            return element.Current.IsPassword ? FieldCheck.Password : FieldCheck.Safe;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return FieldCheck.Unknown;
        }
    }

    private static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsEditable(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        return SafeGet(() =>
        {
            var current = element.Current;
            if (current.ControlType == ControlType.Edit)
            {
                return true;
            }

            return current.ControlType == ControlType.Document &&
                   element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                   !((ValuePattern)pattern).Current.IsReadOnly;
        });
    }

    /// <summary>Chat apps put the message box at the bottom: pick the lowest visible, enabled, focusable edit control.</summary>
    private static AutomationElement? FindComposer(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new PropertyCondition(AutomationElement.IsPasswordProperty, false));
            return root.FindAll(TreeScope.Descendants, condition)
                .Cast<AutomationElement>()
                .Select(e => (Element: e, Rect: SafeGet(() => e.Current.BoundingRectangle)))
                .Where(x => !x.Rect.IsEmpty && x.Rect.Width >= 120)
                .OrderByDescending(x => x.Rect.Bottom)
                .Select(x => x.Element)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"composer search failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static bool? HasTrailingText(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return SafeGet<bool?>(() =>
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = ((ValuePattern)pattern).Current.Value;
                return value.Length > 0 && !char.IsWhiteSpace(value[^1]);
            }

            return null;
        });
    }

    private static T SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return default!;
        }
    }

    private async Task<DesktopResult> ActivateAsync(WindowRef window, string appName, CancellationToken ct)
    {
        if (Win32.IsIconic(window.Handle))
        {
            Win32.ShowWindow(window.Handle, Win32.SW_RESTORE);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            TrySetForeground(window.Handle);
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                if (window.SameWindow(GetForegroundWindow()))
                {
                    return DesktopResult.Ok($"{appName} is ready.", window);
                }
            }
        }

        return DesktopResult.Fail($"Windows didn't let me bring {appName} to the front. Click it once, then say “start typing”.");
    }

    private static void TrySetForeground(nint hwnd)
    {
        if (Win32.SetForegroundWindow(hwnd))
        {
            return;
        }

        // Windows only lets the process that produced the last input change the foreground window.
        KeyboardInput.TapUnassignedKey();
        if (Win32.SetForegroundWindow(hwnd))
        {
            return;
        }

        var foregroundThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
        var thisThread = Win32.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == thisThread)
        {
            return;
        }

        if (Win32.AttachThreadInput(thisThread, foregroundThread, true))
        {
            try
            {
                Win32.BringWindowToTop(hwnd);
                Win32.SetForegroundWindow(hwnd);
            }
            finally
            {
                Win32.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    private WindowRef? FindAppWindow(AppEntry app)
    {
        WindowRef? found = null;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0 || Win32.IsCloaked(hwnd))
            {
                return true;
            }

            if ((Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            var title = Win32.GetWindowTitle(hwnd);
            if (title.Length == 0 || (app.WindowTitleContains is { } must && !title.Contains(must, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == _ownProcessId)
            {
                return true;
            }

            var name = ProcessName((int)pid);
            if (app.ProcessNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                found = new WindowRef(hwnd, (int)pid, name, title);
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
