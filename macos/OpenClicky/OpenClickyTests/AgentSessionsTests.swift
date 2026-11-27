//
//  AgentSessionsTests.swift
//  OpenClickyTests
//
//  What a coding agent's own session log says it is doing, when a session counts as live, and
//  the watcher that lists them.
//

import Foundation
import Testing
@testable import OpenClicky

struct AgentSessionsTests {
    func claudeLine(_ object: [String: Any]) -> String {
        String(data: try! JSONSerialization.data(withJSONObject: object), encoding: .utf8)!
    }

    func assistant(_ blocks: [[String: Any]], stopReason: String?, cwd: String = "/Users/me/Code/app") -> String {
        var message: [String: Any] = ["role": "assistant", "content": blocks]
        if let stopReason { message["stop_reason"] = stopReason }
        return claudeLine(["type": "assistant", "cwd": cwd, "message": message])
    }

    @Test func aClaudeToolCallReadsAsWhatItIsDoing() {
        let tail = [
            claudeLine(["type": "user", "cwd": "/Users/me/Code/app", "message": ["role": "user", "content": "fix it"]]),
            assistant([["type": "tool_use", "name": "Edit", "input": ["file_path": "/Users/me/Code/app/Sources/Hero.swift"]]], stopReason: "tool_use"),
            claudeLine(["type": "attachment", "attachment": [:]]),
            claudeLine(["type": "api-request", "requestId": "x"]),
        ].joined(separator: "\n")
        let parse = AgentSessionParser.parseClaudeTail(tail)
        #expect(parse.activity == .tool(name: "Edit", detail: "Hero.swift"))
        #expect(parse.activity.line == "editing Hero.swift")
        #expect(parse.cwd == "/Users/me/Code/app")
        let bash = AgentSessionParser.parseClaudeTail(assistant([["type": "tool_use", "name": "Bash", "input": ["command": "git status --short && git log --oneline -5"]]], stopReason: "tool_use"))
        // The first 40 characters of the command, trailing space trimmed.
        #expect(bash.activity.line == "running: git status --short && git log --oneline")
    }

    @Test func aFinishedClaudeAnswerIsIdleAndAFollowUpPromptIsWaiting() {
        let finished = assistant([["type": "text", "text": "Done, the hero is blue."]], stopReason: "end_turn")
        #expect(AgentSessionParser.parseClaudeTail(finished).activity == .idle)
        let streaming = assistant([["type": "text", "text": "Let me look"]], stopReason: nil)
        #expect(AgentSessionParser.parseClaudeTail(streaming).activity == .writing("Let me look"))
        let thinking = assistant([["type": "thinking", "thinking": "hmm"]], stopReason: "tool_use")
        #expect(AgentSessionParser.parseClaudeTail(thinking).activity == .thinking)
        let followUp = [finished, claudeLine(["type": "user", "message": ["role": "user", "content": "now red"]])].joined(separator: "\n")
        #expect(AgentSessionParser.parseClaudeTail(followUp).activity == .waiting)
        let title = [claudeLine(["type": "ai-title", "aiTitle": "Blue hero"]), finished].joined(separator: "\n")
        #expect(AgentSessionParser.parseClaudeTail(title).title == "Blue hero")
    }

    @Test func noiseWithoutAnAssistantRecordIsWaitingAndBrokenLinesAreSkipped() {
        let tail = "not json at all\n" + claudeLine(["type": "api-request-blob", "cwd": "/x/y"]) + "\n{\"type\": \"progress\""
        let parse = AgentSessionParser.parseClaudeTail(tail)
        #expect(parse.activity == .waiting)
        #expect(parse.cwd == "/x/y")
    }

    @Test func codexRolloutsReadTheSameWay() {
        let meta = claudeLine(["type": "session_meta", "payload": ["cwd": "/Users/me/Code/site", "id": "abc"]])
        let call = claudeLine(["type": "response_item", "payload": ["type": "function_call", "name": "shell", "arguments": "{\"command\":[\"npm\",\"test\"]}"]])
        let parse = AgentSessionParser.parseCodexTail([meta, call].joined(separator: "\n"))
        #expect(parse.cwd == "/Users/me/Code/site")
        #expect(parse.activity.line == "running: npm test")
        let done = claudeLine(["type": "event_msg", "payload": ["type": "task_complete", "last_agent_message": "ok"]])
        #expect(AgentSessionParser.parseCodexTail([meta, call, done].joined(separator: "\n")).activity == .idle)
        let reasoning = claudeLine(["type": "response_item", "payload": ["type": "reasoning", "summary": []]])
        #expect(AgentSessionParser.parseCodexTail(reasoning).activity == .thinking)
        let message = claudeLine(["type": "response_item", "payload": ["type": "message", "role": "assistant", "content": [["type": "output_text", "text": "Here is the plan"]]]])
        #expect(AgentSessionParser.parseCodexTail(message).activity == .writing("Here is the plan"))
        let firstPrompt = claudeLine(["type": "response_item", "payload": ["type": "message", "role": "user", "content": [["type": "input_text", "text": "make the site faster please"]]]])
        #expect(AgentSessionParser.parseCodexTail([meta, firstPrompt, call].joined(separator: "\n")).title == "make the site faster please")
        #expect(AgentSessionParser.isOwnScratchRollout(cwd: FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-plan-answers/abc").path))
        #expect(!AgentSessionParser.isOwnScratchRollout(cwd: "/Users/me/Code/site"))
    }

    @Test func aSessionIsLiveForTwoMinutesUnlessItIsIdle() {
        let now = Date()
        let working = AgentSession(id: "a", agent: .claudeCode, project: "app", title: nil, activity: .thinking, lastEventAt: now.addingTimeInterval(-119))
        #expect(working.isLive(now: now))
        #expect(!AgentSession(id: "b", agent: .claudeCode, project: "app", title: nil, activity: .thinking, lastEventAt: now.addingTimeInterval(-121)).isLive(now: now))
        #expect(!AgentSession(id: "c", agent: .codex, project: "app", title: nil, activity: .idle, lastEventAt: now).isLive(now: now))
    }

    @MainActor @Test func theWatcherListsRecentLogsLiveFirstAndSkipsItsOwnScratchRuns() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agents-tests-\(UUID().uuidString)", isDirectory: true)
        let claude = root.appendingPathComponent("claude/projects/-Users-me-app", isDirectory: true)
        let now = Date()
        // Today's folder: the watcher only reads the day folders its recent window touches.
        let today = Calendar.current.dateComponents([.year, .month, .day], from: now)
        let dayPath = String(format: "codex/sessions/%04d/%02d/%02d", today.year!, today.month!, today.day!)
        let codex = root.appendingPathComponent(dayPath, isDirectory: true)
        try FileManager.default.createDirectory(at: claude, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: codex, withIntermediateDirectories: true)
        let done = claude.appendingPathComponent("done.jsonl")
        try assistant([["type": "text", "text": "done"]], stopReason: "end_turn").write(to: done, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-30)], ofItemAtPath: done.path)
        let stale = claude.appendingPathComponent("stale.jsonl")
        try assistant([["type": "thinking", "thinking": "x"]], stopReason: nil).write(to: stale, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-900)], ofItemAtPath: stale.path)
        let scratch = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-plan-answers/x").path
        try claudeLine(["type": "session_meta", "payload": ["cwd": scratch]]).write(to: codex.appendingPathComponent("rollout-own.jsonl"), atomically: true, encoding: .utf8)
        let site = codex.appendingPathComponent("rollout-site.jsonl")
        try claudeLine(["type": "session_meta", "payload": ["cwd": "/Users/me/site"]]).write(to: site, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-10)], ofItemAtPath: site.path)
        let live = claude.appendingPathComponent("live.jsonl")
        try assistant([["type": "tool_use", "name": "Read", "input": ["file_path": "/Users/me/app/README.md"]]], stopReason: "tool_use").write(to: live, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: now], ofItemAtPath: live.path)
        let watcher = AgentSessionWatcher(roots: AgentLogRoots(claudeProjects: root.appendingPathComponent("claude/projects"), codexSessions: root.appendingPathComponent("codex/sessions")), now: { now })
        watcher.refreshNow()
        #expect(watcher.sessions.map(\.project) == ["app", "site", "app"])
        #expect(watcher.sessions[0].activity.line == "reading README.md")
        #expect(watcher.liveCount == 2)          // the Codex one is waiting on its prompt
        #expect(watcher.peekLine == "claude · reading README.md")
        #expect(!watcher.isPolling)
        watcher.setEnabled(true)
        #expect(watcher.isPolling && watcher.pollInterval == 20)
        watcher.beginWatching()
        #expect(watcher.pollInterval == 3)
        watcher.endWatching()
        #expect(watcher.pollInterval == 20)
        watcher.setEnabled(false)
        #expect(!watcher.isPolling)
    }

    @Test func rowsShowThreeSessionsAndCountTheRest() {
        let now = Date()
        let sessions: [AgentSession] = (0..<5).map { index in
            let agent: AgentKind = index % 2 == 0 ? .claudeCode : .codex
            let activity: AgentActivity = index < 2 ? .thinking : .idle
            let lastEventAt = now.addingTimeInterval(Double(-60 * index))
            return AgentSession(id: "\(index)", agent: agent, project: "p\(index)", title: nil, activity: activity, lastEventAt: lastEventAt)
        }
        let rows = AgentsIslandView.rows(for: sessions, now: now)
        #expect(rows.count == 3)
        #expect(rows[0].agent == "claude" && rows[0].isLive && rows[0].line == "thinking")
        #expect(rows[2].line == "done 2 min ago")
        #expect(AgentsIslandView.overflowText(count: 5) == "+2 more")
        #expect(AgentsIslandView.overflowText(count: 3) == nil)
    }

    @Test func thePeekLineNamesALiveSessionWhenGivenOne() {
        #expect(NotchPeekLine.peekText(status: "pointer follows you", agentLine: "claude · thinking") == "claude · thinking")
        #expect(NotchPeekLine.peekText(status: "pointer follows you", agentLine: nil) == "pointer follows you")
    }

    @MainActor @Test func aSessionGoneQuietLeavesTheLiveSetAndSubagentLogsAreNotSessions() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agents-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("claude/projects/-Users-me-app", isDirectory: true)
        let subagents = folder.appendingPathComponent("abc/subagents", isDirectory: true)
        try FileManager.default.createDirectory(at: subagents, withIntermediateDirectories: true)
        final class Clock { var now = Date() }
        let clock = Clock()
        let live = folder.appendingPathComponent("abc.jsonl")
        try assistant([["type": "tool_use", "name": "Bash", "input": ["command": "npm test"]]], stopReason: "tool_use").write(to: live, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: clock.now], ofItemAtPath: live.path)
        let subagent = subagents.appendingPathComponent("agent-x.jsonl")
        try assistant([["type": "thinking", "thinking": "x"]], stopReason: nil).write(to: subagent, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: clock.now], ofItemAtPath: subagent.path)
        let watcher = AgentSessionWatcher(roots: AgentLogRoots(claudeProjects: root.appendingPathComponent("claude/projects"), codexSessions: root.appendingPathComponent("codex/sessions")), now: { clock.now })
        watcher.refreshNow()
        #expect(watcher.sessions.count == 1)
        #expect(watcher.liveIDs.map { URL(fileURLWithPath: $0).lastPathComponent } == ["abc.jsonl"])   // the temp folder resolves to /private/var
        clock.now = clock.now.addingTimeInterval(121)
        watcher.refreshNow()
        #expect(watcher.sessions.count == 1)       // still listed as recent
        #expect(watcher.liveIDs.isEmpty)           // but published as no longer live
        #expect(watcher.peekLine == nil)
    }

    @Test func openClickysOwnScratchRunsAreNotTheUsersSessions() {
        let scratchFolder = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-plan-answers/abc", isDirectory: true).path
        #expect(AgentSessionParser.isOwnScratchRollout(cwd: scratchFolder))
        #expect(!AgentSessionParser.isOwnScratchRollout(cwd: "/Users/me/Code/app"))
    }

    @MainActor @Test func theWatcherPollsFasterInTheBackgroundWhileNoticesAreOn() {
        let emptyFolder = FileManager.default.temporaryDirectory.appendingPathComponent("AgentSessionsTests-\(UUID().uuidString)", isDirectory: true)
        let watcher = AgentSessionWatcher(roots: AgentLogRoots(claudeProjects: emptyFolder, codexSessions: emptyFolder))
        #expect(watcher.pollInterval == AgentSessionWatcher.backgroundInterval)
        watcher.noticesAreOn = true
        #expect(watcher.pollInterval == AgentSessionWatcher.noticesInterval)
        watcher.beginWatching()
        #expect(watcher.pollInterval == AgentSessionWatcher.watchedInterval)
    }

    @MainActor @Test func everyRefreshIsReportedEvenWhenNothingChanged() {
        let emptyFolder = FileManager.default.temporaryDirectory.appendingPathComponent("AgentSessionsTests-\(UUID().uuidString)", isDirectory: true)
        let watcher = AgentSessionWatcher(roots: AgentLogRoots(claudeProjects: emptyFolder, codexSessions: emptyFolder))
        var refreshCount = 0
        watcher.onRefresh = { _ in refreshCount += 1 }
        watcher.refreshNow()
        watcher.refreshNow()
        #expect(refreshCount == 2)
    }
}
