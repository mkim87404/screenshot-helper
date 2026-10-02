using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.IO;
using ScreenshotHelper.Core.Platform;

namespace ScreenshotHelper.Platform.Windows.Tests;

public class ScreenCaptureTests
{
    [Theory]
    [InlineData(CaptureTarget.PrimaryMonitor)]
    [InlineData(CaptureTarget.MonitorUnderCursor)]
    [InlineData(CaptureTarget.AllMonitors)]
    [InlineData(CaptureTarget.ActiveWindow)]
    public void Every_target_resolves_to_a_non_empty_area(CaptureTarget target)
    {
        var bounds = WindowsScreenCapture.ResolveBounds(target);

        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{target}: {bounds}");
    }

    [Fact]
    public void Captured_image_commits_as_a_valid_png()
    {
        var folder = Directory.CreateTempSubdirectory("sshelper-platform-");
        try
        {
            using var image = new WindowsScreenCapture(ScreenshotHelper.Core.Diagnostics.AppLog.Null).Capture(CaptureTarget.PrimaryMonitor);
            var path = ShotFile.Commit(folder.FullName, "1.png", image);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
            Assert.True(image.Width > 0 && image.Height > 0);
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}

public class RecycleBinTests
{
    [Fact]
    public void Recycling_removes_the_file_from_its_folder()
    {
        // Uses the real Recycle Bin (that's the behaviour under test), then removes its own entry so the bin is left as it was.
        var folder = Directory.CreateTempSubdirectory("sshelper-platform-");
        var path = Path.Combine(folder.FullName, $"sshelper-recycle-test-{Guid.NewGuid():N}.txt");
        var cleanedUp = 0;
        try
        {
            File.WriteAllText(path, "test");

            new WindowsRecycleBin().Recycle(path);

            Assert.False(File.Exists(path));
        }
        finally
        {
            cleanedUp = RecycleBinCleanup.Remove(path);
            folder.Delete(recursive: true);
        }

        Assert.Equal(1, cleanedUp);
    }

    [Fact]
    public void Recycling_a_missing_file_throws() =>
        Assert.Throws<FileNotFoundException>(() => new WindowsRecycleBin().Recycle(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")));
}

public class HotkeyTests
{
    // Ctrl+Alt+Shift+F24 is about as unlikely to be taken on any machine as a chord gets.
    private static readonly KeyChord Obscure = new(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift, "F24");

    [Fact]
    public void Registers_and_detects_a_chord_already_in_use()
    {
        using var first = new WindowsHotkeyService();
        using var second = new WindowsHotkeyService();

        Assert.Empty(first.Apply(new Dictionary<HotkeyAction, KeyChord> { [HotkeyAction.MainShot] = Obscure }));
        var failures = second.Apply(new Dictionary<HotkeyAction, KeyChord> { [HotkeyAction.MainShot] = Obscure });

        Assert.Equal(HotkeyFailure.InUse, failures[HotkeyAction.MainShot]);
    }

    [Fact]
    public void Applying_an_empty_set_releases_chords()
    {
        using var first = new WindowsHotkeyService();
        using var second = new WindowsHotkeyService();
        first.Apply(new Dictionary<HotkeyAction, KeyChord> { [HotkeyAction.MainShot] = Obscure });

        first.Apply(new Dictionary<HotkeyAction, KeyChord>());

        Assert.Empty(second.Apply(new Dictionary<HotkeyAction, KeyChord> { [HotkeyAction.MainShot] = Obscure }));
    }

    [Fact]
    public void Reserved_and_unknown_keys_are_unsupported()
    {
        using var service = new WindowsHotkeyService();

        var failures = service.Apply(new Dictionary<HotkeyAction, KeyChord>
        {
            [HotkeyAction.MainShot] = KeyChord.CtrlShift("F12"),
            [HotkeyAction.SubShot] = KeyChord.CtrlShift("NotAKey"),
        });

        Assert.Equal(HotkeyFailure.Unsupported, failures[HotkeyAction.MainShot]);
        Assert.Equal(HotkeyFailure.Unsupported, failures[HotkeyAction.SubShot]);
    }
}

public class KeyboardTests
{
    private readonly WindowsKeyboard _keyboard = new();

    [Theory]
    [InlineData("KeyT")]
    [InlineData("Backquote")]
    [InlineData("Minus")]
    [InlineData("Space")]
    [InlineData("F5")]
    public void Common_keys_are_supported_and_labelled(string code)
    {
        Assert.True(_keyboard.IsSupported(code));
        Assert.False(string.IsNullOrWhiteSpace(_keyboard.KeyLabel(code)));
    }

    [Fact]
    public void F12_is_not_supported() => Assert.False(_keyboard.IsSupported("F12"));

    [Fact]
    public void Bare_letters_type_and_ctrl_shift_letters_do_not()
    {
        Assert.True(_keyboard.ProducesCharacter(KeyChord.Key("KeyT")));
        Assert.False(_keyboard.ProducesCharacter(KeyChord.CtrlShift("KeyZ")));
        Assert.False(_keyboard.ProducesCharacter(KeyChord.CtrlShift("Space")));
    }
}

public class SoundAndInstanceTests
{
    [Fact]
    public void Plays_and_replaces_sounds_without_blocking()
    {
        using var player = new WindowsSoundPlayer();
        var silent = SoundBank.Create(0);

        player.Play(silent[FeedbackKind.MainSaved]);
        player.Play(silent[FeedbackKind.SubSaved]);
    }

    [Fact]
    public async Task Second_instance_signals_the_first()
    {
        using var activated = new ManualResetEventSlim();
        using var first = SingleInstance.Acquire(activated.Set);

        // A mutex is re-entrant on its owning thread, so a second "process" must be simulated from another thread.
        using var second = await Task.Run(() => SingleInstance.Acquire(() => { }), TestContext.Current.CancellationToken);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", TestCategories.Disruptive)]
    public void Clipboard_accepts_a_capture()
    {
        // Replaces the clipboard briefly; the snapshot puts the user's clipboard back afterwards, even if the test fails.
        using var restore = ClipboardSnapshot.Take();
        using var clipboard = new WindowsClipboard();
        using var image = new WindowsScreenCapture(ScreenshotHelper.Core.Diagnostics.AppLog.Null).Capture(CaptureTarget.PrimaryMonitor);

        clipboard.SetImage(image);
    }

}

/// <summary>
/// Tests the revealer's decision only: actually launching Explorer from a test leaves real windows open on the developer's desktop
/// (and a missing folder made explorer.exe open Documents — the stray windows this replaced test used to cause).
/// </summary>
public class RevealDecisionTests
{
    [Fact]
    public void Missing_folder_opens_nothing() =>
        Assert.Null(WindowsFileRevealer.FilesToReveal(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), []));

    [Fact]
    public void Only_existing_files_inside_the_folder_are_selected()
    {
        var folder = Directory.CreateTempSubdirectory("sshelper-reveal-");
        var other = Directory.CreateTempSubdirectory("sshelper-reveal-other-");
        try
        {
            var inside = Path.Combine(folder.FullName, "1.png");
            var outside = Path.Combine(other.FullName, "2.png");
            File.WriteAllText(inside, "x");
            File.WriteAllText(outside, "x");

            var files = WindowsFileRevealer.FilesToReveal(folder.FullName, [inside, outside, Path.Combine(folder.FullName, "gone.png")]);

            Assert.Equal([inside], files);
        }
        finally
        {
            folder.Delete(recursive: true);
            other.Delete(recursive: true);
        }
    }

    [Fact]
    public void Existing_folder_with_no_files_opens_the_folder() =>
        Assert.Empty(WindowsFileRevealer.FilesToReveal(Path.GetTempPath(), [])!);
}

public class WindowCaptureTests
{
    [Fact]
    [Trait("Category", TestCategories.Disruptive)]
    public void Active_window_capture_has_an_alpha_channel_or_falls_back_cleanly()
    {
        // Whatever is in the foreground on the test machine (a console, an IDE, a CI desktop) — the capture must succeed either way.
        using var capture = new WindowsScreenCapture(ScreenshotHelper.Core.Diagnostics.AppLog.Null);
        using var image = (GdiCapturedImage)capture.Capture(CaptureTarget.ActiveWindow);

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Contains(image.Bitmap.PixelFormat, new[] { System.Drawing.Imaging.PixelFormat.Format32bppArgb, System.Drawing.Imaging.PixelFormat.Format32bppRgb });
    }

    [Fact]
    public void Cloaking_round_trips_on_a_real_window()
    {
        // The backdrop window is a real top-level window that stays hidden here, so nothing appears on screen.
        using var window = new Interop.BackdropWindow();

        Assert.True(WindowsDesktop.SetCloaked(window.Handle, true));
        Assert.True(WindowsDesktop.IsCloaked(window.Handle));
        Assert.True(WindowsDesktop.SetCloaked(window.Handle, false));
        Assert.False(WindowsDesktop.IsCloaked(window.Handle));
        Assert.False(WindowsDesktop.SetCloaked(IntPtr.Zero, true));
    }

    [Fact]
    public void Passive_overlay_is_click_through_never_activated_and_excluded_from_capture()
    {
        using var window = new Interop.BackdropWindow();
        Assert.False(WindowsDesktop.IsExcludedFromCapture(window.Handle));

        WindowsDesktop.MakePassiveOverlay(window.Handle);

        var style = Interop.NativeMethods.GetWindowLongPtr(window.Handle, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
        const long expected = Interop.NativeMethods.WS_EX_LAYERED | Interop.NativeMethods.WS_EX_TRANSPARENT
                              | Interop.NativeMethods.WS_EX_NOACTIVATE | Interop.NativeMethods.WS_EX_TOOLWINDOW;
        Assert.Equal(expected, style & expected);
        Assert.True(WindowsDesktop.IsExcludedFromCapture(window.Handle));
    }

    [Fact]
    [Trait("Category", TestCategories.Disruptive)]
    public void Backdrop_window_shows_and_hides_without_taking_focus()
    {
        using var backdrop = new Interop.BackdropWindow();
        var before = WindowsDesktop.GetForeground();

        backdrop.ShowBehind(before, new System.Drawing.Rectangle(0, 0, 50, 50), white: true);
        backdrop.Hide();

        Assert.Equal(before, WindowsDesktop.GetForeground());
    }
}
