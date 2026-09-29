using ScreenshotHelper.Core.Folder;

namespace ScreenshotHelper.Core.Session;

/// <summary>Defaults the Start screen pre-fills from a folder scan.</summary>
public sealed record StartDefaults(int NewGroupMain, int ContinueMain, int ContinueSub, IndexedShot? HighestShot);

/// <summary>Result of checking the numbers typed on the Start screen. <see cref="IsBlocking"/> is true only for invalid input.</summary>
public sealed record StartCheck(string? Message, bool IsWarning, bool IsBlocking, int? SuggestedMain, int? SuggestedSub)
{
    public static StartCheck Ok { get; } = new(null, false, false, null, null);
}

/// <summary>Computes Start-screen defaults and live collision hints (spec §2).</summary>
public static class StartAdvisor
{
    /// <summary>The numbers the Start screen pre-fills for each mode, derived from the highest shot in the folder.</summary>
    public static StartDefaults Defaults(FolderIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var highest = index.HighestShot;
        if (highest is null)
        {
            return new StartDefaults(1, 1, 1, null);
        }

        var main = highest.Main;
        return new StartDefaults(main + 1, main, NextContinueSub(index, main), highest);
    }

    /// <summary>Checks the numbers typed on the Start screen against the folder and suggests the next free number on a collision.</summary>
    public static StartCheck Check(FolderIndex index, StartMode mode, int main, int sub)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (main < 0)
        {
            return new StartCheck("The main number can't be negative.", false, true, null, null);
        }

        if (mode == StartMode.NewGroup)
        {
            if (!index.GroupExists(main))
            {
                return StartCheck.Ok;
            }

            var free = index.NextFreeMain(main);
            return new StartCheck($"Group {main} already exists — next free is {free}.", true, false, free, null);
        }

        if (sub < 1)
        {
            return new StartCheck("The sub number starts at 1.", false, true, null, null);
        }

        if (!index.GroupExists(main))
        {
            return new StartCheck($"Group {main} doesn't exist yet — it will be created.", false, false, null, null);
        }

        var group = index.Group(main);
        if (sub == 1 && group.Count == 1 && group[0].Sub is null)
        {
            return new StartCheck($"{group[0].FileName} will become {main}-1, so the next shot is {main}-2.", false, false, null, 2);
        }

        if (index.NumberExists(main, sub))
        {
            var next = NextContinueSub(index, main);
            return new StartCheck($"{main}-{sub} already exists — next free is {main}-{next}.", true, false, null, next);
        }

        return StartCheck.Ok;
    }

    /// <summary>Next sub after the group's highest; a lone solo counts as 1 because it is renamed to M-1 when the group grows.</summary>
    private static int NextContinueSub(FolderIndex index, int main)
    {
        var group = index.Group(main);
        return group.Count == 1 && group[0].Sub is null ? 2 : index.HighestSub(main) + 1;
    }
}
