using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// WH_KEYBOARD_LL hook on its own thread with its own message loop (the Mac's listen-only CGEvent
/// tap). The callback does nothing slow: it reads the struct, updates the key table, decides
/// whether to swallow from an array lookup, queues the event and returns, so it stays far inside
/// LowLevelHooksTimeout (Windows silently removes a hook that is slow too often). Events are
/// raised in order on a separate dispatch thread.
/// </summary>
public sealed class GlobalKeyboardMonitor : IGlobalKeyboardMonitor
{
    private readonly object lifecycleLock = new();
    private readonly bool[] keyIsDown = new bool[256];
    private volatile bool[] swallowedKeys = new bool[256];
    private MessageLoopThread? hookThread;
    private LowLevelKeyboardProc? hookProcedure;
    private nint hookHandle;
    private Channel<RawKeyEvent>? pendingEvents;
    private Thread? dispatchThread;

    public event Action<RawKeyEvent>? KeyEvent;

    public bool IsRunning => hookHandle != 0;

    public void Start()
    {
        lock (lifecycleLock)
        {
            // Already running: keep the hook (restarting would drop a press in progress, as on the Mac).
            if (hookHandle != 0) return;

            pendingEvents = Channel.CreateUnbounded<RawKeyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var reader = pendingEvents.Reader;
            dispatchThread = new Thread(() => DispatchEvents(reader)) { IsBackground = true, Name = "OpenClicky keyboard events" };
            dispatchThread.Start();

            hookThread = new MessageLoopThread("OpenClicky keyboard hook", priority: ThreadPriority.Highest);
            hookProcedure = HookCallback;
            hookHandle = hookThread.Invoke(() => SetWindowsHookEx(WH_KEYBOARD_LL, hookProcedure!, GetModuleHandle(null), 0));
            if (hookHandle == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                StopLocked();
                throw new InvalidOperationException($"couldn't install the keyboard hook (error {error})");
            }
        }
    }

    public void Stop()
    {
        lock (lifecycleLock) StopLocked();
    }

    private void StopLocked()
    {
        if (hookHandle != 0 && hookThread is not null)
        {
            var handle = hookHandle;
            hookThread.Invoke(() => UnhookWindowsHookEx(handle));
        }
        hookHandle = 0;
        hookThread?.Dispose();
        hookThread = null;
        pendingEvents?.Writer.TryComplete();
        pendingEvents = null;
        dispatchThread = null;
        Array.Clear(keyIsDown);
    }

    public void SetSwallowedKeys(IReadOnlyCollection<int> virtualKeys)
    {
        var table = new bool[256];
        foreach (var virtualKey in virtualKeys)
        {
            if (virtualKey is > 0 and < 256) table[virtualKey] = true;
        }
        swallowedKeys = table;
    }

    public bool IsKeyDown(int virtualKey)
    {
        if (virtualKey is > 0 and < 256 && keyIsDown[virtualKey]) return true;
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    public void SuppressMenuActivation() =>
        Send(VirtualKeyInput(VirtualKeys.Unassigned, keyUp: false), VirtualKeyInput(VirtualKeys.Unassigned, keyUp: true));

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(hookHandle, code, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Keystrokes OpenClicky typed itself (live typing, the synthetic paste) are not the person pressing keys.
        if ((long)data.dwExtraInfo == LiveTypingMarker.Value) return CallNextHookEx(hookHandle, code, wParam, lParam);

        var virtualKey = (int)data.vkCode;
        var isDown = (data.flags & LLKHF_UP) == 0;
        var isRepeat = false;
        if (virtualKey is > 0 and < 256)
        {
            isRepeat = isDown && keyIsDown[virtualKey];
            keyIsDown[virtualKey] = isDown;
        }
        var swallow = virtualKey is > 0 and < 256 && swallowedKeys[virtualKey];

        pendingEvents?.Writer.TryWrite(new RawKeyEvent(
            VirtualKey: virtualKey,
            ScanCode: (int)data.scanCode,
            IsDown: isDown,
            IsRepeat: isRepeat,
            IsExtended: (data.flags & LLKHF_EXTENDED) != 0,
            Side: VirtualKeys.SideOf(virtualKey),
            IsInjected: (data.flags & LLKHF_INJECTED) != 0,
            IsAltDown: (data.flags & LLKHF_ALTDOWN) != 0,
            ExtraInfo: (long)data.dwExtraInfo,
            TimestampMilliseconds: data.time,
            UptimeSeconds: Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            WasSwallowed: swallow));

        return swallow ? 1 : CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    private void DispatchEvents(ChannelReader<RawKeyEvent> reader)
    {
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var keyEvent))
            {
                try
                {
                    KeyEvent?.Invoke(keyEvent);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"keyboard monitor handler failed: {exception}");
                }
            }
        }
    }

    public void Dispose() => Stop();
}
