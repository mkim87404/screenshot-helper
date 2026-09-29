using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.App.Services;

/// <summary>
/// The system-tray icon: during a session it shows the state (active / paused / armed / error) as a badge and exposes session controls,
/// so the hidden main window isn't needed. Badged icons are drawn once at start-up from the app icon.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Application _app;
    private readonly AppServices _services;
    private readonly SessionCoordinator _coordinator;
    private readonly TrayIcon _icon;
    private readonly Dictionary<string, WindowIcon> _icons = [];
    private readonly NativeMenuItem _pause = new("Pause");
    private readonly NativeMenuItem _caption = new("Caption next shot…");
    private readonly NativeMenuItem _undo = new("Undo last shot");
    private readonly NativeMenuItem _end = new("End session");
    private readonly NativeMenuItem _show = new("Show Screenshot Helper");
    private readonly List<(NativeMenuItem Item, CaptureTarget Target)> _targets = [];

    public TrayController(Application app, AppServices services, SessionCoordinator coordinator, Action showWindow, Action exit)
    {
        _app = app;
        _services = services;
        _coordinator = coordinator;

        _pause.Click += (_, _) => _coordinator.TogglePause();
        _caption.Click += (_, _) => _ = _coordinator.CaptionAsync();
        _undo.Click += (_, _) => _coordinator.Undo();
        _end.Click += (_, _) => _ = _coordinator.EndAsync();
        _show.Click += (_, _) => showWindow();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        var targetMenu = new NativeMenu();
        foreach (var option in CaptureTargetOption.All)
        {
            var item = new NativeMenuItem(option.Label) { ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) =>
            {
                _coordinator.SetCaptureTarget(option.Value);
                Update();
            };
            targetMenu.Items.Add(item);
            _targets.Add((item, option.Value));
        }

        var menu = new NativeMenu();
        menu.Items.Add(_show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_caption);
        menu.Items.Add(_undo);
        menu.Items.Add(new NativeMenuItem("Capture target") { Menu = targetMenu });
        menu.Items.Add(_end);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _icon = new TrayIcon { Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) =>
        {
            if (_coordinator.IsRunning)
            {
                _coordinator.TogglePause();
            }
            else
            {
                showWindow();
            }
        };
        TrayIcon.SetIcons(app, [_icon]);
        Update();
    }

    /// <summary>Refreshes icon, tooltip and menu state from the coordinator (UI thread).</summary>
    public void Update()
    {
        var running = _coordinator.IsRunning;
        var phase = _coordinator.Phase;
        var status = _coordinator.Status;

        var (badge, badgeColor) = !running ? (null, default(Color))
            : _coordinator.HasError ? ("!", Color.FromRgb(0xDC, 0x26, 0x26))
            : phase == SessionPhase.Paused ? ("❚❚", Color.FromRgb(0x6B, 0x72, 0x80))
            : status?.TimestampArmed == true || status?.Caption is not null ? ("•", Color.FromRgb(0xF5, 0x9E, 0x0B))
            : ("●", Color.FromRgb(0x16, 0xA3, 0x4A));
        _icon.Icon = IconFor(badge, badgeColor);

        _icon.ToolTipText = !running
            ? "Screenshot Helper"
            : phase == SessionPhase.Paused
                ? $"Screenshot Helper — paused ({_services.Describe(HotkeyAction.PauseResume)} to resume)"
                : $"Screenshot Helper — next {status?.NextMainText} / {status?.NextSubText}"
                  + (status?.TimestampArmed == true ? " · timestamp" : string.Empty)
                  + (status?.Caption is not null ? " · caption" : string.Empty);

        _pause.Header = phase == SessionPhase.Paused ? "Resume" : "Pause";
        var controllable = running && phase is SessionPhase.Active or SessionPhase.Paused;
        _pause.IsEnabled = controllable;
        _caption.IsEnabled = running && phase == SessionPhase.Active;
        _undo.IsEnabled = controllable;
        _end.IsEnabled = controllable;
        _show.IsEnabled = !running;
        var current = status?.CaptureTarget ?? _services.Settings.CaptureTarget;
        foreach (var (item, target) in _targets)
        {
            item.IsChecked = target == current;
            item.IsEnabled = running;
        }
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        TrayIcon.SetIcons(_app, null);
        _icon.Dispose();
    }

    private WindowIcon IconFor(string? badge, Color color)
    {
        var key = badge ?? "none";
        if (_icons.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var baseStream = AssetLoader.Open(new Uri("avares://ScreenshotHelper/Assets/app-256.png"));
        using var baseImage = new Bitmap(baseStream);
        const int size = 64;
        using var target = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(baseImage, new Rect(0, 0, size, size));
            if (badge is not null)
            {
                // Badge bottom-right with a white ring so it reads on light and dark taskbars.
                var centre = new Point(size - 17, size - 17);
                context.DrawEllipse(Brushes.White, null, centre, 17, 17);
                context.DrawEllipse(new SolidColorBrush(color), null, centre, 14, 14);
            }
        }

        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        var icon = new WindowIcon(stream);
        _icons[key] = icon;
        return icon;
    }
}
