//
//  OnScreenWindows.swift
//  OpenClicky
//
//  The windows on screen right now, as CGWindowList reports them, for whoever needs to reason
//  about them (the tab locator's "is this tab covered?", minimize everything).
//

import AppKit
import Foundation

/// One on-screen window as CGWindowList reports it: owner process, bounds in CG global
/// coordinates (top-left origin), and its window layer (0 is an ordinary window; menus,
/// tooltips and the like sit above).
nonisolated struct OnScreenWindow: Equatable {
    let ownerPID: pid_t
    let bounds: CGRect
    let layer: Int

    init(ownerPID: pid_t, bounds: CGRect, layer: Int = 0) {
        self.ownerPID = ownerPID
        self.bounds = bounds
        self.layer = layer
    }
}

nonisolated enum OnScreenWindows {
    /// Every visible window on the current Space, front to back. Windows on other Spaces are
    /// not on screen and never appear.
    static func current() -> [OnScreenWindow] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { info in
            guard let layer = info[kCGWindowLayer as String] as? Int,
                  let pid = info[kCGWindowOwnerPID as String] as? pid_t,
                  let boundsDictionary = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDictionary),
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0 else { return nil }
            return OnScreenWindow(ownerPID: pid, bounds: bounds, layer: layer)
        }
    }
}
