//
//  LocalAskGateTests.swift
//  OpenClickyTests
//
//  The keyword gate in front of the on-device model: commands and rewrites get through, questions
//  and how-tos never pay for the model.
//

import Testing
@testable import OpenClicky

struct LocalAskGateTests {

    @Test(arguments: ["open safari", "Open Google Chrome please", "launch mail", "go to youtube", "turn the volume down", "set the volume to thirty", "mute", "pause the music", "next song", "make a folder called taxes on the desktop", "show me the downloads folder"])
    func commandsPassTheGate(transcript: String) {
        #expect(LocalAskGate.decide(transcript: transcript, replyLanguageCode: "en") == .tryModel(mentionsRewriting: false))
    }

    @Test(arguments: ["make this shorter", "rephrase this", "fix the grammar", "can you make it warmer", "translate this to french"])
    func rewritesPassTheGateAndAskForATarget(transcript: String) {
        #expect(LocalAskGate.decide(transcript: transcript, replyLanguageCode: "en") == .tryModel(mentionsRewriting: true))
    }

    @Test(arguments: ["how do i open safari", "How can I turn the volume down?", "what is this", "where is the save button", "teach me how pythagoras works", "explain how to make a folder", "can you show me how to open mail",
                      // The contracted forms every transcriber produces (review, 2026-10-28).
                      "what's the volume at", "where's my downloads folder", "whats this", "who's playing", "how's this folder organised"])
    func questionsAndHowTosAreSkipped(transcript: String) {
        #expect(LocalAskGate.decide(transcript: transcript, replyLanguageCode: "en") == .skip(reason: "a question"))
    }

    @Test func plainQuestionsWithNoCommandWordAreSkipped() {
        #expect(LocalAskGate.decide(transcript: "is it going to rain tomorrow", replyLanguageCode: "en") == .skip(reason: "no command word"))
    }

    @Test func longRequestsAreSkipped() {
        let long = Array(repeating: "open", count: 25).joined(separator: " ")
        #expect(LocalAskGate.decide(transcript: long, replyLanguageCode: "en") == .skip(reason: "too long"))
    }

    @Test func otherLanguagesAreSkipped() {
        #expect(LocalAskGate.decide(transcript: "open safari", replyLanguageCode: "hi") == .skip(reason: "not english"))
    }
}
