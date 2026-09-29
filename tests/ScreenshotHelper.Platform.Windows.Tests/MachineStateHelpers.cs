using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ScreenshotHelper.Platform.Windows.Interop;

namespace ScreenshotHelper.Platform.Windows.Tests;

/// <summary>Test category for tests that show windows or temporarily replace shared state; they run locally and in CI (see README).</summary>
internal static class TestCategories
{
    public const string Disruptive = "Disruptive";
}

/// <summary>
/// Removes a test's own file from the current user's Recycle Bin, so a test that recycles something leaves the bin as it found it.
/// Each recycled file is stored as <c>$R&lt;id&gt;</c> (content) plus <c>$I&lt;id&gt;</c> (metadata holding the original path).
/// </summary>
internal static class RecycleBinCleanup
{
    /// <summary>Deletes the Recycle Bin entries whose original path is <paramref name="originalPath"/>; returns how many were removed.</summary>
    public static int Remove(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID.");
        var bin = Path.Combine(Path.GetPathRoot(full)!, "$Recycle.Bin", sid);
        if (!Directory.Exists(bin))
        {
            return 0;
        }

        var removed = 0;
        foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
        {
            if (!string.Equals(ReadOriginalPath(info), full, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
            File.Delete(content);
            File.Delete(info);
            removed++;
        }

        return removed;
    }

    /// <summary>Reads the original path from a <c>$I</c> file (format 1: fixed 260-char path at offset 24; format 2: length-prefixed at 28).</summary>
    private static string? ReadOriginalPath(string infoFile)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoFile);
            var version = BitConverter.ToInt64(bytes, 0);
            return version switch
            {
                2 => Encoding.Unicode.GetString(bytes, 28, (BitConverter.ToInt32(bytes, 24) - 1) * 2),
                1 => Encoding.Unicode.GetString(bytes, 24, 520).TrimEnd('\0'),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Snapshot of the clipboard's memory-based formats (text, images, file lists, rich formats), restored after a test that overwrites it.
/// Handle-based formats such as CF_BITMAP aren't copied; Windows synthesises them from the memory-based ones (e.g. CF_DIB) on demand.
/// </summary>
internal sealed class ClipboardSnapshot : IDisposable
{
    private const uint GMEM_MOVEABLE = 0x0002;

    // Formats whose data isn't an HGLOBAL (GDI or owner handles) — copying them as memory would be invalid.
    private static readonly HashSet<uint> HandleFormats = [2, 3, 9, 14, 0x0080, 0x0081, 0x0082, 0x0083, 0x008E];

    private readonly MessageThread _thread = new("ScreenshotHelper.Tests.Clipboard");
    private readonly List<(uint Format, byte[] Data)> _items = [];

    private ClipboardSnapshot()
    {
    }

    public static ClipboardSnapshot Take()
    {
        var snapshot = new ClipboardSnapshot();
        snapshot._thread.Invoke(() =>
        {
            OpenWithRetry(snapshot._thread.WindowHandle);
            try
            {
                for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                {
                    if (!HandleFormats.Contains(format) && ReadGlobal(GetClipboardData(format)) is { } data)
                    {
                        snapshot._items.Add((format, data));
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }

            return 0;
        });
        return snapshot;
    }

    /// <summary>Puts the snapshot back (an empty snapshot leaves the clipboard empty, as it was).</summary>
    public void Dispose()
    {
        _thread.Invoke(() =>
        {
            OpenWithRetry(_thread.WindowHandle);
            try
            {
                EmptyClipboard();
                foreach (var (format, data) in _items)
                {
                    var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
                    Marshal.Copy(data, 0, GlobalLock(handle), data.Length);
                    GlobalUnlock(handle);
                    if (SetClipboardData(format, handle) == IntPtr.Zero)
                    {
                        GlobalFree(handle);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }

            return 0;
        });
        _thread.Dispose();
    }

    private static byte[]? ReadGlobal(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (int)GlobalSize(handle);
        var source = GlobalLock(handle);
        if (source == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var data = new byte[size];
            Marshal.Copy(source, data, 0, size);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static void OpenWithRetry(IntPtr owner)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(owner))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new IOException("The clipboard is in use by another application.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
