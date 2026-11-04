//
//  DrawStageTurn.swift
//  OpenClicky
//
//  What a new question does to the drawing stage, as a pure decision: a new drawing, a follow-up
//  to the drawing that is open, an interruption of one still playing, sending an unrelated
//  question's drawing to its bubble first, or nothing (the setting off, a guide's own next step,
//  a drawing already in its bubble, a question words answer).
//

import Foundation

enum DrawStageTurn {
    enum StageState: Equatable {
        /// Nothing drawn.
        case none
        /// A drawing is being drawn and spoken.
        case playing
        /// A finished drawing is open on screen (never minimised, or brought back from its bubble).
        case open
        /// A finished drawing waits in its bubble.
        case bubble
    }

    enum Decision: Equatable {
        /// Answer the ordinary way; the stage is left as it is.
        case notADrawingQuestion
        /// Try a drawing on a fresh stage (a bubble, if any, is replaced when it draws).
        case newDrawing
        /// Stop the drawing that is playing, then ask the model with its program.
        case interruptThenFollowUp
        /// Ask the model with the open drawing's program.
        case followUp
        /// Send the drawing to its bubble, then answer the ordinary way.
        case toBubbleThenOrdinary
    }

    static func decide(question: String, stageState: StageState, drawToExplain: Bool, isGuidedStepContinuation: Bool) -> Decision {
        guard drawToExplain, !isGuidedStepContinuation else { return .notADrawingQuestion }
        switch stageState {
        case .playing:
            return DrawStageLesson.mayBeAFollowUp(question) ? .interruptThenFollowUp : .toBubbleThenOrdinary
        case .open:
            return DrawStageLesson.mayBeAFollowUp(question) ? .followUp : .toBubbleThenOrdinary
        case .none, .bubble:
            return DrawStageLesson.wantsADrawing(question) ? .newDrawing : .notADrawingQuestion
        }
    }
}
