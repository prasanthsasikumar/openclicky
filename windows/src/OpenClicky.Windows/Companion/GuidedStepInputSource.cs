// The OS side of GuidedStepWatcher (GuidedSteps.swift's global monitor): clicks (left button up)
// from a WH_MOUSE_LL hook on its own message-loop thread, and key downs from the keyboard monitor
// that already runs for the shortcuts. Only while a step waits; OpenClicky's own windows don't count
// (a click on the island, keys typed into the composer).

using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Companion;

public sealed class GuidedStepInputSource : IGuidedStepInputSource, IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP = 0x0202;

    private readonly IGlobalKeyboardMonitor keyboardMonitor;
    private readonly int ownProcessId = Environment.ProcessId;
    private readonly object lifecycleLock = new();
    private MessageLoopThread? hookThread;
    private LowLevelKeyboardProc? mouseProcedure;
    private nint mouseHook;

    public event Action<GuidedStepInput, ScreenPoint?>? InputReceived;

    public GuidedStepInputSource(IGlobalKeyboardMonitor keyboardMonitor)
    {
        this.keyboardMonitor = keyboardMonitor;
    }

    public void Start()
    {
        lock (lifecycleLock)
        {
            if (mouseHook != 0) return;
            keyboardMonitor.KeyEvent += OnKeyEvent;
            hookThread = new MessageLoopThread("OpenClicky guided-step mouse hook");
            mouseProcedure = MouseCallback;
            mouseHook = hookThread.Invoke(() => SetWindowsHookEx(WH_MOUSE_LL, mouseProcedure!, GetModuleHandle(null), 0));
            if (mouseHook == 0) Core.Configuration.AppLog.Write($"guide: couldn't install the mouse hook (error {Marshal.GetLastPInvokeError()})");
        }
    }

    public void Stop()
    {
        lock (lifecycleLock)
        {
            keyboardMonitor.KeyEvent -= OnKeyEvent;
            if (mouseHook != 0 && hookThread is not null)
            {
                var hook = mouseHook;
                hookThread.Invoke(() => UnhookWindowsHookEx(hook));
            }
            mouseHook = 0;
            hookThread?.Dispose();
            hookThread = null;
            mouseProcedure = null;
        }
    }

    private nint MouseCallback(int code, nint message, nint data)
    {
        if (code >= 0 && message == WM_LBUTTONUP)
        {
            var point = Marshal.PtrToStructure<POINT>(data);
            // The hook must return quickly: the check and the event go to the thread pool.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (IsOwnWindow(WindowFromPoint(point))) return;
                // The hook's point is in physical pixels (the process is per-monitor DPI aware), the
                // space the pointer's own spot is kept in, so the two can be compared.
                InputReceived?.Invoke(GuidedStepInputMapping.ForLeftMouseUp(), new ScreenPoint(point.X, point.Y));
            });
        }
        return CallNextHookEx(0, code, message, data);
    }

    private void OnKeyEvent(RawKeyEvent keyEvent)
    {
        if (!keyEvent.IsDown || keyEvent.IsRepeat) return;
        if (IsOwnWindow(GetForegroundWindow())) return;
        InputReceived?.Invoke(GuidedStepInputMapping.ForKeyDown(keyEvent.VirtualKey), null);
    }

    private bool IsOwnWindow(nint window)
    {
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == ownProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    public void Dispose() => Stop();
}
