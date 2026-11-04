//
//  DrawStageTurnTests.swift
//  OpenClickyTests
//
//  What a question does to the stage: a new drawing, a follow-up to the open one, an interruption
//  of one still playing, or nothing at all (the setting off, a guide's own step, a bubble).
//

import Foundation
import Testing
@testable import OpenClicky

struct DrawStageTurnTests {
    private func decide(_ question: String, _ state: DrawStageTurn.StageState, drawToExplain: Bool = true, guide: Bool = false) -> DrawStageTurn.Decision {
        DrawStageTurn.decide(question: question, stageState: state, drawToExplain: drawToExplain, isGuidedStepContinuation: guide)
    }

    @Test func aCuriousQuestionWithNothingUpIsANewDrawing() {
        #expect(decide("how does a jet engine work?", .none) == .newDrawing)
    }

    @Test func aSimpleQuestionWithNothingUpIsNotADrawingQuestion() {
        #expect(decide("what time is it", .none) == .notADrawingQuestion)
    }

    @Test func theSettingOffNeverDraws() {
        #expect(decide("how does a jet engine work?", .none, drawToExplain: false) == .notADrawingQuestion)
        #expect(decide("what does the turbine do?", .open, drawToExplain: false) == .notADrawingQuestion)
    }

    @Test func aQuestionWhilePlayingInterruptsThenFollowsUp() {
        #expect(decide("what does the turbine do?", .playing) == .interruptThenFollowUp)
    }

    @Test func anUnrelatedQuestionSendsTheDrawingToItsBubble() {
        #expect(decide("open spotify", .open) == .toBubbleThenOrdinary)
        #expect(decide("open spotify", .playing) == .toBubbleThenOrdinary)
    }

    @Test func aFollowUpWhileOpen() {
        #expect(decide("now show it in 3d", .open) == .followUp)
        #expect(decide("why does it spin?", .open) == .followUp)
    }

    @Test func aBubbleNeverGetsFollowUps() {
        #expect(decide("what does the turbine do?", .bubble) == .notADrawingQuestion)
        #expect(decide("how does a heart work?", .bubble) == .newDrawing)
    }

    @Test func aGuideStepNeverDraws() {
        #expect(decide("how does this work", .none, guide: true) == .notADrawingQuestion)
        #expect(decide("how does this work", .open, guide: true) == .notADrawingQuestion)
    }

    @MainActor @Test func drawToExplainIsOnByDefault() {
        let defaults = ScratchDefaults.make()
        let settings = DictationSettings(defaults: defaults)
        #expect(settings.drawToExplain)
        settings.drawToExplain = false
        #expect(!DictationSettings(defaults: defaults).drawToExplain)
    }
}
