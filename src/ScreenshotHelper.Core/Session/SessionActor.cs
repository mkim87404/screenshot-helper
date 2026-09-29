using System.Threading.Channels;
using ScreenshotHelper.Core.Diagnostics;
using ScreenshotHelper.Core.Feedback;

namespace ScreenshotHelper.Core.Session;

/// <summary>
/// Serialises all work on a <see cref="SessionEngine"/> through one channel consumer, so hotkeys pressed in quick succession are
/// processed strictly in order and the engine never needs locks. Posting is non-blocking (safe from the hotkey thread).
/// </summary>
public sealed class SessionActor : IAsyncDisposable
{
    private readonly Channel<Func<SessionEngine, CancellationToken, Task>> _queue =
        Channel.CreateUnbounded<Func<SessionEngine, CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SessionEngine _engine;
    private readonly IFeedbackSink _feedback;
    private readonly AppLog _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    public SessionActor(SessionEngine engine, IFeedbackSink feedback, AppLog log)
    {
        _engine = engine;
        _feedback = feedback;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Queues work; returns false once the actor is completing.</summary>
    public bool Post(Func<SessionEngine, CancellationToken, Task> work) => _queue.Writer.TryWrite(work);

    public bool Post(Action<SessionEngine> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Post((engine, _) =>
        {
            work(engine);
            return Task.CompletedTask;
        });
    }

    /// <summary>Queues work and awaits its result (runs after everything already queued).</summary>
    public Task<T> InvokeAsync<T>(Func<SessionEngine, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = Post((engine, _) =>
        {
            try
            {
                completion.SetResult(work(engine));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }

            return Task.CompletedTask;
        });
        if (!posted)
        {
            completion.SetException(new InvalidOperationException("The session has ended."));
        }

        return completion.Task;
    }

    /// <summary>Stops accepting work, drains what is queued (so no pressed shot is lost), then stops.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work(_engine, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed command must not kill the session: log it, tell the user, keep processing.
                _log.Error("Unhandled error in session command.", ex);
                _feedback.Notify(new FeedbackEvent(FeedbackKind.Error, "Something went wrong — see the log for details"));
            }
        }
    }
}
