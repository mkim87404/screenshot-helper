using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Renumber;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App;

/// <summary>Per-user folders: settings roam with the profile; logs and renumber journals stay on this machine.</summary>
public sealed record AppPaths(string Settings, string Logs, string Journal)
{
    /// <summary>Environment variable that redirects all app data to one folder (development and test runs).</summary>
    public const string HomeOverrideVariable = "SCREENSHOTHELPER_HOME";

    public static AppPaths ForCurrentUser()
    {
        if (Environment.GetEnvironmentVariable(HomeOverrideVariable) is { Length: > 0 } home && Path.IsPathFullyQualified(home))
        {
            return new AppPaths(home, Path.Combine(home, "logs"), Path.Combine(home, "journal"));
        }

        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotHelper");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotHelper");
        return new AppPaths(roaming, Path.Combine(local, "logs"), Path.Combine(local, "journal"));
    }
}

/// <summary>Platform services the app depends on, so tests can supply stand-ins for OS-level pieces (hotkeys, screen).</summary>
public sealed record PlatformServices(
    IHotkeyService Hotkeys,
    IKeyboardLayout Keyboard,
    IScreenCapture Capture,
    IRecycleBin RecycleBin,
    IFileRevealer Revealer,
    ISoundPlayer Sound,
    IClipboardImage Clipboard);

/// <summary>
/// Composition root and shared mutable app state (current settings/state). Settings changes are saved immediately and raise
/// <see cref="SettingsChanged"/>; everything else reads <see cref="Settings"/> on demand, so it's always current.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private AppState _state;
    private SoundBank _sounds;

    public AppServices(AppPaths paths, AppLog log, PlatformServices platform)
    {
        Paths = paths;
        Log = log;
        Platform = platform;
        _store = new SettingsStore(paths.Settings, log);
        _settings = _store.LoadSettings();
        _state = _store.LoadState();
        _sounds = SoundBank.Create(_settings.SoundVolume);
        Renumber = new RenumberExecutor(paths.Journal, log);
    }

    public event Action<AppSettings>? SettingsChanged;

    public AppPaths Paths { get; }

    public AppLog Log { get; }

    public PlatformServices Platform { get; }

    public RenumberExecutor Renumber { get; }

    public AppSettings Settings => Volatile.Read(ref _settings);

    public AppState State => Volatile.Read(ref _state);

    public SoundBank Sounds => Volatile.Read(ref _sounds);

    /// <summary>A chord as the user sees it on their keyboard layout (e.g. "Ctrl+Shift+Space", "` (backtick)").</summary>
    public string Describe(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        return chord.Describe(Platform.Keyboard.KeyLabel);
    }

    /// <summary>The key currently bound to <paramref name="action"/>, as the user sees it.</summary>
    public string Describe(HotkeyAction action) => Describe(Settings.Hotkeys[action]);

    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sanitized = settings.Sanitized();
        if (sanitized.SoundVolume != Settings.SoundVolume)
        {
            Volatile.Write(ref _sounds, SoundBank.Create(sanitized.SoundVolume));
        }

        Volatile.Write(ref _settings, sanitized);
        TrySave(() => _store.Save(sanitized));
        SettingsChanged?.Invoke(sanitized);
    }

    public void UpdateState(Func<AppState, AppState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var updated = change(State);
        Volatile.Write(ref _state, updated);
        TrySave(() => _store.Save(updated));
    }

    public void Dispose()
    {
        Platform.Hotkeys.Dispose();
        (Platform.Capture as IDisposable)?.Dispose();
        (Platform.Sound as IDisposable)?.Dispose();
        (Platform.Clipboard as IDisposable)?.Dispose();
    }

    private void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings still apply for this run; only persistence failed.
            Log.Error("Couldn't save settings.", ex);
        }
    }
}
