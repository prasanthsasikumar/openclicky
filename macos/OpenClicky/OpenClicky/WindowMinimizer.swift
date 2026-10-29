//
//  WindowMinimizer.swift
//  OpenClicky
//
//  "Minimize everything": every ordinary window on screen goes to the Dock through Accessibility,
//  and "bring them back" restores exactly those. "Minimize everything but Terminal" keeps one app
//  up, and "show just the terminal" brings back that app's windows alone. OpenClicky's own windows
//  (the overlay, the island, the orb) and windows on other Spaces are never touched.
//

import AppKit
import ApplicationServices
import Foundation

nonisolated struct MinimizableWindow: Equatable {
    let ownerPID: pid_t
    let bounds: CGRect
}

/// A running app as the name matching sees it.
nonisolated struct RunningAppName: Equatable {
    let processIdentifier: pid_t
    let localizedName: String
    let bundleIdentifier: String?
}

/// A window minimize_all minimized, held by its Accessibility element: restoring by frame also
/// brought back a window the person had minimized themselves at the same size and place (8
/// minimized, 9 back, 2026-10-28).
nonisolated struct MinimizedWindow {
    let ownerPID: pid_t
    let element: AXUIElement
}

nonisolated enum WindowMinimizer {
    /// Smaller than this is a tooltip or a palette, not a window anyone wants in the Dock.
    private static let smallestWindowSide: CGFloat = 50
    /// How far an Accessibility frame may sit from the CGWindowList bounds and still be the
    /// same window (the two agree to the point).
    private static let frameMatchTolerance: CGFloat = 2

    // MARK: - Pure part (unit-tested)

    /// Ordinary windows (layer 0, big enough to be a window) of regular apps other than OpenClicky
    /// and the apps asked to stay up.
    static func windowsToMinimize(onScreen: [OnScreenWindow], regularAppPIDs: Set<pid_t>, ownPID: pid_t, keptAppPIDs: Set<pid_t> = []) -> [MinimizableWindow] {
        onScreen
            .filter { window in
                window.layer == 0
                    && window.ownerPID != ownPID
                    && !keptAppPIDs.contains(window.ownerPID)
                    && regularAppPIDs.contains(window.ownerPID)
                    && window.bounds.width >= smallestWindowSide && window.bounds.height >= smallestWindowSide
            }
            .map { MinimizableWindow(ownerPID: $0.ownerPID, bounds: $0.bounds) }
    }

    /// The running apps the spoken name means: an exact name or bundle id first ("Terminal",
    /// "com.apple.Terminal"), else every app whose name holds it as whole words ("Chrome" for
    /// Google Chrome). Case and a leading "the" do not matter. Empty when nothing matches.
    static func processIDs(ofAppsNamed spokenName: String, among runningApps: [RunningAppName]) -> Set<pid_t> {
        var wanted = spokenName.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if wanted.hasPrefix("the ") { wanted = String(wanted.dropFirst(4)) }
        guard !wanted.isEmpty else { return [] }
        let exactMatches = runningApps.filter { app in
            app.localizedName.lowercased() == wanted || app.bundleIdentifier?.lowercased() == wanted
        }
        if !exactMatches.isEmpty { return Set(exactMatches.map(\.processIdentifier)) }
        let wantedWords = wanted.split(separator: " ")
        let wordMatches = runningApps.filter { app in
            let nameWords = app.localizedName.lowercased().split(separator: " ")
            guard nameWords.count >= wantedWords.count else { return false }
            return (0...(nameWords.count - wantedWords.count)).contains { start in
                Array(nameWords[start..<(start + wantedWords.count)]) == wantedWords
            }
        }
        return Set(wordMatches.map(\.processIdentifier))
    }

    // MARK: - Accessibility

    /// Minimizes each window; returns the ones it minimized.
    @MainActor
    static func minimize(_ windows: [MinimizableWindow]) -> [MinimizedWindow] {
        guard AXIsProcessTrusted() else { return [] }
        var minimized: [MinimizedWindow] = []
        for (ownerPID, windowsOfApp) in Dictionary(grouping: windows, by: \.ownerPID) {
            for accessibilityWindow in accessibilityWindows(ofProcess: ownerPID) {
                guard let frame = copyFrame(accessibilityWindow),
                      windowsOfApp.contains(where: { matches($0.bounds, frame) }) else { continue }
                // A window already in the Dock is not ours: a window the person minimized
                // themselves, with the same frame as one on screen, stays theirs.
                if isMinimized(accessibilityWindow) { continue }
                if setMinimized(true, on: accessibilityWindow) {
                    minimized.append(MinimizedWindow(ownerPID: ownerPID, element: accessibilityWindow))
                }
            }
        }
        return minimized
    }

    /// Un-minimizes each window still in the Dock; returns how many it brought back.
    @MainActor
    static func restore(_ windows: [MinimizedWindow]) -> Int {
        guard AXIsProcessTrusted() else { return 0 }
        return windows.filter { isMinimized($0.element) && setMinimized(false, on: $0.element) }.count
    }

    /// Un-minimizes every minimized window of these apps, for "show me Terminal" when it was not
    /// OpenClicky that put Terminal in the Dock. The person named the app, so they are all wanted.
    @MainActor
    static func restoreEveryMinimizedWindow(ofProcesses processIdentifiers: Set<pid_t>) -> Int {
        guard AXIsProcessTrusted() else { return 0 }
        var restoredCount = 0
        for processIdentifier in processIdentifiers {
            for accessibilityWindow in accessibilityWindows(ofProcess: processIdentifier)
            where isMinimized(accessibilityWindow) && setMinimized(false, on: accessibilityWindow) {
                restoredCount += 1
            }
        }
        return restoredCount
    }

    @MainActor
    private static func accessibilityWindows(ofProcess processIdentifier: pid_t) -> [AXUIElement] {
        let application = AXUIElementCreateApplication(processIdentifier)
        AXUIElementSetMessagingTimeout(application, 0.25)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application, kAXWindowsAttribute as CFString, &value) == .success,
              let accessibilityWindows = value as? [AXUIElement] else { return [] }
        return accessibilityWindows
    }

    private static func isMinimized(_ accessibilityWindow: AXUIElement) -> Bool {
        var minimizedValue: CFTypeRef?
        return AXUIElementCopyAttributeValue(accessibilityWindow, kAXMinimizedAttribute as CFString, &minimizedValue) == .success
            && (minimizedValue as? Bool) == true
    }

    private static func setMinimized(_ minimized: Bool, on accessibilityWindow: AXUIElement) -> Bool {
        AXUIElementSetAttributeValue(accessibilityWindow, kAXMinimizedAttribute as CFString, minimized as CFBoolean) == .success
    }

    private static func matches(_ bounds: CGRect, _ frame: CGRect) -> Bool {
        abs(bounds.minX - frame.minX) <= frameMatchTolerance && abs(bounds.minY - frame.minY) <= frameMatchTolerance
            && abs(bounds.width - frame.width) <= frameMatchTolerance && abs(bounds.height - frame.height) <= frameMatchTolerance
    }

    private static func copyFrame(_ element: AXUIElement) -> CGRect? {
        var positionValue: CFTypeRef?
        var sizeValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
              AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionValue as! AXValue, .cgPoint, &position),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: position, size: size)
    }
}

/// What "minimize everything" minimized, so "bring them back" restores those and not every
/// window in the Dock. Newest set first; the app that was in front comes back in front.
@MainActor
final class MinimizedWindowsMemory {
    private var sets: [(windows: [MinimizedWindow], frontAppPID: pid_t?)] = []

    /// Minimizes every ordinary window except those of the app named in `keepingAppNamed`.
    /// Nil when that app is not running (nothing is minimized then).
    func minimizeEverything(keepingAppNamed keptAppName: String?) -> Int? {
        var keptAppPIDs: Set<pid_t> = []
        if let keptAppName {
            keptAppPIDs = WindowMinimizer.processIDs(ofAppsNamed: keptAppName, among: Self.runningAppNames())
            guard !keptAppPIDs.isEmpty else { return nil }
        }
        let regularAppPIDs = Set(NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }.map(\.processIdentifier))
        let windows = WindowMinimizer.windowsToMinimize(
            onScreen: OnScreenWindows.current(), regularAppPIDs: regularAppPIDs,
            ownPID: ProcessInfo.processInfo.processIdentifier, keptAppPIDs: keptAppPIDs)
        let frontAppPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let minimized = windows.isEmpty ? [] : WindowMinimizer.minimize(windows)
        if !minimized.isEmpty { sets.append((minimized, frontAppPID)) }
        if let keptAppPID = keptAppPIDs.first, let keptApp = NSRunningApplication(processIdentifier: keptAppPID) {
            keptApp.activate()
        }
        return minimized.count
    }

    /// With no app: brings back the last set. With an app: brings back that app's windows only,
    /// from every set (and any of its windows the person minimized themselves), takes them out of
    /// what "bring them back" would restore later, and brings the app to the front. Returns how
    /// many windows came back, or nil when the named app is not running.
    func restore(appNamed appName: String?) -> Int? {
        guard let appName else {
            guard let last = sets.popLast() else { return 0 }
            let count = WindowMinimizer.restore(last.windows)
            if let frontAppPID = last.frontAppPID, let app = NSRunningApplication(processIdentifier: frontAppPID) {
                app.activate()
            }
            return count
        }
        let appPIDs = WindowMinimizer.processIDs(ofAppsNamed: appName, among: Self.runningAppNames())
        guard !appPIDs.isEmpty else { return nil }
        sets = sets
            .map { set in (set.windows.filter { !appPIDs.contains($0.ownerPID) }, set.frontAppPID) }
            .filter { !$0.windows.isEmpty }
        let count = WindowMinimizer.restoreEveryMinimizedWindow(ofProcesses: appPIDs)
        if let appPID = appPIDs.first, let app = NSRunningApplication(processIdentifier: appPID) {
            app.activate()
        }
        return count
    }

    private static func runningAppNames() -> [RunningAppName] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular }
            .compactMap { app in
                guard let localizedName = app.localizedName else { return nil }
                return RunningAppName(processIdentifier: app.processIdentifier, localizedName: localizedName, bundleIdentifier: app.bundleIdentifier)
            }
    }
}
