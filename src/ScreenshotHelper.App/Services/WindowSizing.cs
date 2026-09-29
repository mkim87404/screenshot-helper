using Avalonia;

namespace ScreenshotHelper.App.Services;

/// <summary>
/// Main-window sizing: the last size is remembered (position is not, so the window can't reopen off-screen or stuck in a corner
/// after a monitor change) and always clamped to the screen it opens on.
/// </summary>
public static class WindowSizing
{
    public static Size Default { get; } = new(900, 860);

    public static Size Minimum { get; } = new(720, 560);

    /// <summary>Fraction of the working area the window may take, leaving room for the taskbar and window chrome.</summary>
    private const double MaxShare = 0.95;

    /// <summary>The size to open at: the remembered size (or the default), clamped between the minimum and the screen's working area.</summary>
    public static Size Fit(Size? remembered, Size workingArea)
    {
        var desired = remembered ?? Default;
        var max = new Size(workingArea.Width * MaxShare, workingArea.Height * MaxShare);
        var min = MinimumFor(workingArea);
        return new Size(Math.Clamp(desired.Width, min.Width, max.Width), Math.Clamp(desired.Height, min.Height, max.Height));
    }

    /// <summary>The minimum size, shrunk on screens too small for it (e.g. 1366×768 at 150 % scaling).</summary>
    public static Size MinimumFor(Size workingArea) =>
        new(Math.Min(Minimum.Width, workingArea.Width * MaxShare), Math.Min(Minimum.Height, workingArea.Height * MaxShare));
}
