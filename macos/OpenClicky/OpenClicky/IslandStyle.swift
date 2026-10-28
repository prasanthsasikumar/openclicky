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
    /// 11 pt is the hardware notch's own corner; the peek and the busy strip round a little more;
    /// cards and the open panel scale with their width (5 %, between 20 and 30 pt).
    static func bottomRadius(for expansion: NotchHUDExpansion, width: CGFloat) -> CGFloat {
        switch expansion {
        case .collapsed: return 11
        case .peek, .compact: return 18
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

/// Where the black ends and the glass begins, as fractions of the panel's height (0 = top).
struct IslandGlassStops: Equatable {
    let start: CGFloat
    let end: CGFloat
}

/// The black-into-glass melt and its setting.
enum IslandGlass {
    static let defaultsKey = "openclicky.island.glassLevel"
    /// Solid black: it reads more easily than the melt into glass (2026-10-28; it was 0.6). The
    /// Settings slider is gone too (it cluttered General), but the glass still works and can be
    /// brought back without a build: `defaults write org.openclicky.app openclicky.island.glassLevel 0.6`
    /// (the island picks it up while running).
    static let defaultLevel: Double = 0
    /// The dark wash over the glass so white text keeps its contrast on a bright wallpaper.
    static let wash: Double = 0.35

    /// The stops for a glass level: at 1 the black ends at the band row; at 0 there is no glass
    /// at all (nil); in between the black reaches further down. The fade spans 40 % of what is
    /// left under it.
    static func stops(level: Double, bandBottom: CGFloat, panelHeight: CGFloat) -> IslandGlassStops? {
        guard level > 0, panelHeight > 0 else { return nil }
        let level = CGFloat(min(level, 1))
        let start = panelHeight - (panelHeight - bandBottom) * level
        let end = start + (panelHeight - start) * 0.4
        return IslandGlassStops(start: start / panelHeight, end: end / panelHeight)
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
