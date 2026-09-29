using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App.Views;

/// <summary>The main window: app bar plus the current page.</summary>
public partial class MainWindow : Window
{
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();
        _normalSize = WindowSizing.Default;
    }

    /// <summary>The size to remember (the last un-maximized size) and whether the window is maximized.</summary>
    public (Size NormalSize, bool Maximized) Placement => (_normalSize, WindowState == WindowState.Maximized);

    /// <summary>Opens at the remembered size, clamped to the primary screen's working area; always centred (position isn't remembered).</summary>
    public void RestoreSize(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var screen = Screens.Primary;
        var area = screen is null
            ? WindowSizing.Default
            : new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        var minimum = WindowSizing.MinimumFor(area);
        MinWidth = minimum.Width;
        MinHeight = minimum.Height;

        var remembered = state.WindowWidth is { } w && state.WindowHeight is { } h ? new Size(w, h) : (Size?)null;
        _normalSize = WindowSizing.Fit(remembered, area);
        Width = _normalSize.Width;
        Height = _normalSize.Height;
        if (state.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Native folder picker; returns the local path, or null if cancelled or not a local folder.</summary>
    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the screenshot folder",
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Track only the normal size, so closing while maximized still restores a sensible window next time.
        if (change.Property == ClientSizeProperty && WindowState == WindowState.Normal && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            _normalSize = ClientSize;
        }
    }
}
