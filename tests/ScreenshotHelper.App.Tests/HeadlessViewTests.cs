using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.App.Views;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App.Tests;

/// <summary>Real views rendered on the headless platform, driven with simulated keyboard input.</summary>
public class HeadlessViewTests
{
    [AvaloniaFact]
    public void Caption_box_refuses_typing_past_the_budget_and_enter_confirms()
    {
        var window = new CaptionWindow { DataContext = new CaptionViewModel(new CaptionRequest(8, null, "5")) };
        window.Show();

        // What the user actually sees in the box — not just the view-model — must stop at the limit, without invalid characters.
        window.KeyTextInput("login*page!!");
        var box = window.FindControl<TextBox>("CaptionBox")!;
        Assert.Equal("loginpag", box.Text);
        Assert.True(((CaptionViewModel)window.DataContext!).RemovedInvalid);

        window.KeyTextInput("xyz");
        Assert.Equal("loginpag", box.Text);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("loginpag", window.Result);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Caption_box_escape_cancels()
    {
        var window = new CaptionWindow { DataContext = new CaptionViewModel(new CaptionRequest(20, null, "5")) };
        window.Show();
        window.KeyTextInput("abc");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.Null(window.Result);
    }

    [AvaloniaFact]
    public void Collision_prompt_enter_appends_and_escape_discards()
    {
        var collision = new SubCollision("C:\\f", 5, 2, ["5-2.png"], 4);
        var append = new CollisionPromptWindow { DataContext = new CollisionPromptViewModel(collision) };
        append.Show();
        append.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(SubCollisionChoice.Append, append.Answer.Choice);

        var discard = new CollisionPromptWindow { DataContext = new CollisionPromptViewModel(collision) };
        discard.Show();
        discard.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal(SubCollisionChoice.Discard, discard.Answer.Choice);
    }

    [AvaloniaTheory]
    [InlineData(new[] { Key.Down }, SubCollisionChoice.Insert)]
    [InlineData(new[] { Key.Down, Key.Down }, SubCollisionChoice.Overwrite)]
    [InlineData(new[] { Key.Down, Key.Down, Key.Down }, SubCollisionChoice.Discard)]
    [InlineData(new[] { Key.Down, Key.Up }, SubCollisionChoice.Append)]
    [InlineData(new[] { Key.Up }, SubCollisionChoice.Append)]
    public void Collision_prompt_arrow_keys_move_between_choices_and_enter_picks_one(Key[] keys, SubCollisionChoice expected)
    {
        var collision = new SubCollision("C:\f", 5, 2, ["5-2.png"], 4);
        var prompt = new CollisionPromptWindow { DataContext = new CollisionPromptViewModel(collision) };
        prompt.Show();

        foreach (var key in keys)
        {
            prompt.KeyPress(key, RawInputModifiers.None, key == Key.Down ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp, null);
        }

        prompt.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(expected, prompt.Answer.Choice);
    }

    [AvaloniaFact]
    public async Task Settings_view_captures_a_new_key_from_the_keyboard()
    {
        using var h = new ServicesHarness();
        var vm = new SettingsViewModel(h.Services);
        var window = new Window { Content = new SettingsView { DataContext = vm } };
        window.Show();

        vm.BeginCapture(vm.Hotkeys.Single(r => r.Action == HotkeyAction.EndSession));
        window.KeyPress(Key.X, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.X, null);

        Assert.Equal(KeyChord.CtrlShift("KeyX"), h.Services.Settings.Hotkeys[HotkeyAction.EndSession]);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void Main_window_restores_the_remembered_size_and_reports_it_back()
    {
        var window = new MainWindow();
        window.RestoreSize(new Core.Settings.AppState { WindowWidth = 800, WindowHeight = 600 });
        window.Show();

        var (size, maximized) = window.Placement;

        Assert.False(maximized);
        Assert.True(size.Width <= 800 && size.Height <= 600);
        Assert.True(size.Width >= window.MinWidth && size.Height >= window.MinHeight);
    }

    [AvaloniaFact]
    public void Main_window_restores_maximized()
    {
        var window = new MainWindow();
        window.RestoreSize(new Core.Settings.AppState { WindowWidth = 800, WindowHeight = 600, WindowMaximized = true });

        Assert.Equal(WindowState.Maximized, window.WindowState);
    }

    [AvaloniaFact]
    public async Task Main_window_shows_each_page()
    {
        using var h = new ServicesHarness();
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        Func<Task<string?>> pick = () => Task.FromResult<string?>(null);
        var vm = new MainWindowViewModel(h.Services, new HomeViewModel(h.Services, coordinator, pick), new SettingsViewModel(h.Services), new RenumberViewModel(h.Services, pick));
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.NotEmpty(window.GetVisualDescendants().OfType<HomeView>());
        vm.ShowSettingsCommand.Execute(null);
        window.UpdateLayout();
        Assert.NotEmpty(window.GetVisualDescendants().OfType<SettingsView>());
        vm.ShowSummary(new SessionSummary(Path.GetTempPath(), []));
        window.UpdateLayout();
        Assert.NotEmpty(window.GetVisualDescendants().OfType<SummaryView>());
    }

    [AvaloniaFact]
    public async Task Long_folder_paths_are_trimmed_in_the_middle_and_shown_in_full_as_tooltips()
    {
        using var h = new ServicesHarness();
        var longFolder = Directory.CreateDirectory(Path.Combine(h.Data.Path, "Project screenshots " + new string('x', 120), "final")).FullName;
        var missing = Path.Combine(h.Data.Path, "gone " + new string('y', 120), "old");
        h.Services.UpdateState(s => s with { RecentFolders = [longFolder, missing] });
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null));
        var window = new Window { Width = 720, Height = 900, Content = new HomeView { DataContext = vm } };
        window.Show();
        window.UpdateLayout();

        var folderBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Screenshot folder");
        Assert.Equal(longFolder, ToolTip.GetTip(folderBox));

        var links = window.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is RecentFolderItem && b.Content is TextBlock).ToList();
        Assert.Equal(2, links.Count);
        foreach (var link in links)
        {
            var item = (RecentFolderItem)link.DataContext!;
            var text = (TextBlock)link.Content!;
            Assert.Equal(item.Display, ToolTip.GetTip(link));
            Assert.True(ToolTip.GetShowOnDisabled(link));
            Assert.Equal(TextTrimming.PathSegmentEllipsis, text.TextTrimming);

            // Too long for the window, so the visible line is actually shortened.
            Assert.Contains(text.TextLayout.TextLines, line => line.HasCollapsed);
        }

        Assert.False(links.Single(l => !((RecentFolderItem)l.DataContext!).Exists).IsEnabled);
    }

    [AvaloniaFact]
    public async Task Paused_toast_stays_up_until_resume_and_other_messages_give_way_back_to_it()
    {
        var toast = new ToastWindow { DisplayTime = TimeSpan.FromMilliseconds(40) };
        var paused = new FeedbackEvent(FeedbackKind.Paused, "Paused");

        toast.Show(new FeedbackEvent(FeedbackKind.MainSaved, "Saved 1"), ToastCorner.BottomRight);
        await TestWait.Until(() => !toast.IsVisible);

        toast.Show(paused, ToastCorner.BottomRight);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal("Paused", toast.Message);

        // A message during the pause (e.g. undo from the tray) times out back to "Paused", not to nothing.
        toast.Show(new FeedbackEvent(FeedbackKind.Undo, "Undone"), ToastCorner.BottomRight);
        Assert.Equal("Undone", toast.Message);
        await TestWait.Until(() => toast.Message == "Paused");

        toast.Show(new FeedbackEvent(FeedbackKind.Resumed, "Resumed"), ToastCorner.BottomRight);
        await TestWait.Until(() => !toast.IsVisible);

        // Ending the session while paused clears it too.
        toast.Show(paused, ToastCorner.BottomRight);
        toast.ClearPaused();
        Assert.False(toast.IsVisible);
        toast.Close();
    }

    [AvaloniaFact]
    public void Renumber_list_builds_only_the_rows_in_view()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With([.. Enumerable.Range(1, 40).SelectMany(g => Enumerable.Range(1, 20).Select(s => $"{g}-{s}.png"))]);
        var vm = new RenumberViewModel(h.Services, () => Task.FromResult<string?>(null));
        vm.Open(folder.Path);
        var window = new Window { Width = 900, Height = 860, Content = new RenumberView { DataContext = vm } };
        window.Show();
        window.UpdateLayout();

        var rows = window.GetVisualDescendants().OfType<CheckBox>().Count();
        Assert.Equal(800, vm.Files.Count);
        Assert.InRange(rows, 1, 100);
    }

    // A 1920×1080 screen whose working area is the whole screen, as Windows reports it when the taskbar auto-hides.
    [Theory]
    [InlineData(ToastCorner.BottomRight, 0, 0, 0, 48, 1444, 816)]   // taskbar at the bottom (Windows 11, and 10 by default)
    [InlineData(ToastCorner.BottomRight, 0, 0, 0, 120, 1444, 744)]  // taller taskbar (Windows 10, several rows)
    [InlineData(ToastCorner.BottomRight, 0, 0, 62, 0, 1382, 864)]   // taskbar on the right edge
    [InlineData(ToastCorner.BottomLeft, 62, 0, 0, 0, 78, 864)]      // taskbar on the left edge
    [InlineData(ToastCorner.TopRight, 0, 48, 0, 0, 1444, 64)]       // taskbar at the top
    [InlineData(ToastCorner.TopLeft, 0, 0, 0, 48, 16, 16)]          // bar on another edge leaves this corner alone
    [InlineData(ToastCorner.BottomRight, 0, 0, 0, 0, 1444, 864)]    // no auto-hide bar
    public void Toast_keeps_clear_of_an_auto_hide_bar_on_any_edge(ToastCorner corner, int left, int top, int right, int bottom, int x, int y)
    {
        var position = ToastWindow.Place(new PixelRect(0, 0, 1920, 1080), (left, top, right, bottom), new PixelSize(460, 200), 16, corner);

        Assert.Equal(new PixelPoint(x, y), position);
        var toast = new PixelRect(position, new PixelSize(460, 200));
        var visibleArea = new PixelRect(left, top, 1920 - left - right, 1080 - top - bottom);
        Assert.True(visibleArea.Contains(toast), $"{toast} overlaps the bar");
    }

    [AvaloniaFact]
    public async Task Start_session_stays_in_view_with_many_recent_folders_and_alt_s_starts()
    {
        using var h = new ServicesHarness();
        var folders = Enumerable.Range(1, 10).Select(i => Directory.CreateDirectory(Path.Combine(h.Data.Path, $"recent folder {i}")).FullName).ToList();
        h.Services.UpdateState(s => s with { RecentFolders = folders });
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), a => a());
        var vm = new HomeViewModel(h.Services, coordinator, () => Task.FromResult<string?>(null));

        // The smallest window the app allows.
        var window = new Window { Width = 720, Height = 560, Content = new HomeView { DataContext = vm } };
        window.Show();
        window.UpdateLayout();

        var start = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Start session");
        var topLeft = start.TranslatePoint(default, window)!.Value;
        Assert.True(topLeft.Y >= 0 && topLeft.Y + start.Bounds.Height <= window.ClientSize.Height, $"Start session is at y={topLeft.Y}, outside the window");

        window.KeyPress(Key.S, RawInputModifiers.Alt, PhysicalKey.S, "s");
        Assert.True(coordinator.IsRunning);
        await coordinator.EndAsync();
    }

    [AvaloniaFact]
    public void Tray_icon_image_resolves_under_the_spaced_assembly_name()
    {
        // The tray is only built by the real app, so a wrong resource name would only show up as a crash at start-up.
        Assert.StartsWith("avares://Screenshot Helper/", TrayController.AppIconUri.OriginalString, StringComparison.Ordinal);
        Assert.True(AssetLoader.Exists(TrayController.AppIconUri));
    }
}
