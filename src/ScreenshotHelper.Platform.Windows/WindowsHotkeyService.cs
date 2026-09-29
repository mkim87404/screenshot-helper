using System.Runtime.InteropServices;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Platform.Windows.Interop;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows;

/// <summary>
/// Exclusive system-wide hotkeys via RegisterHotKey. Unlike a low-level keyboard hook, the OS delivers only our own chords as messages,
/// so there's no hook timeout that can silently drop us and no visibility of other keystrokes. MOD_NOREPEAT is always set.
/// </summary>
public sealed class WindowsHotkeyService : IHotkeyService
{
    private readonly MessageThread _thread = new("ScreenshotHelper.Hotkeys");
    private readonly Dictionary<int, HotkeyAction> _registered = [];

    public WindowsHotkeyService()
    {
        _thread.ThreadMessage += OnThreadMessage;
    }

    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyDictionary<HotkeyAction, HotkeyFailure> Apply(IReadOnlyDictionary<HotkeyAction, KeyChord> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        // RegisterHotKey(NULL, ...) ties the hotkey to the calling thread, so every (un)registration runs on the message thread.
        return _thread.Invoke(() =>
        {
            UnregisterAll();
            var failures = new Dictionary<HotkeyAction, HotkeyFailure>();
            foreach (var (action, chord) in bindings)
            {
                var id = (int)action + 1;
                if (!WindowsKeyboard.TryGetVirtualKey(chord.Code, out var vk) || vk == 0x7B)
                {
                    failures[action] = HotkeyFailure.Unsupported;
                    continue;
                }

                if (RegisterHotKey(IntPtr.Zero, id, WindowsKeyboard.ToNativeModifiers(chord.Modifiers), vk))
                {
                    _registered[id] = action;
                }
                else
                {
                    failures[action] = Marshal.GetLastPInvokeError() == ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyFailure.InUse : HotkeyFailure.Unsupported;
                }
            }

            return (IReadOnlyDictionary<HotkeyAction, HotkeyFailure>)failures;
        });
    }

    public void Dispose()
    {
        try
        {
            _thread.Invoke(() =>
            {
                UnregisterAll();
                return 0;
            });
        }
        catch (ObjectDisposedException)
        {
        }

        _thread.ThreadMessage -= OnThreadMessage;
        _thread.Dispose();
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }

        _registered.Clear();
    }

    private void OnThreadMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_HOTKEY && _registered.TryGetValue(unchecked((int)wParam.ToInt64()), out var action))
        {
            Pressed?.Invoke(action);
        }
    }
}
