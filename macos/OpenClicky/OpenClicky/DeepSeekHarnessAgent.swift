//
//  DeepSeekHarnessAgent.swift
//  OpenClicky
//
//  Real work (edit a file, run a command) goes to DeepSeek Harness (`dsh --profile headless`)
//  with the DeepSeek key from shell.json. A question stays on the ask lane. The harness home
//  is OpenClicky's own, so a personal `~/.dsh` is left alone.
//

import Foundation

enum DeepSeekHarnessLane {
    /// "agent" when the words ask for work a harness can do. A question stays "ask".
    static func lane(for transcript: String) -> String {
        let text = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return "ask" }
        if chineseWorkMarkers.contains(where: { text.contains($0) }) { return "agent" }
        if englishWork.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil { return "agent" }
        return "ask"
    }

    private static let chineseWorkMarkers = [
        "创建", "新建", "写一个", "写一份", "改一下", "修改", "修好", "修一下",
        "运行", "执行", "安装", "删除", "删掉", "提交", "重构", "重命名", "跑一下", "跑测试",
    ]

    private static let englishWork = try! NSRegularExpression(
        pattern: #"\b(create|write|edit|fix|delete|remove|refactor|rename|commit|deploy|install|build|make a|make the)\b"#,
        options: [.caseInsensitive])
}

/// One `--json` line from `dsh --profile headless`.
enum DeepSeekHarnessEvent {
    /// Applies one JSON line to `result`. The returned string is a short milestone, when the line has one.
    static func apply(_ line: String, to result: inout OpenClickyAgentRunResult) -> String? {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else { return nil }
        switch type {
        case "session":
            result.threadId = object["sessionId"] as? String
            return nil
        case "tool_call":
            let tool = object["tool"] as? String ?? "a tool"
            return "using \(tool)"
        case "final":
            result.text = object["text"] as? String ?? ""
            result.status = "completed"
            return nil
        default:
            return nil
        }
    }
}

@MainActor
final class DeepSeekHarnessAgent {
    /// A saved DeepSeek key and `dsh` on the path the app gives command-line tools.
    static var isAvailable: Bool {
        guard Self.apiKey != nil else { return false }
        return OpenClickyAgentError.toolResolves("dsh", path: OpenClickyConfiguration.cliProcessEnvironment["PATH"])
    }

    private var runningProcess: Process?

    func cancel() {
        guard let process = runningProcess, process.isRunning else { return }
        process.interrupt()
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            if process.isRunning { process.terminate() }
        }
    }

    /// One headless run. `sessionId` resumes that Session when the harness still has it.
    func run(
        task: String,
        sessionId: String?,
        onEvent: @escaping @MainActor (String) -> Void
    ) async throws -> OpenClickyAgentRunResult {
        guard let apiKey = Self.apiKey else {
            throw OpenClickyAgentError(message: "add a DeepSeek key in Settings, Keys, and the agent can run.")
        }
        let home = Self.harnessHome
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(atPath: OpenClickyConfiguration.workspacePath, withIntermediateDirectories: true)

        var environment = OpenClickyConfiguration.cliProcessEnvironment
        environment["DEEPSEEK_API_KEY"] = apiKey
        environment["DSH_HOME"] = home.path

        var arguments = ["dsh", "--profile", "headless", "--json"]
        if let sessionId, !sessionId.isEmpty { arguments += ["--session-id", sessionId] }
        arguments.append(task)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = arguments
        process.environment = environment
        process.currentDirectoryURL = URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)
        let standardOutputPipe = Pipe()
        let standardErrorPipe = Pipe()
        process.standardOutput = standardOutputPipe
        process.standardError = standardErrorPipe

        let collector = Collector(onEvent: onEvent)
        standardOutputPipe.fileHandleForReading.readabilityHandler = { fileHandle in
            let chunk = String(decoding: fileHandle.availableData, as: UTF8.self)
            guard !chunk.isEmpty else { return }
            Task { @MainActor in collector.consume(chunk) }
        }

        return try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { finished in
                standardOutputPipe.fileHandleForReading.readabilityHandler = nil
                let rest = String(decoding: standardOutputPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                let errorText = String(decoding: standardErrorPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                let status = finished.terminationStatus
                Task { @MainActor in
                    if !rest.isEmpty { collector.consume(rest) }
                    collector.flush()
                    self.runningProcess = nil
                    let result = collector.result
                    if result.text.isEmpty && status != 0 {
                        let message = errorText.isEmpty ? "dsh exited with status \(status)" : String(errorText.prefix(300))
                        continuation.resume(throwing: OpenClickyAgentError(message: message))
                    } else {
                        continuation.resume(returning: result)
                    }
                }
            }
            do {
                try process.run()
                runningProcess = process
            } catch {
                continuation.resume(throwing: OpenClickyAgentError(message: "couldn't start dsh: \(error.localizedDescription)"))
            }
        }
    }

    private static var apiKey: String? {
        let trimmed = (OpenClickyConfiguration.settings.deepseekApiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    /// OpenClicky's harness home. A personal `~/.dsh` is not used.
    static var harnessHome: URL {
        URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
            .appendingPathComponent(".openclicky/dsh-home", isDirectory: true)
    }
}

@MainActor
private final class Collector {
    private(set) var result = OpenClickyAgentRunResult()
    private var buffer = ""
    private let onEvent: @MainActor (String) -> Void

    init(onEvent: @escaping @MainActor (String) -> Void) { self.onEvent = onEvent }

    func consume(_ chunk: String) {
        buffer += chunk
        while let newline = buffer.firstIndex(of: "\n") {
            let line = String(buffer[..<newline])
            buffer = String(buffer[buffer.index(after: newline)...])
            if let milestone = DeepSeekHarnessEvent.apply(line, to: &result) { onEvent(milestone) }
        }
    }

    func flush() {
        let tail = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
        if !tail.isEmpty, let milestone = DeepSeekHarnessEvent.apply(tail, to: &result) { onEvent(milestone) }
        buffer = ""
    }
}
