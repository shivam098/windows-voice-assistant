using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.Platform;

/// <summary>
/// Navigation on Windows with UI Automation: tabs, clickable elements and text boxes. One cached FindAll per scan keeps
/// a scan of a large Chromium window to a single cross-process round trip. Logs counts and timings only, never names.
/// </summary>
internal sealed partial class WindowsDesktop
{
    private const int MaxElements = 1500;

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    private static readonly (ControlType Type, ElementRole Role)[] ClickableTypes =
    [
        (ControlType.Button, ElementRole.Button), (ControlType.SplitButton, ElementRole.Button), (ControlType.Hyperlink, ElementRole.Link),
        (ControlType.TabItem, ElementRole.Tab), (ControlType.MenuItem, ElementRole.MenuItem), (ControlType.TreeItem, ElementRole.TreeItem),
        (ControlType.ListItem, ElementRole.ListItem), (ControlType.CheckBox, ElementRole.CheckBox), (ControlType.RadioButton, ElementRole.RadioButton),
        (ControlType.ComboBox, ElementRole.ComboBox),

        // Rows and cells of grids and tables, e.g. WhatsApp Web's chat list.
        (ControlType.DataItem, ElementRole.ListItem),
    ];

    private readonly object _registryGate = new();
    private readonly Dictionary<int, AutomationElement> _registry = [];
    private int _nextId;

    public ScreenRect? GetWindowRect(WindowRef window) =>
        Win32.GetWindowRect(window.Handle, out var r) ? new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    public Task<IReadOnlyList<UiElement>> ListClickableAsync(WindowRef window, CancellationToken cancellationToken) =>
        RunUia("clickable scan", () => ScanClickable(window), [], cancellationToken);

    public Task<IReadOnlyList<UiElement>> ListTextBoxesAsync(WindowRef window, CancellationToken cancellationToken) =>
        RunUia("text box scan", () => ScanTextBoxes(window), [], cancellationToken);

    public Task<IReadOnlyList<BrowserTab>> ListTabsAsync(CancellationToken cancellationToken) =>
        RunUia("tab scan", ScanTabs, [], cancellationToken);

    public async Task<DesktopResult> SelectTabAsync(BrowserTab tab, CancellationToken cancellationToken)
    {
        if (Win32.IsProcessElevated(tab.Window.ProcessId) == true)
        {
            return DesktopResult.Fail("That browser runs as administrator; Windows won't let me control it.");
        }

        if (!tab.Window.SameWindow(GetForegroundWindow()))
        {
            var activated = await ActivateAsync(tab.Window, "the browser", cancellationToken).ConfigureAwait(false);
            if (!activated.Success)
            {
                return activated;
            }
        }

        var element = Lookup(tab.Id);
        if (element is null)
        {
            return DesktopResult.Fail("That tab changed. Try again.");
        }

        var acted = await RunUia("tab select", token => Activate(element, tab.Rect, preferSelect: true, tab.Window.Handle, token), (string?)"timed out", cancellationToken).ConfigureAwait(false);
        if (acted is not null)
        {
            return DesktopResult.Fail($"I couldn't switch to that tab ({acted}).");
        }

        // Verify: the browser window's title follows the active tab.
        var wanted = NameMatcher.CleanTabTitle(tab.Title);
        var prefix = wanted.Length > 16 ? wanted[..16] : wanted;
        for (var i = 0; i < 15; i++)
        {
            var title = Win32.GetWindowTitle(tab.Window.Handle);
            if (prefix.Length == 0 || NameMatcher.CleanTabTitle(title).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"tab select ok after {i * 100} ms");
                return DesktopResult.Ok("Switched tab.", tab.Window with { Title = title });
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        Log.Warn("tab select not verified");
        return DesktopResult.Fail("The tab didn't change. Click it once, or try “next tab”.");
    }

    public async Task<DesktopResult> ClickAsync(UiElement element, CancellationToken cancellationToken)
    {
        var live = Lookup(element.Id);
        var fg = GetForegroundWindow();
        if (live is null || fg is null)
        {
            return DesktopResult.Fail("That changed on screen. Try again.");
        }

        if (Win32.IsProcessElevated(fg.ProcessId) == true)
        {
            return DesktopResult.Fail("That window runs as administrator; Windows won't let me click in it.");
        }

        var error = await RunUia(
            "click",
            token =>
            {
                // The confirmed name must still be the element's name: a re-rendered page must not turn
                // "click 7" or a confirmed "yes" into a click on something else.
                var now = SafeGet(() => live.Current.Name)?.Trim();
                return now != element.Name ? "that changed on screen" : Activate(live, element.Rect, element.Role == ElementRole.Tab, fg.Handle, token);
            },
            (string?)"timed out",
            cancellationToken).ConfigureAwait(false);
        return error is null ? DesktopResult.Ok("Clicked.") : DesktopResult.Fail($"I couldn't click that ({error}).");
    }

    public async Task<TextTargetResult> FocusTextBoxAsync(UiElement box, CancellationToken cancellationToken)
    {
        var live = Lookup(box.Id);
        var fg = GetForegroundWindow();
        if (live is null || fg is null)
        {
            return new TextTargetResult(false, "That text box changed. Try again.");
        }

        return await RunUia("text box focus", token => FocusBox(live, box.Rect, fg.Handle, token), new TextTargetResult(false, "That app isn't responding. Try again in a moment."), cancellationToken).ConfigureAwait(false);
    }

    private static TextTargetResult FocusBox(AutomationElement element, ScreenRect rect, nint window, CancellationToken token)
    {
        try
        {
            element.SetFocus();
        }
        catch (ElementNotAvailableException)
        {
            return new TextTargetResult(false, "That text box changed. Try again.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Log.Warn($"text box SetFocus failed: {ex.GetType().Name}");
        }

        Thread.Sleep(150);
        var focused = Focused();
        if (!IsInside(focused, element, rect) && !token.IsCancellationRequested)
        {
            // Some web inputs ignore programmatic focus; a real click lands the caret (only inside the target window).
            ClickAt(rect.CenterX, rect.CenterY, window);
            Thread.Sleep(200);
            focused = Focused();
        }

        if (!IsInside(focused, element, rect) || !IsEditable(focused))
        {
            return new TextTargetResult(false, "I couldn't put the cursor in that box. Click it once, then say “start typing”.");
        }

        return Check(focused) == FieldCheck.Password
            ? new TextTargetResult(false, "That's a password field — I won't type there.")
            : new TextTargetResult(true, string.Empty, HasTrailingText(focused), true);
    }

    /// <summary>The focused element is the box, or inside it (rich editors focus an inner element).</summary>
    private static bool IsInside(AutomationElement? focused, AutomationElement box, ScreenRect rect)
    {
        if (focused is null)
        {
            return false;
        }

        if (SafeGet(() => Automation.Compare(focused, box)))
        {
            return true;
        }

        var r = SafeGet(() => focused.Current.BoundingRectangle);
        return !r.IsEmpty && rect.Contains(r.X + (r.Width / 2), r.Y + (r.Height / 2));
    }

    /// <summary>Invoke / select / toggle / expand-collapse; a real click on the centre if none works. Null on success.</summary>
    private static string? Activate(AutomationElement element, ScreenRect rect, bool preferSelect, nint window, CancellationToken token)
    {
        try
        {
            if (element.Current.ControlType == ControlType.DataItem && !preferSelect)
            {
                // Web grid rows (chat lists) only highlight on a programmatic "select"; they open on a real click.
                return token.IsCancellationRequested ? "timed out" : ClickGridItem(element, rect, window);
            }

            if (preferSelect && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                ((SelectionItemPattern)select).Select();
                return null;
            }

            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return null;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out select))
            {
                ((SelectionItemPattern)select).Select();
                return null;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                ((TogglePattern)toggle).Toggle();
                return null;
            }

            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    pattern.Expand();
                }
                else
                {
                    pattern.Collapse();
                }

                return null;
            }
        }
        catch (ElementNotAvailableException)
        {
            // Gone from the screen: never click where it used to be.
            return "that changed on screen";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotEnabledException or COMException)
        {
            Log.Warn($"pattern action failed, clicking instead: {ex.GetType().Name}");
        }

        if (token.IsCancellationRequested)
        {
            return "timed out";
        }

        return ClickAt(rect.CenterX, rect.CenterY, window) ? null : "something else is on top of it";
    }

    /// <summary>
    /// Scrolls a grid row into view, then clicks its centre only if that point really is the row (a half-hidden row's
    /// centre can sit over the search box above the list). Null on success.
    /// </summary>
    private static string? ClickGridItem(AutomationElement element, ScreenRect rect, nint window)
    {
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
        {
            try
            {
                ((ScrollItemPattern)scroll).ScrollIntoView();
                Thread.Sleep(150);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                Log.Warn($"scroll into view failed: {ex.GetType().Name}");
            }
        }

        var now = element.Current.BoundingRectangle;
        if (!now.IsEmpty)
        {
            rect = new ScreenRect(now.X, now.Y, now.Width, now.Height);
        }

        var point = new System.Windows.Point(rect.CenterX, rect.CenterY);
        var hit = SafeGet(() => AutomationElement.FromPoint(point));
        var walker = TreeWalker.RawViewWalker;
        var onTarget = false;
        for (var (node, depth) = (hit, 0); node is not null && depth < 25; node = SafeGet(() => walker.GetParent(node)), depth++)
        {
            if (SafeGet(() => Automation.Compare(node, element)))
            {
                onTarget = true;
                break;
            }
        }

        if (!onTarget)
        {
            Log.Warn("grid item centre is covered by something else");
            return "something else is on top of it";
        }

        return ClickAt(point.X, point.Y, window) ? null : "something else is on top of it";
    }

    /// <summary>
    /// A real left click at a screen point, only if that point belongs to <paramref name="window"/>; then the pointer
    /// goes back where it was.
    /// </summary>
    private static bool ClickAt(double x, double y, nint window)
    {
        var point = new Win32.POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        var under = Win32.WindowFromPoint(point);
        if (under == 0 || Win32.GetAncestor(under, Win32.GA_ROOT) != window)
        {
            Log.Warn("click point is not in the target window");
            return false;
        }

        KeyboardInput.WaitForModifiersReleased();
        Win32.GetCursorPos(out var before);
        if (!Win32.SetCursorPos(point.X, point.Y))
        {
            return false;
        }

        Thread.Sleep(30);
        var inputs = new Win32.INPUT[]
        {
            new() { type = Win32.INPUT_MOUSE, U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_LEFTDOWN } } },
            new() { type = Win32.INPUT_MOUSE, U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_LEFTUP } } },
        };
        var sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
        Thread.Sleep(60);
        Win32.SetCursorPos(before.X, before.Y);
        return sent == inputs.Length;
    }

    private IReadOnlyList<UiElement> ScanClickable(WindowRef window)
    {
        var sw = Stopwatch.StartNew();
        var bounds = GetWindowRect(window);
        var condition = new AndCondition(
            new OrCondition(ClickableTypes.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t.Type)).ToArray()),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        var found = FindCached(window.Handle, condition);
        var result = new List<UiElement>();
        foreach (AutomationElement e in found)
        {
            var (name, type, rect) = (SafeGet(() => e.Cached.Name) ?? string.Empty, SafeGet(() => e.Cached.ControlType), SafeGet(() => e.Cached.BoundingRectangle));
            if (name.Trim().Length == 0 || rect.IsEmpty || type is null)
            {
                continue;
            }

            var screen = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
            if (bounds is not null && !screen.Intersects(bounds))
            {
                // Chromium reports hidden web content far outside the window as "on screen".
                continue;
            }

            var role = ClickableTypes.FirstOrDefault(t => t.Type == type).Role;
            result.Add(new UiElement(Register(e), name.Trim(), role, screen));
            if (result.Count >= MaxElements)
            {
                break;
            }
        }

        Log.Info($"clickable scan: {result.Count} of {found.Count} in {sw.ElapsedMilliseconds} ms");
        return result;
    }

    private IReadOnlyList<UiElement> ScanTextBoxes(WindowRef window)
    {
        var sw = Stopwatch.StartNew();
        var bounds = GetWindowRect(window);
        var condition = new AndCondition(
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsPasswordProperty, false));
        var found = FindCached(window.Handle, condition);
        var result = new List<UiElement>();
        foreach (AutomationElement e in found)
        {
            var rect = SafeGet(() => e.Cached.BoundingRectangle);
            if (rect.IsEmpty || rect.Width < 40 || !SafeGet(() => e.Cached.IsKeyboardFocusable))
            {
                continue;
            }

            var type = SafeGet(() => e.Cached.ControlType);
            var hasValue = CachedBool(e, AutomationElement.IsValuePatternAvailableProperty);
            var readOnly = hasValue && CachedBool(e, ValuePattern.IsReadOnlyProperty);
            if (readOnly || (type != ControlType.Edit && !hasValue))
            {
                // Documents and combo boxes count only when they take typed text (not a web page's root document).
                continue;
            }

            var screen = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
            if (bounds is not null && !screen.Intersects(bounds))
            {
                continue;
            }

            result.Add(new UiElement(Register(e), (SafeGet(() => e.Cached.Name) ?? string.Empty).Trim(), ElementRole.TextBox, screen));
        }

        Log.Info($"text box scan: {result.Count} of {found.Count} in {sw.ElapsedMilliseconds} ms");
        return result;
    }

    private IReadOnlyList<BrowserTab> ScanTabs()
    {
        var sw = Stopwatch.StartNew();
        var tabs = new List<BrowserTab>();
        foreach (var window in BrowserWindows())
        {
            var found = FindCached(window.Handle, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem), withSelection: true);
            var items = new List<(AutomationElement Element, string Name, System.Windows.Rect Rect, bool Selected)>();
            foreach (AutomationElement e in found)
            {
                var rect = SafeGet(() => e.Cached.BoundingRectangle);
                var name = SafeGet(() => e.Cached.Name) ?? string.Empty;
                if (!rect.IsEmpty && name.Trim().Length > 0)
                {
                    items.Add((e, name.Trim(), rect, CachedBool(e, SelectionItemPattern.IsSelectedProperty)));
                }
            }

            if (items.Count == 0)
            {
                continue;
            }

            // Browser tabs live in the browser's own UI, outside web content: tab controls inside pages (and the
            // whole page of an installed web app) sit inside a Document and are not browser tabs. Horizontal and
            // vertical tab strips both come out in reading order.
            var pages = FindCached(window.Handle, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
                .Cast<AutomationElement>()
                .Select(d => SafeGet(() => d.Cached.BoundingRectangle))
                .Where(r => !r.IsEmpty)
                .ToList();
            var strip = TargetResolver.SortReadingOrder(
                    items.Where(i => !pages.Any(p => p.Contains(new System.Windows.Point(i.Rect.X + (i.Rect.Width / 2), i.Rect.Y + (i.Rect.Height / 2))))),
                    i => new ScreenRect(i.Rect.X, i.Rect.Y, i.Rect.Width, i.Rect.Height))
                .ToList();
            for (var i = 0; i < strip.Count; i++)
            {
                var r = strip[i].Rect;
                tabs.Add(new BrowserTab(Register(strip[i].Element), strip[i].Name, i + 1, strip[i].Selected, window, new ScreenRect(r.X, r.Y, r.Width, r.Height)));
            }
        }

        Log.Info($"tab scan: {tabs.Count} tabs in {tabs.Select(t => t.Window.Handle).Distinct().Count()} windows, {sw.ElapsedMilliseconds} ms");
        return tabs;
    }

    /// <summary>Visible browser windows, front-most first.</summary>
    private List<WindowRef> BrowserWindows()
    {
        var found = new List<WindowRef>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0 || Win32.IsCloaked(hwnd))
            {
                return true;
            }

            var title = Win32.GetWindowTitle(hwnd);
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (title.Length == 0 || pid == 0 || pid == _ownProcessId)
            {
                return true;
            }

            var name = ProcessName((int)pid);
            if (BrowserProcesses.Contains(name))
            {
                found.Add(new WindowRef(hwnd, (int)pid, name, title));
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>A cached boolean property; false when the pattern isn't supported (UIA returns a "not supported" marker).</summary>
    private static bool CachedBool(AutomationElement element, AutomationProperty property) =>
        SafeGet(() => element.GetCachedPropertyValue(property, ignoreDefaultValue: false)) is bool value && value;

    private static AutomationElementCollection FindCached(nint hwnd, Condition condition, bool withSelection = false)
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsKeyboardFocusableProperty);
        request.Add(AutomationElement.IsValuePatternAvailableProperty);
        request.Add(ValuePattern.Pattern);
        request.Add(ValuePattern.IsReadOnlyProperty);
        if (withSelection)
        {
            request.Add(SelectionItemPattern.Pattern);
            request.Add(SelectionItemPattern.IsSelectedProperty);
        }

        using (request.Activate())
        {
            return AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, condition);
        }
    }

    private int Register(AutomationElement element)
    {
        lock (_registryGate)
        {
            var id = ++_nextId;
            _registry[id] = element;
            if (_registry.Count > 6000)
            {
                // Ids from old scans are only kept long enough to answer "click 7" after "show numbers".
                foreach (var stale in _registry.Keys.Where(k => k < id - 4000).ToList())
                {
                    _registry.Remove(stale);
                }
            }

            return id;
        }
    }

    private AutomationElement? Lookup(int id)
    {
        lock (_registryGate)
        {
            return _registry.GetValueOrDefault(id);
        }
    }

    private static Task<T> RunUia<T>(string what, Func<T> work, T fallback, CancellationToken cancellationToken) =>
        RunUia(what, _ => work(), fallback, cancellationToken);

    /// <summary>
    /// Runs UI Automation work off the caller's thread with a timeout. The work's token is cancelled when the timeout
    /// hits, so work that is still running (a slow app) never clicks after the user was told it timed out.
    /// </summary>
    private static async Task<T> RunUia<T>(string what, Func<CancellationToken, T> work, T fallback, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await Task.Run(() => work(timeout.Token), cancellationToken).WaitAsync(UiaTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            Log.Warn($"{what} timed out");
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"{what} failed: {ex.GetType().Name}");
            return fallback;
        }
    }
}
