//
//  CursorBuddyMotionTests.swift
//  OpenClickyTests
//
//  The pointer's motion maths: the flight arc, the streak behind it, the waveform shaping, the
//  thinking dots, the idle bob, and the pointer's motion tokens.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct CursorBuddyMotionTests {
    private func isClose(_ a: CGFloat, _ b: CGFloat, within tolerance: CGFloat = 0.0001) -> Bool {
        abs(a - b) <= tolerance
    }

    // MARK: Flight

    @Test func aFlightTakesItsLengthOverEightHundredPointsASecond() {
        #expect(abs(CursorFlightPath.duration(forDistance: 800) - 1.0) < 0.0001)
        #expect(abs(CursorFlightPath.duration(forDistance: 560) - 0.7) < 0.0001)
    }

    @Test func aFlightIsNeverLongerThanOnePointFourSecondsNorShorterThanPointFour() {
        #expect(CursorFlightPath.duration(forDistance: 5000) == 1.4)
        #expect(CursorFlightPath.duration(forDistance: 1120) == 1.4)
        #expect(CursorFlightPath.duration(forDistance: 40) == 0.4)
        #expect(CursorFlightPath.duration(forDistance: 0) == 0.4)
    }

    @Test func theLiftIsAFifthOfTheDistanceUpToEightyPoints() {
        #expect(isClose(CursorFlightPath.lift(forDistance: 100), 20))
        #expect(isClose(CursorFlightPath.lift(forDistance: 400), 80))
        #expect(isClose(CursorFlightPath.lift(forDistance: 3000), 80))
        #expect(CursorFlightPath.lift(forDistance: 0) == 0)
    }

    @Test func theArcStartsAndEndsWhereItShouldAndRisesAboveTheStraightLine() {
        let path = CursorFlightPath(from: CGPoint(x: 100, y: 500), to: CGPoint(x: 900, y: 500))
        #expect(path.pose(atProgress: 0).position == CGPoint(x: 100, y: 500))
        #expect(path.pose(atProgress: 1).position == CGPoint(x: 900, y: 500))
        // The control point is lifted 80 (y grows downwards), so the arc's top is 40 above the line.
        #expect(path.control == CGPoint(x: 500, y: 420))
        let middle = path.pose(atProgress: 0.5).position
        #expect(isClose(middle.x, 500))
        #expect(isClose(middle.y, 460))
        #expect(path.duration == 1.0)
    }

    @Test func theReduceMotionFlightIsStraightAndKeepsItsLeanAndSize() {
        let path = CursorFlightPath(from: CGPoint(x: 100, y: 500), to: CGPoint(x: 900, y: 300), straight: true)
        for step in 0...10 {
            let pose = path.pose(atProgress: Double(step) / 10, calm: true)
            // On the straight line between the two points.
            let alongX = (pose.position.x - 100) / 800
            #expect(isClose(pose.position.y, 500 - 200 * alongX, within: 0.001))
            #expect(pose.rotationDegrees == CursorFlightPath.restingRotationDegrees)
            #expect(pose.scale == 1)
        }
        #expect(path.duration == CursorFlightPath(from: path.start, to: path.end).duration)
    }

    @Test func thePointerFacesWhereItIsGoing() {
        // Flying right: it leaves heading up and right, is level at the top, and comes in heading down.
        let path = CursorFlightPath(from: CGPoint(x: 0, y: 500), to: CGPoint(x: 800, y: 500))
        let leaving = path.pose(atProgress: 0.02).rotationDegrees
        let top = path.pose(atProgress: 0.5).rotationDegrees
        let arriving = path.pose(atProgress: 0.98).rotationDegrees
        #expect(leaving > 60 && leaving < 90)
        #expect(abs(top - 90) < 0.001)
        #expect(arriving > 90 && arriving < 120)

        // Flying left it faces left.
        let back = CursorFlightPath(from: CGPoint(x: 800, y: 500), to: CGPoint(x: 0, y: 500))
        #expect(abs(back.pose(atProgress: 0.5).rotationDegrees - 270) < 0.001)
    }

    @Test func aFlightToWhereItAlreadyIsKeepsTheRestingLean() {
        let here = CGPoint(x: 300, y: 300)
        let path = CursorFlightPath(from: here, to: here)
        #expect(path.pose(atProgress: 0.5).rotationDegrees == CursorFlightPath.restingRotationDegrees)
        #expect(path.pose(atProgress: 0.5).position == here)
    }

    @Test func itSwellsInTheMiddleAndLandsSlightlySmall() {
        #expect(isClose(CursorFlightPath.scale(atProgress: 0), 1))
        #expect(isClose(CursorFlightPath.scale(atProgress: 0.5), 1.3))
        #expect(isClose(CursorFlightPath.scale(atProgress: 1), 0.92))
        // Shrinking all the way through the landing stretch.
        var previous = CursorFlightPath.scale(atProgress: 0.7)
        for step in 1...30 {
            let scale = CursorFlightPath.scale(atProgress: 0.7 + 0.01 * Double(step))
            #expect(scale < previous)
            previous = scale
        }
        #expect(Motion.pointerLandingDuration == 0.16)
    }

    @Test func progressComesFromTheClockAndStopsAtTheEnds() {
        let path = CursorFlightPath(from: .zero, to: CGPoint(x: 800, y: 0))
        #expect(path.progress(afterSeconds: -1) == 0)
        #expect(abs(path.progress(afterSeconds: 0.25) - 0.25) < 0.0001)
        #expect(path.progress(afterSeconds: 3) == 1)
        #expect(CursorFlightPath.eased(0) == 0)
        #expect(CursorFlightPath.eased(0.5) == 0.5)
        #expect(CursorFlightPath.eased(1) == 1)
        #expect(CursorFlightPath.eased(0.1) < 0.1)
        #expect(CursorFlightPath.eased(0.9) > 0.9)
    }

    @Test func theGlowWidensWithTheSwellAndNeverVanishes() {
        #expect(isClose(CursorFlightPath.glowRadius(forScale: 1), 8))
        #expect(isClose(CursorFlightPath.glowRadius(forScale: 1.3), 14))
        #expect(CursorFlightPath.glowRadius(forScale: 0.92) > 6)
        #expect(CursorFlightPath.glowRadius(forScale: 0) == 4)
    }

    // MARK: Streak

    @Test func theStreakTrailsThePointerAlongTheArc() {
        let path = CursorFlightPath(from: CGPoint(x: 0, y: 400), to: CGPoint(x: 1000, y: 400))
        let points = CursorTravelStreak.points(on: path, atProgress: 0.5)
        #expect(points.count == CursorTravelStreak.pieces + 1)
        let head = path.pose(atProgress: 0.5).position
        #expect(isClose(points[CursorTravelStreak.pieces].x, head.x) && isClose(points[CursorTravelStreak.pieces].y, head.y))
        // Oldest first: every point is farther along than the one before.
        for (earlier, later) in zip(points, points.dropFirst()) {
            #expect(later.x > earlier.x)
        }
        // At take-off there is nothing behind it yet.
        let atStart = CursorTravelStreak.points(on: path, atProgress: 0)
        #expect(atStart.allSatisfy { $0 == path.start })
    }

    @Test func theStreakFadesInAndIsGoneOnLanding() {
        #expect(CursorTravelStreak.opacity(atProgress: 0) == 0)
        #expect(CursorTravelStreak.opacity(atProgress: 1) == 0)
        #expect(abs(CursorTravelStreak.opacity(atProgress: 0.4) - CursorTravelStreak.peakOpacity) < 0.0001)
        #expect(CursorTravelStreak.opacity(atProgress: 0.9) < CursorTravelStreak.opacity(atProgress: 0.7))
        #expect(!CursorTravelStreak.isShown(forDistance: 90))
        #expect(CursorTravelStreak.isShown(forDistance: 600))
    }

    // MARK: Waveform

    @Test func roomNoiseMovesNothingAndLoudSpeechFillsTheBars() {
        #expect(CursorWaveform.shapedLevel(0) == 0)
        #expect(CursorWaveform.shapedLevel(0.008) == 0)
        #expect(CursorWaveform.shapedLevel(0.5) == 1)
        #expect(CursorWaveform.shapedLevel(1) == 1)
    }

    @Test func quietSpeechAlreadyShows() {
        // A level of 0.05 is 12 % of the range after the gain; the curve lifts it to about 20 %.
        let shaped = CursorWaveform.shapedLevel(0.05)
        #expect(shaped > 0.18 && shaped < 0.22)
        // Rising all the way.
        var previous: CGFloat = 0
        for step in 1...30 {
            let value = CursorWaveform.shapedLevel(0.008 + CGFloat(step) * 0.01)
            #expect(value > previous)
            previous = value
        }
    }

    @Test func theMiddleBarIsTallestAndStillBarsHoldTheirPlace() {
        let middle = CursorWaveform.barHeight(bar: 2, level: 0.3, time: 0, sways: false)
        let edge = CursorWaveform.barHeight(bar: 0, level: 0.3, time: 0, sways: false)
        #expect(middle > edge)
        // Without the sway, time changes nothing.
        #expect(CursorWaveform.barHeight(bar: 2, level: 0.3, time: 7.3, sways: false) == middle)
        // In silence a still bar rests at its base plus half the sway.
        #expect(isClose(CursorWaveform.barHeight(bar: 1, level: 0, time: 0, sways: false), 3.75))
        // With the sway it stays within the sway's range.
        for step in 0..<40 {
            let height = CursorWaveform.barHeight(bar: 1, level: 0, time: Double(step) * 0.11, sways: true)
            #expect(height >= 3 && height <= 4.5)
        }
    }

    // MARK: Sketching pencil

    @Test func thePencilDrawsItsSquiggleLeftToRightThenTheInkFades() {
        #expect(CursorSketching.progress(time: 0) == 0)
        #expect(abs(CursorSketching.progress(time: CursorSketching.strokeSeconds) - 1) < 0.0001)
        var lastProgress = -1.0
        for step in 0...50 {
            let progress = CursorSketching.progress(time: CursorSketching.strokeSeconds * Double(step) / 50)
            #expect(progress >= lastProgress)
            lastProgress = progress
        }
        // The ink is whole while it is drawn and while it rests, and gone as the round ends.
        #expect(CursorSketching.inkOpacity(time: CursorSketching.strokeSeconds / 2) == 1)
        #expect(CursorSketching.inkOpacity(time: CursorSketching.strokeSeconds + CursorSketching.restSeconds) == 1)
        #expect(CursorSketching.inkOpacity(time: CursorSketching.period - 0.001) < 0.01)
        // The next round is the same as this one.
        #expect(abs(CursorSketching.progress(time: 0.4) - CursorSketching.progress(time: 0.4 + CursorSketching.period)) < 0.0001)
    }

    @Test func theSquiggleStaysInsideItsBox() {
        for step in 0...40 {
            let point = CursorSketching.point(at: Double(step) / 40)
            #expect(point.x >= 0 && point.x <= CursorSketching.size.width)
            #expect(point.y >= -0.0001 && point.y <= CursorSketching.size.height + 0.0001)
        }
        #expect(CursorSketching.point(at: 0).x == 0)
        #expect(CursorSketching.point(at: 1).x == CursorSketching.size.width)
    }

    // MARK: Thinking dots

    @Test func theDotsLightOneAfterAnother() {
        // Each dot is brightest half a glow after its turn starts.
        for dot in 0..<CursorThinkingDots.count {
            let peak = Double(dot) * CursorThinkingDots.step + CursorThinkingDots.glow / 2
            #expect(abs(CursorThinkingDots.brightness(dot: dot, time: peak) - 1) < 0.0001)
            #expect(abs(CursorThinkingDots.opacity(dot: dot, time: peak) - 1) < 0.0001)
        }
        // When the first is brightest the last has not started.
        #expect(CursorThinkingDots.brightness(dot: 2, time: CursorThinkingDots.glow / 2) < 0.2)
    }

    @Test func theDotsRepeatAndNeverGoOut() {
        for dot in 0..<CursorThinkingDots.count {
            for step in 0..<60 {
                let time = Double(step) * 0.037
                let now = CursorThinkingDots.opacity(dot: dot, time: time)
                let nextRound = CursorThinkingDots.opacity(dot: dot, time: time + CursorThinkingDots.period)
                #expect(abs(now - nextRound) < 0.0001)
                #expect(now >= CursorThinkingDots.restingOpacity && now <= 1)
            }
        }
        // There is a rest between rounds: all three dim at once.
        let rest = CursorThinkingDots.period - 0.05
        #expect((0..<3).allSatisfy { CursorThinkingDots.brightness(dot: $0, time: rest) == 0 })
    }

    // MARK: Idle bob

    @Test func thePointerBobsOnlyForAWhileAfterTheMouseStops() {
        #expect(!CursorIdleBob.isBobbing(secondsSinceMouseMoved: 0))
        #expect(!CursorIdleBob.isBobbing(secondsSinceMouseMoved: 0.5))
        #expect(CursorIdleBob.isBobbing(secondsSinceMouseMoved: 0.6))
        #expect(CursorIdleBob.isBobbing(secondsSinceMouseMoved: 5))
        #expect(CursorIdleBob.lastsFor == 8)
        #expect(!CursorIdleBob.isBobbing(secondsSinceMouseMoved: 8.7))
        #expect(!CursorIdleBob.isBobbing(secondsSinceMouseMoved: 600))
    }

    @Test func theRestClockCountsFromTheLastMove() {
        let clock = MouseRestClock()
        let here = CGPoint(x: 10, y: 10)
        #expect(!clock.note(here, at: 100))
        #expect(clock.note(here, at: 101))
        // A move starts the count again.
        #expect(!clock.note(CGPoint(x: 11, y: 10), at: 101.1))
        #expect(clock.note(CGPoint(x: 11, y: 10), at: 102))
        // Long still: the bob has ended.
        #expect(!clock.note(CGPoint(x: 11, y: 10), at: 120))
        // Coming back from a flight starts it again without a move.
        clock.restart(at: 120)
        #expect(clock.note(CGPoint(x: 11, y: 10), at: 121))
    }

    // MARK: Tokens

    @Test func thePointerSpringsAreTheOnesTheOverlayUses() {
        #expect(Motion.pointerFollow == Motion.SpringSpec(response: 0.26, damping: 0.72))
        #expect(Motion.pointerScale == Motion.SpringSpec(response: 0.34, damping: 0.62))
        #expect(Motion.pointerArrive == Motion.SpringSpec(response: 0.42, damping: 0.78))
        #expect(Motion.haloBeat == 0.9)
        #expect(Motion.bobBeat == 0.8)
    }
}
