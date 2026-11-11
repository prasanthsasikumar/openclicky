//
//  OpenAIChatAnswerProvider.swift
//  OpenClicky
//
//  Answers through an OpenAI-compatible Chat Completions endpoint: OpenAI, OpenRouter, Groq,
//  Mistral and xAI all accept the same streamed request, so one provider serves the five of them
//  (`AnswerSource.chatCompletionsURL` says where). The request is built and the stream is read by
//  pure static functions, so both are unit-tested without the network (AnswerProviderHTTPTests).
//  The key goes straight to the vendor in an `Authorization: Bearer` header and is never logged.
//

import Foundation

// MARK: - The HTTP seam both HTTP providers share

/// A response as the providers read it: the status, then the body line by line (SSE is one event
/// per `data:` line, and an error body is read whole from the same lines).
nonisolated struct AnswerHTTPResponseLines: Sendable {
    let statusCode: Int
    let bodyLines: AsyncThrowingStream<String, Error>
}

/// Sends one request. Injected so tests feed canned SSE lines instead of the network.
typealias AnswerHTTPTransport = @Sendable (URLRequest) async throws -> AnswerHTTPResponseLines

nonisolated enum AnswerHTTPTransports {
    /// One long-lived session, like ClaudeAPI's: `.default` keeps TLS session tickets between
    /// questions (big screenshot bodies on a cold handshake fail with -1200), nothing cached on disk.
    private static let sharedSession: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 120
        configuration.timeoutIntervalForResource = 300
        configuration.waitsForConnectivity = true
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        return URLSession(configuration: configuration)
    }()

    /// The real network. The body is relayed line by line as it arrives so text can be shown and
    /// spoken while the answer is still being written; a consumer that stops early (`[DONE]`)
    /// cancels the download.
    static let urlSession: AnswerHTTPTransport = { request in
        let (byteStream, response) = try await sharedSession.bytes(for: request)
        let statusCode = (response as? HTTPURLResponse)?.statusCode ?? 0
        let bodyLines = AsyncThrowingStream<String, Error> { continuation in
            let readingTask = Task {
                do {
                    for try await line in byteStream.lines {
                        continuation.yield(line)
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in readingTask.cancel() }
        }
        return AnswerHTTPResponseLines(statusCode: statusCode, bodyLines: bodyLines)
    }
}

/// Helpers the OpenAI-compatible and Gemini providers both need.
nonisolated enum AnswerProviderHTTPSupport {
    /// The whole body of a refused request, read so the vendor's own message can be shown.
    static func readWholeBody(_ bodyLines: AsyncThrowingStream<String, Error>) async -> String {
        var collectedLines: [String] = []
        do {
            for try await line in bodyLines { collectedLines.append(line) }
        } catch {
            // A body that breaks off still says what it said up to there.
        }
        return collectedLines.joined(separator: "\n")
    }

    /// The vendor's own words from an error body. OpenAI, Groq, xAI and OpenRouter send
    /// `{"error":{"message":…}}`; Gemini sends the same, sometimes wrapped in an array on the
    /// streaming endpoint; Mistral sometimes sends a top-level `message` or `detail`. Anything else
    /// falls back to the start of the raw body, so the person still sees something specific.
    static func vendorErrorMessage(fromErrorBody errorBody: String) -> String {
        let trimmedErrorBody = errorBody.trimmingCharacters(in: .whitespacesAndNewlines)
        let parsedErrorBody = try? JSONSerialization.jsonObject(with: Data(trimmedErrorBody.utf8))
        let errorObject = (parsedErrorBody as? [String: Any]) ?? (parsedErrorBody as? [[String: Any]])?.first
        if let errorObject {
            if let nestedError = errorObject["error"] as? [String: Any],
               let nestedMessage = nestedError["message"] as? String, !nestedMessage.isEmpty {
                return nestedMessage
            }
            if let plainError = errorObject["error"] as? String, !plainError.isEmpty { return plainError }
            if let topLevelMessage = errorObject["message"] as? String, !topLevelMessage.isEmpty { return topLevelMessage }
            if let detail = errorObject["detail"] as? String, !detail.isEmpty { return detail }
        }
        if trimmedErrorBody.isEmpty { return "the request was refused with no explanation" }
        return String(trimmedErrorBody.prefix(300))
    }

    /// A tool's JSON arguments as ClaudeToolCall carries them, the same way
    /// `ClaudeAPI.parseToolCalls` reads Anthropic's: strings kept, numbers and booleans as their
    /// text, anything nested dropped (no quick action takes a nested argument).
    static func toolArguments(fromJSONObject argumentsObject: [String: Any]) -> [String: String] {
        var arguments: [String: String] = [:]
        for (argumentName, argumentValue) in argumentsObject {
            if let stringValue = argumentValue as? String { arguments[argumentName] = stringValue }
            else if let numberValue = argumentValue as? NSNumber { arguments[argumentName] = numberValue.stringValue }
        }
        return arguments
    }

    /// The tool's input schema as a JSON object. A schema that does not parse becomes an empty
    /// object schema rather than failing the whole question.
    static func inputSchemaObject(from inputSchemaJSON: Data) -> [String: Any] {
        if let schemaObject = (try? JSONSerialization.jsonObject(with: inputSchemaJSON)) as? [String: Any] {
            return schemaObject
        }
        return ["type": "object", "properties": [String: Any]()]
    }

    /// The text after `data:` on an SSE line, or nil for comments (OpenRouter sends
    /// `: OPENROUTER PROCESSING` keep-alives), `event:` lines and blanks.
    static func sseDataPayload(fromLine line: String) -> String? {
        guard line.hasPrefix("data:") else { return nil }
        return String(line.dropFirst(5)).trimmingCharacters(in: .whitespaces)
    }
}

// MARK: - The provider

nonisolated struct OpenAIChatAnswerProvider: AnswerProvider {
    let source: AnswerSource
    let apiKey: String
    let model: String
    let transport: AnswerHTTPTransport

    init(source: AnswerSource, apiKey: String, model: String, transport: @escaping AnswerHTTPTransport = AnswerHTTPTransports.urlSession) {
        self.source = source
        self.apiKey = apiKey
        self.model = model
        self.transport = transport
    }

    func answer(_ request: AnswerRequest, onTextChunk: @escaping @Sendable (String) -> Void) async throws -> AnswerResult {
        let urlRequest = try Self.makeURLRequest(source: source, apiKey: apiKey, model: model, answerRequest: request)
        let response = try await transport(urlRequest)

        guard (200...299).contains(response.statusCode) else {
            let errorBody = await AnswerProviderHTTPSupport.readWholeBody(response.bodyLines)
            let vendorMessage = AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: errorBody)
            AppLog.append("answer: \(source.rawValue) \(model) refused with \(response.statusCode): \(vendorMessage)")
            throw AnswerProviderError(source: source, status: response.statusCode, message: vendorMessage)
        }

        var streamAccumulator = OpenAIChatStreamAccumulator()
        readingStream: for try await line in response.bodyLines {
            let lineOutcome: OpenAIChatStreamAccumulator.LineOutcome
            do {
                lineOutcome = try streamAccumulator.consume(line: line)
            } catch let midStreamError as OpenAIChatStreamAccumulator.MidStreamError {
                AppLog.append("answer: \(source.rawValue) \(model) stream error after \(streamAccumulator.accumulatedText.count) chars: \(midStreamError.message)")
                throw AnswerProviderError(source: source, status: response.statusCode, message: midStreamError.message)
            }
            switch lineOutcome {
            case .textChunk(let textChunk): onTextChunk(textChunk)
            case .streamFinished: break readingStream
            case .nothingToShow: continue
            }
        }
        if streamAccumulator.finishReason == "length" {
            AppLog.append("answer: \(source.rawValue) \(model) cut off at the token limit (\(streamAccumulator.accumulatedText.count) chars)")
        }
        return streamAccumulator.result()
    }

    // MARK: request

    /// OpenAI's reasoning models (gpt-5…, o1/o3/o4…) refuse `max_tokens` and want
    /// `max_completion_tokens`, which OpenAI's other current models accept too; so every request to
    /// OpenAI itself sends `max_completion_tokens`. The compatible services (Groq, Mistral, xAI,
    /// OpenRouter) are sent the classic `max_tokens`, which all of them take.
    /// Note: on a reasoning model the limit also covers its hidden reasoning, so a small limit can
    /// leave no room for the visible answer.
    static func tokenLimitFieldName(for source: AnswerSource) -> String {
        source == .openAI ? "max_completion_tokens" : "max_tokens"
    }

    /// The request body as a JSON object (kept separate from the URLRequest so tests can read it).
    static func makeRequestBody(source: AnswerSource, model: String, answerRequest: AnswerRequest) -> [String: Any] {
        var messages: [[String: Any]] = []
        if !answerRequest.systemPrompt.isEmpty {
            messages.append(["role": "system", "content": answerRequest.systemPrompt])
        }
        for earlierTurn in answerRequest.history {
            messages.append(["role": "user", "content": earlierTurn.user])
            messages.append(["role": "assistant", "content": earlierTurn.assistant])
        }

        // Each screenshot, then its label (which says which display it is and its pixel size, what
        // the [POINT:…] coordinates are measured against), then the question itself.
        var currentUserContentParts: [[String: Any]] = []
        for labelledImage in answerRequest.images {
            let dataURL = "data:\(labelledImage.mediaType);base64,\(labelledImage.data.base64EncodedString())"
            currentUserContentParts.append(["type": "image_url", "image_url": ["url": dataURL]])
            currentUserContentParts.append(["type": "text", "text": labelledImage.label])
        }
        currentUserContentParts.append(["type": "text", "text": answerRequest.userPrompt])
        messages.append(["role": "user", "content": currentUserContentParts])

        var body: [String: Any] = [
            "model": model,
            "stream": true,
            "messages": messages,
            tokenLimitFieldName(for: source): answerRequest.maxTokens,
        ]
        if !answerRequest.tools.isEmpty {
            body["tools"] = answerRequest.tools.map { quickActionTool in
                [
                    "type": "function",
                    "function": [
                        "name": quickActionTool.name,
                        "description": quickActionTool.description,
                        "parameters": AnswerProviderHTTPSupport.inputSchemaObject(from: quickActionTool.inputSchemaJSON),
                    ] as [String: Any],
                ] as [String: Any]
            }
        }
        return body
    }

    static func makeURLRequest(source: AnswerSource, apiKey: String, model: String, answerRequest: AnswerRequest) throws -> URLRequest {
        guard let chatCompletionsURL = source.chatCompletionsURL else {
            throw AnswerProviderError(source: source, status: 0, message: "\(source.displayName) does not offer an OpenAI-compatible chat endpoint")
        }
        var urlRequest = URLRequest(url: chatCompletionsURL)
        urlRequest.httpMethod = "POST"
        urlRequest.timeoutInterval = 120
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        urlRequest.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        if source == .openRouter {
            // OpenRouter's app attribution: optional, but it is how the requests show up as
            // OpenClicky's on their side instead of as an unnamed client.
            urlRequest.setValue("https://github.com/prasanthsasikumar/openclicky", forHTTPHeaderField: "HTTP-Referer")
            urlRequest.setValue("OpenClicky", forHTTPHeaderField: "X-Title")
        }
        urlRequest.httpBody = try JSONSerialization.data(withJSONObject: makeRequestBody(source: source, model: model, answerRequest: answerRequest))
        return urlRequest
    }

    // MARK: stream

    /// The answer in a whole list of SSE lines (what the streaming loop builds line by line).
    static func parseStream(lines: [String]) throws -> AnswerResult {
        var streamAccumulator = OpenAIChatStreamAccumulator()
        for line in lines {
            if case .streamFinished = try streamAccumulator.consume(line: line) { break }
        }
        return streamAccumulator.result()
    }
}

/// Reads a Chat Completions stream one SSE line at a time: text deltas as they come, tool calls
/// gathered by their `index` (the name arrives in the first fragment, the JSON arguments spread
/// over many) and parsed once the stream is over.
nonisolated struct OpenAIChatStreamAccumulator {
    enum LineOutcome: Equatable {
        case textChunk(String)
        case streamFinished
        case nothingToShow
    }

    /// An `{"error":…}` event after the 200 (OpenRouter reports an upstream failure this way);
    /// ignored, it would end the answer in silence as if it were complete.
    struct MidStreamError: Error, Equatable {
        let message: String
    }

    private struct PartialToolCall {
        var id: String = ""
        var functionName: String = ""
        var argumentsJSONText: String = ""
    }

    private(set) var accumulatedText = ""
    private(set) var finishReason: String?
    private var partialToolCallsByIndex: [Int: PartialToolCall] = [:]

    mutating func consume(line: String) throws -> LineOutcome {
        guard let dataPayload = AnswerProviderHTTPSupport.sseDataPayload(fromLine: line) else { return .nothingToShow }
        if dataPayload == "[DONE]" { return .streamFinished }
        guard let event = (try? JSONSerialization.jsonObject(with: Data(dataPayload.utf8))) as? [String: Any] else {
            return .nothingToShow
        }
        if let streamError = event["error"] {
            let errorMessage = (streamError as? [String: Any])?["message"] as? String ?? (streamError as? String) ?? "the stream broke off"
            throw MidStreamError(message: errorMessage)
        }
        guard let firstChoice = (event["choices"] as? [[String: Any]])?.first else { return .nothingToShow }
        if let choiceFinishReason = firstChoice["finish_reason"] as? String {
            finishReason = choiceFinishReason
            if choiceFinishReason == SafetyBlock.openAIFinishReason { throw SafetyBlockError(reason: choiceFinishReason) }
        }
        guard let delta = firstChoice["delta"] as? [String: Any] else { return .nothingToShow }
        // A model that declines writes it in `refusal`, not `content`: unread, the answer came back empty.
        if let refusal = delta["refusal"] as? String, !refusal.isEmpty { throw SafetyBlockError(reason: "refusal") }

        if let toolCallFragments = delta["tool_calls"] as? [[String: Any]] {
            for toolCallFragment in toolCallFragments {
                // Some compatible services leave out `index` when there is only one call.
                let toolCallIndex = toolCallFragment["index"] as? Int ?? 0
                var partialToolCall = partialToolCallsByIndex[toolCallIndex] ?? PartialToolCall()
                if let toolCallID = toolCallFragment["id"] as? String, !toolCallID.isEmpty { partialToolCall.id = toolCallID }
                if let function = toolCallFragment["function"] as? [String: Any] {
                    if let functionName = function["name"] as? String, !functionName.isEmpty { partialToolCall.functionName = functionName }
                    if let argumentsFragment = function["arguments"] as? String { partialToolCall.argumentsJSONText += argumentsFragment }
                }
                partialToolCallsByIndex[toolCallIndex] = partialToolCall
            }
        }

        if let textChunk = delta["content"] as? String, !textChunk.isEmpty {
            accumulatedText += textChunk
            return .textChunk(textChunk)
        }
        return .nothingToShow
    }

    /// The text and the tool calls, in the order the model made them. A call whose arguments are
    /// not valid JSON is dropped, as `ClaudeAPI.parseToolCalls` drops Anthropic's.
    func result() -> AnswerResult {
        var toolCalls: [ClaudeToolCall] = []
        for toolCallIndex in partialToolCallsByIndex.keys.sorted() {
            guard let partialToolCall = partialToolCallsByIndex[toolCallIndex], !partialToolCall.functionName.isEmpty else { continue }
            let argumentsJSONText = partialToolCall.argumentsJSONText.isEmpty ? "{}" : partialToolCall.argumentsJSONText
            guard let argumentsObject = (try? JSONSerialization.jsonObject(with: Data(argumentsJSONText.utf8))) as? [String: Any] else { continue }
            toolCalls.append(ClaudeToolCall(
                id: partialToolCall.id.isEmpty ? "call-\(toolCallIndex)" : partialToolCall.id,
                name: partialToolCall.functionName,
                arguments: AnswerProviderHTTPSupport.toolArguments(fromJSONObject: argumentsObject)
            ))
        }
        return AnswerResult(text: accumulatedText, toolCalls: toolCalls)
    }
}
