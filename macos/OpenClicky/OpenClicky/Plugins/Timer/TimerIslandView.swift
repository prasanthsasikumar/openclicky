//
//  TimerIslandView.swift
//  OpenClicky
//
//  The Timer component: a focus/break cycle or a stopwatch, the time large, the phase above it,
//  start/pause and reset on the right, the mode switch on top.
//

import SwiftUI

/// A plugin: a focus timer with breaks, and a stopwatch.
struct TimerIslandComponent: IslandComponent {
    let id = IslandPluginRegistry.timerID
    let title = "Timer"
    let symbolName = "timer"
    let preferredHeight: CGFloat = 110
    let isPlugin = true
    let settingsDetail: String? = "Focus in rounds with short breaks and a long one every fourth round, or run a stopwatch. A soft chime marks each change, even with the island closed."
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(TimerIslandView(store: TimerStore.shared))
    }
}

struct TimerIslandView: View {
    @ObservedObject var store: TimerStore

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                IslandSegmentedSwitch(options: TimerMode.allCases, title: \.title, selection: $store.mode)
                Spacer()
                if store.mode == .focus { roundDots }
            }
            HStack(alignment: .bottom) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(phaseLine)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(IslandDash.secondaryText)
                        .lineLimit(1)
                    Text(timeText)
                        .font(.system(size: 28, weight: .semibold, design: .monospaced))
                        .monospacedDigit()
                        .foregroundColor(.white)
                        .lineLimit(1)
                        .accessibilityLabel(timeText)
                }
                Spacer()
                HStack(spacing: 6) {
                    Button(action: store.toggleRunning) {
                        Image(systemName: store.isShownModeRunning ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(IslandIconButtonStyle(size: 14, frameScale: 2.3, isActive: store.isShownModeRunning))
                    .help(store.isShownModeRunning ? "Pause" : "Start")
                    .accessibilityLabel(store.isShownModeRunning ? "Pause" : "Start")
                    Button(action: store.resetShownMode) {
                        Image(systemName: "arrow.counterclockwise")
                    }
                    .buttonStyle(IslandIconButtonStyle(size: 14, frameScale: 2.3))
                    .disabled(!store.shownModeHasProgress)
                    .opacity(store.shownModeHasProgress ? 1 : 0.4)
                    .help("Reset")
                    .accessibilityLabel("Reset")
                }
                .padding(.bottom, 2)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(.top, 4)
        // The tick only runs while a clock does; reading the time once on appear keeps a paused
        // or finished clock honest after the island was closed for a while.
        .onAppear { store.tick() }
    }

    private var timeText: String {
        switch store.mode {
        case .focus: return TimerFormatter.countdown(store.focusRemaining)
        case .stopwatch: return TimerFormatter.clock(store.stopwatchElapsed)
        }
    }

    private var phaseLine: String {
        switch store.mode {
        case .focus:
            let state = store.cycle.isRunning ? "" : (store.cycle.hasProgress ? " · paused" : "")
            return "\(store.cycle.phase.title)\(state)"
        case .stopwatch:
            return store.stopwatch.isRunning ? "Running" : (store.stopwatchElapsed > 0 ? "Paused" : "Ready")
        }
    }

    /// Four dots: the focus rounds done in this set of four, the current one brighter.
    private var roundDots: some View {
        let done = store.cycle.completedFocusRounds % FocusCycle.roundsPerLongBreak
        let current = store.cycle.phase == .focus ? done : -1
        return HStack(spacing: 5) {
            ForEach(0..<FocusCycle.roundsPerLongBreak, id: \.self) { index in
                Circle()
                    .fill(Color.white.opacity(index < done ? 0.85 : (index == current ? 0.45 : 0.15)))
                    .frame(width: 6, height: 6)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Round \(store.cycle.roundInSet) of \(FocusCycle.roundsPerLongBreak)")
    }
}

/// A small two-or-more segment switch on the island: a faint track, the chosen segment lit.
struct IslandSegmentedSwitch<Option: Hashable>: View {
    let options: [Option]
    let title: (Option) -> String
    @Binding var selection: Option

    init(options: [Option], title: KeyPath<Option, String>, selection: Binding<Option>) {
        self.options = options
        self.title = { $0[keyPath: title] }
        self._selection = selection
    }

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.self) { option in
                Button(action: { selection = option }) {
                    Text(title(option))
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.white.opacity(option == selection ? 1 : 0.6))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 3)
                        .background(Capsule().fill(option == selection ? Color.white.opacity(0.2) : Color.clear))
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .accessibilityAddTraits(option == selection ? .isSelected : [])
            }
        }
        .padding(2)
        .background(Capsule().fill(Color.white.opacity(0.08)))
        .animation(.easeOut(duration: 0.15), value: selection)
    }
}
