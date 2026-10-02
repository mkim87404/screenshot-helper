using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Renumber;

namespace ScreenshotHelper.App.ViewModels;

/// <summary>Which Renumber operation is chosen.</summary>
public enum RenumberKind
{
    Shift,
    CloseGaps,
    MoveToGroup,
    NormaliseSolos,
}

/// <summary>A file in the Renumber list; group-first shots start a visual group.</summary>
public sealed partial class RenumberFileViewModel : ObservableObject
{
    public RenumberFileViewModel(IndexedShot shot, bool startsGroup)
    {
        Shot = shot;
        StartsGroup = startsGroup;
    }

    public IndexedShot Shot { get; }

    public string FileName => Shot.FileName;

    public bool StartsGroup { get; }

    public string GroupLabel => $"Group {Shot.Main}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Renumber tool (spec §9): select files, pick an operation, preview, apply (journaled), undo.</summary>
public sealed partial class RenumberViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly Func<Task<string?>> _pickFolder;
    private FolderIndex? _index;
    private RenumberPlan? _plan;
    private bool _opening;

    public RenumberViewModel(AppServices services, Func<Task<string?>> pickFolder)
    {
        _services = services;
        _pickFolder = pickFolder;
        Operation = Operations[0];
        MainDelta = 0;
        SubDelta = 0;
        StartMain = 1;
        CompactSubs = true;
        RenumberGroups = true;
        TargetMain = 1;
        StartSub = 1;
    }

    public ObservableCollection<RenumberFileViewModel> Files { get; } = [];

    public ObservableCollection<PlanRow> Preview { get; } = [];

    public IReadOnlyList<Choice<RenumberKind>> Operations { get; } =
    [
        new(RenumberKind.Shift, "Shift numbers"),
        new(RenumberKind.CloseGaps, "Close gaps"),
        new(RenumberKind.MoveToGroup, "Move to group"),
        new(RenumberKind.NormaliseSolos, "Normalise solos"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(UndoLastCommand))]
    public partial string? Folder { get; set; }

    [ObservableProperty] public partial Choice<RenumberKind> Operation { get; set; }
    [ObservableProperty] public partial decimal? MainDelta { get; set; }
    [ObservableProperty] public partial decimal? SubDelta { get; set; }
    [ObservableProperty] public partial bool RenumberGroups { get; set; }
    [ObservableProperty] public partial decimal? StartMain { get; set; }
    [ObservableProperty] public partial bool CompactSubs { get; set; }
    [ObservableProperty] public partial decimal? TargetMain { get; set; }
    [ObservableProperty] public partial decimal? StartSub { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool CanApplyPlan { get; set; }

    /// <summary>Thumbnail of the focused file; owned here and disposed when replaced, so decoded images don't pile up.</summary>
    [ObservableProperty] public partial Bitmap? PreviewImage { get; set; }

    public bool IsShift => Operation.Value == RenumberKind.Shift;
    public bool IsCloseGaps => Operation.Value == RenumberKind.CloseGaps;
    public bool IsMoveToGroup => Operation.Value == RenumberKind.MoveToGroup;
    public bool IsNormalise => Operation.Value == RenumberKind.NormaliseSolos;

    public string OperationHelp => Operation.Value switch
    {
        RenumberKind.Shift => "Adds to the main and/or sub number of every selected file (solo shots have no sub number).",
        RenumberKind.CloseGaps => "Renumbers the selected groups consecutively and/or their sub numbers from 1.",
        RenumberKind.MoveToGroup => "Moves the selected files, in order, into one group.",
        _ => "A lone member becomes a solo shot; a solo shot beside members becomes member 1.",
    };

    public string SelectionSummary => $"{Files.Count(f => f.IsSelected)} of {Files.Count} selected";

    /// <summary>
    /// Opens the tool on a folder, optionally pre-selecting files (e.g. the session just finished). Re-opening the folder already shown
    /// (switching back to the tab) keeps its list and selection, and rebuilds the list only if the folder's screenshots changed.
    /// </summary>
    public void Open(string folder, IReadOnlyCollection<string>? preselect = null)
    {
        if (preselect is null && _index is not null && string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            // One directory listing is cheap; rebuilding hundreds of rows on every tab switch is what made it lag.
            var current = TryScan(folder);
            if (current is null || !current.Shots.Select(s => s.FileName).SequenceEqual(_index.Shots.Select(s => s.FileName), StringComparer.Ordinal))
            {
                Load(Files.Where(f => f.IsSelected).Select(f => f.FileName).ToList(), current);
            }

            return;
        }

        // Setting Folder would load the list on its own; load once, with the pre-selection.
        _opening = true;
        try
        {
            Folder = folder;
        }
        finally
        {
            _opening = false;
        }

        Load(preselect);
    }

    partial void OnFolderChanged(string? value)
    {
        if (!_opening)
        {
            Load(null);
        }
    }

    partial void OnOperationChanged(Choice<RenumberKind> value)
    {
        OnPropertyChanged(nameof(IsShift));
        OnPropertyChanged(nameof(IsCloseGaps));
        OnPropertyChanged(nameof(IsMoveToGroup));
        OnPropertyChanged(nameof(IsNormalise));
        OnPropertyChanged(nameof(OperationHelp));
        UpdatePreview();
    }

    partial void OnMainDeltaChanged(decimal? value) => UpdatePreview();
    partial void OnSubDeltaChanged(decimal? value) => UpdatePreview();
    partial void OnRenumberGroupsChanged(bool value) => UpdatePreview();
    partial void OnStartMainChanged(decimal? value) => UpdatePreview();
    partial void OnCompactSubsChanged(bool value) => UpdatePreview();
    partial void OnTargetMainChanged(decimal? value) => UpdatePreview();
    partial void OnStartSubChanged(decimal? value) => UpdatePreview();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await _pickFolder().ConfigureAwait(true);
        if (picked is not null)
        {
            Folder = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void Refresh() => Load(Files.Where(f => f.IsSelected).Select(f => f.FileName).ToList());

    [RelayCommand]
    private void SelectAll(bool select)
    {
        foreach (var file in Files)
        {
            file.IsSelected = select;
        }
    }

    [RelayCommand]
    private void SelectGroup(RenumberFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        var target = !Files.Where(f => f.Shot.Main == file.Shot.Main).All(f => f.IsSelected);
        foreach (var member in Files.Where(f => f.Shot.Main == file.Shot.Main))
        {
            member.IsSelected = target;
        }
    }

    [RelayCommand]
    private void ShowPreview(RenumberFileViewModel? file) =>
        SetPreview(file is null || Folder is null ? null : LoadThumbnail(Path.Combine(Folder, file.FileName)));

    public void Dispose() => SetPreview(null);

    private void SetPreview(Bitmap? image)
    {
        var old = PreviewImage;
        PreviewImage = image;
        old?.Dispose();
    }

    private Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 480);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _services.Log.Warn($"Couldn't load a preview of {path}.", ex);
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyPlan))]
    private void Apply()
    {
        if (_plan is null || !_plan.IsValid)
        {
            return;
        }

        var result = _services.Renumber.Execute(_plan);
        var renamed = _plan.Steps.Count(s => !s.To.StartsWith(RenumberPlanner.TempPrefix, StringComparison.Ordinal));
        SetMessage(result.Succeeded ? $"Renamed {renamed} file(s). Use “Undo last renumber” to reverse." : result.Error, !result.Succeeded);
        var selectedTargets = _plan.Rows.Select(r => r.Target).ToList();
        Load(selectedTargets);
        UndoLastCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoLast()
    {
        var result = _services.Renumber.UndoLast();
        SetMessage(result.Succeeded ? "The last renumber was reversed." : result.Error, !result.Succeeded);
        Load(null);
        UndoLastCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _services.Renumber.CanUndoLast;

    private bool HasFolder() => Folder is not null;

    /// <summary>Rebuilds the file list, using <paramref name="scanned"/> if the folder was just scanned.</summary>
    private void Load(IReadOnlyCollection<string>? preselect, FolderIndex? scanned = null)
    {
        foreach (var old in Files)
        {
            old.PropertyChanged -= OnFilePropertyChanged;
        }

        Files.Clear();
        _index = null;
        SetPreview(null);
        if (Folder is null)
        {
            UpdatePreview();
            return;
        }

        try
        {
            _index = scanned ?? FolderIndex.Scan(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetMessage($"Can't read this folder: {ex.Message}", true);
            UpdatePreview();
            return;
        }

        var selected = new HashSet<string>(preselect ?? [], StringComparer.OrdinalIgnoreCase);
        int? previousMain = null;
        foreach (var shot in _index.Shots)
        {
            var vm = new RenumberFileViewModel(shot, shot.Main != previousMain) { IsSelected = selected.Contains(shot.FileName) };
            vm.PropertyChanged += OnFilePropertyChanged;
            Files.Add(vm);
            previousMain = shot.Main;
        }

        UpdatePreview();
    }

    private void OnFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenumberFileViewModel.IsSelected))
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        Preview.Clear();
        _plan = null;
        CanApplyPlan = false;
        OnPropertyChanged(nameof(SelectionSummary));
        var selection = Files.Where(f => f.IsSelected).Select(f => f.FileName).ToList();
        if (_index is null || selection.Count == 0)
        {
            return;
        }

        RenumberOperation? operation = Operation.Value switch
        {
            RenumberKind.Shift when MainDelta is not null && SubDelta is not null => new ShiftOperation((int)MainDelta, (int)SubDelta),
            RenumberKind.CloseGaps when !RenumberGroups || StartMain is not null => new CloseGapsOperation(RenumberGroups ? (int?)StartMain : null, CompactSubs),
            RenumberKind.MoveToGroup when TargetMain is not null && StartSub is not null => new MoveToGroupOperation((int)TargetMain, (int)StartSub),
            RenumberKind.NormaliseSolos => new NormaliseSolosOperation(),
            _ => null,
        };
        if (operation is null)
        {
            return;
        }

        _plan = RenumberPlanner.Plan(_index, selection, operation);
        foreach (var row in _plan.Rows)
        {
            Preview.Add(row);
        }

        CanApplyPlan = _plan.IsValid;
    }

    private void SetMessage(string? message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }

    /// <summary>Scans a folder, or returns null if it can't be read (the following load reports why).</summary>
    private static FolderIndex? TryScan(string folder)
    {
        try
        {
            return FolderIndex.Scan(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
