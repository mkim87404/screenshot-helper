using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Settings;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App.Views;

/// <summary>
/// Passive on-screen notification: never takes focus, lets clicks through, and is excluded from screen capture so it can't end up in a
/// screenshot. One window is reused; a new message replaces the current one and restarts its timer.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _overlayApplied;

    public ToastWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _timer.Tick += OnTimerTick;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        };
    }

    public void Show(FeedbackEvent feedback, ToastCorner corner)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        MessageText.Text = feedback.Message;
        Card.BorderBrush = new SolidColorBrush(feedback.HasCaveat ? AccentFor(FeedbackKind.Warning) : AccentFor(feedback.Kind));
        _timer.Interval = feedback.Kind == FeedbackKind.Error ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2.2);

        if (!IsVisible)
        {
            Show();
        }

        if (!_overlayApplied && TryGetPlatformHandle()?.Handle is { } handle)
        {
            WindowsDesktop.MakePassiveOverlay(handle);
            _overlayApplied = true;
        }

        // Position after layout so the size is known.
        Dispatcher.UIThread.Post(() => PlaceAt(corner), DispatcherPriority.Loaded);
        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Hide();
    }

    /// <summary>Places the toast in a corner of the working area of the screen under the mouse (where the user is looking).</summary>
    private void PlaceAt(ToastCorner corner)
    {
        var screen = Screens.ScreenFromPoint(PointerScreenPosition()) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        var margin = (int)(16 * scale);
        var x = corner is ToastCorner.TopLeft or ToastCorner.BottomLeft ? area.X + margin : area.Right - width - margin;
        var y = corner is ToastCorner.TopLeft or ToastCorner.TopRight ? area.Y + margin : area.Bottom - height - margin;
        Position = new PixelPoint(x, y);
    }

    private static PixelPoint PointerScreenPosition()
    {
        var (x, y) = WindowsDesktop.CursorPosition();
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
