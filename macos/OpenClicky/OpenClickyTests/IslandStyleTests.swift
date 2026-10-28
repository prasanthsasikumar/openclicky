//
//  IslandStyleTests.swift
//  OpenClickyTests
//
//  The island's corners, the black-into-glass stops and its motion, as pure maths.
//

import CoreGraphics
import SwiftUI
import Testing
@testable import OpenClicky

struct IslandStyleTests {
    @Test func bottomRadiusFollowsTheState() {
        #expect(IslandCorners.bottomRadius(for: .collapsed, width: 190) == 11)
        #expect(IslandCorners.bottomRadius(for: .peek, width: 230) == 18)
        #expect(IslandCorners.bottomRadius(for: .compact, width: 380) == 18)
        #expect(IslandCorners.bottomRadius(for: .full, width: 512) == 25.6)
        #expect(IslandCorners.bottomRadius(for: .full, width: 360) == 20)   // clamped low
        #expect(IslandCorners.bottomRadius(for: .connect, width: 1000) == 30) // clamped high
    }

    @Test func topFlareFollowsTheStateAndVanishesInADrawer() {
        #expect(IslandCorners.topFlare(for: .collapsed, isSideDrawer: false) == 6)
        #expect(IslandCorners.topFlare(for: .peek, isSideDrawer: false) == 6)
        #expect(IslandCorners.topFlare(for: .compact, isSideDrawer: false) == 10)
        #expect(IslandCorners.topFlare(for: .permission, isSideDrawer: false) == 10)
        #expect(IslandCorners.topFlare(for: .full, isSideDrawer: false) == 12)
        #expect(IslandCorners.topFlare(for: .full, isSideDrawer: true) == 0)
    }

    @Test func glassStopsScaleWithTheLevel() {
        // Band row ends at 40 of a 120 pt panel. Level 1: black ends at the band; level 0.5: halfway
        // between the band and the bottom; the fade spans 40 % of what is left.
        let full = IslandGlass.stops(level: 1, bandBottom: 40, panelHeight: 120)
        #expect(full == IslandGlassStops(start: 40 / 120, end: (40 + 0.4 * 80) / 120))
        let half = IslandGlass.stops(level: 0.5, bandBottom: 40, panelHeight: 120)
        #expect(half == IslandGlassStops(start: 80 / 120, end: (80 + 0.4 * 40) / 120))
        #expect(IslandGlass.stops(level: 0, bandBottom: 40, panelHeight: 120) == nil)
        #expect(IslandGlass.stops(level: -1, bandBottom: 40, panelHeight: 120) == nil)
        #expect(IslandGlass.stops(level: 2, bandBottom: 40, panelHeight: 120) == full) // clamped
    }

    @Test func theOpenPanelIsSolidBlackUntilSomeoneAsksForGlass() {
        // Black reads more easily than black melting into glass (Prasanth, 2026-10-28): the open
        // panel is solid unless the glass slider in Settings → General was moved.
        #expect(IslandGlass.defaultLevel == 0)
        #expect(!IslandSurface(expansion: .full, width: 512, height: 120, bandBottom: 40, glassLevel: IslandGlass.defaultLevel, isSideDrawer: false).hasGlassLayer)
    }

    @Test func theGlassLayerExistsOnlyWhenOpenAndLevelIsAboveZero() {
        #expect(IslandSurface(expansion: .full, width: 512, height: 120, bandBottom: 40, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .full, width: 512, height: 120, bandBottom: 40, glassLevel: 0, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .peek, width: 218, height: 57, bandBottom: 33, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .compact, width: 380, height: 41, bandBottom: 33, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
        // Cards are mostly dim text (the permission card above all) and show on any wallpaper:
        // they stay solid black. Only the open panel melts into glass.
        #expect(!IslandSurface(expansion: .connect, width: 512, height: 150, bandBottom: 33, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .permission, width: 512, height: 150, bandBottom: 33, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .tour, width: 512, height: 150, bandBottom: 33, glassLevel: 0.6, isSideDrawer: false).hasGlassLayer)
    }

    @Test func reduceMotionTurnsSpringsIntoFadesAndDropsThePressScale() {
        #expect(IslandMotion.pressScale(reduceMotion: false) == 0.97)
        #expect(IslandMotion.pressScale(reduceMotion: true) == 1)
        #expect(IslandMotion.unfoldWidth(reduceMotion: true) == .easeInOut(duration: 0.15))
        #expect(IslandMotion.unfoldHeight(reduceMotion: true) == .easeInOut(duration: 0.15))
        #expect(IslandMotion.peek(reduceMotion: true) == .easeInOut(duration: 0.15))
        #expect(IslandMotion.unfoldWidth(reduceMotion: false) != IslandMotion.unfoldWidth(reduceMotion: true))
    }
}
