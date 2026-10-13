//
//  ProviderRouteTests.swift
//  OpenClickyTests
//
//  Own keys are the primary route (issue #4): every capability × own key / account / neither, the
//  direct requests (shaped like the backend's own-key path), and the 401 rules for a Mac with no
//  account. Nothing here reads shell.json or any file.
//

import Foundation
import Testing
@testable import OpenClicky

struct ProviderRouteTests {
    private let openAIOnly = ProviderCredentials(openAIKey: "sk-user", anthropicKey: nil, accountToken: nil)
    private let anthropicOnly = ProviderCredentials(openAIKey: nil, anthropicKey: "ak-user", accountToken: nil)
    private let bothKeys = ProviderCredentials(openAIKey: "sk-user", anthropicKey: "ak-user", accountToken: nil)
    private let accountOnly = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: "tok")
    private let openAIAndAccount = ProviderCredentials(openAIKey: "sk-user", anthropicKey: nil, accountToken: "tok")
    private let nothing = ProviderCredentials(openAIKey: " ", anthropicKey: "", accountToken: "\n")

    private let openAICapabilities: [ProviderCapability] = [.openAIChat, .speech, .transcription, .realtime]

    // MARK: the decision

    @Test func anOwnOpenAIKeyGoesStraightToOpenAI() {
        for capability in openAICapabilities {
            #expect(ProviderRouter.route(for: capability, credentials: openAIOnly) == .openAIDirect)
            #expect(ProviderRouter.route(for: capability, credentials: openAIAndAccount) == .openAIDirect)
        }
    }

    @Test func anOwnAnthropicKeyGoesStraightToAnthropic() {
        #expect(ProviderRouter.route(for: .claude, credentials: anthropicOnly) == .anthropicDirect)
        #expect(ProviderRouter.route(for: .claude, credentials: bothKeys) == .anthropicDirect)
    }

    @Test func anAccountCoversWhatNoOwnKeyDoes() {
        for capability in ProviderCapability.allCases {
            #expect(ProviderRouter.route(for: capability, credentials: accountOnly) == .backend)
        }
        // Signed in with an OpenAI key too: questions run on the key (it can answer now), not the credits.
        #expect(ProviderRouter.route(for: .claude, credentials: openAIAndAccount) == .answerProvider(.openAI))
        #expect(ProviderRouter.route(for: .assemblyAIStreaming, credentials: openAIAndAccount) == .backend)
    }

    @Test func withNeitherTheRouteSaysWhichKeyWouldDo() {
        for capability in openAICapabilities {
            #expect(ProviderRouter.route(for: capability, credentials: nothing) == .unavailable(ProviderRouter.needsOpenAIKeySentence))
            #expect(ProviderRouter.route(for: capability, credentials: anthropicOnly) == .unavailable(ProviderRouter.needsOpenAIKeySentence))
        }
        #expect(ProviderRouter.route(for: .claude, credentials: nothing) == .unavailable(ProviderRouter.needsAnthropicKeySentence))
        // An OpenAI key answers questions itself now (AnswerProvider), no Anthropic key needed.
        #expect(ProviderRouter.route(for: .claude, credentials: openAIOnly) == .answerProvider(.openAI))
        // AssemblyAI is minted on OpenClicky's key: an own key never covers it.
        #expect(ProviderRouter.route(for: .assemblyAIStreaming, credentials: bothKeys) == .unavailable(ProviderRouter.assemblyAINeedsAccountSentence))
    }

    /// Automatic order: own keys (Anthropic, then OpenRouter, OpenAI, Gemini, xAI, Mistral, Groq),
    /// then the plans switched on, then OpenClicky's credits.
    @Test func automaticAnswersWithTheFirstSourceThatCan() {
        let account = "acct"
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: account, geminiKey: "g")) == .answerProvider(.gemini))
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: "o", anthropicKey: nil, accountToken: nil, openRouterKey: "r")) == .answerProvider(.openRouter))
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: nil, anthropicKey: "a", accountToken: nil, openRouterKey: "r")) == .anthropicDirect)
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: account, codexPlanOn: true)) == .answerProvider(.codexPlan))
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil, claudeCodePlanOn: true, codexPlanOn: true)) == .answerProvider(.claudeCodePlan))
        #expect(ProviderRouter.route(for: .claude, credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: account)) == .backend)
    }

    /// A source the person picked answers when it can; one that cannot falls back to automatic.
    @Test func aPickedSourceWinsWhenItCanAnswer() {
        let pickedGroq = ProviderCredentials(openAIKey: nil, anthropicKey: "a", accountToken: nil, groqKey: "q", preferredAnswerSource: .groq)
        #expect(ProviderRouter.route(for: .claude, credentials: pickedGroq) == .answerProvider(.groq))
        let pickedCreditsSignedIn = ProviderCredentials(openAIKey: nil, anthropicKey: "a", accountToken: "acct", preferredAnswerSource: .openClickyAccount)
        #expect(ProviderRouter.route(for: .claude, credentials: pickedCreditsSignedIn) == .backend)
        let pickedMissing = ProviderCredentials(openAIKey: nil, anthropicKey: "a", accountToken: nil, preferredAnswerSource: .mistral)
        #expect(ProviderRouter.route(for: .claude, credentials: pickedMissing) == .anthropicDirect)
    }

    /// Only questions move: speech, dictation clean-up and the rest still need OpenAI or the account.
    @Test func otherKeysOnlyAnswerQuestions() {
        let geminiOnly = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil, geminiKey: "g")
        #expect(ProviderRouter.route(for: .speech, credentials: geminiOnly) == .unavailable(ProviderRouter.needsOpenAIKeySentence))
        #expect(ProviderRouter.route(for: .openAIChat, credentials: geminiOnly) == .unavailable(ProviderRouter.needsOpenAIKeySentence))
    }

    /// The island names who is answering: the source behind each route that can answer.
    @Test func eachAnsweringRouteNamesItsSource() {
        #expect(ProviderRoute.anthropicDirect.answeringSource?.shortName == "Claude")
        #expect(ProviderRoute.answerProvider(.claudeCodePlan).answeringSource?.shortName == "Claude Code")
        #expect(ProviderRoute.backend.answeringSource?.shortName == "free credits")
        #expect(ProviderRoute.unavailable("no").answeringSource == nil)
        #expect(ProviderRoute.openAIDirect.answeringSource == nil)
    }

    /// The Agents tab says the tool is missing instead of env's "No such file or directory".
    @Test func aMissingAgentToolIsToldApartFromOneThatRuns() {
        #expect(OpenClickyAgentError.toolResolves("sh", path: "/nowhere:/bin"))
        #expect(OpenClickyAgentError.toolResolves("/bin/sh", path: nil))
        #expect(!OpenClickyAgentError.toolResolves("openclicky-not-a-real-tool", path: "/bin:/usr/bin"))
        #expect(!OpenClickyAgentError.toolResolves("openclicky", path: ""))
    }

    /// Quick actions reach the other providers: an Anthropic tool becomes an AnswerRequest tool
    /// with its schema intact; one without a name is dropped.
    @Test func anthropicToolsBecomeAnswerTools() throws {
        let tool: [String: Any] = ["name": "open_app", "description": "Open an app", "input_schema": ["type": "object", "properties": ["name": ["type": "string"]], "required": ["name"]]]
        let converted = try #require(ClaudeAPI.answerTool(fromAnthropicTool: tool))
        #expect(converted.name == "open_app")
        #expect(converted.description == "Open an app")
        let schema = try #require(try JSONSerialization.jsonObject(with: converted.inputSchemaJSON) as? [String: Any])
        #expect((schema["required"] as? [String]) == ["name"])
        #expect(ClaudeAPI.answerTool(fromAnthropicTool: ["description": "no name"]) == nil)
    }

    @Test func blankValuesCountAsMissing() {
        #expect(nothing.openAIKey == nil && nothing.anthropicKey == nil && nothing.accountToken == nil)
        #expect(ProviderCredentials(openAIKey: " sk-user\n", anthropicKey: nil, accountToken: nil).openAIKey == "sk-user")
    }

    // MARK: the direct requests

    private func json(_ request: URLRequest) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: request.httpBody ?? Data())) as? [String: Any] ?? [:]
    }

    @Test func claudeGoesToAnthropicWithTheKeyAndVersion() {
        let request = DirectProviderRequests.anthropicMessages(key: "ak-user")
        #expect(request.url?.absoluteString == "https://api.anthropic.com/v1/messages")
        #expect(request.value(forHTTPHeaderField: "x-api-key") == "ak-user")
        #expect(request.value(forHTTPHeaderField: "anthropic-version") == "2023-06-01")
        #expect(request.value(forHTTPHeaderField: "Authorization") == nil)
    }

    @Test func speechIsOpenAISpeechAsTheBackendAsksForIt() throws {
        let request = try DirectProviderRequests.openAISpeech(key: "sk-user", text: "hello")
        #expect(request.url?.absoluteString == "https://api.openai.com/v1/audio/speech")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer sk-user")
        let body = json(request)
        #expect(body["model"] as? String == "gpt-4o-mini-tts")
        #expect(body["voice"] as? String == "marin")
        #expect(body["input"] as? String == "hello")
        #expect(body["response_format"] as? String == "mp3")
    }

    @Test func transcriptionIsTheMultipartUploadTheBackendBuilds() throws {
        let request = DirectProviderRequests.openAITranscription(key: "sk-user", wav: Data("RIFFdata".utf8), language: "en", prompt: "OpenClicky", boundary: "b")
        #expect(request.url?.absoluteString == "https://api.openai.com/v1/audio/transcriptions")
        #expect(request.value(forHTTPHeaderField: "Content-Type") == "multipart/form-data; boundary=b")
        let body = String(decoding: try #require(request.httpBody), as: UTF8.self)
        #expect(body.contains("name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\nRIFFdata\r\n"))
        #expect(body.contains("name=\"model\"\r\n\r\ngpt-4o-mini-transcribe\r\n"))
        #expect(body.contains("name=\"language\"\r\n\r\nen\r\n"))
        #expect(body.contains("name=\"prompt\"\r\n\r\nOpenClicky\r\n"))
        #expect(body.hasSuffix("--b--\r\n"))
    }

    @Test func realtimeMintsTheClientSecretAtOpenAI() throws {
        let request = try DirectProviderRequests.openAIRealtimeSecret(key: "sk-user", instructions: "be brief", voice: "marin")
        #expect(request.url?.absoluteString == "https://api.openai.com/v1/realtime/client_secrets")
        let body = json(request)
        #expect((body["expires_after"] as? [String: Any])?["seconds"] as? Int == 600)
        let session = try #require(body["session"] as? [String: Any])
        #expect(session["type"] as? String == "realtime")
        #expect(session["model"] as? String == "gpt-realtime")
        #expect(session["instructions"] as? String == "be brief")
        #expect(((session["audio"] as? [String: Any])?["output"] as? [String: Any])?["voice"] as? String == "marin")
    }

    @Test func polishIsHaikuWithTheBackendsCeiling() throws {
        let request = try DirectProviderRequests.anthropicPolish(key: "ak-user", system: "tidy", text: "hi there")
        let body = json(request)
        #expect(body["model"] as? String == "claude-haiku-4-5")
        #expect(body["max_tokens"] as? Int == 8 * 2 + 64)
        let system = try #require((body["system"] as? [[String: Any]])?.first)
        #expect(system["text"] as? String == "tidy")
        #expect((system["cache_control"] as? [String: Any])?["type"] as? String == "ephemeral")
    }

    @Test func aPolishCutOffAtMaxTokensIsNeverPasted() {
        let whole = Data(#"{"content":[{"type":"text","text":" Hi. "}],"stop_reason":"end_turn"}"#.utf8)
        let cut = Data(#"{"content":[{"type":"text","text":"Hi"}],"stop_reason":"max_tokens"}"#.utf8)
        #expect(DirectProviderRequests.anthropicReplyText(from: whole) == "Hi.")
        #expect(DirectProviderRequests.anthropicReplyText(from: cut) == nil)
    }

    @Test func theAnthropicPolisherSendsToAnthropicAndReadsTheText() async throws {
        let polisher = AnthropicTakePolisher(anthropicKey: "ak-user", send: { request in
            #expect(request.url?.host == "api.anthropic.com")
            let reply = Data(#"{"content":[{"type":"text","text":"Hello."}],"stop_reason":"end_turn"}"#.utf8)
            return (reply, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        })
        #expect(try await polisher.polish(system: "tidy", user: "hello") == "Hello.")
    }

    // MARK: a 401 on a Mac with no account

    @Test func a401ForARequestWithNoTokenSignsNobodyOut() async throws {
        var request = URLRequest(url: URL(string: "https://backend.test/v1/messages")!)
        request.httpMethod = "POST"
        let refusal = Data(#"{"error":"missing Authorization: Bearer <token>"}"#.utf8)
        let handling = BackendSignIn.SignInRejectionHandling(
            handleRejection: { _ in Issue.record("a request with no token has no sign-in to handle"); return .signedOut },
            currentToken: { nil },
            signOutExpired: { Issue.record("nobody may be signed out") })
        let answer = try await BackendSignIn.send(request, using: { _ in
            (refusal, HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!)
        }, signIn: handling)
        #expect((answer.1 as? HTTPURLResponse)?.statusCode == 401)
    }
}
