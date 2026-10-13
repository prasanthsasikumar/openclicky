using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Types text into the app in front: clipboard + Ctrl+V with the clipboard restored 0.6 s later,
/// or live typing as KEYEVENTF_UNICODE keystrokes. Every injected event carries
/// <see cref="LiveTypingMarker"/> in dwExtraInfo so OpenClicky's own keyboard hook ignores it.
/// </summary>
public sealed class FrontAppTextInserter : ITextInserter
{
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromSeconds(0.6);

    private static readonly int[] ModifiersReleasedBeforePaste =
    [
        VirtualKeys.LeftShift, VirtualKeys.RightShift, VirtualKeys.LeftAlt, VirtualKeys.RightAlt,
        VirtualKeys.LeftWindows, VirtualKeys.RightWindows,
    ];

    private static readonly int[] ModifiersReleasedBeforeTyping =
    [
        VirtualKeys.LeftControl, VirtualKeys.RightControl, VirtualKeys.LeftAlt, VirtualKeys.RightAlt,
        VirtualKeys.LeftWindows, VirtualKeys.RightWindows,
    ];

    private readonly IClipboard clipboard;
    private readonly object restoreLock = new();
    private CancellationTokenSource? pendingClipboardRestore;
    private long lastInsertedAtTicks = DateTimeOffset.MinValue.UtcTicks;

    public FrontAppTextInserter(IClipboard clipboard)
    {
        this.clipboard = clipboard;
    }

    public DateTimeOffset LastInsertedAt => new(Interlocked.Read(ref lastInsertedAtTicks), TimeSpan.Zero);

    public TextInsertionOutcome Insert(string text)
    {
        var previousClipboard = clipboard.Snapshot();
        MarkInserted();
        CancelPendingRestore();
        if (!clipboard.SetText(text, excludeFromHistory: true)) return TextInsertionOutcome.LeftOnClipboard;

        if (!CanSendInputToForeground()) return TextInsertionOutcome.LeftOnClipboard;
        var inputs = new List<INPUT>();
        AddModifierRelease(inputs, ModifiersReleasedBeforePaste);
        var controlWasDown = IsPhysicallyDown(VirtualKeys.LeftControl) || IsPhysicallyDown(VirtualKeys.RightControl);
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: false));
        inputs.Add(VirtualKeyInput(VirtualKeys.V, keyUp: false));
        inputs.Add(VirtualKeyInput(VirtualKeys.V, keyUp: true));
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: true));
        if (Send([.. inputs]) != inputs.Count) return TextInsertionOutcome.LeftOnClipboard;

        // Give the front app time to read the clipboard before the person's clipboard comes back.
        var restore = new CancellationTokenSource();
        lock (restoreLock) pendingClipboardRestore = restore;
        _ = RestoreLaterAsync(text, previousClipboard, restore.Token);
        return TextInsertionOutcome.Typed;
    }

    public void CopyToClipboard(string text)
    {
        CancelPendingRestore();
        MarkInserted();
        clipboard.SetText(text);
    }

    public bool SelectAll()
    {
        if (!CanSendInputToForeground()) return false;
        var inputs = new List<INPUT>();
        AddModifierRelease(inputs, ModifiersReleasedBeforePaste);
        var controlWasDown = IsPhysicallyDown(VirtualKeys.LeftControl) || IsPhysicallyDown(VirtualKeys.RightControl);
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: false));
        inputs.Add(VirtualKeyInput(VirtualKeys.A, keyUp: false));
        inputs.Add(VirtualKeyInput(VirtualKeys.A, keyUp: true));
        if (!controlWasDown) inputs.Add(VirtualKeyInput(VirtualKeys.LeftControl, keyUp: true));
        return Send([.. inputs]) == inputs.Count;
    }

    public bool TypeAsKeystrokes(string text)
    {
        if (text.Length == 0) return true;
        if (!CanSendInputToForeground()) return false;

        // Ctrl/Alt/Win + a unicode key can be an app shortcut; the dictation key (Right Ctrl, Right Alt)
        // is usually still held, so it is released for the system first (see ITextInserter).
        var release = new List<INPUT>();
        AddModifierRelease(release, ModifiersReleasedBeforeTyping);
        if (release.Count > 0 && Send([.. release]) != release.Count) return false;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var chunk in LiveTypingChunker.KeyboardChunks(normalized))
        {
            var inputs = new List<INPUT>(chunk.Length * 2);
            foreach (var unit in chunk)
            {
                if (unit == '\n')
                {
                    inputs.Add(VirtualKeyInput(VirtualKeys.Return, keyUp: false));
                    inputs.Add(VirtualKeyInput(VirtualKeys.Return, keyUp: true));
                    continue;
                }
                if (unit == '\t')
                {
                    inputs.Add(VirtualKeyInput(VirtualKeys.Tab, keyUp: false));
                    inputs.Add(VirtualKeyInput(VirtualKeys.Tab, keyUp: true));
                    continue;
                }
                inputs.Add(KeyboardInput(0, unit, KEYEVENTF_UNICODE));
                inputs.Add(KeyboardInput(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            if (Send([.. inputs]) != inputs.Count) return false;
        }
        return true;
    }

    /// <summary>
    /// Key-ups for the listed modifiers that are physically down. When Alt or Win is among them the
    /// unassigned key is tapped first so their release opens neither the menu bar nor Start.
    /// </summary>
    private static void AddModifierRelease(List<INPUT> inputs, int[] modifiers)
    {
        var held = modifiers.Where(IsPhysicallyDown).ToList();
        if (held.Count == 0) return;
        if (held.Any(key => VirtualKeys.IsAlt(key) || VirtualKeys.IsWindows(key)))
        {
            inputs.Add(VirtualKeyInput(VirtualKeys.Unassigned, keyUp: false));
            inputs.Add(VirtualKeyInput(VirtualKeys.Unassigned, keyUp: true));
        }
        foreach (var key in held) inputs.Add(VirtualKeyInput(key, keyUp: true));
    }

    private static bool IsPhysicallyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// False when nothing is in front or the window in front belongs to an elevated process: UIPI
    /// drops input from this unelevated process without telling anyone, so the text stays on the clipboard.
    /// </summary>
    private static bool CanSendInputToForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        var processId = ProcessIdOf(foreground);
        if (processId == 0) return false;
        if (processId == Environment.ProcessId) return true;
        return IsProcessElevated(processId) != true || IsProcessElevated(Environment.ProcessId) == true;
    }

    private async Task RestoreLaterAsync(string pastedText, ClipboardSnapshot previousClipboard, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ClipboardRestoreDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested) return;
        // Only if the clipboard still holds the pasted words (the person may have copied something since).
        if (clipboard.GetText() != pastedText) return;
        // An empty snapshot empties the clipboard, as the Mac clears the pasteboard.
        clipboard.Restore(previousClipboard);
    }

    private void CancelPendingRestore()
    {
        lock (restoreLock)
        {
            pendingClipboardRestore?.Cancel();
            pendingClipboardRestore = null;
        }
    }

    private void MarkInserted() => Interlocked.Exchange(ref lastInsertedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
}
