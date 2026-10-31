//
//  IslandStyle.swift
//  OpenClicky
//
//  The island's look as pure maths: its corners by state, where its black melts into glass, and
//  the springs that unfold it. `NotchHUDView` draws from these; the tests pin the numbers.
//

import CoreGraphics
import SwiftUI

/// The island outline's two parameters, by state.
enum IslandCorners {
    /// 11 pt is the hardware notch's own corner; the peek and the busy strip round a touch more;
    /// cards and the open panel scale with their width (5 %, between 20 and 30 pt).
    static func bottomRadius(for expansion: NotchHUDExpansion, width: CGFloat) -> CGFloat {
        switch expansion {
        case .collapsed: return 11
        // In the band beside the camera the peek and the busy strip are the notch's own height:
        // they keep a corner close to the notch's.
        case .peek, .compact: return 12
        case .connect, .permission, .composer, .tour, .full: return min(max(width * 0.05, 20), 30)
        }
    }

    /// How far the top corners curve outward into the menu bar, continuing the notch housing. A
    /// side drawer hangs from a screen edge and has none.
    static func topFlare(for expansion: NotchHUDExpansion, isSideDrawer: Bool) -> CGFloat {
        guard !isSideDrawer else { return 0 }
        switch expansion {
        case .collapsed, .peek: return 6
        case .full: return 12
        case .compact, .connect, .permission, .composer, .tour: return 10
        }
    }
}

/// The island's motion. Reduce motion turns every spring into a short fade and drops the press.
enum IslandMotion {
    static let fade = Animation.easeInOut(duration: 0.15)
    static let heightDelay: TimeInterval = 0.06
    static let pressScale: CGFloat = 0.97
    static let pressDuration: TimeInterval = 0.12

    static func unfoldWidth(reduceMotion: Bool) -> Animation {
        reduceMotion ? fade : .spring(response: 0.36, dampingFraction: 0.82)
    }

    static func unfoldHeight(reduceMotion: Bool) -> Animation {
        reduceMotion ? fade : .spring(response: 0.42, dampingFraction: 0.84)
    }

    static func peek(reduceMotion: Bool) -> Animation {
        reduceMotion ? fade : .spring(response: 0.3, dampingFraction: 0.8)
    }

    static func pressScale(reduceMotion: Bool) -> CGFloat {
        reduceMotion ? 1 : pressScale
    }
}
