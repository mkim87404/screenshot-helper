using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.Core.Naming;

namespace ScreenshotHelper.App.Views;

/// <summary>
/// Caption box: Enter confirms, Esc cancels. <see cref="Result"/> is the trimmed caption (empty = remove it), or null if cancelled.
/// Invalid file-name characters are dropped as they arrive, so they never appear in the box.
/// </summary>
public partial class CaptionWindow : Window
{
    public CaptionWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            CaptionBox.Focus();
            CaptionBox.CaretIndex = CaptionBox.Text?.Length ?? 0;
        };

        // Tunnel: filter typed text before the TextBox inserts it.
        CaptionBox.AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);

        // Paste doesn't raise TextInput, so clean up after any change as a backstop (MaxLength already caps paste length).
        CaptionBox.TextChanged += OnTextChanged;
    }

    public string? Result { get; private set; }

    private CaptionViewModel? ViewModel => DataContext as CaptionViewModel;

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is null)
        {
            return;
        }

        var cleaned = CaptionRules.RemoveInvalid(e.Text);
        if (cleaned.Length == e.Text.Length)
        {
            return;
        }

        ViewModel?.RemovedInvalid = true;
        if (cleaned.Length == 0)
        {
            e.Handled = true;
        }
        else
        {
            e.Text = cleaned;
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = CaptionBox.Text ?? string.Empty;
        var cleaned = CaptionRules.RemoveInvalid(text);
        if (cleaned.Length == text.Length)
        {
            return;
        }

        var caret = Math.Max(0, CaptionBox.CaretIndex - (text.Length - cleaned.Length));
        ViewModel?.RemovedInvalid = true;
        CaptionBox.Text = cleaned;
        CaptionBox.CaretIndex = Math.Min(caret, cleaned.Length);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Result = ViewModel?.Result ?? string.Empty;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
