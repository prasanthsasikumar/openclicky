//
//  HandheldActionRunner.swift
//  OpenClicky
//
//  Runs what a handheld's gesture is bound to. Dictating, asking, hands-free, the composer and
//  stop all become the shortcut events the keyboard already produces, so a button takes exactly
//  the path its key takes: the same refusals, the same island, the same history.
//
//  A hold always ends. What a long press started is remembered per control and released when the
//  long press ends, when the device goes away, or when the plugin stops, whatever the bindings
//  say by then: a microphone must never stay open because a cable was pulled.
//

import AppKit
import Foundation

/// The parts of the app a handheld's actions reach.
@MainActor
protocol HandheldActionHost: AnyObject {
    func performExternalShortcut(_ shortcutEvent: CompanionShortcutEvent)
    func postKeyCombination(_ combination: HandheldKeyCombination)
    func openIsland()
    func goToWaitingTerminal()
}

/// Something scheduled for later that can still be called off.
protocol HandheldScheduledWork {
    func cancel()
}

extension DispatchWorkItem: HandheldScheduledWork {}

@MainActor
final class HandheldActionRunner {
    /// A question started with one press is sent by itself after this long, in case the second
    /// press never comes.
    static let askToggleLimitSeconds: TimeInterval = 60

    private weak var host: HandheldActionHost?
    private let schedule: (TimeInterval, @escaping () -> Void) -> HandheldScheduledWork
    /// The event that ends what each control's long press started.
    private var releaseEventByHeldControlID: [String: CompanionShortcutEvent] = [:]
    /// Set while a question started by one press waits for the press that sends it.
    private var openAskToggleLimit: HandheldScheduledWork?

    init(host: HandheldActionHost, schedule: @escaping (TimeInterval, @escaping () -> Void) -> HandheldScheduledWork) {
        self.host = host
        self.schedule = schedule
    }

    /// The runner the app uses: the limit runs on the main queue.
    convenience init(host: HandheldActionHost) {
        self.init(host: host) { delaySeconds, work in
            let workItem = DispatchWorkItem(block: work)
            DispatchQueue.main.asyncAfter(deadline: .now() + delaySeconds, execute: workItem)
            return workItem
        }
    }

    func handle(_ event: HandheldEvent, bindings: HandheldBindings) {
        switch event.gesture {
        case .down:
            // Reported before the device knows which gesture this press will be.
            return
        case .longEnd:
            release(controlID: event.controlID)
        case .single, .double, .longStart:
            run(bindings.action(controlID: event.controlID, gesture: event.gesture), for: event)
        }
    }

    /// The device went away or the plugin stopped: every hold and an open toggled question end
    /// as if the control had been released.
    func releaseEverythingHeld(reason: String) {
        let somethingWasHeld = !releaseEventByHeldControlID.isEmpty || openAskToggleLimit != nil
        for controlID in Array(releaseEventByHeldControlID.keys) {
            release(controlID: controlID)
        }
        closeAskToggle()
        if somethingWasHeld {
            AppLog.append("handheld: released what was held (\(reason))")
        }
    }

    private func run(_ action: HandheldAction, for event: HandheldEvent) {
        switch action {
        case .dictateHold:
            beginHold(event, pressed: .dictationPressed, released: .dictationReleased(wasTap: false))
        case .askHold:
            beginHold(event, pressed: .talkPressed, released: .talkReleased)
        case .dictateToggle:
            // The dictation key tapped: the take starts and stays open until the next tap.
            host?.performExternalShortcut(.dictationPressed)
            host?.performExternalShortcut(.dictationReleased(wasTap: true))
        case .askToggle:
            if openAskToggleLimit != nil {
                closeAskToggle()
            } else {
                host?.performExternalShortcut(.talkPressed)
                openAskToggleLimit = schedule(Self.askToggleLimitSeconds) { [weak self] in
                    self?.closeAskToggle()
                }
            }
        case .handsFreeToggle:
            host?.performExternalShortcut(.handsFreeToggleRequested)
        case .typeQuestion:
            host?.performExternalShortcut(.textComposerRequested)
        case .stop:
            host?.performExternalShortcut(.escapePressed)
        case .goToWaitingTerminal:
            host?.goToWaitingTerminal()
        case .openIsland:
            host?.openIsland()
        case .keyCombination(let combination):
            host?.postKeyCombination(combination)
        case .nothing:
            return
        }
    }

    /// A hold starts only on a long press, the one gesture whose end the device reports.
    private func beginHold(_ event: HandheldEvent, pressed: CompanionShortcutEvent, released: CompanionShortcutEvent) {
        guard event.gesture == .longStart, releaseEventByHeldControlID[event.controlID] == nil else { return }
        releaseEventByHeldControlID[event.controlID] = released
        host?.performExternalShortcut(pressed)
    }

    private func release(controlID: String) {
        guard let releaseEvent = releaseEventByHeldControlID.removeValue(forKey: controlID) else { return }
        host?.performExternalShortcut(releaseEvent)
    }

    private func closeAskToggle() {
        guard let limit = openAskToggleLimit else { return }
        limit.cancel()
        openAskToggleLimit = nil
        host?.performExternalShortcut(.talkReleased)
    }
}

// MARK: - The app as the host

extension CompanionManager: HandheldActionHost {
    /// Posts one key with its modifiers to the app in front. Stamped like live typing's
    /// keystrokes, so OpenClicky's own shortcut tap ignores it: a button bound to esc or ⌃⌥ must
    /// not start or stop anything here through the keyboard's path.
    func postKeyCombination(_ combination: HandheldKeyCombination) {
        guard AXIsProcessTrusted(), let eventSource = CGEventSource(stateID: .privateState) else {
            AppLog.append("handheld: key not posted (no accessibility permission)")
            return
        }
        eventSource.userData = FrontAppTextInserter.liveTypingEventMarker
        guard let keyDown = CGEvent(keyboardEventSource: eventSource, virtualKey: CGKeyCode(combination.keyCode), keyDown: true),
              let keyUp = CGEvent(keyboardEventSource: eventSource, virtualKey: CGKeyCode(combination.keyCode), keyDown: false) else { return }
        for event in [keyDown, keyUp] {
            event.flags = CGEventFlags(rawValue: combination.modifierFlags)
            event.setIntegerValueField(.eventSourceUserData, value: FrontAppTextInserter.liveTypingEventMarker)
        }
        keyDown.post(tap: .cgSessionEventTap)
        keyUp.post(tap: .cgSessionEventTap)
    }

    func openIsland() {
        notchHUDManager.openPanel()
    }

    func goToWaitingTerminal() {
        AppLog.append("handheld: go to the waiting terminal (nothing is waiting)")
    }
}
