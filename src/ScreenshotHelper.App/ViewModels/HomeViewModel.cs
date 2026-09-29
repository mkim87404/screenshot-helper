using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Renumber;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.App.ViewModels;

/// <summary>A recent-folder entry; missing folders stay listed (marked) until removed.</summary>
public sealed record RecentFolderItem(string Path, bool Exists)
{
    public string Display => Exists ? Path : $"{Path} (not found)";
}

/// <summary>One key-cap chip in the Start screen's key guide, e.g. [Ctrl+Shift+Space] Pause.</summary>
public sealed record KeyHint(string Keys, string Action);

/// <summary>A capture target with its display text, for combo boxes.</summary>
public sealed record CaptureTargetOption(CaptureTarget Value, string Label)
{
    public static IReadOnlyList<CaptureTargetOption> All { get; } =
    [
        new(CaptureTarget.MonitorUnderCursor, "Monitor under the mouse"),
        new(CaptureTarget.PrimaryMonitor, "Primary monitor"),
        new(CaptureTarget.AllMonitors, "All monitors"),
        new(CaptureTarget.ActiveWindow, "Active window"),
    ];

    public static CaptureTargetOption For(CaptureTarget target) => All.First(o => o.Value == target);
}

/// <summary>The Start Session screen (spec §2): folder, start mode, numbers with live hints, capture target.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SessionCoordinator _coordinator;
    private readonly Func<Task<string?>> _pickFolder;
    private FolderIndex? _index;
    private int? _suggestedMain;
    private int? _suggestedSub;

    public HomeViewModel(AppServices services, SessionCoordinator coordinator, Func<Task<string?>> pickFolder)
    {
        _services = services;
        _coordinator = coordinator;
        _pickFolder = pickFolder;
        HighestText = "Choose a folder to start.";
        SelectedCaptureTarget = CaptureTargetOption.For(services.Settings.CaptureTarget);
        RefreshRecent();
        RefreshRecovery();
        Folder = services.State.RecentFolders.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Raised when the user asks to renumber the current folder.</summary>
    public event Action<string>? RenumberRequested;

    public ObservableCollection<RecentFolderItem> RecentFolders { get; } = [];

    public ObservableCollection<RenameJournal> IncompleteRenumbers { get; } = [];

    public IReadOnlyList<CaptureTargetOption> CaptureTargets => CaptureTargetOption.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(RevealFolderCommand), nameof(RenumberCommand))]
    public partial string? Folder { get; set; }

    [ObservableProperty]
    public partial bool IsContinueMode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial decimal? MainNumber { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial decimal? SubNumber { get; set; }

    [ObservableProperty]
    public partial string HighestText { get; set; }

    [ObservableProperty]
    public partial string? CheckMessage { get; set; }

    [ObservableProperty]
    public partial bool CheckIsWarning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool CheckIsBlocking { get; set; }

    [ObservableProperty]
    public partial string? SuggestionText { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial CaptureTargetOption SelectedCaptureTarget { get; set; }

    public bool HasRecovery => IncompleteRenumbers.Count > 0;

    /// <summary>The session keys as separate chips (key cap + action) so each command's boundaries are obvious.</summary>
    public IReadOnlyList<KeyHint> KeyHints
    {
        get
        {
            var annotateLast = _services.Settings.AnnotationTarget == AnnotationTarget.LastShot;
            KeyHint Hint(HotkeyAction action, string label) => new(_services.Describe(action), label);
            return
            [
                Hint(HotkeyAction.MainShot, "Main shot"),
                Hint(HotkeyAction.SubShot, "Sub shot"),
                Hint(HotkeyAction.ToggleTimestamp, annotateLast ? "Timestamp last shot" : "Timestamp next shot"),
                Hint(HotkeyAction.Caption, annotateLast ? "Caption last shot" : "Caption next shot"),
                Hint(HotkeyAction.PauseResume, "Pause"),
                Hint(HotkeyAction.Undo, "Undo"),
                Hint(HotkeyAction.EndSession, "End session"),
            ];
        }
    }

    /// <summary>Re-reads the folder (e.g. when the window is shown again after a session or a renumber).</summary>
    public void Refresh()
    {
        RefreshRecent();
        RefreshRecovery();
        OnPropertyChanged(nameof(KeyHints));
        ScanFolder(resetNumbers: true);
    }

    partial void OnFolderChanged(string? value) => ScanFolder(resetNumbers: true);

    partial void OnIsContinueModeChanged(bool value)
    {
        ApplyDefaults();
        Check();
    }

    partial void OnMainNumberChanged(decimal? value) => Check();

    partial void OnSubNumberChanged(decimal? value) => Check();

    partial void OnSelectedCaptureTargetChanged(CaptureTargetOption value)
    {
        if (value is not null && value.Value != _services.Settings.CaptureTarget)
        {
            _services.UpdateSettings(_services.Settings with { CaptureTarget = value.Value });
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await _pickFolder().ConfigureAwait(true);
        if (picked is not null)
        {
            Folder = picked;
        }
    }

    [RelayCommand]
    private void OpenRecent(RecentFolderItem? item)
    {
        if (item is { Exists: true })
        {
            Folder = item.Path;
        }
    }

    [RelayCommand]
    private void RemoveRecent(RecentFolderItem? item)
    {
        if (item is null)
        {
            return;
        }

        _services.UpdateState(s => s.WithoutRecentFolder(item.Path));
        RefreshRecent();
    }

    [RelayCommand]
    private void UseSuggestion()
    {
        if (_suggestedMain is { } main)
        {
            MainNumber = main;
        }

        if (_suggestedSub is { } sub)
        {
            SubNumber = sub;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        ErrorMessage = null;
        if (Folder is null || !Directory.Exists(Folder))
        {
            ErrorMessage = "The folder no longer exists.";
            return;
        }

        var settings = _services.Settings;
        var options = new SessionOptions(
            Folder,
            IsContinueMode ? StartMode.ContinueGroup : StartMode.NewGroup,
            (int)(MainNumber ?? 1),
            (int)(SubNumber ?? 1),
            SelectedCaptureTarget.Value,
            settings.AlwaysTimestamp,
            settings.TimestampZone,
            settings.AlwaysNumberFirstShotAsMember,
            settings.CopyToClipboard,
            settings.SubCollisionPolicy,
            settings.AnnotationTarget);

        var failures = _coordinator.Start(options);
        if (failures.Count > 0)
        {
            var keys = settings.Hotkeys;
            ErrorMessage = "Couldn't start — these keys are unavailable: " + string.Join(", ", failures.Select(f =>
                $"{_services.Describe(keys[f.Key])} ({(f.Value == HotkeyFailure.InUse ? "used by another app" : "not supported")})")) +
                ". Change them in Settings.";
            return;
        }

        _services.UpdateState(s => s.WithRecentFolder(Folder));
        RefreshRecent();
    }

    private bool CanStart() => Folder is not null && MainNumber is not null && (!IsContinueMode || SubNumber is not null) && !CheckIsBlocking;

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void RevealFolder()
    {
        if (Folder is not null)
        {
            _services.Platform.Revealer.Reveal(Folder, _index?.HighestShot is { } last ? [Path.Combine(Folder, last.FileName)] : []);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void Renumber()
    {
        if (Folder is not null)
        {
            RenumberRequested?.Invoke(Folder);
        }
    }

    private bool HasFolder() => Folder is not null;

    [RelayCommand]
    private void ResumeRenumber(RenameJournal? journal) => Recover(journal, j => _services.Renumber.Resume(j), "resumed");

    [RelayCommand]
    private void RollBackRenumber(RenameJournal? journal) => Recover(journal, j => _services.Renumber.RollBack(j), "rolled back");

    private void Recover(RenameJournal? journal, Func<RenameJournal, ExecutionResult> action, string verb)
    {
        if (journal is null)
        {
            return;
        }

        var result = action(journal);
        ErrorMessage = result.Succeeded ? null : $"The interrupted renumber couldn't be {verb}: {result.Error}";
        RefreshRecovery();
        ScanFolder(resetNumbers: true);
    }

    private void ScanFolder(bool resetNumbers)
    {
        _index = null;
        CheckMessage = null;
        if (string.IsNullOrWhiteSpace(Folder))
        {
            HighestText = "Choose a folder to start.";
            return;
        }

        try
        {
            _index = FolderIndex.Scan(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            HighestText = $"Can't read this folder: {ex.Message}";
            return;
        }

        HighestText = _index.HighestShot is { } highest
            ? $"Highest in folder: {highest.FileName}"
            : "No numbered screenshots in this folder yet.";
        if (resetNumbers)
        {
            ApplyDefaults();
        }

        Check();
    }

    private void ApplyDefaults()
    {
        if (_index is null)
        {
            return;
        }

        var defaults = StartAdvisor.Defaults(_index);
        MainNumber = IsContinueMode ? defaults.ContinueMain : defaults.NewGroupMain;
        SubNumber = IsContinueMode ? defaults.ContinueSub : 1;
    }

    private void Check()
    {
        if (_index is null || MainNumber is null)
        {
            CheckMessage = null;
            CheckIsBlocking = MainNumber is null;
            return;
        }

        var check = StartAdvisor.Check(_index, IsContinueMode ? StartMode.ContinueGroup : StartMode.NewGroup, (int)MainNumber.Value, (int)(SubNumber ?? 1));
        CheckMessage = check.Message;
        CheckIsWarning = check.IsWarning || check.IsBlocking;
        CheckIsBlocking = check.IsBlocking;
        _suggestedMain = check.SuggestedMain;
        _suggestedSub = check.SuggestedSub;
        SuggestionText = check.SuggestedMain is { } m ? $"Use {m}" : check.SuggestedSub is { } s ? $"Use {MainNumber}-{s}" : null;
    }

    private void RefreshRecent()
    {
        RecentFolders.Clear();
        foreach (var path in _services.State.RecentFolders)
        {
            RecentFolders.Add(new RecentFolderItem(path, Directory.Exists(path)));
        }
    }

    private void RefreshRecovery()
    {
        IncompleteRenumbers.Clear();
        foreach (var journal in _services.Renumber.FindIncomplete())
        {
            IncompleteRenumbers.Add(journal);
        }

        OnPropertyChanged(nameof(HasRecovery));
    }
}
