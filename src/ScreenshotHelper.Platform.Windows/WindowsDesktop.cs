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

        // Click-through needs WS_EX_LAYERED, and a layered window's appearance is only defined once SetLayeredWindowAttributes (or
        // UpdateLayeredWindow) has been called; fully opaque here, since the window's own content supplies the transparency.
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        ExcludeFromCapture(hwnd);
    }

    /// <summary>
    /// Keeps a window off the screen (or puts it back) without hiding it: a cloaked window stays visible to Windows, keeps rendering and
    /// can hold focus, DWM just doesn't compose it (DWMWA_CLOAK, Windows 8+). Returns false if the window couldn't be cloaked.
    /// </summary>
    public static bool SetCloaked(IntPtr hwnd, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return hwnd != IntPtr.Zero && DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
    }

    /// <summary>
    /// Starts a fade-in: makes the window layered (if it isn't already) and fully transparent. Returns whether it was layered before, for
    /// <see cref="EndFadeIn"/>. Layered opacity applies to the whole window, title bar included.
    /// </summary>
    public static bool BeginFadeIn(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var wasLayered = (style & WS_EX_LAYERED) != 0;
        if (!wasLayered)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_LAYERED));
        }

        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        return wasLayered;
    }

    /// <summary>Sets the whole window's opacity (0–255) during a fade started by <see cref="BeginFadeIn"/>.</summary>
    public static void SetFadeOpacity(IntPtr hwnd, byte alpha)
    {
        if (hwnd != IntPtr.Zero)
        {
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
    }

    /// <summary>Ends a fade: fully opaque, and no longer layered if it wasn't before (layering costs a little on every frame).</summary>
    public static void EndFadeIn(IntPtr hwnd, bool wasLayered)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        if (!wasLayered)
        {
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style & ~WS_EX_LAYERED));
        }
    }

    /// <summary>Whether DWM is currently keeping the window off the screen (cloaked by this app, the shell or a virtual desktop).</summary>
    public static bool IsCloaked(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Whether the window is currently excluded from screen capture (for tests and diagnostics).</summary>
    public static bool IsExcludedFromCapture(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;

    /// <summary>
    /// How far an auto-hide taskbar (or other auto-hide app bar) reaches into each edge of a monitor when it slides out, in physical
    /// pixels. Windows counts an auto-hide taskbar's area as part of the working area, so a window placed in that corner is covered
    /// whenever the taskbar is shown (taskbar focused or the mouse at that edge).
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) AutoHideBarInsets(System.Drawing.Rectangle monitor)
    {
        int Thickness(uint edge)
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
                uEdge = edge,
                rc = new RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom },
            };
            var bar = SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data);
            if (bar == IntPtr.Zero || !GetWindowRect(bar, out var r))
            {
                return 0;
            }

            // A hidden bar keeps its size and slides off screen, so its size (not its position) says how far it reaches in.
            return edge is ABE_LEFT or ABE_RIGHT ? r.Right - r.Left : r.Bottom - r.Top;
        }

        return (Thickness(ABE_LEFT), Thickness(ABE_TOP), Thickness(ABE_RIGHT), Thickness(ABE_BOTTOM));
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
