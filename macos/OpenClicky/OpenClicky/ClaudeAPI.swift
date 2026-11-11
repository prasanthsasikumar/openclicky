//
//  ClaudeAPI.swift
//  Claude API Implementation with streaming support
//

import Foundation

/// Claude API helper with streaming for progressive text display.
class ClaudeAPI {
    private static let tlsWarmupLock = NSLock()
    private static var hasStartedTLSWarmup = false

    /// The /chat URL, read for every request so a backend changed in shell.json while the app runs
    /// is used by the next question (it was fixed when the client was built).
    private let currentProxyURL: () -> String
    var apiURL: URL { Self.resolvedAPIURL(fromProxyURL: currentProxyURL()) }
    var model: String

    /// The NSError code (domain "ClaudeAPI") for an `error` event inside a stream that had started.
    static let midStreamErrorCode = -1002
    private let session: URLSession

    /// `proxyURL` is built from the user-configurable backend URL (shell.json or an environment
    /// override), not a compile-time literal, so a malformed value falls back to the hosted
    /// backend's own /chat endpoint instead of crashing the app. That fallback string is a literal
    /// built from a known-good constant, so force-unwrapping it is safe. Pulled out as a pure static
    /// function so the fallback behavior can be unit tested without constructing a full ClaudeAPI
    /// (whose init fires a background TLS warmup network request).
    static func resolvedAPIURL(fromProxyURL proxyURL: String) -> URL {
        URL(string: proxyURL) ?? URL(string: "\(OpenClickyConfiguration.hostedBackendURL)/chat")!
    }

    convenience init(proxyURL: String, model: String = "claude-sonnet-4-6") {
        self.init(currentProxyURL: { proxyURL }, model: model)
    }

    init(currentProxyURL: @escaping () -> String, model: String = "claude-sonnet-4-6") {
        self.currentProxyURL = currentProxyURL
        self.model = model

        // Use .default instead of .ephemeral so TLS session tickets are cached.
        // Ephemeral sessions do a full TLS handshake on every request, which causes
        // transient -1200 (errSSLPeerHandshakeFail) errors with large image payloads.
        // Disable URL/cookie caching to avoid storing responses or credentials on disk.
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 120
        config.timeoutIntervalForResource = 300
        config.waitsForConnectivity = true
        config.urlCache = nil
        config.httpCookieStorage = nil
        self.session = URLSession(configuration: config)

        // Fire a lightweight HEAD request in the background to pre-establish the TLS
        // connection. This caches the TLS session ticket so the first real API call
        // (which carries a large image payload) doesn't need a cold TLS handshake.
        warmUpTLSConnectionIfNeeded()
    }

    /// The person's own Anthropic key goes straight to Anthropic with the same Messages body /chat
    /// relays; without one, the backend's /chat on the account's token (`ProviderRouter`).
    /// `goesToBackend` says the request carries the account's token, so a 401 is the sign-in's
    /// and goes through `BackendSignIn` (refresh once, send again).
    private func makeAPIRequest() throws -> (request: URLRequest, goesToBackend: Bool) {
        var request: URLRequest
        let goesToBackend: Bool
        switch ProviderRouter.current(for: .claude) {
        case .anthropicDirect:
            guard let anthropicKey = ProviderRouter.currentCredentials().anthropicKey else {
                throw ProviderRouteUnavailable(capability: .claude, sentence: ProviderRouter.needsAnthropicKeySentence)
            }
            request = DirectProviderRequests.anthropicMessages(key: anthropicKey)
            goesToBackend = false
        case .unavailable(let sentence):
            throw ProviderRouteUnavailable(capability: .claude, sentence: sentence)
        case .answerProvider(let source):
            // analyzeImageStreaming hands these to an AnswerProvider before building a request.
            throw ProviderRouteUnavailable(capability: .claude, sentence: "\(source.displayName) answers through its own provider")
        case .backend, .openAIDirect:
            request = URLRequest(url: apiURL)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            OpenClickyConfiguration.authorize(&request)
            goesToBackend = true
        }
        request.timeoutInterval = 120
        return (request, goesToBackend)
    }

    /// The start of a streamed answer: the byte stream of a 2xx answer, or a refusal whose body has
    /// been read to the end (BackendSignIn needs a 401's body to tell the sign-in's from an own key's).
    private enum StreamingAnswerStart {
        case streaming(URLSession.AsyncBytes, status: Int)
        case refused(status: Int, body: Data)

        var statusAndBody: (status: Int, body: Data) {
            switch self {
            case .streaming(_, let status): return (status, Data())
            case .refused(let status, let body): return (status, body)
            }
        }
    }

    private static func startStreamingAnswer(_ request: URLRequest, session: URLSession) async throws -> StreamingAnswerStart {
        let (byteStream, response) = try await session.bytes(for: request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw NSError(
                domain: "ClaudeAPI",
                code: -1,
                userInfo: [NSLocalizedDescriptionKey: "Invalid HTTP response"]
            )
        }
        // If non-2xx status, read the full body as error text
        guard (200...299).contains(httpResponse.statusCode) else {
            var errorBodyChunks: [String] = []
            for try await line in byteStream.lines {
                errorBodyChunks.append(line)
            }
            return .refused(status: httpResponse.statusCode, body: Data(errorBodyChunks.joined(separator: "\n").utf8))
        }
        return .streaming(byteStream, status: httpResponse.statusCode)
    }

    /// Detects the MIME type of image data by inspecting the first bytes.
    /// Screen captures from ScreenCaptureKit are JPEG, but pasted images from the
    /// clipboard are PNG. The API rejects requests where the declared media_type
    /// doesn't match the actual image format.
    private func detectImageMediaType(for imageData: Data) -> String {
        // PNG files start with the 8-byte signature: 89 50 4E 47 0D 0A 1A 0A
        if imageData.count >= 4 {
            let pngSignature: [UInt8] = [0x89, 0x50, 0x4E, 0x47]
            let firstFourBytes = [UInt8](imageData.prefix(4))
            if firstFourBytes == pngSignature {
                return "image/png"
            }
        }
        // Default to JPEG — screen captures use JPEG compression
        return "image/jpeg"
    }

    /// Sends a no-op HEAD request to the API host to establish and cache a TLS session.
    /// Failures are silently ignored — this is purely an optimization.
    private func warmUpTLSConnectionIfNeeded() {
        Self.tlsWarmupLock.lock()
        let shouldStartTLSWarmup = !Self.hasStartedTLSWarmup
        if shouldStartTLSWarmup {
            Self.hasStartedTLSWarmup = true
        }
        Self.tlsWarmupLock.unlock()

        guard shouldStartTLSWarmup else { return }

        // Warm the host the next question will go to: Anthropic on an own key, else the backend.
        let warmupTarget = ProviderRouter.currentCredentials().anthropicKey != nil ? URL(string: DirectProviderRequests.anthropicMessagesURL)! : apiURL
        guard var warmupURLComponents = URLComponents(url: warmupTarget, resolvingAgainstBaseURL: false) else {
            return
        }

        // The TLS session ticket is host-scoped, so warming the root host is enough.
        // Hitting the host instead of `/v1/messages` avoids extra endpoint-specific noise.
        warmupURLComponents.path = "/"
        warmupURLComponents.query = nil
        warmupURLComponents.fragment = nil

        guard let warmupURL = warmupURLComponents.url else {
            return
        }

        var warmupRequest = URLRequest(url: warmupURL)
        warmupRequest.httpMethod = "HEAD"
        warmupRequest.timeoutInterval = 10
        session.dataTask(with: warmupRequest) { _, _, _ in
            // Response doesn't matter — the TLS handshake is the goal
        }.resume()
    }

    /// Send a vision request to Claude with streaming.
    /// Calls `onTextChunk` on the main actor each time new text arrives so the UI updates progressively.
    /// Returns the full accumulated text and total duration when the stream completes.
    func analyzeImageStreaming(
        images: [(data: Data, label: String)],
        systemPrompt: String,
        conversationHistory: [(userPlaceholder: String, assistantResponse: String)] = [],
        userPrompt: String,
        tools: [[String: Any]] = [],
        options: AnswerRequestOptions = AnswerRequestOptions(),
        onTextChunk: @MainActor @Sendable (String) -> Void
    ) async throws -> (text: String, toolCalls: [ClaudeToolCall], duration: TimeInterval) {
        let startTime = Date()

        // Another source answers (an OpenAI-compatible key, Gemini, a Claude Code or Codex plan):
        // the same question goes to its AnswerProvider, and its text streams back the same way.
        if case .answerProvider(let source) = ProviderRouter.current(for: .claude) {
            let result = try await answerThroughProvider(
                source: source, images: images, systemPrompt: systemPrompt, conversationHistory: conversationHistory,
                userPrompt: userPrompt, tools: tools, options: options, onTextChunk: onTextChunk)
            return (text: result.text, toolCalls: result.toolCalls, duration: Date().timeIntervalSince(startTime))
        }

        let apiRequest = try makeAPIRequest()
        var request = apiRequest.request
        for (headerName, headerValue) in Self.extraHeaders(for: options) { request.setValue(headerValue, forHTTPHeaderField: headerName) }
        let goesToBackend = apiRequest.goesToBackend

        // Build messages array
        var messages: [[String: Any]] = []

        for (userPlaceholder, assistantResponse) in conversationHistory {
            messages.append(["role": "user", "content": userPlaceholder])
            messages.append(["role": "assistant", "content": assistantResponse])
        }

        // Build current message with all labeled images + prompt
        var contentBlocks: [[String: Any]] = []
        for image in images {
            contentBlocks.append([
                "type": "image",
                "source": [
                    "type": "base64",
                    "media_type": detectImageMediaType(for: image.data),
                    "data": image.data.base64EncodedString()
                ]
            ])
            contentBlocks.append([
                "type": "text",
                "text": image.label
            ])
        }
        contentBlocks.append([
            "type": "text",
            "text": userPrompt
        ])
        messages.append(["role": "user", "content": contentBlocks])

        let body = Self.anthropicRequestBody(model: model, systemPrompt: systemPrompt, messages: messages, tools: tools, options: options)

        let bodyData = try JSONSerialization.data(withJSONObject: body)
        request.httpBody = bodyData
        let payloadMB = Double(bodyData.count) / 1_048_576.0
        print("🌐 Claude streaming request: \(String(format: "%.1f", payloadMB))MB, \(images.count) image(s)")

        // Use bytes streaming for SSE (Server-Sent Events). On the account's token a refused
        // sign-in is renewed once and the question sent again, like every other backend call.
        let session = self.session
        let streamingAnswerStart = goesToBackend
            ? try await BackendSignIn.send(request, using: { try await Self.startStreamingAnswer($0, session: session) },
                                           readingStatusAndBodyWith: { $0.statusAndBody })
            : try await Self.startStreamingAnswer(request, session: session)
        let byteStream: URLSession.AsyncBytes
        switch streamingAnswerStart {
        case .streaming(let answerByteStream, _):
            byteStream = answerByteStream
        case .refused(let status, let body):
            throw BackendHTTPError(service: .answers, status: status, body: body)
        }

        // Parse SSE stream — each event is "data: {json}\n\n"
        var accumulatedResponseText = ""
        var sseLines: [String] = []

        for try await line in byteStream.lines {
            sseLines.append(line)
            // SSE lines look like: "data: {...}"
            guard line.hasPrefix("data: ") else { continue }
            let jsonString = String(line.dropFirst(6)) // Drop "data: " prefix

            // End of stream marker
            guard jsonString != "[DONE]" else { break }

            guard let jsonData = jsonString.data(using: .utf8),
                  let eventPayload = try? JSONSerialization.jsonObject(with: jsonData) as? [String: Any],
                  let eventType = eventPayload["type"] as? String else {
                continue
            }

            // Anthropic reports a failure after the 200 (overloaded, a dropped upstream) as an
            // `error` event; ignored, it ended the answer in silence as if it were complete.
            if let streamFailure = Self.streamFailure(fromEvent: eventPayload) {
                AppLog.append("claude: stream error after \(accumulatedResponseText.count) chars: \(streamFailure.localizedDescription)")
                throw streamFailure
            }
            // A cut-off answer is still spoken as it is; the log says why it ended early.
            let stopReason = Self.stopReason(fromEvent: eventPayload)
            if stopReason == "max_tokens" {
                AppLog.append("claude: answer cut off at max_tokens (\(accumulatedResponseText.count) chars)")
            }
            // Claude's safety classifier stopped the answer (it may have started): said plainly, not spoken half.
            if stopReason == SafetyBlock.anthropicStopReason {
                AppLog.append("claude: answer stopped by the safety filter after \(accumulatedResponseText.count) chars")
                throw SafetyBlockError(reason: SafetyBlock.anthropicStopReason)
            }

            // We care about content_block_delta events that contain text chunks
            if eventType == "content_block_delta",
               let delta = eventPayload["delta"] as? [String: Any],
               let deltaType = delta["type"] as? String,
               deltaType == "text_delta",
               let textChunk = delta["text"] as? String {
                accumulatedResponseText += textChunk
                // Send the accumulated text so far to the UI for progressive rendering
                let currentAccumulatedText = accumulatedResponseText
                await onTextChunk(currentAccumulatedText)
            }
        }

        let duration = Date().timeIntervalSince(startTime)
        return (text: accumulatedResponseText, toolCalls: Self.parseToolCalls(fromSSELines: sseLines), duration: duration)
    }

    /// The same question asked of another source's AnswerProvider (an OpenAI-compatible key,
    /// Gemini, a Claude Code or Codex plan). The pieces come through a stream this call reads to the
    /// end, in order; each is handed on as the whole answer so far, like the Anthropic path.
    /// Cancelling the question cancels the provider (a CLI process is ended).
    /// The Anthropic Messages body for a streamed answer. Pure, so a test can read what a drawing
    /// asks for: its token budget, and no thinking on the models that think by default (Sonnet 5
    /// and Opus 5 take `between_tools`; Haiku and the 4.x models would refuse it).
    static func anthropicRequestBody(model: String, systemPrompt: String, messages: [[String: Any]], tools: [[String: Any]],
                                     options: AnswerRequestOptions) -> [String: Any] {
        var body: [String: Any] = [
            "model": model,
            "max_tokens": options.maxTokens,
            "stream": true,
            "system": systemPrompt,
            "messages": messages
        ]
        if !tools.isEmpty { body["tools"] = tools }
        if options.thinkingOff, model.hasPrefix("claude-sonnet-5") || model.hasPrefix("claude-opus-5") {
            body["thinking"] = ["type": "between_tools"]
        }
        return body
    }

    /// Request headers beyond the usual: a drawing names itself so the backend allows its budget.
    static func extraHeaders(for options: AnswerRequestOptions) -> [String: String] {
        options.purpose == .drawing ? ["X-OpenClicky-Purpose": "drawing"] : [:]
    }

    private func answerThroughProvider(
        source: AnswerSource,
        images: [(data: Data, label: String)],
        systemPrompt: String,
        conversationHistory: [(userPlaceholder: String, assistantResponse: String)],
        userPrompt: String,
        tools: [[String: Any]],
        options: AnswerRequestOptions,
        onTextChunk: @MainActor @Sendable (String) -> Void
    ) async throws -> AnswerResult {
        let provider = try await AnswerProviderFactory.make(for: source, credentials: ProviderRouter.currentCredentials(),
                                                      modelOverrides: OpenClickyConfiguration.settings.answerModels ?? [:])
        var answerRequest = AnswerRequest(
            images: images.map { AnswerRequest.Image(data: $0.data, label: $0.label, mediaType: detectImageMediaType(for: $0.data)) },
            systemPrompt: systemPrompt,
            history: conversationHistory.map { AnswerRequest.Turn(user: $0.userPlaceholder, assistant: $0.assistantResponse) },
            userPrompt: userPrompt,
            tools: tools.compactMap(Self.answerTool(fromAnthropicTool:)))
        answerRequest.maxTokens = options.maxTokens
        answerRequest.purpose = options.purpose
        answerRequest.timeoutSeconds = options.timeoutSeconds
        let (pieces, piecesContinuation) = AsyncStream<String>.makeStream()
        let answerTask = Task<AnswerResult, Error> {
            defer { piecesContinuation.finish() }
            return try await provider.answer(answerRequest) { piecesContinuation.yield($0) }
        }
        let result = try await withTaskCancellationHandler {
            var answerSoFar = ""
            for await piece in pieces {
                answerSoFar += piece
                await onTextChunk(answerSoFar)
            }
            return try await answerTask.value
        } onCancel: {
            answerTask.cancel()
        }
        AppLog.append("answer: \(source.displayName) answered (\(result.text.count) chars, \(result.toolCalls.count) tool calls)")
        return result
    }

    /// Non-streaming request for short answers nobody watches arrive (screen grounding). Another
    /// answer source gets the question through its AnswerProvider, as the streaming call does; it
    /// used to throw here, so grounding silently never ran on those routes.
    func analyzeImage(
        images: [(data: Data, label: String)],
        systemPrompt: String,
        conversationHistory: [(userPlaceholder: String, assistantResponse: String)] = [],
        userPrompt: String
    ) async throws -> (text: String, duration: TimeInterval) {
        let startTime = Date()

        if case .answerProvider(let source) = ProviderRouter.current(for: .claude) {
            let result = try await answerThroughProvider(
                source: source, images: images, systemPrompt: systemPrompt, conversationHistory: conversationHistory,
                // Not the 256 Anthropic gets below: a reasoning model (a Gemini 3, an OpenAI
                // reasoning model) spends part of its limit thinking before the short answer.
                userPrompt: userPrompt, tools: [], options: AnswerRequestOptions(), onTextChunk: { _ in })
            return (text: result.text, duration: Date().timeIntervalSince(startTime))
        }

        let apiRequest = try makeAPIRequest()
        var request = apiRequest.request
        let goesToBackend = apiRequest.goesToBackend

        var messages: [[String: Any]] = []
        for (userPlaceholder, assistantResponse) in conversationHistory {
            messages.append(["role": "user", "content": userPlaceholder])
            messages.append(["role": "assistant", "content": assistantResponse])
        }

        // Build current message with all labeled images + prompt
        var contentBlocks: [[String: Any]] = []
        for image in images {
            contentBlocks.append([
                "type": "image",
                "source": [
                    "type": "base64",
                    "media_type": detectImageMediaType(for: image.data),
                    "data": image.data.base64EncodedString()
                ]
            ])
            contentBlocks.append([
                "type": "text",
                "text": image.label
            ])
        }
        contentBlocks.append([
            "type": "text",
            "text": userPrompt
        ])
        messages.append(["role": "user", "content": contentBlocks])

        let body: [String: Any] = [
            "model": model,
            "max_tokens": 256,
            "system": systemPrompt,
            "messages": messages
        ]

        let bodyData = try JSONSerialization.data(withJSONObject: body)
        request.httpBody = bodyData
        let payloadMB = Double(bodyData.count) / 1_048_576.0
        print("🌐 Claude request: \(String(format: "%.1f", payloadMB))MB, \(images.count) image(s)")

        let (data, response) = goesToBackend
            ? try await BackendSignIn.send(request) { [session] in try await session.data(for: $0) }
            : try await session.data(for: request)

        guard let httpResponse = response as? HTTPURLResponse else {
            throw NSError(
                domain: "ClaudeAPI",
                code: -1,
                userInfo: [NSLocalizedDescriptionKey: "Invalid HTTP response"]
            )
        }
        guard (200...299).contains(httpResponse.statusCode) else {
            throw BackendHTTPError(service: .answers, status: httpResponse.statusCode, body: data)
        }

        let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let content = json?["content"] as? [[String: Any]],
              let textBlock = content.first(where: { ($0["type"] as? String) == "text" }),
              let text = textBlock["text"] as? String else {
            throw NSError(
                domain: "ClaudeAPI",
                code: -1,
                userInfo: [NSLocalizedDescriptionKey: "Invalid response format"]
            )
        }

        let duration = Date().timeIntervalSince(startTime)
        return (text: text, duration: duration)
    }
}

extension ClaudeAPI {
    /// An Anthropic tool definition (name, description, input_schema) as an AnswerRequest tool.
    nonisolated static func answerTool(fromAnthropicTool tool: [String: Any]) -> AnswerRequest.Tool? {
        guard let name = tool["name"] as? String else { return nil }
        let schema = tool["input_schema"] as? [String: Any] ?? ["type": "object", "properties": [String: Any]()]
        guard let schemaJSON = try? JSONSerialization.data(withJSONObject: schema) else { return nil }
        return AnswerRequest.Tool(name: name, description: tool["description"] as? String ?? "", inputSchemaJSON: schemaJSON)
    }
}

nonisolated struct ClaudeToolCall: Equatable, Sendable {
    let id: String
    let name: String
    let arguments: [String: String]
}

extension ClaudeAPI {
    /// The failure an Anthropic `error` event reports ({"type":"error","error":{"type":…,"message":…}}),
    /// as a ClaudeAPI error with `midStreamErrorCode`; nil for every other event.
    static func streamFailure(fromEvent eventPayload: [String: Any]) -> NSError? {
        guard eventPayload["type"] as? String == "error" else { return nil }
        let streamError = eventPayload["error"] as? [String: Any]
        let errorType = streamError?["type"] as? String ?? "unknown"
        let errorMessage = streamError?["message"] as? String ?? ""
        return NSError(
            domain: "ClaudeAPI",
            code: midStreamErrorCode,
            userInfo: [NSLocalizedDescriptionKey: "Stream error (\(errorType)): \(errorMessage)"]
        )
    }

    /// The `stop_reason` a `message_delta` event carries ("end_turn", "max_tokens", "tool_use"…).
    static func stopReason(fromEvent eventPayload: [String: Any]) -> String? {
        guard eventPayload["type"] as? String == "message_delta",
              let delta = eventPayload["delta"] as? [String: Any] else { return nil }
        return delta["stop_reason"] as? String
    }

    /// Tool calls from an Anthropic SSE stream: `content_block_start` names the tool, `input_json_delta`
    /// pieces build its input, `content_block_stop` closes it. Input that is not valid JSON is dropped.
    static func parseToolCalls(fromSSELines lines: [String]) -> [ClaudeToolCall] {
        var open: [Int: (id: String, name: String, json: String)] = [:]
        var calls: [ClaudeToolCall] = []
        for line in lines where line.hasPrefix("data: ") {
            guard let data = line.dropFirst(6).data(using: .utf8),
                  let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let type = event["type"] as? String,
                  let index = event["index"] as? Int else { continue }
            switch type {
            case "content_block_start":
                if let block = event["content_block"] as? [String: Any], block["type"] as? String == "tool_use" {
                    open[index] = (block["id"] as? String ?? "", block["name"] as? String ?? "", "")
                }
            case "content_block_delta":
                if let delta = event["delta"] as? [String: Any], delta["type"] as? String == "input_json_delta",
                   let piece = delta["partial_json"] as? String, open[index] != nil {
                    open[index]!.json += piece
                }
            case "content_block_stop":
                guard let tool = open.removeValue(forKey: index) else { continue }
                let raw = tool.json.isEmpty ? "{}" : tool.json
                guard let object = try? JSONSerialization.jsonObject(with: Data(raw.utf8)) as? [String: Any] else { continue }
                var arguments: [String: String] = [:]
                for (key, value) in object {
                    if let string = value as? String { arguments[key] = string }
                    else if let number = value as? NSNumber { arguments[key] = number.stringValue }
                }
                calls.append(ClaudeToolCall(id: tool.id, name: tool.name, arguments: arguments))
            default:
                continue
            }
        }
        return calls
    }
}
