//
//  HandheldMicrophoneTests.swift
//  OpenClickyTests
//
//  The handheld's own microphone stands in for the person's choice only while everything that
//  makes it usable holds; otherwise their choice is returned untouched.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldMicrophoneTests {
    private let builtIn = MicrophoneDevice(uid: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone", isDefault: true)
    private let handheldMicrophone = MicrophoneDevice(uid: "AppleUSBAudioEngine:WhisPrompt:1", name: "WhisPrompt Mic Stream", isDefault: false)

    private func effectiveUID(personsChoiceUID: String? = "BuiltInMicrophoneDevice", pluginIsOn: Bool = true, optionIsOn: Bool = true,
                              connectedDeviceMicrophoneName: String? = "WhisPrompt Mic Stream",
                              availableMicrophones: [MicrophoneDevice]? = nil) -> String? {
        HandheldMicrophone.effectiveUID(
            personsChoiceUID: personsChoiceUID, pluginIsOn: pluginIsOn, optionIsOn: optionIsOn,
            connectedDeviceMicrophoneName: connectedDeviceMicrophoneName,
            availableMicrophones: availableMicrophones ?? [builtIn, handheldMicrophone])
    }

    @Test func theHandheldsMicrophoneWhenEverythingHolds() {
        #expect(effectiveUID() == "AppleUSBAudioEngine:WhisPrompt:1")
        #expect(effectiveUID(personsChoiceUID: nil) == "AppleUSBAudioEngine:WhisPrompt:1")
    }

    @Test func thePersonsChoiceOtherwise() {
        #expect(effectiveUID(pluginIsOn: false) == "BuiltInMicrophoneDevice")
        #expect(effectiveUID(optionIsOn: false) == "BuiltInMicrophoneDevice")
        #expect(effectiveUID(connectedDeviceMicrophoneName: nil) == "BuiltInMicrophoneDevice")
        #expect(effectiveUID(availableMicrophones: [builtIn]) == "BuiltInMicrophoneDevice")
    }

    @Test func noChoiceStaysNoChoice() {
        #expect(effectiveUID(personsChoiceUID: nil, optionIsOn: false) == nil)
        #expect(effectiveUID(personsChoiceUID: nil, availableMicrophones: [builtIn]) == nil)
    }

    @Test func theOptionIsOffAtFirstKeptAndTellsTheAppWhenItChanges() {
        let scratchSuiteName = "HandheldMicrophoneTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { FakeHandheldReportSource() }, makerAppIsRunning: { false })
        var microphoneChoiceChangeCount = 0
        plugin.onMicrophoneChoiceChanged = { microphoneChoiceChangeCount += 1 }

        #expect(!plugin.usesDeviceMicrophone)
        plugin.setUsesDeviceMicrophone(true)
        #expect(microphoneChoiceChangeCount == 1)
        #expect(HandheldPlugin(defaults: defaults, makeSource: { FakeHandheldReportSource() }, makerAppIsRunning: { false }).usesDeviceMicrophone)
    }

    @Test func aDeviceComingOrGoingTellsTheApp() {
        let scratchSuiteName = "HandheldMicrophoneTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        defaults.set(true, forKey: HandheldPlugin.enabledKey)
        let source = FakeHandheldReportSource()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { source }, makerAppIsRunning: { false })
        let host = RecordingHandheldActionHost()
        plugin.attach(to: host)
        var microphoneChoiceChangeCount = 0
        plugin.onMicrophoneChoiceChanged = { microphoneChoiceChangeCount += 1 }

        source.connectWhisPrompt()
        #expect(microphoneChoiceChangeCount == 1)
        source.disconnectWhisPrompt()
        #expect(microphoneChoiceChangeCount == 2)
    }

    @Test func theMicrophoneRowShowsOnlyForADeviceThatHasOne() {
        let scratchSuiteName = "HandheldMicrophoneTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        defaults.set(true, forKey: HandheldPlugin.enabledKey)
        let source = FakeHandheldReportSource()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { source }, makerAppIsRunning: { false })
        let host = RecordingHandheldActionHost()
        plugin.attach(to: host)

        let microphoneRowBeforeADevice = HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in false }).first { $0.id == "handheld.microphone" }
        #expect(microphoneRowBeforeADevice?.isRelevant() == false)
        source.connectWhisPrompt()
        let microphoneRow = SettingsLayout.arrange(HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in false })).first { $0.id == "handheld.microphone" }
        #expect(microphoneRow?.isRelevant() == true)
        #expect(microphoneRow?.page == .handheld)
    }
}
