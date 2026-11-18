using System.Diagnostics;
using System.Windows.Automation;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.Platform;

/// <summary>
/// What the AI brain needs from Windows: the open windows front to back, bringing one to the front, and opening web
/// pages in the default browser. Logs outcomes only, never titles or addresses.
/// </summary>
internal sealed partial class WindowsDesktop
{
    private const int MaxListedWindows = 12;

    public Task<IReadOnlyList<DesktopWindow>> ListWindowsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DesktopWindow>>(ListWindows());

    public Task<DesktopResult> ActivateWindowAsync(WindowRef window, CancellationToken cancellationToken)
    {
        // The title is shown to the user but never logged.
        var name = window.Title.Length is > 0 and <= 40 ? $"“{window.Title}”" : "that window";
        return ActivateAsync(window, name, cancellationToken, logName: "a window");
    }

    public FocusInfo? DescribeFocus()
    {
        try
        {
            var read = Task.Run(() => SafeGet(() => Focused() is { } focused
                ? new FocusInfo(
                    focused.Current.Name ?? string.Empty,
                    IsEditable(focused),
                    InPage(focused),

                    // Every Chromium browser's address bar (Chrome's, Edge's, BraveOmniboxViewViews…), whatever its process.
                    AddressBar: (focused.Current.ClassName ?? string.Empty).EndsWith("OmniboxViewViews", StringComparison.Ordinal))
                : null));
            return read.Wait(600) ? read.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// Part of a web page: the element or a parent is a document. A browser's address, search and find bars are not, in
    /// any language.
    /// </summary>
    private static bool InPage(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; current is not null && depth < 60; depth++)
        {
            if (current.Current.ControlType == ControlType.Document)
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    public async Task<DesktopResult> OpenUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return DesktopResult.Fail("I only open web links.");
        }

        var before = BrowserWindows().ToDictionary(w => w.Handle, w => w.Title);

        // The browser may only take the front if this process could: make it the last input source, then pass the right on.
        KeyboardInput.TapUnassignedKey();
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);

        // "Open Instagram in a new tab" with Chrome in front means a tab in Chrome, whatever the default browser is.
        if (!OpenInBrowserInFront(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn($"open url failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                return DesktopResult.Fail("Couldn't open the web browser.");
            }
        }

        // The page is shown when a browser window appears or its title (the tab's) changes.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var changed = BrowserWindows().FirstOrDefault(w => !before.TryGetValue(w.Handle, out var title) || title != w.Title);
            if (changed is null)
            {
                continue;
            }

            if (!changed.SameWindow(GetForegroundWindow()))
            {
                var activated = await ActivateAsync(changed, "the browser", cancellationToken).ConfigureAwait(false);
                if (!activated.Success)
                {
                    Log.Info("open url: opened, browser not in front");
                    return DesktopResult.Ok("Opened the page in the browser.");
                }
            }

            // Let the page build its first elements before anything is clicked on it.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            Log.Info("open url: browser in front");
            return DesktopResult.Ok("Opened the page.", GetForegroundWindow() ?? changed);
        }

        Log.Info("open url: browser window not seen");
        return DesktopResult.Ok("Asked the browser to open the page.");
    }

    /// <summary>
    /// Opens the page as a new tab of the browser in front, by starting that browser's own program with the address
    /// (a running browser hands it to its open window). False when no browser is in front or it can't be started that way
    /// (run as administrator, a Store app's protected folder): the default browser opens it instead.
    /// </summary>
    private bool OpenInBrowserInFront(Uri url)
    {
        if (GetForegroundWindow() is not { } front || !BrainSafety.IsBrowser(front) || Win32.IsProcessElevated(front.ProcessId) == true ||
            Win32.ProcessImagePath(front.ProcessId) is not { } program || !File.Exists(program))
        {
            return false;
        }

        try
        {
            // One argument, an http(s) address checked by the caller, so it can't be read as a browser switch.
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            start.ArgumentList.Add(url.AbsoluteUri);
            Process.Start(start)?.Dispose();
            Log.Info("open url: in the browser in front");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"open url in the browser in front failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return false;
        }
    }

    /// <summary>Visible top-level app windows, front-most first (the active one first even under always-on-top windows).</summary>
    private List<DesktopWindow> ListWindows()
    {
        var found = new List<DesktopWindow>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0 || Win32.IsCloaked(hwnd) ||
                (Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0 ||
                Win32.GetClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }

            var title = Win32.GetWindowTitle(hwnd);
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (title.Trim().Length == 0 || pid == 0 || pid == _ownProcessId)
            {
                return true;
            }

            found.Add(Describe(new WindowRef(hwnd, (int)pid, ProcessName((int)pid), title)));
            return found.Count < MaxListedWindows;
        }, 0);

        if (GetForegroundWindow() is { } front && found.FindIndex(w => w.Window.SameWindow(front)) is > 0 and var index)
        {
            var active = found[index];
            found.RemoveAt(index);
            found.Insert(0, active);
        }

        return found;
    }

    private static DesktopWindow Describe(WindowRef window)
    {
        var placement = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        var show = !Win32.GetWindowPlacement(window.Handle, ref placement) ? WindowShow.Normal : placement.showCmd switch
        {
            Win32.SW_SHOWMINIMIZED => WindowShow.Minimized,
            Win32.SW_SHOWMAXIMIZED => WindowShow.Maximized,
            _ => WindowShow.Normal,
        };
        if (Win32.IsIconic(window.Handle))
        {
            show = WindowShow.Minimized;
        }

        ScreenRect? rect = Win32.GetWindowRect(window.Handle, out var r) ? new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        var monitor = Win32.MonitorFromWindow(window.Handle, Win32.MONITOR_DEFAULTTONEAREST);
        ScreenRect? work = monitor != 0 && Win32.GetMonitorInfo(monitor, ref info)
            ? new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top)
            : null;
        return new DesktopWindow(window, show, rect, work);
    }
}
