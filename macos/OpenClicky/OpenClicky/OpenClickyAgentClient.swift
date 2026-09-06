//
//  OpenClickyAgentClient.swift
//  OpenClicky
//
//  Runs the `openclicky` CLI as a subprocess and turns its `--events` JSON Lines
//  output into a result the companion can speak. This is the shell side of
//  HeyClicky's two-tier routing: the gate decides "ask" (the teacher lane below)
//  versus "agent" (a Codex thread that does real work).
//

import Foundation

struct OpenClickyAgentRunResult {
    var lane: String?
    var text: String = ""
    var artifacts: [String] = []
    var threadId: String?
    var status: String = "unknown"
    var errorMessage: String?
}

/// One Codex thread as reported by `openclicky threads list --json`.
struct OpenClickyThreadSummary: Decodable, Identifiable, Equatable {
    let id: String
    let preview: String
    let cwd: String
    let createdAt: Double
    let updatedAt: Double
    let status: String
    let modelProvider: String

    var updatedDate: Date { Date(timeIntervalSince1970: updatedAt) }
}

struct OpenClickyThreadTurn: Decodable, Identifiable {
    let id: String
    let status: String
    let startedAt: Double?
    let completedAt: Double?
    let user: [String]
    let agent: [String]
    let commands: [String]
}

/// `openclicky threads show <id> --json`.
struct OpenClickyThreadDetail: Decodable {
    let thread: OpenClickyThreadSummary
    let turns: [OpenClickyThreadTurn]

    /// The last thing the agent said on this thread, for cards and the result panel.
    var lastAgentMessage: String? {
        turns.reversed().lazy.compactMap { $0.agent.last }.first
    }
}

struct OpenClickyAgentError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@MainActor
final class OpenClickyAgentClient {
    private var runningProcess: Process?

    /// Ask the gate which lane this request belongs to. Falls back to "agent" only if the CLI itself
    /// cannot run; the CLI already falls back to a local heuristic when the gate model is unavailable.
    func classifyLane(for transcript: String) async -> String {
        do {
            let result = try await execute(arguments: ["do", transcript, "--gate-only", "--events"], onEvent: { _ in })
            return result.lane ?? "agent"
        } catch {
            print("⚠️ OpenClicky gate failed (\(error.localizedDescription)); defaulting to the teacher lane")
            return "ask"
        }
    }

    /// Full agent run on a (possibly resumed) Codex thread. The screenshot is attached as image context.
    func runAgent(
        task: String,
        screenshotPath: String?,
        threadId: String?,
        onEvent: @escaping @MainActor (String) -> Void
    ) async throws -> OpenClickyAgentRunResult {
        var arguments = ["run", task, "--events", "--cwd", OpenClickyConfiguration.workspacePath]
        if let screenshotPath { arguments += ["--image", screenshotPath] }
        if let threadId { arguments += ["--thread", threadId] }
        if let model = OpenClickyConfiguration.agentModelOverride { arguments += ["--model", model] }
        return try await execute(arguments: arguments, onEvent: onEvent)
    }

    /// Recent Codex threads (newest first) for the Agents tab.
    func listThreads(limit: Int = 30) async throws -> [OpenClickyThreadSummary] {
        let data = try await captureStandardOutput(arguments: ["threads", "list", "--limit", String(limit), "--json"])
        return try JSONDecoder().decode([OpenClickyThreadSummary].self, from: data)
    }

    /// Full turn history of one thread.
    func readThread(_ threadId: String) async throws -> OpenClickyThreadDetail {
        let data = try await captureStandardOutput(arguments: ["threads", "show", threadId, "--json"])
        return try JSONDecoder().decode(OpenClickyThreadDetail.self, from: data)
    }

    /// Whether the agent's Composio MCP server is configured and Codex is logged into it.
    func integrationsStatus() async throws -> OpenClickyIntegrationsStatus {
        let data = try await captureStandardOutput(arguments: ["integrations", "status", "--json"])
        return try JSONDecoder().decode(OpenClickyIntegrationsStatus.self, from: data)
    }

    /// Logs Codex into the Composio MCP server: opens Composio's authorization page in the browser and
    /// returns once the user has finished there (or fails after the CLI's own timeout).
    func loginIntegration(_ server: String = "composio") async throws {
        _ = try await captureStandardOutput(arguments: ["integrations", "login", server])
    }

    /// Runs a CLI command that prints a single JSON document and returns its stdout.
    private func captureStandardOutput(arguments: [String]) async throws -> Data {
        let cliCommand = OpenClickyConfiguration.cliCommand
        guard let executableName = cliCommand.first, !executableName.isEmpty else {
            throw OpenClickyAgentError(message: "cliCommand is empty in \(OpenClickyConfiguration.settingsFileURL.path)")
        }
        let environment = OpenClickyConfiguration.cliProcessEnvironment
        let workingDirectory = OpenClickyConfiguration.workspacePath
        let fullArguments = [executableName] + Array(cliCommand.dropFirst()) + arguments
        try? FileManager.default.createDirectory(atPath: workingDirectory, withIntermediateDirectories: true)
        return try await Task.detached(priority: .userInitiated) {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = fullArguments
            process.environment = environment
            process.currentDirectoryURL = URL(fileURLWithPath: workingDirectory)
            let standardOutputPipe = Pipe()
            let standardErrorPipe = Pipe()
            process.standardOutput = standardOutputPipe
            process.standardError = standardErrorPipe
            try process.run()
            let outputData = standardOutputPipe.fileHandleForReading.readDataToEndOfFile()
            let errorData = standardErrorPipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else {
                let errorText = String(decoding: errorData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                throw OpenClickyAgentError(message: errorText.isEmpty ? "openclicky exited with status \(process.terminationStatus)" : errorText)
            }
            return outputData
        }.value
    }

    /// Interrupt the current run (the user started talking again).
    func cancel() {
        guard let process = runningProcess, process.isRunning else { return }
        process.interrupt()
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            if process.isRunning { process.terminate() }
        }
    }

    // MARK: - Process plumbing

    private func execute(
        arguments: [String],
        onEvent: @escaping @MainActor (String) -> Void
    ) async throws -> OpenClickyAgentRunResult {
        let cliCommand = OpenClickyConfiguration.cliCommand
        guard let executableName = cliCommand.first, !executableName.isEmpty else {
            throw OpenClickyAgentError(message: "cliCommand is empty in \(OpenClickyConfiguration.settingsFileURL.path)")
        }
        try? FileManager.default.createDirectory(atPath: OpenClickyConfiguration.workspacePath, withIntermediateDirectories: true)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = [executableName] + Array(cliCommand.dropFirst()) + arguments
        process.environment = OpenClickyConfiguration.cliProcessEnvironment
        process.currentDirectoryURL = URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)

        let standardOutputPipe = Pipe()
        let standardErrorPipe = Pipe()
        process.standardOutput = standardOutputPipe
        process.standardError = standardErrorPipe

        let collector = EventCollector(onEvent: onEvent)

        standardOutputPipe.fileHandleForReading.readabilityHandler = { fileHandle in
            let chunk = String(decoding: fileHandle.availableData, as: UTF8.self)
            guard !chunk.isEmpty else { return }
            Task { @MainActor in collector.consumeStdout(chunk) }
        }
        standardErrorPipe.fileHandleForReading.readabilityHandler = { fileHandle in
            let chunk = String(decoding: fileHandle.availableData, as: UTF8.self)
            guard !chunk.isEmpty else { return }
            Task { @MainActor in collector.consumeStderr(chunk) }
        }

        return try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { finishedProcess in
                standardOutputPipe.fileHandleForReading.readabilityHandler = nil
                standardErrorPipe.fileHandleForReading.readabilityHandler = nil
                let remainingStdout = String(decoding: standardOutputPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                let remainingStderr = String(decoding: standardErrorPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                let terminationStatus = finishedProcess.terminationStatus
                Task { @MainActor in
                    if !remainingStdout.isEmpty { collector.consumeStdout(remainingStdout) }
                    if !remainingStderr.isEmpty { collector.consumeStderr(remainingStderr) }
                    collector.flush()
                    self.runningProcess = nil
                    var result = collector.result
                    if terminationStatus != 0 && result.errorMessage == nil {
                        result.errorMessage = collector.lastStderrLine ?? "openclicky exited with status \(terminationStatus)"
                    }
                    if let errorMessage = result.errorMessage, terminationStatus != 0 {
                        continuation.resume(throwing: OpenClickyAgentError(message: errorMessage))
                    } else {
                        continuation.resume(returning: result)
                    }
                }
            }
            do {
                try process.run()
                runningProcess = process
            } catch {
                continuation.resume(throwing: OpenClickyAgentError(message: "failed to launch \(cliCommand.joined(separator: " ")): \(error.localizedDescription)"))
            }
        }
    }
}

/// Parses the CLI's `--events` JSON Lines (lane / event / delta / answer / result / error).
@MainActor
private final class EventCollector {
    private(set) var result = OpenClickyAgentRunResult()
    private(set) var lastStderrLine: String?
    private var stdoutBuffer = ""
    private var streamedText = ""
    private let onEvent: @MainActor (String) -> Void

    init(onEvent: @escaping @MainActor (String) -> Void) {
        self.onEvent = onEvent
    }

    func consumeStdout(_ chunk: String) {
        stdoutBuffer += chunk
        while let newlineIndex = stdoutBuffer.firstIndex(of: "\n") {
            let line = String(stdoutBuffer[..<newlineIndex])
            stdoutBuffer = String(stdoutBuffer[stdoutBuffer.index(after: newlineIndex)...])
            handleLine(line)
        }
    }

    func consumeStderr(_ chunk: String) {
        for line in chunk.split(separator: "\n") {
            let trimmedLine = line.trimmingCharacters(in: .whitespaces)
            guard !trimmedLine.isEmpty else { continue }
            lastStderrLine = trimmedLine
            onEvent(trimmedLine.replacingOccurrences(of: "▸ ", with: ""))
        }
    }

    func flush() {
        if !stdoutBuffer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            handleLine(stdoutBuffer)
            stdoutBuffer = ""
        }
        if result.text.isEmpty { result.text = streamedText }
    }

    private func handleLine(_ line: String) {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else {
            if !line.trimmingCharacters(in: .whitespaces).isEmpty { streamedText += line + "\n" }
            return
        }
        switch type {
        case "lane":
            result.lane = object["lane"] as? String
        case "event":
            if let text = object["line"] as? String { onEvent(text) }
        case "delta":
            streamedText += object["text"] as? String ?? ""
        case "answer":
            result.text = object["text"] as? String ?? streamedText
            result.status = "completed"
        case "result":
            result.threadId = object["threadId"] as? String
            result.artifacts = object["artifacts"] as? [String] ?? []
            result.status = object["status"] as? String ?? "completed"
            result.errorMessage = object["error"] as? String
            let finalMessage = object["finalMessage"] as? String ?? ""
            result.text = finalMessage.isEmpty ? streamedText : finalMessage
        case "error":
            result.errorMessage = object["message"] as? String ?? "unknown error"
        default:
            break
        }
    }
}

/// `openclicky integrations status --json`.
struct OpenClickyIntegrationsStatus: Decodable {
    let configured: Bool
    let loggedIn: Bool
}
