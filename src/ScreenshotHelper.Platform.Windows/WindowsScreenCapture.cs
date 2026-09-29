using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Imaging;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Platform.Windows.Interop;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// GDI screen capture. Since Windows 8 the desktop is always DWM-composed, so a plain SRCCOPY blit already includes layered windows;
/// CAPTUREBLT is deliberately not used (it makes the mouse cursor flicker). Coordinates are physical pixels because the app is
/// per-monitor-V2 DPI aware (see app.manifest).
/// Active-window captures get clean, transparent rounded corners and border via <see cref="EdgeMatte"/> (see <see cref="CaptureWindow"/>).
/// Not thread-safe by design: the session actor is the only caller of <see cref="Capture"/>, and <see cref="Dispose"/> runs at app
/// exit after the actor has finished, so no lock is needed (one would otherwise be held across screen reads and compositor waits).
/// </summary>
public sealed class WindowsScreenCapture(AppLog log) : IScreenCapture, IDisposable
{
    // Width of the edge band that is matted, in 96-DPI pixels: covers Windows 11's 8-px corner radius plus the border.
    private const int MatteBand = 12;

    private BackdropWindow? _backdrop;

    public CapturedImage Capture(CaptureTarget target)
    {
        if (target == CaptureTarget.ActiveWindow && ForegroundWindowToMatte() is { } window)
        {
            try
            {
                return CaptureWindow(window.Handle, window.Bounds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
            {
                // Fall through to a plain capture: an unmatted edge is better than no screenshot.
                log.Warn("Window capture with clean edges failed; using a plain capture.", ex);
            }
        }

        var bounds = ResolveBounds(target);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Nothing to capture for {target}.");
        }

        return new GdiCapturedImage(CopyScreen(bounds, PixelFormat.Format32bppRgb));
    }

    public void Dispose()
    {
        _backdrop?.Dispose();
        _backdrop = null;
    }

    /// <summary>
    /// Captures a window twice — over a white and then a black backdrop placed directly behind it — and mattes the edge band, so the
    /// rounded corners are transparent and the translucent border keeps its own colour instead of whatever was behind the window.
    /// </summary>
    private GdiCapturedImage CaptureWindow(IntPtr hwnd, Rectangle bounds)
    {
        _backdrop ??= new BackdropWindow();
        byte[] white, black;
        int stride;
        try
        {
            using var overWhite = CaptureOver(hwnd, bounds, white: true);
            using var overBlack = CaptureOver(hwnd, bounds, white: false);
            white = ReadPixels(overWhite, out stride);
            black = ReadPixels(overBlack, out _);
        }
        finally
        {
            // On every path, or a solid white/black rectangle would stay on screen behind the window.
            _backdrop.Hide();
        }

        var dpi = GetDpiForWindow(hwnd);
        var band = (int)Math.Ceiling(MatteBand * (dpi == 0 ? 1.0 : dpi / 96.0));
        var matted = EdgeMatte.Combine(white, black, bounds.Width, bounds.Height, stride, band);

        var result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            var data = result.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < bounds.Height; y++)
                {
                    Marshal.Copy(matted, y * stride, data.Scan0 + (y * data.Stride), bounds.Width * 4);
                }
            }
            finally
            {
                result.UnlockBits(data);
            }

            return new GdiCapturedImage(result);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private Bitmap CaptureOver(IntPtr hwnd, Rectangle bounds, bool white)
    {
        _backdrop!.ShowBehind(hwnd, bounds, white);
        return CopyScreen(bounds, PixelFormat.Format32bppArgb);
    }

    private static Bitmap CopyScreen(Rectangle bounds, PixelFormat format)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, format);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static byte[] ReadPixels(Bitmap bitmap, out int stride)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * stride, stride);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The foreground window when it has a visible frame worth matting (not minimized, not maximized — maximized windows have no corners or border).</summary>
    private static (IntPtr Handle, Rectangle Bounds)? ForegroundWindowToMatte()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || IsZoomed(hwnd) || ActiveWindowRect() is not { } rect)
        {
            return null;
        }

        return (hwnd, rect);
    }

    /// <summary>Screen rectangle for a target, clipped to the virtual desktop.</summary>
    public static Rectangle ResolveBounds(CaptureTarget target)
    {
        var desktop = new Rectangle(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var rect = target switch
        {
            CaptureTarget.AllMonitors => desktop,
            CaptureTarget.PrimaryMonitor => MonitorRect(new POINT(), MONITOR_DEFAULTTOPRIMARY),
            CaptureTarget.ActiveWindow => ActiveWindowRect() ?? CursorMonitorRect(),
            _ => CursorMonitorRect(),
        };
        return Rectangle.Intersect(rect, desktop);
    }

    private static Rectangle CursorMonitorRect()
    {
        GetCursorPos(out var cursor);
        return MonitorRect(cursor, MONITOR_DEFAULTTONEAREST);
    }

    private static Rectangle MonitorRect(POINT point, uint flags)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromPoint(point, flags);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("Couldn't locate the monitor.");
        }

        var r = info.rcMonitor;
        return new Rectangle(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>The foreground window's visible frame (DWM bounds exclude the invisible resize border); null if minimized or absent.</summary>
    private static Rectangle? ActiveWindowRect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd))
        {
            return null;
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out frame))
        {
            return null;
        }

        return frame.Width > 0 && frame.Height > 0 ? new Rectangle(frame.Left, frame.Top, frame.Width, frame.Height) : null;
    }
}

/// <summary>A captured GDI+ bitmap; disposing releases the bitmap memory deterministically.</summary>
public sealed class GdiCapturedImage : CapturedImage
{
    public GdiCapturedImage(Bitmap bitmap)
    {
        Bitmap = bitmap;
    }

    public Bitmap Bitmap { get; }

    public override int Width => Bitmap.Width;

    public override int Height => Bitmap.Height;

    public override void WritePng(Stream destination) => Bitmap.Save(destination, ImageFormat.Png);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Bitmap.Dispose();
        }

        base.Dispose(disposing);
    }
}
