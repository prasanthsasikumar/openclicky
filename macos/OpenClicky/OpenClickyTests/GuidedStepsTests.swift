//
//  GuidedStepsTests.swift
//  OpenClickyTests
//
//  Step by step guidance: the [NEXT] marker is found and never spoken, and a click, Return or Tab
//  (not typing alone, not esc) is what moves a guide on.
//

import AppKit
import Testing
@testable import OpenClicky

struct GuidedStepsTests {
    @Test func theMarkerIsFoundHoweverItIsWritten() {
        #expect(GuidedStepMarker.containsMarker("click the paperclip. [NEXT]"))
        #expect(GuidedStepMarker.containsMarker("click the paperclip. [ next ]"))
        #expect(!GuidedStepMarker.containsMarker("that's the next button."))
        #expect(GuidedStepMarker.removingMarkers(from: "[POINT:1,2:clip] click the paperclip. [NEXT]\n") == "[POINT:1,2:clip] click the paperclip.")
    }

    @Test func theMarkerIsNeverSpoken() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[POINT:612,88:attach] click the paperclip at the top. [NEXT]") == ["click the paperclip at the top."])
        #expect(chunker.finish() == [])
        #expect(chunker.pointTag == "[POINT:612,88:attach]")
    }

    @Test func aMarkerOnItsOwnLineIsNotSpokenEither() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "click the paperclip at the top.\n[NEXT]\n") == ["click the paperclip at the top."])
        #expect(chunker.finish() == [])
    }

    @Test func aTrailingPointTagBeforeTheMarkerStillPoints() {
        var chunker = SpokenChunker()
        _ = chunker.feed(accumulated: "click the paperclip at the top [POINT:612,88:attach] [NEXT]")
        #expect(chunker.finish() == ["click the paperclip at the top"])
        #expect(chunker.pointTag == "[POINT:612,88:attach]")
    }

    @Test func aClickReturnOrTabFinishesAStepAfterTheScreenSettles() {
        #expect(GuidedStepRules.reaction(to: .click, askIsPending: false) == .askForNextStep(settleSeconds: GuidedStepRules.settleSecondsAfterClick))
        #expect(GuidedStepRules.reaction(to: .stepKey, askIsPending: false) == .askForNextStep(settleSeconds: GuidedStepRules.settleSecondsAfterStepKey))
    }

    @Test func typingAloneIsNotAStepButTypingAfterAClickWaitsLonger() {
        #expect(GuidedStepRules.reaction(to: .otherKey, askIsPending: false) == .ignore)
        #expect(GuidedStepRules.reaction(to: .otherKey, askIsPending: true) == .postponePendingAsk(settleSeconds: GuidedStepRules.settleSecondsWhileTyping))
    }

    /// "Right-click the clip" is a step: the right click that opens the menu counts like a left click.
    @Test func aRightClickCountsAsAClick() throws {
        for eventType in [NSEvent.EventType.leftMouseUp, .rightMouseUp] {
            let event = try #require(NSEvent.mouseEvent(
                with: eventType, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0,
                context: nil, eventNumber: 0, clickCount: 1, pressure: 0))
            #expect(GuidedStepWatcher.input(for: event) == .click)
        }
    }

    @Test func escEndsTheGuide() {
        #expect(GuidedStepRules.reaction(to: .escapeKey, askIsPending: false) == .endGuide)
        #expect(GuidedStepRules.reaction(to: .escapeKey, askIsPending: true) == .endGuide)
    }

    @Test func theNextStepRequestRepeatsTheGoal() {
        let request = GuidedStepRules.nextStepRequest(forGoal: "how do i attach a file")
        #expect(request.hasPrefix(GuidedStepRules.nextStepRequestOpening))
        #expect(request.contains("how do i attach a file"))
        #expect(!request.contains("—"))
    }
}
