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

public sealed record TextTargetResult(bool Success, string Message, bool? HasTrailingText = null);

public enum FieldCheck
{
    /// <summary>Positively confirmed not to be a password field.</summary>
    Safe,
    Password,

    /// <summary>Accessibility query failed or timed out; treat as unsafe.</summary>
    Unknown,
}

/// <summary>Operating-system side effects. Implemented with Win32/UI Automation in the app; faked in tests.</summary>
public interface IDesktop
{
    WindowRef? GetForegroundWindow();

    /// <summary>
    /// Opens or activates the app. A successful result with a null <see cref="DesktopResult.Window"/> means the
    /// launch was requested but the app's window could not be positively identified.
    /// </summary>
    Task<DesktopResult> OpenOrActivateAsync(AppEntry app, CancellationToken cancellationToken);

    /// <summary>Verifies the focused control can receive text and, for supporting profiles, focuses the message box.</summary>
    Task<TextTargetResult> PrepareTextTargetAsync(WindowRef window, AppEntry? profile, CancellationToken cancellationToken);

    FieldCheck CheckFocusedField();

    void TypeText(string text);

    void PressKeys(string chord);

    void Backspace(int count);
}
