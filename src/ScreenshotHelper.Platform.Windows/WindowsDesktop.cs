using ScreenshotHelper.Platform.Windows.Interop;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>Focus and window-style helpers the UI needs from Win32.</summary>
public static class WindowsDesktop
{
    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>Mouse position in physical screen pixels.</summary>
    public static (int X, int Y) CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>Gives focus back to a window we took it from (caption box, collision prompt); ignored if it no longer exists.</summary>
    public static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && IsWindow(hwnd))
        {
            SetForegroundWindow(hwnd);
        }
    }

    public static bool BringToFront(IntPtr hwnd) => hwnd != IntPtr.Zero && SetForegroundWindow(hwnd);

    /// <summary>
    /// Turns a window into a passive overlay: never activated, absent from Alt+Tab/taskbar, clicks pass through,
    /// and excluded from screen capture (Windows 10 2004+) so it can't appear in a screenshot.
    /// </summary>
    public static void MakePassiveOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        ExcludeFromCapture(hwnd);
    }

    /// <summary>Hides a window from every screen capture API that honours display affinity (including our own capture).</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd) => hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
}

/// <summary>
/// Single-instance guard: whoever creates the named mutex first is the instance; later launches signal a named event (asking the first
/// to show itself) and exit. Existence of the kernel object is the signal, so the mutex is never owned: that avoids thread-affine
/// ReleaseMutex calls and abandoned-mutex states after a crash (the OS deletes the object when its last handle closes).
/// "Local\" scopes both objects to the current Windows session.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ScreenshotHelper.SingleInstance";
    private const string EventName = @"Local\ScreenshotHelper.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool isFirst, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        IsFirstInstance = isFirst;
        if (isFirst)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public bool IsFirstInstance { get; }

    /// <summary>Claims the instance. If another process already has it, signals that process and returns an instance with <see cref="IsFirstInstance"/> false.</summary>
    public static SingleInstance Acquire(Action onActivate)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            activate.Set();
        }

        return new SingleInstance(mutex, activate, createdNew, onActivate);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _mutex.Dispose();
        _activate.Dispose();
    }
}
