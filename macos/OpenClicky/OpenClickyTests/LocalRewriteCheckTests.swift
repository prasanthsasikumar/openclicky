//
//  LocalRewriteCheckTests.swift
//  OpenClickyTests
//
//  A rewrite from the on-device model is pasted only when it reads as one: not empty, not an
//  answer to the text, not the same text, not a fragment or a ramble.
//

import Testing
@testable import OpenClicky

private let original = "Hi John, I wanted to check whether you could send the report by tomorrow afternoon because the client meeting is on Thursday morning."

struct LocalRewriteCheckTests {

    @Test func aGoodRewritePasses() {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: "Hi John, could you send the report by tomorrow afternoon? The client meeting is Thursday morning.") == nil)
    }

    @Test func emptyIsRejected() {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: "  \n") == "empty")
    }

    @Test(arguments: ["Sure! Here is a shorter version: Hi John, send the report by tomorrow.", "I can help with that. Hi John, please send the report.", "Certainly, here's the text: Hi John."])
    func anAnswerInsteadOfARewriteIsRejected(rewritten: String) {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: rewritten) == "answered instead of rewrote")
    }

    @Test func theSameTextBackIsRejected() {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: original + " ") == "unchanged")
    }

    @Test func aFragmentIsRejected() {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: "Hi John.") == "too short")
    }

    @Test func aRambleIsRejected() {
        #expect(LocalRewriteCheck.rejectionReason(original: original, rewritten: String(repeating: original + " ", count: 4)) == "too long")
    }

    @Test func aShortOriginalMayGrow() {
        // Three words → a sentence is a legitimate "make it longer".
        #expect(LocalRewriteCheck.rejectionReason(original: "thanks, got it", rewritten: "Thank you, I have received it and will get back to you soon.") == nil)
    }
}
