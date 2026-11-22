//
//  IslandNotices.swift
//  OpenClicky
//
//  One-off cards on the island: something happened that is worth a line and at most one button
//  (an agent's job finished, with "Open"). They queue, show one at a time, and go away by
//  themselves on a countdown ring that holds while the pointer is on the card; the ring is also
//  the button that dismisses the card now. A card yields to the busy strip and comes back.
//

import AppKit
import Combine
import SwiftUI

struct IslandNotice: Equatable, Identifiable {
    enum Tone: Equatable { case done, trouble, plain }

    var id: String
    var tone: Tone = .plain
    var title: String
    var detail: String?
    var actionTitle: String?
    /// How long the card stays when nobody touches it.
    var seconds: Double = 8
}

/// The queue as plain data: what shows, how much of its time is left, what is next.
struct IslandNoticeQueue: Equatable {
    private(set) var waiting: [IslandNotice] = []
    /// Seconds left for the first notice.
    private(set) var remaining: Double = 0
    static let maximumWaiting = 6

    var current: IslandNotice? { waiting.first }
    /// 1 when the current notice has all its time, 0 when it is out of it.
    var fraction: Double {
        guard let current, current.seconds > 0 else { return 0 }
        return min(1, max(0, remaining / current.seconds))
    }

    /// Adds a notice; one with an id already waiting replaces it in place (and, when it is the
    /// one showing, starts its time again). The oldest waiting notice is dropped past the limit.
    mutating func enqueue(_ notice: IslandNotice) {
        if let index = waiting.firstIndex(where: { $0.id == notice.id }) {
            waiting[index] = notice
            if index == 0 { remaining = notice.seconds }
            return
        }
        waiting.append(notice)
        if waiting.count == 1 { remaining = notice.seconds }
        if waiting.count > Self.maximumWaiting { waiting.remove(at: 1) }
    }

    /// Time passes for the notice showing, unless it is held. Returns true when the card changed.
    @discardableResult
    mutating func advance(by seconds: Double, isHeld: Bool) -> Bool {
        guard current != nil, !isHeld else { return false }
        remaining -= seconds
        guard remaining <= 0 else { return false }
        dismissCurrent()
        return true
    }

    mutating func dismissCurrent() {
        guard !waiting.isEmpty else { return }
        waiting.removeFirst()
        remaining = waiting.first?.seconds ?? 0
    }

    mutating func removeAll() {
        waiting = []
        remaining = 0
    }
}

/// What the island draws for a notice: the card and how much of its ring is left.
struct IslandNoticeState: Equatable {
    var notice: IslandNotice
    var fraction: Double

    static let contentHeight: CGFloat = 58
}

@MainActor
final class IslandNoticeCenter {
    private weak var companionManager: CompanionManager?
    private var queue = IslandNoticeQueue()
    private var actions: [String: () -> Void] = [:]
    private var timer: Timer?
    private var subscriptions: Set<AnyCancellable> = []
    static let tick: TimeInterval = 0.1

    func start(companionManager: CompanionManager) {
        self.companionManager = companionManager
        // An agent's job finished: say so with a way to open it, once the spoken result is over.
        companionManager.$lastAgentResult
            .dropFirst()
            .compactMap { $0 }
            .removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] result in self?.postJobFinished(result) }
            .store(in: &subscriptions)
    }

    func stop() {
        subscriptions.removeAll()
        timer?.invalidate()
        timer = nil
        queue.removeAll()
        actions = [:]
        publish()
    }

    func post(_ notice: IslandNotice, action: (() -> Void)? = nil) {
        queue.enqueue(notice)
        actions[notice.id] = action
        publish()
        startTimerIfNeeded()
    }

    /// The card's button: runs its action and closes the card.
    func performAction() {
        guard let current = queue.current else { return }
        let action = actions[current.id]
        dismiss()
        action?()
    }

    /// The ring's click.
    func dismiss() {
        if let current = queue.current { actions[current.id] = nil }
        queue.dismissCurrent()
        publish()
    }

    static func jobFinishedNotice(_ result: OpenClickyAgentResultSummary) -> IslandNotice {
        let finished = result.status == "completed"
        let firstLine = result.text.split(whereSeparator: \.isNewline).first.map(String.init)?.trimmingCharacters(in: .whitespaces)
        var detail = firstLine.flatMap { $0.isEmpty ? nil : $0 } ?? result.title
        if !result.artifacts.isEmpty {
            detail += result.artifacts.count == 1 ? " · 1 file" : " · \(result.artifacts.count) files"
        }
        return IslandNotice(id: "job-\(result.threadId)", tone: finished ? .done : .trouble,
                            title: finished ? "A job is done" : "A job stopped early",
                            detail: detail, actionTitle: result.threadId.isEmpty ? nil : "Open", seconds: finished ? 10 : 14)
    }

    private func postJobFinished(_ result: OpenClickyAgentResultSummary) {
        let threadId = result.threadId
        post(Self.jobFinishedNotice(result)) { [weak self] in
            guard !threadId.isEmpty else { return }
            self?.companionManager?.openAgentResultCard(threadId: threadId)
        }
    }

    private func startTimerIfNeeded() {
        guard timer == nil, queue.current != nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: Self.tick, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tickOnce() }
        }
    }

    private func tickOnce() {
        guard let hud = companionManager?.notchHUDManager else { return }
        // Time runs only while the card is actually up (not behind the busy strip) and not held.
        let isShowing = hud.isShowingNotice
        queue.advance(by: Self.tick, isHeld: !isShowing || hud.isPointerOnPrimaryIsland)
        publish()
        if queue.current == nil {
            timer?.invalidate()
            timer = nil
        }
    }

    private func publish() {
        let state = queue.current.map { IslandNoticeState(notice: $0, fraction: queue.fraction) }
        companionManager?.notchHUDManager.setNotice(state)
    }
}

struct IslandNoticeView: View {
    let state: IslandNoticeState
    let center: IslandNoticeCenter
    var topInset: CGFloat = 0
    var reduceMotion = false

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .bold))
                .foregroundColor(tint)
                .frame(width: 30, height: 30)
                .background(Circle().fill(tint.opacity(0.16)))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(state.notice.title)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundColor(DS.HUD.text)
                    .lineLimit(1)
                if let detail = state.notice.detail {
                    Text(detail)
                        .font(.system(size: 11.5))
                        .foregroundColor(DS.HUD.text2)
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if let actionTitle = state.notice.actionTitle {
                Button(actionTitle) { center.performAction() }
                    .buttonStyle(IslandPillButtonStyle())
            }
            IslandCountdownRing(fraction: state.fraction, action: { center.dismiss() })
        }
        .padding(.horizontal, 18)
        .frame(height: IslandNoticeState.contentHeight)
        .padding(.top, topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .accessibilityElement(children: .contain)
    }

    private var symbol: String {
        switch state.notice.tone {
        case .done: return "checkmark"
        case .trouble: return "exclamationmark"
        case .plain: return "circle.fill"
        }
    }

    private var tint: Color {
        switch state.notice.tone {
        case .done: return DS.HUD.live
        case .trouble: return DS.Colors.warning
        case .plain: return DS.HUD.text2
        }
    }
}

/// The time a card has left, as a ring around a cross: a click dismisses the card now.
struct IslandCountdownRing: View {
    let fraction: Double
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().stroke(Color.white.opacity(0.16), lineWidth: 1.8)
                Circle().trim(from: 0, to: fraction)
                    .stroke(Color.white.opacity(0.8), style: StrokeStyle(lineWidth: 1.8, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.linear(duration: IslandNoticeCenter.tick), value: fraction)
                Image(systemName: "xmark").font(.system(size: 7.5, weight: .bold)).foregroundColor(DS.HUD.text2)
            }
            .frame(width: 20, height: 20)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityLabel("dismiss")
    }
}

/// The ring that fills while the pointer rests on the island, before the panel opens by itself.
struct IslandDwellRing: View {
    let seconds: Double
    let reduceMotion: Bool
    @State private var isFull = false

    var body: some View {
        ZStack {
            Circle().stroke(Color.white.opacity(0.18), lineWidth: 1.6)
            Circle().trim(from: 0, to: isFull ? 1 : 0)
                .stroke(DS.HUD.pointer, style: StrokeStyle(lineWidth: 1.6, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
        .frame(width: 10, height: 10)
        .onAppear {
            if reduceMotion { isFull = true } else { withAnimation(.linear(duration: seconds)) { isFull = true } }
        }
        .accessibilityHidden(true)
    }
}
