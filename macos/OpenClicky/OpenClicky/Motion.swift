//
//  Motion.swift
//  OpenClicky
//
//  OpenClicky's motion vocabulary in one place, so every surface moves the same way: one curve
//  for anything that changes shape, three springs (settle, pop, bounce), short ease-outs for
//  hover and content, a short ease-in for leaving. Reduce Motion turns each into a brief fade.
//  New surfaces take their animations from here; older ones move over as they are touched.
//

import SwiftUI

enum Motion {
    /// A spring by the two numbers SwiftUI takes, kept as data so tests can pin it and Core
    /// Animation layers can use the same spring.
    struct SpringSpec: Equatable {
        var response: Double
        var damping: Double

        var animation: Animation { .spring(response: response, dampingFraction: damping) }
        /// The same spring for `CASpringAnimation` with mass 1.
        var stiffness: Double { pow(2 * .pi / response, 2) }
        var dampingCoefficient: Double { 4 * .pi * damping / response }
    }

    /// A cubic curve with its duration.
    struct CurveSpec: Equatable {
        var x1: Double, y1: Double, x2: Double, y2: Double
        var duration: Double

        var animation: Animation { .timingCurve(x1, y1, x2, y2, duration: duration) }

        /// The curve's value at a fraction of its duration (for flights drawn by hand).
        func value(at fraction: Double) -> Double {
            let target = min(1, max(0, fraction))
            func bezier(_ t: Double, _ a: Double, _ b: Double) -> Double {
                let u = 1 - t
                return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t
            }
            var low = 0.0, high = 1.0, t = target
            for _ in 0..<24 {
                let x = bezier(t, x1, x2)
                if abs(x - target) < 0.0005 { break }
                if x < target { low = t } else { high = t }
                t = (low + high) / 2
            }
            return bezier(t, y1, y2)
        }
    }

    // Shape changes: fast out of the gate, a long soft landing.
    static let structural = CurveSpec(x1: 0.23, y1: 1, x2: 0.32, y2: 1, duration: 0.34)
    static let structuralSlow = CurveSpec(x1: 0.23, y1: 1, x2: 0.32, y2: 1, duration: 0.40)
    /// A group of things laid out together (a hand of cards).
    static let spread = CurveSpec(x1: 0.22, y1: 1, x2: 0.36, y2: 1, duration: 0.48)

    // Springs.
    /// Panels, rows, things finding their place: no visible bounce.
    static let settle = SpringSpec(response: 0.40, damping: 0.86)
    /// Chips, cards and buttons arriving.
    static let pop = SpringSpec(response: 0.32, damping: 0.74)
    /// Ticks, stamps, small rewards.
    static let bounce = SpringSpec(response: 0.30, damping: 0.56)
    /// A thrown thing landing: slower than a pop, a little overshoot.
    static let land = SpringSpec(response: 0.50, damping: 0.78)

    static let hoverDuration = 0.12
    static let swapDuration = 0.18
    static let exitDuration = 0.22
    static let fadeDuration = 0.15
    /// Between items that arrive one after another.
    static let stagger = 0.08

    static func structural(reduceMotion: Bool) -> Animation { reduceMotion ? fade : structural.animation }
    static func spread(reduceMotion: Bool) -> Animation { reduceMotion ? fade : spread.animation }
    static func settle(reduceMotion: Bool) -> Animation { reduceMotion ? fade : settle.animation }
    static func pop(reduceMotion: Bool) -> Animation { reduceMotion ? fade : pop.animation }
    static func bounce(reduceMotion: Bool) -> Animation { reduceMotion ? fade : bounce.animation }
    static func land(reduceMotion: Bool) -> Animation { reduceMotion ? fade : land.animation }
    static var hover: Animation { .easeOut(duration: hoverDuration) }
    static var swap: Animation { .easeOut(duration: swapDuration) }
    static var exit: Animation { .easeIn(duration: exitDuration) }
    static var fade: Animation { .easeInOut(duration: fadeDuration) }

    /// A point on a thrown arc from `start` to `end`: straight-line travel with a lift that
    /// peaks midway. `lift` is how far above the straight line the peak is.
    static func arcPoint(from start: CGPoint, to end: CGPoint, lift: CGFloat, progress: CGFloat) -> CGPoint {
        let t = min(1, max(0, progress))
        return CGPoint(x: start.x + (end.x - start.x) * t,
                       y: start.y + (end.y - start.y) * t - lift * 4 * t * (1 - t))
    }
}
