using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.Core.Tests;

public class SessionEngineNumberingTests
{
    [Fact]
    public async Task Main_then_subs_renames_the_solo_and_keeps_its_tail()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, Options.NextShot(folder));

        h.Engine.ToggleTimestamp();
        await h.Main();
        await h.Sub();
        await h.Sub();
        await h.Main();

        Assert.Equal(["1-1 (2026-09-27 10.05.33 UTC+13).png", "1-2.png", "1-3.png", "2.png"], folder.Names());
        Assert.Equal("image-1", folder.Read("1-1 (2026-09-27 10.05.33 UTC+13).png"));
        Assert.Equal([FeedbackKind.TimestampArmed, FeedbackKind.MainSaved, FeedbackKind.SubSaved, FeedbackKind.SubSaved, FeedbackKind.MainSaved],
            h.Feedback.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Sub_first_in_new_group_starts_at_one_without_touching_the_previous_group()
    {
        // Regression for the auto-start script, which wrote {highest}-2 into the existing group (and could overwrite it).
        using var folder = new TempFolder().With("7-1.png", "7-2.png");
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.NewGroup, 8, 1, CaptureTarget.PrimaryMonitor));

        await h.Sub();
        await h.Sub();

        Assert.Equal(["7-1.png", "7-2.png", "8-1.png", "8-2.png"], folder.Names());
        Assert.Equal("7-2.png", folder.Read("7-2.png"));
    }

    [Fact]
    public async Task Main_first_with_custom_start_uses_the_configured_number()
    {
        // Regression for the custom-start script, which skipped the configured main (X+1).
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.NewGroup, 5, 1, CaptureTarget.PrimaryMonitor));

        await h.Main();

        Assert.Equal(["5.png"], folder.Names());
    }

    [Fact]
    public async Task Continue_group_appends_subs_and_main_opens_next_free_group()
    {
        using var folder = new TempFolder().With("3-1.png", "3-2.png", "4.png");
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 3, 3, CaptureTarget.PrimaryMonitor));

        await h.Sub();
        await h.Main();

        Assert.Equal(["3-1.png", "3-2.png", "3-3.png", "4.png", "5.png"], folder.Names());
        Assert.Equal(FeedbackKind.MainSaved, h.Feedback.Last.Kind);
        Assert.True(h.Feedback.Last.HasCaveat);
        Assert.Contains("4 was taken", h.Feedback.Last.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuing_a_pre_existing_solo_renames_it_too()
    {
        using var folder = new TempFolder().With("6 (old caption).png");
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 6, 2, CaptureTarget.PrimaryMonitor));

        await h.Sub();

        Assert.Equal(["6-1 (old caption).png", "6-2.png"], folder.Names());
    }

    [Fact]
    public async Task Always_number_first_shot_as_member_never_renames()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.NewGroup, 1, 1, CaptureTarget.PrimaryMonitor, AlwaysNumberFirstShotAsMember: true));

        await h.Main();
        await h.Sub();
        await h.Main();

        Assert.Equal(["1-1.png", "1-2.png", "2-1.png"], folder.Names());
    }

    [Fact]
    public async Task Timestamp_applies_once_caption_is_last_and_trimmed()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, Options.NextShot(folder));

        h.Engine.ToggleTimestamp();
        h.Engine.SetCaption("   login page  ");
        await h.Main();
        await h.Main();

        Assert.Equal(["1 (2026-09-27 10.05.33 UTC+13) (login page).png", "2.png"], folder.Names());
    }

    [Fact]
    public async Task Always_timestamp_with_utc_zone()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.NewGroup, 1, 1, CaptureTarget.PrimaryMonitor,
            AlwaysTimestamp: true, TimestampZone: Naming.TimestampZone.Utc));

        await h.Main();
        await h.Main();

        Assert.Equal(["1 (2026-09-26 21.05.33 UTC).png", "2 (2026-09-26 21.05.33 UTC).png"], folder.Names());
    }

    [Fact]
    public async Task Next_shot_mode_arms_a_flag_and_stamps_the_time_of_the_shot_not_of_the_key()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(13)));
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, Options.NextShot(folder), time: clock);

        h.Engine.ToggleTimestamp();
        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(33)));
        await h.Main();

        Assert.Equal(["1 (2026-09-27 10.05.33 UTC+13).png"], folder.Names());
    }

    [Fact]
    public void Toggling_timestamp_twice_disarms_it()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, Options.NextShot(folder));

        h.Engine.ToggleTimestamp();
        h.Engine.ToggleTimestamp();

        Assert.False(h.Engine.TimestampArmed);
        Assert.Equal(FeedbackKind.TimestampDisarmed, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task Capture_failure_reports_an_error_and_writes_nothing()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        h.Capture.Fail = true;

        await h.Main();

        Assert.Empty(folder.Names());
        Assert.Equal(FeedbackKind.Error, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task Saved_sounds_fire_only_once_the_file_is_on_disk()
    {
        using var folder = new TempFolder();
        var checker = new DiskCheckingFeedback(folder.Path);
        using var h = new EngineHarness(folder, feedback: checker);

        await h.Main();
        await h.Sub();
        await h.Sub();
        Directory.Delete(folder.Path, recursive: true);
        await h.Main();
        Directory.CreateDirectory(folder.Path);

        Assert.Equal(3, checker.SavedEvents);
        Assert.Empty(checker.SavedWithoutFile);
        Assert.Equal(FeedbackKind.Error, checker.Events[^1].Kind);
    }

    [Fact]
    public async Task No_temp_files_are_left_behind()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);

        await h.Main();
        await h.Sub();

        Assert.DoesNotContain(Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locked_solo_still_saves_the_new_shot_with_a_warning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows blocks renaming an open file.");
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();

        using (new FileStream(folder.File("1.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await h.Sub();
        }

        Assert.Equal(["1-2.png", "1.png"], folder.Names());
        Assert.Equal(FeedbackKind.SubSaved, h.Feedback.Last.Kind);
        Assert.True(h.Feedback.Last.HasCaveat);
    }
}

public class SessionEngineCollisionTests
{
    [Fact]
    public async Task Append_saves_after_the_groups_highest_sub()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2.png", "5-3.png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Append);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Equal(["5-1.png", "5-2.png", "5-3.png", "5-4.png"], folder.Names());
        var asked = Assert.Single(prompt.Asked);
        Assert.Equal((5, 2, 4), (asked.Main, asked.Sub, asked.AppendSub));
    }

    [Fact]
    public async Task Insert_shifts_later_members_up_and_keeps_their_tails()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2 (b).png", "5-3 (c).png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Insert);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Equal(["5-1.png", "5-2.png", "5-3 (b).png", "5-4 (c).png"], folder.Names());
        Assert.Equal("5-2 (b).png", folder.Read("5-3 (b).png"));
        Assert.Equal("image-1", folder.Read("5-2.png"));
    }

    [Fact]
    public async Task Overwrite_recycles_the_old_file()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2 (old).png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Overwrite);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Equal(["5-1.png", "5-2.png"], folder.Names());
        Assert.Equal(["5-2 (old).png"], h.Bin.Recycled);
    }

    [Fact]
    public async Task Discard_writes_nothing_and_keeps_the_cursor()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2.png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Discard).Then(SubCollisionChoice.Append);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();
        Assert.Equal(["5-1.png", "5-2.png"], folder.Names());

        await h.Sub();
        Assert.Equal(["5-1.png", "5-2.png", "5-3.png"], folder.Names());
        Assert.Equal(2, prompt.Asked.Count);
    }

    [Fact]
    public async Task Remembered_choice_is_not_asked_again()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2.png", "5-3.png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Append, remember: true);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 1, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();
        h.Engine.Undo();
        await h.Sub();

        Assert.Single(prompt.Asked);
    }

    [Fact]
    public async Task Overwrite_is_never_remembered()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2.png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Overwrite, remember: true).Then(SubCollisionChoice.Append);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 1, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();
        await h.Sub();

        Assert.Equal(2, prompt.Asked.Count);
    }

    [Fact]
    public async Task Insert_shifts_every_later_member_in_one_bulk_plan_so_nothing_cascades()
    {
        // 5-3, 5-5 and 5-6 would each collide with the next one if shifted one at a time; the planner moves them as one set,
        // highest first, so each target is already free when its turn comes. Gaps (no 5-4 before) are preserved, not compacted.
        using var folder = new TempFolder().With("5-1.png", "5-2.png", "5-4 (x).png", "5-5.png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Insert);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Equal(["5-1.png", "5-2.png", "5-3.png", "5-5 (x).png", "5-6.png"], folder.Names());
        Assert.Equal("image-1", folder.Read("5-2.png"));
        Assert.Equal("5-2.png", folder.Read("5-3.png"));
        Assert.Equal("5-5.png", folder.Read("5-6.png"));
    }

    [Fact]
    public async Task Insert_moves_files_that_already_share_a_number_together()
    {
        // 5-2 and "5-2 (dup)" already share a number; a uniform +1 keeps them sharing 5-3, which is no worse than before.
        using var folder = new TempFolder().With("5-1.png", "5-2.png", "5-2 (dup).png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Insert);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 2, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Equal(["5-1.png", "5-2.png", "5-3 (dup).png", "5-3.png"], folder.Names());
        Assert.Equal("image-1", folder.Read("5-2.png"));
        Assert.False(h.Feedback.Last.HasCaveat);
    }

    [Fact]
    public async Task Insert_that_would_break_the_path_limit_appends_instead_with_a_caveat()
    {
        // The only real refusals: a later file whose name would grow past 260 characters when its number gains a digit (5-9 → 5-10),
        // or a hidden file already occupying a target name. Numbers are unbounded, but Windows paths aren't.
        using var folder = new TempFolder();
        var longCaption = new string('x', Naming.CaptionRules.MaxPathLength - (folder.Path.Length + 1) - "5-9 ().png".Length);
        folder.With("5-8.png", $"5-9 ({longCaption}).png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Insert);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 8, CaptureTarget.PrimaryMonitor), prompt);

        await h.Sub();

        Assert.Contains("5-8.png", folder.Names());
        Assert.Contains($"5-9 ({longCaption}).png", folder.Names());
        Assert.Contains("5-10.png", folder.Names());
        Assert.True(h.Feedback.Last.HasCaveat);
    }

    [Fact]
    public async Task Persisted_policy_skips_the_prompt()
    {
        using var folder = new TempFolder().With("5-1.png");
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 1, CaptureTarget.PrimaryMonitor,
            SubCollisionPolicy: SubCollisionPolicy.Append));

        await h.Sub();

        Assert.Empty(h.Prompt.Asked);
        Assert.Equal(["5-1.png", "5-2.png"], folder.Names());
    }
}

public class SessionEngineUndoAndLedgerTests
{
    [Fact]
    public async Task Undo_recycles_the_shot_reverts_the_solo_rename_and_rewinds()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        await h.Sub();

        h.Engine.Undo();
        Assert.Equal(["1.png"], folder.Names());

        await h.Sub();
        Assert.Equal(["1-1.png", "1-2.png"], folder.Names());
    }

    [Fact]
    public async Task Undo_of_an_insert_shifts_members_back()
    {
        using var folder = new TempFolder().With("5-1.png", "5-2 (b).png");
        var prompt = new ScriptedPrompt().Then(SubCollisionChoice.Insert);
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.ContinueGroup, 5, 1, CaptureTarget.PrimaryMonitor), prompt);
        await h.Sub();
        Assert.Equal(["5-1.png", "5-2.png", "5-3 (b).png"], folder.Names());

        h.Engine.Undo();

        Assert.Equal(["5-1.png", "5-2 (b).png"], folder.Names());
        Assert.Equal("5-1.png", folder.Read("5-1.png"));
    }

    [Fact]
    public void Undo_with_nothing_to_undo_warns()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);

        h.Engine.Undo();

        Assert.Equal(FeedbackKind.Warning, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task Ledger_tracks_renames_and_orders_lowest_first()
    {
        using var folder = new TempFolder().With("1.png");
        using var h = new EngineHarness(folder, new SessionOptions(folder.Path, StartMode.NewGroup, 3, 1, CaptureTarget.PrimaryMonitor));
        await h.Main();
        await h.Main();
        await h.Sub();

        var files = h.Engine.End().Select(Path.GetFileName);

        Assert.Equal(["3.png", "4-1.png", "4-2.png"], files);
    }

    [Fact]
    public async Task Caption_budget_accounts_for_the_folder_path()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, Options.NextShot(folder));

        var budget = h.Engine.CaptionBudget();
        h.Engine.SetCaption(new string('c', budget));
        h.Engine.ToggleTimestamp();
        await h.Main();

        var name = Assert.Single(folder.Names());
        Assert.Contains(new string('c', budget), name, StringComparison.Ordinal);
        Assert.True(folder.File(name).Length + CaptionRules_GrowthReserve <= 259);
    }

    private const int CaptionRules_GrowthReserve = Naming.CaptionRules.GrowthReserve;
}

/// <summary>Timestamp and caption keys acting on the shot just taken (the default "capture first, label after" mode).</summary>
public class SessionEngineLastShotAnnotationTests
{
    [Fact]
    public async Task Timestamp_key_adds_then_removes_the_capture_time_on_the_last_shot()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();

        h.Engine.ToggleTimestamp();
        Assert.Equal(["1 (2026-09-27 10.05.33 UTC+13).png"], folder.Names());
        Assert.Equal(FeedbackKind.TimestampArmed, h.Feedback.Last.Kind);

        h.Engine.ToggleTimestamp();
        Assert.Equal(["1.png"], folder.Names());
        Assert.Equal("image-1", folder.Read("1.png"));
    }

    [Fact]
    public async Task Toggling_the_timestamp_repeatedly_always_shows_the_original_capture_time()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 9, 27, 10, 5, 33, TimeSpan.FromHours(13)));
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder, time: clock);
        await h.Main();

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(7));
            h.Engine.ToggleTimestamp();
            Assert.Equal(["1 (2026-09-27 10.05.33 UTC+13).png"], folder.Names());

            clock.Advance(TimeSpan.FromMinutes(7));
            h.Engine.ToggleTimestamp();
            Assert.Equal(["1.png"], folder.Names());
        }
    }

    [Fact]
    public async Task Caption_goes_on_the_last_shot_after_any_timestamp_and_survives_the_group_growing()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        h.Engine.ToggleTimestamp();

        var request = h.Engine.PrepareCaption();
        Assert.NotNull(request);
        Assert.Equal("1", request.Target);
        h.Engine.SetCaption("  login page ");
        await h.Sub();

        Assert.Equal(["1-1 (2026-09-27 10.05.33 UTC+13) (login page).png", "1-2.png"], folder.Names());
    }

    [Fact]
    public async Task Editing_prefills_the_existing_caption_and_empty_text_removes_it()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        h.Engine.SetCaption("first");

        Assert.Equal("first", h.Engine.PrepareCaption()!.Initial);
        h.Engine.SetCaption("");

        Assert.Equal(["1.png"], folder.Names());
        Assert.Equal(FeedbackKind.CaptionCancelled, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task Cancelled_caption_box_changes_nothing()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        h.Engine.SetCaption("keep");

        h.Engine.SetCaption(null);

        Assert.Equal(["1 (keep).png"], folder.Names());
    }

    [Fact]
    public void Nothing_to_annotate_before_the_first_shot()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);

        h.Engine.ToggleTimestamp();
        Assert.Null(h.Engine.PrepareCaption());

        Assert.Empty(folder.Names());
        Assert.Equal(FeedbackKind.Warning, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task A_shot_renamed_outside_the_app_is_never_annotated()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        File.Move(folder.File("1.png"), folder.File("1 (my own note).png"));

        h.Engine.ToggleTimestamp();

        Assert.Equal(["1 (my own note).png"], folder.Names());
        Assert.Equal(FeedbackKind.Warning, h.Feedback.Last.Kind);
    }

    [Fact]
    public async Task Undo_after_annotating_removes_the_annotated_shot()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();
        await h.Main();
        h.Engine.SetCaption("second");

        h.Engine.Undo();

        Assert.Equal(["1.png"], folder.Names());
    }

    [Fact]
    public async Task Last_shot_caption_budget_leaves_room_for_a_later_timestamp()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await h.Main();

        var budget = h.Engine.PrepareCaption()!.Budget;
        h.Engine.SetCaption(new string('c', budget));
        h.Engine.ToggleTimestamp();

        var name = Assert.Single(folder.Names());
        Assert.Contains(new string('c', budget), name, StringComparison.Ordinal);
        Assert.Contains("UTC+13", name, StringComparison.Ordinal);
        Assert.True(folder.File(name).Length + Naming.CaptionRules.GrowthReserve <= Naming.CaptionRules.MaxPathLength);
    }
}

/// <summary>Option presets for engine tests.</summary>
internal static class Options
{
    public static SessionOptions NextShot(TempFolder folder) =>
        new(folder.Path, StartMode.NewGroup, 1, 1, CaptureTarget.PrimaryMonitor, AnnotationTarget: AnnotationTarget.NextShot);
}
