using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.IO;

namespace ScreenshotHelper.Core.Renumber;

/// <summary>A plan persisted before execution, so an interrupted run can be resumed or rolled back.</summary>
public sealed record RenameJournal(string Id, string Folder, string Description, DateTimeOffset CreatedAt, IReadOnlyList<RenameStep> Steps);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RenameJournal))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;

/// <summary>Outcome of executing, resuming or rolling back a journal.</summary>
public sealed record ExecutionResult(bool Succeeded, int StepsDone, int StepsTotal, string? Error)
{
    public static ExecutionResult Ok(int total) => new(true, total, total, null);
}

/// <summary>
/// Applies rename plans with a write-ahead journal: the plan is saved atomically before the first rename and each finished step is appended
/// and flushed. A crash therefore leaves at most one rename in doubt, which resume/rollback resolve by checking the disk.
/// The last completed journal is kept for "Undo last renumber".
/// </summary>
public sealed class RenumberExecutor
{
    private const string PlanSuffix = ".plan.json";
    private const string ProgressSuffix = ".progress";
    private const string LastCompletedName = "last-completed.plan.json";

    private readonly string _journalDirectory;
    private readonly AppLog _log;

    public RenumberExecutor(string journalDirectory, AppLog log)
    {
        _journalDirectory = journalDirectory;
        _log = log;
    }

    /// <summary>Raised after every successful on-disk rename (full paths), e.g. to keep the session ledger in sync.</summary>
    public event Action<string, string>? Renamed;

    public bool CanUndoLast => File.Exists(LastCompletedPath);

    private string LastCompletedPath => Path.Combine(_journalDirectory, LastCompletedName);

    /// <summary>Journals and applies a valid plan; if a step fails, rolls back the steps already done so no files end up half-renamed.</summary>
    public ExecutionResult Execute(RenumberPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid)
        {
            return new ExecutionResult(false, 0, plan.Steps.Count, "The plan has conflicts or no changes.");
        }

        var journal = new RenameJournal(
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6],
            plan.Folder,
            plan.Description,
            DateTimeOffset.UtcNow,
            plan.Steps);
        Directory.CreateDirectory(_journalDirectory);
        AtomicFile.WriteAllText(PlanPath(journal.Id), JsonSerializer.Serialize(journal, JournalJsonContext.Default.RenameJournal));
        _log.Info($"Renumber {journal.Id} started: {journal.Description}, {journal.Steps.Count} steps in {journal.Folder}.");
        var result = RunForward(journal, 0);
        if (result.Succeeded)
        {
            return result;
        }

        // All-or-nothing from the user's point of view: a failure mid-way (e.g. a file locked by a viewer) undoes the steps already done.
        // The journal is only kept if even the rollback fails, so the next launch can offer recovery.
        var back = RunBackward(journal, result.StepsDone);
        if (back.Succeeded)
        {
            DeleteJournal(journal.Id);
            return result with { StepsDone = 0, Error = result.Error + " No files were changed." };
        }

        return result with { Error = $"{result.Error} Rolling back also failed ({back.Error}); recovery will be offered on next start." };
    }

    /// <summary>Journals left behind by a crash or failure (oldest first).</summary>
    public IReadOnlyList<RenameJournal> FindIncomplete()
    {
        if (!Directory.Exists(_journalDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_journalDirectory, "*" + PlanSuffix)
            .Where(p => !string.Equals(Path.GetFileName(p), LastCompletedName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(ReadJournal)
            .OfType<RenameJournal>()
            .ToList();
    }

    public ExecutionResult Resume(RenameJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var done = ReadProgress(journal.Id);
        return RunForward(journal, ResolveInDoubt(journal, done));
    }

    public ExecutionResult RollBack(RenameJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var done = ResolveInDoubt(journal, ReadProgress(journal.Id));
        var result = RunBackward(journal, done);
        if (result.Succeeded)
        {
            DeleteJournal(journal.Id);
        }

        return result;
    }

    /// <summary>Reverses the most recent fully completed renumber, if the files are still where it left them.</summary>
    public ExecutionResult UndoLast()
    {
        var journal = ReadJournal(LastCompletedPath);
        if (journal is null)
        {
            return new ExecutionResult(false, 0, 0, "There is no renumber to undo.");
        }

        var result = RunBackward(journal, journal.Steps.Count);
        if (result.Succeeded)
        {
            AtomicFile.TryDelete(LastCompletedPath);
        }

        return result;
    }

    /// <summary>Discards a journal without touching files (user chose to keep the current state).</summary>
    public void Discard(RenameJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        DeleteJournal(journal.Id);
    }

    private ExecutionResult RunForward(RenameJournal journal, int startAt)
    {
        for (var i = startAt; i < journal.Steps.Count; i++)
        {
            var step = journal.Steps[i];
            var from = Path.Combine(journal.Folder, step.From);
            var to = Path.Combine(journal.Folder, step.To);
            try
            {
                File.Move(from, to, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Renumber {journal.Id} stopped at step {i + 1}/{journal.Steps.Count}: {step.From} → {step.To}.", ex);
                return new ExecutionResult(false, i, journal.Steps.Count, $"Couldn't rename {step.From} → {step.To}: {ex.Message}");
            }

            AppendProgress(journal.Id, i);
            Renamed?.Invoke(from, to);
        }

        CompleteJournal(journal.Id);
        _log.Info($"Renumber {journal.Id} completed.");
        return ExecutionResult.Ok(journal.Steps.Count);
    }

    private ExecutionResult RunBackward(RenameJournal journal, int doneCount)
    {
        for (var i = doneCount - 1; i >= 0; i--)
        {
            var step = journal.Steps[i];
            var from = Path.Combine(journal.Folder, step.From);
            var to = Path.Combine(journal.Folder, step.To);
            try
            {
                File.Move(to, from, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Rollback of {journal.Id} stopped at step {i + 1}: {step.To} → {step.From}.", ex);
                return new ExecutionResult(false, doneCount - 1 - i, doneCount, $"Couldn't rename {step.To} back to {step.From}: {ex.Message}");
            }

            Renamed?.Invoke(to, from);
        }

        return ExecutionResult.Ok(doneCount);
    }

    /// <summary>
    /// Steps up to the last recorded index are done. The next one is "in doubt" (crash between rename and progress write):
    /// its source can't have been recreated by a later step yet, so checking the disk is unambiguous.
    /// </summary>
    private static int ResolveInDoubt(RenameJournal journal, int recorded)
    {
        if (recorded >= journal.Steps.Count)
        {
            return recorded;
        }

        var step = journal.Steps[recorded];
        var fromExists = File.Exists(Path.Combine(journal.Folder, step.From));
        var toExists = File.Exists(Path.Combine(journal.Folder, step.To));
        return !fromExists && toExists ? recorded + 1 : recorded;
    }

    // Journals are read back from disk, so treat them as untrusted: every step must be a bare filename inside the journal's folder.
    private static bool IsWellFormed(RenameJournal journal) =>
        !string.IsNullOrWhiteSpace(journal.Id)
        && journal.Id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Path.IsPathFullyQualified(journal.Folder ?? string.Empty)
        && journal.Steps is not null
        && journal.Steps.All(s => IsBareFileName(s.From) && IsBareFileName(s.To));

    private static bool IsBareFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    private string PlanPath(string id) => Path.Combine(_journalDirectory, id + PlanSuffix);

    private string ProgressPath(string id) => Path.Combine(_journalDirectory, id + ProgressSuffix);

    private void AppendProgress(string id, int stepIndex)
    {
        using var stream = new FileStream(ProgressPath(id), FileMode.Append, FileAccess.Write, FileShare.Read);
        var line = System.Text.Encoding.ASCII.GetBytes(stepIndex.ToString(CultureInfo.InvariantCulture) + "\n");
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Number of steps recorded as done (steps always complete in order).</summary>
    private int ReadProgress(string id)
    {
        var path = ProgressPath(id);
        if (!File.Exists(path))
        {
            return 0;
        }

        var last = -1;
        foreach (var line in File.ReadAllLines(path))
        {
            // A torn final line from a crash mid-write simply doesn't parse and is ignored.
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                last = Math.Max(last, index);
            }
        }

        return last + 1;
    }

    private void CompleteJournal(string id)
    {
        File.Move(PlanPath(id), LastCompletedPath, overwrite: true);
        AtomicFile.TryDelete(ProgressPath(id));
    }

    private void DeleteJournal(string id)
    {
        AtomicFile.TryDelete(PlanPath(id));
        AtomicFile.TryDelete(ProgressPath(id));
    }

    private RenameJournal? ReadJournal(string path)
    {
        try
        {
            var journal = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), JournalJsonContext.Default.RenameJournal) : null;
            if (journal is not null && !IsWellFormed(journal))
            {
                _log.Warn($"Ignoring malformed renumber journal {path}.");
                return null;
            }

            return journal;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Unreadable renumber journal {path}.", ex);
            return null;
        }
    }
}
