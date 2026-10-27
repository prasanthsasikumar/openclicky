//
//  CompanionShortcutRecognizerTests.swift
//  OpenClickyTests
//
//  The keyboard shortcuts (dictate, Hey Clicky, talk, text, hands-free) recognised from modifier
//  events. The dictation key is identified by its key code on the flagsChanged event, as macOS
//  reports it.
//

import AppKit
import Testing
@testable import OpenClicky

struct CompanionShortcutRecognizerTests {
    private static let fnKeyCode: UInt16 = 63
    private static let leftControlKeyCode: UInt16 = 59
    private static let leftOptionKeyCode: UInt16 = 58
    private static let globeKeyCode: UInt16 = 179

    private func flags(_ recognizer: inout CompanionShortcutRecognizer, _ modifierFlags: NSEvent.ModifierFlags, keyCode: UInt16 = 0, at time: TimeInterval) -> [CompanionShortcutEvent] {
        recognizer.handle(.flagsChanged, keyCode: keyCode, modifierFlags: modifierFlags, at: time)
    }

    @Test func holdingControlAndOptionIsTalk() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0) == [])
        #expect(flags(&recognizer, [.control, .option], keyCode: Self.leftOptionKeyCode, at: 0.05) == [.talkPressed])
        #expect(flags(&recognizer, [.control], keyCode: Self.leftOptionKeyCode, at: 2.0) == [.talkReleased])
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 2.05) == [])
    }

    @Test func tappingControlTwiceOpensTheTextComposer() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0) == [])
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.1) == [])
        #expect(flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0.3) == [])
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.4) == [.textComposerRequested])
    }

    @Test func slowControlTapsDoNotOpenTheComposer() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.1)
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 1.0)
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 1.1) == [])
    }

    @Test func controlShortcutsLikeControlCAreNotTaps() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        #expect(recognizer.handle(.keyDown, keyCode: 8, modifierFlags: [.control], at: 0.05) == [])
        _ = recognizer.handle(.keyUp, keyCode: 8, modifierFlags: [.control], at: 0.1)
        _ = flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.15)
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0.3)
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.4) == [])
    }

    @Test func aTalkPressInsideTheTapWindowIsNotAControlTap() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [.control, .option], keyCode: Self.leftOptionKeyCode, at: 0.05)
        _ = flags(&recognizer, [.control], keyCode: Self.leftOptionKeyCode, at: 0.1)
        _ = flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.15)
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0.3)
        #expect(flags(&recognizer, [], keyCode: Self.leftControlKeyCode, at: 0.4) == [])
    }

    // MARK: dictation

    @Test func holdingFnIsADictationTake() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0) == [.dictationPressed])
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 3.0) == [.dictationReleased(wasTap: false)])
    }

    @Test func aQuickFnPressIsATap() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0)
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 0.2) == [.dictationReleased(wasTap: true)])
    }

    @Test func twoQuickFnTapsCancel() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0)
        _ = flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 0.1)
        _ = flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0.3)
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 0.4) == [.dictationReleased(wasTap: true), .dictationDoubleTapped])
    }

    @Test func fnWithAnArrowKeyIsNotATap() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0)
        _ = recognizer.handle(.keyDown, keyCode: 123, modifierFlags: [.function], at: 0.05)
        _ = recognizer.handle(.keyUp, keyCode: 123, modifierFlags: [.function], at: 0.1)
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 0.2) == [.dictationReleased(wasTap: false, keyWasPressedDuringHold: true)])
    }

    @Test func controlJoiningAHeldFnIsHeyClicky() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.function], keyCode: Self.fnKeyCode, at: 0) == [.dictationPressed])
        #expect(flags(&recognizer, [.function, .control], keyCode: Self.leftControlKeyCode, at: 0.1) == [.dictationEditModifierJoined])
        #expect(flags(&recognizer, [.function], keyCode: Self.leftControlKeyCode, at: 2.0) == [])
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 2.1) == [.dictationReleased(wasTap: false)])
    }

    @Test func fnPressedWhileControlIsDownIsHeyClickyFromTheStart() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        #expect(flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.05) == [.dictationEditPressed])
    }

    @Test func tappingFnAndControlTwiceTogglesHandsFree() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.02)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 0.1) == [.dictationReleased(wasTap: true)])
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.3)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 0.4) == [.dictationReleased(wasTap: true), .handsFreeToggleRequested])
    }

    /// Recorded on a MacBook Pro (2026-10-27): with control held, every release of fn is followed
    /// 1 ms later by a key down and up of key code 179, the Globe key's own key event. It is fn
    /// itself, not another key, so it must not break the double tap.
    @Test func theGlobeKeyEventAfterEachFnReleaseStillTogglesHandsFree() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 1.749)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 1.880) == [.dictationReleased(wasTap: true)])
        #expect(recognizer.handle(.keyDown, keyCode: Self.globeKeyCode, modifierFlags: [.control], at: 1.881) == [])
        #expect(recognizer.handle(.keyUp, keyCode: Self.globeKeyCode, modifierFlags: [.control], at: 1.881) == [])
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 1.972)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 2.072) == [.dictationReleased(wasTap: true), .handsFreeToggleRequested])
    }

    @Test func fnControlOptionIsTalkNotDictation() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [.control, .option], keyCode: Self.leftOptionKeyCode, at: 0.01)
        #expect(flags(&recognizer, [.function, .control, .option], keyCode: Self.fnKeyCode, at: 0.02) == [])
        #expect(flags(&recognizer, [.function, .control], keyCode: Self.leftOptionKeyCode, at: 1.0) == [.talkReleased])
    }

    @Test func escapeIsReported() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(recognizer.handle(.keyDown, keyCode: 53, modifierFlags: [], at: 0) == [.escapePressed])
    }

    @Test func theRightOptionKeyCanBeTheDictationKey() {
        var recognizer = CompanionShortcutRecognizer(dictationKey: .rightOption)
        // The left option key sets the same flag but is not the dictation key.
        #expect(flags(&recognizer, [.option], keyCode: 58, at: 0) == [])
        #expect(flags(&recognizer, [], keyCode: 58, at: 0.1) == [])
        #expect(flags(&recognizer, [.option], keyCode: 61, at: 1) == [.dictationPressed])
        #expect(flags(&recognizer, [], keyCode: 61, at: 2) == [.dictationReleased(wasTap: false)])
    }
}

// MARK: - every dictation key × every gesture

/// A physical modifier key as macOS reports it on flagsChanged: its key code, the flag it sets, and
/// its device bit (IOLLEvent.h's NX_DEVICE…KEYMASK) — left and right control set the same flag but
/// different bits.
private enum PhysicalKey {
    case fn, leftControl, rightControl, leftOption, rightOption, leftCommand, rightCommand

    var keyCode: UInt16 {
        switch self {
        case .fn: return 63
        case .leftControl: return 59
        case .rightControl: return 62
        case .leftOption: return 58
        case .rightOption: return 61
        case .leftCommand: return 55
        case .rightCommand: return 54
        }
    }

    var modifierFlag: NSEvent.ModifierFlags {
        switch self {
        case .fn: return .function
        case .leftControl, .rightControl: return .control
        case .leftOption, .rightOption: return .option
        case .leftCommand, .rightCommand: return .command
        }
    }

    var deviceBit: UInt {
        switch self {
        case .fn: return 0
        case .leftControl: return 0x0000_0001
        case .rightControl: return 0x0000_2000
        case .leftOption: return 0x0000_0020
        case .rightOption: return 0x0000_0040
        case .leftCommand: return 0x0000_0008
        case .rightCommand: return 0x0000_0010
        }
    }

    static func dictationKey(_ hotkey: DictationHotkey) -> PhysicalKey {
        switch hotkey {
        case .fn: return .fn
        case .rightOption: return .rightOption
        case .rightCommand: return .rightCommand
        case .rightControl: return .rightControl
        }
    }
}

/// Plays key presses into a recogniser and collects every event it reports.
private struct SimulatedKeyboard {
    var recognizer: CompanionShortcutRecognizer
    var heldKeys: [PhysicalKey] = []
    var time: TimeInterval = 0
    var events: [CompanionShortcutEvent] = []

    init(dictationKey: DictationHotkey) {
        recognizer = CompanionShortcutRecognizer(dictationKey: dictationKey)
    }

    private var currentFlags: NSEvent.ModifierFlags {
        var rawValue: UInt = 0
        for key in heldKeys { rawValue |= key.modifierFlag.rawValue | key.deviceBit }
        return NSEvent.ModifierFlags(rawValue: rawValue)
    }

    mutating func wait(_ seconds: TimeInterval) { time += seconds }

    mutating func press(_ key: PhysicalKey) {
        heldKeys.append(key)
        events += recognizer.handle(.flagsChanged, keyCode: key.keyCode, modifierFlags: currentFlags, at: time)
    }

    mutating func release(_ key: PhysicalKey) {
        heldKeys.removeAll { $0 == key }
        events += recognizer.handle(.flagsChanged, keyCode: key.keyCode, modifierFlags: currentFlags, at: time)
    }

    /// A letter (C is key code 8) pressed and released with whatever modifiers are held.
    mutating func typeLetter(keyCode: UInt16 = 8) {
        events += recognizer.handle(.keyDown, keyCode: keyCode, modifierFlags: currentFlags, at: time)
        wait(0.03)
        events += recognizer.handle(.keyUp, keyCode: keyCode, modifierFlags: currentFlags, at: time)
    }
}

/// Each gesture must produce exactly the events of its own shortcut, whichever key dictates: a
/// right ⌃ hold is a dictation and not a Hey Clicky edit, right ⌥ then ⌃ is an edit and not a ⌃⌥
/// question as well, a shortcut typed while the key is held says so when the key comes up.
struct DictationKeyGestureMatrixTests {

    @Test(arguments: DictationHotkey.allCases)
    func holdingTheKeyIsOneDictationTake(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(2)
        keyboard.release(key)
        #expect(keyboard.events == [.dictationPressed, .dictationReleased(wasTap: false)])
    }

    @Test(arguments: DictationHotkey.allCases)
    func tappingTheKeyIsATapAndNothingElse(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(0.1)
        keyboard.release(key)
        #expect(keyboard.events == [.dictationPressed, .dictationReleased(wasTap: true)])
    }

    @Test(arguments: DictationHotkey.allCases)
    func doubleTappingTheKeyCancelsAndOpensNothingElse(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(0.1)
        keyboard.release(key)
        keyboard.wait(0.15)
        keyboard.press(key)
        keyboard.wait(0.1)
        keyboard.release(key)
        #expect(keyboard.events == [
            .dictationPressed, .dictationReleased(wasTap: true),
            .dictationPressed, .dictationReleased(wasTap: true), .dictationDoubleTapped,
        ])
    }

    @Test(arguments: DictationHotkey.allCases)
    func controlJoiningTheHeldKeyIsAnEditAndNotTalk(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(0.2)
        keyboard.press(.leftControl)
        #expect(!keyboard.recognizer.isTalkHeld)
        keyboard.wait(2)
        keyboard.release(.leftControl)
        keyboard.wait(0.05)
        keyboard.release(key)
        #expect(keyboard.events == [.dictationPressed, .dictationEditModifierJoined, .dictationReleased(wasTap: false)])
    }

    @Test(arguments: DictationHotkey.allCases)
    func theKeyPressedWithControlAlreadyDownIsAnEditFromTheStart(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(.leftControl)
        keyboard.wait(0.05)
        keyboard.press(key)
        keyboard.wait(2)
        keyboard.release(key)
        keyboard.wait(0.05)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [.dictationEditPressed, .dictationReleased(wasTap: false)])
    }

    @Test(arguments: DictationHotkey.allCases)
    func tappingTheKeyTwiceWithControlHeldTogglesHandsFreeOnly(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(.leftControl)
        keyboard.wait(0.02)
        keyboard.press(key)
        keyboard.wait(0.08)
        keyboard.release(key)
        keyboard.wait(0.15)
        keyboard.press(key)
        keyboard.wait(0.08)
        keyboard.release(key)
        keyboard.wait(0.05)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [
            .dictationEditPressed, .dictationReleased(wasTap: true),
            .dictationEditPressed, .dictationReleased(wasTap: true), .handsFreeToggleRequested,
        ])
    }

    @Test(arguments: DictationHotkey.allCases)
    func holdingControlAndOptionIsTalkWhateverTheDictationKey(hotkey: DictationHotkey) {
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(.leftControl)
        keyboard.wait(0.05)
        keyboard.press(.leftOption)
        keyboard.wait(2)
        keyboard.release(.leftOption)
        keyboard.wait(0.05)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [.talkPressed, .talkReleased])
    }

    @Test(arguments: DictationHotkey.allCases)
    func tappingControlTwiceOpensTheComposerOnly(hotkey: DictationHotkey) {
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(.leftControl)
        keyboard.wait(0.1)
        keyboard.release(.leftControl)
        keyboard.wait(0.15)
        keyboard.press(.leftControl)
        keyboard.wait(0.1)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [.textComposerRequested])
    }

    @Test(arguments: DictationHotkey.allCases)
    func aShortcutTypedWithTheKeyHeldIsReportedOnRelease(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(0.05)
        keyboard.typeLetter()
        keyboard.wait(0.05)
        keyboard.release(key)
        #expect(keyboard.events == [.dictationPressed, .dictationReleased(wasTap: false, keyWasPressedDuringHold: true)])
    }

    /// A letter typed just before the key went down whose key-up lands after it is not a shortcut.
    @Test(arguments: DictationHotkey.allCases)
    func aLateKeyUpFromTypingIsNotAShortcut(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.events += keyboard.recognizer.handle(.keyUp, keyCode: 0, modifierFlags: [], at: 0.02)
        keyboard.wait(2)
        keyboard.release(key)
        #expect(keyboard.events == [.dictationPressed, .dictationReleased(wasTap: false)])
    }

    /// One gesture, one lane: ⌃⌥ completed while the key is held is not also a question.
    @Test(arguments: DictationHotkey.allCases)
    func talkNeverStartsWhileTheKeyIsHeld(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(key)
        keyboard.wait(0.2)
        keyboard.press(.leftControl)
        keyboard.wait(0.05)
        keyboard.press(.leftOption)
        keyboard.wait(1)
        keyboard.release(key)
        keyboard.wait(0.5)
        keyboard.release(.leftOption)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [.dictationPressed, .dictationEditModifierJoined, .dictationReleased(wasTap: false)])
    }

    @Test(arguments: DictationHotkey.allCases)
    func theKeyPressedDuringTalkIsIgnored(hotkey: DictationHotkey) {
        let key = PhysicalKey.dictationKey(hotkey)
        var keyboard = SimulatedKeyboard(dictationKey: hotkey)
        keyboard.press(.leftControl)
        keyboard.press(.leftOption)
        keyboard.wait(0.5)
        keyboard.press(key)
        keyboard.wait(0.5)
        keyboard.release(key)
        keyboard.wait(0.5)
        keyboard.release(.leftOption)
        keyboard.release(.leftControl)
        #expect(keyboard.events == [.talkPressed, .talkReleased])
    }

    /// With right ⌥ dictating, the left ⌥ keeps its job: ⌥ then ⌃ is still talk.
    @Test func withRightOptionDictatingTheLeftOptionStillTalks() {
        var keyboard = SimulatedKeyboard(dictationKey: .rightOption)
        keyboard.press(.leftOption)
        keyboard.wait(0.05)
        keyboard.press(.leftControl)
        keyboard.wait(1)
        keyboard.release(.leftControl)
        keyboard.release(.leftOption)
        #expect(keyboard.events == [.talkPressed, .talkReleased])
    }

    /// Both controls down, then the right one (the dictation key) comes up: the flag is still set by
    /// the left one, but the take ends.
    @Test func withRightControlDictatingReleasingItWhileTheLeftIsHeldEndsTheTake() {
        var keyboard = SimulatedKeyboard(dictationKey: .rightControl)
        keyboard.press(.rightControl)
        keyboard.wait(0.5)
        keyboard.press(.leftControl)
        keyboard.wait(1)
        keyboard.release(.rightControl)
        #expect(keyboard.events == [.dictationPressed, .dictationEditModifierJoined, .dictationReleased(wasTap: false)])
        #expect(!keyboard.recognizer.isDictationKeyHeld)
    }
}
