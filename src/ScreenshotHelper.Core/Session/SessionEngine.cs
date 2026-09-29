using System.Globalization;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Folder;
using ScreenshotHelper.Core.IO;
using ScreenshotHelper.Core.Naming;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Renumber;
using ScreenshotHelper.Core.Settings;

namespace ScreenshotHelper.Core.Session;

/// <summary>
/// The screenshot session state machine (spec §2, §4–§6). Not thread-safe by design: <see cref="SessionActor"/> is its only caller,
/// so every state change and file operation is serialised without locks.
/// </summary>
public sealed class SessionEngine
{
    private readonly SessionOptions _options;
    private readonly IScreenCapture _capture;
    private readonly IRecycleBin _recycleBin;
    private readonly IClipboardImage? _clipboard;
    private readonly ICollisionPrompt _prompt;
    private readonly IFeedbackSink _feedback;
    private readonly RenumberExecutor _renumber;
    private readonly TimeProvider _time;
    private readonly AppLog _log;
    private readonly Stack<UndoRecord> _undo = new();

    private Cursor _cursor;
    private SubCollisionChoice? _rememberedChoice;

    public SessionEngine(
        SessionOptions options,
        IScreenCapture capture,
        IRecycleBin recycleBin,
        IClipboardImage? clipboard,
        ICollisionPrompt prompt,
        IFeedbackSink feedback,
        RenumberExecutor renumber,
        TimeProvider time,
        AppLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _capture = capture;
        _recycleBin = recycleBin;
        _clipboard = clipboard;
        _prompt = prompt;
        _feedback = feedback;
        _renumber = renumber;
        _time = time;
        _log = log;
        CaptureTarget = options.CaptureTarget;
        _cursor = options.Mode == StartMode.ContinueGroup
            ? new Cursor(options.Main, GroupOpened: true, NextSub: Math.Max(1, options.Sub))
            : new Cursor(options.Main, GroupOpened: false, NextSub: 1);
        _rememberedChoice = options.SubCollisionPolicy switch
        {
            SubCollisionPolicy.Append => SubCollisionChoice.Append,
            SubCollisionPolicy.Insert => SubCollisionChoice.Insert,
            _ => null,
        };
        _renumber.Renamed += Ledger.Renamed;
    }

    /// <summary>Raised on the actor thread after every state change.</summary>
    public event Action<SessionStatus>? StatusChanged;

    public SessionLedger Ledger { get; } = new();

    public string Folder => _options.Folder;

    public CaptureTarget CaptureTarget { get; private set; }

    public bool TimestampArmed { get; private set; }

    public string? Caption { get; private set; }

    public SessionStatus Status => new(
        _options.Folder, _cursor.Main, _cursor.GroupOpened, _cursor.NextSub, TimestampArmed || _options.AlwaysTimestamp, Caption, Ledger.Count, CaptureTarget);

    public void Start()
    {
        ShotFile.CleanOrphans(_options.Folder);
        var status = Status;
        Notify(FeedbackKind.SessionStarted, $"Session started — next: {status.NextMainText} (main) or {status.NextSubText} (sub)");
    }

    /// <summary>Ends the session: cleans temp files and returns the session's files, lowest-numbered first.</summary>
    public IReadOnlyList<string> End()
    {
        _renumber.Renamed -= Ledger.Renamed;
        ShotFile.CleanOrphans(_options.Folder);
        var files = Ledger.ExistingFilesInNumberingOrder();
        Notify(FeedbackKind.SessionEnded, files.Count == 1 ? "Session ended — 1 screenshot" : $"Session ended — {files.Count} screenshots");
        return files;
    }

    public void SetCaptureTarget(CaptureTarget target)
    {
        CaptureTarget = target;
        RaiseStatus();
    }

    /// <summary>
    /// The timestamp key. In <see cref="AnnotationTarget.LastShot"/> mode it adds (or removes) the capture-time timestamp on the shot just
    /// taken; in <see cref="AnnotationTarget.NextShot"/> mode it arms (or disarms) one for the next shot. It never captures anything.
    /// </summary>
    public void ToggleTimestamp()
    {
        if (_options.AlwaysTimestamp)
        {
            Notify(FeedbackKind.TimestampArmed, "⏱ Timestamp is always on (Settings)");
            return;
        }

        if (_options.AnnotationTarget == AnnotationTarget.LastShot)
        {
            if (LastShot() is { } last)
            {
                var add = last.Timestamp is null;
                var timestamp = add ? ShotTail.FormatTimestamp(last.CapturedAt, _options.TimestampZone) : null;
                Annotate(last, timestamp, last.Caption, add ? FeedbackKind.TimestampArmed : FeedbackKind.TimestampDisarmed,
                    add ? "⏱ Timestamp added to {0}" : "⏱ Timestamp removed from {0}");
            }

            return;
        }

        TimestampArmed = !TimestampArmed;
        Notify(
            TimestampArmed ? FeedbackKind.TimestampArmed : FeedbackKind.TimestampDisarmed,
            TimestampArmed ? "⏱ Timestamp next shot" : "⏱ Timestamp off");
    }

    /// <summary>
    /// What the caption box should show, or null (after a warning) when there is nothing to caption yet in last-shot mode.
    /// The budget always leaves room for a timestamp that may still be added.
    /// </summary>
    public CaptionRequest? PrepareCaption()
    {
        if (_options.AnnotationTarget == AnnotationTarget.NextShot)
        {
            return new CaptionRequest(CaptionBudget(), Caption, "the next screenshot");
        }

        if (LastShot() is not { } last || CurrentName(last) is not { } name)
        {
            return null;
        }

        var withoutCaption = name.WithTail(ShotTail.Build(last.Timestamp, null)).FileName.Length
                             + (last.Timestamp is null ? ShotTail.MaxTimestampLength + 3 : 0);
        return new CaptionRequest(CaptionRules.BudgetForName(_options.Folder, withoutCaption), last.Caption, name.Numbering);
    }

    /// <summary>
    /// The caption box's answer. Null means the box was cancelled (nothing changes). Otherwise the text is normalised (trimmed, invalid
    /// characters removed) and applied to the last shot or armed for the next one; empty text removes/cancels the caption.
    /// </summary>
    public void SetCaption(string? caption)
    {
        if (caption is null)
        {
            Notify(FeedbackKind.CaptionCancelled, "✎ Caption unchanged");
            return;
        }

        var normalized = CaptionRules.Normalize(caption);
        if (_options.AnnotationTarget == AnnotationTarget.LastShot)
        {
            if (LastShot() is { } last)
            {
                Annotate(last, last.Timestamp, normalized, normalized is null ? FeedbackKind.CaptionCancelled : FeedbackKind.CaptionArmed,
                    normalized is null ? "✎ Caption removed from {0}" : "✎ Caption added to {0}: " + normalized);
            }

            return;
        }

        Caption = normalized;
        Notify(
            Caption is null ? FeedbackKind.CaptionCancelled : FeedbackKind.CaptionArmed,
            Caption is null ? "✎ Caption cancelled" : $"✎ Caption set: {Caption}");
    }

    /// <summary>How many caption characters the next shot can take in this folder (worst case of either key, plus a timestamp).</summary>
    public int CaptionBudget()
    {
        var index = FolderIndex.Scan(_options.Folder);
        var nextMain = Math.Max(_cursor.Main + 1, (index.HighestMain ?? 0) + 1);
        var nextSub = Math.Max(_cursor.NextSub, index.HighestSub(_cursor.Main) + 1) + 1;
        var worstNumbering = Digits(nextMain) + 1 + Digits(nextSub);
        return CaptionRules.Budget(_options.Folder, worstNumbering);
    }

    /// <summary>Captures the screen, then names and saves the shot as a main or sub shot, resolving collisions and reporting the outcome.</summary>
    public async Task TakeShotAsync(ShotKind kind, CancellationToken cancellationToken)
    {
        CapturedImage image;
        try
        {
            image = _capture.Capture(CaptureTarget);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Screen capture failed.", ex);
            Notify(FeedbackKind.Error, "Capture failed — nothing was saved");
            return;
        }

        // Timestamp is the moment of capture, not when 't' was pressed.
        var capturedAt = _time.GetLocalNow();
        using (image)
        {
            try
            {
                if (kind == ShotKind.Main)
                {
                    SaveMain(image, capturedAt);
                }
                else
                {
                    await SaveSubAsync(image, capturedAt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Saving a {kind} shot in {_options.Folder} failed.", ex);
                Notify(FeedbackKind.Error, Directory.Exists(_options.Folder)
                    ? $"Save failed — {ex.Message}"
                    : "Save failed — the folder is no longer available");
            }
        }
    }

    /// <summary>Sends the last shot to the Recycle Bin, reverses the renames it caused, and rewinds the numbering to before it.</summary>
    public void Undo()
    {
        if (!_undo.TryPop(out var record))
        {
            Notify(FeedbackKind.Warning, "Nothing to undo");
            return;
        }

        var path = Ledger.PathOf(record.LedgerId);
        if (path is null || !File.Exists(path))
        {
            Notify(FeedbackKind.Warning, "The last screenshot was moved or deleted outside the app — nothing undone");
            return;
        }

        try
        {
            _recycleBin.Recycle(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Undo could not recycle {path}.", ex);
            _undo.Push(record);
            Notify(FeedbackKind.Error, "Undo failed — couldn't move the file to the Recycle Bin");
            return;
        }

        Ledger.Remove(record.LedgerId);
        var reverted = RevertRenames(record.Renames);
        _cursor = record.CursorBefore;
        var message = $"↶ Removed {Path.GetFileName(path)}";
        if (!reverted)
        {
            _log.Warn("Undo recycled the shot but could not reverse every rename it caused.");
            Notify(FeedbackKind.Undo, message + " — some earlier renames could not be reversed", hasCaveat: true);
        }
        else
        {
            Notify(FeedbackKind.Undo, message);
        }
    }

    private void SaveMain(CapturedImage image, DateTimeOffset capturedAt)
    {
        var index = FolderIndex.Scan(_options.Folder);
        var desired = _cursor.GroupOpened ? _cursor.Main + 1 : _cursor.Main;
        var main = index.NextFreeMain(desired);
        var warning = main != desired ? $"{desired} was taken — saved as group {main}" : null;
        int? sub = _options.AlwaysNumberFirstShotAsMember ? 1 : null;

        var cursorBefore = _cursor;
        var written = CommitWithRetry(image, capturedAt, ref main, ref sub, isMain: true, ref warning);
        _cursor = new Cursor(main, GroupOpened: true, NextSub: 2);
        Completed(image, written, cursorBefore, [], FeedbackKind.MainSaved, warning);
    }

    private async Task SaveSubAsync(CapturedImage image, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        var cursorBefore = _cursor;
        var cursor = _cursor.GroupOpened ? _cursor : _cursor with { GroupOpened = true, NextSub = 1 };
        var main = cursor.Main;
        var index = FolderIndex.Scan(_options.Folder);
        var group = index.Group(main);
        var solos = group.Where(s => s.Sub is null).ToList();
        var hasMembers = group.Any(s => s.Sub is not null);
        var renames = new List<RenameRecord>();
        string? warning = null;
        var sub = cursor.NextSub;

        if (solos.Count == 1 && !hasMembers)
        {
            // The group's only shot is solo "M": it becomes "M-1" (tail kept) so the group reads consistently, and this shot is M-2 or later.
            sub = Math.Max(sub, 2);
            var from = Path.Combine(_options.Folder, solos[0].FileName);
            var to = Path.Combine(_options.Folder, solos[0].Name.WithNumbers(main, 1).FileName);
            try
            {
                ShotFile.Rename(from, to);
                Ledger.Renamed(from, to);
                renames.Add(new RenameRecord(from, to));
            }
            catch (IOException ex)
            {
                _log.Warn($"Solo rename {from} → {to} failed; saving anyway.", ex);
                warning = $"Couldn't rename {solos[0].FileName} to {main}-1 (file in use) — fix later with Renumber › Normalise solos";
            }
        }
        else if (solos.Count > 0 && hasMembers)
        {
            warning = $"Group {main} has both a solo shot and members — left as is";
        }

        if (index.NumberExists(main, sub))
        {
            var existing = group.Where(s => s.Sub == sub).Select(s => s.FileName).ToList();
            var appendSub = index.HighestSub(main) + 1;
            var choice = _rememberedChoice;
            if (choice is null)
            {
                var answer = await _prompt.AskAsync(new SubCollision(_options.Folder, main, sub, existing, appendSub), cancellationToken)
                    .ConfigureAwait(false);
                choice = answer.Choice;
                if (answer.RememberForSession && choice != SubCollisionChoice.Overwrite)
                {
                    _rememberedChoice = choice;
                }
            }

            switch (choice.Value)
            {
                case SubCollisionChoice.Discard:
                    RevertRenames(renames);
                    Notify(FeedbackKind.Warning, $"Shot discarded — {main}-{sub} was taken");
                    return;
                case SubCollisionChoice.Append:
                    sub = appendSub;
                    break;
                case SubCollisionChoice.Insert:
                    if (!TryInsertGap(index, main, sub, renames))
                    {
                        sub = appendSub;
                        warning = Join(warning, $"Couldn't make room at {main}-{sub} — appended instead");
                    }

                    break;
                case SubCollisionChoice.Overwrite:
                    if (!TryRecycle(existing))
                    {
                        sub = appendSub;
                        warning = Join(warning, "Couldn't move the old file to the Recycle Bin — appended instead");
                    }

                    break;
            }
        }

        int? subValue = sub;
        var written = CommitWithRetry(image, capturedAt, ref main, ref subValue, isMain: false, ref warning);
        _cursor = new Cursor(main, GroupOpened: true, NextSub: subValue!.Value + 1);
        Completed(image, written, cursorBefore, renames, FeedbackKind.SubSaved, warning);
    }

    /// <summary>
    /// Writes the shot. If a file with the exact name appears between the scan and the write (another program), moves to the next free
    /// number instead of overwriting.
    /// </summary>
    private WrittenShot CommitWithRetry(CapturedImage image, DateTimeOffset capturedAt, ref int main, ref int? sub, bool isMain, ref string? warning)
    {
        var timestamp = TimestampArmed || _options.AlwaysTimestamp ? ShotTail.FormatTimestamp(capturedAt, _options.TimestampZone) : null;
        for (var attempt = 0; ; attempt++)
        {
            var withoutCaption = ShotName.Create(main, sub, ShotTail.Build(timestamp, null)).FileName;
            var caption = CaptionRules.FitToPath(_options.Folder, withoutCaption, Caption);
            if (Caption is not null && caption != Caption)
            {
                warning = Join(warning, caption is null ? "Caption dropped — the folder path is too long" : "Caption shortened to fit the path limit");
            }

            var name = ShotName.Create(main, sub, ShotTail.Build(timestamp, caption));
            try
            {
                return new WrittenShot(ShotFile.Commit(_options.Folder, name.FileName, image), capturedAt, timestamp, caption);
            }
            catch (DestinationExistsException) when (attempt < 5)
            {
                var index = FolderIndex.Scan(_options.Folder);
                if (isMain)
                {
                    main = index.NextFreeMain(main + 1);
                }
                else
                {
                    sub = index.NextFreeSub(main, sub!.Value + 1);
                }

                warning = Join(warning, $"A file with that name appeared — saved as {ShotName.Create(main, sub).Numbering}");
            }
        }
    }

    private void Completed(CapturedImage image, WrittenShot written, Cursor cursorBefore, List<RenameRecord> renames, FeedbackKind savedKind, string? warning)
    {
        var path = written.Path;
        var id = Ledger.Add(path);
        _undo.Push(new UndoRecord(id, cursorBefore, renames, written.CapturedAt, written.Timestamp, written.Caption));
        TimestampArmed = false;
        Caption = null;

        if (_options.CopyToClipboard && _clipboard is not null)
        {
            try
            {
                _clipboard.SetImage(image);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("Copying the screenshot to the clipboard failed.", ex);
                warning = Join(warning, "couldn't copy to the clipboard");
            }
        }

        // Reached only after ShotFile.Commit returned, i.e. the PNG is flushed and atomically in place: the MainSaved/SubSaved sound is
        // a guarantee the shot exists. A caveat never replaces that sound; it's shown in the (amber) toast and the log.
        var numbering = ShotName.TryParse(Path.GetFileName(path))?.Numbering ?? Path.GetFileName(path);
        if (warning is not null)
        {
            _log.Warn($"Saved {path} with a caveat: {warning}");
        }

        Notify(savedKind, warning is null ? $"✓ {numbering}" : $"✓ {numbering} — {warning}", hasCaveat: warning is not null);
    }

    private bool TryInsertGap(FolderIndex index, int main, int sub, List<RenameRecord> renames)
    {
        var toShift = index.Group(main).Where(s => s.Sub >= sub).Select(s => s.FileName).ToList();
        var plan = RenumberPlanner.Plan(index, toShift, new ShiftOperation(0, 1));
        if (!plan.IsValid)
        {
            _log.Warn($"Insert at {main}-{sub} not possible: {string.Join("; ", plan.Rows.Where(r => r.Message is not null).Select(r => r.Message))}");
            return false;
        }

        var result = _renumber.Execute(plan);
        if (!result.Succeeded)
        {
            _log.Warn($"Insert at {main}-{sub} failed: {result.Error}");
            return false;
        }

        renames.AddRange(plan.Steps.Select(s => new RenameRecord(Path.Combine(_options.Folder, s.From), Path.Combine(_options.Folder, s.To))));
        return true;
    }

    private bool TryRecycle(IReadOnlyList<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            try
            {
                _recycleBin.Recycle(Path.Combine(_options.Folder, fileName));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"Could not recycle {fileName} for overwrite.", ex);
                return false;
            }
        }

        return true;
    }

    /// <summary>Reverses renames newest-first; each is skipped if its files are no longer where the rename left them. Returns false if any couldn't be reversed.</summary>
    private bool RevertRenames(IReadOnlyList<RenameRecord> renames)
    {
        var allReverted = true;
        for (var i = renames.Count - 1; i >= 0; i--)
        {
            var (from, to) = renames[i];
            try
            {
                if (File.Exists(to) && !File.Exists(from))
                {
                    ShotFile.Rename(to, from);
                    Ledger.Renamed(to, from);
                }
                else
                {
                    allReverted = false;
                }
            }
            catch (IOException ex)
            {
                _log.Warn($"Could not reverse rename {from} → {to}.", ex);
                allReverted = false;
            }
        }

        return allReverted;
    }

    /// <summary>The most recent shot of this session (the one last-shot annotations act on), or null after a warning.</summary>
    private UndoRecord? LastShot()
    {
        if (_undo.TryPeek(out var last))
        {
            return last;
        }

        Notify(FeedbackKind.Warning, "No screenshot yet in this session — take one first");
        return null;
    }

    /// <summary>
    /// The shot's current name, if it still carries exactly the tail the app wrote. Anything else means it was renamed or moved outside
    /// the app, and annotating it could overwrite the user's own edits, so null is returned (after a warning).
    /// </summary>
    private ShotName? CurrentName(UndoRecord shot)
    {
        var path = Ledger.PathOf(shot.LedgerId);
        var name = path is not null && File.Exists(path) ? ShotName.TryParse(Path.GetFileName(path)) : null;
        if (name is not null && string.Equals(name.Tail, ShotTail.Build(shot.Timestamp, shot.Caption), StringComparison.Ordinal))
        {
            return name;
        }

        Notify(FeedbackKind.Warning, "The last screenshot was renamed or moved outside the app — left unchanged");
        return null;
    }

    /// <summary>Renames the last shot to carry <paramref name="timestamp"/> and <paramref name="caption"/> (numbers untouched).</summary>
    private void Annotate(UndoRecord shot, string? timestamp, string? caption, FeedbackKind kind, string messageFormat)
    {
        if (CurrentName(shot) is not { } name)
        {
            return;
        }

        var from = Ledger.PathOf(shot.LedgerId)!;
        var withoutCaption = name.WithTail(ShotTail.Build(timestamp, null)).FileName;
        var fitted = CaptionRules.FitToPath(_options.Folder, withoutCaption, caption);
        var renamed = name.WithTail(ShotTail.Build(timestamp, fitted));
        var to = Path.Combine(_options.Folder, renamed.FileName);
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            try
            {
                ShotFile.Rename(from, to);
            }
            catch (IOException ex)
            {
                _log.Warn($"Annotating {from} → {to} failed.", ex);
                Notify(FeedbackKind.Warning, ex is DestinationExistsException
                    ? $"{renamed.FileName} already exists — {name.Numbering} left unchanged"
                    : $"Couldn't rename {name.Numbering} (file in use) — left unchanged");
                return;
            }

            Ledger.Renamed(from, to);
        }

        _undo.Pop();
        _undo.Push(shot with { Timestamp = timestamp, Caption = fitted });
        var message = string.Format(CultureInfo.InvariantCulture, messageFormat, name.Numbering);
        var shortened = fitted != caption;
        Notify(kind, shortened ? message + " (shortened to fit the path limit)" : message, hasCaveat: shortened);
    }

    private void Notify(FeedbackKind kind, string message, bool hasCaveat = false)
    {
        _feedback.Notify(new FeedbackEvent(kind, message, hasCaveat));
        RaiseStatus();
    }

    private void RaiseStatus() => StatusChanged?.Invoke(Status);

    private static int Digits(int value) => Math.Max(1, value).ToString(CultureInfo.InvariantCulture).Length;

    private static string Join(string? a, string b) => a is null ? b : $"{a}; {b}";

    /// <summary>Numbering position: the current group, whether it has been started this session, and the next sub to use.</summary>
    private readonly record struct Cursor(int Main, bool GroupOpened, int NextSub);

    private sealed record RenameRecord(string From, string To);

    /// <summary>What CommitWithRetry actually wrote: the final path plus the exact timestamp/caption text in its name.</summary>
    private sealed record WrittenShot(string Path, DateTimeOffset CapturedAt, string? Timestamp, string? Caption);

    /// <summary>One saved shot: enough to undo it, and to annotate it later without guessing what its tail contains.</summary>
    private sealed record UndoRecord(
        int LedgerId, Cursor CursorBefore, IReadOnlyList<RenameRecord> Renames, DateTimeOffset CapturedAt, string? Timestamp, string? Caption);
}
