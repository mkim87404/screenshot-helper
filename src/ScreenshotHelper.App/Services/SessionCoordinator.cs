using ScreenshotHelper.Core.Feedback;
using ScreenshotHelper.Core.Platform;
using ScreenshotHelper.Core.Session;

namespace ScreenshotHelper.App.Services;

/// <summary>What the UI must do for a session; implemented by the Avalonia app and by test doubles.</summary>
public interface ISessionUi
{
    /// <summary>Shows the caption box; returns the raw text (empty = remove the caption), or null if cancelled.</summary>
    Task<string?> AskCaptionAsync(CaptionRequest request);

    Task<SubCollisionAnswer> AskCollisionAsync(SubCollision collision);

    void ShowToast(FeedbackEvent feedback);

    void SessionStarted();

    void SessionEnded(SessionSummary summary);

    void StateChanged();

    /// <summary>Closes any open caption box (as cancelled) or collision prompt (as discard), e.g. when the app exits mid-prompt.</summary>
    void CloseOpenPrompts();
}

/// <summary>Result of a finished session, for the Summary screen and Explorer reveal.</summary>
public sealed record SessionSummary(string Folder, IReadOnlyList<string> Files);

/// <summary>
/// Orchestrates a session: owns the <see cref="SessionActor"/>, maps hotkeys to commands and keeps hotkey registration in step with the
/// session phase (Active → all keys; Paused → only pause; Modal → none, so typing reaches the caption box or prompt).
/// Phase changes and UI calls happen on the UI thread (via <c>postToUi</c>); shot commands go straight to the actor.
/// </summary>
public sealed class SessionCoordinator : ICollisionPrompt, IFeedbackSink, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly ISessionUi _ui;
    private readonly Action<Action> _postToUi;
    private SessionActor? _actor;
    private SessionEngine? _engine;
    private SessionPhase _phaseBeforeModal = SessionPhase.Active;
    private int _modalDepth;
    private bool _ending;

    public SessionCoordinator(AppServices services, ISessionUi ui, Action<Action> postToUi)
    {
        _services = services;
        _ui = ui;
        _postToUi = postToUi;
        _services.Platform.Hotkeys.Pressed += OnHotkey;
    }

    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;

    public bool IsRunning => _actor is not null;

    /// <summary>Latest engine snapshot (updated from the actor thread).</summary>
    public SessionStatus? Status { get; private set; }

    /// <summary>True from an error until the next successful action, so the tray can show it.</summary>
    public bool HasError { get; private set; }

    /// <summary>Starts a session. Returns the hotkeys that couldn't be registered; if any, nothing was started.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyFailure> Start(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsRunning)
        {
            throw new InvalidOperationException("A session is already running.");
        }

        var failures = _services.Platform.Hotkeys.Apply(HotkeyPlan.For(SessionPhase.Active, _services.Settings.Hotkeys));
        if (failures.Count > 0)
        {
            _services.Platform.Hotkeys.Apply(new Dictionary<HotkeyAction, KeyChord>());
            return failures;
        }

        _engine = new SessionEngine(
            options,
            _services.Platform.Capture,
            _services.Platform.RecycleBin,
            _services.Platform.Clipboard,
            this,
            this,
            _services.Renumber,
            TimeProvider.System,
            _services.Log);
        _engine.StatusChanged += OnStatusChanged;
        _actor = new SessionActor(_engine, this, _services.Log);
        Phase = SessionPhase.Active;
        _ending = false;
        HasError = false;
        Status = _engine.Status;
        _services.Log.Info($"Session started in {options.Folder} ({options.Mode}, main {options.Main}, sub {options.Sub}).");
        _actor.Post(engine => engine.Start());
        _ui.SessionStarted();
        _ui.StateChanged();
        return failures;
    }

    /// <summary>Ends the session: stops hotkeys, drains queued shots, then hands the session's files to the UI.</summary>
    public async Task EndAsync()
    {
        if (_actor is null || _engine is null || _ending)
        {
            return;
        }

        _ending = true;
        ApplyPhase(SessionPhase.Idle);

        // A queued shot may be waiting on an open prompt (e.g. Exit chosen from the tray mid-prompt). Answer it (discard) so draining
        // the queue can finish instead of waiting forever for a dialog nobody will see again. A collision prompt for a shot still queued
        // opens normally (ending drains queued shots, so the user answers it); a caption box doesn't (see CaptionAsync).
        if (_modalDepth > 0)
        {
            _ui.CloseOpenPrompts();
        }

        await _actor.CompleteAsync().ConfigureAwait(true);
        var files = _engine.End();
        _engine.StatusChanged -= OnStatusChanged;
        await _actor.DisposeAsync().ConfigureAwait(true);
        var summary = new SessionSummary(_engine.Folder, files);
        _actor = null;
        _engine = null;
        _services.Log.Info($"Session ended with {files.Count} file(s).");
        _ui.SessionEnded(summary);
        _ui.StateChanged();
    }

    /// <summary>
    /// Another copy of the app was launched during the session. The window stays hidden while a session runs (the tray is the control
    /// surface), so this says why nothing opened. A warning: it uses the warning sound and doesn't replace a lingering "paused" toast.
    /// </summary>
    public void ReportSecondLaunch()
    {
        if (IsRunning)
        {
            Notify(new FeedbackEvent(
                FeedbackKind.Warning,
                $"Screenshot Helper is already running a session — use the tray icon, or {_services.Describe(HotkeyAction.EndSession)} to end it"));
        }
    }

    public void TogglePause()
    {
        if (Phase is not (SessionPhase.Active or SessionPhase.Paused))
        {
            return;
        }

        var pausing = Phase == SessionPhase.Active;
        ApplyPhase(pausing ? SessionPhase.Paused : SessionPhase.Active);
        Notify(new FeedbackEvent(
            pausing ? FeedbackKind.Paused : FeedbackKind.Resumed,
            pausing ? $"⏸ Paused — press {_services.Describe(HotkeyAction.PauseResume)} to resume" : "▶ Resumed"));
    }

    /// <summary>
    /// Opens the caption box with capture keys released, then applies the caption to the last shot or arms it for the next one
    /// (per the annotation setting). Nothing is captured here: only the main/sub keys ever take a screenshot.
    /// </summary>
    public async Task CaptionAsync()
    {
        if (_actor is null || Phase != SessionPhase.Active)
        {
            return;
        }

        EnterModal();
        try
        {
            // Queued after any shots already pressed, so "last shot" really is the latest one.
            var request = await _actor.InvokeAsync(engine => engine.PrepareCaption()).ConfigureAwait(true);

            // The session began ending while the request was queued: the caption could no longer be applied, so don't open the box.
            if (request is null || _ending)
            {
                return;
            }

            var text = await _ui.AskCaptionAsync(request).ConfigureAwait(true);
            _actor?.Post(engine => engine.SetCaption(text));
        }
        finally
        {
            LeaveModal();
        }
    }

    public void Undo() => _actor?.Post(engine => engine.Undo());

    public void SetCaptureTarget(CaptureTarget target) => _actor?.Post(engine => engine.SetCaptureTarget(target));

    /// <summary>Collision prompt, called by the engine on the actor thread; shown on the UI thread with hotkeys released.</summary>
    public Task<SubCollisionAnswer> AskAsync(SubCollision collision, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SubCollisionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _postToUi(async () =>
        {
            EnterModal();
            try
            {
                completion.TrySetResult(await _ui.AskCollisionAsync(collision).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                _services.Log.Error("Collision prompt failed; appending instead.", ex);
                completion.TrySetResult(new SubCollisionAnswer(SubCollisionChoice.Append, false));
            }
            finally
            {
                LeaveModal();
            }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Feedback from the engine (actor thread) or the coordinator (UI thread): sound immediately, toast/tray on the UI thread.</summary>
    public void Notify(FeedbackEvent feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var settings = _services.Settings;
        if (settings.SoundsEnabled && !settings.MutedSounds.Contains(feedback.Kind))
        {
            try
            {
                _services.Platform.Sound.Play(_services.Sounds[feedback.Kind]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _services.Log.Warn("Playing a feedback sound failed.", ex);
            }
        }

        _postToUi(() =>
        {
            HasError = feedback.Kind == FeedbackKind.Error || (HasError && feedback.Kind is not (FeedbackKind.MainSaved or FeedbackKind.SubSaved or FeedbackKind.SessionStarted));
            if (settings.ToastsEnabled)
            {
                _ui.ShowToast(feedback);
            }

            _ui.StateChanged();
        });
    }

    public async ValueTask DisposeAsync()
    {
        _services.Platform.Hotkeys.Pressed -= OnHotkey;
        if (IsRunning)
        {
            await EndAsync().ConfigureAwait(true);
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        // Runs on the hotkey thread: shot/timestamp/undo go straight into the actor queue (no UI hop, strict order);
        // actions that change phase or show UI are marshalled to the UI thread.
        var actor = _actor;
        if (actor is null)
        {
            return;
        }

        switch (action)
        {
            case HotkeyAction.MainShot:
                actor.Post((engine, ct) => engine.TakeShotAsync(ShotKind.Main, ct));
                break;
            case HotkeyAction.SubShot:
                actor.Post((engine, ct) => engine.TakeShotAsync(ShotKind.Sub, ct));
                break;
            case HotkeyAction.ToggleTimestamp:
                actor.Post(engine => engine.ToggleTimestamp());
                break;
            case HotkeyAction.Undo:
                actor.Post(engine => engine.Undo());
                break;
            case HotkeyAction.PauseResume:
                _postToUi(TogglePause);
                break;
            case HotkeyAction.Caption:
                _postToUi(() => _ = CaptionAsync());
                break;
            case HotkeyAction.EndSession:
                _postToUi(() => _ = EndAsync());
                break;
        }
    }

    private void OnStatusChanged(SessionStatus status)
    {
        Status = status;
        _postToUi(_ui.StateChanged);
    }

    // A caption box and a collision prompt can overlap (a queued shot collides while the caption box is open), so modality is counted
    // and keys come back only when the last modal closes.
    private void EnterModal()
    {
        if (_modalDepth++ == 0)
        {
            _phaseBeforeModal = Phase;
            ApplyPhase(SessionPhase.Modal);
        }
    }

    private void LeaveModal()
    {
        if (--_modalDepth == 0 && !_ending && IsRunning)
        {
            ApplyPhase(_phaseBeforeModal);
        }
    }

    private void ApplyPhase(SessionPhase phase)
    {
        Phase = phase;
        var failures = _services.Platform.Hotkeys.Apply(HotkeyPlan.For(phase, _services.Settings.Hotkeys));
        if (failures.Count > 0)
        {
            // Another app grabbed a chord mid-session (rare): say so rather than silently losing a key.
            var names = string.Join(", ", failures.Keys);
            _services.Log.Warn($"Re-registering hotkeys failed for: {names}.");
            Notify(new FeedbackEvent(FeedbackKind.Warning, $"Some keys are in use by another app: {names}"));
        }

        _ui.StateChanged();
    }
}
