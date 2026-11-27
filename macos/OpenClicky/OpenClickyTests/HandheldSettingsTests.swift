//
//  HandheldSettingsTests.swift
//  OpenClickyTests
//
//  The handheld plugin in Settings: one folded switch for everyone, and a page of bindings only
//  for someone who switched it on.
//

import AppKit
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldSettingsTests {
    private let scratchSuiteName = "HandheldSettingsTests-\(UUID().uuidString)"
    private let host = RecordingHandheldActionHost()

    private func makePlugin(switchedOn: Bool, deviceSeen: Bool, makerAppIsRunning: Bool = false) -> HandheldPlugin {
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defaults.removePersistentDomain(forName: scratchSuiteName)
        if switchedOn { defaults.set(true, forKey: HandheldPlugin.enabledKey) }
        let source = FakeHandheldReportSource()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { source }, makerAppIsRunning: { makerAppIsRunning })
        plugin.attach(to: host)
        if deviceSeen { source.connectWhisPrompt() }
        return plugin
    }

    private func relevantIDs(_ items: [SettingsItem]) -> [String] {
        items.filter { $0.isRelevant() }.map(\.id)
    }

    @Test func everyRowHasAPlace() {
        let items = HandheldSettings.items(plugin: makePlugin(switchedOn: true, deviceSeen: true))
        let arranged = SettingsLayout.arrange(items)
        #expect(Set(arranged.map(\.id)) == Set(items.map(\.id)))
        #expect(arranged.count == items.count)
        for item in arranged {
            let expectedPage: DictationSettingsPage = ["handheld.enabled", "island.openHandheld"].contains(item.id) ? .island : .handheld
            #expect(item.page == expectedPage, "\(item.id) is on \(item.page)")
        }
    }

    @Test func offShowsOnlyTheSwitchAndItIsFolded() {
        let items = SettingsLayout.arrange(HandheldSettings.items(plugin: makePlugin(switchedOn: false, deviceSeen: false)))
        #expect(relevantIDs(items) == ["handheld.enabled"])
        #expect(items.first { $0.id == "handheld.enabled" }?.isAdvanced == true)
        #expect(items.first { $0.id == "handheld.enabled" }?.page == .island)
    }

    @Test func onWithNoDeviceEverSeenShowsTheStatusButNoPickers() {
        let ids = relevantIDs(HandheldSettings.items(plugin: makePlugin(switchedOn: true, deviceSeen: false)))
        #expect(ids.contains("island.openHandheld"))
        #expect(ids.contains("handheld.status"))
        #expect(!ids.contains { $0.hasPrefix("handheld.binding.") })
        #expect(!ids.contains("handheld.microphone"))
    }

    @Test func aControlGetsOnePickerPerBindableGesture() {
        let ids = relevantIDs(HandheldSettings.items(plugin: makePlugin(switchedOn: true, deviceSeen: true)))
        #expect(ids.filter { $0.hasPrefix("handheld.binding.") } == [
            "handheld.binding.whisprompt.keyA.single", "handheld.binding.whisprompt.keyA.double", "handheld.binding.whisprompt.keyA.longStart",
            "handheld.binding.whisprompt.keyB.single", "handheld.binding.whisprompt.keyB.double", "handheld.binding.whisprompt.keyB.longStart",
            "handheld.binding.whisprompt.keyC.single", "handheld.binding.whisprompt.keyC.double", "handheld.binding.whisprompt.keyC.longStart",
            "handheld.binding.whisprompt.wheelButton.single", "handheld.binding.whisprompt.wheelButton.double", "handheld.binding.whisprompt.wheelButton.longStart",
        ])
        #expect(ids.contains("handheld.mouseNote"))
    }

    @Test func theMakerAppNoteFollowsTheApp() {
        let idsWithTheAppRunning = relevantIDs(HandheldSettings.items(plugin: makePlugin(switchedOn: true, deviceSeen: true, makerAppIsRunning: true)))
        #expect(idsWithTheAppRunning.contains("handheld.makerAppNote"))
        let idsWithout = relevantIDs(HandheldSettings.items(plugin: makePlugin(switchedOn: true, deviceSeen: true)))
        #expect(!idsWithout.contains("handheld.makerAppNote"))
    }

    @Test func gestureLabelsAreInPlainWords() {
        #expect(HandheldSettings.label(for: .single) == "press")
        #expect(HandheldSettings.label(for: .double) == "twice")
        #expect(HandheldSettings.label(for: .longStart) == "hold")
    }

    @Test func aRecordedKeyIsNamedTheWayAKeyboardShowsIt() {
        #expect(HandheldKeyCombination.recorded(keyCode: 36, modifiers: [], characters: "\r").displayText == "Enter")
        #expect(HandheldKeyCombination.recorded(keyCode: 6, modifiers: [.command], characters: "z").displayText == "⌘Z")
        #expect(HandheldKeyCombination.recorded(keyCode: 6, modifiers: [.command, .shift], characters: "z").displayText == "⇧⌘Z")
        #expect(HandheldKeyCombination.recorded(keyCode: 49, modifiers: [.control, .option], characters: " ").displayText == "⌃⌥Space")
        #expect(HandheldKeyCombination.recorded(keyCode: 123, modifiers: [], characters: "").displayText == "←")
        // The posted flags are the Quartz ones for the same modifiers.
        #expect(HandheldKeyCombination.recorded(keyCode: 6, modifiers: [.command], characters: "z")
                == HandheldKeyCombination(keyCode: 6, modifierFlags: HandheldKeyCombination.commandFlag, displayText: "⌘Z"))
    }
}
