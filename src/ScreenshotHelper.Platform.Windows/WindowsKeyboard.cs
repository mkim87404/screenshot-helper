using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Platform.Windows.Interop;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// Maps W3C physical key codes to Windows virtual keys. Typing keys go through their scan code and the active layout
/// (so "Backquote" is whatever key sits left of 1, and its VK follows the layout); navigation/function keys map directly.
/// </summary>
public sealed class WindowsKeyboard : IKeyboardLayout
{
    private const uint VK_F12 = 0x7B;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;

    // Set-1 scan codes for keys whose virtual key depends on the keyboard layout.
    private static readonly Dictionary<string, uint> ScanCodes = BuildScanCodes();

    // Layout-independent keys.
    private static readonly Dictionary<string, uint> DirectKeys = BuildDirectKeys();

    public bool IsSupported(string code) => TryGetVirtualKey(code, out var vk) && vk != VK_F12;

    public string KeyLabel(string code)
    {
        if (code == "Space")
        {
            return "Space";
        }

        if (!TryGetVirtualKey(code, out var vk))
        {
            return code;
        }

        var layout = GetKeyboardLayout(0);
        var scan = ScanCodes.TryGetValue(code, out var s) ? s : MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC_EX, layout);
        var lParam = (int)((scan & 0xFF) << 16);
        if ((scan & 0xFF00) == 0xE000)
        {
            lParam |= 1 << 24;
        }

        string label;
        unsafe
        {
            var buffer = stackalloc char[64];
            var length = GetKeyNameText(lParam, buffer, 64);
            label = length > 0 ? new string(buffer, 0, length) : code;
        }

        // A lone punctuation glyph (` or -) is easy to miss in the UI, so name it too.
        return label.Length == 1 && !char.IsLetterOrDigit(label[0]) ? $"{label} ({PunctuationName(code)})" : label;
    }

    private static string PunctuationName(string code) => code switch
    {
        "Backquote" => "backtick",
        "Minus" => "minus",
        "Equal" => "equals",
        "BracketLeft" => "left bracket",
        "BracketRight" => "right bracket",
        "Backslash" or "IntlBackslash" => "backslash",
        "Semicolon" => "semicolon",
        "Quote" => "quote",
        "Comma" => "comma",
        "Period" => "period",
        "Slash" => "slash",
        _ => code,
    };

    public bool ProducesCharacter(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (!TryGetVirtualKey(chord.Code, out var vk))
        {
            return false;
        }

        var layout = GetKeyboardLayout(0);
        var scan = MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC_EX, layout);
        unsafe
        {
            var state = stackalloc byte[256];
            new Span<byte>(state, 256).Clear();
            if (chord.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                state[VK_SHIFT] = 0x80;
            }

            if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl))
            {
                state[VK_CONTROL] = 0x80;
            }

            if (chord.Modifiers.HasFlag(KeyModifiers.Alt))
            {
                state[VK_MENU] = 0x80;
            }

            if (chord.Modifiers.HasFlag(KeyModifiers.Win))
            {
                state[VK_LWIN] = 0x80;
            }

            var buffer = stackalloc char[8];

            // Flag 0x4: don't change the keyboard's dead-key state (Windows 10 1607+).
            var result = ToUnicodeEx(vk, scan, state, buffer, 8, 0x4, layout);

            // Control characters (Ctrl+T → 0x14) aren't "typing"; a dead key (result < 0) is.
            return result < 0 || (result > 0 && buffer[0] >= 0x20 && buffer[0] != 0x7F);
        }
    }

    /// <summary>Resolves a physical key code to the virtual key under the current thread's layout.</summary>
    internal static bool TryGetVirtualKey(string code, out uint vk)
    {
        if (DirectKeys.TryGetValue(code, out vk))
        {
            return true;
        }

        if (ScanCodes.TryGetValue(code, out var scan))
        {
            vk = MapVirtualKeyEx(scan, MAPVK_VSC_TO_VK_EX, GetKeyboardLayout(0));
            return vk != 0;
        }

        vk = 0;
        return false;
    }

    internal static uint ToNativeModifiers(KeyModifiers modifiers)
    {
        var native = MOD_NOREPEAT;
        if (modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            native |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            native |= MOD_ALT;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            native |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(KeyModifiers.Win))
        {
            native |= MOD_WIN;
        }

        return native;
    }

    private static Dictionary<string, uint> BuildScanCodes()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["Backquote"] = 0x29, ["Minus"] = 0x0C, ["Equal"] = 0x0D, ["BracketLeft"] = 0x1A, ["BracketRight"] = 0x1B,
            ["Backslash"] = 0x2B, ["Semicolon"] = 0x27, ["Quote"] = 0x28, ["Comma"] = 0x33, ["Period"] = 0x34,
            ["Slash"] = 0x35, ["IntlBackslash"] = 0x56,
        };
        uint[] letters = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        for (var i = 0; i < 26; i++)
        {
            map[$"Key{(char)('A' + i)}"] = letters[i];
        }

        for (var d = 1; d <= 9; d++)
        {
            map[$"Digit{d}"] = (uint)(0x01 + d);
        }

        map["Digit0"] = 0x0B;
        return map;
    }

    private static Dictionary<string, uint> BuildDirectKeys()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["Space"] = 0x20, ["Escape"] = 0x1B, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Backspace"] = 0x08,
            ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
            ["Pause"] = 0x13, ["PrintScreen"] = 0x2C, ["ScrollLock"] = 0x91,
            ["NumPadMultiply"] = 0x6A, ["NumPadAdd"] = 0x6B, ["NumPadSubtract"] = 0x6D, ["NumPadDecimal"] = 0x6E, ["NumPadDivide"] = 0x6F,
        };
        for (var f = 1; f <= 24; f++)
        {
            map[$"F{f}"] = (uint)(0x6F + f);
        }

        for (var n = 0; n <= 9; n++)
        {
            map[$"NumPad{n}"] = (uint)(0x60 + n);
        }

        return map;
    }
}
