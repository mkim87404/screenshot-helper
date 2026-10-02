using ScreenshotHelper.App.Services;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App.Tests;

public class HomeViewModelTests
{
    [Fact]
    public async Task Folder_scan_fills_defaults_hint_and_collision_suggestion()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("7-1.png", "7-2 (login).png");
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null)) { Folder = folder.Path };

        Assert.Equal("Highest in folder: 7-2 (login).png", vm.HighestText);
        Assert.Equal(8, vm.MainNumber);

        vm.IsContinueMode = true;
        Assert.Equal((7m, 3m), (vm.MainNumber!.Value, vm.SubNumber!.Value));

        vm.SubNumber = 2;
        Assert.True(vm.CheckIsWarning);
        vm.UseSuggestionCommand.Execute(null);
        Assert.Equal(3, vm.SubNumber);
    }

    [Fact]
    public async Task Start_reports_unavailable_keys_and_remembers_the_folder_on_success()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null)) { Folder = folder.Path };

        h.Hotkeys.InUseElsewhere.Add(AppSettings.DefaultHotkeys[HotkeyAction.Undo]);
        vm.StartCommand.Execute(null);
        Assert.Contains("used by another app", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(coordinator.IsRunning);

        h.Hotkeys.InUseElsewhere.Clear();
        vm.StartCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(coordinator.IsRunning);
        Assert.Equal(folder.Path, h.Services.State.RecentFolders[0]);
        await coordinator.EndAsync();
    }
}

public class SettingToSessionTests
{
    [Fact]
    public async Task Next_shot_setting_makes_t_arm_the_following_shot()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        h.Services.UpdateSettings(h.Services.Settings with { AnnotationTarget = Core.Session.AnnotationTarget.NextShot });
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null)) { Folder = folder.Path };

        vm.StartCommand.Execute(null);
        h.Hotkeys.Press(HotkeyAction.ToggleTimestamp);
        h.Hotkeys.Press(HotkeyAction.MainShot);
        await coordinator.EndAsync();

        // With "next shot", t was pressed before any shot existed and still timestamped the shot that followed.
        var name = Assert.Single(folder.Names());
        Assert.Matches(@"^1 \(\d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2} UTC[+-][\d.]+\)\.png$", name);
    }

    [Fact]
    public async Task Default_setting_makes_t_act_on_the_shot_just_taken()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null)) { Folder = folder.Path };

        vm.StartCommand.Execute(null);
        h.Hotkeys.Press(HotkeyAction.ToggleTimestamp);
        h.Hotkeys.Press(HotkeyAction.MainShot);
        await coordinator.EndAsync();

        // In "last shot" mode, t before any shot does nothing, so the shot has no timestamp.
        Assert.Equal(["1.png"], folder.Names());
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public void Rebinding_rejects_duplicates_and_bare_pause_then_saves_valid_keys()
    {
        using var h = new ServicesHarness();
        var vm = new SettingsViewModel(h.Services);
        var caption = vm.Hotkeys.Single(r => r.Action == HotkeyAction.Caption);
        var pause = vm.Hotkeys.Single(r => r.Action == HotkeyAction.PauseResume);

        vm.BeginCapture(caption);
        vm.TryCapture(KeyChord.Key("KeyT"));
        Assert.True(caption.ProblemIsError);
        Assert.Equal(KeyChord.Key("KeyC"), h.Services.Settings.Hotkeys[HotkeyAction.Caption]);

        vm.BeginCapture(pause);
        vm.TryCapture(KeyChord.Key("Pause"));
        Assert.True(pause.ProblemIsError);

        vm.BeginCapture(caption);
        vm.TryCapture(KeyChord.Key("KeyN"));
        Assert.Equal(KeyChord.Key("KeyN"), h.Services.Settings.Hotkeys[HotkeyAction.Caption]);
        Assert.False(vm.IsCapturing);
    }

    [Fact]
    public void Chords_that_type_characters_get_a_warning()
    {
        using var h = new ServicesHarness();
        var vm = new SettingsViewModel(h.Services);
        var undo = vm.Hotkeys.Single(r => r.Action == HotkeyAction.Undo);

        vm.BeginCapture(undo);
        vm.TryCapture(new KeyChord(KeyModifiers.Ctrl | KeyModifiers.Alt, "KeyE"));

        Assert.NotNull(undo.Problem);
        Assert.False(undo.ProblemIsError);
    }

    [Fact]
    public void Escape_cancels_capturing()
    {
        using var h = new ServicesHarness();
        var vm = new SettingsViewModel(h.Services);
        vm.BeginCapture(vm.Hotkeys[0]);

        vm.TryCapture(KeyChord.Key("Escape"));

        Assert.False(vm.IsCapturing);
        Assert.Equal(AppSettings.DefaultHotkeys, h.Services.Settings.Hotkeys);
    }

    [Fact]
    public void Changes_persist_and_reset_restores_defaults_but_keeps_recent_folders()
    {
        using var h = new ServicesHarness();
        h.Services.UpdateState(s => s.WithRecentFolder(Path.GetTempPath()));
        var vm = new SettingsViewModel(h.Services);

        vm.SelectedTheme = vm.Themes.Single(t => t.Value == AppTheme.Dark);
        vm.AlwaysTimestamp = true;
        vm.SoundToggles.Single(t => t.Kind == Core.Feedback.FeedbackKind.Paused).IsOn = false;
        var reloaded = new SettingsStore(h.Services.Paths.Settings, Core.Diagnostics.AppLog.Null).LoadSettings();
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
        Assert.True(reloaded.AlwaysTimestamp);
        Assert.Equal([Core.Feedback.FeedbackKind.Paused], reloaded.MutedSounds);

        vm.ResetAllCommand.Execute(null);
        Assert.Equal(AppTheme.System, h.Services.Settings.Theme);
        Assert.False(vm.AlwaysTimestamp);
        Assert.Single(h.Services.State.RecentFolders);
    }
}

public class CaptionViewModelTests
{
    [Fact]
    public void Existing_caption_is_prefilled_and_counted()
    {
        var vm = new CaptionViewModel(new Core.Session.CaptionRequest(20, "login", "5-2"));

        Assert.Equal("login", vm.Text);
        Assert.Equal(15, vm.Remaining);
        Assert.Equal("Caption for 5-2", vm.Title);
    }

    [Fact]
    public void Result_is_trimmed_and_empty_means_remove()
    {
        var vm = new CaptionViewModel(new Core.Session.CaptionRequest(20, null, "5")) { Text = "  hi  " };
        Assert.Equal("hi", vm.Result);

        vm.Text = "   ";
        Assert.Equal(string.Empty, vm.Result);
    }

    [Fact]
    public void Zero_budget_disables_typing()
    {
        var vm = new CaptionViewModel(new Core.Session.CaptionRequest(0, null, "5"));

        Assert.False(vm.CanType);
        Assert.Contains("too long", vm.RemainingText, StringComparison.Ordinal);
    }
}

public class RenumberViewModelTests
{
    [Fact]
    public void Preview_apply_and_undo_round_trip()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("3-1 (a).png", "3-2.png", "9.png");
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path, ["3-1 (a).png", "3-2.png"]);

        vm.MainDelta = -2;
        Assert.Equal(["1-1 (a).png", "1-2.png"], vm.Preview.Select(r => r.Target));
        Assert.True(vm.CanApplyPlan);

        vm.ApplyCommand.Execute(null);
        Assert.Equal(["1-1 (a).png", "1-2.png", "9.png"], folder.Names());

        vm.UndoLastCommand.Execute(null);
        Assert.Equal(["3-1 (a).png", "3-2.png", "9.png"], folder.Names());
    }

    [Fact]
    public void Conflicting_plans_cannot_be_applied()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("3-1.png", "1-1.png");
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path, ["3-1.png"]);

        vm.MainDelta = -2;

        Assert.False(vm.CanApplyPlan);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Selecting_a_group_toggles_all_its_files()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("3-1.png", "3-2.png", "4.png");
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path);

        vm.SelectGroupCommand.Execute(vm.Files[0]);

        Assert.Equal([true, true, false], vm.Files.Select(f => f.IsSelected));
        Assert.Equal("2 of 3 selected", vm.SelectionSummary);
    }

    [Fact]
    public void Reopening_the_same_folder_keeps_the_list_and_selection_until_its_files_change()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("3-1.png", "3-2.png", "4.png");
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path);
        vm.Files[1].IsSelected = true;
        var rows = vm.Files.ToList();

        // Switching back to the tab with nothing changed: same rows, same selection, nothing rebuilt.
        vm.Open(folder.Path);
        Assert.Equal(rows, vm.Files);
        Assert.True(vm.Files[1].IsSelected);

        // A new screenshot in the folder: the list is rebuilt and the selection carried over.
        File.WriteAllBytes(Path.Combine(folder.Path, "5.png"), []);
        vm.Open(folder.Path);
        Assert.Equal(["3-1.png", "3-2.png", "4.png", "5.png"], vm.Files.Select(f => f.FileName));
        Assert.Equal(["3-2.png"], vm.Files.Where(f => f.IsSelected).Select(f => f.FileName));
    }

    [Fact]
    public void Opening_with_files_to_preselect_always_reloads_with_that_selection()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("3-1.png", "3-2.png", "4.png");
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path);
        vm.Files[0].IsSelected = true;

        vm.Open(folder.Path, ["4.png"]);

        Assert.Equal(["4.png"], vm.Files.Where(f => f.IsSelected).Select(f => f.FileName));
    }
}

public class RenumberNavigationTests
{
    [Fact]
    public async Task Renumber_tab_follows_a_changed_session_folder_and_otherwise_keeps_its_own()
    {
        using var h = new ServicesHarness();
        using var a = new TempDir().With("1.png");
        using var b = new TempDir().With("2.png");
        using var c = new TempDir().With("3.png");
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), x => x());
        var vm = Window(h, coordinator);

        vm.Home.Folder = a.Path;
        vm.ShowRenumberCommand.Execute(null);
        Assert.Equal(a.Path, vm.Renumber.Folder);

        // A folder chosen on the Renumber tab survives a trip to another tab while the Session folder stays the same.
        vm.Renumber.Folder = c.Path;
        vm.ShowSettingsCommand.Execute(null);
        vm.ShowRenumberCommand.Execute(null);
        Assert.Equal(c.Path, vm.Renumber.Folder);
        Assert.Equal(["3.png"], vm.Renumber.Files.Select(f => f.FileName));

        // Changing the Session folder carries over on the next visit.
        vm.Home.Folder = b.Path;
        vm.ShowRenumberCommand.Execute(null);
        Assert.Equal(["2.png"], vm.Renumber.Files.Select(f => f.FileName));
    }

    [Fact]
    public async Task Coming_back_to_the_window_refreshes_the_renumber_list_only_while_it_is_shown()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("1.png");
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), x => x());
        var vm = Window(h, coordinator);
        vm.Home.Folder = folder.Path;
        vm.ShowRenumberCommand.Execute(null);

        File.WriteAllBytes(Path.Combine(folder.Path, "2.png"), []);
        vm.WindowActivated();
        Assert.Equal(["1.png", "2.png"], vm.Renumber.Files.Select(f => f.FileName));

        vm.ShowSettingsCommand.Execute(null);
        File.WriteAllBytes(Path.Combine(folder.Path, "3.png"), []);
        vm.WindowActivated();
        Assert.Equal(2, vm.Renumber.Files.Count);
    }

    private static MainWindowViewModel Window(ServicesHarness h, SessionCoordinator coordinator)
    {
        Func<Task<string?>> pick = () => Task.FromResult<string?>(null);
        return new MainWindowViewModel(h.Services, new HomeViewModel(h.Services, coordinator, pick), new SettingsViewModel(h.Services), new RenumberViewModel(h.Services, pick));
    }
}

public class WindowSizingTests
{
    private static readonly Avalonia.Size FullHd = new(1920, 1040);

    [Fact]
    public void Default_size_is_used_when_nothing_is_remembered() =>
        Assert.Equal(WindowSizing.Default, WindowSizing.Fit(null, FullHd));

    [Fact]
    public void Remembered_size_is_restored_within_the_screen() =>
        Assert.Equal(new Avalonia.Size(1100, 900), WindowSizing.Fit(new Avalonia.Size(1100, 900), FullHd));

    [Fact]
    public void Remembered_size_larger_than_a_new_smaller_screen_is_clamped()
    {
        var laptop = new Avalonia.Size(1280, 680);

        var size = WindowSizing.Fit(new Avalonia.Size(1900, 1000), laptop);

        Assert.True(size.Width <= laptop.Width && size.Height <= laptop.Height);
    }

    [Fact]
    public void Minimum_shrinks_on_tiny_screens_so_the_window_still_fits()
    {
        var tiny = new Avalonia.Size(800, 500);

        var size = WindowSizing.Fit(new Avalonia.Size(300, 200), tiny);

        Assert.True(size.Height <= tiny.Height);
        Assert.True(WindowSizing.MinimumFor(tiny).Height < WindowSizing.Minimum.Height);
    }

    [Fact]
    public void Implausible_saved_sizes_are_discarded()
    {
        var state = new Core.Settings.AppState { WindowWidth = 5, WindowHeight = double.NaN }.Sanitized();

        Assert.Null(state.WindowWidth);
        Assert.Null(state.WindowHeight);
    }
}
