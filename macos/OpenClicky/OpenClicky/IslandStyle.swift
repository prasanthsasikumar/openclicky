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
        case .connect, .permission, .composer, .tour, .plate, .notice, .reply, .settings, .full: return min(max(width * 0.05, 20), 30)
        }
    }

    /// How far the top corners curve outward into the menu bar, continuing the notch housing. A
    /// side drawer hangs from a screen edge and has none.
    static func topFlare(for expansion: NotchHUDExpansion, isSideDrawer: Bool) -> CGFloat {
        guard !isSideDrawer else { return 0 }
        switch expansion {
        case .collapsed, .peek: return 6
        case .full: return 12
        case .compact, .connect, .permission, .composer, .tour, .plate, .notice, .reply: return 10
        case .settings: return 12
        }
    }
}

/// The island's motion: every change of shape runs on the one structural curve, what is inside
/// swaps by opacity, and a press shrinks it a touch. Reduce motion turns each into a short fade
/// and drops the press.
enum IslandMotion {
    static let fade = Motion.fade
    static let pressScale: CGFloat = Motion.pressScale
    static let pressDuration: TimeInterval = Motion.hoverDuration
    /// How long the new content waits for the shape to open under it.
    static let contentDelay: TimeInterval = 0.07
    /// How long the pointer rests on the island before the panel opens by itself.
    static let dwellSeconds: TimeInterval = 0.75

    /// The island changing size, whatever it changes to.
    static func reshape(reduceMotion: Bool) -> Animation {
        Motion.structural(reduceMotion: reduceMotion)
    }

    static func unfoldWidth(reduceMotion: Bool) -> Animation { reshape(reduceMotion: reduceMotion) }
    static func unfoldHeight(reduceMotion: Bool) -> Animation { reshape(reduceMotion: reduceMotion) }
    static func peek(reduceMotion: Bool) -> Animation { reshape(reduceMotion: reduceMotion) }

    static func pressScale(reduceMotion: Bool) -> CGFloat {
        reduceMotion ? 1 : pressScale
    }

    /// What is inside the island: the new content fades in once the shape has begun to open, the
    /// old fades out at once, and nothing scales (the shape does the moving).
    static var contentTransition: AnyTransition {
        .asymmetric(
            insertion: .opacity.animation(Motion.swap.delay(contentDelay)),
            removal: .opacity.animation(.easeIn(duration: 0.1)))
    }
}
