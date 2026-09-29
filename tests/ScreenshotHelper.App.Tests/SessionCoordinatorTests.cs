using System.Collections.Concurrent;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.App.Tests;

public class SessionCoordinatorTests
{
    [Fact]
    public async Task Full_session_registers_keys_saves_shots_and_releases_keys_at_the_end()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        var ui = new FakeSessionUi();
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());

        var failures = coordinator.Start(Options(folder, 1));
        Assert.Empty(failures);
        Assert.True(ui.Started);
        Assert.Equal(AppSettings.DefaultHotkeys.Count, h.Hotkeys.Registered.Count);

        h.Hotkeys.Press(HotkeyAction.MainShot);
        h.Hotkeys.Press(HotkeyAction.SubShot);
        h.Hotkeys.Press(HotkeyAction.ToggleTimestamp);
        h.Hotkeys.Press(HotkeyAction.SubShot);
        await coordinator.EndAsync();

        Assert.Empty(h.Hotkeys.Registered);
        Assert.Equal(3, folder.Names().Length);
        // Default "last shot" mode: t timestamps the shot just taken (1-2), not the next one.
        Assert.StartsWith("1-2 (", folder.Names()[1], StringComparison.Ordinal);
        Assert.Equal("1-3.png", folder.Names()[2]);
        Assert.Equal(3, ui.Summary!.Files.Count);
        Assert.True(h.Sound.Played > 0);
    }

    [Fact]
    public async Task Pause_keeps_only_the_pause_key_and_resume_restores_all()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        await using var coordinator = new SessionCoordinator(h.Services, new FakeSessionUi(), action => action());
        coordinator.Start(Options(folder, 1));

        h.Hotkeys.Press(HotkeyAction.PauseResume);
        Assert.Equal(SessionPhase.Paused, coordinator.Phase);
        Assert.Equal([HotkeyAction.PauseResume], h.Hotkeys.Registered.Keys);

        h.Hotkeys.Press(HotkeyAction.MainShot);
        h.Hotkeys.Press(HotkeyAction.PauseResume);
        Assert.Equal(SessionPhase.Active, coordinator.Phase);
        Assert.Equal(AppSettings.DefaultHotkeys.Count, h.Hotkeys.Registered.Count);

        await coordinator.EndAsync();
        Assert.Empty(folder.Names());
    }

    [Fact]
    public async Task Caption_releases_keys_while_the_box_is_open_and_applies_the_trimmed_text()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        var ui = new FakeSessionUi { Hotkeys = h.Hotkeys, CaptionAnswer = _ => "  login page  " };
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());
        coordinator.Start(Options(folder, 1));

        // Default mode: capture first, then caption the shot just taken.
        h.Hotkeys.Press(HotkeyAction.MainShot);
        await coordinator.CaptionAsync();
        await coordinator.EndAsync();

        Assert.Empty(ui.RegisteredDuringCaption!);
        Assert.Equal(["1 (login page).png"], folder.Names());
    }

    [Fact]
    public async Task Collision_prompt_goes_through_the_ui_with_keys_released()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("5-1.png");
        var ui = new FakeSessionUi { CollisionAnswer = new SubCollisionAnswer(SubCollisionChoice.Append, false) };
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());
        coordinator.Start(Options(folder, 5, StartMode.ContinueGroup, 1));

        h.Hotkeys.Press(HotkeyAction.SubShot);
        await coordinator.EndAsync();

        Assert.Equal(["5-1.png", "5-2.png"], folder.Names());
    }

    [Fact]
    public async Task Ending_with_a_collision_prompt_open_closes_it_and_discards_the_shot()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("5-1.png");
        var ui = new FakeSessionUi { PendingCollision = new TaskCompletionSource<SubCollisionAnswer>() };
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());
        coordinator.Start(Options(folder, 5, StartMode.ContinueGroup, 1));

        h.Hotkeys.Press(HotkeyAction.SubShot);
        await WaitUntil(() => coordinator.Phase == SessionPhase.Modal);
        await coordinator.EndAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, ui.PromptsClosed);
        Assert.Equal(["5-1.png"], folder.Names());
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task A_caption_request_still_queued_when_the_session_ends_opens_no_box()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir().With("5-1.png");
        var ui = new FakeSessionUi { Hotkeys = h.Hotkeys, PendingCollision = new TaskCompletionSource<SubCollisionAnswer>() };
        var uiQueue = new ConcurrentQueue<Action>();
        await using var coordinator = new SessionCoordinator(h.Services, ui, uiQueue.Enqueue);

        // "Next shot" mode, so the caption box would open even though no shot was saved.
        coordinator.Start(Options(folder, 5, StartMode.ContinueGroup, 1) with { AnnotationTarget = AnnotationTarget.NextShot });
        PumpUi(uiQueue);

        // The colliding shot is queued first, so the caption request waits behind it on the actor.
        h.Hotkeys.Press(HotkeyAction.SubShot);
        var caption = coordinator.CaptionAsync();

        // Ending closes the prompts; the actor then reaches the caption request, but the box must not open.
        var end = coordinator.EndAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!end.IsCompleted && DateTime.UtcNow < deadline)
        {
            PumpUi(uiQueue);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(end.IsCompleted, "EndAsync did not finish.");
        await end;
        await caption.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, ui.PromptsClosed);
        Assert.Null(ui.RegisteredDuringCaption);
        Assert.Equal(["5-1.png"], folder.Names());
    }

    [Fact]
    public async Task A_key_in_use_elsewhere_blocks_starting_and_registers_nothing()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        h.Hotkeys.InUseElsewhere.Add(AppSettings.DefaultHotkeys[HotkeyAction.EndSession]);
        var ui = new FakeSessionUi();
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());

        var failures = coordinator.Start(Options(folder, 1));

        Assert.Equal(HotkeyFailure.InUse, failures[HotkeyAction.EndSession]);
        Assert.False(coordinator.IsRunning);
        Assert.False(ui.Started);
        Assert.Empty(h.Hotkeys.Registered);
    }

    [Fact]
    public async Task Muted_sounds_and_disabled_toasts_are_respected()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        h.Services.UpdateSettings(h.Services.Settings with { SoundsEnabled = false, ToastsEnabled = false });
        var ui = new FakeSessionUi();
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());
        coordinator.Start(Options(folder, 1));

        h.Hotkeys.Press(HotkeyAction.MainShot);
        await coordinator.EndAsync();

        Assert.Equal(0, h.Sound.Played);
        Assert.Empty(ui.Toasts);
    }

    [Fact]
    public async Task Errors_are_reported_with_the_error_sound_and_toast()
    {
        using var h = new ServicesHarness();
        using var folder = new TempDir();
        var ui = new FakeSessionUi();
        await using var coordinator = new SessionCoordinator(h.Services, ui, action => action());
        coordinator.Start(Options(folder, 1));
        Directory.Delete(folder.Path, recursive: true);

        h.Hotkeys.Press(HotkeyAction.MainShot);
        await coordinator.EndAsync();

        Assert.Contains(ui.Toasts, t => t.Kind == FeedbackKind.Error);
        Directory.CreateDirectory(folder.Path);
    }

    private static SessionOptions Options(TempDir folder, int main, StartMode mode = StartMode.NewGroup, int sub = 1) =>
        new(folder.Path, mode, main, sub, CaptureTarget.PrimaryMonitor);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // Runs everything posted to the fake UI thread so far, in order.
    private static void PumpUi(ConcurrentQueue<Action> queue)
    {
        while (queue.TryDequeue(out var action))
        {
            action();
        }
    }
}
