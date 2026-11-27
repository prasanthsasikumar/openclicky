//
//  HandheldActionRunner.swift
//  OpenClicky
//
//  Runs what a handheld's gesture is bound to. Dictating, asking, hands-free, the composer and
//  stop all become the shortcut events the keyboard already produces, so a button takes exactly
//  the path its key takes: the same refusals, the same island, the same history.
//
//  A hold always ends. What a long press started is remembered per control and released when the
//  long press ends, when the device goes away, when the Mac sleeps, when the plugin stops, or
//  after two minutes (a wireless handheld switched off mid-hold never reports the release),
//  whatever the bindings say by then: a microphone must never stay open because a cable was
//  pulled. A take or a question a single press opened is ended the same way.
//
//  The runner keeps as little of its own idea of the app's state as it can. Whether a question
//  is still listening, or a take still open, is asked of the host each time: either can end at
//  the keyboard, and a button acting on a stale belief would cut off the next one.
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
    /// A question is being listened to right now (⌃⌥ held, or a button standing in for it).
    var askIsListening: Bool { get }
    /// A dictation take is open right now, whoever started it.
    var dictationTakeIsInProgress: Bool { get }
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
    /// How often an open toggled question is looked at again: it may have ended at the keyboard.
    static let askToggleCheckSeconds: TimeInterval = 1
    /// The longest a hold lasts. A wireless handheld that is switched off, out of range or flat
    /// while a key is held never reports the release, and its receiver stays plugged in.
    static let holdLimitSeconds: TimeInterval = 120

    private weak var host: HandheldActionHost?
    private let schedule: (TimeInterval, @escaping () -> Void) -> HandheldScheduledWork
    /// What each control's long press started: the event that ends it, and its time limit.
    private var holdByControlID: [String: (releaseEvent: CompanionShortcutEvent, limit: HandheldScheduledWork)] = [:]
    /// True while a question started by one press waits for the press that sends it.
    private var askToggleIsOpen = false
    private var askToggleSecondsOpen: TimeInterval = 0
    private var askToggleNextCheck: HandheldScheduledWork?
    /// True when the last take a single press opened was still open after that press.
    private var aPressOpenedTheTake = false

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
        let takeOpenedByAPressIsStillOpen = aPressOpenedTheTake && (host?.dictationTakeIsInProgress ?? false)
        let somethingWasHeld = !holdByControlID.isEmpty || askToggleIsOpen || takeOpenedByAPressIsStillOpen
        for controlID in Array(holdByControlID.keys) {
            release(controlID: controlID)
        }
        closeAskToggle()
        // A take a single press opened stays open until the next press, which can no longer come.
        // A take the keyboard started is not this device's to end.
        if takeOpenedByAPressIsStillOpen {
            tapTheDictationKey()
        }
        aPressOpenedTheTake = false
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
            tapTheDictationKey()
            aPressOpenedTheTake = host?.dictationTakeIsInProgress ?? false
        case .askToggle:
            // Believed only while the app agrees: a press that was refused, or a question since
            // stopped at the keyboard, leaves nothing to send, so this press starts a new one.
            if askToggleIsOpen && (host?.askIsListening ?? false) {
                closeAskToggle()
            } else {
                forgetAskToggle()
                host?.performExternalShortcut(.talkPressed)
                askToggleIsOpen = true
                askToggleSecondsOpen = 0
                scheduleAskToggleCheck()
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
        guard event.gesture == .longStart, holdByControlID[event.controlID] == nil else { return }
        let controlID = event.controlID
        let limit = schedule(Self.holdLimitSeconds) { [weak self] in
            guard let self, self.holdByControlID[controlID] != nil else { return }
            AppLog.append("handheld: hold ended at its limit (no release was reported)")
            self.release(controlID: controlID)
        }
        holdByControlID[controlID] = (released, limit)
        host?.performExternalShortcut(pressed)
    }

    private func release(controlID: String) {
        guard let hold = holdByControlID.removeValue(forKey: controlID) else { return }
        hold.limit.cancel()
        host?.performExternalShortcut(hold.releaseEvent)
    }

    private func tapTheDictationKey() {
        host?.performExternalShortcut(.dictationPressed)
        host?.performExternalShortcut(.dictationReleased(wasTap: true))
    }

    /// Looks at an open toggled question once a second: gone at the keyboard, it is forgotten
    /// without a release (a release then would cut off a question asked since); at its limit, it
    /// is sent.
    private func scheduleAskToggleCheck() {
        askToggleNextCheck = schedule(Self.askToggleCheckSeconds) { [weak self] in
            guard let self, self.askToggleIsOpen else { return }
            guard self.host?.askIsListening ?? false else {
                self.forgetAskToggle()
                return
            }
            self.askToggleSecondsOpen += Self.askToggleCheckSeconds
            if self.askToggleSecondsOpen >= Self.askToggleLimitSeconds {
                self.closeAskToggle()
            } else {
                self.scheduleAskToggleCheck()
            }
        }
    }

    private func forgetAskToggle() {
        askToggleNextCheck?.cancel()
        askToggleNextCheck = nil
        askToggleIsOpen = false
    }

    /// Sends the open toggled question, if the app is still listening to it.
    private func closeAskToggle() {
        guard askToggleIsOpen else { return }
        let questionIsStillListening = host?.askIsListening ?? false
        forgetAskToggle()
        if questionIsStillListening {
            host?.performExternalShortcut(.talkReleased)
        }
    }
}

// MARK: - The app as the host

extension CompanionManager: HandheldActionHost {
    var askIsListening: Bool { voiceState == .listening }
    var dictationTakeIsInProgress: Bool { dictationTakeController.isTakeInProgress }

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

    /// The session the terminal notices last spoke of (`AgentSessionNotifier`); with those notices
    /// off, or nothing waiting, the newest live session's terminal.
    func goToWaitingTerminal() {
        let sessionToShow = notchHUDManager.agentSessionNotifier?.newestSessionNeedingAttention
            ?? notchHUDManager.agentSessions.sessions.first
        guard let sessionToShow else {
            AppLog.append("handheld: go to the waiting terminal (no session to go to)")
            showAskCaption("no terminal session to go to", holdSeconds: 2)
            return
        }
        TerminalWindowFinder.bringForward(sessionToShow)
    }
}
