//
//  GeminiAnswerProvider.swift
//  OpenClicky
//
//  Answers through Google's Gemini API (`streamGenerateContent` with `alt=sse`). Gemini has its
//  own shapes: turns are `contents` with roles user/model, images are `inlineData`, tools are
//  `functionDeclarations`, and a tool call arrives whole as a `functionCall` part. The request is
//  built and the stream is read by pure static functions, unit-tested in AnswerProviderHTTPTests.
//  The key travels in the `x-goog-api-key` header, never in the URL, so it cannot end up in a log
//  line that prints a URL.
//

import Foundation

nonisolated struct GeminiAnswerProvider: AnswerProvider {
    let apiKey: String
    let model: String
    let transport: AnswerHTTPTransport

    init(apiKey: String, model: String, transport: @escaping AnswerHTTPTransport = AnswerHTTPTransports.urlSession) {
        self.apiKey = apiKey
        self.model = model
        self.transport = transport
    }

    func answer(_ request: AnswerRequest, onTextChunk: @escaping @Sendable (String) -> Void) async throws -> AnswerResult {
        let urlRequest = try Self.makeURLRequest(apiKey: apiKey, model: model, answerRequest: request)
        let response = try await transport(urlRequest)

        guard (200...299).contains(response.statusCode) else {
            let errorBody = await AnswerProviderHTTPSupport.readWholeBody(response.bodyLines)
            let vendorMessage = AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: errorBody)
            AppLog.append("answer: gemini \(model) refused with \(response.statusCode): \(vendorMessage)")
            throw AnswerProviderError(source: .gemini, status: response.statusCode, message: vendorMessage)
        }

        var streamAccumulator = GeminiStreamAccumulator()
        for try await line in response.bodyLines {
            let textChunks: [String]
            do {
                textChunks = try streamAccumulator.consume(line: line)
            } catch let midStreamError as GeminiStreamAccumulator.StreamRefusal {
                AppLog.append("answer: gemini \(model) stream refused after \(streamAccumulator.accumulatedText.count) chars: \(midStreamError.message)")
                throw AnswerProviderError(source: .gemini, status: response.statusCode, message: midStreamError.message)
            }
            for textChunk in textChunks { onTextChunk(textChunk) }
        }
        if streamAccumulator.finishReason == "MAX_TOKENS" {
            AppLog.append("answer: gemini \(model) cut off at maxOutputTokens (\(streamAccumulator.accumulatedText.count) chars)")
        }
        return streamAccumulator.result()
    }

    // MARK: request

    static func streamGenerateContentURL(model: String) -> URL? {
        let pathSafeModel = model.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? model
        return URL(string: "https://generativelanguage.googleapis.com/v1beta/models/\(pathSafeModel):streamGenerateContent?alt=sse")
    }

    /// JSON Schema keywords Gemini's function declarations reject ("Unknown name
    /// \"additionalProperties\""): its parameters are an OpenAPI subset, so these are removed.
    static let schemaKeywordsGeminiRejects: Set<String> = [
        "additionalProperties", "$schema", "$id", "$comment", "$defs", "definitions", "examples",
    ]

    /// The schema without the keywords Gemini rejects, at every depth. The keys directly inside
    /// `properties` are argument names, not keywords, so an argument that happens to be called
    /// "examples" is kept.
    static func schemaGeminiAccepts(from schemaValue: Any) -> Any {
        if let schemaArray = schemaValue as? [Any] {
            return schemaArray.map { schemaGeminiAccepts(from: $0) }
        }
        guard let schemaObject = schemaValue as? [String: Any] else { return schemaValue }
        var acceptedSchemaObject: [String: Any] = [:]
        for (keyword, keywordValue) in schemaObject where !schemaKeywordsGeminiRejects.contains(keyword) {
            if keyword == "properties", let propertiesByArgumentName = keywordValue as? [String: Any] {
                var acceptedProperties: [String: Any] = [:]
                for (argumentName, argumentSchema) in propertiesByArgumentName {
                    acceptedProperties[argumentName] = schemaGeminiAccepts(from: argumentSchema)
                }
                acceptedSchemaObject[keyword] = acceptedProperties
            } else {
                acceptedSchemaObject[keyword] = schemaGeminiAccepts(from: keywordValue)
            }
        }
        return acceptedSchemaObject
    }

    /// Gemini 3 models think before answering by default, and that thinking is paid out of
    /// `maxOutputTokens`: measured 2026-10-11 on gemini-3.8-flash, a 40-token limit was spent
    /// entirely on 37 thought tokens and the answer came back empty (finishReason MAX_TOKENS).
    /// The ask lane is short and spoken, so Gemini 3 is asked to think little ("low"; "minimal" is
    /// refused by 3.8-flash). Even "low" still spends 30-40 thought tokens on a one-word image
    /// question, so a limit of a few dozen tokens is still too small; the ask lane's 1024 is fine.
    /// Older models are left to their defaults: 2.5 takes `thinkingBudget` instead, and 2.5 Pro
    /// refuses a budget of 0.
    static func thinkingConfig(forModel model: String) -> [String: Any]? {
        guard model.hasPrefix("gemini-3") else { return nil }
        return ["thinkingLevel": "low"]
    }

    /// The request body as a JSON object (kept separate from the URLRequest so tests can read it).
    static func makeRequestBody(model: String, answerRequest: AnswerRequest) -> [String: Any] {
        var contents: [[String: Any]] = []
        for earlierTurn in answerRequest.history {
            contents.append(["role": "user", "parts": [["text": earlierTurn.user]]])
            contents.append(["role": "model", "parts": [["text": earlierTurn.assistant]]])
        }

        // Each screenshot, then its label (which display, and the pixel size the [POINT:…]
        // coordinates are measured against), then the question itself.
        var currentUserParts: [[String: Any]] = []
        for labelledImage in answerRequest.images {
            currentUserParts.append(["inlineData": ["mimeType": labelledImage.mediaType, "data": labelledImage.data.base64EncodedString()]])
            currentUserParts.append(["text": labelledImage.label])
        }
        currentUserParts.append(["text": answerRequest.userPrompt])
        contents.append(["role": "user", "parts": currentUserParts])

        var generationConfig: [String: Any] = ["maxOutputTokens": answerRequest.maxTokens]
        if let thinkingConfig = thinkingConfig(forModel: model) {
            generationConfig["thinkingConfig"] = thinkingConfig
        }
        var body: [String: Any] = [
            "contents": contents,
            "generationConfig": generationConfig,
        ]
        if !answerRequest.systemPrompt.isEmpty {
            body["systemInstruction"] = ["parts": [["text": answerRequest.systemPrompt]]]
        }
        if !answerRequest.tools.isEmpty {
            let functionDeclarations: [[String: Any]] = answerRequest.tools.map { quickActionTool in
                [
                    "name": quickActionTool.name,
                    "description": quickActionTool.description,
                    "parameters": schemaGeminiAccepts(from: AnswerProviderHTTPSupport.inputSchemaObject(from: quickActionTool.inputSchemaJSON)),
                ]
            }
            body["tools"] = [["functionDeclarations": functionDeclarations]]
        }
        return body
    }

    static func makeURLRequest(apiKey: String, model: String, answerRequest: AnswerRequest) throws -> URLRequest {
        guard let url = streamGenerateContentURL(model: model) else {
            throw AnswerProviderError(source: .gemini, status: 0, message: "\"\(model)\" is not a model name Gemini can be asked for")
        }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.timeoutInterval = 120
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        urlRequest.setValue(apiKey, forHTTPHeaderField: "x-goog-api-key")
        urlRequest.httpBody = try JSONSerialization.data(withJSONObject: makeRequestBody(model: model, answerRequest: answerRequest))
        return urlRequest
    }

    // MARK: stream

    /// The answer in a whole list of SSE lines (what the streaming loop builds line by line).
    static func parseStream(lines: [String]) throws -> AnswerResult {
        var streamAccumulator = GeminiStreamAccumulator()
        for line in lines { _ = try streamAccumulator.consume(line: line) }
        return streamAccumulator.result()
    }
}

/// Reads a Gemini SSE stream one line at a time. Every `data:` line is a whole
/// GenerateContentResponse; its first candidate's parts are text (shown as it comes) or a complete
/// `functionCall`. Gemini gives a call no id, so each gets "gemini-<n>" in the order it came.
nonisolated struct GeminiStreamAccumulator {
    /// An `error` object inside the stream after the 200. A safety block (`promptFeedback.blockReason`,
    /// or a safety `finishReason`) is a `SafetyBlockError` instead.
    struct StreamRefusal: Error, Equatable {
        let message: String
    }

    private(set) var accumulatedText = ""
    private(set) var finishReason: String?
    private var toolCalls: [ClaudeToolCall] = []

    /// The text pieces this line carried, in order (usually one, sometimes none).
    mutating func consume(line: String) throws -> [String] {
        guard let dataPayload = AnswerProviderHTTPSupport.sseDataPayload(fromLine: line),
              let event = (try? JSONSerialization.jsonObject(with: Data(dataPayload.utf8))) as? [String: Any] else {
            return []
        }
        if let streamError = event["error"] as? [String: Any] {
            throw StreamRefusal(message: streamError["message"] as? String ?? "the stream broke off")
        }
        // The question itself was blocked: every blockReason (SAFETY, BLOCKLIST, PROHIBITED_CONTENT, OTHER…) is Gemini's filter.
        if let promptFeedback = event["promptFeedback"] as? [String: Any],
           let blockReason = promptFeedback["blockReason"] as? String {
            throw SafetyBlockError(reason: "Gemini \(blockReason)")
        }
        guard let firstCandidate = (event["candidates"] as? [[String: Any]])?.first else { return [] }
        if let candidateFinishReason = firstCandidate["finishReason"] as? String {
            finishReason = candidateFinishReason
            // The answer was stopped partway: what came is cut off mid-thought, so none of it stands as the answer.
            if SafetyBlock.isGeminiSafetyFinish(candidateFinishReason) { throw SafetyBlockError(reason: "Gemini \(candidateFinishReason)") }
        }
        guard let content = firstCandidate["content"] as? [String: Any],
              let parts = content["parts"] as? [[String: Any]] else { return [] }

        var textChunks: [String] = []
        for part in parts {
            // A thinking model can be asked to include its thoughts; they are never the answer.
            if part["thought"] as? Bool == true { continue }
            if let functionCall = part["functionCall"] as? [String: Any],
               let functionName = functionCall["name"] as? String, !functionName.isEmpty {
                let argumentsObject = functionCall["args"] as? [String: Any] ?? [:]
                toolCalls.append(ClaudeToolCall(
                    id: functionCall["id"] as? String ?? "gemini-\(toolCalls.count + 1)",
                    name: functionName,
                    arguments: AnswerProviderHTTPSupport.toolArguments(fromJSONObject: argumentsObject)
                ))
                continue
            }
            if let textChunk = part["text"] as? String, !textChunk.isEmpty {
                accumulatedText += textChunk
                textChunks.append(textChunk)
            }
        }
        return textChunks
    }

    func result() -> AnswerResult {
        AnswerResult(text: accumulatedText, toolCalls: toolCalls)
    }
}
