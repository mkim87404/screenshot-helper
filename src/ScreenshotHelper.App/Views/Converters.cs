using Avalonia.Data.Converters;
using ScreenshotHelper.Core.Renumber;

namespace ScreenshotHelper.App.Views;

/// <summary>Maps a plan row status to the warning/error text styles.</summary>
public static class PlanStatusConverter
{
    public static FuncValueConverter<PlanRowStatus, bool> IsWarning { get; } = new(s => s == PlanRowStatus.Warning);

    public static FuncValueConverter<PlanRowStatus, bool> IsConflict { get; } = new(s => s == PlanRowStatus.Conflict);
}
