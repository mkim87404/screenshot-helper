namespace ScreenshotHelper.Core.Platform;

/// <summary>What a capture covers.</summary>
public enum CaptureTarget
{
    MonitorUnderCursor,
    PrimaryMonitor,
    AllMonitors,
    ActiveWindow,
}

/// <summary>A captured screen image held in memory until it is written (so prompts never appear in the shot).</summary>
public abstract class CapturedImage : IDisposable
{
    public abstract int Width { get; }
    public abstract int Height { get; }

    /// <summary>Encodes the image as PNG into <paramref name="destination"/>.</summary>
    public abstract void WritePng(Stream destination);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>Captures the screen.</summary>
public interface IScreenCapture
{
    CapturedImage Capture(CaptureTarget target);
}

/// <summary>Sends files to the OS recycle bin / trash (never permanent deletion).</summary>
public interface IRecycleBin
{
    void Recycle(string path);
}

/// <summary>Shows files in the OS file manager.</summary>
public interface IFileRevealer
{
    /// <summary>Opens <paramref name="folder"/> with <paramref name="files"/> selected (first item focused); falls back to fewer selections or just the folder.</summary>
    void Reveal(string folder, IReadOnlyList<string> files);
}

/// <summary>Copies an image to the clipboard.</summary>
public interface IClipboardImage
{
    void SetImage(CapturedImage image);
}

/// <summary>Plays short in-memory WAV sounds without blocking.</summary>
public interface ISoundPlayer
{
    void Play(byte[] wav);
}
