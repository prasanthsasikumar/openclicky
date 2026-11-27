//
//  HandheldTabCyclingTests.swift
//  OpenClickyTests
//
//  Tab cycling: a button switches a mode on and off, and while it is on each click of the wheel
//  is ⌃Tab (down, the next tab) or ⌃⇧Tab (up, the one before) in the app in front.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldTabCyclingTests {
    @Test func itIsAnActionAButtonCanBeGiven() {
        let wheelButton = WhisPromptHandheld.device.control(withID: "wheelButton")!
        #expect(HandheldAction.cycleTabs.title == "Cycle tabs with the wheel")
        #expect(HandheldAction.choices(for: .double, on: wheelButton).contains(.cycleTabs))
        #expect(WhisPromptHandheld.device.defaultBindings.action(controlID: "wheelButton", gesture: .double) == .cycleTabs)
    }

    @Test func theButtonSwitchesTheModeThroughTheHost() {
        let host = RecordingHandheldActionHost()
        let runner = HandheldActionRunner(host: host, schedule: ManualHandheldScheduler().schedule)
        runner.handle(HandheldEvent(controlID: "wheelButton", gesture: .double, sequence: 1), bindings: WhisPromptHandheld.device.defaultBindings)
        #expect(host.tabCyclingToggleCount == 1)
    }

    /// The wheel must never stay taken away from scrolling: the device going, the Mac sleeping
    /// and the plugin switching off all end the mode.
    @Test func theModeEndsWithEverythingElseThatIsHeld() {
        let host = RecordingHandheldActionHost()
        let runner = HandheldActionRunner(host: host, schedule: ManualHandheldScheduler().schedule)
        runner.releaseEverythingHeld(reason: "device disconnected")
        #expect(host.tabCyclingStopCount == 1)
    }

    @Test func aClickDownIsTheNextTabAndAClickUpTheOneBefore() {
        let next = HandheldTabCycling.keyCombination(forScrollDeltaY: -5)
        let previous = HandheldTabCycling.keyCombination(forScrollDeltaY: 5)
        #expect(next == HandheldKeyCombination(keyCode: 48, modifierFlags: CGEventFlags.maskControl.rawValue, displayText: "⌃Tab"))
        #expect(previous == HandheldKeyCombination(keyCode: 48, modifierFlags: CGEventFlags.maskControl.union(.maskShift).rawValue, displayText: "⌃⇧Tab"))
        #expect(HandheldTabCycling.keyCombination(forScrollDeltaY: 0) == nil)
    }

    /// A trackpad's smooth scrolling is left alone, so the page under the pointer still scrolls
    /// while the mode is on; only a wheel's clicks cycle tabs.
    @Test func onlyAWheelsClicksAreTaken() {
        #expect(HandheldTabCycling.takes(scrollIsContinuous: false))
        #expect(!HandheldTabCycling.takes(scrollIsContinuous: true))
    }

    @Test func theModeEndsByItselfWhenTheWheelIsLeftAlone() {
        #expect(HandheldTabCycling.idleSecondsBeforeItEnds == 30)
    }
}
