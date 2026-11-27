//
//  HandheldPluginTests.swift
//  OpenClickyTests
//
//  The handheld plugin as a whole: off, nothing is made; on, a press runs its binding; switched
//  off mid-hold, the hold ends.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldPluginTests {
    private let host = RecordingHandheldActionHost()
    private let scratchSuiteName = "HandheldPluginTests-\(UUID().uuidString)"
    private let keyASingle = "04 02 02 01 22 00 00 00 07 17 43 00 00"
    private let keyALongStart = "04 02 02 03 26 00 00 00 e0 21 43 00 00"

    private final class SourceFactory {
        private(set) var madeSources: [FakeHandheldReportSource] = []
        func make() -> HandheldReportSource {
            let source = FakeHandheldReportSource()
            madeSources.append(source)
            return source
        }
    }

    private func makePlugin(switchedOn: Bool, makerAppIsRunning: Bool = false) -> (plugin: HandheldPlugin, factory: SourceFactory, defaults: UserDefaults) {
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defaults.removePersistentDomain(forName: scratchSuiteName)
        if switchedOn { defaults.set(true, forKey: HandheldPlugin.enabledKey) }
        let factory = SourceFactory()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: factory.make, makerAppIsRunning: { makerAppIsRunning })
        return (plugin, factory, defaults)
    }

    @Test func offMeansNoSourceIsEverMade() {
        let (plugin, factory, defaults) = makePlugin(switchedOn: false)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        plugin.attach(to: host)
        #expect(!plugin.isEnabled)
        #expect(factory.madeSources.isEmpty)
    }

    @Test func switchingOnStartsAndOffStopsAndReleases() {
        let (plugin, factory, defaults) = makePlugin(switchedOn: false)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        plugin.attach(to: host)
        plugin.setEnabled(true)
        #expect(defaults.bool(forKey: HandheldPlugin.enabledKey))
        #expect(factory.madeSources.count == 1)
        let source = factory.madeSources[0]
        #expect(source.startCount == 1)

        source.connectWhisPrompt()
        source.sendFromWhisPrompt(keyALongStart)
        #expect(host.shortcutEvents == [.dictationPressed])

        plugin.setEnabled(false)
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
        #expect(source.stopCount == 1)
        #expect(plugin.connectedDevice == nil)
        #expect(!defaults.bool(forKey: HandheldPlugin.enabledKey))
    }

    @Test func aPressRunsItsBinding() {
        let (plugin, factory, defaults) = makePlugin(switchedOn: true)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        plugin.attach(to: host)
        factory.madeSources[0].connectWhisPrompt()
        factory.madeSources[0].sendFromWhisPrompt(keyASingle)
        #expect(plugin.connectedDevice?.id == "whisprompt")
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: true)])
    }

    @Test func unpluggingMidHoldEndsTheHold() {
        let (plugin, factory, defaults) = makePlugin(switchedOn: true)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        plugin.attach(to: host)
        factory.madeSources[0].connectWhisPrompt()
        factory.madeSources[0].sendFromWhisPrompt(keyALongStart)
        factory.madeSources[0].disconnectWhisPrompt()
        #expect(host.shortcutEvents == [.dictationPressed, .dictationReleased(wasTap: false)])
    }

    @Test func bindingsEditedArePersistedForTheDevice() {
        let (plugin, factory, defaults) = makePlugin(switchedOn: true)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        plugin.attach(to: host)
        factory.madeSources[0].connectWhisPrompt()
        plugin.setAction(.askToggle, controlID: "keyC", gesture: .single)

        let pluginReadAgain = HandheldPlugin(defaults: defaults, makeSource: factory.make, makerAppIsRunning: { false })
        // The device is not connected for this one: the last one seen keeps its bindings editable.
        #expect(pluginReadAgain.lastSeenDevice?.id == "whisprompt")
        #expect(pluginReadAgain.bindings.action(controlID: "keyC", gesture: .single) == .askToggle)

        pluginReadAgain.resetBindings()
        #expect(pluginReadAgain.bindings == WhisPromptHandheld.device.defaultBindings)
    }

    @Test func theMakerAppRunningIsReported() {
        let (plugin, _, defaults) = makePlugin(switchedOn: false, makerAppIsRunning: true)
        defer { defaults.removePersistentDomain(forName: scratchSuiteName) }
        #expect(plugin.makerAppIsAlsoRunning)
    }
}
