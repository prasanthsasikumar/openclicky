//
//  ScreenTextLocatorTests.swift
//  OpenClickyTests
//
//  `point_at` snapping: the Realtime model's pixel guess is off by 30–100 px, so the app looks for
//  the element's visible text (OCR of the same screenshot) near the guess and points at that.
//

import AppKit
import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct ScreenTextLocatorTests {
    private func line(_ text: String, x: CGFloat, y: CGFloat, width: CGFloat, height: CGFloat = 14) -> ScreenTextLine {
        ScreenTextLine(text: text, box: CGRect(x: x, y: y, width: width, height: height))
    }

    @Test func clearlyNearestMatchWins() {
        // Two "Star" buttons, one of them far down the page: the guess is off by up to 100 px, which
        // is nowhere near enough to confuse these two, so distance decides.
        let lines = [line("Star", x: 800, y: 500, width: 30), line("Star", x: 800, y: 1400, width: 30)]
        let match = ScreenTextLocator.locate("star", near: CGPoint(x: 700, y: 520), in: lines, maxDistance: 1000, ambiguityMargin: 128)
        #expect(match?.center == CGPoint(x: 815, y: 507))
    }

    @Test func matchesAtSimilarDistancesAreLeftUnresolved() {
        // Two "Star" buttons one feed row apart. The model's guess is itself 30-100 px off, so a
        // 26 px difference between the candidates is its error rather than evidence: pointing at the
        // nearer one is a coin toss. Unresolved goes to the grounding pass, which is told what the
        // user actually asked and can pick the row they meant.
        let lines = [line("Star", x: 800, y: 500, width: 30), line("Star", x: 800, y: 600, width: 30)]
        #expect(ScreenTextLocator.locate("star", near: CGPoint(x: 700, y: 590), in: lines, maxDistance: 300, ambiguityMargin: 128) == nil)
    }

    @Test func aSecondMatchOutsideTheRadiusDoesNotMakeItAmbiguous() {
        // The runner-up only counts if it was a candidate at all; one already excluded by the
        // radius must not stop the winner from being used.
        let lines = [line("Edit", x: 400, y: 300, width: 30), line("Edit", x: 400, y: 360, width: 30)]
        let match = ScreenTextLocator.locate("Edit", near: CGPoint(x: 395, y: 305), in: lines, maxDistance: 40, ambiguityMargin: 128)
        #expect(match?.center.y == 307)
    }

    @Test func substringInsideNoisyOcrLineUsesTheSubRangeBox() {
        // The button glyph gets read as a letter ("HNew"); the caption is still inside the line,
        // and the recognizer's per-range box wins over the whole-line box.
        var noisy = line("HNew", x: 217, y: 271, width: 40, height: 16)
        noisy.rangeBox = { range in
            range.lowerBound == noisy.text.index(after: noisy.text.startIndex) ? CGRect(x: 227, y: 271, width: 30, height: 16) : nil
        }
        let match = ScreenTextLocator.locate("New", near: CGPoint(x: 160, y: 250), in: [noisy], maxDistance: 300, ambiguityMargin: 128)
        #expect(match?.center == CGPoint(x: 242, y: 279))
    }

    @Test func substringWithoutRangeBoxIsPlacedProportionally() {
        // "New" is the last three of twenty characters: its box is the right 3/20 of the line.
        let lines = [line("Top repositories New", x: 100, y: 200, width: 200)]
        let match = ScreenTextLocator.locate("new", near: CGPoint(x: 150, y: 200), in: lines, maxDistance: 300, ambiguityMargin: 128)
        #expect(match != nil)
        #expect(abs((match?.center.x ?? 0) - 285) < 1)
        #expect(match?.center.y == 207)
    }

    @Test func nearMissesAreNotSnappedTo() {
        // "reditt" in the menu bar is one edit from "reddit", but the user meant a browser tab whose
        // title never says reddit. Near misses go to the Claude fallback instead of snapping.
        let lines = [line("reditt", x: 1310, y: 8, width: 40, height: 12), line("Home", x: 317, y: 225, width: 58, height: 21)]
        #expect(ScreenTextLocator.locate("reddit", near: CGPoint(x: 1300, y: 20), in: lines, maxDistance: 300, ambiguityMargin: 128) == nil)
        #expect(ScreenTextLocator.locate("New", near: CGPoint(x: 160, y: 250), in: [line("Now", x: 217, y: 271, width: 41)], maxDistance: 300, ambiguityMargin: 128) == nil)
    }

    @Test func exactMatchIsFoundEvenWhenANearMissIsCloser() {
        let lines = [line("Now", x: 100, y: 100, width: 30), line("New", x: 400, y: 100, width: 30)]
        let match = ScreenTextLocator.locate("New", near: CGPoint(x: 110, y: 100), in: lines, maxDistance: 1000, ambiguityMargin: 128)
        #expect(match?.text == "New")
    }

    @Test func descriptiveHintFallsBackToItsDistinctiveWord() {
        // The model describes the element instead of copying its caption: "new repo button" is not
        // on screen, "New" is. Generic UI nouns ("button") are never matched on their own.
        let lines = [line("New", x: 217, y: 271, width: 40, height: 16), line("Button styles", x: 700, y: 271, width: 90)]
        let match = ScreenTextLocator.locate("new repo button", near: CGPoint(x: 160, y: 250), in: lines, maxDistance: 300, ambiguityMargin: 128)
        #expect(match?.text == "New")
        let onlyGeneric = ScreenTextLocator.locate("button", near: CGPoint(x: 700, y: 271), in: lines, maxDistance: 300, ambiguityMargin: 128)
        #expect(onlyGeneric == nil)
    }

    @Test func farAwayMatchesAndEmptyHintsAreIgnored() {
        let lines = [line("New", x: 1200, y: 800, width: 30)]
        #expect(ScreenTextLocator.locate("New", near: CGPoint(x: 100, y: 100), in: lines, maxDistance: 300, ambiguityMargin: 128) == nil)
        #expect(ScreenTextLocator.locate("   ", near: CGPoint(x: 1200, y: 800), in: lines, maxDistance: 300, ambiguityMargin: 128) == nil)
        #expect(ScreenTextLocator.locate("Ne", near: CGPoint(x: 1200, y: 800), in: lines, maxDistance: 300, ambiguityMargin: 128) == nil)
    }

    @Test func recognizesSmallButtonCaptionInARenderedScreenshot() async throws {
        // A 1280-px-wide capture with a small caption, like the "New" button on GitHub's sidebar.
        let size = CGSize(width: 1280, height: 200)
        let image = NSImage(size: size, flipped: true) { rect in
            NSColor.white.setFill()
            rect.fill()
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11, weight: .semibold), .foregroundColor: NSColor.black]
            NSAttributedString(string: "Top repositories", attributes: attributes).draw(at: CGPoint(x: 27, y: 92))
            NSAttributedString(string: "Export", attributes: attributes).draw(at: CGPoint(x: 300, y: 92))
            return true
        }
        let tiff = try #require(image.tiffRepresentation)
        let jpeg = try #require(NSBitmapImageRep(data: tiff)?.representation(using: .jpeg, properties: [.compressionFactor: 0.8]))

        let lines = try await ScreenTextRecognizer.recognize(jpeg: jpeg)
        let match = try #require(ScreenTextLocator.locate("Export", near: CGPoint(x: 250, y: 60), in: lines, maxDistance: 300, ambiguityMargin: 128))
        // Drawn at x 300…≈335, y 92…≈105 in the capture's own pixels.
        #expect(abs(match.center.x - 318) < 12)
        #expect(abs(match.center.y - 99) < 8)
    }
}
