using System.Globalization;
using System.Threading.Channels;

namespace ScreenshotHelper.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file log (one file per UTC day). Callers only enqueue; a single background writer appends and flushes each batch,
/// so no caller (including the UI thread) ever waits on the disk. <see cref="Flush"/> drains the queue synchronously, which the crash
/// handlers and app exit use so nothing already logged is lost. Logging never throws: a broken log must not take the app down with it.
/// </summary>
public sealed class AppLog : IDisposable
{
    /// <summary>Default number of daily log files kept; a day's file is typically a few KB, so this bounds the folder to well under a MB.</summary>
    public const int DefaultRetentionDays = 30;

    private readonly string _directory;
    private readonly Channel<(DateTime At, string Line)>? _queue;
    private readonly Task? _writer;

    public AppLog(string directory)
    {
        _directory = directory;
        if (directory.Length > 0)
        {
            _queue = Channel.CreateUnbounded<(DateTime, string)>(new UnboundedChannelOptions { SingleReader = true });
            _writer = Task.Run(WriteLoopAsync);
        }
    }

    /// <summary>A log that discards everything (tests, or when the log folder is unusable).</summary>
    public static AppLog Null { get; } = new(string.Empty);

    public string Directory => _directory;

    /// <summary>
    /// Deletes daily log files older than <paramref name="retentionDays"/> (by the date in the name). Run at start-up so the folder can't
    /// grow forever. Best effort: a locked or unreadable file is simply skipped.
    /// </summary>
    public int Prune(int retentionDays = DefaultRetentionDays, DateTime? utcToday = null)
    {
        if (_directory.Length == 0 || !System.IO.Directory.Exists(_directory))
        {
            return 0;
        }

        var cutoff = (utcToday ?? DateTime.UtcNow).Date.AddDays(-retentionDays);
        var removed = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "app-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(path)["app-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                && day.Date < cutoff)
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return removed;
    }

    public void Info(string message) => Write("INFO ", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// Stops accepting lines and waits (up to <paramref name="timeout"/>) until everything queued is on disk. Used on exit and from the
    /// crash handlers; afterwards further lines are dropped.
    /// </summary>
    public void Flush(TimeSpan timeout)
    {
        if (_queue is null || _writer is null)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose() => Flush(TimeSpan.FromSeconds(2));

    private void Write(string level, string message, Exception? exception)
    {
        if (_queue is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}");
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        _queue.Writer.TryWrite((now, line));
    }

    /// <summary>Appends queued lines in batches (whatever arrived together), flushing each batch to disk.</summary>
    private async Task WriteLoopAsync()
    {
        var reader = _queue!.Reader;
        var batch = new List<(DateTime At, string Line)>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            // Lines are grouped by UTC day so a batch spanning midnight lands in the right files.
            foreach (var day in batch.GroupBy(i => i.At.Date))
            {
                AppendToDayFile(day.Key, day.Select(i => i.Line));
            }
        }
    }

    private void AppendToDayFile(DateTime day, IEnumerable<string> lines)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"app-{day:yyyyMMdd}.log"));
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
