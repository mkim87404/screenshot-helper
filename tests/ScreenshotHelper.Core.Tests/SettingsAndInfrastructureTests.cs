using System.Buffers.Binary;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_files_give_defaults()
    {
        using var dir = new TempFolder();
        var store = new SettingsStore(dir.Path, AppLog.Null);

        Assert.Equal(AppSettings.DefaultHotkeys, store.LoadSettings().Hotkeys);
        Assert.Empty(store.LoadState().RecentFolders);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var dir = new TempFolder();
        var store = new SettingsStore(dir.Path, AppLog.Null);
        var custom = AppSettings.Default with
        {
            Theme = AppTheme.Dark,
            SoundVolume = 35,
            TimestampZone = Naming.TimestampZone.Utc,
            MutedSounds = [FeedbackKind.Paused],
            Hotkeys = new Dictionary<HotkeyAction, KeyChord>(AppSettings.DefaultHotkeys) { [HotkeyAction.MainShot] = KeyChord.CtrlShift("KeyA") },
        };

        store.Save(custom);
        var loaded = store.LoadSettings();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(35, loaded.SoundVolume);
        Assert.Equal(Naming.TimestampZone.Utc, loaded.TimestampZone);
        Assert.Equal([FeedbackKind.Paused], loaded.MutedSounds);
        Assert.Equal(KeyChord.CtrlShift("KeyA"), loaded.Hotkeys[HotkeyAction.MainShot]);
        Assert.Contains("\"theme\": \"Dark\"", File.ReadAllText(store.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_settings_are_quarantined_and_defaults_used()
    {
        using var dir = new TempFolder();
        var store = new SettingsStore(dir.Path, AppLog.Null);
        File.WriteAllText(store.SettingsPath, "{ not json");

        var loaded = store.LoadSettings();

        Assert.Equal(AppSettings.Default.Theme, loaded.Theme);
        Assert.False(File.Exists(store.SettingsPath));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Hand_edited_values_are_sanitised()
    {
        using var dir = new TempFolder();
        var store = new SettingsStore(dir.Path, AppLog.Null);
        File.WriteAllText(store.SettingsPath, """{ "soundVolume": 900, "hotkeys": { "MainShot": { "modifiers": "None", "code": "" } } }""");

        var loaded = store.LoadSettings();

        Assert.Equal(100, loaded.SoundVolume);
        Assert.Equal(AppSettings.DefaultHotkeys[HotkeyAction.MainShot], loaded.Hotkeys[HotkeyAction.MainShot]);
        Assert.Equal(AppSettings.DefaultHotkeys.Count, loaded.Hotkeys.Count);
    }

    [Fact]
    public void Recent_folders_are_most_recent_first_deduplicated_and_capped()
    {
        var state = new AppState();
        for (var i = 0; i < 12; i++)
        {
            state = state.WithRecentFolder(Path.Combine(Path.GetTempPath(), $"f{i}"));
        }

        state = state.WithRecentFolder(Path.Combine(Path.GetTempPath(), "F5"));

        Assert.Equal(AppState.MaxRecentFolders, state.RecentFolders.Count);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "F5"), state.RecentFolders[0]);
        Assert.Single(state.RecentFolders, f => f.EndsWith("f5", StringComparison.OrdinalIgnoreCase));
    }
}

public class AppLogTests
{
    [Fact]
    public void Lines_from_many_threads_all_reach_the_file_after_flush()
    {
        using var dir = new TempFolder();
        using var log = new AppLog(dir.Path);

        Parallel.For(0, 200, i => log.Info($"line {i}"));
        log.Flush(TimeSpan.FromSeconds(5));

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "app-*.log")));
        Assert.All(Enumerable.Range(0, 200), i => Assert.Contains($"INFO  line {i}" + Environment.NewLine, text, StringComparison.Ordinal));
    }

    [Fact]
    public void Exceptions_are_written_with_their_stack()
    {
        using var dir = new TempFolder();
        using var log = new AppLog(dir.Path);

        log.Error("boom", new InvalidOperationException("details"));
        log.Flush(TimeSpan.FromSeconds(5));

        Assert.Contains("InvalidOperationException: details", File.ReadAllText(Directory.GetFiles(dir.Path)[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_after_flush_are_dropped_without_throwing()
    {
        using var dir = new TempFolder();
        using var log = new AppLog(dir.Path);
        log.Flush(TimeSpan.FromSeconds(5));

        log.Info("too late");

        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}

public class RetentionTests
{
    [Fact]
    public void Log_prune_keeps_recent_days_and_unrelated_files()
    {
        using var dir = new TempFolder().With("app-20260801.log", "app-20260829.log", "app-20260927.log", "notes.txt", "app-garbage.log");

        using var log = new AppLog(dir.Path);
        var removed = log.Prune(30, new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, removed);
        Assert.Equal(["app-20260829.log", "app-20260927.log", "app-garbage.log", "notes.txt"], dir.Names());
    }

    [Fact]
    public void Only_the_newest_three_corrupt_backups_are_kept()
    {
        using var dir = new TempFolder().With(
            "settings.json.corrupt-20260101000000", "settings.json.corrupt-20260102000000",
            "settings.json.corrupt-20260103000000", "settings.json.corrupt-20260104000000");
        var store = new SettingsStore(dir.Path, AppLog.Null);
        File.WriteAllText(store.SettingsPath, "{ broken");

        store.LoadSettings();

        var backups = dir.Names().Where(n => n.Contains(".corrupt-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, backups.Count);
        Assert.DoesNotContain("settings.json.corrupt-20260101000000", backups);
        Assert.DoesNotContain("settings.json.corrupt-20260102000000", backups);
    }

    [Fact]
    public void Orphaned_shot_temp_files_are_removed_but_renumber_temps_are_kept()
    {
        using var dir = new TempFolder().With(".~sshelper-abc.tmp", ".~sshelper-renumber-1234-0.tmp", "1.png");

        var removed = IO.ShotFile.CleanOrphans(dir.Path);

        Assert.Equal(1, removed);
        Assert.Equal([".~sshelper-renumber-1234-0.tmp", "1.png"], dir.Names());
    }
}

public class HotkeyPlanTests
{
    [Fact]
    public void Each_phase_registers_the_right_actions()
    {
        var bindings = AppSettings.DefaultHotkeys;

        Assert.Equal(bindings.Count, HotkeyPlan.For(SessionPhase.Active, bindings).Count);
        Assert.Equal([HotkeyAction.PauseResume], HotkeyPlan.For(SessionPhase.Paused, bindings).Keys);
        Assert.Empty(HotkeyPlan.For(SessionPhase.Modal, bindings));
        Assert.Empty(HotkeyPlan.For(SessionPhase.Idle, bindings));
    }

    [Fact]
    public void Default_bindings_have_no_duplicates_and_duplicates_are_detected()
    {
        Assert.Empty(HotkeyPlan.Duplicates(AppSettings.DefaultHotkeys));

        var clash = new Dictionary<HotkeyAction, KeyChord>(AppSettings.DefaultHotkeys) { [HotkeyAction.Caption] = KeyChord.Key("KeyT") };
        Assert.Equal([HotkeyAction.ToggleTimestamp, HotkeyAction.Caption], Assert.Single(HotkeyPlan.Duplicates(clash)).Order());
    }

    [Fact]
    public void Chord_description_orders_modifiers()
    {
        Assert.Equal("Ctrl+Shift+Space", KeyChord.CtrlShift("Space").ToString());
    }
}

public class SoundTests
{
    [Fact]
    public void Every_feedback_kind_has_a_valid_wav()
    {
        var bank = SoundBank.Create(70);

        foreach (var kind in Enum.GetValues<FeedbackKind>())
        {
            var wav = bank[kind];
            Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
            Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
            Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        }
    }

    [Fact]
    public void Zero_volume_is_silent()
    {
        var wav = ToneSynth.Render([new Tone(440, 50)], 0);

        Assert.All(Enumerable.Range(0, (wav.Length - 44) / 2), i => Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2)))));
    }
}

public class SessionActorTests
{
    [Fact]
    public async Task Commands_run_in_order_and_one_failure_does_not_stop_the_queue()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        var actor = new SessionActor(h.Engine, h.Feedback, AppLog.Null);

        actor.Post((e, ct) => e.TakeShotAsync(ShotKind.Main, ct));
        actor.Post(_ => throw new InvalidOperationException("boom"));
        actor.Post((e, ct) => e.TakeShotAsync(ShotKind.Sub, ct));
        await actor.CompleteAsync();

        Assert.Equal(["1-1.png", "1-2.png"], folder.Names());
        Assert.Contains(h.Feedback.Events, e => e.Kind == FeedbackKind.Error);
        Assert.False(actor.Post(_ => { }));
    }

    [Fact]
    public async Task InvokeAsync_returns_results()
    {
        using var folder = new TempFolder();
        using var h = new EngineHarness(folder);
        await using var actor = new SessionActor(h.Engine, h.Feedback, AppLog.Null);

        var budget = await actor.InvokeAsync(e => e.CaptionBudget());

        Assert.True(budget > 100);
    }
}
