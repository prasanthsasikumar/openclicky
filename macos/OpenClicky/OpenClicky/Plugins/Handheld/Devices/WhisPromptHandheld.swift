//
//  WhisPromptHandheld.swift
//  OpenClicky
//
//  The WhisPrompt handheld: three keys, a wheel and a touch pad on a USB receiver, with its own
//  microphone. Measured 2026-11-27 with its maker's app quit: the keys arrive by themselves as
//  input report 4 on the device's vendor-defined HID collection; the wheel and the touch pad are
//  an ordinary mouse, which macOS already handles, so they are not controls here.
//
//  Report 4, after the report id byte:
//
//      04 02 KK GG SS SS SS SS T0 T1 T2 T3 00
//            |  |  |           '-- the device's clock, not used
//            |  |  '-- event counter, little endian
//            |  '-- gesture: 05 down, 01 single, 02 double, 03 long press start, 04 long press end
//            '-- key: 02, 01, 00
//

import Foundation

enum WhisPromptHandheld {
    static let keyEventReportID: UInt32 = 4
    /// Report id, a constant byte, key, gesture, four counter bytes.
    private static let shortestKeyEventLength = 8

    static let device = HandheldDevice(
        id: "whisprompt",
        displayName: "WhisPrompt handheld",
        vendorID: 0x303a,
        productID: 0x8001,
        microphoneName: "WhisPrompt Mic Stream",
        controls: controls,
        defaultBindings: defaultBindings,
        decode: decode
    )

    /// Only the first key reports going down and being held; the other two report whole presses.
    static let controls: [HandheldControl] = [
        HandheldControl(id: "keyA", name: "Key A", gestures: [.down, .single, .double, .longStart, .longEnd]),
        HandheldControl(id: "keyB", name: "Key B", gestures: [.single, .double]),
        HandheldControl(id: "keyC", name: "Key C", gestures: [.single, .double]),
    ]

    /// Key A is the voice key; B and C stand in for the keys a hand is away from while it holds
    /// the device.
    static let defaultBindings = HandheldBindings([
        (controlID: "keyA", gesture: .single, action: .dictateToggle),
        (controlID: "keyA", gesture: .double, action: .typeQuestion),
        (controlID: "keyA", gesture: .longStart, action: .dictateHold),
        (controlID: "keyB", gesture: .single, action: .keyCombination(HandheldKeyCombination(keyCode: 36, modifierFlags: 0, displayText: "Enter"))),
        (controlID: "keyB", gesture: .double, action: .goToWaitingTerminal),
        (controlID: "keyC", gesture: .single, action: .keyCombination(HandheldKeyCombination(keyCode: 51, modifierFlags: 0, displayText: "Delete"))),
        (controlID: "keyC", gesture: .double, action: .keyCombination(HandheldKeyCombination(keyCode: 6, modifierFlags: HandheldKeyCombination.commandFlag, displayText: "⌘Z"))),
    ])

    private static let controlIDByKeyByte: [UInt8: String] = [0x02: "keyA", 0x01: "keyB", 0x00: "keyC"]
    private static let gestureByGestureByte: [UInt8: HandheldGesture] = [
        0x05: .down, 0x01: .single, 0x02: .double, 0x03: .longStart, 0x04: .longEnd,
    ]

    static func decode(reportID: UInt32, reportBytes: [UInt8]) -> HandheldEvent? {
        guard reportID == keyEventReportID, reportBytes.count >= shortestKeyEventLength else { return nil }
        guard let controlID = controlIDByKeyByte[reportBytes[2]],
              let gesture = gestureByGestureByte[reportBytes[3]],
              let control = controls.first(where: { $0.id == controlID }),
              control.gestures.contains(gesture) else { return nil }
        let sequence = UInt32(reportBytes[4]) | UInt32(reportBytes[5]) << 8 | UInt32(reportBytes[6]) << 16 | UInt32(reportBytes[7]) << 24
        return HandheldEvent(controlID: controlID, gesture: gesture, sequence: sequence)
    }
}
