using Avalonia;
using Avalonia.Headless;
using ScreenshotHelper.App.Services;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(ScreenshotHelper.App.Tests.TestAppBuilder))]

namespace ScreenshotHelper.App.Tests;

/// <summary>Runs the real <see cref="App"/> (styles, themes, views) on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>A real temp folder, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sshelper-apptests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public TempDir With(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, name), name);
        }

        return this;
    }

    public string[] Names() => [.. Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Records registrations and lets tests "press" keys; real system-wide hotkeys would steal the developer's keyboard.</summary>
internal sealed class FakeHotkeys : IHotkeyService
{
    public HashSet<KeyChord> InUseElsewhere { get; } = [];

    public IReadOnlyDictionary<HotkeyAction, KeyChord> Registered { get; private set; } = new Dictionary<HotkeyAction, KeyChord>();

    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyDictionary<HotkeyAction, HotkeyFailure> Apply(IReadOnlyDictionary<HotkeyAction, KeyChord> bindings)
    {
        var failures = bindings.Where(b => InUseElsewhere.Contains(b.Value)).ToDictionary(b => b.Key, _ => HotkeyFailure.InUse);
        Registered = bindings.Where(b => !failures.ContainsKey(b.Key)).ToDictionary(b => b.Key, b => b.Value);
        return failures;
    }

    /// <summary>Simulates a key press; like the OS, only registered chords are delivered.</summary>
    public void Press(HotkeyAction action)
    {
        if (Registered.ContainsKey(action))
        {
            Pressed?.Invoke(action);
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeKeyboard : IKeyboardLayout
{
    public string KeyLabel(string code) => code switch
    {
        "Backquote" => "`",
        "Minus" => "-",
        _ when code.StartsWith("Key", StringComparison.Ordinal) => code[3..],
        _ => code,
    };

    public bool IsSupported(string code) => code != "F12";

    public bool ProducesCharacter(KeyChord chord) => chord.Modifiers.HasFlag(KeyModifiers.Alt) && chord.Modifiers.HasFlag(KeyModifiers.Ctrl);
}

internal sealed class FakeCapture : IScreenCapture
{
    public CapturedImage Capture(CaptureTarget target) => new FakeImage();

    private sealed class FakeImage : CapturedImage
    {
        public override int Width => 1;
        public override int Height => 1;

        public override void WritePng(Stream destination) => destination.WriteByte(0x89);
    }
}

internal sealed class FakeRecycleBin : IRecycleBin
{
    public void Recycle(string path) => File.Delete(path);
}

internal sealed class FakeRevealer : IFileRevealer
{
    public List<(string Folder, IReadOnlyList<string> Files)> Reveals { get; } = [];

    public void Reveal(string folder, IReadOnlyList<string> files) => Reveals.Add((folder, files));
}

internal sealed class SilentSound : ISoundPlayer
{
    public int Played { get; private set; }

    public void Play(byte[] wav) => Played++;
}

internal sealed class NoClipboard : IClipboardImage
{
    public void SetImage(CapturedImage image)
    {
    }
}

/// <summary>Scripted session UI: answers prompts, records what the coordinator asked it to show.</summary>
internal sealed class FakeSessionUi : ISessionUi
{
    public Func<CaptionRequest, string?> CaptionAnswer { get; set; } = _ => null;

    public SubCollisionAnswer CollisionAnswer { get; set; } = new(SubCollisionChoice.Append, false);

    public List<FeedbackEvent> Toasts { get; } = [];

    public SessionSummary? Summary { get; private set; }

    public bool Started { get; private set; }

    /// <summary>What was registered while the caption box was "open" (should be nothing).</summary>
    public IReadOnlyDictionary<HotkeyAction, KeyChord>? RegisteredDuringCaption { get; private set; }

    public FakeHotkeys? Hotkeys { get; set; }

    public Task<string?> AskCaptionAsync(CaptionRequest request)
    {
        RegisteredDuringCaption = Hotkeys?.Registered;
        return Task.FromResult(CaptionAnswer(request));
    }

    public Task<SubCollisionAnswer> AskCollisionAsync(SubCollision collision) =>
        PendingCollision?.Task ?? Task.FromResult(CollisionAnswer);

    public void ShowToast(FeedbackEvent feedback) => Toasts.Add(feedback);

    public void SessionStarted() => Started = true;

    public void SessionEnded(SessionSummary summary) => Summary = summary;

    public void StateChanged()
    {
    }

    /// <summary>Set when a collision prompt should stay open until <see cref="CloseOpenPrompts"/> (simulates a user who never answers).</summary>
    public TaskCompletionSource<SubCollisionAnswer>? PendingCollision { get; set; }

    public int PromptsClosed { get; private set; }

    public void CloseOpenPrompts()
    {
        PromptsClosed++;
        PendingCollision?.TrySetResult(new SubCollisionAnswer(SubCollisionChoice.Discard, false));
    }
}

/// <summary>App services over temp folders and fakes for OS-level pieces.</summary>
internal sealed class ServicesHarness : IDisposable
{
    public ServicesHarness()
    {
        Services = new AppServices(
            new AppPaths(Data.Path, System.IO.Path.Combine(Data.Path, "logs"), System.IO.Path.Combine(Data.Path, "journal")),
            AppLog.Null,
            new PlatformServices(Hotkeys, new FakeKeyboard(), new FakeCapture(), new FakeRecycleBin(), Revealer, Sound, new NoClipboard()));
    }

    public TempDir Data { get; } = new();

    public FakeHotkeys Hotkeys { get; } = new();

    public FakeRevealer Revealer { get; } = new();

    public SilentSound Sound { get; } = new();

    public AppServices Services { get; }

    public void Dispose() => Data.Dispose();
}
