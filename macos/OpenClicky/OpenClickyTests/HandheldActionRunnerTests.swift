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

    func performExternalShortcut(_ shortcutEvent: CompanionShortcutEvent) { shortcutEvents.append(shortcutEvent) }
    func postKeyCombination(_ combination: HandheldKeyCombination) { postedCombinations.append(combination) }
    func openIsland() { islandOpenCount += 1 }
    func goToWaitingTerminal() { goToWaitingTerminalCount += 1 }
}

/// A scheduler the test fires by hand, in place of a real timer.
@MainActor
final class ManualHandheldScheduler {
    final class Work: HandheldScheduledWork {
        let delaySeconds: TimeInterval
        let run: () -> Void
        private(set) var wasCancelled = false
        init(delaySeconds: TimeInterval, run: @escaping () -> Void) {
            self.delaySeconds = delaySeconds
            self.run = run
        }
        func cancel() { wasCancelled = true }
    }

    private(set) var scheduledWork: [Work] = []

    func schedule(_ delaySeconds: TimeInterval, _ run: @escaping () -> Void) -> HandheldScheduledWork {
        let work = Work(delaySeconds: delaySeconds, run: run)
        scheduledWork.append(work)
        return work
    }

    func fireEverythingNotCancelled() {
        for work in scheduledWork where !work.wasCancelled { work.run() }
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
        #expect(scheduler.scheduledWork.last?.delaySeconds == 60)
        scheduler.fireEverythingNotCancelled()
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased])

        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        runner.handle(event("keyB", .single), bindings: askOnKeyB)
        #expect(host.shortcutEvents == [.talkPressed, .talkReleased, .talkPressed, .talkReleased])
        #expect(scheduler.scheduledWork.last?.wasCancelled == true)
        // The limit of a question already sent must not release anything later.
        scheduler.fireEverythingNotCancelled()
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
        scheduler.fireEverythingNotCancelled()
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
}
