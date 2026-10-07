//
//  OverlayScreenLayoutTests.swift
//  OpenClickyTests
//
//  The overlay windows are built from the screen list; when a display is plugged in, unplugged,
//  or moved in the arrangement, they must be rebuilt, and left alone otherwise.
//

import CoreGraphics
import Testing
@testable import OpenClicky

struct OverlayScreenLayoutTests {
    private let builtIn = CGRect(x: 0, y: 0, width: 1512, height: 982)
    private let externalAbove = CGRect(x: -964, y: 982, width: 3440, height: 1440)
    private let externalLeft = CGRect(x: -3440, y: 0, width: 3440, height: 1440)

    @Test func sameScreensNeedNoRebuild() {
        #expect(!OverlayWindowManager.screensChanged(from: [builtIn, externalAbove], to: [builtIn, externalAbove]))
    }

    @Test func orderDoesNotMatter() {
        #expect(!OverlayWindowManager.screensChanged(from: [builtIn, externalAbove], to: [externalAbove, builtIn]))
    }

    @Test func aMovedDisplayNeedsARebuild() {
        #expect(OverlayWindowManager.screensChanged(from: [builtIn, externalAbove], to: [builtIn, externalLeft]))
    }

    @Test func aPluggedInDisplayNeedsARebuild() {
        #expect(OverlayWindowManager.screensChanged(from: [builtIn], to: [builtIn, externalAbove]))
    }

    @Test func anUnpluggedDisplayNeedsARebuild() {
        #expect(OverlayWindowManager.screensChanged(from: [builtIn, externalAbove], to: [builtIn]))
    }
}
