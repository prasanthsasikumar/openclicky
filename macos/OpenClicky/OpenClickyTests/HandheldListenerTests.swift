//
//  HandheldListenerTests.swift
//  OpenClickyTests
//
//  The listener between a handheld's raw reports and the plugin: which device is connected, a
//  report decoded into an event, a report delivered twice dropped once.
//

import Foundation
import Testing
@testable import OpenClicky

/// Stands in for the IOKit side: the test plays a device being plugged in and sending reports.
final class FakeHandheldReportSource: HandheldReportSource {
    var onDeviceConnected: ((_ vendorID: Int, _ productID: Int) -> Void)?
    var onDeviceDisconnected: ((_ vendorID: Int, _ productID: Int) -> Void)?
    var onInputReport: ((_ vendorID: Int, _ productID: Int, _ reportID: UInt32, _ reportBytes: [UInt8]) -> Void)?
    private(set) var startCount = 0
    private(set) var stopCount = 0
    private(set) var matchedDevices: [(vendorID: Int, productID: Int)] = []

    func start(matching devices: [(vendorID: Int, productID: Int)]) {
        startCount += 1
        matchedDevices = devices
    }

    func stop() { stopCount += 1 }

    func connectWhisPrompt() { onDeviceConnected?(0x303a, 0x8001) }
    func disconnectWhisPrompt() { onDeviceDisconnected?(0x303a, 0x8001) }

    func sendFromWhisPrompt(_ hex: String) {
        let reportBytes = hex.split(separator: " ").map { UInt8($0, radix: 16)! }
        onInputReport?(0x303a, 0x8001, UInt32(reportBytes[0]), reportBytes)
    }
}

@MainActor
struct HandheldListenerTests {
    private let keyASingleSequence22 = "04 02 02 01 22 00 00 00 07 17 43 00 00"
    private let keyADoubleSequence24 = "04 02 02 02 24 00 00 00 8c 1b 43 00 00"

    private func makeListener() -> (listener: HandheldListener, source: FakeHandheldReportSource, events: () -> [HandheldEvent]) {
        let source = FakeHandheldReportSource()
        let listener = HandheldListener(source: source)
        var receivedEvents: [HandheldEvent] = []
        listener.onEvent = { _, event in receivedEvents.append(event) }
        return (listener, source, { receivedEvents })
    }

    @Test func decodesAndForwards() {
        let (listener, source, events) = makeListener()
        listener.start()
        #expect(listener.isRunning)
        #expect(source.matchedDevices.count == 1)
        #expect(source.matchedDevices.first?.vendorID == 0x303a)
        source.connectWhisPrompt()
        source.sendFromWhisPrompt(keyASingleSequence22)
        #expect(listener.connectedDevice?.id == "whisprompt")
        #expect(events() == [HandheldEvent(controlID: "keyA", gesture: .single, sequence: 0x22)])
    }

    @Test func dropsARepeatedSequenceOnce() {
        let (listener, source, events) = makeListener()
        listener.start()
        source.connectWhisPrompt()
        source.sendFromWhisPrompt(keyASingleSequence22)
        source.sendFromWhisPrompt(keyASingleSequence22)
        #expect(events().count == 1)
        source.sendFromWhisPrompt(keyADoubleSequence24)
        #expect(events().map(\.gesture) == [.single, .double])
    }

    @Test func aReconnectForgetsTheLastSequence() {
        let (listener, source, events) = makeListener()
        listener.start()
        source.connectWhisPrompt()
        source.sendFromWhisPrompt(keyASingleSequence22)
        source.disconnectWhisPrompt()
        #expect(listener.connectedDevice == nil)
        source.connectWhisPrompt()
        source.sendFromWhisPrompt(keyASingleSequence22)
        #expect(events().count == 2)
    }

    @Test func stopReportsADisconnectAndStopsTheSource() {
        let (listener, source, _) = makeListener()
        var disconnectedDeviceIDs: [String] = []
        listener.onDisconnect = { device in disconnectedDeviceIDs.append(device.id) }
        listener.start()
        source.connectWhisPrompt()
        listener.stop()
        #expect(disconnectedDeviceIDs == ["whisprompt"])
        #expect(listener.connectedDevice == nil)
        #expect(source.stopCount == 1)
        #expect(!listener.isRunning)
    }

    @Test func anUnknownDeviceIsIgnored() {
        let (listener, source, events) = makeListener()
        listener.start()
        source.onDeviceConnected?(0x303a, 0x1234)
        source.onInputReport?(0x303a, 0x1234, 4, [0x04, 0x02, 0x02, 0x01, 0x22, 0, 0, 0])
        #expect(listener.connectedDevice == nil)
        #expect(events().isEmpty)
    }

    @Test func aReportBeforeItsConnectStillCounts() {
        // IOKit can deliver a first report before the matching callback has been handled.
        let (listener, source, events) = makeListener()
        listener.start()
        source.sendFromWhisPrompt(keyASingleSequence22)
        #expect(listener.connectedDevice?.id == "whisprompt")
        #expect(events().count == 1)
    }

    @Test func startingTwiceStartsTheSourceOnce() {
        let (listener, source, _) = makeListener()
        listener.start()
        listener.start()
        #expect(source.startCount == 1)
    }
}
