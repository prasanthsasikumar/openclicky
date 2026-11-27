//
//  HandheldActionRunnerTests.swift
//  OpenClickyTests
//
//  A handheld's bound actions become the shortcut events the keyboard already produces, and a
//  hold always ends: on release, when the device goes, when the plugin stops.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
final class RecordingHandheldActionHost: HandheldActionHost {
    private(set) var shortcutEvents: [CompanionShortcutEvent] = []
    private(set) var postedCombinations: [HandheldKeyCombination] = []
    private(set) var islandOpenCount = 0
    private(set) var goToWaitingTerminalCount = 0
    /// The app's side, played simply: a question listens from talk pressed to talk released unless
    /// the press is refused, and a tapped dictation key opens a take and the next tap ends it.
    var askIsListening = false
    var dictationTakeIsInProgress = false
    var refusesTalk = false

    func performExternalShortcut(_ shortcutEvent: CompanionShortcutEvent) {
        shortcutEvents.append(shortcutEvent)
        switch shortcutEvent {
        case .talkPressed: askIsListening = !refusesTalk
        case .talkReleased: askIsListening = false
        case .dictationReleased(let wasTap, _) where wasTap: dictationTakeIsInProgress.toggle()
        default: break
        }
    }
    func postKeyCombination(_ combination: HandheldKeyCombination) { postedCombinations.append(combination) }
    func openIsland() { islandOpenCount += 1 }
    func goToWaitingTerminal() { goToWaitingTerminalCount += 1 }
}

/// A clock the test moves by hand, in place of real timers.
@MainActor
final class ManualHandheldScheduler {
    final class Work: HandheldScheduledWork {
        let dueAtSeconds: TimeInterval
        let run: () -> Void
        var isFinished = false
        init(dueAtSeconds: TimeInterval, run: @escaping () -> Void) {
            self.dueAtSeconds = dueAtSeconds
            self.run = run
        }
        func cancel() { isFinished = true }
    }

    private var scheduledWork: [Work] = []
    private(set) var elapsedSeconds: TimeInterval = 0

    func schedule(_ delaySeconds: TimeInterval, _ run: @escaping () -> Void) -> HandheldScheduledWork {
        let work = Work(dueAtSeconds: elapsedSeconds + delaySeconds, run: run)
        scheduledWork.append(work)
        return work
    }

    /// Moves the clock on, running whatever comes due on the way, in order, work scheduled by
    /// that work included.
    func advance(bySeconds seconds: TimeInterval) {
        let endSeconds = elapsedSeconds + seconds
        while let nextWork = scheduledWork.filter({ !$0.isFinished && $0.dueAtSeconds <= endSeconds }).min(by: { $0.dueAtSeconds < $1.dueAtSeconds }) {
            elapsedSeconds = nextWork.dueAtSeconds
            nextWork.isFinished = true
            nextWork.run()
        }
        elapsedSeconds = endSeconds
    }
}

@MainActor
struct HandheldActionRunnerTests {
    private let host = RecordingHandheldActionHost()
    private let scheduler = ManualHandheldScheduler()
    private let defaultBindings = WhisPromptHandheld.device.defaultBindings

    private func makeRunner() -> HandheldActionRunner {
        HandheldActionRunner(host: host, schedule: scheduler.schedule)
    }

    private func event(_ controlID: String, _ gesture: HandheldGesture) -> HandheldEvent {
        HandheldEvent(controlID: controlID, gesture: gesture, sequence: 0)
    }

    private func bindings(_ action: HandheldAction, controlID: String, gesture: HandheldGesture) -> HandheldBindings {
        var bindings = defaultBindings
        bindings.set(action, controlID: controlID, gesture: gesture)
        return bindings
    }

    @Test func holdDictationIsPressThenRelease() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        #expect(host.shortcutEvents == [.dictationPressed])
        runner.handle(event("keyA", .longEnd), bindings: defaultBindings)
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
    }

    @Test func toggleDictationIsATap() {
        let runner = makeRunner()
        runner.handle(event("keyA", .single), bindings: defaultBindings)
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: true)])
    }

    @Test func holdAskIsTalkPressedThenReleased() {
        let runner = makeRunner()
        let askOnHold = bindings(.askHold, controlID: "keyA", gesture: .longStart)
        runner.handle(event("keyA", .longStart), bindings: askOnHold)
        runner.handle(event("keyA", .longEnd), bindings: askOnHold)
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased])
    }

    @Test func askToggleAlternatesAndReleasesItselfAfterSixtySeconds() {
        let runner = makeRunner()
        let askOnKeyB = bindings(.askToggle, controlID: "keyB", gesture: .single)

        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        #expect(host.shortcutEvents == [.talkPressed])
        scheduler.advance(bySeconds: 59)
        #expect(host.shortcutEvents == [.talkPressed])
        scheduler.advance(bySeconds: 1)
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased])

        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased, .talkPressed, .talkReleased])
        // The limit of a question already sent must not release anything later.
        scheduler.advance(bySeconds: 120)
        #expect(host.shortcutEvents.count == 4)
    }

    @Test func aHoldEndsWhenTheDeviceGoes() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents.count == 2)
    }

    @Test func aHeldQuestionAndAnOpenToggledQuestionEndWhenTheDeviceGoes() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: bindings(.askHold, controlID: "keyA", gesture: .longStart))
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased])

        runner.handle(event("keyB", .single), bindings: bindings(.askToggle, controlID: "keyB", gesture: .single))
        runner.releaseEverythingHeld(reason: "switched off")
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased, .talkPressed, .talkReleased])
        scheduler.advance(bySeconds: 300)
        #expect(host.shortcutEvents.count == 4)
    }

    @Test func longEndReleasesWhatLongStartBegan() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        // The binding is changed in Settings while the key is still held.
        runner.handle(event("keyA", .longEnd), bindings: bindings(.nothing, controlID: "keyA", gesture: .longStart))
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
    }

    @Test func aLongEndWithNothingHeldDoesNothing() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longEnd), bindings: defaultBindings)
        #expect(host.shortcutEvents.isEmpty)
    }

    @Test func keyCombinationGoesToTheHostNotTheShortcuts() {
        let runner = makeRunner()
        runner.handle(event("keyB", .single), bindings: defaultBindings)
        #expect(host.postedCombinations == [HandheldKeyCombination(keyCode: 36, modifierFlags: 0, displayText: "Enter")])
        #expect(host.shortcutEvents.isEmpty)
    }

    @Test func theSingleEventActions() {
        let runner = makeRunner()
        runner.handle(event("keyA", .double), bindings: defaultBindings)
        runner.handle(event("keyB", .double), bindings: defaultBindings)
        runner.handle(event("keyC", .single), bindings: bindings(.handsFreeToggle, controlID: "keyC", gesture: .single))
        runner.handle(event("keyC", .single), bindings: bindings(.stop, controlID: "keyC", gesture: .single))
        runner.handle(event("keyC", .single), bindings: bindings(.openIsland, controlID: "keyC", gesture: .single))
        #expect(host.shortcutEvents == [.textComposerRequested, .handsFreeToggleRequested, .escapePressed])
        #expect(host.goToWaitingTerminalCount == 1)
        #expect(host.islandOpenCount == 1)
    }

    @Test func downAndNothingDoNothing() {
        let runner = makeRunner()
        runner.handle(event("keyA", .down), bindings: defaultBindings)
        runner.handle(event("keyB", .single), bindings: bindings(.nothing, controlID: "keyB", gesture: .single))
        #expect(host.shortcutEvents.isEmpty)
        #expect(host.postedCombinations.isEmpty)
    }

    @Test func aHoldActionBoundToAPressIsNeverLeftOpen() {
        // Stored bindings could carry a hold action on a gesture that has no end (edited by hand,
        // or from a device description that changed). It must not open the microphone for good.
        let runner = makeRunner()
        runner.handle(event("keyB", .single), bindings: bindings(.dictateHold, controlID: "keyB", gesture: .single))
        #expect(host.shortcutEvents.isEmpty)
    }

    // MARK: found by the whole-branch review

    @Test func aToggledTakeEndsWhenTheDeviceGoes() {
        // Key A pressed once opens a take that stays open; pulling the receiver must end it.
        let runner = makeRunner()
        runner.handle(event("keyA", .single), bindings: defaultBindings)
        #expect(host.dictationTakeIsInProgress)
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: true), .dictationPressed, .dictationReleased(wasTap: true)])
        #expect(!host.dictationTakeIsInProgress)
    }

    @Test func aToggledTakeThatAlreadyEndedIsNotStartedAgainWhenTheDeviceGoes() {
        let runner = makeRunner()
        runner.handle(event("keyA", .single), bindings: defaultBindings)
        // The take ended some other way: fn, esc, or its own silence timeout.
        host.dictationTakeIsInProgress = false
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents.count == 2)
    }

    @Test func aTakeTheKeyboardStartedIsLeftAloneWhenTheDeviceGoes() {
        let runner = makeRunner()
        host.dictationTakeIsInProgress = true
        runner.releaseEverythingHeld(reason: "disconnected")
        #expect(host.shortcutEvents.isEmpty)
    }

    @Test func aRefusedAskPressLeavesNothingOpen() {
        // "you're still dictating": the press was refused, so the next press must start a
        // question, not send a release for one that never began.
        let runner = makeRunner()
        let askOnKeyB = bindings(.askToggle, controlID: "keyB", gesture: .single)
        host.refusesTalk = true
        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        scheduler.advance(bySeconds: 2)
        host.refusesTalk = false
        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        #expect(host.shortcutEvents == [.talkPressed, .talkPressed])
        #expect(host.askIsListening)
    }

    @Test func aQuestionStoppedAtTheKeyboardIsNotReleasedLater() {
        // Esc at the keyboard ends the toggled question. A ⌃⌥ hold begun afterwards must not be
        // cut off by the old question's limit.
        let runner = makeRunner()
        runner.handle(event("keyB", .single), bindings: bindings(.askToggle, controlID: "keyB", gesture: .single))
        scheduler.advance(bySeconds: 5)
        host.askIsListening = false
        scheduler.advance(bySeconds: 2)
        host.askIsListening = true
        scheduler.advance(bySeconds: 120)
        #expect(host.shortcutEvents == [.talkPressed])
        #expect(host.askIsListening)
    }

    @Test func aHoldIsReleasedAfterItsLimit() {
        // A wireless handheld switched off or out of range mid-hold never reports the release.
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        scheduler.advance(bySeconds: HandheldActionRunner.holdLimitSeconds - 1)
        #expect(host.shortcutEvents == [.dictationPressed])
        scheduler.advance(bySeconds: 1)
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
        // The key works again afterwards.
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        #expect(host.shortcutEvents.count == 3)
    }

    @Test func aHoldReleasedInTimeIsNotReleasedAgainAtItsLimit() {
        let runner = makeRunner()
        runner.handle(event("keyA", .longStart), bindings: defaultBindings)
        runner.handle(event("keyA", .longEnd), bindings: defaultBindings)
        scheduler.advance(bySeconds: HandheldActionRunner.holdLimitSeconds * 2)
        #expect(host.shortcutEvents.count == 2)
    }
}
