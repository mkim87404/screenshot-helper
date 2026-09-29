using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Naming;

namespace ScreenshotHelper.Core.Renumber;

/// <summary>A bulk renumbering request applied to a selection of shots.</summary>
public abstract record RenumberOperation
{
    public abstract string Describe();
}

/// <summary>Adds deltas to every selected shot's numbers. Solo shots have no sub number, so only the main delta applies to them.</summary>
public sealed record ShiftOperation(int MainDelta, int SubDelta) : RenumberOperation
{
    public override string Describe() => $"Shift main {MainDelta:+#;-#;0}, sub {SubDelta:+#;-#;0}";
}

/// <summary>Removes numbering gaps: selected groups renumbered consecutively from <paramref name="StartMain"/> and/or members renumbered 1..n per group.</summary>
public sealed record CloseGapsOperation(int? StartMain, bool CompactSubs) : RenumberOperation
{
    public override string Describe() => StartMain is { } start
        ? $"Close gaps (groups from {start}{(CompactSubs ? ", subs from 1" : string.Empty)})"
        : "Close gaps (subs from 1)";
}

/// <summary>Moves the selection, in numbering order, into group <paramref name="TargetMain"/> starting at sub <paramref name="StartSub"/>.</summary>
public sealed record MoveToGroupOperation(int TargetMain, int StartSub) : RenumberOperation
{
    public override string Describe() => $"Move to group {TargetMain} from sub {StartSub}";
}

/// <summary>Makes each selected group consistent: a lone member becomes solo; a solo beside members becomes member 1.</summary>
public sealed record NormaliseSolosOperation : RenumberOperation
{
    public override string Describe() => "Normalise solos";
}

public enum PlanRowStatus
{
    Unchanged,
    Rename,
    Warning,
    Conflict,
}

/// <summary>One file in a plan preview.</summary>
public sealed record PlanRow(string Source, string Target, PlanRowStatus Status, string? Message);

/// <summary>One on-disk rename (filenames relative to the plan folder).</summary>
public sealed record RenameStep(string From, string To);

/// <summary>A validated, ordered renumbering plan. Only <see cref="IsValid"/> plans may be executed.</summary>
public sealed record RenumberPlan(string Folder, string Description, IReadOnlyList<PlanRow> Rows, IReadOnlyList<RenameStep> Steps)
{
    public bool HasConflicts => Rows.Any(r => r.Status == PlanRowStatus.Conflict);

    public bool IsValid => !HasConflicts && Steps.Count > 0;
}

/// <summary>
/// Pure planning: turns a selection + operation into preview rows and a collision-free rename order.
/// Only numeric prefixes change; tails (timestamps, captions, anything else) are carried over untouched.
/// </summary>
public static class RenumberPlanner
{
    /// <summary>Prefix of the temporary names used to break rename cycles.</summary>
    public const string TempPrefix = ".~sshelper-renumber-";

    /// <summary>Builds the preview rows and a collision-free rename order for applying <paramref name="operation"/> to the selected files.</summary>
    public static RenumberPlan Plan(FolderIndex index, IReadOnlyCollection<string> selectedFileNames, RenumberOperation operation)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(selectedFileNames);
        ArgumentNullException.ThrowIfNull(operation);

        var selected = new HashSet<string>(selectedFileNames, StringComparer.OrdinalIgnoreCase);
        var shots = index.Shots.Where(s => selected.Contains(s.FileName)).ToList();
        var mapping = operation switch
        {
            ShiftOperation shift => Shift(shots, shift),
            CloseGapsOperation gaps => CloseGaps(shots, gaps),
            MoveToGroupOperation move => MoveToGroup(shots, move),
            NormaliseSolosOperation => NormaliseSolos(index, shots),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var rows = Validate(index, mapping);
        var steps = rows.Any(r => r.Status == PlanRowStatus.Conflict) ? [] : Order(rows);
        return new RenumberPlan(index.Folder, operation.Describe(), rows, steps);
    }

    private sealed record Mapping(IndexedShot Shot, int Main, int? Sub, string? Note);

    private static List<Mapping> Shift(List<IndexedShot> shots, ShiftOperation op) =>
        shots.Select(s => new Mapping(
                s,
                s.Main + op.MainDelta,
                s.Sub is { } sub ? sub + op.SubDelta : null,
                s.Sub is null && op.SubDelta != 0 ? "Solo shot has no sub number; only the main number changed." : null))
            .ToList();

    private static List<Mapping> CloseGaps(List<IndexedShot> shots, CloseGapsOperation op)
    {
        var result = new List<Mapping>();
        var groupIndex = 0;
        foreach (var group in shots.GroupBy(s => s.Main).OrderBy(g => g.Key))
        {
            var main = op.StartMain is { } start ? start + groupIndex : group.Key;
            groupIndex++;
            var nextSub = 1;
            foreach (var shot in group.OrderBy(s => s, FolderIndex.NumberingOrder))
            {
                int? sub = shot.Sub is null ? null : op.CompactSubs ? nextSub++ : shot.Sub;
                result.Add(new Mapping(shot, main, sub, null));
            }
        }

        return result;
    }

    private static List<Mapping> MoveToGroup(List<IndexedShot> shots, MoveToGroupOperation op) =>
        shots.Order(FolderIndex.NumberingOrder)
            .Select((s, i) => new Mapping(s, op.TargetMain, op.StartSub + i, null))
            .ToList();

    private static List<Mapping> NormaliseSolos(FolderIndex index, List<IndexedShot> shots)
    {
        var result = new List<Mapping>();
        foreach (var main in shots.Select(s => s.Main).Distinct().Order())
        {
            var group = index.Group(main);
            var solos = group.Where(s => s.Sub is null).ToList();
            var members = group.Where(s => s.Sub is not null).ToList();
            if (solos.Count == 0 && members.Count == 1)
            {
                result.Add(new Mapping(members[0], main, null, "Only shot in its group; made solo."));
            }
            else if (solos.Count == 1 && members.Count > 0)
            {
                // The solo was taken first, so it becomes member 1; existing members move up only if 1 is taken.
                var shiftMembers = members.Any(m => m.Sub == 1);
                result.Add(new Mapping(solos[0], main, 1, "Solo beside members; made member 1."));
                result.AddRange(members.Select(m => new Mapping(m, main, shiftMembers ? m.Sub + 1 : m.Sub, null)));
            }
        }

        return result;
    }

    private static List<PlanRow> Validate(FolderIndex index, List<Mapping> mapping)
    {
        var inPlan = new HashSet<string>(mapping.Select(m => m.Shot.FileName), StringComparer.OrdinalIgnoreCase);
        var targets = mapping
            .Select(m => (Mapping: m, Name: m.Main >= 0 && m.Sub is not < 0 ? m.Shot.Name.WithNumbers(m.Main, m.Sub) : null))
            .ToList();

        // A target number is a conflict only if *different* source numbers would merge into it. Files that already share a number
        // (e.g. "5-2.png" and "5-2 (dup).png") may move together: the shift doesn't make anything worse, and their names stay distinct.
        var numberCounts = targets.Where(t => t.Name is not null)
            .GroupBy(t => (t.Name!.Main, t.Name.Sub))
            .ToDictionary(g => g.Key, g => g.Select(t => (t.Mapping.Shot.Main, t.Mapping.Shot.Sub)).Distinct().Count());
        var nameCounts = targets.Where(t => t.Name is not null)
            .GroupBy(t => t.Name!.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var outsiders = index.Shots.Where(s => !inPlan.Contains(s.FileName)).ToList();
        var folderPrefix = Path.TrimEndingDirectorySeparator(index.Folder).Length + 1;

        var rows = new List<PlanRow>();
        foreach (var (map, name) in targets)
        {
            var source = map.Shot.FileName;
            if (name is null)
            {
                rows.Add(new PlanRow(source, source, PlanRowStatus.Conflict, "Numbers can't be negative."));
                continue;
            }

            var target = name.FileName;
            var outsiderSameNumber = outsiders.FirstOrDefault(o => o.Main == name.Main && o.Sub == name.Sub);
            string? conflict =
                numberCounts[(name.Main, name.Sub)] > 1 ? $"Two selected files would both become {name.Numbering}." :
                nameCounts[target] > 1 ? $"Two selected files would both be named {target}." :
                outsiderSameNumber is not null ? $"{name.Numbering} is already used by {outsiderSameNumber.FileName}." :
                !string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && index.ContainsFileName(target) && !inPlan.Contains(target)
                    ? $"{target} already exists."
                    : target.Length > CaptionRules.MaxFileNameLength || folderPrefix + target.Length > CaptionRules.MaxPathLength
                        ? "New name would exceed the Windows path length limit."
                        : null;
            if (conflict is not null)
            {
                rows.Add(new PlanRow(source, target, PlanRowStatus.Conflict, conflict));
                continue;
            }

            var warning = map.Note;
            if (name.Sub == 0)
            {
                warning = Join(warning, "Sub number 0.");
            }

            if (name.Sub is null && outsiders.Any(o => o.Main == name.Main && o.Sub is not null))
            {
                warning = Join(warning, $"Group {name.Main} would have a solo shot alongside members.");
            }
            else if (name.Sub is not null && outsiders.Any(o => o.Main == name.Main && o.Sub is null))
            {
                warning = Join(warning, $"Group {name.Main} already has a solo shot.");
            }

            var unchanged = string.Equals(source, target, StringComparison.Ordinal);
            rows.Add(new PlanRow(
                source,
                target,
                unchanged ? PlanRowStatus.Unchanged : warning is null ? PlanRowStatus.Rename : PlanRowStatus.Warning,
                warning));
        }

        return rows;
    }

    /// <summary>
    /// Orders renames so no file is ever moved onto a name another pending file still holds.
    /// A file moves as soon as its target is free; if every pending file is blocked (a cycle), one is parked under a temporary name.
    /// For a uniform shift this produces highest-first for positive deltas and lowest-first for negative ones.
    /// </summary>
    private static List<RenameStep> Order(List<PlanRow> rows)
    {
        var pending = rows
            .Where(r => r.Status is PlanRowStatus.Rename or PlanRowStatus.Warning)
            .OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PendingRename(r.Source, r.Target))
            .ToList();
        var occupied = new HashSet<string>(pending.Select(p => p.Current), StringComparer.OrdinalIgnoreCase);
        var steps = new List<RenameStep>();
        var tempCounter = 0;
        var tempBase = TempPrefix + Guid.NewGuid().ToString("N")[..8];

        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var item in pending.ToList())
            {
                if (occupied.Contains(item.Target))
                {
                    continue;
                }

                steps.Add(new RenameStep(item.Current, item.Target));
                occupied.Remove(item.Current);
                pending.Remove(item);
                progressed = true;
            }

            if (!progressed)
            {
                var parked = pending[0];
                var temp = $"{tempBase}-{tempCounter++}.tmp";
                steps.Add(new RenameStep(parked.Current, temp));
                occupied.Remove(parked.Current);
                parked.Current = temp;
            }
        }

        return steps;
    }

    private static string Join(string? a, string b) => a is null ? b : $"{a} {b}";

    private sealed class PendingRename(string current, string target)
    {
        public string Current { get; set; } = current;
        public string Target { get; } = target;
    }
}
