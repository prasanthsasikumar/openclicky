//
//  PointerPresenceTests.swift
//  OpenClickyTests
//
//  When the Clicky pointer is on screen while it follows the mouse: always, only while the mouse
//  is moving (it rests away like the system cursor over a video), or only after a shake.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct PointerPresenceTests {
    private let frameInterval: TimeInterval = 1.0 / 30.0

    @Test @MainActor func aPointerThatWasDockedIsReleasedOnceAndTheSettingForgotten() {
        // "Dock the pointer in the island" was removed (2026-10-28); someone who had it on gets the
        // pointer the way everyone has it, and the stored flag goes so it is never read again.
        let defaults = UserDefaults(suiteName: "retired-dock-\(UUID().uuidString)")!
        defaults.set(true, forKey: CompanionManager.retiredDockedPointerKey)
        #expect(CompanionManager.forgetRetiredDockSetting(in: defaults))
        #expect(defaults.object(forKey: CompanionManager.retiredDockedPointerKey) == nil)
        #expect(!CompanionManager.forgetRetiredDockSetting(in: defaults))
    }

    /// Feeds a straight move from `from` to `to` over `duration` seconds, sampled at 30 Hz.
    private func sweep(_ tracker: inout PointerPresenceTracker, from: CGPoint, to: CGPoint, start: TimeInterval, duration: TimeInterval) -> TimeInterval {
        let steps = max(1, Int(duration / frameInterval))
        var time = start
        for step in 1...steps {
            let fraction = CGFloat(step) / CGFloat(steps)
            time = start + duration * Double(step) / Double(steps)
            let point = CGPoint(x: from.x + (to.x - from.x) * fraction, y: from.y + (to.y - from.y) * fraction)
            tracker.update(mouse: point, at: time)
        }
        return time
    }

    /// Holds the mouse still at `point` for `duration` seconds, sampled at 30 Hz.
    private func rest(_ tracker: inout PointerPresenceTracker, at point: CGPoint, start: TimeInterval, duration: TimeInterval) -> TimeInterval {
        sweep(&tracker, from: point, to: point, start: start, duration: duration)
    }

    /// A deliberate side-to-side shake: five 120 pt swings (four reversals) in about two thirds of a second.
    private func shake(_ tracker: inout PointerPresenceTracker, around point: CGPoint, start: TimeInterval) -> TimeInterval {
        var time = start
        var from = point
        for swing in 0..<5 {
            let to = CGPoint(x: point.x + (swing.isMultiple(of: 2) ? 120 : -120), y: point.y)
            time = sweep(&tracker, from: from, to: to, start: time, duration: 0.13)
            from = to
        }
        return time
    }

    @Test func alwaysStaysAwakeThroughALongRest() {
        var tracker = PointerPresenceTracker(presence: .always)
        #expect(tracker.isAwake)
        _ = rest(&tracker, at: CGPoint(x: 100, y: 100), start: 0, duration: 10)
        #expect(tracker.isAwake)
    }

    @Test func whileMovingRestsAwayAfterTheDelayAndReturnsOnMovement() {
        var tracker = PointerPresenceTracker(presence: .whileMoving, restDelay: 3)
        #expect(tracker.isAwake)
        var time = rest(&tracker, at: CGPoint(x: 100, y: 100), start: 0, duration: 2.5)
        #expect(tracker.isAwake, "still within the rest delay")
        time = rest(&tracker, at: CGPoint(x: 100, y: 100), start: time, duration: 1)
        #expect(!tracker.isAwake, "the mouse has rested past the delay")
        _ = sweep(&tracker, from: CGPoint(x: 100, y: 100), to: CGPoint(x: 140, y: 100), start: time, duration: 0.1)
        #expect(tracker.isAwake, "any movement brings it back")
    }

    @Test func whileMovingIgnoresSensorJitter() {
        var tracker = PointerPresenceTracker(presence: .whileMoving, restDelay: 3)
        var time = rest(&tracker, at: CGPoint(x: 100, y: 100), start: 0, duration: 3.5)
        #expect(!tracker.isAwake)
        time = sweep(&tracker, from: CGPoint(x: 100, y: 100), to: CGPoint(x: 100.4, y: 100.3), start: time, duration: 0.1)
        #expect(!tracker.isAwake, "sub-point drift is not movement")
    }

    @Test func onShakeStartsHiddenAndIgnoresPlainMovement() {
        var tracker = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        #expect(!tracker.isAwake)
        _ = sweep(&tracker, from: CGPoint(x: 0, y: 0), to: CGPoint(x: 800, y: 500), start: 0, duration: 1)
        #expect(!tracker.isAwake)
    }

    @Test func onShakeWakesOnAShakeAndRestsAwayAgain() {
        var tracker = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        var time = shake(&tracker, around: CGPoint(x: 400, y: 300), start: 0)
        #expect(tracker.isAwake, "a shake summons the pointer")
        time = sweep(&tracker, from: CGPoint(x: 400, y: 300), to: CGPoint(x: 600, y: 300), start: time, duration: 1)
        #expect(tracker.isAwake, "it follows while the mouse keeps moving")
        time = rest(&tracker, at: CGPoint(x: 600, y: 300), start: time, duration: 3.5)
        #expect(!tracker.isAwake, "and rests away once the mouse stops")
    }

    @Test func aSlowBackAndForthIsNotAShake() {
        var tracker = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        var time: TimeInterval = 0
        var from = CGPoint(x: 400, y: 300)
        for swing in 0..<4 {
            let to = CGPoint(x: 400 + (swing.isMultiple(of: 2) ? 120 : -120), y: 300)
            time = sweep(&tracker, from: from, to: to, start: time, duration: 0.8)
            from = to
        }
        #expect(!tracker.isAwake)
    }

    @Test func wakeShowsThePointerUntilTheMouseRests() {
        var tracker = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        tracker.wake(at: 10)
        #expect(tracker.isAwake)
        _ = rest(&tracker, at: CGPoint(x: 100, y: 100), start: 10, duration: 3.5)
        #expect(!tracker.isAwake)
    }

    @Test func switchingToAlwaysWakesImmediately() {
        var tracker = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        #expect(!tracker.isAwake)
        tracker.presence = .always
        tracker.update(mouse: CGPoint(x: 1, y: 1), at: 0.5)
        #expect(tracker.isAwake)
    }

    @Test func onlyTheShakeModeReportsAShake() {
        var shakeMode = PointerPresenceTracker(presence: .onShake, restDelay: 3)
        var alwaysMode = PointerPresenceTracker(presence: .always, restDelay: 3)
        var shakeSeen = false
        var alwaysSeen = false
        var time: TimeInterval = 0
        var from = CGPoint(x: 400, y: 300)
        for swing in 0..<5 {
            let to = CGPoint(x: 400 + (swing.isMultiple(of: 2) ? 120 : -120), y: 300)
            let steps = 4
            for step in 1...steps {
                time += 0.033
                let point = CGPoint(x: from.x + (to.x - from.x) * CGFloat(step) / CGFloat(steps), y: 300)
                shakeMode.update(mouse: point, at: time)
                alwaysMode.update(mouse: point, at: time)
                if shakeMode.shookOnLastUpdate { shakeSeen = true }
                if alwaysMode.shookOnLastUpdate { alwaysSeen = true }
            }
            from = to
        }
        #expect(shakeSeen)
        #expect(!alwaysSeen)
    }

    @Test func aQuickWobbleOrThreeTurnsIsNotAShake() {
        func shakes(turns: [CGFloat]) -> Bool {
            var detector = MouseShakeDetector()
            var time: TimeInterval = 0
            for (index, x) in turns.enumerated() where index > 0 {
                let previous = turns[index - 1]
                for step in 1...4 {
                    time += 0.033
                    if detector.feed(CGPoint(x: previous + (x - previous) * CGFloat(step) / 4, y: 50), at: time) { return true }
                }
            }
            return false
        }
        #expect(!shakes(turns: [100, 220, 100, 220, 100]), "three reversals used to be enough; ordinary mousing made them")
        #expect(!shakes(turns: [100, 140, 100, 140, 100, 140, 100, 140]), "a 40 pt jiggle is the hand, not a shake")
    }

    @Test func shakeDetectorNeedsReversalsWithinTheWindow() {
        var detector = MouseShakeDetector()
        var shaken = false
        var time: TimeInterval = 0
        // Five big swings in about two thirds of a second: four reversals of direction.
        let turns: [CGFloat] = [100, 220, 100, 220, 100, 220]
        for (index, x) in turns.enumerated() where index > 0 {
            let previous = turns[index - 1]
            for step in 1...4 {
                time += 0.033
                let point = CGPoint(x: previous + (x - previous) * CGFloat(step) / 4, y: 50)
                if detector.feed(point, at: time) { shaken = true }
            }
        }
        #expect(shaken)
    }
}
