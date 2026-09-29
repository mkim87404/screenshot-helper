using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScreenshotHelper.Core.Naming;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.App.ViewModels;

/// <summary>
/// Caption box state (spec §6). Length and invalid characters are enforced by the view as input arrives (TextBox.MaxLength plus an
/// input filter), so the text can never exceed the budget; this model only reports what's left and the final, trimmed caption.
/// </summary>
public sealed partial class CaptionViewModel : ObservableObject
{
    public CaptionViewModel(CaptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Budget = Math.Max(0, request.Budget);
        Title = $"Caption for {request.Target}";
        var initial = request.Initial ?? string.Empty;
        Text = initial.Length > Budget ? initial[..Budget] : initial;
    }

    public int Budget { get; }

    public string Title { get; }

    /// <summary>False when the folder path leaves no room at all (a MaxLength of 0 would mean "unlimited", so the box is disabled instead).</summary>
    public bool CanType => Budget > 0;

    public string InvalidCharactersHint => $"Not allowed in filenames: {CaptionRules.InvalidCharactersDisplay}";

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool RemovedInvalid { get; set; }

    /// <summary>Characters still accepted by the box (the same count MaxLength enforces).</summary>
    public int Remaining => Math.Max(0, Budget - (Text?.Length ?? 0));

    public string RemainingText => Budget == 0
        ? "This folder's path is too long for a caption."
        : string.Create(CultureInfo.InvariantCulture, $"{Remaining} of {Budget} characters left");

    /// <summary>The caption to save when confirmed: trimmed, or empty to remove the caption.</summary>
    public string Result => CaptionRules.Normalize(Text) ?? string.Empty;

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
    }
}

/// <summary>Sub-number collision prompt (spec §5).</summary>
public sealed partial class CollisionPromptViewModel : ObservableObject
{
    public CollisionPromptViewModel(SubCollision collision)
    {
        ArgumentNullException.ThrowIfNull(collision);
        Collision = collision;
    }

    public SubCollision Collision { get; }

    public string Title => $"{Collision.Main}-{Collision.Sub} already exists";

    public string Detail => Collision.ExistingFileNames.Count == 1
        ? $"“{Collision.ExistingFileNames[0]}” already uses this number. The screenshot has been captured — choose where to save it."
        : $"{Collision.ExistingFileNames.Count} files already use this number. The screenshot has been captured — choose where to save it.";

    public string AppendText => $"Append as {Collision.Main}-{Collision.AppendSub}";

    public string InsertText => $"Insert as {Collision.Main}-{Collision.Sub} (later shots move up by one)";

    public string OverwriteText => "Overwrite (old file goes to the Recycle Bin)";

    public string DiscardText => "Discard this screenshot";

    [ObservableProperty]
    public partial bool RememberForSession { get; set; }
}
