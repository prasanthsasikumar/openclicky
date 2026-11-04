//
//  ClaudeCodeAnswerProvider.swift
//  OpenClicky
//
//  Answers on the person's Claude plan through the Claude Code CLI, for one turn, with no tools:
//
//    claude -p --input-format stream-json --output-format stream-json --verbose
//           --include-partial-messages --tools "" --permission-mode dontAsk --permission-prompts none
//           --system-prompt <the request's system prompt> --no-session-persistence --safe-mode
//           --strict-mcp-config --disable-slash-commands [--model <model>]
//
//  and one stream-json user message on stdin whose content is the earlier turns as text, each
//  screenshot as its label plus a base64 image block, and the question. Verified against Claude
//  Code 2.1.296 (2026-10-11): a red square comes back as "red" in about 3.5 s.
//
//  Why these flags:
//    - `--system-prompt` replaces Claude Code's own coding-agent prompt rather than appending to it
//      (`--append-system-prompt`): that prompt is several thousand tokens about repositories and
//      tools, which slows the answer and pulls it away from OpenClicky's voice.
//    - `--tools ""` removes every built-in tool, `--strict-mcp-config` (with no `--mcp-config`)
//      loads no MCP servers, `--safe-mode` skips the person's hooks, plugins and CLAUDE.md, and
//      `dontAsk` + `--permission-prompts none` deny anything that would still ask. The answer can
//      only come from the model.
//    - stream-json input is the one way `-p` accepts images; stream-json output with partial
//      messages gives `text_delta` events, so the answer can be spoken while it arrives.
//    - The request's quick-action tools have no CLI equivalent: they are described in the system
//      prompt and asked for as [ACTION: …] lines (PlanToolProtocol.swift), which are kept out of
//      the streamed text and returned as tool calls. maxTokens has no equivalent and is not sent.
//

import Foundation

nonisolated struct ClaudeCodeAnswerProvider: AnswerProvider {
    let executablePath: String
    /// `--model`: an alias ("sonnet", "opus", "haiku") or a full model name; nil = the CLI's default.
    let model: String?
    let processRunner: PlanCLIProcessRunning
    let timeoutSeconds: TimeInterval

    init(
        executablePath: String,
        model: String? = nil,
        processRunner: PlanCLIProcessRunning = PlanCLIProcessRunner(),
        timeoutSeconds: TimeInterval = 90
    ) {
        self.executablePath = executablePath
        self.model = model
        self.processRunner = processRunner
        self.timeoutSeconds = timeoutSeconds
    }

    func answer(_ request: AnswerRequest, onTextChunk: @escaping @Sendable (String) -> Void) async throws -> AnswerResult {
        let scratchWorkingDirectory: URL
        do {
            scratchWorkingDirectory = try PlanCLIs.makeScratchWorkingDirectory()
        } catch {
            throw AnswerProviderError(source: .claudeCodePlan, status: 0, message: "Couldn't make a scratch folder to run Claude Code in.")
        }
        defer { try? FileManager.default.removeItem(at: scratchWorkingDirectory) }

        let streamParser = ClaudeCodeStreamParser()
        // With quick actions offered, an [ACTION: …] line must never reach the voice.
        let actionLineFilter: PlanActionLineFilter? = request.tools.isEmpty ? nil : PlanActionLineFilter()
        let handOnTextDelta: @Sendable (String) -> Void = { textDelta in
            guard let actionLineFilter else { onTextChunk(textDelta); return }
            let safeText = actionLineFilter.consume(textDelta)
            if !safeText.isEmpty { onTextChunk(safeText) }
        }
        // A drawing (an 8k-token program) asks for longer than an ordinary answer's 90 s.
        let timeoutSeconds = request.timeoutSeconds ?? self.timeoutSeconds
        let output: PlanCLIProcessOutput
        do {
            output = try await processRunner.run(
                executablePath: executablePath,
                arguments: Self.commandLineArguments(
                    model: model,
                    systemPrompt: PlanToolProtocol.systemPrompt(request.systemPrompt, offering: request.tools)
                ),
                standardInput: Self.standardInputMessage(for: request),
                workingDirectory: scratchWorkingDirectory,
                environment: PlanCLIs.processEnvironment(forExecutableAt: executablePath),
                timeoutSeconds: timeoutSeconds,
                onStandardOutputChunk: { chunk in
                    for textDelta in streamParser.consume(chunk) { handOnTextDelta(textDelta) }
                }
            )
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw AnswerProviderError(source: .claudeCodePlan, status: 0, message: "Couldn't start Claude Code at \(executablePath).")
        }
        try Task.checkCancellation()
        for textDelta in streamParser.finish() { handOnTextDelta(textDelta) }
        if let actionLineFilter {
            let heldBackText = actionLineFilter.finish()
            if !heldBackText.isEmpty { onTextChunk(heldBackText) }
        }

        let answerText = try Self.answerText(
            from: streamParser.summary,
            output: output,
            timeoutSeconds: timeoutSeconds
        )
        guard !request.tools.isEmpty else { return AnswerResult(text: answerText, toolCalls: []) }
        let (spokenText, toolCalls) = PlanToolProtocol.parse(answerText: answerText, knownToolNames: Set(request.tools.map(\.name)))
        return AnswerResult(text: spokenText, toolCalls: toolCalls)
    }

    // MARK: building the call

    static func commandLineArguments(model: String?, systemPrompt: String) -> [String] {
        var arguments = [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            // stream-json output requires --verbose in print mode.
            "--verbose",
            "--include-partial-messages",
            "--tools", "",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            "--system-prompt", systemPrompt,
            "--no-session-persistence",
            "--safe-mode",
            "--strict-mcp-config",
            "--disable-slash-commands",
        ]
        if let model = model?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty {
            arguments += ["--model", model]
        }
        return arguments
    }

    /// Earlier turns as plain text, because a one-turn `-p` run has no conversation to resume.
    static func historyText(for history: [AnswerRequest.Turn]) -> String? {
        guard !history.isEmpty else { return nil }
        var lines = ["Earlier in this conversation:"]
        for turn in history {
            lines.append("User: \(turn.user)")
            lines.append("You: \(turn.assistant)")
        }
        return lines.joined(separator: "\n")
    }

    /// The content blocks of the one user message, in the Anthropic Messages shape the CLI passes
    /// through: history text, then each screenshot's label followed by its image, then the question.
    static func userMessageContent(for request: AnswerRequest) -> [[String: Any]] {
        var contentBlocks: [[String: Any]] = []
        if let historyText = historyText(for: request.history) {
            contentBlocks.append(["type": "text", "text": historyText])
        }
        for image in request.images {
            contentBlocks.append(["type": "text", "text": image.label])
            contentBlocks.append([
                "type": "image",
                "source": [
                    "type": "base64",
                    "media_type": image.mediaType,
                    "data": image.data.base64EncodedString(),
                ],
            ])
        }
        contentBlocks.append(["type": "text", "text": request.userPrompt])
        return contentBlocks
    }

    /// One JSON line; the CLI answers it and exits when stdin closes.
    static func standardInputMessage(for request: AnswerRequest) -> Data {
        let message: [String: Any] = [
            "type": "user",
            "message": ["role": "user", "content": userMessageContent(for: request)],
        ]
        // Sorted keys keep the same request byte-for-byte identical, which the tests compare.
        let messageData = (try? JSONSerialization.data(withJSONObject: message, options: [.sortedKeys])) ?? Data()
        return messageData + Data("\n".utf8)
    }

    // MARK: reading the result

    /// The answer, or an error with a sentence the ask lane can say.
    static func answerText(
        from summary: ClaudeCodeStreamSummary,
        output: PlanCLIProcessOutput,
        timeoutSeconds: TimeInterval
    ) throws -> String {
        if output.timedOut {
            throw AnswerProviderError(source: .claudeCodePlan, status: 0, message: "Claude Code took longer than \(Int(timeoutSeconds)) seconds to answer.")
        }
        if let failureText = summary.failureText {
            throw AnswerProviderError(source: .claudeCodePlan, status: 0, message: humanSentence(forFailureText: failureText))
        }
        if output.exitStatus != 0 {
            let reason = PlanCLIs.lastNonEmptyLine(of: output.standardErrorText)
            throw AnswerProviderError(
                source: .claudeCodePlan,
                status: 0,
                message: reason.map { humanSentence(forFailureText: $0) } ?? "Claude Code stopped with exit status \(output.exitStatus)."
            )
        }
        let answerText = (summary.resultText ?? summary.streamedText).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !answerText.isEmpty else {
            throw AnswerProviderError(source: .claudeCodePlan, status: 0, message: "Claude Code finished without an answer.")
        }
        return answerText
    }

    /// The CLI's own failure text is usually fine to show; the sign-in one names a slash command
    /// the person cannot type here, so it gets a sentence that says what to do instead.
    static func humanSentence(forFailureText failureText: String) -> String {
        let trimmedFailureText = failureText.trimmingCharacters(in: .whitespacesAndNewlines)
        let lowercasedFailureText = trimmedFailureText.lowercased()
        if lowercasedFailureText.contains("not logged in") || lowercasedFailureText.contains("/login")
            || lowercasedFailureText.contains("authentication_failed") {
            return "Claude Code is signed out. Open Terminal, run claude, and sign in with your Claude account."
        }
        if lowercasedFailureText.contains("rate_limit") || lowercasedFailureText.contains("usage limit") {
            return "Your Claude plan has hit its usage limit for now."
        }
        return trimmedFailureText.isEmpty ? "Claude Code couldn't answer." : "Claude Code couldn't answer: \(trimmedFailureText)"
    }
}

/// What the stream said once it ended.
nonisolated struct ClaudeCodeStreamSummary: Equatable, Sendable {
    /// Every `text_delta` joined, in order.
    var streamedText = ""
    /// The final `result` event's text, when it succeeded.
    var resultText: String?
    /// Set when the run failed: the result event's text (`is_error`), or an assistant message's
    /// `error` code ("authentication_failed") when no result came.
    var failureText: String?
}

/// Turns `claude -p --output-format stream-json --include-partial-messages` output into text
/// deltas and a summary. Fed from the runner's reader thread, so it locks its own state.
nonisolated final class ClaudeCodeStreamParser: @unchecked Sendable {
    private let lock = NSLock()
    private var bufferedBytes = Data()
    private var state = ClaudeCodeStreamSummary()
    private var sawTextDelta = false
    private var assistantMessageText = ""
    private var assistantErrorCode: String?

    /// Consumes a piece of stdout and returns the text deltas from the complete lines in it.
    func consume(_ chunk: Data) -> [String] {
        lock.lock(); defer { lock.unlock() }
        bufferedBytes.append(chunk)
        var textDeltas: [String] = []
        while let newlineIndex = bufferedBytes.firstIndex(of: UInt8(ascii: "\n")) {
            let lineData = bufferedBytes[bufferedBytes.startIndex..<newlineIndex]
            bufferedBytes = Data(bufferedBytes[bufferedBytes.index(after: newlineIndex)...])
            if let textDelta = handleLine(Data(lineData)) { textDeltas.append(textDelta) }
        }
        return textDeltas
    }

    /// Handles a last line with no newline after it.
    func finish() -> [String] {
        lock.lock(); defer { lock.unlock() }
        var textDeltas: [String] = []
        if !bufferedBytes.isEmpty, let textDelta = handleLine(bufferedBytes) { textDeltas.append(textDelta) }
        bufferedBytes = Data()
        return textDeltas
    }

    var summary: ClaudeCodeStreamSummary {
        lock.lock(); defer { lock.unlock() }
        var finishedSummary = state
        // Without partial messages (or if they were dropped) the whole assistant message is the
        // only copy of the text.
        if !sawTextDelta && finishedSummary.streamedText.isEmpty { finishedSummary.streamedText = assistantMessageText }
        if finishedSummary.failureText == nil && finishedSummary.resultText == nil, let assistantErrorCode {
            finishedSummary.failureText = assistantMessageText.isEmpty ? assistantErrorCode : assistantMessageText
        }
        return finishedSummary
    }

    private func handleLine(_ lineData: Data) -> String? {
        guard let event = try? JSONSerialization.jsonObject(with: lineData) as? [String: Any],
              let eventType = event["type"] as? String else { return nil }
        // Subagent output would carry a parent tool id; with no tools there is none, but it is
        // never part of the answer.
        if let parentToolUseIdentifier = event["parent_tool_use_id"] as? String, !parentToolUseIdentifier.isEmpty { return nil }

        switch eventType {
        case "stream_event":
            guard let streamEvent = event["event"] as? [String: Any],
                  streamEvent["type"] as? String == "content_block_delta",
                  let delta = streamEvent["delta"] as? [String: Any],
                  delta["type"] as? String == "text_delta",
                  let text = delta["text"] as? String, !text.isEmpty else { return nil }
            sawTextDelta = true
            state.streamedText += text
            return text
        case "assistant":
            if let errorCode = event["error"] as? String, !errorCode.isEmpty { assistantErrorCode = errorCode }
            if let message = event["message"] as? [String: Any], let contentBlocks = message["content"] as? [[String: Any]] {
                for contentBlock in contentBlocks where contentBlock["type"] as? String == "text" {
                    assistantMessageText += contentBlock["text"] as? String ?? ""
                }
            }
            return nil
        case "result":
            let resultText = event["result"] as? String
            let isError = event["is_error"] as? Bool ?? false
            let subtype = event["subtype"] as? String ?? ""
            if isError || subtype.hasPrefix("error") {
                let errorsText = (event["errors"] as? [String])?.joined(separator: " ")
                state.failureText = [resultText, errorsText, subtype].compactMap { $0 }.first { !$0.isEmpty } ?? "unknown error"
            } else {
                state.resultText = resultText
            }
            return nil
        default:
            return nil
        }
    }
}
