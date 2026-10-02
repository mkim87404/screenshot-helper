using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.App.Views;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Settings;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App;

/// <summary>Application root: wires services, windows and tray, applies the theme, and owns shutdown.</summary>
public partial class App : Application, IDisposable
{
    private static AppPaths? s_paths;
    private static AppLog s_log = AppLog.Null;

    private AppServices? _services;
    private SessionCoordinator? _coordinator;
    private MainWindow? _mainWindow;
    private TrayController? _tray;
    private AvaloniaSessionUi? _sessionUi;
    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public void Dispose()
    {
        DisposeServices();
        GC.SuppressFinalize(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUiException;
            var paths = s_paths ?? AppPaths.ForCurrentUser();
            var platform = new PlatformServices(
                new WindowsHotkeyService(),
                new WindowsKeyboard(),
                new WindowsScreenCapture(s_log),
                new WindowsRecycleBin(),
                new WindowsFileRevealer(s_log),
                new WindowsSoundPlayer(),
                new WindowsClipboard());
            _services = new AppServices(paths, s_log, platform);
            ApplyTheme(_services.Settings.Theme);
            _services.SettingsChanged += s => Dispatcher.UIThread.Post(() => ApplyTheme(s.Theme));

            _mainWindow = new MainWindow();
            _mainWindow.RestoreSize(_services.State);
            var pickFolder = () => _mainWindow.PickFolderAsync();
            _sessionUi = new AvaloniaSessionUi(_services, _mainWindow, Shutdown);
            _coordinator = new SessionCoordinator(_services, _sessionUi, action => Dispatcher.UIThread.Post(action));
            _sessionUi.Attach(_coordinator);
            var viewModel = new MainWindowViewModel(
                _services,
                new HomeViewModel(_services, _coordinator, pickFolder),
                new SettingsViewModel(_services),
                new RenumberViewModel(_services, pickFolder));
            _sessionUi.AttachViewModel(viewModel);
            _mainWindow.DataContext = viewModel;
            _mainWindow.Closing += OnMainWindowClosing;

            _tray = new TrayController(this, _services, _coordinator, ShowMainWindow, Shutdown);
            _sessionUi.AttachTray(_tray);

            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += (_, _) => DisposeServices();
            WindowReveal.ShowWhenDrawn(_mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shows the window, or during a session (when it stays hidden) a toast saying the app is already running.</summary>
    private void OnSecondLaunch()
    {
        if (_coordinator is { IsRunning: true })
        {
            _coordinator.ReportSecondLaunch();
            return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null || _coordinator is { IsRunning: true })
        {
            // During a session the window stays hidden; the tray is the control surface.
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            WindowReveal.ShowWhenDrawn(_mainWindow);
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    /// <summary>Closing the window exits the app (the tray icon is for sessions, not a background mode).</summary>
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_shuttingDown)
        {
            e.Cancel = true;
            Shutdown();
        }
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Windows logoff/shutdown or an explicit request: end any session first so queued shots are written and hotkeys released.
        if (!_shuttingDown)
        {
            e.Cancel = true;
            Shutdown();
        }
    }

    /// <summary>Starts an orderly exit from a synchronous caller (menu, window close); any failure is logged rather than lost.</summary>
    private void Shutdown() =>
        ShutdownAsync().ContinueWith(
            t => s_log.Error("Shutdown failed.", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>Ends any session (so queued shots are written and hotkeys released), saves the window size, then exits.</summary>
    private async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        try
        {
            SaveWindowSize();
            if (_coordinator is not null)
            {
                await _coordinator.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            s_log.Error("Error while ending the session during shutdown.", ex);
        }
        finally
        {
            // Always leave: an error while ending the session must not keep a half-shut app running.
            _tray?.Dispose();
            _mainWindow?.Close();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }

    private void SaveWindowSize()
    {
        if (_mainWindow is null || _services is null)
        {
            return;
        }

        var (size, maximized) = _mainWindow.Placement;
        _services.UpdateState(s => s with { WindowWidth = size.Width, WindowHeight = size.Height, WindowMaximized = maximized });
    }

    private void DisposeServices()
    {
        (_mainWindow?.DataContext as MainWindowViewModel)?.Renumber.Dispose();
        _tray?.Dispose();
        _sessionUi?.Dispose();
        _services?.Dispose();
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>UI-thread exceptions: log, tell the user, keep the app alive (each UI action is independent).</summary>
    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        s_log.Error("Unhandled UI exception.", e.Exception);
        _sessionUi?.ShowToast(new Core.Feedback.FeedbackEvent(Core.Feedback.FeedbackKind.Error, "Something went wrong — see the log for details"));
        e.Handled = true;
    }

    /// <summary>Called by <see cref="Program"/> before the lifetime starts (the previewer and tests skip it and get defaults).</summary>
    public static void Configure(AppPaths paths, AppLog log)
    {
        s_paths = paths;
        s_log = log;
    }

    /// <summary>A second launch asked this instance to show itself (called on a thread-pool thread).</summary>
    public static void RequestActivation() =>
        Dispatcher.UIThread.Post(() => (Current as App)?.OnSecondLaunch());
}
