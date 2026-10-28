//
//  WindowMinimizer.swift
//  OpenClicky
//
//  "Minimize everything": every ordinary window on screen goes to the Dock through Accessibility,
//  and "bring them back" restores exactly those. OpenClicky's own windows (the overlay, the
//  island, the orb) and windows on other Spaces are never touched.
//

import AppKit
import ApplicationServices
import Foundation

nonisolated struct MinimizableWindow: Equatable {
    let ownerPID: pid_t
    let bounds: CGRect
}

nonisolated enum WindowMinimizer {
    /// Smaller than this is a tooltip or a palette, not a window anyone wants in the Dock.
    private static let smallestWindowSide: CGFloat = 50
    /// How far an Accessibility frame may sit from the CGWindowList bounds and still be the
    /// same window (the two agree to the point).
    private static let frameMatchTolerance: CGFloat = 2

    // MARK: - Pure part (unit-tested)

    /// Ordinary windows (layer 0, big enough to be a window) of regular apps other than OpenClicky.
    static func windowsToMinimize(onScreen: [OnScreenWindow], regularAppPIDs: Set<pid_t>, ownPID: pid_t) -> [MinimizableWindow] {
        onScreen
            .filter { window in
                window.layer == 0
                    && window.ownerPID != ownPID
                    && regularAppPIDs.contains(window.ownerPID)
                    && window.bounds.width >= smallestWindowSide && window.bounds.height >= smallestWindowSide
            }
            .map { MinimizableWindow(ownerPID: $0.ownerPID, bounds: $0.bounds) }
    }

    // MARK: - Accessibility

    /// Minimizes each window; returns how many it set.
    @MainActor
    static func minimize(_ windows: [MinimizableWindow]) -> Int {
        setMinimized(true, on: windows)
    }

    /// Un-minimizes each window; returns how many it set.
    @MainActor
    static func restore(_ windows: [MinimizableWindow]) -> Int {
        setMinimized(false, on: windows)
    }

    @MainActor
    private static func setMinimized(_ minimized: Bool, on windows: [MinimizableWindow]) -> Int {
        guard AXIsProcessTrusted() else { return 0 }
        var changed = 0
        for (ownerPID, windowsOfApp) in Dictionary(grouping: windows, by: \.ownerPID) {
            let application = AXUIElementCreateApplication(ownerPID)
            AXUIElementSetMessagingTimeout(application, 0.25)
            var value: CFTypeRef?
            guard AXUIElementCopyAttributeValue(application, kAXWindowsAttribute as CFString, &value) == .success,
                  let accessibilityWindows = value as? [AXUIElement] else { continue }
            for accessibilityWindow in accessibilityWindows {
                guard let frame = copyFrame(accessibilityWindow),
                      windowsOfApp.contains(where: { matches($0.bounds, frame) }) else { continue }
                // A window already in the state asked for is not ours: a window the person
                // minimized themselves, with the same frame as one on screen, stays theirs.
                var minimizedValue: CFTypeRef?
                if AXUIElementCopyAttributeValue(accessibilityWindow, kAXMinimizedAttribute as CFString, &minimizedValue) == .success,
                   (minimizedValue as? Bool) == minimized { continue }
                if AXUIElementSetAttributeValue(accessibilityWindow, kAXMinimizedAttribute as CFString, minimized as CFBoolean) == .success {
                    changed += 1
                }
            }
        }
        return changed
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
    private var sets: [(windows: [MinimizableWindow], frontAppPID: pid_t?)] = []

    func minimizeEverything() -> Int {
        let regularAppPIDs = Set(NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }.map(\.processIdentifier))
        let windows = WindowMinimizer.windowsToMinimize(
            onScreen: OnScreenWindows.current(), regularAppPIDs: regularAppPIDs, ownPID: ProcessInfo.processInfo.processIdentifier)
        guard !windows.isEmpty else { return 0 }
        let frontAppPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let count = WindowMinimizer.minimize(windows)
        if count > 0 { sets.append((windows, frontAppPID)) }
        return count
    }

    func restoreLastSet() -> Int {
        guard let last = sets.popLast() else { return 0 }
        let count = WindowMinimizer.restore(last.windows)
        if let frontAppPID = last.frontAppPID, let app = NSRunningApplication(processIdentifier: frontAppPID) {
            app.activate()
        }
        return count
    }
}
