//
//  CompanionShortcutRecognizerTests.swift
//  OpenClickyTests
//
//  The four keyboard shortcuts (talk, text, dictate, hands-free) recognised from modifier events.
//

import AppKit
import Testing
@testable import OpenClicky

struct CompanionShortcutRecognizerTests {
    private func flags(_ recognizer: inout CompanionShortcutRecognizer, _ modifierFlags: NSEvent.ModifierFlags, at time: TimeInterval) -> [CompanionShortcutEvent] {
        recognizer.handle(.flagsChanged, keyCode: 0, modifierFlags: modifierFlags, at: time)
    }

    @Test func holdingControlAndOptionIsTalk() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.control], at: 0) == [])
        #expect(flags(&recognizer, [.control, .option], at: 0.05) == [.talkPressed])
        #expect(flags(&recognizer, [.control], at: 2.0) == [.talkReleased])
        #expect(flags(&recognizer, [], at: 2.05) == [])
    }

    @Test func tappingControlTwiceOpensTheTextComposer() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.control], at: 0) == [])
        #expect(flags(&recognizer, [], at: 0.1) == [])
        #expect(flags(&recognizer, [.control], at: 0.3) == [])
        #expect(flags(&recognizer, [], at: 0.4) == [.textComposerRequested])
    }

    @Test func slowControlTapsDoNotOpenTheComposer() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], at: 0)
        _ = flags(&recognizer, [], at: 0.1)
        _ = flags(&recognizer, [.control], at: 1.0)
        #expect(flags(&recognizer, [], at: 1.1) == [])
    }

    @Test func controlShortcutsLikeControlCAreNotTaps() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], at: 0)
        #expect(recognizer.handle(.keyDown, keyCode: 8, modifierFlags: [.control], at: 0.05) == [])
        _ = recognizer.handle(.keyUp, keyCode: 8, modifierFlags: [.control], at: 0.1)
        _ = flags(&recognizer, [], at: 0.15)
        _ = flags(&recognizer, [.control], at: 0.3)
        #expect(flags(&recognizer, [], at: 0.4) == [])
    }

    @Test func aTalkPressInsideTheTapWindowIsNotAControlTap() {
        var recognizer = CompanionShortcutRecognizer()
        _ = flags(&recognizer, [.control], at: 0)
        _ = flags(&recognizer, [.control, .option], at: 0.05)
        _ = flags(&recognizer, [.control], at: 0.1)
        _ = flags(&recognizer, [], at: 0.15)
        _ = flags(&recognizer, [.control], at: 0.3)
        #expect(flags(&recognizer, [], at: 0.4) == [])
    }

    @Test func holdingFnAndControlIsDictation() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.function], at: 0) == [])
        #expect(flags(&recognizer, [.function, .control], at: 0.05) == [.dictatePressed])
        #expect(flags(&recognizer, [.function], at: 3.0) == [.dictateReleased(wasTap: false)])
        #expect(flags(&recognizer, [], at: 3.05) == [])
    }

    @Test func tappingFnAndControlTwiceTogglesHandsFree() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.function, .control], at: 0) == [.dictatePressed])
        #expect(flags(&recognizer, [], at: 0.1) == [.dictateReleased(wasTap: true)])
        #expect(flags(&recognizer, [.function, .control], at: 0.3) == [.dictatePressed])
        #expect(flags(&recognizer, [], at: 0.4) == [.dictateReleased(wasTap: true), .handsFreeToggleRequested])
    }

    @Test func fnControlOptionIsTalkNotDictation() {
        var recognizer = CompanionShortcutRecognizer()
        #expect(flags(&recognizer, [.function, .control, .option], at: 0) == [.talkPressed])
        #expect(flags(&recognizer, [], at: 1.0) == [.talkReleased])
    }
}
