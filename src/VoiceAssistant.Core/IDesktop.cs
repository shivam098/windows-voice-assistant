namespace VoiceAssistant.Core;

/// <summary>A top-level window identity. Handle values can be reused by Windows, so the process ID is also compared.</summary>
public sealed record WindowRef(nint Handle, int ProcessId, string ProcessName, string Title)
{
    public bool SameWindow(WindowRef? other) => other is not null && other.Handle == Handle && other.ProcessId == ProcessId;
}

public sealed record DesktopResult(bool Success, string Message, WindowRef? Window = null)
{
    public static DesktopResult Ok(string message, WindowRef? window = null) => new(true, message, window);

    public static DesktopResult Fail(string message) => new(false, message);
}

public sealed record TextTargetResult(bool Success, string Message, bool? HasTrailingText = null, bool? FocusIsEditable = null);

public enum FieldCheck
{
    /// <summary>Positively confirmed not to be a password field.</summary>
    Safe,
    Password,

    /// <summary>The foreground window runs as administrator; Windows silently drops synthesized input to it.</summary>
    Elevated,

    /// <summary>Accessibility query failed or timed out; treat as unsafe.</summary>
    Unknown,
}

/// <summary>Operating-system side effects. Implemented with Win32/UI Automation in the app; faked in tests.</summary>
public interface IDesktop
{
    WindowRef? GetForegroundWindow();

    /// <summary>
    /// Opens or activates the app. With <paramref name="newWindow"/>, an already-running app with
    /// <see cref="AppEntry.NewWindowKeys"/> gets a fresh window. A successful result with a null
    /// <see cref="DesktopResult.Window"/> means the launch was requested but the window couldn't be identified.
    /// </summary>
    Task<DesktopResult> OpenOrActivateAsync(AppEntry app, bool newWindow, CancellationToken cancellationToken);

    /// <summary>Verifies the focused control can receive text and, for profiles with a <see cref="AppEntry.TextArea"/>, focuses it.</summary>
    Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken);

    /// <summary>Finds an edit box named like "search" in the window and focuses it.</summary>
    Task<TextTargetResult> FocusSearchBoxAsync(WindowRef window, CancellationToken cancellationToken);

    FieldCheck CheckFocusedField();

    /// <summary>True if the focused text field already ends in a non-space character (so dictation needs a leading space); null if unknown.</summary>
    bool? FocusedTextEndsWithNonWhitespace();

    void TypeText(string text);

    void PressKeys(string chord);

    void Backspace(int count);

    // Navigation (tabs, clicking by name, choosing a text box). Platforms that can't do one report it unsupported.

    /// <summary>The window's bounds in screen pixels, for “on the left / right” hints.</summary>
    ScreenRect? GetWindowRect(WindowRef window) => null;

    /// <summary>Named, enabled, visible elements the user could click (buttons, links, tabs, list/tree/menu items).</summary>
    Task<IReadOnlyList<UiElement>> ListClickableAsync(WindowRef window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UiElement>>([]);

    /// <summary>Visible, enabled, non-password text boxes in the window.</summary>
    Task<IReadOnlyList<UiElement>> ListTextBoxesAsync(WindowRef window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UiElement>>([]);

    /// <summary>Tabs of every open browser window, front-most window first, in tab-strip order.</summary>
    Task<IReadOnlyList<BrowserTab>> ListTabsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrowserTab>>([]);

    /// <summary>Brings the tab's window to the front, selects the tab, and verifies it is the active tab.</summary>
    Task<DesktopResult> SelectTabAsync(BrowserTab tab, CancellationToken cancellationToken) =>
        Task.FromResult(DesktopResult.Fail("Switching tabs isn't supported here."));

    /// <summary>Activates the element: invoke / select / toggle / expand, falling back to a real click on its centre.</summary>
    Task<DesktopResult> ClickAsync(UiElement element, CancellationToken cancellationToken) =>
        Task.FromResult(DesktopResult.Fail("Clicking isn't supported here."));

    /// <summary>Focuses the text box and verifies the focus landed in it.</summary>
    Task<TextTargetResult> FocusTextBoxAsync(UiElement box, CancellationToken cancellationToken) =>
        Task.FromResult(new TextTargetResult(false, "Choosing a text box isn't supported here."));
}
