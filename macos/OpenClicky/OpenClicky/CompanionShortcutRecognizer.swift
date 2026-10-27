//
//  CompanionShortcutRecognizer.swift
//  OpenClicky
//
//  Recognises the keyboard shortcuts from the raw modifier / key events of the global event tap:
//    Dictate     hold the dictation key (fn)      hold → speak → release; a tap starts a take that
//                                                 the next tap ends; two quick taps cancel; esc cancels
//    Hey Clicky  dictation key + control held     the take is an edit instruction, not words to type
//    Talk        hold control + option            (push-to-talk to the companion; released = send)
//    Text        tap control twice                (opens the text composer in the notch)
//    Hands-free  tap dictation key + control twice (toggles always-on listening)
//  A pure state machine — no NSEvent objects — so it is unit-testable.
//
//  The dictation key can be a right-hand modifier (right ⌥, ⌘ or ⌃), which sets the same flag as
//  its left twin. That flag is the dictation key's own while it is held, never "control joined" or
//  half of ⌃⌥: the companion shortcuts read the flags with the key's own press taken out (the
//  per-key device bits macOS puts on every modifier event tell the two sides apart).
//

import AppKit
import Foundation

/// What the keyboard asked for.
enum CompanionShortcutEvent: Equatable {
    case talkPressed
    case talkReleased
    /// The dictation key went down: start listening now, before knowing whether it is a hold or a tap.
    case dictationPressed
    /// The dictation key went down with control already held: a Hey Clicky press — or the first
    /// half of the hands-free gesture, which only the release can tell.
    case dictationEditPressed
    /// Control joined while the dictation key is held: this take is a Hey Clicky instruction.
    case dictationEditModifierJoined
    /// `wasTap`: the key came back up within the tap window with nothing else pressed.
    /// `keyWasPressedDuringHold`: a key went down while the dictation key was held (right ⌘ + C,
    /// fn + an arrow): the hold was a keyboard shortcut, not a take, and the take is thrown away.
    case dictationReleased(wasTap: Bool, keyWasPressedDuringHold: Bool = false)
    /// Two quick taps of the dictation key: discard the take in progress.
    case dictationDoubleTapped
    /// The escape key went down (the controller cancels a take in progress).
    case escapePressed
    case textComposerRequested
    case handsFreeToggleRequested
}

struct CompanionShortcutRecognizer {
    enum EventKind {
        case flagsChanged
        case keyDown
        case keyUp
    }

    /// A press that lasts longer than this is a hold (talk / dictate), not a tap.
    static let tapMaxHoldSeconds: TimeInterval = 0.35
    /// Two taps this close together make a double tap.
    static let doubleTapWindowSeconds: TimeInterval = 0.45
    private static let escapeKeyCode: UInt16 = 53
    /// The fn / Globe key as a key event (kVK_Function is 63 on flagsChanged; this is the other one).
    private static let globeKeyCode: UInt16 = 179

    /// The modifiers that take part in the shortcuts; caps lock and the like are ignored.
    private static let trackedModifierFlags: NSEvent.ModifierFlags = [.control, .option, .shift, .command, .function]

    /// The key that dictates. Changing it resets the dictation state.
    var dictationKey: DictationHotkey {
        didSet { if dictationKey != oldValue { resetDictationState() } }
    }

    private(set) var isTalkHeld = false
    private(set) var isDictationKeyHeld = false

    /// The flags of the last event as the companion shortcuts read them (see `companionFlags`).
    private var previousCompanionFlags: NSEvent.ModifierFlags = []
    private var previousTrackedFlags: NSEvent.ModifierFlags = []
    /// When control alone went down (a possible tap), or nil.
    private var controlTapStartedAt: TimeInterval?
    private var lastControlTapCompletedAt: TimeInterval?
    /// A real key was pressed while control was down (control + C): not a control tap.
    private var keyWasPressedDuringControlTap = false
    /// When the dictation key went down, or nil.
    private var dictationHoldStartedAt: TimeInterval?
    /// Control joined during this hold (Hey Clicky).
    private var editModifierJoinedDuringHold = false
    private var lastDictationTapCompletedAt: TimeInterval?
    private var lastEditTapCompletedAt: TimeInterval?
    /// A key went down or up while the dictation key was held (fn + arrow): not a tap.
    private var keyEventDuringDictationHold = false
    /// A key went down while the dictation key was held (right ⌘ + C): the hold was a shortcut.
    /// Only key-downs count: the key-up of a letter typed just before the press is not a shortcut.
    private var keyWentDownDuringDictationHold = false
    /// Talk's modifiers came together while the dictation key was held: not a ⌃⌥ question until
    /// one of them is let go, so one gesture never opens a take and a question at once.
    private var talkHeldBackByDictation = false
    /// Without per-key device bits (synthetic events), whether the dictation key's modifier was
    /// already down from its other-side twin when the key went down.
    private var twinModifierWasDownAtPress = false

    init(dictationKey: DictationHotkey = .fn) {
        self.dictationKey = dictationKey
    }

    /// Feeds one event; returns the shortcut events it completes, in order.
    mutating func handle(
        _ eventKind: EventKind,
        keyCode: UInt16,
        modifierFlags rawModifierFlags: NSEvent.ModifierFlags,
        at time: TimeInterval
    ) -> [CompanionShortcutEvent] {
        switch eventKind {
        case .keyDown, .keyUp:
            // With control held, macOS follows each release of fn with a key down and up of the
            // Globe key's own key code. That is fn itself, not another key: counting it as one
            // forgot the first tap and the hands-free double tap never completed (2026-10-27).
            if keyCode == Self.globeKeyCode { return [] }
            keyWasPressedDuringControlTap = true
            if isDictationKeyHeld {
                keyEventDuringDictationHold = true
                if eventKind == .keyDown { keyWentDownDuringDictationHold = true }
            }
            controlTapStartedAt = nil
            lastControlTapCompletedAt = nil
            lastDictationTapCompletedAt = nil
            lastEditTapCompletedAt = nil
            if eventKind == .keyDown && keyCode == Self.escapeKeyCode { return [.escapePressed] }
            return []
        case .flagsChanged:
            break
        }

        let trackedFlags = rawModifierFlags.intersection(Self.trackedModifierFlags)

        // The dictation key is identified by its own key code on the flagsChanged event (the right
        // option key and the left one set the same flag), and the device bit (or, without one, the
        // flag) tells down from up. The fn flag also rides along on arrow and function keys, but
        // those are keyDown events, not flagsChanged ones with fn's key code, so they never reach here.
        let dictationKeyIsDownNow = dictationKeyIsDown(keyCode: keyCode, rawModifierFlags: rawModifierFlags, trackedFlags: trackedFlags)
        let dictationKeyWentDown = dictationKeyIsDownNow == true && !isDictationKeyHeld
        let dictationKeyWentUp = dictationKeyIsDownNow == false && isDictationKeyHeld
        if dictationKeyWentDown {
            twinModifierWasDownAtPress = previousTrackedFlags.contains(dictationKey.modifierFlag)
        }

        var companionFlags = companionFlags(trackedFlags: trackedFlags, rawModifierFlags: rawModifierFlags,
                                            dictationKeyIsHeld: dictationKeyWentDown || (isDictationKeyHeld && !dictationKeyWentUp))
        let talkModifiersDown = companionFlags.contains([.control, .option])

        var dictationEvents: [CompanionShortcutEvent] = []
        if dictationKeyWentDown {
            if isTalkHeld && talkModifiersDown {
                // Talk (control + option) with the dictation key added by accident is still talk.
                // The key's press is not tracked, so its release is no event either.
                companionFlags = self.companionFlags(trackedFlags: trackedFlags, rawModifierFlags: rawModifierFlags, dictationKeyIsHeld: false)
            } else {
                isDictationKeyHeld = true
                dictationHoldStartedAt = time
                keyEventDuringDictationHold = false
                keyWentDownDuringDictationHold = false
                editModifierJoinedDuringHold = companionFlags.contains(.control)
                dictationEvents.append(editModifierJoinedDuringHold ? .dictationEditPressed : .dictationPressed)
            }
        } else if dictationKeyWentUp {
            isDictationKeyHeld = false
            let holdDuration = dictationHoldStartedAt.map { time - $0 } ?? .infinity
            let wasTap = holdDuration <= Self.tapMaxHoldSeconds && !keyEventDuringDictationHold
            dictationHoldStartedAt = nil
            dictationEvents.append(.dictationReleased(wasTap: wasTap, keyWasPressedDuringHold: keyWentDownDuringDictationHold))
            if editModifierJoinedDuringHold {
                if wasTap, let lastTap = lastEditTapCompletedAt, time - lastTap <= Self.doubleTapWindowSeconds {
                    lastEditTapCompletedAt = nil
                    dictationEvents.append(.handsFreeToggleRequested)
                } else {
                    lastEditTapCompletedAt = wasTap ? time : nil
                }
                lastDictationTapCompletedAt = nil
            } else {
                if wasTap, let lastTap = lastDictationTapCompletedAt, time - lastTap <= Self.doubleTapWindowSeconds {
                    lastDictationTapCompletedAt = nil
                    dictationEvents.append(.dictationDoubleTapped)
                } else {
                    lastDictationTapCompletedAt = wasTap ? time : nil
                }
                lastEditTapCompletedAt = nil
            }
            editModifierJoinedDuringHold = false
            keyEventDuringDictationHold = false
            keyWentDownDuringDictationHold = false
        } else if isDictationKeyHeld, !editModifierJoinedDuringHold, companionFlags.contains(.control) {
            // Control pressed while the dictation key is held: Hey Clicky mode for this take.
            editModifierJoinedDuringHold = true
            dictationEvents.append(.dictationEditModifierJoined)
        }

        // Talk starts only while the dictation key is up; ⌃⌥ completed during a take is the take's
        // (control already made it an edit), and stays held back until ⌃ or ⌥ is let go.
        if !talkModifiersDown {
            talkHeldBackByDictation = false
        } else if isDictationKeyHeld && !isTalkHeld {
            talkHeldBackByDictation = true
        }
        let isTalkHeldNow = talkModifiersDown && !talkHeldBackByDictation
        var events: [CompanionShortcutEvent] = []
        if isTalkHeldNow != isTalkHeld {
            isTalkHeld = isTalkHeldNow
            events.append(isTalkHeldNow ? .talkPressed : .talkReleased)
        }
        events += dictationEvents

        // A control tap: control alone goes down from no modifiers, and everything comes back up
        // shortly after without any other modifier or key joining in between. The dictation key
        // moving at all (right ⌃ is a control key too) is never part of one.
        if dictationKeyIsDownNow != nil || isDictationKeyHeld {
            controlTapStartedAt = nil
        } else if companionFlags == [.control] && previousCompanionFlags.isEmpty {
            controlTapStartedAt = time
            keyWasPressedDuringControlTap = false
        } else if companionFlags.isEmpty, let tapStartedAt = controlTapStartedAt {
            controlTapStartedAt = nil
            let wasTap = time - tapStartedAt <= Self.tapMaxHoldSeconds && !keyWasPressedDuringControlTap && previousCompanionFlags == [.control]
            if wasTap, let lastTapCompletedAt = lastControlTapCompletedAt, time - lastTapCompletedAt <= Self.doubleTapWindowSeconds {
                lastControlTapCompletedAt = nil
                events.append(.textComposerRequested)
            } else {
                lastControlTapCompletedAt = wasTap ? time : nil
            }
        } else if companionFlags != [.control] {
            // Another modifier joined (talk, dictate, a shortcut): not a control tap any more.
            controlTapStartedAt = nil
        }

        previousCompanionFlags = companionFlags
        previousTrackedFlags = trackedFlags
        return events
    }

    /// Whether this flagsChanged event moved the dictation key, and to where: nil when it was
    /// another key. With both controls (or options, or commands) down, the flag stays set when one
    /// comes up; the device bit says which one did.
    private func dictationKeyIsDown(keyCode: UInt16, rawModifierFlags: NSEvent.ModifierFlags, trackedFlags: NSEvent.ModifierFlags) -> Bool? {
        guard keyCode == dictationKey.keyCode else { return nil }
        if let deviceBits = dictationKey.deviceBits, rawModifierFlags.rawValue & (deviceBits.own | deviceBits.twin) != 0 {
            return rawModifierFlags.rawValue & deviceBits.own != 0
        }
        return dictationKey.isDown(keyCode: keyCode, modifierFlags: trackedFlags)
    }

    /// The modifiers as the companion shortcuts (talk, Hey Clicky's control, the control tap) read
    /// them: without the dictation key's own press, so a held right ⌃ is not "control" and a held
    /// right ⌥ is not half of ⌃⌥ — but with its left-hand twin when that is down too.
    private func companionFlags(trackedFlags: NSEvent.ModifierFlags, rawModifierFlags: NSEvent.ModifierFlags, dictationKeyIsHeld: Bool) -> NSEvent.ModifierFlags {
        let keyFlag = dictationKey.modifierFlag
        guard trackedFlags.contains(keyFlag) else { return trackedFlags }
        let twinIsDown: Bool
        if let deviceBits = dictationKey.deviceBits, rawModifierFlags.rawValue & (deviceBits.own | deviceBits.twin) != 0 {
            twinIsDown = rawModifierFlags.rawValue & deviceBits.twin != 0
        } else if dictationKeyIsHeld {
            // No device bits: the flag is the key's own, unless its twin was down before it.
            twinIsDown = dictationKey.deviceBits != nil && twinModifierWasDownAtPress
        } else {
            twinIsDown = true
        }
        return twinIsDown ? trackedFlags : trackedFlags.subtracting(keyFlag)
    }

    private mutating func resetDictationState() {
        isDictationKeyHeld = false
        dictationHoldStartedAt = nil
        editModifierJoinedDuringHold = false
        lastDictationTapCompletedAt = nil
        lastEditTapCompletedAt = nil
        keyEventDuringDictationHold = false
        keyWentDownDuringDictationHold = false
        twinModifierWasDownAtPress = false
    }

    mutating func reset() {
        self = CompanionShortcutRecognizer(dictationKey: dictationKey)
    }
}

private extension DictationHotkey {
    /// The device-dependent bits (IOLLEvent.h's NX_DEVICE…KEYMASK) for this key and for the
    /// same modifier on the left. fn has no twin, and no bit.
    var deviceBits: (own: UInt, twin: UInt)? {
        switch self {
        case .fn: return nil
        case .rightControl: return (own: 0x0000_2000, twin: 0x0000_0001)
        case .rightOption: return (own: 0x0000_0040, twin: 0x0000_0020)
        case .rightCommand: return (own: 0x0000_0010, twin: 0x0000_0008)
        }
    }
}
