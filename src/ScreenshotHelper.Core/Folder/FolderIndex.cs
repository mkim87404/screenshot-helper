using ScreenshotHelper.Core.Naming;

namespace ScreenshotHelper.Core.Folder;

/// <summary>A recognised screenshot in a folder: its current filename and parsed numbering.</summary>
public sealed record IndexedShot(string FileName, ShotName Name)
{
    public int Main => Name.Main;
    public int? Sub => Name.Sub;
}

/// <summary>
/// Point-in-time snapshot of a folder's screenshots, answering numbering questions (highest shot, next free main/sub, collisions).
/// Cheap to build, so callers rescan before each decision instead of caching state that can drift from disk.
/// </summary>
public sealed class FolderIndex
{
    /// <summary>Numbering order: main, then solo before members, then sub, then filename.</summary>
    public static readonly IComparer<IndexedShot> NumberingOrder = Comparer<IndexedShot>.Create((a, b) =>
    {
        var byMain = a.Main.CompareTo(b.Main);
        if (byMain != 0)
        {
            return byMain;
        }

        var bySub = (a.Sub ?? -1).CompareTo(b.Sub ?? -1);
        return bySub != 0 ? bySub : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
    });

    private readonly Dictionary<int, List<IndexedShot>> _groups;
    private readonly HashSet<string> _allFileNames;

    private FolderIndex(string folder, IReadOnlyList<IndexedShot> shots, HashSet<string> allFileNames)
    {
        Folder = folder;
        Shots = shots;
        _allFileNames = allFileNames;
        _groups = shots.GroupBy(s => s.Main).ToDictionary(g => g.Key, g => g.ToList());
    }

    public string Folder { get; }

    /// <summary>Recognised screenshots in numbering order.</summary>
    public IReadOnlyList<IndexedShot> Shots { get; }

    public int? HighestMain => Shots.Count == 0 ? null : Shots[^1].Main;

    /// <summary>The last shot in numbering order (highest main, then highest sub).</summary>
    public IndexedShot? HighestShot => Shots.Count == 0 ? null : Shots[^1];

    /// <summary>Scans <paramref name="folder"/> (non-recursive). Hidden/system files, including the app's own temp files, are ignored.</summary>
    public static FolderIndex Scan(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchType = MatchType.Simple,
        };

        var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shots = new List<IndexedShot>();
        foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", options))
        {
            var name = Path.GetFileName(path);
            allNames.Add(name);
            if (ShotName.TryParse(name) is { } parsed)
            {
                shots.Add(new IndexedShot(name, parsed));
            }
        }

        shots.Sort(NumberingOrder);
        return new FolderIndex(folder, shots, allNames);
    }

    /// <summary>Builds an index from filenames (used by planners working on hypothetical folder states).</summary>
    public static FolderIndex FromFileNames(string folder, IEnumerable<string> fileNames)
    {
        var allNames = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var shots = allNames
            .Select(n => ShotName.TryParse(n) is { } parsed ? new IndexedShot(n, parsed) : null)
            .OfType<IndexedShot>()
            .ToList();
        shots.Sort(NumberingOrder);
        return new FolderIndex(folder, shots, allNames);
    }

    /// <summary>All shots whose main number is <paramref name="main"/>, in numbering order.</summary>
    public IReadOnlyList<IndexedShot> Group(int main) =>
        _groups.TryGetValue(main, out var group) ? group : [];

    public bool GroupExists(int main) => _groups.ContainsKey(main);

    /// <summary>Whether a shot numbered exactly main[-sub] exists (any tail).</summary>
    public bool NumberExists(int main, int? sub) => Group(main).Any(s => s.Sub == sub);

    /// <summary>Highest sub number in the group, or 0 when it has no members.</summary>
    public int HighestSub(int main) => Group(main).Select(s => s.Sub ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>Case-insensitive check against every entry in the folder, including foreign files and subfolders.</summary>
    public bool ContainsFileName(string fileName) => _allFileNames.Contains(fileName);

    /// <summary>Smallest main number ≥ <paramref name="from"/> with no existing group.</summary>
    public int NextFreeMain(int from)
    {
        var candidate = Math.Max(0, from);
        while (GroupExists(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    /// <summary>Smallest sub number ≥ <paramref name="from"/> not used in group <paramref name="main"/>.</summary>
    public int NextFreeSub(int main, int from)
    {
        var candidate = Math.Max(1, from);
        while (NumberExists(main, candidate))
        {
            candidate++;
        }

        return candidate;
    }
}
