using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Platform.Windows.Interop;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// Puts a screenshot on the clipboard as both CF_DIB (understood everywhere) and "PNG" (lossless, preferred by browsers and Office).
/// Clipboard ownership needs a window, so this uses its own message thread's message-only window.
/// </summary>
public sealed class WindowsClipboard : IClipboardImage, IDisposable
{
    private readonly MessageThread _thread = new("ScreenshotHelper.Clipboard");
    private readonly uint _pngFormat = RegisterClipboardFormat("PNG");

    public void SetImage(CapturedImage image)
    {
        if (image is not GdiCapturedImage gdi)
        {
            throw new ArgumentException("Only images captured by WindowsScreenCapture are supported.", nameof(image));
        }

        var dib = ToDib(gdi.Bitmap);
        byte[] png;
        using (var stream = new MemoryStream())
        {
            gdi.WritePng(stream);
            png = stream.ToArray();
        }

        _thread.Invoke(() =>
        {
            OpenWithRetry(_thread.WindowHandle);
            try
            {
                EmptyClipboard();
                SetGlobal(CF_DIB, dib);
                SetGlobal(_pngFormat, png);
            }
            finally
            {
                CloseClipboard();
            }

            return 0;
        });
    }

    public void Dispose() => _thread.Dispose();

    /// <summary>Another app may hold the clipboard briefly; retry for ~0.5 s before giving up.</summary>
    private static void OpenWithRetry(IntPtr owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new IOException("The clipboard is in use by another application.");
    }

    /// <summary>Copies bytes into movable global memory; on success the clipboard owns it, otherwise it's freed here.</summary>
    private static void SetGlobal(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == IntPtr.Zero)
        {
            throw new IOException("Not enough memory to copy the image to the clipboard.");
        }

        try
        {
            var target = GlobalLock(handle);
            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(handle);
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                throw new IOException($"SetClipboardData failed ({Marshal.GetLastPInvokeError()}).");
            }

            handle = IntPtr.Zero;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    /// <summary>BITMAPINFOHEADER + bottom-up 32-bit BGR pixels.</summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        const int headerSize = 40;
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = width * 4;
        var dib = new byte[headerSize + (stride * height)];
        var header = dib.AsSpan(0, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                var source = data.Scan0 + (y * data.Stride);
                Marshal.Copy(source, dib, headerSize + ((height - 1 - y) * stride), stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return dib;
    }
}
