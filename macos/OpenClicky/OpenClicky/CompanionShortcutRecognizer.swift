//
//  CompanionShortcutRecognizer.swift
//  OpenClicky
//
//  Recognises the four HeyClicky keyboard shortcuts from the raw modifier / key events of the
//  global event tap:
//    Talk        hold control + option           (push-to-talk; released = send)
//    Text        tap control twice               (opens the text composer in the notch)
//    Dictate     hold fn + control               (speech is typed into the app in front)
//    Hands-free  tap fn + control twice          (toggles always-on listening)
//  A pure state machine — no NSEvent objects — so it is unit-testable.
//

import AppKit
import Foundation

/// What the keyboard asked for.
enum CompanionShortcutEvent: Equatable {
    case talkPressed
    case talkReleased
    case dictatePressed
    /// `wasTap`: fn + control came back up within the tap window, so nothing worth typing was said.
    case dictateReleased(wasTap: Bool)
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

    /// The modifiers that take part in the shortcuts; caps lock and the like are ignored.
    private static let trackedModifierFlags: NSEvent.ModifierFlags = [.control, .option, .shift, .command, .function]

    private(set) var isTalkHeld = false
    private(set) var isDictateHeld = false

    private var previousTrackedFlags: NSEvent.ModifierFlags = []
    /// When control alone went down (a possible tap), or nil.
    private var controlTapStartedAt: TimeInterval?
    private var lastControlTapCompletedAt: TimeInterval?
    /// When fn + control went down, or nil.
    private var dictateHoldStartedAt: TimeInterval?
    private var lastDictateTapCompletedAt: TimeInterval?
    /// A real key was pressed while the modifier was down (control + C, fn + arrow): not a tap.
    private var keyWasPressedDuringHold = false

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
            lastDictateTapCompletedAt = nil
            return []
        case .flagsChanged:
            break
        }

        var events: [CompanionShortcutEvent] = []
        let trackedFlags = rawModifierFlags.intersection(Self.trackedModifierFlags)
        let isTalkHeldNow = trackedFlags.contains([.control, .option])
        // fn + control, but not while option is down (that is talk with fn added by accident).
        let isDictateHeldNow = trackedFlags.contains([.control, .function]) && !trackedFlags.contains(.option)

        if isTalkHeldNow != isTalkHeld {
            isTalkHeld = isTalkHeldNow
            events.append(isTalkHeldNow ? .talkPressed : .talkReleased)
        }

        if isDictateHeldNow != isDictateHeld {
            isDictateHeld = isDictateHeldNow
            if isDictateHeldNow {
                dictateHoldStartedAt = time
                keyWasPressedDuringHold = false
                events.append(.dictatePressed)
            } else {
                let holdDuration = dictateHoldStartedAt.map { time - $0 } ?? .infinity
                let wasTap = holdDuration <= Self.tapMaxHoldSeconds && !keyWasPressedDuringHold
                dictateHoldStartedAt = nil
                events.append(.dictateReleased(wasTap: wasTap))
                if wasTap, let lastTapCompletedAt = lastDictateTapCompletedAt, time - lastTapCompletedAt <= Self.doubleTapWindowSeconds {
                    lastDictateTapCompletedAt = nil
                    events.append(.handsFreeToggleRequested)
                } else {
                    lastDictateTapCompletedAt = wasTap ? time : nil
                }
            }
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

    mutating func reset() {
        self = CompanionShortcutRecognizer()
    }
}
