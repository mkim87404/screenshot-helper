using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Settings;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App.Views;

/// <summary>
/// Passive on-screen notification: never takes focus, lets clicks through, and is excluded from screen capture so it can't end up in a
/// screenshot. One window is reused; a new message replaces the current one and restarts its timer. The "paused" message stays up until
/// the session resumes or ends, so a paused session (where the capture keys deliberately do nothing) is never mistaken for an active one.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private FeedbackEvent? _sticky;
    private FeedbackEvent? _shown;
    private ToastCorner _corner;

    public ToastWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer();
        _timer.Tick += OnTimerTick;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        };
    }

    /// <summary>How long a message stays up; errors stay longer. Settable so tests don't wait in real time.</summary>
    internal TimeSpan DisplayTime { get; init; } = TimeSpan.FromSeconds(2.2);

    internal TimeSpan ErrorDisplayTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The message shown now (tests and diagnostics).</summary>
    internal string? Message => IsVisible ? _shown?.Message : null;

    public void Show(FeedbackEvent feedback, ToastCorner corner)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        _sticky = feedback.Kind switch
        {
            FeedbackKind.Paused => feedback,
            FeedbackKind.Resumed or FeedbackKind.SessionStarted or FeedbackKind.SessionEnded => null,
            _ => _sticky,
        };
        _corner = corner;
        Display(feedback);
    }

    /// <summary>Drops a lingering "paused" message (the session is over, however it ended).</summary>
    public void ClearPaused()
    {
        if (_sticky is not null && ReferenceEquals(_shown, _sticky))
        {
            _timer.Stop();
            Hide();
        }

        _sticky = null;
    }

    private void Display(FeedbackEvent feedback)
    {
        _shown = feedback;
        MessageText.Text = feedback.Message;
        Card.BorderBrush = new SolidColorBrush(feedback.HasCaveat ? AccentFor(FeedbackKind.Warning) : AccentFor(feedback.Kind));
        Card.HorizontalAlignment = _corner is ToastCorner.TopLeft or ToastCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Card.VerticalAlignment = _corner is ToastCorner.TopLeft or ToastCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // The window's size never changes, so its place is known before it's shown: no move after it appears.
        var position = PositionFor(_corner);
        if (!IsVisible)
        {
            Position = position;
            WindowReveal.ShowWhenDrawn(this, ApplyOverlay);
        }
        else if (Position != position)
        {
            Position = position;
        }

        _timer.Stop();
        if (!ReferenceEquals(feedback, _sticky))
        {
            _timer.Interval = feedback.Kind == FeedbackKind.Error ? ErrorDisplayTime : DisplayTime;
            _timer.Start();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_sticky is not null)
        {
            // Back to "paused" after any message shown in the meantime (e.g. undo from the tray menu).
            Display(_sticky);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    /// Makes the toast click-through, never activated and capture-excluded. Applied on every show: Avalonia rewrites a window's extended
    /// styles when it's shown again, which used to drop click-through after the first toast.
    /// </summary>
    private void ApplyOverlay()
    {
        if (TryGetPlatformHandle()?.Handle is { } handle)
        {
            WindowsDesktop.MakePassiveOverlay(handle);
        }
    }

    /// <summary>
    /// The window's position in the chosen corner of the working area of the screen under the mouse (where the user is looking), kept
    /// clear of an auto-hide taskbar: Windows counts its strip as working area, but it covers the corner whenever it slides out.
    /// </summary>
    private PixelPoint PositionFor(ToastCorner corner)
    {
        var (cursorX, cursorY) = WindowsDesktop.CursorPosition();
        var screen = Screens.ScreenFromPoint(new PixelPoint(cursorX, cursorY)) ?? Screens.Primary;
        if (screen is null)
        {
            return Position;
        }

        var bounds = screen.Bounds;
        var insets = WindowsDesktop.AutoHideBarInsets(new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        var scale = screen.Scaling;
        return Place(screen.WorkingArea, insets, new PixelSize((int)(Width * scale), (int)(Height * scale)), (int)(16 * scale), corner);
    }

    /// <summary>
    /// Where a window of <paramref name="size"/> goes in <paramref name="corner"/> of a working area, kept clear of auto-hide bars on any
    /// edge (<paramref name="autoHideInsets"/>: how far each edge's bar reaches in when it slides out), all in physical pixels.
    /// </summary>
    internal static PixelPoint Place(PixelRect workingArea, (int Left, int Top, int Right, int Bottom) autoHideInsets, PixelSize size, int margin, ToastCorner corner)
    {
        var (left, top, right, bottom) = autoHideInsets;
        var area = new PixelRect(workingArea.X + left, workingArea.Y + top, workingArea.Width - left - right, workingArea.Height - top - bottom);
        var x = corner is ToastCorner.TopLeft or ToastCorner.BottomLeft ? area.X + margin : area.Right - size.Width - margin;
        var y = corner is ToastCorner.TopLeft or ToastCorner.TopRight ? area.Y + margin : area.Bottom - size.Height - margin;
        return new PixelPoint(x, y);
    }

    private static Color AccentFor(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Error => Color.FromRgb(0xEF, 0x44, 0x44),
        FeedbackKind.Warning => Color.FromRgb(0xF5, 0x9E, 0x0B),
        FeedbackKind.Paused or FeedbackKind.CaptionCancelled or FeedbackKind.TimestampDisarmed => Color.FromRgb(0x9C, 0xA3, 0xAF),
        FeedbackKind.MainSaved or FeedbackKind.SubSaved => Color.FromRgb(0x22, 0xC5, 0x5E),
        _ => Color.FromRgb(0x81, 0x8C, 0xF8),
    };
}
