//
//  TimerStore.swift
//  OpenClicky
//
//  The Timer plugin's one live instance. It lives outside the island's views so a run keeps
//  going (and chimes between phases) after the island closes, and so other parts of the island
//  can show `peekLine` while it runs.
//

import AppKit
import Combine
import Foundation

enum TimerMode: String, CaseIterable, Equatable {
    case focus
    case stopwatch

    var title: String {
        switch self {
        case .focus: return "Focus"
        case .stopwatch: return "Stopwatch"
        }
    }
}

/// The minutes each phase lasts, in UserDefaults (Settings → Tools → Timer).
enum TimerSettings {
    static let focusMinutesKey = "openclicky.timer.focusMinutes"
    static let breakMinutesKey = "openclicky.timer.breakMinutes"
    static let longBreakMinutesKey = "openclicky.timer.longBreakMinutes"

    static let defaultFocusMinutes = 25
    static let defaultBreakMinutes = 5
    static let defaultLongBreakMinutes = 15
    /// What the pickers offer.
    static let minuteChoices = Array(stride(from: 5, through: 60, by: 5))

    static func durations(from defaults: UserDefaults) -> FocusDurations {
        func minutes(_ key: String, _ fallback: Int) -> TimeInterval {
            let stored = defaults.integer(forKey: key)
            // Unset reads as 0; anything outside the pickers' range is treated as unset too.
            return TimeInterval((1...240).contains(stored) ? stored : fallback) * 60
        }
        return FocusDurations(
            focus: minutes(focusMinutesKey, defaultFocusMinutes),
            shortBreak: minutes(breakMinutesKey, defaultBreakMinutes),
            longBreak: minutes(longBreakMinutesKey, defaultLongBreakMinutes)
        )
    }
}

@MainActor
final class TimerStore: ObservableObject {
    static let shared = TimerStore()

    @Published var mode: TimerMode = .focus
    @Published private(set) var cycle = FocusCycle()
    @Published private(set) var stopwatch = RunClock()
    /// The time the views read; moved on by the tick while anything runs.
    @Published private(set) var now: Date

    private let clock: () -> Date
    private let defaults: UserDefaults
    private let chime: (FocusPhase) -> Void
    private var ticker: Timer?

    init(clock: @escaping () -> Date = Date.init, defaults: UserDefaults = .standard, chime: @escaping (FocusPhase) -> Void = TimerStore.playChime(for:)) {
        self.clock = clock
        self.defaults = defaults
        self.chime = chime
        self.now = clock()
    }

    var durations: FocusDurations { TimerSettings.durations(from: defaults) }

    /// Either clock is running.
    var isRunning: Bool { cycle.isRunning || stopwatch.isRunning }

    /// The shown mode's clock is running.
    var isShownModeRunning: Bool { mode == .focus ? cycle.isRunning : stopwatch.isRunning }

    /// The shown mode has something to reset.
    var shownModeHasProgress: Bool { mode == .focus ? cycle.hasProgress : stopwatch.elapsed(at: now) > 0 }

    /// The focus cycle's time left in its phase.
    var focusRemaining: TimeInterval { cycle.remaining(at: now, durations: durations) }
    var stopwatchElapsed: TimeInterval { stopwatch.elapsed(at: now) }

    /// One short line for elsewhere on the island ("Focus 12:04", "Break 3:10", "Stopwatch 4:31"):
    /// the running clock, the focus cycle first when both run; nil when neither runs.
    var peekLine: String? {
        if cycle.isRunning { return "\(cycle.phase.title) \(TimerFormatter.countdown(focusRemaining))" }
        if stopwatch.isRunning { return "\(TimerMode.stopwatch.title) \(TimerFormatter.clock(stopwatchElapsed))" }
        return nil
    }

    /// Starts or pauses the shown mode's clock.
    func toggleRunning() {
        tick()
        let at = clock()
        switch mode {
        case .focus: cycle.isRunning ? cycle.pause(at: at) : cycle.start(at: at)
        case .stopwatch: stopwatch.isRunning ? stopwatch.pause(at: at) : stopwatch.start(at: at)
        }
        followRunning()
    }

    /// Stops the shown mode's clock and puts it back to the start.
    func resetShownMode() {
        switch mode {
        case .focus: cycle.reset()
        case .stopwatch: stopwatch.reset()
        }
        now = clock()
        followRunning()
    }

    /// Reads the clock, moves the focus cycle past any phase that ran out, and chimes once if it
    /// did (several at once only after the Mac slept; one chime is enough).
    func tick() {
        now = clock()
        let entered = cycle.advance(at: now, durations: durations)
        if let last = entered.last { chime(last) }
    }

    /// The tick runs only while a clock does; twice a second keeps the shown second from lagging.
    private func followRunning() {
        if isRunning, ticker == nil {
            let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
                // Scheduled on the main run loop, so the callback already runs on the main actor.
                MainActor.assumeIsolated { self?.tick() }
            }
            // Common modes, so the time keeps moving while a menu is open.
            RunLoop.main.add(timer, forMode: .common)
            ticker = timer
        } else if !isRunning {
            ticker?.invalidate()
            ticker = nil
        }
    }

    /// A short system sound between phases: a brighter one when focus starts, a softer one for a
    /// break.
    nonisolated static func playChime(for phase: FocusPhase) {
        DispatchQueue.main.async {
            NSSound(named: phase == .focus ? "Glass" : "Tink")?.play()
        }
    }
}
