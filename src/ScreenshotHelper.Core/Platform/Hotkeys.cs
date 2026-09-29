using System.Text;

namespace ScreenshotHelper.Core.Platform;

/// <summary>Modifier keys of a hotkey chord.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A hotkey: modifiers plus a physical key identified by its W3C UI Events <c>code</c> name (e.g. <c>Backquote</c>, <c>KeyT</c>, <c>Space</c>).
/// Physical codes are layout-independent, so a binding keeps meaning the same key position on any keyboard layout.
/// </summary>
public sealed record KeyChord(KeyModifiers Modifiers, string Code)
{
    public static KeyChord Key(string code) => new(KeyModifiers.None, code);

    public static KeyChord CtrlShift(string code) => new(KeyModifiers.Ctrl | KeyModifiers.Shift, code);

    /// <summary>Human-readable form using <paramref name="keyLabel"/> for the key itself (layout-aware on the platform).</summary>
    public string Describe(Func<string, string> keyLabel)
    {
        ArgumentNullException.ThrowIfNull(keyLabel);
        var builder = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            builder.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            builder.Append("Shift+");
        }

        if (Modifiers.HasFlag(KeyModifiers.Win))
        {
            builder.Append("Win+");
        }

        return builder.Append(keyLabel(Code)).ToString();
    }

    public override string ToString() => Describe(code => code);
}

/// <summary>Everything a hotkey can trigger during a session.</summary>
public enum HotkeyAction
{
    MainShot,
    SubShot,
    ToggleTimestamp,
    Caption,
    PauseResume,
    Undo,
    EndSession,
}

/// <summary>Why a hotkey could not be registered.</summary>
public enum HotkeyFailure
{
    /// <summary>Another application (or the OS) already owns this combination.</summary>
    InUse,

    /// <summary>The key can't be registered on this platform (unknown code, or reserved like F12).</summary>
    Unsupported,
}

/// <summary>System-wide, exclusive hotkeys.</summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Raised on the hotkey thread; handlers must only enqueue work and return immediately.</summary>
    event Action<HotkeyAction>? Pressed;

    /// <summary>
    /// Makes exactly <paramref name="bindings"/> registered (unregistering everything else). Auto-repeat never fires twice.
    /// Returns the bindings that failed.
    /// </summary>
    IReadOnlyDictionary<HotkeyAction, HotkeyFailure> Apply(IReadOnlyDictionary<HotkeyAction, KeyChord> bindings);
}

/// <summary>Keyboard-layout questions for the settings UI.</summary>
public interface IKeyboardLayout
{
    /// <summary>The label printed on the key for <paramref name="code"/> under the active layout (e.g. <c>`</c>, <c>T</c>, <c>Space</c>).</summary>
    string KeyLabel(string code);

    /// <summary>Whether the platform can register this key at all.</summary>
    bool IsSupported(string code);

    /// <summary>Whether pressing the chord would type a character in the active layout (e.g. Ctrl+Alt+E = € on many layouts).</summary>
    bool ProducesCharacter(KeyChord chord);
}
