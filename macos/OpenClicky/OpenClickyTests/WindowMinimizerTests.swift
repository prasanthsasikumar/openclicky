//
//  WindowMinimizerTests.swift
//  OpenClickyTests
//
//  "Minimize everything": which of the windows on screen it touches. The Accessibility calls that
//  do the minimizing are not tested here.
//

import CoreGraphics
import Testing
@testable import OpenClicky

struct WindowMinimizerTests {
    @Test func onlyOrdinaryWindowsOfRegularAppsAreMinimizedNeverOpenClickysOwn() {
        let onScreen = [
            OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800), layer: 0),
            OnScreenWindow(ownerPID: 99, bounds: CGRect(x: 0, y: 0, width: 1710, height: 1112), layer: 0),      // OpenClicky's overlay
            OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 300, y: 300, width: 40, height: 20), layer: 0),     // a tooltip
            OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 0, width: 400, height: 300), layer: 25),      // a menu
            OnScreenWindow(ownerPID: 20, bounds: CGRect(x: 0, y: 40, width: 800, height: 600), layer: 0),      // a background-only app
        ]
        let picked = WindowMinimizer.windowsToMinimize(onScreen: onScreen, regularAppPIDs: [10, 99], ownPID: 99)
        #expect(picked == [MinimizableWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800))])
    }

    @Test func aKeptAppsWindowsStayOnScreen() {
        let onScreen = [
            OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800)),
            OnScreenWindow(ownerPID: 30, bounds: CGRect(x: 100, y: 100, width: 900, height: 600)),   // Terminal
        ]
        let picked = WindowMinimizer.windowsToMinimize(onScreen: onScreen, regularAppPIDs: [10, 30], ownPID: 99, keptAppPIDs: [30])
        #expect(picked == [MinimizableWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800))])
    }

    @Test func aSpokenAppNameFindsTheRunningApp() {
        let running = [
            RunningAppName(processIdentifier: 30, localizedName: "Terminal", bundleIdentifier: "com.apple.Terminal"),
            RunningAppName(processIdentifier: 40, localizedName: "Google Chrome", bundleIdentifier: "com.google.Chrome"),
            RunningAppName(processIdentifier: 50, localizedName: "Visual Studio Code", bundleIdentifier: "com.microsoft.VSCode"),
        ]
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "the terminal", among: running) == [30])
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "com.apple.Terminal", among: running) == [30])
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "Chrome", among: running) == [40])
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "studio code", among: running) == [50])
        // Whole words only: "term" is not Terminal.
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "term", among: running).isEmpty)
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "Xcode", among: running).isEmpty)
        #expect(WindowMinimizer.processIDs(ofAppsNamed: "  ", among: running).isEmpty)
    }

    @Test func theLayerDefaultsToAnOrdinaryWindow() {
        #expect(OnScreenWindow(ownerPID: 1, bounds: .zero).layer == 0)
    }
}
