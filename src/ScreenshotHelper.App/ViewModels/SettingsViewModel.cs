using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Naming;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App.ViewModels;

/// <summary>A labelled enum value for combo lists (displayed via <see cref="ToString"/>, so XAML needs no generic templates).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One rebindable hotkey row.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public HotkeyRowViewModel(SettingsViewModel owner, HotkeyAction action, string label, string description)
    {
        _owner = owner;
        Action = action;
        Label = label;
        Description = description;
        ChordText = string.Empty;
    }

    public HotkeyAction Action { get; }

    public string Label { get; }

    public string Description { get; }

    [ObservableProperty]
    public partial string ChordText { get; set; }

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial bool ProblemIsError { get; set; }

    public string ButtonText => IsCapturing ? "Press a key… (Esc cancels)" : ChordText;

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(ButtonText));

    partial void OnChordTextChanged(string value) => OnPropertyChanged(nameof(ButtonText));

    [RelayCommand]
    private void Capture() => _owner.BeginCapture(this);
}

/// <summary>Settings screen (spec §10). Every change is validated, saved immediately and applied to the next session.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Hotkeys =
        [
            new(this, HotkeyAction.MainShot, "Main shot", "Starts a new group."),
            new(this, HotkeyAction.SubShot, "Sub shot", "Adds to the current group."),
            new(this, HotkeyAction.ToggleTimestamp, "Timestamp", "Adds or removes a timestamp (see File names)."),
            new(this, HotkeyAction.Caption, "Caption", "Opens the caption box (see File names)."),
            new(this, HotkeyAction.PauseResume, "Pause / resume", "Stays active while paused, so it needs Ctrl, Alt or Win."),
            new(this, HotkeyAction.Undo, "Undo last shot", "Moves the last shot to the Recycle Bin."),
            new(this, HotkeyAction.EndSession, "End session", "Opens the folder with this session's shots."),
        ];
        SoundToggles = [.. Enum.GetValues<FeedbackKind>().Select(k => new SoundToggleViewModel(this, k, SoundLabel(k)))];
        Load();
    }

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; }

    public ObservableCollection<SoundToggleViewModel> SoundToggles { get; }

    public IReadOnlyList<CaptureTargetOption> CaptureTargets => CaptureTargetOption.All;

    public IReadOnlyList<Choice<TimestampZone>> TimestampZones { get; } =
        [new(TimestampZone.Local, "Local time (with UTC offset)"), new(TimestampZone.Utc, "UTC")];

    public IReadOnlyList<Choice<AnnotationTarget>> AnnotationTargets { get; } =
    [
        new(AnnotationTarget.LastShot, "The screenshot just taken (capture first, label after)"),
        new(AnnotationTarget.NextShot, "The next screenshot (set up before capturing)"),
    ];

    public IReadOnlyList<Choice<SubCollisionPolicy>> CollisionPolicies { get; } =
        [new(SubCollisionPolicy.Ask, "Ask each time"), new(SubCollisionPolicy.Append, "Append to the end of the group"), new(SubCollisionPolicy.Insert, "Insert and shift later shots")];

    public IReadOnlyList<Choice<ToastCorner>> ToastCorners { get; } =
        [new(ToastCorner.BottomRight, "Bottom right"), new(ToastCorner.BottomLeft, "Bottom left"), new(ToastCorner.TopRight, "Top right"), new(ToastCorner.TopLeft, "Top left")];

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } =
        [new(AppTheme.System, "Match Windows"), new(AppTheme.Light, "Light"), new(AppTheme.Dark, "Dark")];

    /// <summary>The row waiting for a key press, if any (the view routes KeyDown to <see cref="TryCapture"/>).</summary>
    public HotkeyRowViewModel? CapturingRow { get; private set; }

    [ObservableProperty] public partial bool AlwaysTimestamp { get; set; }
    [ObservableProperty] public partial Choice<TimestampZone>? SelectedTimestampZone { get; set; }
    [ObservableProperty] public partial Choice<AnnotationTarget>? SelectedAnnotationTarget { get; set; }
    [ObservableProperty] public partial bool AlwaysNumberFirstShotAsMember { get; set; }
    [ObservableProperty] public partial CaptureTargetOption? SelectedCaptureTarget { get; set; }
    [ObservableProperty] public partial bool CopyToClipboard { get; set; }
    [ObservableProperty] public partial Choice<SubCollisionPolicy>? SelectedCollisionPolicy { get; set; }
    [ObservableProperty] public partial bool OpenFolderOnSessionEnd { get; set; }
    [ObservableProperty] public partial bool ExitOnSessionEnd { get; set; }
    [ObservableProperty] public partial bool SoundsEnabled { get; set; }
    [ObservableProperty] public partial double SoundVolume { get; set; }
    [ObservableProperty] public partial bool ToastsEnabled { get; set; }
    [ObservableProperty] public partial Choice<ToastCorner>? SelectedToastCorner { get; set; }
    [ObservableProperty] public partial Choice<AppTheme>? SelectedTheme { get; set; }

    public bool IsCapturing => CapturingRow is not null;

    public void BeginCapture(HotkeyRowViewModel row)
    {
        CancelCapture();
        CapturingRow = row;
        row.IsCapturing = true;
        OnPropertyChanged(nameof(IsCapturing));
    }

    public void CancelCapture()
    {
        if (CapturingRow is not null)
        {
            CapturingRow.IsCapturing = false;
            CapturingRow = null;
            OnPropertyChanged(nameof(IsCapturing));
        }
    }

    /// <summary>
    /// Applies a key press to the capturing row. Returns false if it was ignored (a lone modifier). Bare Esc cancels capturing.
    /// </summary>
    public bool TryCapture(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var row = CapturingRow;
        if (row is null)
        {
            return false;
        }

        if (chord.Code == "Escape" && chord.Modifiers == KeyModifiers.None)
        {
            CancelCapture();
            return true;
        }

        if (!_services.Platform.Keyboard.IsSupported(chord.Code))
        {
            row.Problem = "That key can't be used as a hotkey.";
            row.ProblemIsError = true;
            return true;
        }

        if (row.Action == HotkeyAction.PauseResume && (chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) == 0)
        {
            row.Problem = "Pause stays active while you type, so it needs Ctrl, Alt or Win.";
            row.ProblemIsError = true;
            return true;
        }

        CancelCapture();
        var hotkeys = new Dictionary<HotkeyAction, KeyChord>(_services.Settings.Hotkeys) { [row.Action] = chord };
        var duplicate = HotkeyPlan.Duplicates(hotkeys).FirstOrDefault(d => d.Contains(row.Action));
        if (duplicate is not null)
        {
            var other = Hotkeys.First(h => h.Action == duplicate.First(a => a != row.Action));
            row.Problem = $"Already used by “{other.Label}”.";
            row.ProblemIsError = true;
            return true;
        }

        Save(_services.Settings with { Hotkeys = hotkeys });
        LoadHotkeys();
        return true;
    }

    internal void SetMuted(FeedbackKind kind, bool muted)
    {
        if (_loading)
        {
            return;
        }

        var set = new HashSet<FeedbackKind>(_services.Settings.MutedSounds);
        if (muted)
        {
            set.Add(kind);
        }
        else
        {
            set.Remove(kind);
        }

        Save(_services.Settings with { MutedSounds = [.. set.Order()] });
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        CancelCapture();
        Save(_services.Settings with { Hotkeys = AppSettings.DefaultHotkeys });
        LoadHotkeys();
    }

    [RelayCommand]
    private void ResetAll()
    {
        CancelCapture();
        Save(AppSettings.Default);
        Load();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_services.Log.Directory);
        _services.Platform.Revealer.Reveal(_services.Log.Directory, []);
    }

    [RelayCommand]
    private void PreviewSound(FeedbackKind kind) => _services.Platform.Sound.Play(_services.Sounds[kind]);

    partial void OnAlwaysTimestampChanged(bool value) => Save(s => s with { AlwaysTimestamp = value });
    partial void OnSelectedTimestampZoneChanged(Choice<TimestampZone>? value) => Save(s => value is null ? s : s with { TimestampZone = value.Value });
    partial void OnSelectedAnnotationTargetChanged(Choice<AnnotationTarget>? value) => Save(s => value is null ? s : s with { AnnotationTarget = value.Value });
    partial void OnAlwaysNumberFirstShotAsMemberChanged(bool value) => Save(s => s with { AlwaysNumberFirstShotAsMember = value });
    partial void OnSelectedCaptureTargetChanged(CaptureTargetOption? value) => Save(s => value is null ? s : s with { CaptureTarget = value.Value });
    partial void OnCopyToClipboardChanged(bool value) => Save(s => s with { CopyToClipboard = value });
    partial void OnSelectedCollisionPolicyChanged(Choice<SubCollisionPolicy>? value) => Save(s => value is null ? s : s with { SubCollisionPolicy = value.Value });
    partial void OnOpenFolderOnSessionEndChanged(bool value) => Save(s => s with { OpenFolderOnSessionEnd = value });
    partial void OnExitOnSessionEndChanged(bool value) => Save(s => s with { ExitOnSessionEnd = value });
    partial void OnSoundsEnabledChanged(bool value) => Save(s => s with { SoundsEnabled = value });
    partial void OnSoundVolumeChanged(double value) => Save(s => s with { SoundVolume = (int)Math.Round(value) });
    partial void OnToastsEnabledChanged(bool value) => Save(s => s with { ToastsEnabled = value });
    partial void OnSelectedToastCornerChanged(Choice<ToastCorner>? value) => Save(s => value is null ? s : s with { ToastCorner = value.Value });
    partial void OnSelectedThemeChanged(Choice<AppTheme>? value) => Save(s => value is null ? s : s with { Theme = value.Value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
        {
            Save(change(_services.Settings));
        }
    }

    private void Save(AppSettings settings) => _services.UpdateSettings(settings);

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _services.Settings;
            AlwaysTimestamp = s.AlwaysTimestamp;
            SelectedTimestampZone = TimestampZones.First(z => z.Value == s.TimestampZone);
            SelectedAnnotationTarget = AnnotationTargets.First(a => a.Value == s.AnnotationTarget);
            AlwaysNumberFirstShotAsMember = s.AlwaysNumberFirstShotAsMember;
            SelectedCaptureTarget = CaptureTargetOption.For(s.CaptureTarget);
            CopyToClipboard = s.CopyToClipboard;
            SelectedCollisionPolicy = CollisionPolicies.First(p => p.Value == s.SubCollisionPolicy);
            OpenFolderOnSessionEnd = s.OpenFolderOnSessionEnd;
            ExitOnSessionEnd = s.ExitOnSessionEnd;
            SoundsEnabled = s.SoundsEnabled;
            SoundVolume = s.SoundVolume;
            ToastsEnabled = s.ToastsEnabled;
            SelectedToastCorner = ToastCorners.First(c => c.Value == s.ToastCorner);
            SelectedTheme = Themes.First(t => t.Value == s.Theme);
            foreach (var toggle in SoundToggles)
            {
                toggle.IsOn = !s.MutedSounds.Contains(toggle.Kind);
            }

            LoadHotkeys();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadHotkeys()
    {
        var keyboard = _services.Platform.Keyboard;
        foreach (var row in Hotkeys)
        {
            var chord = _services.Settings.Hotkeys[row.Action];
            row.ChordText = _services.Describe(chord);
            var typesCharacter = (chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0 && keyboard.ProducesCharacter(chord);
            row.Problem = typesCharacter ? "On your keyboard layout this combination also types a character." : null;
            row.ProblemIsError = false;
        }
    }

    private static string SoundLabel(FeedbackKind kind) => kind switch
    {
        FeedbackKind.SessionStarted => "Session started",
        FeedbackKind.MainSaved => "Main shot saved",
        FeedbackKind.SubSaved => "Sub shot saved",
        FeedbackKind.TimestampArmed => "Timestamp on",
        FeedbackKind.TimestampDisarmed => "Timestamp off",
        FeedbackKind.CaptionArmed => "Caption set",
        FeedbackKind.CaptionCancelled => "Caption cancelled",
        FeedbackKind.Paused => "Paused",
        FeedbackKind.Resumed => "Resumed",
        FeedbackKind.Undo => "Undo",
        FeedbackKind.Warning => "Warning",
        FeedbackKind.Error => "Error",
        FeedbackKind.SessionEnded => "Session ended",
        _ => kind.ToString(),
    };
}

/// <summary>Per-event sound on/off with a preview button.</summary>
public sealed partial class SoundToggleViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public SoundToggleViewModel(SettingsViewModel owner, FeedbackKind kind, string label)
    {
        _owner = owner;
        Kind = kind;
        Label = label;
    }

    public FeedbackKind Kind { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value) => _owner.SetMuted(Kind, !value);
}
