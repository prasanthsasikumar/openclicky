//
//  CompanionShortcutRecognizer.cs
//  OpenClicky (Windows port of CompanionShortcutRecognizer.swift)
//
//  Recognises the keyboard shortcuts from the raw key events of the low-level keyboard hook:
//    Dictate     hold the dictation key (right Ctrl)  hold → speak → release; a tap starts a take
//                                                     that the next tap ends; two quick taps cancel;
//                                                     esc cancels
//    Hey Clicky  dictation key + Ctrl held            the take is an edit instruction, not words to type
//    Talk        hold right Ctrl + Win                (push-to-talk to the companion; released = send;
//                                                     the dictation Ctrl is talk's Ctrl, the left one works too)
//    Text        tap Ctrl twice                       (opens the text composer in the island)
//    Hands-free  tap dictation key + Ctrl twice       (toggles always-on listening)
//  A pure state machine — no hook structs — so it is unit-testable.
//
//  The dictation key can be a right-hand modifier (right Ctrl, Alt or Shift), which folds into the same
//  modifier as its left twin. That modifier is the dictation key's own while it is held, never "Ctrl
//  joined" or half of Ctrl+Win: the companion shortcuts read the modifiers with the key's own press
//  taken out. On the Mac the per-key device bits tell the two sides apart; on Windows the hook's
//  virtual-key codes always do (VK_LCONTROL vs VK_RCONTROL…), so the Mac's no-device-bits fallback
//  (synthetic events, `twinModifierWasDownAtPress`) is not needed and is dropped.
//
//  Mac → Windows: ⌃ → Ctrl, ⌥ → Win, ⌘ → Alt, fn → Caps Lock (held). A Mac `flagsChanged` event is a
//  key event here whose virtual key is a modifier key (or Caps Lock when it is the dictation key).
//

namespace OpenClicky.Core.Shortcuts;

public enum CompanionShortcutEventKind
{
    TalkPressed,
    TalkReleased,
    /// <summary>The dictation key went down: start listening now, before knowing whether it is a hold or a tap.</summary>
    DictationPressed,
    /// <summary>The dictation key went down with Ctrl already held: a Hey Clicky press — or the first
    /// half of the hands-free gesture, which only the release can tell.</summary>
    DictationEditPressed,
    /// <summary>Ctrl joined while the dictation key is held: this take is a Hey Clicky instruction.</summary>
    DictationEditModifierJoined,
    /// <summary>See <see cref="CompanionShortcutEvent.WasTap"/> and <see cref="CompanionShortcutEvent.KeyWasPressedDuringHold"/>.</summary>
    DictationReleased,
    /// <summary>Two quick taps of the dictation key: discard the take in progress.</summary>
    DictationDoubleTapped,
    /// <summary>The escape key went down (the controller cancels a take in progress).</summary>
    EscapePressed,
    TextComposerRequested,
    HandsFreeToggleRequested,
    /// <summary>Another key that could dictate (<see cref="CompanionShortcutEvent.OtherKey"/>) was held alone
    /// and let go, as someone does whose keyboard has no right Ctrl. The first-run tour adopts it.</summary>
    OtherDictationKeyHeld,
}

/// <summary>What the keyboard asked for.</summary>
/// <param name="WasTap">DictationReleased only: the key came back up within the tap window with nothing else pressed.</param>
/// <param name="KeyWasPressedDuringHold">DictationReleased only: a key went down while the dictation key was
/// held (right Ctrl + C): the hold was a keyboard shortcut, not a take, and the take is thrown away.</param>
/// <param name="OtherKey">OtherDictationKeyHeld only: the key that was held.</param>
public sealed record CompanionShortcutEvent(CompanionShortcutEventKind Kind, bool WasTap = false, bool KeyWasPressedDuringHold = false,
                                            DictationHotkey? OtherKey = null)
{
    public static readonly CompanionShortcutEvent TalkPressed = new(CompanionShortcutEventKind.TalkPressed);
    public static readonly CompanionShortcutEvent TalkReleased = new(CompanionShortcutEventKind.TalkReleased);
    public static readonly CompanionShortcutEvent DictationPressed = new(CompanionShortcutEventKind.DictationPressed);
    public static readonly CompanionShortcutEvent DictationEditPressed = new(CompanionShortcutEventKind.DictationEditPressed);
    public static readonly CompanionShortcutEvent DictationEditModifierJoined = new(CompanionShortcutEventKind.DictationEditModifierJoined);
    public static readonly CompanionShortcutEvent DictationDoubleTapped = new(CompanionShortcutEventKind.DictationDoubleTapped);
    public static readonly CompanionShortcutEvent EscapePressed = new(CompanionShortcutEventKind.EscapePressed);
    public static readonly CompanionShortcutEvent TextComposerRequested = new(CompanionShortcutEventKind.TextComposerRequested);
    public static readonly CompanionShortcutEvent HandsFreeToggleRequested = new(CompanionShortcutEventKind.HandsFreeToggleRequested);

    public static CompanionShortcutEvent DictationReleased(bool wasTap, bool keyWasPressedDuringHold = false) =>
        new(CompanionShortcutEventKind.DictationReleased, wasTap, keyWasPressedDuringHold);

    public static CompanionShortcutEvent OtherDictationKeyHeld(DictationHotkey key) =>
        new(CompanionShortcutEventKind.OtherDictationKeyHeld, OtherKey: key);
}

/// <summary>One event from the low-level keyboard hook.</summary>
/// <param name="VirtualKey">The side-specific virtual-key code (VK_LCONTROL, VK_RMENU, 'C'…).</param>
/// <param name="IsDown">Key down (WM_KEYDOWN / WM_SYSKEYDOWN, including auto-repeat) or key up.</param>
/// <param name="Modifiers">The physical modifier keys held after this event.</param>
/// <param name="Timestamp">Seconds, monotonic (the hook's millisecond time / 1000 is fine).</param>
public readonly record struct ShortcutKeyEvent(int VirtualKey, bool IsDown, PhysicalModifiers Modifiers, double Timestamp);

public sealed class CompanionShortcutRecognizer
{
    private enum EventKind { ModifiersChanged, KeyDown, KeyUp }

    /// <summary>A press that lasts longer than this is a hold (talk / dictate), not a tap.</summary>
    public const double TapMaxHoldSeconds = 0.35;
    /// <summary>Two taps this close together make a double tap.</summary>
    public const double DoubleTapWindowSeconds = 0.45;
    /// <summary>Another dictation key held alone at least this long reads as "this is the key I'd hold".</summary>
    public const double OtherKeyHoldSeconds = 0.5;

    /// <summary>The modifiers that take part in the shortcuts. Caps Lock only counts when it is the
    /// dictation key; otherwise it is an ordinary key (the Mac ignores caps lock too).</summary>
    private const CompanionModifiers TrackedModifierFlags =
        CompanionModifiers.Control | CompanionModifiers.Windows | CompanionModifiers.Alt | CompanionModifiers.Shift | CompanionModifiers.CapsLock;

    private DictationHotkey dictationKey;

    /// <summary>The key that dictates. Changing it resets the dictation state.</summary>
    public DictationHotkey DictationKey
    {
        get => dictationKey;
        set
        {
            if (value == dictationKey) return;
            dictationKey = value;
            ResetDictationState();
        }
    }

    /// <summary>Whether OtherDictationKeyHeld is reported (off by default; the first-run tour turns it on).</summary>
    public bool ReportsOtherDictationKeys { get; set; }

    public bool IsTalkHeld { get; private set; }
    public bool IsDictationKeyHeld { get; private set; }

    /// <summary>The modifiers of the last event as the companion shortcuts read them (see CompanionFlags).</summary>
    private CompanionModifiers previousCompanionFlags = CompanionModifiers.None;
    /// <summary>When Ctrl alone went down (a possible tap), or null.</summary>
    private double? controlTapStartedAt;
    private double? lastControlTapCompletedAt;
    /// <summary>A real key was pressed while Ctrl was down (Ctrl + C): not a Ctrl tap.</summary>
    private bool keyWasPressedDuringControlTap;
    /// <summary>When the dictation key went down, or null.</summary>
    private double? dictationHoldStartedAt;
    /// <summary>Ctrl joined during this hold (Hey Clicky).</summary>
    private bool editModifierJoinedDuringHold;
    private double? lastDictationTapCompletedAt;
    private double? lastEditTapCompletedAt;
    /// <summary>A key went down or up while the dictation key was held (right Ctrl + arrow): not a tap.</summary>
    private bool keyEventDuringDictationHold;
    /// <summary>A key went down while the dictation key was held (right Ctrl + C): the hold was a shortcut.
    /// Only key-downs count: the key-up of a letter typed just before the press is not a shortcut.</summary>
    private bool keyWentDownDuringDictationHold;
    /// <summary>Talk's modifiers came together while the dictation key was held: not a Ctrl+Win question
    /// until one of them is let go, so one gesture never opens a take and a question at once.</summary>
    private bool talkHeldBackByDictation;
    /// <summary>A Ctrl dictating is also talk's Ctrl: this press of it went to Ctrl+Win (Win was down, or
    /// joined the hold), so it is no take until it comes back up.</summary>
    private bool dictationKeyPressIsTalk;
    /// <summary>Another key that could dictate went down with nothing else held, and when; null once
    /// anything else moves.</summary>
    private (DictationHotkey Key, double StartedAt)? otherKeyHold;

    /// <summary>Right Ctrl (the default) or left Ctrl dictates: Ctrl+Win is then that key plus Win, so
    /// everything stays on one hand and the other Ctrl is free for copy and paste.</summary>
    private bool DictationKeyIsAControl => dictationKey.ModifierFlag() == CompanionModifiers.Control;

    public CompanionShortcutRecognizer(DictationHotkey dictationKey = DictationHotkeys.Default)
    {
        this.dictationKey = dictationKey;
    }

    /// <summary>Folds the sides together (and drops Caps Lock unless it dictates).</summary>
    private CompanionModifiers TrackedFlags(PhysicalModifiers modifiers)
    {
        var flags = CompanionModifiers.None;
        if ((modifiers & (PhysicalModifiers.LeftControl | PhysicalModifiers.RightControl)) != 0) flags |= CompanionModifiers.Control;
        if ((modifiers & (PhysicalModifiers.LeftWindows | PhysicalModifiers.RightWindows)) != 0) flags |= CompanionModifiers.Windows;
        if ((modifiers & (PhysicalModifiers.LeftAlt | PhysicalModifiers.RightAlt)) != 0) flags |= CompanionModifiers.Alt;
        if ((modifiers & (PhysicalModifiers.LeftShift | PhysicalModifiers.RightShift)) != 0) flags |= CompanionModifiers.Shift;
        if ((modifiers & PhysicalModifiers.CapsLock) != 0 && dictationKey == DictationHotkey.CapsLock) flags |= CompanionModifiers.CapsLock;
        return flags & TrackedModifierFlags;
    }

    /// <summary>Whether the event is the Windows twin of a Mac flagsChanged event.</summary>
    private EventKind Classify(ShortcutKeyEvent keyEvent)
    {
        var modifier = VirtualKeys.ModifierFor(keyEvent.VirtualKey);
        var isModifierKey = modifier != PhysicalModifiers.None
            && (modifier != PhysicalModifiers.CapsLock || dictationKey == DictationHotkey.CapsLock);
        if (isModifierKey) return EventKind.ModifiersChanged;
        return keyEvent.IsDown ? EventKind.KeyDown : EventKind.KeyUp;
    }

    /// <summary>Feeds one event; returns the shortcut events it completes, in order.</summary>
    public IReadOnlyList<CompanionShortcutEvent> Handle(ShortcutKeyEvent keyEvent)
    {
        var time = keyEvent.Timestamp;
        var eventKind = Classify(keyEvent);
        var otherKeyEvents = TrackOtherKeyHold(keyEvent, eventKind);
        if (eventKind != EventKind.ModifiersChanged)
        {
            keyWasPressedDuringControlTap = true;
            if (IsDictationKeyHeld)
            {
                keyEventDuringDictationHold = true;
                if (eventKind == EventKind.KeyDown) keyWentDownDuringDictationHold = true;
            }
            controlTapStartedAt = null;
            lastControlTapCompletedAt = null;
            lastDictationTapCompletedAt = null;
            lastEditTapCompletedAt = null;
            if (eventKind == EventKind.KeyDown && keyEvent.VirtualKey == VirtualKeys.Escape)
                return new[] { CompanionShortcutEvent.EscapePressed };
            return Array.Empty<CompanionShortcutEvent>();
        }

        var trackedFlags = TrackedFlags(keyEvent.Modifiers);

        // The dictation key is identified by its own virtual key (right and left Ctrl fold into the same
        // modifier), and its side bit in the modifier state tells down from up. Auto-repeat key-downs of
        // a held modifier move nothing.
        var dictationKeyIsDownNow = dictationKey.IsDown(keyEvent.VirtualKey, keyEvent.Modifiers);
        var dictationKeyWentDown = dictationKeyIsDownNow == true && !IsDictationKeyHeld;
        var dictationKeyWentUp = dictationKeyIsDownNow == false && IsDictationKeyHeld;

        var companionFlags = CompanionFlags(trackedFlags, keyEvent.Modifiers);
        var talkModifiersDown = Contains(companionFlags, CompanionModifiers.Control | CompanionModifiers.Windows);

        var dictationEvents = new List<CompanionShortcutEvent>();
        var dictationGaveWayToTalk = false;
        if (dictationKeyWentDown)
        {
            if (dictationKeyPressIsTalk)
            {
                // A repeat of a Ctrl press that talk took (Win was let go first): still no take.
            }
            else if (DictationKeyIsAControl && talkModifiersDown)
            {
                // Win, then the dictation Ctrl: talk, and the press is talk's until it comes up.
                dictationKeyPressIsTalk = true;
            }
            else if (IsTalkHeld && talkModifiersDown)
            {
                // Talk (Ctrl + Win) with the dictation key added by accident is still talk. The key's
                // press is not tracked, so its release is no event either. (The Mac recomputes the
                // companion flags here without the key held; with side bits always present that is the
                // same value.)
            }
            else
            {
                IsDictationKeyHeld = true;
                dictationHoldStartedAt = time;
                keyEventDuringDictationHold = false;
                keyWentDownDuringDictationHold = false;
                editModifierJoinedDuringHold = Contains(companionFlags, CompanionModifiers.Control);
                dictationEvents.Add(editModifierJoinedDuringHold ? CompanionShortcutEvent.DictationEditPressed : CompanionShortcutEvent.DictationPressed);
            }
        }
        else if (dictationKeyWentUp)
        {
            IsDictationKeyHeld = false;
            var holdDuration = dictationHoldStartedAt is { } startedAt ? time - startedAt : double.PositiveInfinity;
            var wasTap = holdDuration <= TapMaxHoldSeconds && !keyEventDuringDictationHold;
            dictationHoldStartedAt = null;
            dictationEvents.Add(CompanionShortcutEvent.DictationReleased(wasTap, keyWentDownDuringDictationHold));
            if (editModifierJoinedDuringHold)
            {
                if (wasTap && lastEditTapCompletedAt is { } lastTap && time - lastTap <= DoubleTapWindowSeconds)
                {
                    lastEditTapCompletedAt = null;
                    dictationEvents.Add(CompanionShortcutEvent.HandsFreeToggleRequested);
                }
                else
                {
                    lastEditTapCompletedAt = wasTap ? time : null;
                }
                lastDictationTapCompletedAt = null;
            }
            else
            {
                if (wasTap && lastDictationTapCompletedAt is { } lastTap && time - lastTap <= DoubleTapWindowSeconds)
                {
                    lastDictationTapCompletedAt = null;
                    dictationEvents.Add(CompanionShortcutEvent.DictationDoubleTapped);
                }
                else
                {
                    lastDictationTapCompletedAt = wasTap ? time : null;
                }
                lastEditTapCompletedAt = null;
            }
            editModifierJoinedDuringHold = false;
            keyEventDuringDictationHold = false;
            keyWentDownDuringDictationHold = false;
        }
        else if (IsDictationKeyHeld && DictationKeyIsAControl && !editModifierJoinedDuringHold && talkModifiersDown)
        {
            // Win joined the held dictation Ctrl: the hold was talk's Ctrl, not a take. The take opened by
            // the press is cancelled as a shortcut first, so the microphone is free for the question.
            ResetDictationState();
            dictationKeyPressIsTalk = true;
            dictationGaveWayToTalk = true;
            dictationEvents.Add(CompanionShortcutEvent.DictationReleased(wasTap: false, keyWasPressedDuringHold: true));
        }
        else if (IsDictationKeyHeld && !editModifierJoinedDuringHold && Contains(companionFlags, CompanionModifiers.Control))
        {
            // Ctrl pressed while the dictation key is held: Hey Clicky mode for this take.
            editModifierJoinedDuringHold = true;
            dictationEvents.Add(CompanionShortcutEvent.DictationEditModifierJoined);
        }

        // Talk starts only while the dictation key is up; Ctrl+Win completed during a take is the take's
        // (Ctrl already made it an edit), and stays held back until Ctrl or Win is let go.
        if (!talkModifiersDown)
        {
            talkHeldBackByDictation = false;
        }
        else if (IsDictationKeyHeld && !IsTalkHeld)
        {
            talkHeldBackByDictation = true;
        }
        var isTalkHeldNow = talkModifiersDown && !talkHeldBackByDictation;
        if (dictationKeyIsDownNow == false) dictationKeyPressIsTalk = false;
        var events = new List<CompanionShortcutEvent>();
        if (dictationGaveWayToTalk) events.AddRange(dictationEvents);
        if (isTalkHeldNow != IsTalkHeld)
        {
            IsTalkHeld = isTalkHeldNow;
            events.Add(isTalkHeldNow ? CompanionShortcutEvent.TalkPressed : CompanionShortcutEvent.TalkReleased);
        }
        if (!dictationGaveWayToTalk) events.AddRange(dictationEvents);

        // A Ctrl tap: Ctrl alone goes down from no modifiers, and everything comes back up shortly
        // after without any other modifier or key joining in between. The dictation key moving at all
        // (right Ctrl is a Ctrl key too) is never part of one.
        if (dictationKeyIsDownNow != null || IsDictationKeyHeld)
        {
            controlTapStartedAt = null;
        }
        else if (companionFlags == CompanionModifiers.Control && previousCompanionFlags == CompanionModifiers.None)
        {
            controlTapStartedAt = time;
            keyWasPressedDuringControlTap = false;
        }
        else if (companionFlags == CompanionModifiers.None && controlTapStartedAt is { } tapStartedAt)
        {
            controlTapStartedAt = null;
            var wasTap = time - tapStartedAt <= TapMaxHoldSeconds && !keyWasPressedDuringControlTap
                && previousCompanionFlags == CompanionModifiers.Control;
            if (wasTap && lastControlTapCompletedAt is { } lastTapCompletedAt && time - lastTapCompletedAt <= DoubleTapWindowSeconds)
            {
                lastControlTapCompletedAt = null;
                events.Add(CompanionShortcutEvent.TextComposerRequested);
            }
            else
            {
                lastControlTapCompletedAt = wasTap ? time : null;
            }
        }
        else if (companionFlags != CompanionModifiers.Control)
        {
            // Another modifier joined (talk, dictate, a shortcut): not a Ctrl tap any more.
            controlTapStartedAt = null;
        }

        previousCompanionFlags = companionFlags;
        events.AddRange(otherKeyEvents);
        return events;
    }

    /// <summary>Watches for another key that could dictate (not Caps Lock, which toggles) held alone for
    /// <see cref="OtherKeyHoldSeconds"/> and let go with nothing else having moved.</summary>
    private IReadOnlyList<CompanionShortcutEvent> TrackOtherKeyHold(ShortcutKeyEvent keyEvent, EventKind eventKind)
    {
        if (ReportsOtherDictationKeys && eventKind == EventKind.ModifiersChanged)
        {
            foreach (var candidate in DictationHotkeys.AllCases)
            {
                if (candidate == dictationKey || candidate == DictationHotkey.CapsLock || candidate.VirtualKey() != keyEvent.VirtualKey) continue;
                var own = candidate.SideBits().Own;
                if (keyEvent.IsDown)
                {
                    // Auto-repeat keeps the hold; anything else held makes it a shortcut.
                    if (otherKeyHold?.Key != candidate) otherKeyHold = keyEvent.Modifiers == own ? (candidate, keyEvent.Timestamp) : null;
                    return Array.Empty<CompanionShortcutEvent>();
                }
                if (otherKeyHold is { } hold && hold.Key == candidate && keyEvent.Modifiers == PhysicalModifiers.None
                    && keyEvent.Timestamp - hold.StartedAt >= OtherKeyHoldSeconds)
                {
                    otherKeyHold = null;
                    return new[] { CompanionShortcutEvent.OtherDictationKeyHeld(candidate) };
                }
                break;
            }
        }
        otherKeyHold = null;
        return Array.Empty<CompanionShortcutEvent>();
    }

    private static bool Contains(CompanionModifiers flags, CompanionModifiers required) => (flags & required) == required;

    /// <summary>The modifiers as the companion shortcuts (talk, Hey Clicky's Ctrl, the Ctrl tap) read
    /// them: without the dictation key's own press, so a held right Ctrl is not "Ctrl" and a held right
    /// Alt is not Alt — but with its left-hand twin when that is down too.</summary>
    private CompanionModifiers CompanionFlags(CompanionModifiers trackedFlags, PhysicalModifiers modifiers)
    {
        var keyFlag = dictationKey.ModifierFlag();
        if ((trackedFlags & keyFlag) == 0) return trackedFlags;
        // A Ctrl dictating is also talk's Ctrl: with Win down it is Ctrl.
        if (DictationKeyIsAControl && (trackedFlags & CompanionModifiers.Windows) != 0) return trackedFlags;
        var twin = dictationKey.SideBits().Twin;
        var twinIsDown = twin != PhysicalModifiers.None && (modifiers & twin) != 0;
        return twinIsDown ? trackedFlags : trackedFlags & ~keyFlag;
    }

    private void ResetDictationState()
    {
        IsDictationKeyHeld = false;
        dictationHoldStartedAt = null;
        editModifierJoinedDuringHold = false;
        lastDictationTapCompletedAt = null;
        lastEditTapCompletedAt = null;
        keyEventDuringDictationHold = false;
        keyWentDownDuringDictationHold = false;
        dictationKeyPressIsTalk = false;
    }

    public void Reset()
    {
        IsTalkHeld = false;
        previousCompanionFlags = CompanionModifiers.None;
        controlTapStartedAt = null;
        lastControlTapCompletedAt = null;
        keyWasPressedDuringControlTap = false;
        talkHeldBackByDictation = false;
        otherKeyHold = null;
        ResetDictationState();
    }
}
