namespace ScreenshotHelper.Core.Feedback;

/// <summary>Every user-facing event that gets a sound and a toast (spec §7).</summary>
public enum FeedbackKind
{
    SessionStarted,
    MainSaved,
    SubSaved,
    TimestampArmed,
    TimestampDisarmed,
    CaptionArmed,
    CaptionCancelled,
    Paused,
    Resumed,
    Undo,
    Warning,
    Error,
    SessionEnded,
}

/// <summary>
/// One feedback notification: what happened plus the short text shown in the toast.
/// <paramref name="HasCaveat"/> marks an action that succeeded with something worth noticing (e.g. "saved as 5 because 4 was taken"):
/// it keeps the success sound — so the MainSaved/SubSaved sounds always and only mean "the file is on disk" — and only tints the toast.
/// </summary>
public sealed record FeedbackEvent(FeedbackKind Kind, string Message, bool HasCaveat = false);

/// <summary>Receives feedback from the session engine. Implementations must not block (they run on the session actor).</summary>
public interface IFeedbackSink
{
    void Notify(FeedbackEvent feedback);
}
