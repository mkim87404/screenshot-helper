using System.Runtime.InteropServices;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows.Interop;

/// <summary>
/// A plain, never-activated popup window that can be shown directly *behind* another window in a solid white or black colour.
/// Used to capture a window over two known backgrounds so its rounded corners and translucent border can be matted cleanly.
/// Lives on its own message thread (a window needs one to paint). The window procedure is the system's DefWindowProc, which paints
/// the class background brush — so no managed callback is involved.
/// </summary>
internal sealed class BackdropWindow : IDisposable
{
    private const string ClassName = "ScreenshotHelper.Backdrop";

    private readonly MessageThread _thread = new("ScreenshotHelper.Backdrop");
    private readonly IntPtr _className;
    private readonly IntPtr _hwnd;

    public BackdropWindow()
    {
        _className = Marshal.StringToHGlobalUni(ClassName);
        _hwnd = _thread.Invoke(() =>
        {
            var instance = GetModuleHandle(null);
            var wndClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                hbrBackground = GetStockObject(WHITE_BRUSH),
                lpszClassName = _className,
            };

            // Registration fails harmlessly if the class already exists in this process.
            RegisterClassEx(in wndClass);
            return CreateWindowEx(
                (uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE), ClassName, string.Empty, WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        });

        if (_hwnd == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_className);
            _thread.Dispose();
            throw new InvalidOperationException("Couldn't create the capture backdrop window.");
        }
    }

    /// <summary>The native window (a hidden top-level popup until shown); tests use it as a real window to exercise Win32 helpers.</summary>
    internal IntPtr Handle => _hwnd;

    /// <summary>Shows the backdrop over <paramref name="bounds"/>, directly below <paramref name="target"/> in z-order, and waits until it's on screen.</summary>
    public void ShowBehind(IntPtr target, System.Drawing.Rectangle bounds, bool white)
    {
        _thread.Invoke(() =>
        {
            SetClassLongPtr(_hwnd, GCLP_HBRBACKGROUND, GetStockObject(white ? WHITE_BRUSH : BLACK_BRUSH));
            SetWindowPos(_hwnd, target, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(_hwnd, IntPtr.Zero, true);
            UpdateWindow(_hwnd);
            return 0;
        });

        // Two compositions: one to pick up the repaint, one margin for the frame in flight. If DWM can't confirm them, the capture
        // can't be trusted to show the backdrop, so give up (the caller falls back to an unmatted capture).
        if (DwmFlush() != 0 || DwmFlush() != 0)
        {
            throw new InvalidOperationException("Desktop composition didn't confirm the backdrop frame.");
        }
    }

    public void Hide() => _thread.Invoke(() => ShowWindow(_hwnd, SW_HIDE));

    public void Dispose()
    {
        try
        {
            _thread.Invoke(() =>
            {
                DestroyWindow(_hwnd);
                UnregisterClass(_className, GetModuleHandle(null));
                return 0;
            });
        }
        catch (ObjectDisposedException)
        {
        }

        _thread.Dispose();
        Marshal.FreeHGlobal(_className);
    }
}
