// press_keys, type_text and window (2026-11-01): keys and shortcuts sent to the window in front with SendInput (the
// closed set Core's KeyChords allows), words typed where the keyboard is through the same inserter dictation uses
// (never into a password box), and a window minimized, maximized, restored, closed or brought to the front by app.

using System.Runtime.InteropServices;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Configuration;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

public sealed partial class DesktopActionExecutor
{
    /// <summary>Between chords, so an app that reacts to the first (a menu opening, focus moving) is ready for the next.</summary>
    private static readonly TimeSpan BetweenChords = TimeSpan.FromMilliseconds(60);

    public KeyInputResult PressKeys(IReadOnlyList<KeyChord> chords, int times)
    {
        var window = GetForegroundWindow();
        if (window == 0 || ProcessIdOf(window) == Environment.ProcessId) return KeyInputResult.NoWindow;
        if (!FrontAppTextInserter.CanSendInputToForeground()) return KeyInputResult.NotAllowed;
        // The talk keys (Ctrl + Win) may still be down as the answer arrives: let them go first.
        var release = new List<INPUT>();
        FrontAppTextInserter.AddModifierRelease(release, FrontAppTextInserter.ModifiersReleasedBeforePaste);
        if (release.Count > 0) Send([.. release]);
        for (var round = 0; round < times; round++)
        {
            foreach (var chord in chords)
            {
                var inputs = new List<INPUT>();
                foreach (var modifier in chord.Modifiers) inputs.Add(VirtualKeyInput(modifier, keyUp: false));
                inputs.Add(VirtualKeyInput(chord.Key, keyUp: false));
                inputs.Add(VirtualKeyInput(chord.Key, keyUp: true));
                foreach (var modifier in chord.Modifiers.Reverse()) inputs.Add(VirtualKeyInput(modifier, keyUp: true));
                if (Send([.. inputs]) != inputs.Count) return KeyInputResult.NotAllowed;
                Thread.Sleep(BetweenChords);
            }
        }
        AppLog.Write($"mac action: pressed {string.Join(", ", chords.Select(chord => chord.Spoken))} ×{times}");
        return KeyInputResult.Sent;
    }

    public async Task<KeyInputResult> TypeTextAsync(string text)
    {
        var window = GetForegroundWindow();
        if (window == 0 || ProcessIdOf(window) == Environment.ProcessId || textInserter is null) return KeyInputResult.NoWindow;
        if (!FrontAppTextInserter.CanSendInputToForeground()) return KeyInputResult.NotAllowed;
        if (focusedElements is not null && await focusedElements.FocusedTextTargetAsync().ConfigureAwait(false) is { IsSecure: true })
        {
            return KeyInputResult.PasswordField;
        }
        return textInserter.Insert(text) == Core.Platform.TextInsertionOutcome.Typed ? KeyInputResult.Sent : KeyInputResult.NotAllowed;
    }

    public WindowControlResult ControlWindow(WindowCommand command, string? app)
    {
        nint window;
        if (app is null)
        {
            window = GetForegroundWindow();
            if (window == 0 || ProcessIdOf(window) == Environment.ProcessId) return new WindowControlResult(null);
        }
        else
        {
            var processIds = WindowMinimizer.ProcessIdsOfAppsNamed(app, RunningAppNames());
            if (processIds.Count == 0) return new WindowControlResult(null, AppNotRunning: true);
            window = TopLevelWindowsOf(processIds, minimizedOnly: false).FirstOrDefault();
            if (window == 0) return new WindowControlResult(null, AppNotRunning: true);
        }
        var name = WindowName(window);
        switch (command)
        {
            case WindowCommand.Minimize:
                ShowWindowAsync(window, SW_MINIMIZE);
                break;
            case WindowCommand.Maximize:
                ShowWindowAsync(window, SW_MAXIMIZE);
                ForceForeground(window);
                break;
            case WindowCommand.Restore:
                ShowWindowAsync(window, SW_RESTORE);
                ForceForeground(window);
                break;
            case WindowCommand.Close:
                // Asks the app to close, as its ✕ does: it can still ask to save.
                PostMessage(window, WM_CLOSE, 0, 0);
                break;
            default:
                if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE);
                ForceForeground(window);
                break;
        }
        AppLog.Write($"mac action: window {command.RawValue()} on {name}");
        return new WindowControlResult(name);
    }

    /// <summary>The app's name ("Google Chrome") for the sentence, else the window's title.</summary>
    private static string WindowName(nint window)
    {
        var description = ProcessDescriber.Describe(ProcessIdOf(window));
        // "Notepad.exe" when the app has no friendlier name: said as "Notepad".
        if (description.DisplayName is { Length: > 0 } displayName)
        {
            return displayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? displayName[..^4] : displayName;
        }
        var length = GetWindowTextLength(window);
        var title = new System.Text.StringBuilder(Math.Max(1, length + 1));
        GetWindowText(window, title, title.Capacity);
        return title.Length > 0 ? title.ToString() : description.ImageName ?? "the window";
    }

    /// <summary>Windows gives the foreground only to the thread that has input: join the front window's input queue for the call.</summary>
    private static void ForceForeground(nint window)
    {
        var front = GetForegroundWindow();
        var frontThread = front == 0 ? 0 : GetWindowThreadProcessId(front, out _);
        var ownThread = GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != ownThread && AttachThreadInput(ownThread, frontThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(ownThread, frontThread, false);
        }
    }

    private const int SW_MAXIMIZE = 3;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);
}
