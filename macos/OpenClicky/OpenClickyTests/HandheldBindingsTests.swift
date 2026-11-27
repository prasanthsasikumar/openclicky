//
//  HandheldBindingsTests.swift
//  OpenClickyTests
//
//  What a handheld's buttons do: the device's defaults, which actions a gesture may be given,
//  and that a person's own choices are kept per device.
//

import Foundation
import Testing
@testable import OpenClicky

struct HandheldBindingsTests {
    private let whisprompt = WhisPromptHandheld.device
    private let enterKey = HandheldKeyCombination(keyCode: 36, modifierFlags: 0, displayText: "Enter")
    private let deleteKey = HandheldKeyCombination(keyCode: 51, modifierFlags: 0, displayText: "Delete")

    private func makeScratchDefaults() -> (defaults: UserDefaults, suiteName: String) {
        let suiteName = "HandheldBindingsTests-\(UUID().uuidString)"
        return (UserDefaults(suiteName: suiteName)!, suiteName)
    }

    @Test func defaultsAreTheDevices() {
        let bindings = whisprompt.defaultBindings
        #expect(bindings.action(controlID: "keyA", gesture: .single) == .dictateToggle)
        #expect(bindings.action(controlID: "keyA", gesture: .double) == .typeQuestion)
        #expect(bindings.action(controlID: "keyA", gesture: .longStart) == .dictateHold)
        #expect(bindings.action(controlID: "keyB", gesture: .single) == .keyCombination(enterKey))
        #expect(bindings.action(controlID: "keyB", gesture: .double) == .goToWaitingTerminal)
        #expect(bindings.action(controlID: "keyC", gesture: .single) == .keyCombination(deleteKey))
        #expect(bindings.action(controlID: "keyC", gesture: .double)
                == .keyCombination(HandheldKeyCombination(keyCode: 6, modifierFlags: HandheldKeyCombination.commandFlag, displayText: "⌘Z")))
        #expect(bindings.action(controlID: "keyB", gesture: .longStart) == .nothing)
    }

    @Test func choicesOfferHoldOnlyWhereItCanEnd() {
        let keyA = whisprompt.control(withID: "keyA")!
        let controlThatNeverReportsRelease = HandheldControl(id: "side", name: "Side", gestures: [.single, .longStart])

        let pressChoices = HandheldAction.choices(for: .single, on: keyA)
        #expect(!pressChoices.contains(.dictateHold))
        #expect(!pressChoices.contains(.askHold))
        #expect(pressChoices.contains(.dictateToggle))
        #expect(pressChoices.contains(.askToggle))
        #expect(pressChoices.contains(.nothing))

        let holdChoicesWithoutRelease = HandheldAction.choices(for: .longStart, on: controlThatNeverReportsRelease)
        #expect(!holdChoicesWithoutRelease.contains(.dictateHold))
        #expect(!holdChoicesWithoutRelease.contains(.askHold))

        let holdChoices = HandheldAction.choices(for: .longStart, on: keyA)
        #expect(holdChoices.contains(.dictateHold))
        #expect(holdChoices.contains(.askHold))
        #expect(holdChoices.contains(.stop))
    }

    @Test func bindableGesturesAreTheOnesAPersonChooses() {
        #expect(HandheldBindings.bindableGestures(of: whisprompt.control(withID: "keyA")!) == [.single, .double, .longStart])
        #expect(HandheldBindings.bindableGestures(of: whisprompt.control(withID: "keyB")!) == [.single, .double])
    }

    @Test func savedBindingsSurviveAndResetReturnsDefaults() {
        let scratch = makeScratchDefaults()
        defer { scratch.defaults.removePersistentDomain(forName: scratch.suiteName) }

        var bindings = HandheldBindingsStore(defaults: scratch.defaults).bindings(for: whisprompt)
        bindings.set(.askToggle, controlID: "keyC", gesture: .single)
        HandheldBindingsStore(defaults: scratch.defaults).save(bindings, for: whisprompt)

        let storeReadAgain = HandheldBindingsStore(defaults: scratch.defaults)
        #expect(storeReadAgain.bindings(for: whisprompt).action(controlID: "keyC", gesture: .single) == .askToggle)
        #expect(storeReadAgain.bindings(for: whisprompt).action(controlID: "keyA", gesture: .single) == .dictateToggle)

        storeReadAgain.reset(whisprompt)
        #expect(storeReadAgain.bindings(for: whisprompt) == whisprompt.defaultBindings)
    }

    @Test func corruptStoredDataFallsBackToDefaults() {
        let scratch = makeScratchDefaults()
        defer { scratch.defaults.removePersistentDomain(forName: scratch.suiteName) }
        scratch.defaults.set(Data([0x00]), forKey: "openclicky.handheld.bindings.whisprompt")
        #expect(HandheldBindingsStore(defaults: scratch.defaults).bindings(for: whisprompt) == whisprompt.defaultBindings)
    }

    @Test func everyActionHasATitle() {
        #expect(HandheldAction.dictateHold.title == "Dictate (hold)")
        #expect(HandheldAction.askToggle.title == "Ask Clicky (press to start, press to stop)")
        #expect(HandheldAction.keyCombination(enterKey).title == "Enter")
        #expect(HandheldAction.nothing.title == "Nothing")
    }
}
