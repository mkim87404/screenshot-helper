using System.Runtime.InteropServices;
using ScreenshotHelper.Core.Platform;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// Plays in-memory WAVs asynchronously with PlaySound. With SND_MEMORY|SND_ASYNC the OS reads the buffer while playing, so each sound
/// is copied to unmanaged memory that is freed only after playback of it has been stopped (never while the OS may still read it).
/// A new sound replaces the one playing, which suits short feedback blips.
/// </summary>
public sealed class WindowsSoundPlayer : ISoundPlayer, IDisposable
{
    private readonly Lock _gate = new();
    private IntPtr _current;
    private bool _disposed;

    public void Play(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StopAndFree();
            _current = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, _current, wav.Length);
            PlaySound(_current, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopAndFree();
        }
    }

    private void StopAndFree()
    {
        if (_current == IntPtr.Zero)
        {
            return;
        }

        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        Marshal.FreeHGlobal(_current);
        _current = IntPtr.Zero;
    }
}
