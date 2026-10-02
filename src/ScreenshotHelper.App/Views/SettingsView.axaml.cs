using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.Core.Platform;
using CoreModifiers = ScreenshotHelper.Core.Platform.KeyModifiers;

namespace ScreenshotHelper.App.Views;

/// <summary>View for <see cref="SettingsViewModel"/>; routes key presses to the row being rebound.</summary>
public partial class SettingsView : UserControl
{
    private TopLevel? _topLevel;

    public SettingsView()
    {
        InitializeComponent();
    }

    // The handler sits on the window (not this view) so a key is captured wherever focus is, and tunnels so the focused button
    // doesn't treat Space/Enter as a click. It's removed when the view leaves the window, so a closed page never keeps listening.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsCapturing: true } vm)
        {
            return;
        }

        e.Handled = true;
        if (ToChord(e.PhysicalKey, e.KeyModifiers) is { } chord)
        {
            vm.TryCapture(chord);
        }
    }

    /// <summary>Converts an Avalonia key event into a chord; null for lone modifier keys (still waiting for the real key).</summary>
    internal static KeyChord? ToChord(PhysicalKey key, Avalonia.Input.KeyModifiers modifiers)
    {
        if (key is PhysicalKey.None or PhysicalKey.ControlLeft or PhysicalKey.ControlRight or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight
            or PhysicalKey.AltLeft or PhysicalKey.AltRight or PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            return null;
        }

        var mods = CoreModifiers.None;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            mods |= CoreModifiers.Ctrl;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            mods |= CoreModifiers.Alt;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            mods |= CoreModifiers.Shift;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            mods |= CoreModifiers.Win;
        }

        // Avalonia names letter keys "A".."Z"; settings store W3C codes ("KeyA") like every other key ("Digit1", "Backquote").
        var code = key.ToString();
        if (code.Length == 1 && char.IsAsciiLetterUpper(code[0]))
        {
            code = "Key" + code;
        }

        return new KeyChord(mods, code);
    }
}
