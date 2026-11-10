using System.Diagnostics;
using System.Windows.Automation;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.Platform;

internal sealed partial class WindowsDesktop : IDesktop
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

    public async Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken)
    {
        if (app.ProcessNames.Count > 0 && FindAppWindow(app) is { } existing)
        {
            var activated = await ActivateAsync(existing, app.Name, cancellationToken).ConfigureAwait(false);
            Log.Info($"open {app.Name}: existing window {(activated.Success ? "activated" : "activation failed")}");
            if (!activated.Success || !newWindow || app.NewWindowKeys is not { } newWindowKeys)
            {
                return activated;
            }

            // "Open Notepad" means a fresh window, not the document that's already open.
            var known = FindAppWindows(app).Select(w => w.Handle).ToHashSet();
            KeyboardInput.WaitForModifiersReleased();
            if (!existing.SameWindow(GetForegroundWindow()))
            {
                return DesktopResult.Fail($"Focus moved away from {app.Name}, so I didn't open a new window.");
            }

            KeyboardInput.PressChord(newWindowKeys);
            var fresh = await WaitForNewWindowAsync(app, known, cancellationToken).ConfigureAwait(false);
            Log.Info($"open {app.Name}: new window {(fresh is null ? "did not appear" : "opened")}");
            return fresh is null
                ? DesktopResult.Fail($"{app.Name} didn't open a new window. Say “switch to {app.Name}” to use the open one.")
                : DesktopResult.Ok($"Opened a new {app.Name} window.", fresh);
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
                    var activated = await ActivateAsync(started, app.Name, cancellationToken).ConfigureAwait(false);
                    Log.Info($"open {app.Name}: launched, {(activated.Success ? "activated" : "activation failed")}");
                    if (activated.Success && app.NewDocumentKeys is { } newDocumentKeys)
                    {
                        // Apps that restore previous documents on launch (Notepad) get a blank one to type into.
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        KeyboardInput.WaitForModifiersReleased();
                        if (started.SameWindow(GetForegroundWindow()))
                        {
                            KeyboardInput.PressChord(newDocumentKeys);
                            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    return activated;
                }
            }
            else if (GetForegroundWindow() is { } now && !now.SameWindow(before))
            {
                // Some window came to the front, but we can't prove it belongs to the app: report without a window.
                Log.Info($"open {app.Name}: launched (window not identifiable)");
                return DesktopResult.Ok($"Opened {app.Name}.");
            }
        }

        Log.Info($"open {app.Name}: launched, window not seen");
        return app.ProcessNames.Count > 0
            ? DesktopResult.Fail($"{app.Name} is starting, but its window didn't appear.")
            : DesktopResult.Ok($"Asked Windows to open {app.Name}.");
    }

    private async Task<WindowRef?> WaitForNewWindowAsync(AppEntry app, HashSet<nint> known, CancellationToken ct)
    {
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var fresh = FindAppWindows(app).FirstOrDefault(w => !known.Contains(w.Handle));
            if (fresh is null)
            {
                continue;
            }

            if (!fresh.SameWindow(GetForegroundWindow()))
            {
                var activated = await ActivateAsync(fresh, app.Name, ct).ConfigureAwait(false);
                if (!activated.Success)
                {
                    return null;
                }
            }

            return fresh;
        }

        return null;
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
        // The desktop and the taskbar belong to Explorer; nothing there takes text except renaming an icon.
        var hwnd = Win32.GetForegroundWindow();
        var top = hwnd == 0 ? 0 : Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        var shell = top != 0 && Win32.GetClassName(top) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        if (GetForegroundWindow() is { } foreground && Win32.IsProcessElevated(foreground.ProcessId) == true)
        {
            return FieldCheck.Elevated;
        }

        try
        {
            var check = Task.Run(() =>
            {
                var focused = Focused();
                if (shell && !IsEditable(focused))
                {
                    return FieldCheck.NotText;
                }

                var result = Check(focused);
                return result == FieldCheck.Safe && !shell && IsClearlyNotText(focused) ? FieldCheck.NotText : result;
            });
            return check.Wait(800) ? check.Result : FieldCheck.Unknown;
        }
        catch (AggregateException)
        {
            return FieldCheck.Unknown;
        }
    }

    public bool? FocusedTextEndsWithNonWhitespace()
    {
        try
        {
            var check = Task.Run(() => HasTrailingText(Focused()));
            return check.Wait(600) ? check.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    public async Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => FocusSearchBox(window), cancellationToken).WaitAsync(UiaTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("uia search box timed out");
            return new TextTargetResult(false, "That app isn't responding. Try again in a moment.");
        }
    }

    /// <summary>"Paste" (reliable in every editor, Wispr-style) or "Type" (synthesized keystrokes).</summary>
    public static string InsertMethod { get; set; } = "Paste";

    public void TypeText(string text)
    {
        text = text.Replace("\r\n", " ").Replace('\n', ' ');
        KeyboardInput.WaitForModifiersReleased();
        if (string.Equals(InsertMethod, "Paste", StringComparison.OrdinalIgnoreCase))
        {
            ClipboardPaste.Paste(text);
        }
        else
        {
            KeyboardInput.TypeUnicode(text);
        }
    }

    public void PressKeys(string chord)
    {
        KeyboardInput.WaitForModifiersReleased();
        KeyboardInput.PressChord(chord);
    }

    public void Backspace(int count)
    {
        if (count > 0)
        {
            KeyboardInput.WaitForModifiersReleased();
            KeyboardInput.Repeat(0x08, count);
        }
    }

    public void PressKeysRepeated(string chord, int count)
    {
        KeyboardInput.WaitForModifiersReleased();
        var keys = KeyboardInput.ParseChord(chord);
        if (keys.Count == 1)
        {
            KeyboardInput.Repeat(keys[0], count);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            KeyboardInput.PressChord(chord);
        }
    }

    public CaretText? ReadTextBeforeCaret()
    {
        try
        {
            var read = Task.Run(() => ReadTextBeforeCaret(Focused()));
            return read.Wait(1500) ? read.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// TextPattern: the document from its start to the cursor (exact). ValuePattern only: the whole value, with the
    /// cursor position unknown. Password fields are never read.
    /// </summary>
    private static CaretText? ReadTextBeforeCaret(AutomationElement? element)
    {
        const int MaxChars = 20000;
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.Current.IsPassword)
            {
                return null;
            }

            var editable = IsEditable(element);
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                var text = (TextPattern)textPattern;
                var selection = text.GetSelection();
                if (selection.Length > 0)
                {
                    var caret = selection[0];
                    var hasSelection = caret.CompareEndpoints(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, caret, System.Windows.Automation.Text.TextPatternRangeEndpoint.End) != 0;
                    var range = text.DocumentRange.Clone();
                    range.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, caret, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
                    var value = range.GetText(-1);
                    return new CaretText(value.Length > MaxChars ? value[^MaxChars..] : value, CaretKnown: true, hasSelection, editable);
                }
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var value = ((ValuePattern)valuePattern).Current.Value;
                return new CaretText(value.Length > MaxChars ? value[^MaxChars..] : value, CaretKnown: false, Editable: editable);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"read text before cursor failed: {ex.GetType().Name}");
        }

        return null;
    }

    private static TextTargetResult FocusSearchBox(WindowRef window)
    {
        var box = FindSearchBox(window.Handle);
        if (box is null)
        {
            Thread.Sleep(400);
            box = FindSearchBox(window.Handle);
        }

        var name = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
        if (box is null)
        {
            return new TextTargetResult(false, $"I couldn't find a search box in {name}.");
        }

        try
        {
            box.SetFocus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
        {
            Log.Warn($"search focus failed: {ex.GetType().Name}");
        }

        Thread.Sleep(150);
        return IsEditable(Focused())
            ? new TextTargetResult(true, string.Empty)
            : new TextTargetResult(false, $"I couldn't focus the search box in {name}. Click it once, then say “start typing”.");
    }

    /// <summary>Visible edit/combo box whose name, id, or help text mentions "search"; the top-most one wins.</summary>
    private static AutomationElement? FindSearchBox(nint hwnd)
    {
        try
        {
            return EditableCandidates(hwnd, includeCombo: true)
                .Where(x => SafeGet(() =>
                {
                    var c = x.Element.Current;
                    return $"{c.Name} {c.AutomationId} {c.HelpText}".Contains("search", StringComparison.OrdinalIgnoreCase);
                }))
                .OrderBy(x => x.Rect.Top)
                .Select(x => x.Element)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"search box lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static TextTargetResult PrepareTextTarget(WindowRef window, AppEntry? profile)
    {
        var focused = Focused();
        var wrongBox = profile?.TextAreaName is { } wanted && !MatchesName(focused, wanted);
        if (profile?.TextArea is { } strategy && (!IsEditable(focused) || wrongBox))
        {
            var area = FindTextArea(window.Handle, strategy, profile.TextAreaName);
            if (area is null)
            {
                // Chromium builds its accessibility tree on first request; give it a moment.
                Thread.Sleep(400);
                area = FindTextArea(window.Handle, strategy, profile.TextAreaName);
            }

            if (area is null)
            {
                return new TextTargetResult(false, $"I couldn't find where to type in {profile.Name}. Click it once, then say “start typing”.");
            }

            try
            {
                area.SetFocus();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
            {
                Log.Warn($"text area focus failed: {ex.GetType().Name}");
            }

            Thread.Sleep(150);
            focused = Focused();
            if (!IsEditable(focused) || (profile.TextAreaName is { } name && !MatchesName(focused, name)))
            {
                return new TextTargetResult(false, $"I couldn't focus the text area in {profile.Name}. Click it once, then say “start typing”.");
            }
        }

        switch (Check(focused))
        {
            case FieldCheck.Password:
                return new TextTargetResult(false, "That's a password field — I won't type there.");
            case FieldCheck.Unknown:
                return new TextTargetResult(false, "I couldn't check the focused field. Click into the text box, then say “start typing”.");
        }

        return new TextTargetResult(true, string.Empty, HasTrailingText(focused), IsEditable(focused));
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

    /// <summary>
    /// Focus is on something that positively doesn't take text: a file or chat list, a tree, a button, a link, a menu,
    /// a tab. Anything else (documents, panes, custom controls, spreadsheet cells, terminals) still gets the text, so
    /// apps with unusual text boxes are never skipped.
    /// </summary>
    private static bool IsClearlyNotText(AutomationElement? element)
    {
        if (element is null || IsEditable(element))
        {
            return false;
        }

        return SafeGet(() =>
        {
            var type = element.Current.ControlType;
            return type == ControlType.List || type == ControlType.ListItem || type == ControlType.Tree || type == ControlType.TreeItem
                || type == ControlType.Button || type == ControlType.SplitButton || type == ControlType.Hyperlink
                || type == ControlType.MenuItem || type == ControlType.Menu || type == ControlType.MenuBar
                || type == ControlType.Tab || type == ControlType.TabItem || type == ControlType.CheckBox
                || type == ControlType.RadioButton || type == ControlType.Image || type == ControlType.Header || type == ControlType.HeaderItem;
        });
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

            if (current.ControlType != ControlType.Document && current.ControlType != ControlType.ComboBox)
            {
                return false;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return !((ValuePattern)pattern).Current.IsReadOnly;
            }

            // Rich editors (e.g. Notepad) expose TextPattern without ValuePattern.
            return current.ControlType == ControlType.Document && current.IsKeyboardFocusable &&
                   element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        });
    }

    /// <summary>
    /// "Bottom": chat apps put the message box at the bottom, so pick the lowest editable control.
    /// "Largest": document editors, so pick the biggest one. A name narrows the candidates first.
    /// </summary>
    private static AutomationElement? FindTextArea(nint hwnd, string strategy, string? name)
    {
        try
        {
            var candidates = EditableCandidates(hwnd, includeCombo: false).Where(x => x.Rect.Width >= 120);
            if (name is not null)
            {
                candidates = candidates.Where(x => MatchesName(x.Element, name));
            }

            var ordered = string.Equals(strategy, "Largest", StringComparison.OrdinalIgnoreCase)
                ? candidates.OrderByDescending(x => x.Rect.Width * x.Rect.Height)
                : candidates.OrderByDescending(x => x.Rect.Bottom);
            return ordered.Select(x => x.Element).FirstOrDefault();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"text area search failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static bool MatchesName(AutomationElement? element, string name) =>
        element is not null && SafeGet(() =>
        {
            var c = element.Current;
            return string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                   c.HelpText.Contains(name, StringComparison.OrdinalIgnoreCase);
        });

    private static IEnumerable<(AutomationElement Element, System.Windows.Rect Rect)> EditableCandidates(nint hwnd, bool includeCombo)
    {
        var root = AutomationElement.FromHandle(hwnd);
        Condition types = includeCombo
            ? new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))
            : new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        var condition = new AndCondition(
            types,
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsPasswordProperty, false));
        return root.FindAll(TreeScope.Descendants, condition)
            .Cast<AutomationElement>()
            .Where(IsEditable)
            .Select(e => (Element: e, Rect: SafeGet(() => e.Current.BoundingRectangle)))
            .Where(x => !x.Rect.IsEmpty)
            .ToList();
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

        for (var attempt = 0; attempt < 5; attempt++)
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

        Log.Warn($"activation of {appName} refused by Windows");
        return DesktopResult.Fail($"Windows didn't let me bring {appName} to the front. Click it once, then say “start typing”.");
    }

    internal static void TrySetForeground(nint hwnd)
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

    private WindowRef? FindAppWindow(AppEntry app) => FindAppWindows(app).FirstOrDefault();

    /// <summary>The app's visible top-level windows, front-most first.</summary>
    private List<WindowRef> FindAppWindows(AppEntry app)
    {
        var found = new List<WindowRef>();
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
                found.Add(new WindowRef(hwnd, (int)pid, name, title));
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
