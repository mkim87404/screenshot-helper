using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.App.Views;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;

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
}
