using ScreenshotHelper.Core.Platform;

namespace ScreenshotHelper.Core.Session;

/// <summary>Where the session is, as far as hotkey registration is concerned.</summary>
public enum SessionPhase
{
    /// <summary>No session: nothing registered, so the keyboard behaves normally.</summary>
    Idle,

    /// <summary>Capturing: every action registered.</summary>
    Active,

    /// <summary>Paused: only pause/resume stays registered so the user can type everything else.</summary>
    Paused,

    /// <summary>A caption box or collision prompt has focus: nothing registered so typing reaches it.</summary>
    Modal,
}

/// <summary>Decides which hotkeys must be registered in each session phase.</summary>
public static class HotkeyPlan
{
    public static IReadOnlyDictionary<HotkeyAction, KeyChord> For(SessionPhase phase, IReadOnlyDictionary<HotkeyAction, KeyChord> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return phase switch
        {
            SessionPhase.Active => bindings,
            SessionPhase.Paused => bindings
                .Where(b => b.Key == HotkeyAction.PauseResume)
                .ToDictionary(b => b.Key, b => b.Value),
            _ => new Dictionary<HotkeyAction, KeyChord>(),
        };
    }

    /// <summary>Actions bound to the same chord; a valid configuration has none.</summary>
    public static IReadOnlyList<IReadOnlyList<HotkeyAction>> Duplicates(IReadOnlyDictionary<HotkeyAction, KeyChord> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return bindings
            .GroupBy(b => b.Value)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<HotkeyAction>)g.Select(b => b.Key).OrderBy(a => a).ToList())
            .ToList();
    }
}
