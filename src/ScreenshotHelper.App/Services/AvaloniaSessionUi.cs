using Avalonia.Controls;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.App.Views;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Platform.Windows;

namespace ScreenshotHelper.App.Services;

/// <summary>
/// The real session UI: hides the main window during a session, shows the caption box / collision prompt on top (then gives focus back
/// to the app the user was capturing), shows toasts, and presents the summary or exits when the session ends.
/// </summary>
public sealed class AvaloniaSessionUi : ISessionUi, IDisposable
{
    private readonly AppServices _services;
    private readonly MainWindow _mainWindow;
    private readonly Action _shutdown;
    private readonly ToastWindow _toast = new();
    private readonly List<Window> _openPrompts = [];
    private SessionCoordinator? _coordinator;
    private MainWindowViewModel? _viewModel;
    private TrayController? _tray;

    public AvaloniaSessionUi(AppServices services, MainWindow mainWindow, Action shutdown)
    {
        _services = services;
        _mainWindow = mainWindow;
        _shutdown = shutdown;
    }

    public void Attach(SessionCoordinator coordinator) => _coordinator = coordinator;

    public void AttachViewModel(MainWindowViewModel viewModel) => _viewModel = viewModel;

    public void AttachTray(TrayController tray) => _tray = tray;

    public async Task<string?> AskCaptionAsync(CaptionRequest request)
    {
        var previous = WindowsDesktop.GetForeground();
        var window = new CaptionWindow { DataContext = new CaptionViewModel(request) };
        var result = await ShowOnTopAsync(window, () => window.Result).ConfigureAwait(true);
        WindowsDesktop.RestoreForeground(previous);
        return result;
    }

    public async Task<SubCollisionAnswer> AskCollisionAsync(SubCollision collision)
    {
        var previous = WindowsDesktop.GetForeground();
        var window = new CollisionPromptWindow { DataContext = new CollisionPromptViewModel(collision) };
        var result = await ShowOnTopAsync(window, () => window.Answer).ConfigureAwait(true);
        WindowsDesktop.RestoreForeground(previous);
        return result;
    }

    public void ShowToast(FeedbackEvent feedback) => _toast.Show(feedback, _services.Settings.ToastCorner);

    public void SessionStarted() => _mainWindow.Hide();

    public void SessionEnded(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _toast.ClearPaused();
        var settings = _services.Settings;
        // Nothing saved → nothing to show; don't pop Explorer open for an empty session.
        if (settings.OpenFolderOnSessionEnd && summary.Files.Count > 0)
        {
            _services.Platform.Revealer.Reveal(summary.Folder, summary.Files);
        }

        if (settings.ExitOnSessionEnd)
        {
            _shutdown();
            return;
        }

        _viewModel?.ShowSummary(summary);
        WindowReveal.ShowWhenDrawn(_mainWindow);
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
    }

    public void StateChanged()
    {
        if (_coordinator is not null)
        {
            _tray?.Update();
        }
    }

    public void CloseOpenPrompts()
    {
        // Closing without a choice is the safe answer: the caption box reports "cancelled", the collision prompt "discard".
        foreach (var prompt in _openPrompts.ToList())
        {
            prompt.Close();
        }
    }

    public void Dispose() => _toast.Close();

    /// <summary>Shows a dialog topmost and focused (allowed: our process just received the hotkey input) and waits for it to close.</summary>
    private async Task<T> ShowOnTopAsync<T>(Window window, Func<T> result)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _openPrompts.Add(window);
        window.Closed += (_, _) =>
        {
            _openPrompts.Remove(window);
            closed.TrySetResult();
        };
        window.Topmost = true;

        // Focus is taken at once (keys typed straight after the hotkey must land in the box), while the window is still cloaked.
        WindowReveal.ShowWhenDrawn(window);
        window.Activate();
        WindowsDesktop.BringToFront(window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        await closed.Task.ConfigureAwait(true);
        return result();
    }
}
