//
//  DrawStageBubbleTests.swift
//  OpenClickyTests
//
//  Where a finished drawing's bubble sits for each island placement: beside a side drawer,
//  under the notch, or in the screen's top-right corner when the island is hidden.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@Suite struct DrawStageBubbleTests {
    private let screen = CGRect(x: 0, y: 0, width: 1440, height: 875)

    @Test func besideALeftDrawer() {
        let island = CGRect(x: 16, y: 500, width: 360, height: 200)
        let frame = DrawStageBubblePlacement.frame(islandFrame: island, placement: .left, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.minX == 388)
        #expect(frame.maxY == 700)
        #expect(frame.width == 96)
        #expect(frame.height == 64)
    }

    @Test func besideARightDrawer() {
        let island = CGRect(x: 1064, y: 500, width: 360, height: 200)
        let frame = DrawStageBubblePlacement.frame(islandFrame: island, placement: .right, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.maxX == 1052)
        #expect(frame.maxY == 700)
    }

    @Test func underTheNotch() {
        let island = CGRect(x: 540, y: 800, width: 360, height: 75)
        let frame = DrawStageBubblePlacement.frame(islandFrame: island, placement: .notch, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.minX == 912)
        #expect(frame.maxY == 792)
    }

    @Test func topRightWhenHidden() {
        let frame = DrawStageBubblePlacement.frame(islandFrame: nil, placement: .hidden, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.maxX == 1424)
        #expect(frame.maxY == 859)
        let noIsland = DrawStageBubblePlacement.frame(islandFrame: nil, placement: .left, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(noIsland == frame)
    }

    @Test func heightIsClamped() {
        #expect(DrawStageBubblePlacement.frame(islandFrame: nil, placement: .hidden, screenVisibleFrame: screen, thumbnailAspect: 0.3).height == 120)
        #expect(DrawStageBubblePlacement.frame(islandFrame: nil, placement: .hidden, screenVisibleFrame: screen, thumbnailAspect: 4).height == 54)
        #expect(DrawStageBubblePlacement.frame(islandFrame: nil, placement: .hidden, screenVisibleFrame: screen, thumbnailAspect: 0).height == 64)
    }

    @Test func theBubbleStaysOnTheScreen() {
        // A drawer near the bottom of a short screen: the bubble is lifted so it never hangs below the visible frame.
        let island = CGRect(x: 16, y: 0, width: 360, height: 40)
        let frame = DrawStageBubblePlacement.frame(islandFrame: island, placement: .left, screenVisibleFrame: screen, thumbnailAspect: 1.5)
        #expect(frame.minY >= screen.minY)
        #expect(frame.maxX <= screen.maxX)
    }
}
