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
        #expect(flags(&recognizer, [], keyCode: Self.fnKeyCode, at: 0.2) == [.dictationReleased(wasTap: false)])
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
        #expect(flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.05) == [.dictationPressed, .dictationEditModifierJoined])
    }

    @Test func tappingFnAndControlTwiceTogglesHandsFree() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], keyCode: Self.leftControlKeyCode, at: 0)
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.02)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 0.1) == [.dictationReleased(wasTap: true)])
        _ = flags(&recognizer, [.control, .function], keyCode: Self.fnKeyCode, at: 0.3)
        #expect(flags(&recognizer, [.control], keyCode: Self.fnKeyCode, at: 0.4) == [.dictationReleased(wasTap: true), .handsFreeToggleRequested])
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
