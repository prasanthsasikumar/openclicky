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
