using ScreenshotHelper.Core.Platform;

namespace ScreenshotHelper.Core.IO;

/// <summary>Thrown when a no-clobber write or rename finds its destination already taken.</summary>
public sealed class DestinationExistsException(string path)
    : IOException($"'{Path.GetFileName(path)}' already exists.")
{
    public string DestinationPath { get; } = path;
}

/// <summary>Durable, never-overwriting screenshot writes and renames.</summary>
public static class ShotFile
{
    /// <summary>Prefix of in-progress shot files (hidden, and skipped by folder scans).</summary>
    public const string TempPrefix = ".~sshelper-";

    /// <summary>
    /// Writes <paramref name="image"/> to a hidden temp file in <paramref name="folder"/>, flushes it to disk, then renames it to
    /// <paramref name="fileName"/> without overwriting. A crash can leave only a temp file (cleaned by <see cref="CleanOrphans"/>),
    /// never a half-written screenshot under a real name.
    /// </summary>
    /// <returns>The final full path.</returns>
    /// <exception cref="DestinationExistsException">A file with that name appeared (e.g. created by another program).</exception>
    public static string Commit(string folder, string fileName, CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var final = Path.Combine(folder, fileName);
        var temp = Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(temp, FileAttributes.Hidden);
                }

                image.WritePng(stream);
                stream.Flush(flushToDisk: true);
            }

            // The rename keeps attributes, so un-hide first or the finished screenshot would stay hidden.
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(temp, FileAttributes.Normal);
            }

            MoveNoClobber(temp, final);
            return final;
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    /// <summary>
    /// Renames without overwriting, retrying briefly when another program holds the file open (e.g. an image viewer).
    /// </summary>
    /// <exception cref="DestinationExistsException">The destination exists.</exception>
    /// <exception cref="IOException">Still locked after the retries.</exception>
    public static void Rename(string from, string to, int attempts = 3, int delayMs = 100)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                MoveNoClobber(from, to);
                return;
            }
            catch (IOException ex) when (ex is not DestinationExistsException and not FileNotFoundException && attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Deletes temp files left behind by a crash. Best effort.</summary>
    public static int CleanOrphans(string folder)
    {
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, TempPrefix + "*.tmp"))
            {
                // Renumber cycle temps share the prefix but are still referenced by a journal; only delete shot temps.
                if (Path.GetFileName(path).StartsWith(Renumber.RenumberPlanner.TempPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AtomicFile.TryDelete(path);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }

    private static void MoveNoClobber(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
        }
        catch (IOException) when (File.Exists(to) || Directory.Exists(to))
        {
            throw new DestinationExistsException(to);
        }
    }
}
