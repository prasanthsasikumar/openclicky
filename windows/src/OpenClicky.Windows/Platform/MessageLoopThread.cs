using System.Collections.Concurrent;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// A dedicated background thread running a Win32 message loop, optionally with a message-only
/// window (HWND_MESSAGE). Hooks (WH_KEYBOARD_LL, WinEvent) and clipboard listeners call back on the
/// thread that installed them, and only while that thread pumps messages, so each of them gets one.
/// Work is run on the thread with <see cref="Invoke{T}"/>.
/// </summary>
internal sealed class MessageLoopThread : IDisposable
{
    private const uint RunQueuedWorkMessage = WM_USER + 1;

    private readonly ConcurrentQueue<Action> queuedWork = new();
    private readonly ManualResetEventSlim started = new();
    private readonly Thread thread;
    private readonly WndProc? windowProcedure;
    private readonly Func<nint, uint, nint, nint, nint?>? messageHandler;
    private uint threadId;
    private bool disposed;

    /// <param name="messageHandler">Called for the message-only window's messages; return a value to handle one, null to pass it on. Null = no window.</param>
    public MessageLoopThread(string name, Func<nint, uint, nint, nint, nint?>? messageHandler = null, ThreadPriority priority = ThreadPriority.Normal)
    {
        this.messageHandler = messageHandler;
        if (messageHandler is not null) windowProcedure = WindowProcedure;
        thread = new Thread(Run) { IsBackground = true, Name = name, Priority = priority };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Wait();
    }

    /// <summary>The message-only window (0 when none was asked for or it could not be created).</summary>
    public nint WindowHandle { get; private set; }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == thread.ManagedThreadId;

    /// <summary>Runs <paramref name="work"/> on the loop thread and waits for it.</summary>
    public T Invoke<T>(Func<T> work)
    {
        if (IsCurrentThread) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queuedWork.Enqueue(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        Wake();
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action work) => Invoke(() => { work(); return true; });

    private void Wake()
    {
        if (WindowHandle != 0) PostMessage(WindowHandle, RunQueuedWorkMessage, 0, 0);
        else PostThreadMessage(threadId, RunQueuedWorkMessage, 0, 0);
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        if (windowProcedure is not null)
        {
            var className = "OpenClicky.MessageWindow." + Guid.NewGuid().ToString("N");
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(windowProcedure),
                hInstance = GetModuleHandle(null),
                lpszClassName = className,
            };
            if (RegisterClassEx(ref windowClass) != 0)
            {
                WindowHandle = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, windowClass.hInstance, 0);
            }
        }
        // Make sure the thread has a message queue before anyone posts to it.
        PeekQueueIntoExistence();
        started.Set();

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.message == RunQueuedWorkMessage)
            {
                RunQueuedWork();
                continue;
            }
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        RunQueuedWork();
        if (WindowHandle != 0) DestroyWindow(WindowHandle);
    }

    private static void PeekQueueIntoExistence()
    {
        // PeekMessage(PM_NOREMOVE) creates the thread's message queue (PostThreadMessage needs one).
        PeekMessage(out _, 0, 0, 0, 0);
    }

    private void RunQueuedWork()
    {
        while (queuedWork.TryDequeue(out var work))
        {
            work();
        }
    }

    private nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == RunQueuedWorkMessage)
        {
            RunQueuedWork();
            return 0;
        }
        var handled = messageHandler?.Invoke(windowHandle, message, wParam, lParam);
        return handled ?? DefWindowProc(windowHandle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PostThreadMessage(threadId, WM_QUIT, 0, 0);
        if (!IsCurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
    }
}
