using Avalonia;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App;

/// <summary>Process entry point: single-instance check, crash safety net, then the Avalonia desktop lifetime.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.ForCurrentUser();
        using var log = new AppLog(paths.Logs);
        InstallCrashHandlers(log);
        log.Prune();

        using var instance = SingleInstance.Acquire(App.RequestActivation);
        if (!instance.IsFirstInstance)
        {
            // The running instance was asked to show itself.
            return 0;
        }

        log.Info($"Starting Screenshot Helper {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion}.");
        try
        {
            return BuildAvaloniaApp()
                .AfterSetup(_ => App.Configure(paths, log))
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            log.Error("Fatal error; the app is closing.", ex);
            throw;
        }
        finally
        {
            // `using` flushes the log's queue when Main returns, so these final lines reach the disk too.
            log.Info("Exited.");
        }
    }

    /// <summary>Used by the Avalonia previewer and by <see cref="Main"/>.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Last-resort logging for failures off the UI thread. Hotkeys and the tray icon are OS resources owned by this process,
    /// so Windows releases them when the process dies; the log is the part that would otherwise be lost.
    /// </summary>
    private static void InstallCrashHandlers(AppLog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Error($"Unhandled exception (terminating: {e.IsTerminating}).", e.ExceptionObject as Exception);

            // The process is about to die: write the queued lines now, synchronously, or the one that explains the crash is lost.
            if (e.IsTerminating)
            {
                log.Flush(TimeSpan.FromSeconds(2));
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }
}
