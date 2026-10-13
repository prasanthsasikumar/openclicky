// Port of GlobalPushToTalkShortcutMonitor.swift's glue: the low-level keyboard hook (Platform's
// GlobalKeyboardMonitor, the Mac's listen-only CGEvent tap) feeds CompanionShortcutRecognizer, and
// the shortcuts it completes are published on the UI thread: talk press/release as a transition,
// everything else as a companion shortcut event.
//
// The recogniser runs on the monitor's dispatch thread, in event order. The modifiers it reads are
// the physical ones held after each event, tracked here from the hook's own events (the hook
// reports left and right separately). Our own keystrokes never arrive (the monitor drops the marker).

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Windows.Companion;

public sealed class GlobalShortcutMonitor : ICompanionShortcutSource, IDisposable
{
    private readonly IGlobalKeyboardMonitor keyboardMonitor;
    private readonly IMainThread mainThread;
    private readonly object recognizerLock = new();
    private readonly CompanionShortcutRecognizer recognizer = new();
    private PhysicalModifiers heldModifiers;
    private bool isSubscribed;

    public event Action<BuddyPushToTalkShortcut.ShortcutTransition>? ShortcutTransition;
    public event Action<CompanionShortcutEvent>? CompanionShortcut;

    public GlobalShortcutMonitor(IGlobalKeyboardMonitor keyboardMonitor, IMainThread mainThread)
    {
        this.keyboardMonitor = keyboardMonitor;
        this.mainThread = mainThread;
    }

    public DictationHotkey DictationKey
    {
        get { lock (recognizerLock) return recognizer.DictationKey; }
        set
        {
            lock (recognizerLock) recognizer.DictationKey = value;
            // Caps Lock as the dictation key must not toggle Caps Lock on every take.
            keyboardMonitor.SetSwallowedKeys(value == DictationHotkey.CapsLock ? [Core.Platform.VirtualKeys.CapsLock] : []);
        }
    }

    public bool IsRunning => keyboardMonitor.IsRunning;

    public void Start()
    {
        if (!isSubscribed)
        {
            keyboardMonitor.KeyEvent += OnKeyEvent;
            isSubscribed = true;
        }
        // Idempotent: a running hook keeps its press in progress.
        keyboardMonitor.Start();
    }

    public void Stop()
    {
        keyboardMonitor.Stop();
        if (isSubscribed)
        {
            keyboardMonitor.KeyEvent -= OnKeyEvent;
            isSubscribed = false;
        }
        lock (recognizerLock)
        {
            recognizer.Reset();
            heldModifiers = PhysicalModifiers.None;
        }
    }

    /// <summary>Runs on the monitor's dispatch thread.</summary>
    private void OnKeyEvent(RawKeyEvent rawEvent)
    {
        IReadOnlyList<CompanionShortcutEvent> shortcutEvents;
        lock (recognizerLock)
        {
            var modifier = Core.Shortcuts.VirtualKeys.ModifierFor(rawEvent.VirtualKey);
            // Auto-repeat of a held modifier changes nothing the recogniser reads.
            if (modifier != PhysicalModifiers.None && rawEvent.IsRepeat) return;
            if (modifier != PhysicalModifiers.None)
            {
                heldModifiers = rawEvent.IsDown ? heldModifiers | modifier : heldModifiers & ~modifier;
            }
            else if (rawEvent.IsRepeat)
            {
                return;
            }
            shortcutEvents = recognizer.Handle(new ShortcutKeyEvent(rawEvent.VirtualKey, rawEvent.IsDown, heldModifiers, rawEvent.UptimeSeconds));
        }
        foreach (var shortcutEvent in shortcutEvents)
        {
            switch (shortcutEvent.Kind)
            {
                case CompanionShortcutEventKind.TalkPressed:
                    // Win is held as half of the talk shortcut: its release must not open Start.
                    keyboardMonitor.SuppressMenuActivation();
                    Publish(() => ShortcutTransition?.Invoke(BuddyPushToTalkShortcut.ShortcutTransition.Pressed));
                    break;
                case CompanionShortcutEventKind.TalkReleased:
                    Publish(() => ShortcutTransition?.Invoke(BuddyPushToTalkShortcut.ShortcutTransition.Released));
                    break;
                default:
                    if (shortcutEvent.Kind is CompanionShortcutEventKind.DictationPressed or CompanionShortcutEventKind.DictationEditPressed
                        && DictationKey == DictationHotkey.RightAlt)
                    {
                        // Right Alt held to dictate: its release must not open the app's menu bar.
                        keyboardMonitor.SuppressMenuActivation();
                    }
                    var captured = shortcutEvent;
                    Publish(() => CompanionShortcut?.Invoke(captured));
                    break;
            }
        }
    }

    private void Publish(Action action) => mainThread.Post(() =>
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            AppLog.Write($"shortcuts: handler failed: {error}");
        }
    });

    public void Dispose() => Stop();
}
