// Holding a mouse button to talk (Core/Shortcuts/MouseTalkButton): a low-level mouse hook, installed only while a
// button is chosen (a global mouse hook sits in the path of every mouse event), on its own message-loop thread like
// the keyboard hook. The chosen button's presses are swallowed and reported as the talk key going down and up;
// every other mouse event passes straight on. Injected events (another app replaying clicks) are ignored.

using System.Runtime.InteropServices;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Shortcuts;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

public sealed class MouseTalkMonitor : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x00000001;

    private readonly object lifecycleLock = new();
    private readonly Action<bool> onTalk;
    private volatile MouseTalkButton button = MouseTalkButton.Off;
    private MessageLoopThread? hookThread;
    private LowLevelMouseProc? hookProcedure;
    private nint hookHandle;
    private bool isDown;

    /// <param name="onTalk">The talk key going down (true) or up (false); called on the hook thread, so it must only post.</param>
    public MouseTalkMonitor(Action<bool> onTalk)
    {
        this.onTalk = onTalk;
    }

    /// <summary>The chosen button; Off removes the hook.</summary>
    public MouseTalkButton Button
    {
        get => button;
        set
        {
            lock (lifecycleLock)
            {
                if (button == value) return;
                // Let go of a talk in progress before the button changes under it.
                if (isDown) { isDown = false; onTalk(false); }
                button = value;
                if (value == MouseTalkButton.Off) StopLocked();
                else StartLocked();
            }
        }
    }

    private void StartLocked()
    {
        if (hookHandle != 0) return;
        hookThread = new MessageLoopThread("OpenClicky mouse talk hook", priority: ThreadPriority.Highest);
        hookProcedure = HookCallback;
        hookHandle = hookThread.Invoke(() => SetMouseHook(WH_MOUSE_LL, hookProcedure!, GetModuleHandle(null), 0));
        if (hookHandle == 0)
        {
            AppLog.Write($"mouse talk: couldn't install the mouse hook (error {Marshal.GetLastPInvokeError()})");
            StopLocked();
            return;
        }
        AppLog.Write($"mouse talk: listening for the {button.RawValue()} button");
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
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0 && MouseTalkButtons.Read((int)wParam, info.mouseData) is { } change && change.Button == button)
            {
                if (change.IsDown != isDown)
                {
                    isDown = change.IsDown;
                    onTalk(change.IsDown);
                }
                return 1; // swallowed: holding "back" to talk doesn't also go back a page
            }
        }
        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        lock (lifecycleLock) StopLocked();
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetMouseHook(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);
}
