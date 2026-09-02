import Foundation
import Combine

/// One line of the panel's activity timeline (HeyClicky's notch activity surface, in miniature).
struct ActivityEntry: Identifiable, Equatable {
    enum Kind: Equatable { case user, lane, event, agent, artifact, system, error }
    let id = UUID()
    let kind: Kind
    var text: String
}

/// Runs the `openclicky` CLI as a subprocess and turns its `--events` JSON Lines into a timeline.
/// This is the thin bridge HeyClicky's CodexRuntimeBridge plays; the heavy lifting stays in agent/.
@MainActor
final class AgentRunner: ObservableObject {
    @Published var entries: [ActivityEntry] = []
    @Published var isRunning = false
    @Published var lastThreadId: String?
    @Published var lane: String?
    @Published var status: String = "idle"
    @Published var artifacts: [String] = []
    @Published var screenshotNext = false
    @Published var followThread = true

    let settings: ShellSettings
    /// Called on the main actor after each CLI invocation finishes (status code).
    var onFinished: ((Int32) -> Void)?
    private var process: Process?
    private var stdoutBuffer = ""
    private var streamingAgentText = false

    init(settings: ShellSettings) {
        self.settings = settings
        appendSystem("OpenClicky shell ready. Backend \(settings.backendUrl). Workspace \(settings.workspace).")
        if settings.token.isEmpty { appendSystem("No token configured — set \"token\" in ~/.openclicky/shell.json (see README: Auth flow).") }
    }

    /// Plain-text rendering of the timeline (used by --smoke-run and for copy/paste).
    var transcript: String {
        entries.map { e in
            switch e.kind {
            case .user: return "> \(e.text)"
            case .lane: return "  lane: \(e.text)"
            case .event: return "  ▸ \(e.text)"
            case .agent: return e.text
            case .artifact: return "  📄 \(e.text)"
            case .system: return "· \(e.text)"
            case .error: return "! \(e.text)"
            }
        }.joined(separator: "\n") + "\n"
    }

    func appendSystem(_ line: String) { entries.append(ActivityEntry(kind: .system, text: line)) }

    /// `openclicky do <text>` — the gate decides ask vs. agent. Resumes the last thread when following.
    func submit(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !isRunning else { return }
        var args = ["do", trimmed, "--events", "--cwd", settings.workspace]
        if screenshotNext { args.append("--screenshot"); screenshotNext = false }
        if followThread, let t = lastThreadId { args += ["--thread", t] }
        if !settings.model.isEmpty { args += ["--model", settings.model] }
        entries.append(ActivityEntry(kind: .user, text: trimmed))
        lane = nil
        artifacts = []
        launch(args)
    }

    /// `openclicky voice` — record N seconds, transcribe via the backend, route through the gate.
    func voice() {
        guard !isRunning else { return }
        appendSystem("listening \(settings.voiceSeconds)s…")
        var args = ["voice", "--events", "--seconds", String(settings.voiceSeconds), "--cwd", settings.workspace]
        if !settings.model.isEmpty { args += ["--model", settings.model] }
        launch(args)
    }

    /// `openclicky talk` — always-on Realtime conversation; runs until Stop.
    func talk() {
        guard !isRunning else { return }
        appendSystem("talk session (Stop to hang up)")
        var args = ["talk", "--cwd", settings.workspace]
        if !settings.model.isEmpty { args += ["--model", settings.model] }
        launch(args)
    }

    func cancel() {
        process?.interrupt() // SIGINT so `talk` hangs up cleanly; falls back to terminate below
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            if let p = self?.process, p.isRunning { p.terminate() }
        }
    }

    // MARK: - Process plumbing

    private func launch(_ args: [String]) {
        let p = Process()
        let cmd = settings.cliCommand
        guard let first = cmd.first else { appendSystem("cliCommand is empty in shell.json"); return }
        p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        p.arguments = [first] + Array(cmd.dropFirst()) + args
        p.environment = settings.environment
        p.currentDirectoryURL = URL(fileURLWithPath: settings.workspace)
        try? FileManager.default.createDirectory(atPath: settings.workspace, withIntermediateDirectories: true)

        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let s = String(decoding: h.availableData, as: UTF8.self)
            guard !s.isEmpty else { return }
            Task { @MainActor in self?.consumeStdout(s) }
        }
        err.fileHandleForReading.readabilityHandler = { [weak self] h in
            let s = String(decoding: h.availableData, as: UTF8.self)
            guard !s.isEmpty else { return }
            Task { @MainActor in self?.consumeStderr(s) }
        }
        p.terminationHandler = { [weak self] proc in
            out.fileHandleForReading.readabilityHandler = nil
            err.fileHandleForReading.readabilityHandler = nil
            Task { @MainActor in
                guard let self else { return }
                self.isRunning = false
                self.streamingAgentText = false
                self.status = proc.terminationStatus == 0 ? "done" : "failed (\(proc.terminationStatus))"
                if proc.terminationStatus != 0 { self.entries.append(ActivityEntry(kind: .error, text: "exited with status \(proc.terminationStatus)")) }
                self.onFinished?(proc.terminationStatus)
            }
        }
        do {
            try p.run()
            process = p
            isRunning = true
            status = "starting"
        } catch {
            appendSystem("failed to launch \(cmd.joined(separator: " ")): \(error.localizedDescription)")
        }
    }

    private func consumeStdout(_ chunk: String) {
        stdoutBuffer += chunk
        while let nl = stdoutBuffer.firstIndex(of: "\n") {
            let line = String(stdoutBuffer[..<nl])
            stdoutBuffer = String(stdoutBuffer[stdoutBuffer.index(after: nl)...])
            handleLine(line)
        }
    }

    private func consumeStderr(_ chunk: String) {
        // `talk` and non-events commands write milestones to stderr; keep them as events.
        for line in chunk.split(separator: "\n") {
            let l = String(line).replacingOccurrences(of: "▸ ", with: "")
            if let r = l.range(of: "thread: ") { lastThreadId = String(l[r.upperBound...]).trimmingCharacters(in: .whitespaces) }
            if l.hasPrefix("you: ") || l.hasPrefix("openclicky: ") { entries.append(ActivityEntry(kind: .agent, text: l)) }
            else if !l.isEmpty { entries.append(ActivityEntry(kind: .event, text: l)); status = l }
        }
    }

    /// Handles one stdout line: a JSON event from `--events`, or plain text (e.g. `talk` transcripts).
    private func handleLine(_ line: String) {
        guard let data = line.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = obj["type"] as? String else {
            if !line.isEmpty { entries.append(ActivityEntry(kind: .agent, text: line)) }
            return
        }
        switch type {
        case "lane":
            lane = obj["lane"] as? String
            let reason = obj["reason"] as? String ?? (obj["forced"] as? Bool == true ? "forced" : "")
            entries.append(ActivityEntry(kind: .lane, text: "\(lane ?? "?")\(reason.isEmpty ? "" : " — \(reason)")"))
            status = lane == "agent" ? "spawning agent" : "asking"
        case "event":
            let text = obj["line"] as? String ?? ""
            if let r = text.range(of: "thread ") , text.hasPrefix("started") || text.hasPrefix("resumed") {
                lastThreadId = String(text[r.upperBound...]).split(separator: " ").first.map(String.init)
            }
            entries.append(ActivityEntry(kind: .event, text: text))
            status = text
        case "delta":
            let text = obj["text"] as? String ?? ""
            if streamingAgentText, let last = entries.indices.last, entries[last].kind == .agent {
                entries[last].text += text
            } else {
                entries.append(ActivityEntry(kind: .agent, text: text))
                streamingAgentText = true
            }
            status = "responding"
        case "answer":
            streamingAgentText = false
            if let text = obj["text"] as? String, !(entries.last?.kind == .agent && entries.last?.text == text) {
                entries.append(ActivityEntry(kind: .agent, text: text))
            }
        case "result":
            streamingAgentText = false
            if let t = obj["threadId"] as? String { lastThreadId = t }
            if let list = obj["artifacts"] as? [String] {
                artifacts = list
                for a in list { entries.append(ActivityEntry(kind: .artifact, text: a)) }
            }
            if let final = obj["finalMessage"] as? String, !final.isEmpty, !(entries.contains { $0.kind == .agent && $0.text == final }) {
                entries.append(ActivityEntry(kind: .agent, text: final))
            }
            status = (obj["status"] as? String) ?? "done"
        case "error":
            entries.append(ActivityEntry(kind: .error, text: obj["message"] as? String ?? "error"))
            status = "error"
        default:
            entries.append(ActivityEntry(kind: .event, text: line))
        }
    }
}
