//
//  NotchGeometryTests.swift
//  OpenClickyTests
//
//  The virtual notch on displays without a hardware notch hangs from the menu bar band — and
//  from the very top edge on a display that shows no menu bar.
//

import CoreGraphics
import Testing
@testable import OpenClicky

struct NotchGeometryTests {
    @Test func displayWithAMenuBarHangsFromTheMenuBarBand() {
        let screenFrame = CGRect(x: 0, y: 0, width: 1512, height: 982)
        let visibleFrame = CGRect(x: 0, y: 86, width: 1512, height: 863)
        let geometry = NotchGeometry.virtualNotch(screenFrame: screenFrame, visibleFrame: visibleFrame)
        #expect(!geometry.hasHardwareNotch)
        #expect(geometry.notchHeight == 33)
        #expect(geometry.notchRect.maxY == 982)
    }

    @Test func displayWithoutAMenuBarHangsFromTheTopEdge() {
        // An auto-hidden menu bar (or a display macOS shows none on): visibleFrame == frame.
        let screenFrame = CGRect(x: -173, y: 982, width: 1920, height: 1080)
        let geometry = NotchGeometry.virtualNotch(screenFrame: screenFrame, visibleFrame: screenFrame)
        #expect(geometry.notchHeight == 0)
        #expect(geometry.notchRect == CGRect(x: -173 + 960 - 95, y: 2062, width: 190, height: 0))
    }

    @MainActor
    @Test func collapsedHandleOnAnExternalDisplayIgnoresTheMenuBarBand() {
        // This Mac's external monitor has a 30 pt menu bar band; the handle still sits at the top edge.
        let screenFrame = CGRect(x: -173, y: 982, width: 1920, height: 1080)
        let visibleFrame = CGRect(x: -173, y: 982, width: 1920, height: 1050)
        let model = NotchHUDModel(geometry: NotchGeometry.virtualNotch(screenFrame: screenFrame, visibleFrame: visibleFrame))
        #expect(model.geometry.notchHeight == 30)
        #expect(model.collapsedHeight == model.handleStripHeight)
        // The full panel still keeps a band for the tab bar.
        #expect(model.topBandHeight == 34)
    }
}
