//
//  AgentSessionNotifier.swift
//  OpenClicky
//
//  Island notices for coding agents working in a terminal: one when a session finishes, one when
//  it has sat on a single step long enough that it may be waiting for the person. Off unless
//  "Tell me when a session finishes or needs me" is switched on under the Coding agents plugin.
//
//  The second notice is a guess and says so. Claude Code and Codex write nothing to their session
//  logs while a permission prompt is up, so a prompt looks exactly like a command that is simply
//  taking long. The wait before saying anything is therefore long enough that ordinary commands
//  have finished, and the wording never claims to know.
//

import Foundation

@MainActor
final class AgentSessionNotifier {
    static let enabledKey = "openclicky.agents.notices"
    static let settingsTitle = "Tell me when a session finishes or needs me"
    static let settingsDetail = "A notice on the island when Claude Code or Codex finishes in a terminal, or has sat on one step for a while and may be waiting for you. The second is a guess from the session's log."
    /// How long a session sits on one tool request before it may be waiting for the person.
    static let stalledAfterSeconds: TimeInterval = 45
    /// More notices than this from one look become a single one.
    private static let mostNoticesFromOneLook = 2

    private let post: (IslandNotice, (() -> Void)?) -> Void
    private let focusTerminal: (AgentSession) -> Void
    private let now: () -> Date

    /// False until the first look, which only records what is already there: a session that
    /// finished or stalled before the switch went on is not news.
    private var hasBaseline = false
    /// Sessions seen working since they last finished.
    private var workingSessionIDs: Set<String> = []
    private var finishCountBySessionID: [String: Int] = [:]
    private var announcedStallIDs: Set<String> = []
    private var lastFinishedSession: AgentSession?
    /// The session "go to the waiting terminal" goes to: the newest one sitting on a step, else
    /// the one that finished last and has not started again.
    private(set) var newestSessionNeedingAttention: AgentSession?

    init(post: @escaping (IslandNotice, (() -> Void)?) -> Void, focusTerminal: @escaping (AgentSession) -> Void, now: @escaping () -> Date = Date.init) {
        self.post = post
        self.focusTerminal = focusTerminal
        self.now = now
    }

    func reset() {
        hasBaseline = false
        workingSessionIDs = []
        finishCountBySessionID = [:]
        announcedStallIDs = []
        lastFinishedSession = nil
        newestSessionNeedingAttention = nil
    }

    /// Called with every look the watcher takes at the logs, changed or not: a stall is time
    /// passing with nothing changing.
    func observe(_ sessions: [AgentSession]) {
        let currentTime = now()
        var finishedSessions: [(session: AgentSession, noticeID: String)] = []
        var stalledSessions: [(session: AgentSession, noticeID: String)] = []
        var sessionsSittingOnAStep: [AgentSession] = []

        for session in sessions {
            if session.activity == .idle {
                if workingSessionIDs.remove(session.id) != nil {
                    let finishCount = (finishCountBySessionID[session.id] ?? 0) + 1
                    finishCountBySessionID[session.id] = finishCount
                    lastFinishedSession = session
                    finishedSessions.append((session, "agent-done-\(session.id)-\(finishCount)"))
                }
                continue
            }
            workingSessionIDs.insert(session.id)
            if lastFinishedSession?.id == session.id { lastFinishedSession = nil }

            guard case .tool = session.activity, currentTime.timeIntervalSince(session.lastEventAt) >= Self.stalledAfterSeconds else { continue }
            sessionsSittingOnAStep.append(session)
            // One notice per step: the step is told apart by when it was written.
            let stallID = "agent-waiting-\(session.id)-\(Int(session.lastEventAt.timeIntervalSince1970))"
            if announcedStallIDs.insert(stallID).inserted {
                stalledSessions.append((session, stallID))
            }
        }

        newestSessionNeedingAttention = sessionsSittingOnAStep.max { $0.lastEventAt < $1.lastEventAt } ?? lastFinishedSession

        guard hasBaseline else {
            hasBaseline = true
            return
        }
        announce(finishedSessions: finishedSessions, stalledSessions: stalledSessions)
    }

    private func announce(finishedSessions: [(session: AgentSession, noticeID: String)], stalledSessions: [(session: AgentSession, noticeID: String)]) {
        let noticeCount = finishedSessions.count + stalledSessions.count
        guard noticeCount > 0 else { return }

        if noticeCount > Self.mostNoticesFromOneLook {
            let everySession = finishedSessions.map(\.session) + stalledSessions.map(\.session)
            var projectNames: [String] = []
            for session in everySession where !projectNames.contains(session.project) { projectNames.append(session.project) }
            let title = stalledSessions.isEmpty ? "\(noticeCount) sessions finished" : "\(noticeCount) sessions finished or may be waiting"
            let burstID = "agent-burst-" + (finishedSessions + stalledSessions).map(\.noticeID).joined(separator: "|")
            post(IslandNotice(id: burstID, tone: stalledSessions.isEmpty ? .done : .plain, title: title,
                              detail: projectNames.joined(separator: ", "), actionTitle: nil, seconds: 12), nil)
            return
        }

        for finished in finishedSessions {
            let session = finished.session
            post(IslandNotice(id: finished.noticeID, tone: .done, title: "\(Self.noticeName(for: session.agent)) finished",
                              detail: session.project, actionTitle: "Go to it", seconds: 10)) { [focusTerminal] in
                focusTerminal(session)
            }
        }
        for stalled in stalledSessions {
            let session = stalled.session
            post(IslandNotice(id: stalled.noticeID, tone: .plain, title: "\(Self.noticeName(for: session.agent)) may be waiting for you",
                              detail: "\(session.project) · \(session.activity.line)", actionTitle: "Go to it", seconds: 14)) { [focusTerminal] in
                focusTerminal(session)
            }
        }
    }

    static func noticeName(for agent: AgentKind) -> String {
        switch agent {
        case .claudeCode: return "Claude Code"
        case .codex: return "Codex"
        }
    }
}
