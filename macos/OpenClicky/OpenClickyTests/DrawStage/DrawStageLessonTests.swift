//
//  DrawStageLessonTests.swift
//  OpenClickyTests
//
//  Which questions get a drawing (the rule Prasanth cares most about: never a simple or personal
//  one), the prompts filled from the shared folder, and how a streamed reply is read.
//

import Foundation
import Testing
@testable import OpenClicky

@Suite struct DrawStageLessonTests {
    @Test(arguments: [
        "how does a jet engine work?", "why is the sky blue", "what happens when ice melts", "what's inside this?",
        "draw a heart for me", "show me how a rocket flies", "teach me how pythagoras works",
        "explain how a transistor works", "how does this work", "what makes a plane fly", "what are the parts of a cell",
        "show me a rotary engine in 3d", "show a jet engine in 3D", "a 3d model of the heart", "can you show me the inside of a turbine",
        "rotary engine cutaway", "show me what a transistor looks like",
        "can you tell me how an electron microscope works", "I want to know how a jet engine works", "tell me how the heart works",
        "explain to me how a transistor works", "I'd like to understand why the sky is blue", "so how do volcanoes erupt",
        "what is aspirin made of?", "what is dopamine?", "what are clouds made of", "what does caffeine look like",
        "show me a water molecule", "How has world population grown over 300 years?",
        "what were the big moments in the history of computers?", "can you make a graph of the temperature of the earth",
    ])
    func curiousQuestionsPassTheGate(question: String) {
        #expect(DrawStageLesson.wantsADrawing(question), "\(question)")
    }

    @Test(arguments: [
        "why is my build failing?", "what time is it", "how do I export this", "how does this app handle login?",
        "explain this error", "what happens if I click save?", "how are you", "why not", "open spotify",
        "how does my code reach the server", "why is this slow", "what's the difference between tcp and udp",
        "python vs rust", "hi", "how does this button work", "why does this file keep crashing", "how do you do",
        "how does the settings page work", "what happens when I press this", "draw", "",
        "show me the weather", "show me my files", "show me the downloads folder", "show me a good restaurant nearby",
        "what is your name", "what does my email look like", "how has your day been", "open my sales chart",
    ])
    func simpleAndPersonalQuestionsStayOut(question: String) {
        #expect(!DrawStageLesson.wantsADrawing(question), "\(question)")
    }

    /// After a drawing on the fast-voice lane, the fast voice is told what was drawn and said so
    /// its next turn can build on it; it is told not to repeat it.
    @Test func theFastVoiceIsToldWhatWasDrawn() {
        let note = DrawStageLesson.fastVoiceNote(question: "how does a jet engine work?", spokenLines: ["A jet engine is a tube.", "Air is squeezed."])
        #expect(note.contains("\"how does a jet engine work?\""))
        #expect(note.contains("A jet engine is a tube. Air is squeezed."))
        #expect(note.lowercased().contains("do not repeat"))
        let stopped = DrawStageLesson.fastVoiceNote(question: "q", spokenLines: [])
        #expect(stopped.contains("stopped") || stopped.contains("before"))
    }

    @Test func onlyAQuestionAboutTheScreenCarriesThePointer() {
        #expect(DrawStageLesson.refersToTheScreen("how does this work?"))
        #expect(DrawStageLesson.refersToTheScreen("what's inside that"))
        #expect(DrawStageLesson.refersToTheScreen("explain what is happening here"))
        #expect(!DrawStageLesson.refersToTheScreen("how does a jet engine work?"))
        #expect(!DrawStageLesson.refersToTheScreen("can you tell me how an electron microscope works"))
    }

    @Test func followUpsAreWiderButNeverActions() {
        #expect(DrawStageLesson.mayBeAFollowUp("now show it in 3d"))
        #expect(DrawStageLesson.mayBeAFollowUp("what does the turbine do?"))
        #expect(DrawStageLesson.mayBeAFollowUp("zoom in on the valve"))
        #expect(DrawStageLesson.mayBeAFollowUp("make the nucleus bigger"))
        #expect(DrawStageLesson.mayBeAFollowUp("slow it down"))
        #expect(DrawStageLesson.mayBeAFollowUp("hide the labels"))
        #expect(DrawStageLesson.mayBeAFollowUp("quiz me"))
        #expect(!DrawStageLesson.mayBeAFollowUp("open spotify"))
        #expect(!DrawStageLesson.mayBeAFollowUp("thanks"))
        #expect(!DrawStageLesson.mayBeAFollowUp("how do I export this"))
    }

    @Test func programInReadsAFenceOrBareCode() {
        #expect(DrawStageLesson.programIn("```js\nstep(1); say(\"hi\")\n```") == "step(1); say(\"hi\")\n")
        #expect(DrawStageLesson.programIn("```js\nstep(1)\n``") == "step(1)\n")
        #expect(DrawStageLesson.programIn("```js\nstep(1)\n") == "step(1)\n")
        #expect(DrawStageLesson.programIn("step(1); say(\"hi\")") == "step(1); say(\"hi\")")
        #expect(DrawStageLesson.programIn("Sure! Here is") == "")
        #expect(DrawStageLesson.programIn("```") == "")
    }

    @Test func keepsTheDrawingWaitsForTheFirstStatement() {
        #expect(DrawStageLesson.keepsTheDrawing("", done: false) == nil)
        #expect(DrawStageLesson.keepsTheDrawing("ke", done: false) == nil)
        #expect(DrawStageLesson.keepsTheDrawing("keep()\nstep(4)", done: false) == true)
        #expect(DrawStageLesson.keepsTheDrawing("step(1)", done: false) == false)
        #expect(DrawStageLesson.keepsTheDrawing("// a comment\n", done: true) == false)
        #expect(DrawStageLesson.keepsTheDrawing("// a comment", done: false) == nil)
        #expect(DrawStageLesson.keepsTheDrawing("", done: true) == false)
    }

    @Test func decidesNoneProseOrProgram() {
        #expect(DrawStageLesson.decide(replySoFar: "", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "NO", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "NONE", done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "  NONE.\n", done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "It is 3 pm.\n", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "It is 3 pm.\nNothing more.\n", done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "It is 3", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "```js\nstep(1)", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "```", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "step(1); say(\"a\")", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "const c = {x: 1}", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "keep()", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "Sure", done: true) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "", done: true) == .notADrawing)
    }

    /// A model that prefaces its program with a line of prose ("Here's a drawing of the heart:")
    /// must not be cut off at that line: the fence a moment later makes it a program.
    @Test func aShortPrefaceBeforeTheFenceIsStillAProgram() {
        #expect(DrawStageLesson.decide(replySoFar: "Here's a drawing of the heart:\n", done: false) == .undecided)
        #expect(DrawStageLesson.decide(replySoFar: "Here's a drawing of the heart:\n```js\nstep(1)", done: false) == .program)
        #expect(DrawStageLesson.decide(replySoFar: "Here's a drawing of the heart:\n", done: true) == .notADrawing)
        // Prose that goes on (a spoken answer) settles it without waiting for the whole reply.
        let longProse = String(repeating: "An electron microscope uses a beam of electrons instead of light. ", count: 5)
        #expect(DrawStageLesson.decide(replySoFar: longProse, done: false) == .notADrawing)
        #expect(DrawStageLesson.decide(replySoFar: "It is 3 pm.\nNothing more.\nThat is all.\n", done: false) == .notADrawing)
    }

    @Test func promptsComeFromTheBundleWithPlaceholdersFilled() throws {
        let system = try #require(DrawStageLesson.systemPrompt(canvasWidth: 1280, canvasHeight: 800))
        #expect(system.contains("W = 1280 by H = 800"))
        #expect(system.contains("below y = 688"))
        #expect(system.contains("3 to 6 beats"))
        #expect(!system.contains("{{"))

        let question = try #require(DrawStageLesson.questionPrompt("how does a heart work?"))
        #expect(question.contains("NONE"))
        #expect(question.contains("\"how does a heart work?\""))

        let drawing = DrawnAnswer(questions: ["how does a heart work?"], program: "step(1); say(\"a\")\nstep(2); say(\"b\")",
                                  canvasWidth: 1280, canvasHeight: 800, spokenLines: ["a", "b"])
        let followUp = try #require(DrawStageLesson.followUpPrompt(question: "what does the valve do?", drawing: drawing))
        #expect(followUp.contains("step(3)"))
        #expect(followUp.contains("\"how does a heart work?\""))
        #expect(followUp.contains("Now they ask: \"what does the valve do?\""))
        #expect(!followUp.contains("{{"))
    }

    @Test(arguments: ["what is a cell?", "what is mitosis", "explain cell division", "what are chromosomes", "tell me about bacteria"])
    func whatAPartOfLifeIsGetsADrawing(question: String) {
        #expect(DrawStageLesson.wantsADrawing(question), "\(question)")
    }

    @Test(arguments: ["what is a cell phone", "what is a cell in excel", "what's my cell number", "what is a solar cell"])
    func otherCellsStayOut(question: String) {
        #expect(!DrawStageLesson.wantsADrawing(question), "\(question)")
    }
}
