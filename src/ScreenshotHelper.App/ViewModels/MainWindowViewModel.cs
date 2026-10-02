using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenshotHelper.App.Services;

namespace ScreenshotHelper.App.ViewModels;

/// <summary>Screen after a session: what was saved, and the natural next steps.</summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<SessionSummary> _renumber;
    private readonly Action _newSession;

    public SummaryViewModel(AppServices services, SessionSummary summary, Action<SessionSummary> renumber, Action newSession)
    {
        _services = services;
        Summary = summary;
        _renumber = renumber;
        _newSession = newSession;
        Files = [.. summary.Files.Select(Path.GetFileName).OfType<string>()];
    }

    public SessionSummary Summary { get; }

    public ObservableCollection<string> Files { get; }

    public string Heading => Files.Count switch
    {
        0 => "No screenshots were saved",
        1 => "1 screenshot saved",
        _ => $"{Files.Count} screenshots saved",
    };

    public string Folder => Summary.Folder;

    public bool HasFiles => Files.Count > 0;

    [RelayCommand]
    private void Reveal() => _services.Platform.Revealer.Reveal(Summary.Folder, Summary.Files);

    [RelayCommand]
    private void Renumber() => _renumber(Summary);

    [RelayCommand]
    private void NewSession() => _newSession();
}

/// <summary>Navigation between the main window's pages.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>The Session tab's folder as last carried over to the Renumber tab.</summary>
    private string? _sessionFolderSeen;

    public MainWindowViewModel(AppServices services, HomeViewModel home, SettingsViewModel settings, RenumberViewModel renumber)
    {
        Services = services;
        Home = home;
        Settings = settings;
        Renumber = renumber;
        CurrentPage = home;
        home.RenumberRequested += folder => ShowRenumberFor(folder, null);
    }

    public AppServices Services { get; }

    public HomeViewModel Home { get; }

    public SettingsViewModel Settings { get; }

    public RenumberViewModel Renumber { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsSessionTab), nameof(IsRenumberTab), nameof(IsSettingsTab))]
    public partial object CurrentPage { get; set; }

    public bool IsHome => ReferenceEquals(CurrentPage, Home);

    /// <summary>The Session tab covers setting up a session and its summary afterwards.</summary>
    public bool IsSessionTab => IsHome || CurrentPage is SummaryViewModel;

    public bool IsRenumberTab => ReferenceEquals(CurrentPage, Renumber);

    public bool IsSettingsTab => ReferenceEquals(CurrentPage, Settings);

    public void ShowSummary(SessionSummary summary) =>
        CurrentPage = new SummaryViewModel(Services, summary, s => ShowRenumberFor(s.Folder, [.. s.Files.Select(Path.GetFileName).OfType<string>()]), ShowHome);

    public void ShowRenumberFor(string folder, IReadOnlyCollection<string>? files)
    {
        if (string.Equals(folder, Home.Folder, StringComparison.OrdinalIgnoreCase))
        {
            _sessionFolderSeen = folder;
        }

        Renumber.Open(folder, files);
        CurrentPage = Renumber;
    }

    /// <summary>
    /// The window was activated again: files may have been changed in another app meanwhile, so a shown Renumber list is checked (one
    /// directory listing; rebuilt only if its screenshots changed).
    /// </summary>
    public void WindowActivated()
    {
        if (ReferenceEquals(CurrentPage, Renumber) && Renumber.Folder is { } folder)
        {
            Renumber.Open(folder);
        }
    }

    [RelayCommand]
    public void ShowHome()
    {
        Settings.CancelCapture();
        Home.Refresh();
        CurrentPage = Home;
    }

    [RelayCommand]
    private void ShowSettings() => CurrentPage = Settings;

    /// <summary>
    /// Opens the Renumber tab on the Session tab's folder if that changed since the last visit; otherwise on the folder the Renumber tab
    /// already shows (possibly one chosen there), checked for changed files.
    /// </summary>
    [RelayCommand]
    private void ShowRenumber()
    {
        var sessionFolder = Home.Folder;
        if (sessionFolder is not null && !string.Equals(sessionFolder, _sessionFolderSeen, StringComparison.OrdinalIgnoreCase))
        {
            ShowRenumberFor(sessionFolder, null);
            return;
        }

        if (Renumber.Folder is { } current)
        {
            Renumber.Open(current);
        }

        CurrentPage = Renumber;
    }
}
