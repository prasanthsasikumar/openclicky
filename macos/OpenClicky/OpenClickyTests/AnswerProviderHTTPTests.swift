//
//  AnswerProviderHTTPTests.swift
//  OpenClickyTests
//
//  The two HTTP answer providers: the OpenAI-compatible chat request (images as data URLs, history,
//  tools as functions, the OpenRouter headers, the token-limit field) and Gemini's (inlineData,
//  user/model turns, functionDeclarations with the schema keywords Gemini rejects removed); the
//  streams they read (text deltas, tool calls split over many chunks, mid-stream errors); and the
//  vendor's own message on a refusal. Canned lines through an injected transport: no network, no
//  files.
//

import Foundation
import Testing
@testable import OpenClicky

/// Records the request a provider sent and answers with canned lines.
private final class CannedTransport: @unchecked Sendable {
    private let lock = NSLock()
    private var recordedRequests: [URLRequest] = []
    let statusCode: Int
    let bodyLines: [String]

    init(statusCode: Int = 200, bodyLines: [String]) {
        self.statusCode = statusCode
        self.bodyLines = bodyLines
    }

    var sentRequest: URLRequest? {
        lock.lock(); defer { lock.unlock() }
        return recordedRequests.last
    }

    private func record(_ request: URLRequest) {
        lock.withLock { recordedRequests.append(request) }
    }

    var transport: AnswerHTTPTransport {
        { [self] request in
            record(request)
            let cannedLines = bodyLines
            let stream = AsyncThrowingStream<String, Error> { continuation in
                for line in cannedLines { continuation.yield(line) }
                continuation.finish()
            }
            return AnswerHTTPResponseLines(statusCode: statusCode, bodyLines: stream)
        }
    }
}

/// Collects the text chunks a provider reports.
private final class ChunkCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var collected: [String] = []
    var chunks: [String] { lock.lock(); defer { lock.unlock() }; return collected }
    func append(_ chunk: String) { lock.lock(); collected.append(chunk); lock.unlock() }
}

private func jsonObject(_ data: Data?) -> [String: Any] {
    guard let data, let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return [:] }
    return object
}

struct AnswerProviderHTTPTests {
    private let pngBytes = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02])

    private var pointAtToolSchema: Data {
        Data("""
        {"$schema":"http://json-schema.org/draft-07/schema#","type":"object","additionalProperties":false,
         "properties":{"target":{"type":"string","description":"what to point at"},
                       "examples":{"type":"object","additionalProperties":false,"properties":{"inner":{"type":"string"}}},
                       "steps":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"}}}}},
         "required":["target"]}
        """.utf8)
    }

    private var sampleRequest: AnswerRequest {
        AnswerRequest(
            images: [AnswerRequest.Image(data: pngBytes, label: "screen 1 of 1 (1512x982)", mediaType: "image/png")],
            systemPrompt: "You are Clicky.",
            history: [AnswerRequest.Turn(user: "earlier question", assistant: "earlier answer")],
            userPrompt: "what is this?",
            tools: [AnswerRequest.Tool(name: "point_at", description: "Point at something", inputSchemaJSON: pointAtToolSchema)],
            maxTokens: 512
        )
    }

    // MARK: OpenAI-compatible request

    @Test func theChatRequestCarriesSystemHistoryImagesAndPrompt() throws {
        let urlRequest = try OpenAIChatAnswerProvider.makeURLRequest(source: .groq, apiKey: "gsk-test", model: "llama", answerRequest: sampleRequest)
        #expect(urlRequest.url?.absoluteString == "https://api.groq.com/openai/v1/chat/completions")
        #expect(urlRequest.httpMethod == "POST")
        #expect(urlRequest.value(forHTTPHeaderField: "Authorization") == "Bearer gsk-test")
        #expect(urlRequest.value(forHTTPHeaderField: "HTTP-Referer") == nil)

        let body = jsonObject(urlRequest.httpBody)
        #expect(body["model"] as? String == "llama")
        #expect(body["stream"] as? Bool == true)
        #expect(body["max_tokens"] as? Int == 512)
        #expect(body["max_completion_tokens"] == nil)

        let messages = try #require(body["messages"] as? [[String: Any]])
        #expect(messages.count == 4)
        #expect(messages[0]["role"] as? String == "system")
        #expect(messages[0]["content"] as? String == "You are Clicky.")
        #expect(messages[1]["role"] as? String == "user")
        #expect(messages[1]["content"] as? String == "earlier question")
        #expect(messages[2]["role"] as? String == "assistant")
        #expect(messages[2]["content"] as? String == "earlier answer")

        let currentParts = try #require(messages[3]["content"] as? [[String: Any]])
        #expect(currentParts.count == 3)
        #expect(currentParts[0]["type"] as? String == "image_url")
        let imageURL = (currentParts[0]["image_url"] as? [String: Any])?["url"] as? String
        #expect(imageURL == "data:image/png;base64,\(pngBytes.base64EncodedString())")
        #expect(currentParts[1]["text"] as? String == "screen 1 of 1 (1512x982)")
        #expect(currentParts[2]["text"] as? String == "what is this?")
    }

    @Test func toolsBecomeFunctionsWithTheirSchemaUntouched() throws {
        let body = OpenAIChatAnswerProvider.makeRequestBody(source: .openAI, model: "gpt-4.1", answerRequest: sampleRequest)
        let tools = try #require(body["tools"] as? [[String: Any]])
        #expect(tools.count == 1)
        #expect(tools[0]["type"] as? String == "function")
        let function = try #require(tools[0]["function"] as? [String: Any])
        #expect(function["name"] as? String == "point_at")
        #expect(function["description"] as? String == "Point at something")
        let parameters = try #require(function["parameters"] as? [String: Any])
        #expect(parameters["additionalProperties"] as? Bool == false)
        #expect((parameters["required"] as? [String]) == ["target"])
    }

    @Test func noToolsMeansNoToolsField() {
        var withoutTools = sampleRequest
        withoutTools.tools = []
        #expect(OpenAIChatAnswerProvider.makeRequestBody(source: .mistral, model: "m", answerRequest: withoutTools)["tools"] == nil)
        #expect(GeminiAnswerProvider.makeRequestBody(model: "gemini-2.5-flash", answerRequest: withoutTools)["tools"] == nil)
    }

    @Test func openCodeZenGoesToItsOwnChatEndpointWithAModelThatSeesImages() throws {
        let urlRequest = try OpenAIChatAnswerProvider.makeURLRequest(source: .openCode, apiKey: "sk-zen", model: AnswerSource.openCode.defaultModel ?? "",
                                                                     answerRequest: sampleRequest)
        #expect(urlRequest.url?.absoluteString == "https://opencode.ai/inference/openai/v1/chat/completions")
        #expect(urlRequest.value(forHTTPHeaderField: "Authorization") == "Bearer sk-zen")
        #expect(AnswerSource.openCode.defaultModel == "kimi-k3")
        #expect(OpenAIChatAnswerProvider.tokenLimitFieldName(for: .openCode) == "max_tokens")
    }

    @Test func openAIItselfGetsMaxCompletionTokens() {
        let body = OpenAIChatAnswerProvider.makeRequestBody(source: .openAI, model: "gpt-5", answerRequest: sampleRequest)
        #expect(body["max_completion_tokens"] as? Int == 512)
        #expect(body["max_tokens"] == nil)
        for compatibleSource in [AnswerSource.openRouter, .groq, .mistral, .xai, .openCode] {
            #expect(OpenAIChatAnswerProvider.tokenLimitFieldName(for: compatibleSource) == "max_tokens")
        }
    }

    @Test func openRouterIsToldWhoIsAsking() throws {
        let urlRequest = try OpenAIChatAnswerProvider.makeURLRequest(source: .openRouter, apiKey: "or-test", model: "anthropic/claude-sonnet-4.6", answerRequest: sampleRequest)
        #expect(urlRequest.url?.host == "openrouter.ai")
        #expect(urlRequest.value(forHTTPHeaderField: "HTTP-Referer") == "https://github.com/prasanthsasikumar/openclicky")
        #expect(urlRequest.value(forHTTPHeaderField: "X-Title") == "OpenClicky")
        #expect(urlRequest.value(forHTTPHeaderField: "Authorization") == "Bearer or-test")
    }

    @Test func aSourceWithoutAChatEndpointIsRefused() {
        #expect(throws: AnswerProviderError.self) {
            _ = try OpenAIChatAnswerProvider.makeURLRequest(source: .gemini, apiKey: "k", model: "m", answerRequest: sampleRequest)
        }
    }

    // MARK: OpenAI-compatible stream

    @Test func textDeltasAreStreamedAndJoined() async throws {
        let canned = CannedTransport(bodyLines: [
            ": OPENROUTER PROCESSING",
            #"data: {"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}"#,
            "",
            #"data: {"choices":[{"index":0,"delta":{"content":"Red"}}]}"#,
            #"data: {"choices":[{"index":0,"delta":{"content":", plainly."}}]}"#,
            #"data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"#,
            "data: [DONE]",
            #"data: {"choices":[{"index":0,"delta":{"content":"after done"}}]}"#,
        ])
        let provider = OpenAIChatAnswerProvider(source: .openAI, apiKey: "sk-test", model: "gpt-4.1", transport: canned.transport)
        let collector = ChunkCollector()
        let result = try await provider.answer(sampleRequest) { collector.append($0) }
        #expect(collector.chunks == ["Red", ", plainly."])
        #expect(result.text == "Red, plainly.")
        #expect(result.toolCalls.isEmpty)
        #expect(canned.sentRequest?.value(forHTTPHeaderField: "Authorization") == "Bearer sk-test")
    }

    @Test func toolCallArgumentsSplitOverChunksAreReassembled() throws {
        let result = try OpenAIChatAnswerProvider.parseStream(lines: [
            #"data: {"choices":[{"delta":{"content":"On it."}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"point_at","arguments":""}}]}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tar"}}]}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"get\": \"Save"}}]}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_def","type":"function","function":{"name":"set_volume","arguments":"{\"level\":"}}]}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":" button\"}"}}]}}]}"#,
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"40, \"loud\": true, \"nested\": {\"a\":1}}"}}]}}]}"#,
            #"data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"#,
            "data: [DONE]",
        ])
        #expect(result.text == "On it.")
        #expect(result.toolCalls == [
            ClaudeToolCall(id: "call_abc", name: "point_at", arguments: ["target": "Save button"]),
            ClaudeToolCall(id: "call_def", name: "set_volume", arguments: ["level": "40", "loud": "1"]),
        ])
    }

    @Test func aToolCallWithBrokenArgumentsIsDropped() throws {
        let result = try OpenAIChatAnswerProvider.parseStream(lines: [
            #"data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"point_at","arguments":"{\"target\": "}}]}}]}"#,
            "data: [DONE]",
        ])
        #expect(result.toolCalls.isEmpty)
    }

    @Test func anErrorInsideTheStreamIsThrownNotSwallowed() async {
        let canned = CannedTransport(bodyLines: [
            #"data: {"choices":[{"delta":{"content":"Half"}}]}"#,
            #"data: {"error":{"message":"Upstream provider overloaded","code":502}}"#,
        ])
        let provider = OpenAIChatAnswerProvider(source: .openRouter, apiKey: "k", model: "m", transport: canned.transport)
        await #expect(throws: AnswerProviderError(source: .openRouter, status: 200, message: "Upstream provider overloaded")) {
            _ = try await provider.answer(sampleRequest) { _ in }
        }
    }

    @Test func aRefusalCarriesTheVendorsMessage() async {
        let canned = CannedTransport(statusCode: 401, bodyLines: [
            "{",
            #"  "error": {"message": "Incorrect API key provided: sk-te**st.", "type": "invalid_request_error", "code": "invalid_api_key"}"#,
            "}",
        ])
        let provider = OpenAIChatAnswerProvider(source: .openAI, apiKey: "sk-test", model: "gpt-4.1", transport: canned.transport)
        await #expect(throws: AnswerProviderError(source: .openAI, status: 401, message: "Incorrect API key provided: sk-te**st.")) {
            _ = try await provider.answer(sampleRequest) { _ in }
        }
    }

    @Test func vendorErrorMessagesInTheirSeveralShapes() {
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: #"{"error":{"message":"nope"}}"#) == "nope")
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: #"[{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}]"#) == "API key not valid.")
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: #"{"message":"Unauthorized","request_id":"x"}"#) == "Unauthorized")
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: #"{"detail":"Model not found"}"#) == "Model not found")
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: "<html>Bad gateway</html>") == "<html>Bad gateway</html>")
        #expect(AnswerProviderHTTPSupport.vendorErrorMessage(fromErrorBody: "  ") == "the request was refused with no explanation")
    }

    // MARK: Gemini request

    @Test func theGeminiRequestKeepsTheKeyInAHeader() throws {
        let urlRequest = try GeminiAnswerProvider.makeURLRequest(apiKey: "AIza-test", model: "gemini-2.5-flash", answerRequest: sampleRequest)
        #expect(urlRequest.url?.absoluteString == "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse")
        #expect(urlRequest.value(forHTTPHeaderField: "x-goog-api-key") == "AIza-test")
        #expect(urlRequest.url?.absoluteString.contains("AIza") == false)
        #expect(urlRequest.value(forHTTPHeaderField: "Authorization") == nil)
    }

    @Test func theGeminiBodyHasSystemTurnsInlineImagesAndPrompt() throws {
        let body = GeminiAnswerProvider.makeRequestBody(model: "gemini-2.5-flash", answerRequest: sampleRequest)
        let systemParts = (body["systemInstruction"] as? [String: Any])?["parts"] as? [[String: Any]]
        #expect(systemParts?.first?["text"] as? String == "You are Clicky.")
        #expect((body["generationConfig"] as? [String: Any])?["maxOutputTokens"] as? Int == 512)

        let contents = try #require(body["contents"] as? [[String: Any]])
        #expect(contents.map { $0["role"] as? String } == ["user", "model", "user"])
        #expect(((contents[0]["parts"] as? [[String: Any]])?.first?["text"] as? String) == "earlier question")
        #expect(((contents[1]["parts"] as? [[String: Any]])?.first?["text"] as? String) == "earlier answer")

        let currentParts = try #require(contents[2]["parts"] as? [[String: Any]])
        #expect(currentParts.count == 3)
        let inlineData = try #require(currentParts[0]["inlineData"] as? [String: Any])
        #expect(inlineData["mimeType"] as? String == "image/png")
        #expect(inlineData["data"] as? String == pngBytes.base64EncodedString())
        #expect(currentParts[1]["text"] as? String == "screen 1 of 1 (1512x982)")
        #expect(currentParts[2]["text"] as? String == "what is this?")
    }

    @Test func geminiToolsAreFunctionDeclarationsWithoutRejectedKeywords() throws {
        let body = GeminiAnswerProvider.makeRequestBody(model: "gemini-2.5-flash", answerRequest: sampleRequest)
        let tools = try #require(body["tools"] as? [[String: Any]])
        let declarations = try #require(tools.first?["functionDeclarations"] as? [[String: Any]])
        #expect(declarations.count == 1)
        #expect(declarations[0]["name"] as? String == "point_at")
        #expect(declarations[0]["description"] as? String == "Point at something")

        let parameters = try #require(declarations[0]["parameters"] as? [String: Any])
        #expect(parameters["$schema"] == nil)
        #expect(parameters["additionalProperties"] == nil)
        #expect(parameters["type"] as? String == "object")
        #expect((parameters["required"] as? [String]) == ["target"])

        let properties = try #require(parameters["properties"] as? [String: Any])
        #expect((properties["target"] as? [String: Any])?["description"] as? String == "what to point at")
        // An argument named like a rejected keyword is still an argument; its own schema is cleaned.
        let examplesArgument = try #require(properties["examples"] as? [String: Any])
        #expect(examplesArgument["additionalProperties"] == nil)
        #expect((examplesArgument["properties"] as? [String: Any])?["inner"] != nil)
        let stepItems = try #require((properties["steps"] as? [String: Any])?["items"] as? [String: Any])
        #expect(stepItems["additionalProperties"] == nil)
        #expect((stepItems["properties"] as? [String: Any])?["x"] != nil)
    }

    @Test func geminiThreeIsAskedToThinkLittleAndOlderModelsAreLeftAlone() {
        let geminiThreeConfig = GeminiAnswerProvider.makeRequestBody(model: "gemini-3.8-flash", answerRequest: sampleRequest)["generationConfig"] as? [String: Any]
        #expect((geminiThreeConfig?["thinkingConfig"] as? [String: Any])?["thinkingLevel"] as? String == "low")
        #expect(geminiThreeConfig?["maxOutputTokens"] as? Int == 512)
        let olderModelConfig = GeminiAnswerProvider.makeRequestBody(model: "gemini-2.5-flash", answerRequest: sampleRequest)["generationConfig"] as? [String: Any]
        #expect(olderModelConfig?["thinkingConfig"] == nil)
    }

    // MARK: Gemini stream

    @Test func geminiTextIsStreamedAndThoughtsAreLeftOut() async throws {
        let canned = CannedTransport(bodyLines: [
            #"data: {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true}],"role":"model"}}]}"#,
            "",
            #"data: {"candidates":[{"content":{"parts":[{"text":"Red"}],"role":"model"}}]}"#,
            "",
            #"data: {"candidates":[{"content":{"parts":[{"text":"."}],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"totalTokenCount":9}}"#,
        ])
        let provider = GeminiAnswerProvider(apiKey: "AIza-test", model: "gemini-2.5-flash", transport: canned.transport)
        let collector = ChunkCollector()
        let result = try await provider.answer(sampleRequest) { collector.append($0) }
        #expect(collector.chunks == ["Red", "."])
        #expect(result.text == "Red.")
        #expect(result.toolCalls.isEmpty)
        #expect(canned.sentRequest?.value(forHTTPHeaderField: "x-goog-api-key") == "AIza-test")
    }

    @Test func geminiFunctionCallsBecomeToolCalls() throws {
        let result = try GeminiAnswerProvider.parseStream(lines: [
            #"data: {"candidates":[{"content":{"parts":[{"text":"Sure. "},{"functionCall":{"name":"point_at","args":{"target":"Save","screen":2}}}],"role":"model"}}]}"#,
            #"data: {"candidates":[{"content":{"parts":[{"functionCall":{"name":"set_volume","args":{"level":40}}}],"role":"model"},"finishReason":"STOP"}]}"#,
        ])
        #expect(result.text == "Sure. ")
        #expect(result.toolCalls == [
            ClaudeToolCall(id: "gemini-1", name: "point_at", arguments: ["target": "Save", "screen": "2"]),
            ClaudeToolCall(id: "gemini-2", name: "set_volume", arguments: ["level": "40"]),
        ])
    }

    // A question Gemini's filter blocked, or an answer it stopped partway, is a safety block: asking again
    // gets the same block, so the ask lane says so plainly instead of "please ask again".
    @Test(arguments: [
        (#"data: {"promptFeedback":{"blockReason":"SAFETY"}}"#, "Gemini SAFETY"),
        (#"data: {"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}"#, "Gemini PROHIBITED_CONTENT"),
        (#"data: {"candidates":[{"content":{"parts":[{"text":"well"}],"role":"model"},"finishReason":"SAFETY"}]}"#, "Gemini SAFETY"),
        (#"data: {"candidates":[{"finishReason":"BLOCKLIST"}]}"#, "Gemini BLOCKLIST"),
    ])
    func aBlockedGeminiQuestionOrAnswerIsASafetyBlock(line: String, reason: String) {
        #expect(throws: SafetyBlockError(reason: reason)) {
            _ = try GeminiAnswerProvider.parseStream(lines: [#"data: {"candidates":[{"content":{"parts":[{"text":"so "}],"role":"model"}}]}"#, line])
        }
    }

    @Test(arguments: ["STOP", "MAX_TOKENS", "RECITATION"])
    func otherGeminiFinishReasonsAreNotASafetyBlock(finishReason: String) throws {
        let result = try GeminiAnswerProvider.parseStream(lines: [
            #"data: {"candidates":[{"content":{"parts":[{"text":"done."}],"role":"model"},"finishReason":""# + finishReason + #""}]}"#,
        ])
        #expect(result.text == "done.")
    }

    // An OpenAI-compatible content filter, or a model that writes its refusal in `refusal`, is a safety block.
    @Test(arguments: [
        (#"data: {"choices":[{"index":0,"delta":{},"finish_reason":"content_filter"}]}"#, "content_filter"),
        (#"data: {"choices":[{"index":0,"delta":{"refusal":"I can't help with that."}}]}"#, "refusal"),
    ])
    func anOpenAIContentFilterIsASafetyBlock(line: String, reason: String) {
        #expect(throws: SafetyBlockError(reason: reason)) {
            _ = try OpenAIChatAnswerProvider.parseStream(lines: [#"data: {"choices":[{"index":0,"delta":{"content":"So "}}]}"#, line, "data: [DONE]"])
        }
    }

    @Test func aGeminiRefusalCarriesItsMessage() async {
        let canned = CannedTransport(statusCode: 400, bodyLines: [
            #"[{"error": {"code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT"}}]"#,
        ])
        let provider = GeminiAnswerProvider(apiKey: "bad", model: "gemini-2.5-flash", transport: canned.transport)
        await #expect(throws: AnswerProviderError(source: .gemini, status: 400, message: "API key not valid. Please pass a valid API key.")) {
            _ = try await provider.answer(sampleRequest) { _ in }
        }
    }
}
