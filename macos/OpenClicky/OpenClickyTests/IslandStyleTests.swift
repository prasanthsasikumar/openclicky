//
//  IslandStyleTests.swift
//  OpenClickyTests
//
//  The island's corners, its style and its motion, as pure maths.
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

    @Test func theStyleDefaultsToLiquidGlassAndHonoursTheOldSlider() {
        let defaults = UserDefaults(suiteName: "IslandStyleTests.\(UUID().uuidString)")!
        #expect(IslandLook.current(in: defaults) == .glass)
        defaults.set(0.6, forKey: IslandLook.legacyGlassLevelKey)
        #expect(IslandLook.current(in: defaults) == .blend)
        defaults.set(0.0, forKey: IslandLook.legacyGlassLevelKey)
        #expect(IslandLook.current(in: defaults) == .black)
        defaults.set("glass", forKey: IslandLook.defaultsKey)
        #expect(IslandLook.current(in: defaults) == .glass)
    }

    @Test func theGlassLayerExistsOnlyOnTheOpenPanelInAGlassStyle() {
        #expect(IslandSurface(expansion: .full, width: 512, height: 120, look: .glass, isSideDrawer: false).hasGlassLayer)
        #expect(IslandSurface(expansion: .full, width: 512, height: 120, look: .blend, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .full, width: 512, height: 120, look: .black, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .peek, width: 218, height: 57, look: .glass, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .compact, width: 380, height: 41, look: .glass, isSideDrawer: false).hasGlassLayer)
        // Cards are mostly dim text (the permission card above all) and show on any wallpaper:
        // they stay solid black. Only the open panel shows glass.
        #expect(!IslandSurface(expansion: .connect, width: 512, height: 150, look: .glass, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .permission, width: 512, height: 150, look: .glass, isSideDrawer: false).hasGlassLayer)
        #expect(!IslandSurface(expansion: .tour, width: 512, height: 150, look: .glass, isSideDrawer: false).hasGlassLayer)
    }

    @Test func glassCastsALighterShadowAndTheClosedIslandNone() {
        #expect(IslandLook.glass.shadowOpacity(isOpen: true) == 0.25)
        #expect(IslandLook.blend.shadowOpacity(isOpen: true) == 0.45)
        #expect(IslandLook.black.shadowOpacity(isOpen: false) == 0)
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
