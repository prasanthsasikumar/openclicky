import Foundation
import Combine

/// Runs the `openclicky` CLI as a subprocess and streams its stdout/stderr into the panel.
/// This is the thin bridge HeyClicky's CodexRuntimeBridge plays; the heavy lifting stays in agent/.
@MainActor
final class AgentRunner: ObservableObject {
    @Published var transcript: String = ""
    @Published var isRunning = false
    @Published var lastThreadId: String?
    @Published var screenshotNext = false
    @Published var followThread = true

    let settings: ShellSettings
    /// Called on the main actor after each CLI invocation finishes (status code).
    var onFinished: ((Int32) -> Void)?
    private var process: Process?

    init(settings: ShellSettings) {
        self.settings = settings
        appendSystem("OpenClicky shell ready. Backend \(settings.backendUrl). Workspace \(settings.workspace).")
        if settings.token.isEmpty { appendSystem("No token configured — set \"token\" in ~/.openclicky/shell.json (see README: Auth flow).") }
    }

    func appendSystem(_ line: String) { transcript += "· \(line)\n" }

    /// `openclicky do <text>` — the gate decides ask vs. agent. Resumes the last thread when following.
    func submit(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !isRunning else { return }
        var args = ["do", trimmed, "--cwd", settings.workspace]
        if screenshotNext { args.append("--screenshot"); screenshotNext = false }
        if followThread, let t = lastThreadId { args += ["--thread", t] }
        transcript += "\n> \(trimmed)\n"
        launch(args)
    }

    /// `openclicky voice` — record N seconds, transcribe via the backend, route through the gate.
    func voice() {
        guard !isRunning else { return }
        transcript += "\n🎤 listening \(settings.voiceSeconds)s…\n"
        launch(["voice", "--seconds", String(settings.voiceSeconds), "--cwd", settings.workspace])
    }

    /// `openclicky talk` — always-on Realtime conversation; runs until Stop.
    func talk() {
        guard !isRunning else { return }
        transcript += "\n🎙 talk session (Stop to hang up)\n"
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
            Task { @MainActor in self?.transcript += s }
        }
        err.fileHandleForReading.readabilityHandler = { [weak self] h in
            let s = String(decoding: h.availableData, as: UTF8.self)
            guard !s.isEmpty else { return }
            Task { @MainActor in
                guard let self else { return }
                for line in s.split(separator: "\n") {
                    let l = String(line)
                    if let r = l.range(of: "thread: ") { self.lastThreadId = String(l[r.upperBound...]).trimmingCharacters(in: .whitespaces) }
                    self.transcript += "  \(l)\n"
                }
            }
        }
        p.terminationHandler = { [weak self] proc in
            out.fileHandleForReading.readabilityHandler = nil
            err.fileHandleForReading.readabilityHandler = nil
            Task { @MainActor in
                guard let self else { return }
                self.isRunning = false
                if proc.terminationStatus != 0 { self.appendSystem("exited with status \(proc.terminationStatus)") }
                self.onFinished?(proc.terminationStatus)
            }
        }
        do {
            try p.run()
            process = p
            isRunning = true
        } catch {
            appendSystem("failed to launch \(cmd.joined(separator: " ")): \(error.localizedDescription)")
        }
    }
}
