using ScreenshotHelper.Core.Naming;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.Core.Session;

/// <summary>How the first keypress of a session is numbered (spec §2).</summary>
public enum StartMode
{
    /// <summary>Start a new group at <see cref="SessionOptions.Main"/>: main key saves M, sub key saves M-1.</summary>
    NewGroup,

    /// <summary>Continue group <see cref="SessionOptions.Main"/> at sub <see cref="SessionOptions.Sub"/>; the main key opens the next free group.</summary>
    ContinueGroup,
}

/// <summary>Which screenshot the timestamp and caption keys act on (one setting for both, so they always behave alike).</summary>
public enum AnnotationTarget
{
    /// <summary>Capture first, annotate after: the keys add/remove a timestamp or caption on the shot just taken (renaming it).</summary>
    LastShot,

    /// <summary>Arm before capturing: the keys apply to the next shot taken.</summary>
    NextShot,
}

/// <summary>What the caption box should show: how much fits, any existing caption to edit, and which shot it's for.</summary>
public sealed record CaptionRequest(int Budget, string? Initial, string Target);

/// <summary>Which hotkey took the shot.</summary>
public enum ShotKind
{
    Main,
    Sub,
}

/// <summary>Everything a session needs to know up front.</summary>
public sealed record SessionOptions(
    string Folder,
    StartMode Mode,
    int Main,
    int Sub,
    CaptureTarget CaptureTarget,
    bool AlwaysTimestamp = false,
    TimestampZone TimestampZone = TimestampZone.Local,
    bool AlwaysNumberFirstShotAsMember = false,
    bool CopyToClipboard = false,
    SubCollisionPolicy SubCollisionPolicy = SubCollisionPolicy.Ask,
    AnnotationTarget AnnotationTarget = AnnotationTarget.LastShot);

/// <summary>User's answer to a sub-number collision (spec §5).</summary>
public enum SubCollisionChoice
{
    /// <summary>Save as the next sub after the group's highest.</summary>
    Append,

    /// <summary>Shift the taken sub and everything after it up by one, then save in the freed slot.</summary>
    Insert,

    /// <summary>Send the existing file(s) with this number to the recycle bin, then save.</summary>
    Overwrite,

    /// <summary>Drop the captured image.</summary>
    Discard,
}

/// <summary>Details shown in the collision prompt.</summary>
public sealed record SubCollision(string Folder, int Main, int Sub, IReadOnlyList<string> ExistingFileNames, int AppendSub);

/// <summary>The prompt's answer; <paramref name="RememberForSession"/> applies the choice to later collisions in this session.</summary>
public sealed record SubCollisionAnswer(SubCollisionChoice Choice, bool RememberForSession);

/// <summary>Asks the user how to resolve a sub collision (the image is already captured, so the prompt never appears in it).</summary>
public interface ICollisionPrompt
{
    Task<SubCollisionAnswer> AskAsync(SubCollision collision, CancellationToken cancellationToken);
}

/// <summary>Immutable snapshot of session state for the UI (tray tooltip, toasts).</summary>
public sealed record SessionStatus(
    string Folder,
    int CurrentMain,
    bool GroupOpened,
    int NextSub,
    bool TimestampArmed,
    string? Caption,
    int ShotCount,
    CaptureTarget CaptureTarget)
{
    /// <summary>What the main key would save next, e.g. "7".</summary>
    public string NextMainText => GroupOpened ? $"{CurrentMain + 1}" : $"{CurrentMain}";

    /// <summary>What the sub key would save next, e.g. "6-3".</summary>
    public string NextSubText => $"{CurrentMain}-{(GroupOpened ? Math.Max(NextSub, 1) : 1)}";
}
