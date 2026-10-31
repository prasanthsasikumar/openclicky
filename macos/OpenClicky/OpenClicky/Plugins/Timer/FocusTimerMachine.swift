//
//  FocusTimerMachine.swift
//  OpenClicky
//
//  The Timer plugin's arithmetic, with no clock of its own: every call takes `now`, so the store
//  can tick it from a real timer and the tests from fixed dates. A focus cycle runs focus, short
//  break, focus, short break and so on, with a long break in place of every fourth short one.
//

import Foundation

/// A run that can be paused and resumed: the time banked by earlier runs plus the current run.
/// Keeping the start date instead of counting ticks means a late or skipped tick never loses time.
struct RunClock: Equatable {
    private(set) var banked: TimeInterval = 0
    private(set) var runningSince: Date?

    var isRunning: Bool { runningSince != nil }

    func elapsed(at now: Date) -> TimeInterval {
        banked + (runningSince.map { max(0, now.timeIntervalSince($0)) } ?? 0)
    }

    mutating func start(at now: Date) {
        guard runningSince == nil else { return }
        runningSince = now
    }

    mutating func pause(at now: Date) {
        guard runningSince != nil else { return }
        banked = elapsed(at: now)
        runningSince = nil
    }

    mutating func reset() {
        banked = 0
        runningSince = nil
    }

    /// Takes `amount` off the banked time, for a phase that ended part-way through a run: the
    /// overshoot belongs to the next phase.
    mutating func removeElapsed(_ amount: TimeInterval) {
        banked -= amount
    }
}

enum FocusPhase: String, Equatable, CaseIterable {
    case focus
    case shortBreak
    case longBreak

    var title: String {
        switch self {
        case .focus: return "Focus"
        case .shortBreak: return "Break"
        case .longBreak: return "Long break"
        }
    }
}

/// How long each phase lasts; read from Settings by the store on every tick, so a change applies
/// to the phase under way.
struct FocusDurations: Equatable {
    var focus: TimeInterval
    var shortBreak: TimeInterval
    var longBreak: TimeInterval

    func duration(of phase: FocusPhase) -> TimeInterval {
        switch phase {
        case .focus: return focus
        case .shortBreak: return shortBreak
        case .longBreak: return longBreak
        }
    }
}

/// The focus cycle: which phase it is in, how many focus rounds are done, and the phase's clock.
struct FocusCycle: Equatable {
    /// A long break comes after this many focus rounds.
    static let roundsPerLongBreak = 4

    private(set) var phase: FocusPhase = .focus
    /// Focus rounds finished since the last reset.
    private(set) var completedFocusRounds = 0
    private(set) var clock = RunClock()

    var isRunning: Bool { clock.isRunning }
    /// Something is on the clock or a round is done: there is a cycle to reset.
    var hasProgress: Bool { clock.isRunning || clock.banked > 0 || completedFocusRounds > 0 || phase != .focus }

    /// The focus round under way or coming next, 1 to 4.
    var roundInSet: Int { completedFocusRounds % Self.roundsPerLongBreak + 1 }

    func remaining(at now: Date, durations: FocusDurations) -> TimeInterval {
        max(0, durations.duration(of: phase) - clock.elapsed(at: now))
    }

    mutating func start(at now: Date) { clock.start(at: now) }
    mutating func pause(at now: Date) { clock.pause(at: now) }

    /// Back to the first focus round, stopped.
    mutating func reset() {
        phase = .focus
        completedFocusRounds = 0
        clock.reset()
    }

    /// Moves past every phase that has run out by `now` and returns the phases entered, in order
    /// (empty when the current one still has time). The next phase starts running at the moment
    /// the last one ended, so a Mac that slept through a break wakes up in the right place.
    mutating func advance(at now: Date, durations: FocusDurations) -> [FocusPhase] {
        var entered: [FocusPhase] = []
        // A zero-length phase would loop forever; Settings never allows one, but guard anyway.
        let shortest = min(durations.focus, durations.shortBreak, durations.longBreak)
        guard shortest > 0 else { return entered }
        while clock.elapsed(at: now) >= durations.duration(of: phase) {
            clock.removeElapsed(durations.duration(of: phase))
            if phase == .focus {
                completedFocusRounds += 1
                phase = completedFocusRounds % Self.roundsPerLongBreak == 0 ? .longBreak : .shortBreak
            } else {
                phase = .focus
            }
            entered.append(phase)
        }
        return entered
    }
}

/// "12:04", or "1:02:03" past an hour. Whole seconds, rounded the way each reading needs.
enum TimerFormatter {
    static func clock(_ seconds: TimeInterval) -> String {
        let total = max(0, Int(seconds))
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let secs = total % 60
        if hours > 0 { return String(format: "%d:%02d:%02d", hours, minutes, secs) }
        return String(format: "%d:%02d", minutes, secs)
    }

    /// A countdown shows the second it is in, so a fresh 25 minute phase reads 25:00 and the
    /// last second reads 0:01, never 0:00 while time is left.
    static func countdown(_ remaining: TimeInterval) -> String {
        clock(remaining.rounded(.up))
    }
}
