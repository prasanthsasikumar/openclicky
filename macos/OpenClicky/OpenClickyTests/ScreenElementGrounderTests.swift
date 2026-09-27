//
//  ScreenElementGrounderTests.swift
//  OpenClickyTests
//
//  `point_at` fallback for icon-only elements: Claude locates the element on the same screenshot
//  and answers with a [POINT:x,y] tag; these cover the parsing and the prompt's contract.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct ScreenElementGrounderTests {
    private let imageSize = CGSize(width: 1280, height: 831)

    @Test func parsesThePointTagWithOrWithoutSpaces() {
        #expect(ScreenElementGrounder.parsePoint(from: "[POINT:1063, 157]", imageSize: imageSize) == CGPoint(x: 1063, y: 157))
        #expect(ScreenElementGrounder.parsePoint(from: "Sure — [POINT:237,277]", imageSize: imageSize) == CGPoint(x: 237, y: 277))
    }

    /// The text lets the pointing chain snap Claude's loose point to the exact OCR box: a paper's
    /// "arXiv" link was located 130 px too high, on the abstract, from the point alone.
    @Test func theVisibleTextRidesAlongWithThePoint() {
        let reply = "[POINT:516, 503:arXiv]"
        #expect(ScreenElementGrounder.parsePoint(from: reply, imageSize: imageSize) == CGPoint(x: 516, y: 503))
        #expect(ScreenElementGrounder.parseVisibleText(from: reply) == "arXiv")
        #expect(ScreenElementGrounder.parseVisibleText(from: "[POINT:10,20]") == nil)
        #expect(ScreenElementGrounder.parseVisibleText(from: "[POINT:10,20: ]") == nil)
        #expect(ScreenElementGrounder.parsePoint(from: "[POINT:none]", imageSize: imageSize) == nil)
    }

    @Test func thePromptAsksForTheTextTheElementShows() {
        let prompt = ScreenElementGrounder.prompt(label: "paper link", text: "", userRequest: "how do I open this paper", imageSize: imageSize)
        #expect(prompt.contains("[POINT:x,y:visible text]"))
        #expect(prompt.contains("arXiv"))
    }

    @Test func noneAndMissingTagsGiveNil() {
        #expect(ScreenElementGrounder.parsePoint(from: "[POINT:none]", imageSize: imageSize) == nil)
        #expect(ScreenElementGrounder.parsePoint(from: "I can't see that element.", imageSize: imageSize) == nil)
    }

    @Test func pointsOutsideTheScreenshotAreRejected() {
        #expect(ScreenElementGrounder.parsePoint(from: "[POINT:1400,100]", imageSize: imageSize) == nil)
        #expect(ScreenElementGrounder.parsePoint(from: "[POINT:100,900]", imageSize: imageSize) == nil)
    }

    @Test func promptNamesTheElementTheSizeAndTheUsersRequest() {
        let prompt = ScreenElementGrounder.prompt(label: "plus menu", text: "+", userRequest: "show me the plus sign", imageSize: imageSize)
        #expect(prompt.contains("plus menu"))
        #expect(prompt.contains("\"+\""))
        #expect(prompt.contains("show me the plus sign"))
        #expect(prompt.contains("1280×831"))
        #expect(prompt.contains("[POINT:none]"))
        // Without a caption or a request the prompt still reads cleanly.
        let bare = ScreenElementGrounder.prompt(label: "gear icon", text: "", userRequest: nil, imageSize: imageSize)
        #expect(bare.contains("gear icon"))
        #expect(!bare.contains("\"\""))
        #expect(!bare.contains("asked"))
    }
}
