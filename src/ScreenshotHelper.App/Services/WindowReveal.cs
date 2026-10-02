using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App.Services;

/// <summary>
/// Shows windows only once they're drawn, then fades them in. Windows makes an Avalonia window visible before its first frame is composed,
/// so for a moment the user sees an empty, see-through frame (about 100 ms at launch, measured with tools/Record-Frames.ps1), or a
/// re-shown window's stale last frame. The window is shown cloaked (alive, focusable, but not composed on screen), and once two frames
/// have rendered it's uncloaked at zero opacity and faded in, so it appears smoothly and fully drawn.
/// </summary>
/// <remarks>
/// Windows' own open animation can't be used instead: it plays when a window is first shown, which is while it's still cloaked, and
/// DWM doesn't replay it on uncloak or on a hide and re-show (both were tried and filmed). The fade uses layered-window opacity, which
/// covers the whole window, title bar included, and keeps the window visible to Windows throughout, so focus taken while it was cloaked
/// (the caption box) is never lost.
/// </remarks>
internal static class WindowReveal
{
    /// <summary>Close to Windows' own open animation.</summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Reveal anyway after this long, so a window whose frames never arrive (e.g. shown minimized) can't stay invisible.</summary>
    private static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Shows <paramref name="window"/> cloaked and fades it in once drawn. <paramref name="beforeReveal"/> runs just before it appears
    /// (window styles, final position). Without a native window (headless tests) it simply shows the window.
    /// </summary>
    public static void ShowWhenDrawn(Window window, Action? beforeReveal = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (!WindowsDesktop.SetCloaked(handle, true))
        {
            window.Show();
            beforeReveal?.Invoke();
            return;
        }

        var revealed = false;
        void Reveal()
        {
            if (revealed)
            {
                return;
            }

            revealed = true;
            beforeReveal?.Invoke();
            var wasLayered = WindowsDesktop.BeginFadeIn(handle);
            WindowsDesktop.SetCloaked(handle, false);
            FadeIn(handle, wasLayered);
        }

        window.Show();

        // A callback for the next frame runs before that frame renders; one for the frame after runs once the first is committed.
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Reveal()));
        DispatcherTimer.RunOnce(Reveal, Fallback);
    }

    /// <summary>Raises the window's opacity from 0 to 255 over <see cref="FadeDuration"/>, easing out (fast start, gentle finish).</summary>
    private static void FadeIn(IntPtr handle, bool wasLayered)
    {
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1.0, clock.Elapsed / FadeDuration);
            if (progress >= 1.0)
            {
                timer.Stop();
                WindowsDesktop.EndFadeIn(handle, wasLayered);
                return;
            }

            var eased = 1 - Math.Pow(1 - progress, 3);
            WindowsDesktop.SetFadeOpacity(handle, (byte)Math.Round(255 * eased));
        };
        timer.Start();
    }
}
