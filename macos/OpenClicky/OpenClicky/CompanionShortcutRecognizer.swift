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

import AppKit
import Foundation

/// What the keyboard asked for.
enum CompanionShortcutEvent: Equatable {
    case talkPressed
    case talkReleased
    /// The dictation key went down: start listening now, before knowing whether it is a hold or a tap.
    case dictationPressed
    /// Control joined while the dictation key is held: this take is a Hey Clicky instruction.
    case dictationEditModifierJoined
    /// `wasTap`: the key came back up within the tap window with nothing else pressed.
    case dictationReleased(wasTap: Bool)
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

    /// The modifiers that take part in the shortcuts; caps lock and the like are ignored.
    private static let trackedModifierFlags: NSEvent.ModifierFlags = [.control, .option, .shift, .command, .function]

    /// The key that dictates. Changing it resets the dictation state.
    var dictationKey: DictationHotkey {
        didSet { if dictationKey != oldValue { resetDictationState() } }
    }

    private(set) var isTalkHeld = false
    private(set) var isDictationKeyHeld = false

    private var previousTrackedFlags: NSEvent.ModifierFlags = []
    /// When control alone went down (a possible tap), or nil.
    private var controlTapStartedAt: TimeInterval?
    private var lastControlTapCompletedAt: TimeInterval?
    /// When the dictation key went down, or nil.
    private var dictationHoldStartedAt: TimeInterval?
    /// Control joined during this hold (Hey Clicky).
    private var editModifierJoinedDuringHold = false
    private var lastDictationTapCompletedAt: TimeInterval?
    private var lastEditTapCompletedAt: TimeInterval?
    /// A real key was pressed while the modifier was down (control + C, fn + arrow): not a tap.
    private var keyWasPressedDuringHold = false

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
            keyWasPressedDuringHold = true
            controlTapStartedAt = nil
            lastControlTapCompletedAt = nil
            lastDictationTapCompletedAt = nil
            lastEditTapCompletedAt = nil
            if eventKind == .keyDown && keyCode == Self.escapeKeyCode { return [.escapePressed] }
            return []
        case .flagsChanged:
            break
        }

        var events: [CompanionShortcutEvent] = []
        let trackedFlags = rawModifierFlags.intersection(Self.trackedModifierFlags)
        let isTalkHeldNow = trackedFlags.contains([.control, .option])

        if isTalkHeldNow != isTalkHeld {
            isTalkHeld = isTalkHeldNow
            events.append(isTalkHeldNow ? .talkPressed : .talkReleased)
        }

        // The dictation key is identified by its own key code on the flagsChanged event (the right
        // option key and the left one set the same flag), with the flag telling down from up. The fn
        // flag also rides along on arrow and function keys, but those are keyDown events, not
        // flagsChanged ones with fn's key code, so they never reach here.
        if let dictationKeyIsDownNow = dictationKey.isDown(keyCode: keyCode, modifierFlags: trackedFlags),
           dictationKeyIsDownNow != isDictationKeyHeld {
            isDictationKeyHeld = dictationKeyIsDownNow
            if dictationKeyIsDownNow {
                // Talk (control + option) with fn added by accident is still talk, not dictation.
                guard !isTalkHeldNow else {
                    isDictationKeyHeld = false
                    previousTrackedFlags = trackedFlags
                    return events
                }
                dictationHoldStartedAt = time
                keyWasPressedDuringHold = false
                editModifierJoinedDuringHold = trackedFlags.contains(.control)
                events.append(.dictationPressed)
                if editModifierJoinedDuringHold { events.append(.dictationEditModifierJoined) }
            } else {
                let holdDuration = dictationHoldStartedAt.map { time - $0 } ?? .infinity
                let wasTap = holdDuration <= Self.tapMaxHoldSeconds && !keyWasPressedDuringHold
                dictationHoldStartedAt = nil
                events.append(.dictationReleased(wasTap: wasTap))
                if editModifierJoinedDuringHold {
                    if wasTap, let lastTap = lastEditTapCompletedAt, time - lastTap <= Self.doubleTapWindowSeconds {
                        lastEditTapCompletedAt = nil
                        events.append(.handsFreeToggleRequested)
                    } else {
                        lastEditTapCompletedAt = wasTap ? time : nil
                    }
                    lastDictationTapCompletedAt = nil
                } else {
                    if wasTap, let lastTap = lastDictationTapCompletedAt, time - lastTap <= Self.doubleTapWindowSeconds {
                        lastDictationTapCompletedAt = nil
                        events.append(.dictationDoubleTapped)
                    } else {
                        lastDictationTapCompletedAt = wasTap ? time : nil
                    }
                    lastEditTapCompletedAt = nil
                }
                editModifierJoinedDuringHold = false
            }
        } else if isDictationKeyHeld, !editModifierJoinedDuringHold, trackedFlags.contains(.control), !isTalkHeldNow {
            // Control pressed while the dictation key is held: Hey Clicky mode for this take.
            editModifierJoinedDuringHold = true
            events.append(.dictationEditModifierJoined)
        }

        // A control tap: control alone goes down from no modifiers, and everything comes back up
        // shortly after without any other modifier or key joining in between.
        if trackedFlags == [.control] && previousTrackedFlags.isEmpty {
            controlTapStartedAt = time
            keyWasPressedDuringHold = false
        } else if trackedFlags.isEmpty, let tapStartedAt = controlTapStartedAt {
            controlTapStartedAt = nil
            let wasTap = time - tapStartedAt <= Self.tapMaxHoldSeconds && !keyWasPressedDuringHold && previousTrackedFlags == [.control]
            if wasTap, let lastTapCompletedAt = lastControlTapCompletedAt, time - lastTapCompletedAt <= Self.doubleTapWindowSeconds {
                lastControlTapCompletedAt = nil
                events.append(.textComposerRequested)
            } else {
                lastControlTapCompletedAt = wasTap ? time : nil
            }
        } else if trackedFlags != [.control] {
            // Another modifier joined (talk, dictate, a shortcut): not a control tap any more.
            controlTapStartedAt = nil
        }

        previousTrackedFlags = trackedFlags
        return events
    }

    private mutating func resetDictationState() {
        isDictationKeyHeld = false
        dictationHoldStartedAt = nil
        editModifierJoinedDuringHold = false
        lastDictationTapCompletedAt = nil
        lastEditTapCompletedAt = nil
    }

    mutating func reset() {
        self = CompanionShortcutRecognizer(dictationKey: dictationKey)
    }
}
