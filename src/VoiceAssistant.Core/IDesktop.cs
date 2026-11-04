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
}
