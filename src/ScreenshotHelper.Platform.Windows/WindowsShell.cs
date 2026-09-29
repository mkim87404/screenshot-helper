using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Platform;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// Sends files to the Recycle Bin via SHFileOperation. FOF_WANTNUKEWARNING makes Windows ask before any permanent delete
/// (e.g. the bin is disabled or full), so the app itself can never silently destroy a file.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public void Recycle(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("File to recycle not found.", full);
        }

        var root = Path.GetPathRoot(full)!;
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBin(root, ref info) != 0)
        {
            throw new IOException($"The drive {root} has no Recycle Bin (e.g. a network share).");
        }

        // pFrom is a double-NUL-terminated list.
        var from = Marshal.StringToHGlobalUni(full + "\0");
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
            };
            var result = SHFileOperation(ref op);
            if (result != 0 || op.fAnyOperationsAborted != 0)
            {
                throw new IOException($"Couldn't move {Path.GetFileName(full)} to the Recycle Bin (code 0x{result:X}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}

/// <summary>
/// Opens Explorer with several files selected via SHOpenFolderAndSelectItems (which needs COM, so it runs on its own STA thread).
/// Falls back to selecting one file with explorer.exe, then to just opening the folder.
/// </summary>
public sealed class WindowsFileRevealer(AppLog log) : IFileRevealer
{
    public void Reveal(string folder, IReadOnlyList<string> files)
    {
        var existing = FilesToReveal(folder, files);
        if (existing is null)
        {
            // explorer.exe given a missing path silently opens its default location (Documents), leaving a stray window.
            log.Warn($"Not opening Explorer: the folder {folder} doesn't exist.");
            return;
        }

        var thread = new Thread(() => RevealOnStaThread(folder, existing)) { IsBackground = true, Name = "ScreenshotHelper.Reveal" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// What to show: the files that still exist inside <paramref name="folder"/> (empty = just open the folder), or null when the
    /// folder itself is gone and nothing should be opened.
    /// </summary>
    internal static List<string>? FilesToReveal(string folder, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return files
            .Where(f => File.Exists(f) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), root, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void RevealOnStaThread(string folder, List<string> files)
    {
        try
        {
            if (files.Count > 0 && TrySelectMany(folder, files))
            {
                return;
            }

            if (files.Count > 0)
            {
                // Quoting the path keeps commas in captions from being read as extra /select arguments.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = false })?.Dispose();
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error($"Couldn't open Explorer for {folder}.", ex);
        }
    }

    private bool TrySelectMany(string folder, List<string> files)
    {
        var folderPidl = IntPtr.Zero;
        var itemPidls = new List<IntPtr>(files.Count);
        try
        {
            if (SHParseDisplayName(Path.GetFullPath(folder), IntPtr.Zero, out folderPidl, 0, out _) != 0)
            {
                return false;
            }

            foreach (var file in files)
            {
                if (SHParseDisplayName(file, IntPtr.Zero, out var pidl, 0, out _) == 0)
                {
                    itemPidls.Add(pidl);
                }
            }

            if (itemPidls.Count == 0)
            {
                return false;
            }

            // Each item must be a child ID relative to the folder: the last ID of its absolute PIDL.
            var children = itemPidls.Select(ILFindLastID).ToArray();
            var hr = SHOpenFolderAndSelectItems(folderPidl, (uint)children.Length, children, 0);
            if (hr != 0)
            {
                log.Warn($"SHOpenFolderAndSelectItems failed (0x{hr:X8}); falling back.");
            }

            return hr == 0;
        }
        finally
        {
            foreach (var pidl in itemPidls)
            {
                ILFree(pidl);
            }

            if (folderPidl != IntPtr.Zero)
            {
                ILFree(folderPidl);
            }
        }
    }
}
