using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Naming;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.Core.Settings;

/// <summary>What happens when a sub shot's number is already taken, if the user isn't asked.</summary>
public enum SubCollisionPolicy
{
    Ask,
    Append,
    Insert,
}

/// <summary>Screen corner for toast notifications.</summary>
public enum ToastCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>Colour theme.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>User-configurable settings; every property's initial value is the documented default (README Settings table, linked from spec §10).</summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    public static IReadOnlyDictionary<HotkeyAction, KeyChord> DefaultHotkeys { get; } = new Dictionary<HotkeyAction, KeyChord>
    {
        [HotkeyAction.MainShot] = KeyChord.Key("Backquote"),
        [HotkeyAction.SubShot] = KeyChord.Key("Minus"),
        [HotkeyAction.ToggleTimestamp] = KeyChord.Key("KeyT"),
        [HotkeyAction.Caption] = KeyChord.Key("KeyC"),
        [HotkeyAction.PauseResume] = KeyChord.CtrlShift("Space"),
        [HotkeyAction.Undo] = KeyChord.CtrlShift("KeyZ"),
        [HotkeyAction.EndSession] = KeyChord.CtrlShift("KeyQ"),
    };

    public IReadOnlyDictionary<HotkeyAction, KeyChord> Hotkeys { get; init; } = DefaultHotkeys;

    public bool AlwaysTimestamp { get; init; }

    public TimestampZone TimestampZone { get; init; } = TimestampZone.Local;

    /// <summary>
    /// Whether the timestamp and caption keys annotate the shot just taken (default: capture first, label after) or arm the next one.
    /// One setting for both keys so they never behave differently.
    /// </summary>
    public AnnotationTarget AnnotationTarget { get; init; } = AnnotationTarget.LastShot;

    /// <summary>Number the first shot of every group M-1 (so the solo→member rename never happens).</summary>
    public bool AlwaysNumberFirstShotAsMember { get; init; }

    /// <summary>Default capture target; the Start screen pre-fills it and saves the last choice back here.</summary>
    public CaptureTarget CaptureTarget { get; init; } = CaptureTarget.MonitorUnderCursor;

    public bool CopyToClipboard { get; init; }

    public SubCollisionPolicy SubCollisionPolicy { get; init; } = SubCollisionPolicy.Ask;

    public bool OpenFolderOnSessionEnd { get; init; } = true;

    public bool ExitOnSessionEnd { get; init; }

    public bool SoundsEnabled { get; init; } = true;

    /// <summary>0–100.</summary>
    public int SoundVolume { get; init; } = 70;

    public IReadOnlyList<FeedbackKind> MutedSounds { get; init; } = [];

    public bool ToastsEnabled { get; init; } = true;

    public ToastCorner ToastCorner { get; init; } = ToastCorner.BottomRight;

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// Repairs values loaded from disk: fills hotkeys added in newer versions, clamps ranges.
    /// Untrusted input (a hand-edited or corrupted file) must never produce an unusable configuration.
    /// </summary>
    public AppSettings Sanitized()
    {
        var hotkeys = new Dictionary<HotkeyAction, KeyChord>();
        foreach (var (action, chord) in DefaultHotkeys)
        {
            hotkeys[action] = Hotkeys is not null
                              && Hotkeys.TryGetValue(action, out var loaded)
                              && loaded is not null
                              && !string.IsNullOrWhiteSpace(loaded.Code)
                ? loaded
                : chord;
        }

        return this with
        {
            Hotkeys = hotkeys,
            SoundVolume = Math.Clamp(SoundVolume, 0, 100),
            MutedSounds = MutedSounds ?? [],
            TimestampZone = Enum.IsDefined(TimestampZone) ? TimestampZone : TimestampZone.Local,
            AnnotationTarget = Enum.IsDefined(AnnotationTarget) ? AnnotationTarget : AnnotationTarget.LastShot,
            CaptureTarget = Enum.IsDefined(CaptureTarget) ? CaptureTarget : CaptureTarget.MonitorUnderCursor,
            SubCollisionPolicy = Enum.IsDefined(SubCollisionPolicy) ? SubCollisionPolicy : SubCollisionPolicy.Ask,
            ToastCorner = Enum.IsDefined(ToastCorner) ? ToastCorner : ToastCorner.BottomRight,
            Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        };
    }
}

/// <summary>Remembered app state that "Reset to defaults" must not wipe.</summary>
public sealed record AppState
{
    public const int MaxRecentFolders = 10;

    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    /// <summary>Last main-window size in device-independent pixels (the normal, un-maximized size); null = default.</summary>
    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }

    /// <summary>Moves <paramref name="folder"/> to the front of the recent list (case-insensitive de-duplication).</summary>
    public AppState WithRecentFolder(string folder)
    {
        var list = new List<string> { folder };
        list.AddRange(RecentFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
        return this with { RecentFolders = list.Take(MaxRecentFolders).ToList() };
    }

    public AppState WithoutRecentFolder(string folder) => this with
    {
        RecentFolders = RecentFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToList(),
    };

    public AppState Sanitized() => this with
    {
        RecentFolders = (RecentFolders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentFolders)
            .ToList(),
        WindowWidth = PlausibleLength(WindowWidth),
        WindowHeight = PlausibleLength(WindowHeight),
    };

    // A hand-edited or corrupted value must never produce a zero-sized or absurd window.
    private static double? PlausibleLength(double? value) => value is >= 200 and <= 20000 ? value : null;
}
