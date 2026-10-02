using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenshotHelper.App.ViewModels;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.App.Views;

/// <summary>Sub-number collision prompt. Closing without choosing (Esc or ×) discards the shot, never touching existing files.</summary>
public partial class CollisionPromptWindow : Window
{
    public CollisionPromptWindow()
    {
        InitializeComponent();
        // Focused as if by keyboard, so the highlight is visible from the start and arrow keys move it.
        Opened += (_, _) => AppendButton.Focus(NavigationMethod.Directional);
    }

    public SubCollisionAnswer Answer { get; private set; } = new(SubCollisionChoice.Discard, false);

    private void OnAppend(object? sender, RoutedEventArgs e) => Choose(SubCollisionChoice.Append);

    private void OnInsert(object? sender, RoutedEventArgs e) => Choose(SubCollisionChoice.Insert);

    private void OnOverwrite(object? sender, RoutedEventArgs e) => Choose(SubCollisionChoice.Overwrite);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Choose(SubCollisionChoice.Discard);

    private void Choose(SubCollisionChoice choice)
    {
        Answer = new SubCollisionAnswer(choice, (DataContext as CollisionPromptViewModel)?.RememberForSession ?? false);
        Close();
    }
}
