//
//  CursorFlightPlanner.swift
//  OpenClicky
//
//  Plans the cursor buddy's flights between displays. Each display has its own overlay window,
//  so a flight whose destination is on another display (docking into the notch from an external
//  monitor, or flying back to a mouse that moved to another screen) is split into two legs: to
//  the edge of the current display nearest the destination, then from the matching edge of the
//  destination display to the destination itself. Pure geometry, unit-tested.
//

import CoreGraphics

/// One leg of a buddy flight, animated by the overlay whose screen is `screenFrame`.
struct CursorFlightLeg: Equatable {
    enum Purpose: Equatable {
        /// Into the notch HUD; the final leg ends with the buddy slipping into the notch.
        case toDock
        /// Back to the mouse pointer; the final leg ends with normal cursor following.
        case toMouse
    }

    let purpose: Purpose
    /// The display (AppKit global frame) whose overlay animates this leg.
    let screenFrame: CGRect
    /// Where the buddy appears before the leg starts; nil = wherever it is on that display now.
    let startScreenLocation: CGPoint?
    /// Where the leg ends, in AppKit global coordinates.
    let endScreenLocation: CGPoint
    /// True when the leg ends at the destination (not at a display edge).
    let isFinalLeg: Bool
    /// Distinguishes consecutive legs with identical geometry so observers see a change.
    let sequence: Int
}

enum CursorFlightPlanner {
    /// The point of `rect` closest to `point` (the point itself when it is inside).
    static func nearestPoint(to point: CGPoint, in rect: CGRect) -> CGPoint {
        CGPoint(
            x: min(max(point.x, rect.minX), rect.maxX),
            y: min(max(point.y, rect.minY), rect.maxY)
        )
    }

    /// The first leg of a flight that starts on `fromScreenFrame`. When the destination is on that
    /// display this is the whole flight; otherwise it ends at the display edge nearest the destination.
    static func firstLeg(
        purpose: CursorFlightLeg.Purpose,
        fromScreenFrame: CGRect,
        destination: CGPoint,
        destinationScreenFrame: CGRect,
        sequence: Int
    ) -> CursorFlightLeg {
        if fromScreenFrame == destinationScreenFrame || fromScreenFrame.contains(destination) {
            return CursorFlightLeg(
                purpose: purpose,
                screenFrame: fromScreenFrame,
                startScreenLocation: nil,
                endScreenLocation: destination,
                isFinalLeg: true,
                sequence: sequence
            )
        }
        return CursorFlightLeg(
            purpose: purpose,
            screenFrame: fromScreenFrame,
            startScreenLocation: nil,
            endScreenLocation: nearestPoint(to: destination, in: fromScreenFrame),
            isFinalLeg: false,
            sequence: sequence
        )
    }

    /// The leg that continues a flight on the destination display: it enters at the edge point
    /// nearest to where the previous leg left off and ends at the destination.
    static func finalLeg(
        purpose: CursorFlightLeg.Purpose,
        afterExitPoint exitPoint: CGPoint,
        destination: CGPoint,
        destinationScreenFrame: CGRect,
        sequence: Int
    ) -> CursorFlightLeg {
        CursorFlightLeg(
            purpose: purpose,
            screenFrame: destinationScreenFrame,
            startScreenLocation: nearestPoint(to: exitPoint, in: destinationScreenFrame),
            endScreenLocation: destination,
            isFinalLeg: true,
            sequence: sequence
        )
    }
}
