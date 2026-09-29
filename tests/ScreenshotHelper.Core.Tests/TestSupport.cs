using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Renumber;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.Core.Tests;

/// <summary>A real, uniquely named temp directory deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sshelper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Creates files whose content is their own name, so tests can prove content followed a rename.</summary>
    public TempFolder With(params string[] names)
    {
        foreach (var name in names)
        {
            System.IO.File.WriteAllText(File(name), name);
        }

        return this;
    }

    public string[] Names() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    public string Read(string name) => System.IO.File.ReadAllText(File(name));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Stands in for the screen: the real capture needs a desktop session, which Core tests (and Linux CI) don't have.</summary>
internal sealed class FakeCapture : IScreenCapture
{
    public int Captures { get; private set; }

    public bool Fail { get; set; }

    public CapturedImage Capture(CaptureTarget target)
    {
        if (Fail)
        {
            throw new InvalidOperationException("No screen.");
        }

        Captures++;
        return new FakeImage($"image-{Captures}");
    }
}

/// <summary>Writes a known marker instead of PNG bytes so tests can identify which capture ended up in which file.</summary>
internal sealed class FakeImage(string marker) : CapturedImage
{
    public override int Width => 1;
    public override int Height => 1;

    public override void WritePng(Stream destination)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(marker);
        destination.Write(bytes);
    }
}

/// <summary>Moves "recycled" files into a side folder (the OS recycle bin is a platform service).</summary>
internal sealed class FolderRecycleBin(string binFolder) : IRecycleBin
{
    public List<string> Recycled { get; } = [];

    public void Recycle(string path)
    {
        Directory.CreateDirectory(binFolder);
        File.Move(path, Path.Combine(binFolder, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path)));
        Recycled.Add(Path.GetFileName(path));
    }
}

/// <summary>Answers collision prompts from a queue and records what was asked (the real prompt is a UI window).</summary>
internal sealed class ScriptedPrompt : ICollisionPrompt
{
    private readonly Queue<SubCollisionAnswer> _answers = new();

    public List<SubCollision> Asked { get; } = [];

    public ScriptedPrompt Then(SubCollisionChoice choice, bool remember = false)
    {
        _answers.Enqueue(new SubCollisionAnswer(choice, remember));
        return this;
    }

    public Task<SubCollisionAnswer> AskAsync(SubCollision collision, CancellationToken cancellationToken)
    {
        Asked.Add(collision);
        return Task.FromResult(_answers.Dequeue());
    }
}

internal class RecordingFeedback : IFeedbackSink
{
    public List<FeedbackEvent> Events { get; } = [];

    public FeedbackEvent Last => Events[^1];

    public virtual void Notify(FeedbackEvent feedback) => Events.Add(feedback);
}

/// <summary>At the moment a "saved" event arrives, checks the numbered file really exists on disk (the sound is played at this point).</summary>
internal sealed class DiskCheckingFeedback(string folder) : RecordingFeedback
{
    public int SavedEvents { get; private set; }

    public List<string> SavedWithoutFile { get; } = [];

    public override void Notify(FeedbackEvent feedback)
    {
        base.Notify(feedback);
        if (feedback.Kind is not (FeedbackKind.MainSaved or FeedbackKind.SubSaved))
        {
            return;
        }

        SavedEvents++;
        var numbering = feedback.Message.TrimStart('✓', ' ').Split(' ')[0];
        var exists = Directory.Exists(folder) && Directory.GetFiles(folder, "*.png").Any(f =>
            Naming.ShotName.TryParse(Path.GetFileName(f))?.Numbering == numbering && new FileInfo(f).Length > 0);
        if (!exists)
        {
            SavedWithoutFile.Add(numbering);
        }
    }
}

/// <summary>A clock frozen at a known local time with a chosen UTC offset.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
}

/// <summary>A clock tests can move forward, to prove which moment a timestamp records.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("test", start.Offset, "test", "test");

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds a session engine wired to real temp folders and the fakes above.</summary>
internal sealed class EngineHarness : IDisposable
{
    public EngineHarness(TempFolder folder, SessionOptions? options = null, ScriptedPrompt? prompt = null, RecordingFeedback? feedback = null, TimeProvider? time = null)
    {
        Feedback = feedback ?? new RecordingFeedback();
        Folder = folder;
        State = new TempFolder();
        Bin = new FolderRecycleBin(System.IO.Path.Combine(State.Path, "bin"));
        Prompt = prompt ?? new ScriptedPrompt();
        Executor = new RenumberExecutor(System.IO.Path.Combine(State.Path, "journal"), AppLog.Null);
        Engine = new SessionEngine(
            options ?? new SessionOptions(folder.Path, StartMode.NewGroup, 1, 1, CaptureTarget.PrimaryMonitor),
            Capture,
            Bin,
            null,
            Prompt,
            Feedback,
            Executor,
            time ?? new FixedTime(new DateTimeOffset(2026, 9, 27, 10, 5, 33, TimeSpan.FromHours(13))),
            AppLog.Null);
    }

    public TempFolder Folder { get; }
    public TempFolder State { get; }
    public FakeCapture Capture { get; } = new();
    public FolderRecycleBin Bin { get; }
    public ScriptedPrompt Prompt { get; }
    public RecordingFeedback Feedback { get; }
    public RenumberExecutor Executor { get; }
    public SessionEngine Engine { get; }

    public Task Main() => Engine.TakeShotAsync(ShotKind.Main, CancellationToken.None);

    public Task Sub() => Engine.TakeShotAsync(ShotKind.Sub, CancellationToken.None);

    public void Dispose() => State.Dispose();
}
