using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using static ScreenshotHelper.Platform.Windows.Interop.NativeMethods;

namespace ScreenshotHelper.Platform.Windows.Interop;

/// <summary>
/// A dedicated STA thread running a Win32 message loop, with a message-only window as a handle for APIs that need an owner (clipboard).
/// Thread-affine Win32 calls (RegisterHotKey, clipboard) are marshalled onto it; thread-posted messages (WM_HOTKEY) are surfaced as an event.
/// </summary>
internal sealed class MessageThread : IDisposable
{
    private const uint WM_INVOKE = WM_APP + 1;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _disposed;

    public MessageThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Raised on the message thread for messages posted to the thread (hwnd == 0), e.g. WM_HOTKEY.</summary>
    public event Action<uint, IntPtr, IntPtr>? ThreadMessage;

    /// <summary>The message-only window owned by this thread.</summary>
    public IntPtr WindowHandle { get; private set; }

    /// <summary>Runs <paramref name="work"/> on the message thread and waits for it (rethrowing its exception here).</summary>
    public T Invoke<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return work();
        }

        T result = default!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });
        if (!PostThreadMessage(_threadId, WM_INVOKE, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("The message thread is not accepting work.");
        }

        done.Wait();
        error?.Throw();
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        // PeekMessage forces the OS to create this thread's message queue before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        WindowHandle = CreateWindowEx(0, "STATIC", "ScreenshotHelper.MessageWindow", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ready.Set();

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_INVOKE)
                {
                    while (_work.TryDequeue(out var work))
                    {
                        work();
                    }
                }
                else if (msg.hwnd == IntPtr.Zero)
                {
                    ThreadMessage?.Invoke(msg.message, msg.wParam, msg.lParam);
                }
                else
                {
                    TranslateMessage(in msg);
                    DispatchMessage(in msg);
                }
            }
        }
        finally
        {
            // Unblock anyone still waiting on queued work, then release the window on its owning thread.
            while (_work.TryDequeue(out var work))
            {
                work();
            }

            if (WindowHandle != IntPtr.Zero)
            {
                DestroyWindow(WindowHandle);
            }
        }
    }
}
