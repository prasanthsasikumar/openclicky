//
//  AgentSessionNotifierTests.swift
//  OpenClickyTests
//
//  Notices for coding agents in a terminal: one when a session finishes, one when it has sat on
//  a single step long enough that it may be waiting for the person; never a flood.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct AgentSessionNotifierTests {
    private final class Recorder {
        var notices: [IslandNotice] = []
        var actions: [(() -> Void)?] = []
        var focusedSessionIDs: [String] = []
        var currentTime = Date(timeIntervalSince1970: 1_800_000_000)
    }

    private let recorder = Recorder()

    private func makeNotifier() -> AgentSessionNotifier {
        AgentSessionNotifier(
            post: { [recorder] notice, action in
                recorder.notices.append(notice)
                recorder.actions.append(action)
            },
            focusTerminal: { [recorder] session in recorder.focusedSessionIDs.append(session.id) },
            now: { [recorder] in recorder.currentTime })
    }

    private func session(_ id: String, agent: AgentKind = .claudeCode, project: String = "openclicky",
                         activity: AgentActivity, secondsSinceLastEvent: TimeInterval = 1) -> AgentSession {
        AgentSession(id: id, agent: agent, project: project, title: nil, activity: activity,
                     lastEventAt: recorder.currentTime.addingTimeInterval(-secondsSinceLastEvent))
    }

    private let runningACommand = AgentActivity.tool(name: "Bash", detail: "npm test")

    @Test func theFirstLookIsABaseline() {
        let notifier = makeNotifier()
        notifier.observe([
            session("finished-before", activity: .idle),
            session("stuck-before", activity: runningACommand, secondsSinceLastEvent: 300),
        ])
        recorder.currentTime = recorder.currentTime.addingTimeInterval(5)
        notifier.observe([
            session("finished-before", activity: .idle),
            session("stuck-before", activity: runningACommand, secondsSinceLastEvent: 305),
        ])
        #expect(recorder.notices.isEmpty)
    }

    @Test func finishingPostsOnceWithTheProject() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: .idle)])
        notifier.observe([session("a", activity: .idle)])
        #expect(recorder.notices.count == 1)
        #expect(recorder.notices.first?.title == "Claude Code finished")
        #expect(recorder.notices.first?.detail == "openclicky")
        #expect(recorder.notices.first?.tone == .done)
        #expect(recorder.notices.first?.actionTitle == "Go to it")

        recorder.actions.first??()
        #expect(recorder.focusedSessionIDs == ["a"])
    }

    @Test func aSessionFirstSeenAlreadyFinishedSaysNothing() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: .thinking), session("old", activity: .idle)])
        #expect(recorder.notices.isEmpty)
    }

    @Test func aSecondTurnInTheSameSessionIsAnnouncedAgain() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: .idle)])
        notifier.observe([session("a", activity: .writing("ok"))])
        notifier.observe([session("a", activity: .idle)])
        #expect(recorder.notices.count == 2)
        #expect(Set(recorder.notices.map(\.id)).count == 2)
    }

    @Test func aStepThatSitsPostsOnlyAfterTheWait() {
        let notifier = makeNotifier()
        notifier.observe([session("a", agent: .codex, activity: .thinking)])
        notifier.observe([session("a", agent: .codex, activity: runningACommand, secondsSinceLastEvent: AgentSessionNotifier.stalledAfterSeconds - 1)])
        #expect(recorder.notices.isEmpty)

        recorder.currentTime = recorder.currentTime.addingTimeInterval(1)
        notifier.observe([session("a", agent: .codex, activity: runningACommand, secondsSinceLastEvent: AgentSessionNotifier.stalledAfterSeconds)])
        #expect(recorder.notices.count == 1)
        #expect(recorder.notices.first?.title == "Codex may be waiting for you")
        #expect(recorder.notices.first?.detail == "openclicky · running: npm test")
        #expect(notifier.newestSessionNeedingAttention?.id == "a")

        // The same step, twenty seconds on.
        recorder.currentTime = recorder.currentTime.addingTimeInterval(20)
        notifier.observe([session("a", agent: .codex, activity: runningACommand, secondsSinceLastEvent: AgentSessionNotifier.stalledAfterSeconds + 20)])
        #expect(recorder.notices.count == 1)
    }

    @Test func thinkingOrWritingForLongIsNotAWait() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: runningACommand)])
        notifier.observe([session("a", activity: .thinking, secondsSinceLastEvent: 100)])
        notifier.observe([session("a", activity: .waiting, secondsSinceLastEvent: 100)])
        #expect(recorder.notices.isEmpty)
    }

    @Test func aNewWaitInTheSameSessionPostsAgain() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: runningACommand, secondsSinceLastEvent: 60)])
        recorder.currentTime = recorder.currentTime.addingTimeInterval(200)
        notifier.observe([session("a", activity: .tool(name: "Edit", detail: "Hero.swift"), secondsSinceLastEvent: 50)])
        #expect(recorder.notices.count == 2)
    }

    @Test func aSessionThatMovesOnNoLongerNeedsAttention() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: runningACommand, secondsSinceLastEvent: 60)])
        #expect(notifier.newestSessionNeedingAttention?.id == "a")
        notifier.observe([session("a", activity: .thinking)])
        #expect(notifier.newestSessionNeedingAttention == nil)
    }

    @Test func aSessionThatFinishedLongAgoIsNotWhereTheButtonGoes() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.observe([session("a", activity: .idle)])
        #expect(notifier.newestSessionNeedingAttention?.id == "a")
        recorder.currentTime = recorder.currentTime.addingTimeInterval(AgentSession.recentWindow + 1)
        notifier.observe([])
        #expect(notifier.newestSessionNeedingAttention == nil)
    }

    @Test func aBurstBecomesOneNotice() {
        let notifier = makeNotifier()
        notifier.observe([
            session("a", project: "alpha", activity: .thinking), session("b", project: "beta", activity: .thinking),
            session("c", project: "gamma", activity: .thinking),
        ])
        notifier.observe([
            session("a", project: "alpha", activity: .idle), session("b", project: "beta", activity: .idle),
            session("c", project: "gamma", activity: .idle),
        ])
        #expect(recorder.notices.count == 1)
        #expect(recorder.notices.first?.title == "3 sessions finished")
        #expect(recorder.notices.first?.detail == "alpha, beta, gamma")
        #expect(recorder.notices.first?.actionTitle == nil)
        // The burst is not announced a second time, one by one.
        notifier.observe([
            session("a", project: "alpha", activity: .idle), session("b", project: "beta", activity: .idle),
            session("c", project: "gamma", activity: .idle),
        ])
        #expect(recorder.notices.count == 1)
    }

    @Test func resetMakesTheNextLookABaselineAgain() {
        let notifier = makeNotifier()
        notifier.observe([session("a", activity: .thinking)])
        notifier.reset()
        notifier.observe([session("a", activity: .idle)])
        #expect(recorder.notices.isEmpty)
        #expect(notifier.newestSessionNeedingAttention == nil)
    }

    // MARK: finding the terminal

    private func window(_ ownerName: String, pid: pid_t, _ title: String, bundleID: String) -> TerminalWindowFinder.WindowCandidate {
        TerminalWindowFinder.WindowCandidate(ownerBundleIdentifier: bundleID, ownerPID: pid, title: title)
    }

    @Test func theTerminalWindowNamingTheProjectWins() {
        let windows = [
            window("Terminal", pid: 10, "notes — zsh — 80×24", bundleID: "com.apple.Terminal"),
            window("iTerm2", pid: 20, "✳ openclicky — claude", bundleID: "com.googlecode.iterm2"),
        ]
        #expect(TerminalWindowFinder.bestWindow(forProject: "openclicky", among: windows)?.ownerPID == 20)
    }

    @Test func noTerminalNamingTheProjectIsNoWindow() {
        let windows = [window("Terminal", pid: 10, "notes — zsh", bundleID: "com.apple.Terminal")]
        #expect(TerminalWindowFinder.bestWindow(forProject: "openclicky", among: windows) == nil)
    }

    @Test func aBrowserTabNamingTheProjectIsIgnored() {
        let windows = [window("Safari", pid: 30, "openclicky · GitHub", bundleID: "com.apple.Safari")]
        #expect(TerminalWindowFinder.bestWindow(forProject: "openclicky", among: windows) == nil)
    }

    @Test func aProjectNameInsideALongerWordDoesNotMatch() {
        let windows = [window("Terminal", pid: 10, "openclicky-site — zsh", bundleID: "com.apple.Terminal")]
        #expect(TerminalWindowFinder.bestWindow(forProject: "open", among: windows) == nil)
    }
}
