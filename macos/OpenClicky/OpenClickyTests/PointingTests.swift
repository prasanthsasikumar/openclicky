//
//  PointingTests.swift
//  OpenClickyTests
//
//  Screenshot pixel → AppKit global point mapping used by both the Claude teacher lane
//  ([POINT:x,y] tags) and the Realtime `point_at` tool.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct PointingTests {
    private func capture(origin: CGPoint = .zero) -> CompanionScreenCapture {
        CompanionScreenCapture(
            imageData: Data(),
            label: "test",
            isCursorScreen: true,
            displayWidthInPoints: 2560,
            displayHeightInPoints: 1600,
            displayFrame: CGRect(origin: origin, size: CGSize(width: 2560, height: 1600)),
            screenshotWidthInPixels: 1280,
            screenshotHeightInPixels: 800
        )
    }

    @Test func scalesScreenshotPixelsToDisplayPoints() {
        let location = CompanionManager.screenLocation(forScreenshotPoint: CGPoint(x: 640, y: 400), in: capture())
        #expect(location == CGPoint(x: 1280, y: 800))
    }

    @Test func flipsTopLeftOriginToAppKitBottomLeft() {
        // Top-left of the screenshot is the top-left of the display: AppKit y = display height.
        let topLeft = CompanionManager.screenLocation(forScreenshotPoint: .zero, in: capture())
        #expect(topLeft == CGPoint(x: 0, y: 1600))
        // Bottom-right of the screenshot is AppKit (width, 0).
        let bottomRight = CompanionManager.screenLocation(forScreenshotPoint: CGPoint(x: 1280, y: 800), in: capture())
        #expect(bottomRight == CGPoint(x: 2560, y: 0))
    }

    @Test func clampsOutOfRangeCoordinates() {
        let farRight = CompanionManager.screenLocation(forScreenshotPoint: CGPoint(x: 99999, y: -5), in: capture())
        #expect(farRight == CGPoint(x: 2560, y: 1600))
    }

    @Test func offsetsBySecondaryDisplayOrigin() {
        let location = CompanionManager.screenLocation(forScreenshotPoint: CGPoint(x: 640, y: 400), in: capture(origin: CGPoint(x: 2560, y: -300)))
        #expect(location == CGPoint(x: 3840, y: 500))
    }
}
