//
//  HandheldDeviceTests.swift
//  OpenClickyTests
//
//  The WhisPrompt handheld's key events, decoded from reports recorded off the device
//  (2026-11-27, with its maker's app quit).
//

import Foundation
import Testing
@testable import OpenClicky

struct HandheldDeviceTests {
    private let whisprompt = HandheldDeviceCatalog.device(vendorID: 0x303a, productID: 0x8001)!

    private func bytes(_ hex: String) -> [UInt8] {
        hex.split(separator: " ").map { UInt8($0, radix: 16)! }
    }

    @Test func keyADownSingleDoubleLong() {
        #expect(whisprompt.decode(4, bytes("04 02 02 05 21 00 00 00 10 15 43 00 00")) == HandheldEvent(controlID: "keyA", gesture: .down, sequence: 0x21))
        #expect(whisprompt.decode(4, bytes("04 02 02 01 22 00 00 00 07 17 43 00 00"))?.gesture == .single)
        #expect(whisprompt.decode(4, bytes("04 02 02 02 24 00 00 00 8c 1b 43 00 00"))?.gesture == .double)
        #expect(whisprompt.decode(4, bytes("04 02 02 03 26 00 00 00 e0 21 43 00 00"))?.gesture == .longStart)
        #expect(whisprompt.decode(4, bytes("04 02 02 04 27 00 00 00 c0 2d 43 00 00"))?.gesture == .longEnd)
    }

    @Test func keysBAndC() {
        #expect(whisprompt.decode(4, bytes("04 02 01 01 28 00 00 00 d3 31 43 00 00")) == HandheldEvent(controlID: "keyB", gesture: .single, sequence: 0x28))
        #expect(whisprompt.decode(4, bytes("04 02 00 02 2b 00 00 00 f8 40 43 00 00")) == HandheldEvent(controlID: "keyC", gesture: .double, sequence: 0x2b))
    }

    @Test func everythingElseIsNoEvent() {
        // The status report that repeats every second.
        #expect(whisprompt.decode(3, bytes("03 02 08 05 01 00 00 00 09 00 03 64 01 2f 24 90 fa 0b 00")) == nil)
        // A wheel click, which macOS already handles as a mouse.
        #expect(whisprompt.decode(2, bytes("02 00 00 00 00 00 fb 00")) == nil)
        // A key and a gesture the device description does not list, and a report cut short.
        #expect(whisprompt.decode(4, bytes("04 02 07 01 28 00 00 00")) == nil)
        #expect(whisprompt.decode(4, bytes("04 02 02 09 28 00 00 00")) == nil)
        #expect(whisprompt.decode(4, bytes("04 02 01 03 28 00 00 00")) == nil)
        #expect(whisprompt.decode(4, bytes("04 02")) == nil)
        #expect(HandheldDeviceCatalog.device(vendorID: 0x303a, productID: 0x1234) == nil)
    }
}
