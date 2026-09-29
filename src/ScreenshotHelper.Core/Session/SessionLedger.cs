using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.Naming;

namespace ScreenshotHelper.Core.Session;

/// <summary>
/// Every file a session created, tracked by a stable id whose path is updated on each rename the app performs,
/// so "reveal the session's files" and "select the lowest-numbered one" stay correct after mid-session renames.
/// </summary>
public sealed class SessionLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, string> _paths = [];
    private int _nextId;

    public int Add(string path)
    {
        lock (_gate)
        {
            _paths[_nextId] = path;
            return _nextId++;
        }
    }

    public string? PathOf(int id)
    {
        lock (_gate)
        {
            return _paths.GetValueOrDefault(id);
        }
    }

    /// <summary>Applies an on-disk rename to whichever entry currently has <paramref name="from"/>.</summary>
    public void Renamed(string from, string to)
    {
        lock (_gate)
        {
            foreach (var (id, path) in _paths)
            {
                if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase))
                {
                    _paths[id] = to;
                    return;
                }
            }
        }
    }

    public void Remove(int id)
    {
        lock (_gate)
        {
            _paths.Remove(id);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _paths.Count;
            }
        }
    }

    /// <summary>Session files still on disk, in numbering order (lowest first); files changed outside the app are skipped.</summary>
    public IReadOnlyList<string> ExistingFilesInNumberingOrder()
    {
        List<string> snapshot;
        lock (_gate)
        {
            snapshot = [.. _paths.Values];
        }

        return snapshot
            .Where(File.Exists)
            .Select(p => (Path: p, Shot: ShotName.TryParse(Path.GetFileName(p))))
            .Where(x => x.Shot is not null)
            .Select(x => new { x.Path, Indexed = new IndexedShot(Path.GetFileName(x.Path), x.Shot!) })
            .OrderBy(x => x.Indexed, FolderIndex.NumberingOrder)
            .Select(x => x.Path)
            .ToList();
    }
}
