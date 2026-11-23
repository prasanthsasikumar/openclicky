//
//  CursorBuddyMotion.swift
//  OpenClicky
//
//  The pointer's motion as plain maths, so it can be unit-tested and tuned in one place: the arc
//  it flies along, the streak it leaves, how loudness becomes bar height, how the thinking dots
//  take turns, and when it bobs. `OverlayWindow.swift` draws what these return. Coordinates are
//  the overlay's (points, y grows downwards).
//

import CoreGraphics
import Foundation

/// One flight of the pointer from where it is to where it should be: a quadratic arc that lifts
/// off the straight line, takes longer the farther it goes, and lands slightly small.
struct CursorFlightPath: Equatable {
    /// Flight speed: a flight takes its length divided by this.
    static let pointsPerSecond: CGFloat = 800
    /// No flight is shorter than this. Anything under 320 pt would otherwise be over in a few
    /// frames and read as a jump; 0.4 s is still about 24 frames of arc.
    static let shortestDuration = 0.4
    /// No flight is longer than this, however far apart the two points are.
    static let longestDuration = 1.4
    /// The arc's control point is lifted by this share of the distance, up to `highestLift`.
    static let liftShare: CGFloat = 0.2
    static let highestLift: CGFloat = 80
    /// How the pointer leans when it is not flying.
    static let restingRotationDegrees = -35.0
    /// How much bigger the pointer is at the middle of a flight.
    static let midFlightGrowth: CGFloat = 0.3
    /// The size it lands at before it fills out again (`Motion.pointerLanding`).
    static let landingScale: CGFloat = 0.92
    /// The last share of the flight over which it shrinks to the landing size.
    static let landingShare = 0.3

    let start: CGPoint
    let end: CGPoint
    /// The arc's control point: the midpoint, lifted.
    let control: CGPoint
    let duration: Double

    /// Where the pointer is, how it is turned and how big it is at one moment of a flight.
    struct Pose: Equatable {
        var position: CGPoint
        var rotationDegrees: Double
        var scale: CGFloat
    }

    /// `straight` is the Reduce Motion flight: same duration, no lift.
    init(from start: CGPoint, to end: CGPoint, straight: Bool = false) {
        self.start = start
        self.end = end
        let distance = hypot(end.x - start.x, end.y - start.y)
        let lift = straight ? 0 : Self.lift(forDistance: distance)
        control = CGPoint(x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 - lift)
        duration = Self.duration(forDistance: distance)
    }

    var distance: CGFloat { hypot(end.x - start.x, end.y - start.y) }

    static func duration(forDistance distance: CGFloat) -> Double {
        min(max(Double(distance / pointsPerSecond), shortestDuration), longestDuration)
    }

    static func lift(forDistance distance: CGFloat) -> CGFloat {
        min(max(distance, 0) * liftShare, highestLift)
    }

    /// How far through the flight it is after `seconds`, 0 to 1. Taken from the clock, so a busy
    /// main thread drops frames instead of stretching the flight.
    func progress(afterSeconds seconds: Double) -> Double {
        guard duration > 0 else { return 1 }
        return min(1, max(0, seconds / duration))
    }

    /// Slow away, fast in the middle, slow in: 3t² − 2t³.
    static func eased(_ progress: Double) -> Double {
        let t = min(1, max(0, progress))
        return t * t * (3 - 2 * t)
    }

    /// The point on the arc at curve parameter `t` (0 at the start, 1 at the end).
    func point(at t: Double) -> CGPoint {
        let t = CGFloat(min(1, max(0, t)))
        let u = 1 - t
        return CGPoint(
            x: u * u * start.x + 2 * u * t * control.x + t * t * end.x,
            y: u * u * start.y + 2 * u * t * control.y + t * t * end.y
        )
    }

    /// The direction of travel at curve parameter `t` (not normalised).
    func tangent(at t: Double) -> CGVector {
        let t = CGFloat(min(1, max(0, t)))
        let u = 1 - t
        return CGVector(
            dx: 2 * u * (control.x - start.x) + 2 * t * (end.x - control.x),
            dy: 2 * u * (control.y - start.y) + 2 * t * (end.y - control.y)
        )
    }

    /// The pointer at `progress` (time, 0 to 1) of the flight. It turns to face where it is
    /// going, swells towards the middle and comes in at `landingScale`. `calm` is the Reduce
    /// Motion form: it keeps its resting lean and its size.
    func pose(atProgress progress: Double, calm: Bool = false) -> Pose {
        let linear = min(1, max(0, progress))
        let t = Self.eased(linear)
        let position = point(at: t)
        guard !calm else {
            return Pose(position: position, rotationDegrees: Self.restingRotationDegrees, scale: 1)
        }
        let direction = tangent(at: t)
        // The triangle's tip points up when it is not turned, and atan2 is 0 for rightwards.
        let rotation = hypot(direction.dx, direction.dy) < 0.001
            ? Self.restingRotationDegrees
            : Double(atan2(direction.dy, direction.dx)) * 180 / .pi + 90
        return Pose(position: position, rotationDegrees: rotation, scale: Self.scale(atProgress: linear))
    }

    /// 1 at take-off, `1 + midFlightGrowth` near the middle, `landingScale` on touching down.
    static func scale(atProgress progress: Double) -> CGFloat {
        let linear = min(1, max(0, progress))
        let swell = midFlightGrowth * CGFloat(sin(linear * .pi))
        let landing = eased((linear - (1 - landingShare)) / landingShare)
        return 1 + swell - (1 - landingScale) * CGFloat(landing)
    }

    /// The glow around the pointer for a given size: wider while it is big in mid-flight.
    static func glowRadius(forScale scale: CGFloat) -> CGFloat {
        max(4, 8 + (scale - 1) * 20)
    }
}

/// The streak the pointer leaves behind it in flight: the stretch of the arc it has just covered,
/// faint at the old end, gone before it lands.
enum CursorTravelStreak {
    /// How much of the arc (in curve parameter) the streak covers behind the pointer.
    static let tail = 0.22
    /// The streak is drawn as this many pieces, fainter and thinner towards the old end.
    static let pieces = 6
    static let peakOpacity = 0.55
    /// Hops shorter than this leave no streak: there is no room for one.
    static let shortestFlight: CGFloat = 160

    static func isShown(forDistance distance: CGFloat) -> Bool { distance >= shortestFlight }

    /// `pieces + 1` points along the arc from the streak's old end to the pointer.
    static func points(on path: CursorFlightPath, atProgress progress: Double) -> [CGPoint] {
        let head = CursorFlightPath.eased(progress)
        let oldEnd = max(0, head - tail)
        return (0...pieces).map { index in
            path.point(at: oldEnd + (head - oldEnd) * Double(index) / Double(pieces))
        }
    }

    /// The streak comes up quickly after take-off and is gone by the landing.
    static func opacity(atProgress progress: Double) -> Double {
        let p = min(1, max(0, progress))
        let rise = min(1, p / 0.12)
        let fall = min(1, (1 - p) / 0.38)
        return peakOpacity * rise * fall
    }
}

/// The bars shown beside the mouse while Clicky listens.
enum CursorWaveform {
    static let barProfile: [CGFloat] = [0.4, 0.7, 1.0, 0.7, 0.4]
    /// Loudness under this is room noise and moves nothing.
    static let noiseFloor: CGFloat = 0.008
    static let gain: CGFloat = 2.85
    /// Under 1, so quiet speech already moves the bars visibly.
    static let curve: CGFloat = 0.76
    static let restingHeight: CGFloat = 3
    static let reach: CGFloat = 10
    static let idleSway: CGFloat = 1.5

    /// Microphone loudness (0 to 1, mostly small) as bar travel, 0 to 1.
    static func shapedLevel(_ level: CGFloat) -> CGFloat {
        let aboveFloor = max(level - noiseFloor, 0)
        return pow(min(aboveFloor * gain, 1), curve)
    }

    /// The height of one bar. With `sways` the bars also drift a little by themselves so silence
    /// does not look frozen; without it (Reduce Motion) they hold the middle of that drift.
    static func barHeight(bar: Int, level: CGFloat, time: TimeInterval, sways: Bool) -> CGFloat {
        let profile = barProfile[min(max(bar, 0), barProfile.count - 1)]
        let phase = time * 3.6 + Double(bar) * 0.35
        let sway = sways ? CGFloat((sin(phase) + 1) / 2) * idleSway : idleSway / 2
        return restingHeight + shapedLevel(level) * reach * profile + sway
    }
}

/// Three dots that light one after another while Clicky thinks.
enum CursorThinkingDots {
    static let count = 3
    /// One full round of the three dots.
    static let period = 1.2
    /// How long one dot takes to light and dim.
    static let glow = 0.54
    /// The next dot starts this long after the one before.
    static let step = 0.2
    static let restingOpacity = 0.32

    /// How lit a dot is at `time`, 0 (resting) to 1.
    static func brightness(dot: Int, time: TimeInterval) -> Double {
        var local = (time - Double(dot) * step).truncatingRemainder(dividingBy: period)
        if local < 0 { local += period }
        guard local < glow else { return 0 }
        return (1 - cos(2 * .pi * local / glow)) / 2
    }

    static func opacity(dot: Int, time: TimeInterval) -> Double {
        restingOpacity + (1 - restingOpacity) * brightness(dot: dot, time: time)
    }
}

/// The pointer's idle bob: once the mouse has been still a moment it rises and sinks a few times,
/// then rests, so a pointer left alone does not keep the screen redrawing.
enum CursorIdleBob {
    /// How far it rises.
    static let rise: CGFloat = 4
    static let startsAfterStill = 0.6
    static let rounds = 5.0

    static var lastsFor: Double { rounds * 2 * Motion.bobBeat }

    static func isBobbing(secondsSinceMouseMoved seconds: Double) -> Bool {
        seconds >= startsAfterStill && seconds < startsAfterStill + lastsFor
    }
}

/// When the mouse last moved, for the idle bob. A class so the 60 Hz tracking timer can update it
/// without invalidating the overlay's view.
final class MouseRestClock {
    private var lastLocation: CGPoint?
    private var lastMoveTime: TimeInterval = 0

    /// Notes where the mouse is at `time`; true while the pointer should be bobbing.
    func note(_ location: CGPoint, at time: TimeInterval) -> Bool {
        if location != lastLocation {
            lastLocation = location
            lastMoveTime = time
        }
        return CursorIdleBob.isBobbing(secondsSinceMouseMoved: time - lastMoveTime)
    }

    /// Start counting stillness again from `time` (the pointer just came back from a flight).
    func restart(at time: TimeInterval) {
        lastMoveTime = time
    }
}
