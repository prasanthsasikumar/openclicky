//
//  PointerPresence.swift
//  OpenClicky
//
//  When the Clicky pointer is on screen while it follows the mouse. Over a video the system
//  cursor slips away once the mouse rests and comes back on a nudge; the pointer can do the same,
//  or stay out of sight until the mouse is shaken, the way macOS finds a lost cursor.
//

import CoreGraphics
import Foundation

enum PointerPresence: String, CaseIterable, Identifiable {
    /// The pointer follows the mouse all the time.
    case always
    /// It fades out once the mouse has rested a few seconds, and any movement brings it back.
    case whileMoving
    /// It stays out of sight until the mouse is shaken, then follows until the mouse rests.
    case onShake

    var id: String { rawValue }

    var label: String {
        switch self {
        case .always: return "always"
        case .whileMoving: return "while the mouse moves"
        case .onShake: return "after a shake"
        }
    }
}

/// Spots a side-to-side (or up-and-down) shake: several quick reversals of direction, each after
/// a swing long enough that a wobble of the hand does not count. Fed with mouse samples.
struct MouseShakeDetector {
    /// Reversals must all fall within this many seconds.
    var window: TimeInterval = 0.7
    /// A swing shorter than this before a reversal is ignored.
    var minimumSwing: CGFloat = 25
    /// How many qualifying reversals make a shake.
    var reversalsNeeded = 3

    private struct Axis {
        var last: CGFloat?
        var direction: Int = 0
        var swingStart: CGFloat = 0
        var reversals: [TimeInterval] = []

        mutating func feed(_ value: CGFloat, at time: TimeInterval, minimumSwing: CGFloat, window: TimeInterval, reversalsNeeded: Int) -> Bool {
            defer { last = value }
            guard let last else { swingStart = value; return false }
            let delta = value - last
            guard abs(delta) >= 1 else { return false }
            let newDirection = delta > 0 ? 1 : -1
            if newDirection != direction {
                if direction != 0, abs(last - swingStart) >= minimumSwing {
                    reversals.append(time)
                }
                swingStart = last
                direction = newDirection
            }
            reversals.removeAll { time - $0 > window }
            if reversals.count >= reversalsNeeded {
                reversals.removeAll()
                return true
            }
            return false
        }
    }

    private var horizontal = Axis()
    private var vertical = Axis()

    /// Returns true on the sample that completes a shake.
    mutating func feed(_ point: CGPoint, at time: TimeInterval) -> Bool {
        let shookSideways = horizontal.feed(point.x, at: time, minimumSwing: minimumSwing, window: window, reversalsNeeded: reversalsNeeded)
        let shookUpDown = vertical.feed(point.y, at: time, minimumSwing: minimumSwing, window: window, reversalsNeeded: reversalsNeeded)
        return shookSideways || shookUpDown
    }
}

/// Decides, sample by sample, whether the pointer should be on screen for the chosen presence.
struct PointerPresenceTracker {
    var presence: PointerPresence {
        didSet { if presence == .always { isAwake = true } }
    }
    /// Seconds of still mouse before the pointer rests away.
    var restDelay: TimeInterval
    private(set) var isAwake: Bool
    /// True on the one update that completed a shake (only watched for in the shake mode).
    private(set) var shookOnLastUpdate = false
    private var lastMovedAt: TimeInterval?
    private var lastPoint: CGPoint?
    private var shake = MouseShakeDetector()

    init(presence: PointerPresence, restDelay: TimeInterval = 3) {
        self.presence = presence
        self.restDelay = restDelay
        self.isAwake = presence != .onShake
    }

    /// Feed the mouse position; returns whether the pointer should be on screen.
    @discardableResult
    mutating func update(mouse: CGPoint, at time: TimeInterval) -> Bool {
        defer { lastPoint = mouse }
        let moved: Bool
        if let lastPoint {
            moved = hypot(mouse.x - lastPoint.x, mouse.y - lastPoint.y) >= 1
        } else {
            moved = false
            lastMovedAt = time
        }
        if moved { lastMovedAt = time }
        let shaken = shake.feed(mouse, at: time)
        shookOnLastUpdate = presence == .onShake && shaken

        switch presence {
        case .always:
            isAwake = true
        case .whileMoving:
            isAwake = moved || time - (lastMovedAt ?? time) < restDelay
        case .onShake:
            if shaken { isAwake = true; lastMovedAt = time }
            if isAwake, !moved, time - (lastMovedAt ?? time) >= restDelay { isAwake = false }
        }
        return isAwake
    }

    /// Show the pointer now (it just flew back to the mouse, say); it rests away again as usual.
    mutating func wake(at time: TimeInterval) {
        isAwake = true
        lastMovedAt = time
    }
}
