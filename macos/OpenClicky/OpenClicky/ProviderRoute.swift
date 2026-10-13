//
//  ProviderRoute.swift
//  OpenClicky
//
//  Own keys are the primary route (issue #4). A capability the person has a key for goes straight
//  to that vendor with their key; the OpenClicky backend is only for a capability they have no key
//  for, on their account's token, and then without any own key: a key never leaves this Mac except
//  to its own vendor. Every caller asks `ProviderRouter` the same question, so there is one place
//  that decides, and one log line per change of route.
//
//  The direct requests are the ones the backend's bring-your-own-key path sends upstream
//  (backend/src/proxy.ts, keys.ts, skillsCreate.ts), so a request means the same thing either way.
//

import Foundation

/// What a call is for, by the key that can pay for it.
nonisolated enum ProviderCapability: String, CaseIterable, Sendable {
    /// Claude: the ⌃⌥ ask lane (/chat) and screen grounding. Anthropic key.
    case claude
    /// OpenAI chat completions: the take clean-up on an own key. OpenAI key.
    case openAIChat
    /// OpenAI speech on an own key, else the account's /tts. No call asks the router for it any
    /// more: who speaks the answers is `SpeechVoiceChoice` (SpeechVoices.swift), which also covers
    /// the other voice keys, Kokoro and this Mac. Kept because the route table is tested per key.
    case speech
    /// Upload speech-to-text (/agent/transcribe). OpenAI key.
    case transcription
    /// Realtime voice: the client secret (/agent/realtime/session). OpenAI key.
    case realtime
    /// AssemblyAI streaming (/transcribe-token): minted on OpenClicky's AssemblyAI key, account only.
    case assemblyAIStreaming
}

nonisolated enum ProviderRoute: Equatable, Sendable {
    case openAIDirect
    case anthropicDirect
    /// Questions answered by another source (AnswerProvider.swift): an OpenAI-compatible key,
    /// Gemini, or a Claude Code / Codex plan. Only ever the route for `.claude`.
    case answerProvider(AnswerSource)
    /// The OpenClicky backend, on the account's token (no own keys sent).
    case backend
    /// No key for this and no account: the sentence says what is missing.
    case unavailable(String)

    var logName: String {
        switch self {
        case .openAIDirect: return "openai, own key"
        case .anthropicDirect: return "anthropic, own key"
        case .answerProvider(let source): return source.displayName
        case .backend: return "openclicky backend, account"
        case .unavailable: return "unavailable"
        }
    }

    /// The source a `.claude` route answers with, for saying who answered; nil when nothing can.
    var answeringSource: AnswerSource? {
        switch self {
        case .anthropicDirect: return .anthropic
        case .answerProvider(let source): return source
        case .backend: return .openClickyAccount
        case .openAIDirect, .unavailable: return nil
        }
    }

    var unavailableSentence: String? {
        if case .unavailable(let sentence) = self { return sentence }
        return nil
    }
}

/// The keys and token a route is decided on. Values only: no key ever goes into a log.
nonisolated struct ProviderCredentials: Equatable, Sendable {
    var openAIKey: String?
    var anthropicKey: String?
    var accountToken: String?
    /// The other keys that can answer questions, and the plans the person switched on.
    var openRouterKey: String?
    var geminiKey: String?
    var groqKey: String?
    var mistralKey: String?
    var xaiKey: String?
    var claudeCodePlanOn = false
    var codexPlanOn = false
    /// Who should answer, when the person picked one (nil: automatic).
    var preferredAnswerSource: AnswerSource?

    init(openAIKey: String?, anthropicKey: String?, accountToken: String?,
         openRouterKey: String? = nil, geminiKey: String? = nil, groqKey: String? = nil,
         mistralKey: String? = nil, xaiKey: String? = nil,
         claudeCodePlanOn: Bool = false, codexPlanOn: Bool = false, preferredAnswerSource: AnswerSource? = nil) {
        func cleaned(_ value: String?) -> String? {
            let trimmed = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        self.openAIKey = cleaned(openAIKey)
        self.anthropicKey = cleaned(anthropicKey)
        self.accountToken = cleaned(accountToken)
        self.openRouterKey = cleaned(openRouterKey)
        self.geminiKey = cleaned(geminiKey)
        self.groqKey = cleaned(groqKey)
        self.mistralKey = cleaned(mistralKey)
        self.xaiKey = cleaned(xaiKey)
        self.claudeCodePlanOn = claudeCodePlanOn
        self.codexPlanOn = codexPlanOn
        self.preferredAnswerSource = preferredAnswerSource
    }

    init(settings: OpenClickyShellSettings) {
        self.init(openAIKey: settings.openaiApiKey, anthropicKey: settings.anthropicApiKey, accountToken: settings.token,
                  openRouterKey: settings.openRouterApiKey, geminiKey: settings.geminiApiKey, groqKey: settings.groqApiKey,
                  mistralKey: settings.mistralApiKey, xaiKey: settings.xaiApiKey,
                  claudeCodePlanOn: settings.useClaudeCodePlan == true, codexPlanOn: settings.useCodexPlan == true,
                  preferredAnswerSource: settings.answerSource.flatMap(AnswerSource.init(rawValue:)))
    }

    /// The key for an API source, nil for the plans and the account.
    func key(for source: AnswerSource) -> String? {
        switch source {
        case .anthropic: return anthropicKey
        case .openAI: return openAIKey
        case .openRouter: return openRouterKey
        case .gemini: return geminiKey
        case .groq: return groqKey
        case .mistral: return mistralKey
        case .xai: return xaiKey
        case .claudeCodePlan, .codexPlan, .openClickyAccount: return nil
        }
    }

    /// Whether `source` can answer with what is set up: its key, its plan switched on, or an account.
    func canAnswer(with source: AnswerSource) -> Bool {
        switch source {
        case .claudeCodePlan: return claudeCodePlanOn
        case .codexPlan: return codexPlanOn
        case .openClickyAccount: return accountToken != nil
        default: return key(for: source) != nil
        }
    }

    /// The sources that can answer, in the order "automatic" tries them: own keys (Anthropic first,
    /// then OpenRouter, which reaches the most models), then the plans switched on, then the account.
    var answerSourcesInOrder: [AnswerSource] {
        let order: [AnswerSource] = [.anthropic, .openRouter, .openAI, .gemini, .xai, .mistral, .groq, .claudeCodePlan, .codexPlan, .openClickyAccount]
        return order.filter(canAnswer(with:))
    }
}

nonisolated enum ProviderRouter {
    /// No key and no account: the free account is the way in for most people, so it comes first.
    static let needsOpenAIKeySentence = "i can do that once you add your email for a free account, in settings, account. your own openai key works too."
    static let needsAnthropicKeySentence = "i can answer that once you add your email for a free account, in settings, account. a key from openai, anthropic, openrouter, gemini and others works too."
    /// The backend's own-key path answers `byok_missing_anthropic_key` here; this is that, said plainly.
    static let assemblyAINeedsAccountSentence = "assemblyai runs on openclicky's own key, so it needs an openclicky account. pick openai or offline dictation instead."

    /// The route for `capability`: own key → that vendor; no key but an account → the backend;
    /// neither → unavailable, saying which key would do.
    static func route(for capability: ProviderCapability, credentials: ProviderCredentials) -> ProviderRoute {
        switch capability {
        case .claude:
            // The person's pick when it can answer, else the first that can in the automatic order.
            let source = credentials.preferredAnswerSource.flatMap { credentials.canAnswer(with: $0) ? $0 : nil }
                ?? credentials.answerSourcesInOrder.first
            switch source {
            case .anthropic: return .anthropicDirect
            case .openClickyAccount: return .backend
            case .some(let other): return .answerProvider(other)
            case .none: return .unavailable(needsAnthropicKeySentence)
            }
        case .openAIChat, .speech, .transcription, .realtime:
            if credentials.openAIKey != nil { return .openAIDirect }
        case .assemblyAIStreaming:
            break
        }
        if credentials.accountToken != nil { return .backend }
        switch capability {
        case .claude:
            return .unavailable(needsAnthropicKeySentence)
        case .assemblyAIStreaming:
            return .unavailable(assemblyAINeedsAccountSentence)
        case .openAIChat, .speech, .transcription, .realtime:
            return .unavailable(needsOpenAIKeySentence)
        }
    }

    /// The keys and token in shell.json now.
    @MainActor static func currentCredentials() -> ProviderCredentials {
        ProviderCredentials(settings: OpenClickyConfiguration.settings)
    }

    /// The route for `capability` with the settings in shell.json now. Logged when it changes.
    @MainActor static func current(for capability: ProviderCapability) -> ProviderRoute {
        let route = route(for: capability, credentials: currentCredentials())
        noteRoute(route, for: capability)
        return route
    }

    private static let lastRoutesLock = NSLock()
    nonisolated(unsafe) private static var lastRoutes: [ProviderCapability: ProviderRoute] = [:]

    private static func noteRoute(_ route: ProviderRoute, for capability: ProviderCapability) {
        lastRoutesLock.lock()
        let changed = lastRoutes[capability] != route
        lastRoutes[capability] = route
        lastRoutesLock.unlock()
        if changed { AppLog.append("route: \(capability.rawValue) → \(route.logName)") }
    }
}

/// The error a caller throws for an unavailable route; its sentence is the one to show or say.
nonisolated struct ProviderRouteUnavailable: LocalizedError, Equatable {
    let capability: ProviderCapability
    let sentence: String
    var errorDescription: String? { sentence }
}

/// The direct vendor requests, shaped as the backend's own-key path shapes them upstream.
nonisolated enum DirectProviderRequests {
    static let openAIBaseURL = "https://api.openai.com/v1"
    static let anthropicMessagesURL = "https://api.anthropic.com/v1/messages"
    static let anthropicVersion = "2023-06-01"

    /// The backend's BYOK defaults (keys.ts, proxy.ts, backend/.dev.vars.example).
    static let defaultOpenAIModel = "gpt-5.5"
    static let defaultAnthropicModel = "claude-haiku-4-5-20251001"
    static let realtimeModel = "gpt-realtime"
    static let transcriptionModel = "gpt-4o-mini-transcribe"
    static let speechModel = "gpt-4o-mini-tts"
    static let speechVoice = "marin"

    private static func openAIRequest(_ path: String, key: String) -> URLRequest {
        var request = URLRequest(url: URL(string: openAIBaseURL + path)!)
        request.httpMethod = "POST"
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        return request
    }

    /// Anthropic Messages; the body is the caller's (the same one /chat relays).
    static func anthropicMessages(key: String) -> URLRequest {
        var request = URLRequest(url: URL(string: anthropicMessagesURL)!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue(anthropicVersion, forHTTPHeaderField: "anthropic-version")
        return request
    }

    /// The polish model the backend picks (modelPolicy.ts) and its output ceiling (polish.ts).
    static let polishModel = "claude-haiku-4-5"
    static let polishMaxOutputTokens = 4096

    /// Dictation polish or a Hey Clicky edit on Claude Haiku, as /v1/polish sends it upstream.
    static func anthropicPolish(key: String, system: String, text: String) throws -> URLRequest {
        var request = anthropicMessages(key: key)
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "model": polishModel,
            // An upper bound: some scripts take a token or more per character, and a take cut off
            // at max_tokens would be pasted half-finished.
            "max_tokens": min(polishMaxOutputTokens, text.utf16.count * 2 + 64),
            "system": [["type": "text", "text": system, "cache_control": ["type": "ephemeral"]]],
            "messages": [["role": "user", "content": text]],
        ] as [String: Any])
        return request
    }

    /// The text of a non-streamed Messages reply. Nil when there is none, or when it stopped at
    /// max_tokens: half a take must never be pasted as if it were whole.
    static func anthropicReplyText(from data: Data) -> String? {
        guard let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let content = json["content"] as? [[String: Any]] else { return nil }
        if (json["stop_reason"] as? String) == "max_tokens" { return nil }
        let text = content.filter { ($0["type"] as? String) == "text" }.compactMap { $0["text"] as? String }.joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text
    }

    /// OpenAI Chat Completions; the body is the caller's.
    static func openAIChatCompletions(key: String) -> URLRequest {
        var request = openAIRequest("/chat/completions", key: key)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return request
    }

    /// OpenAI speech for `text`, mp3 (synthesizeSpeech in proxy.ts).
    static func openAISpeech(key: String, text: String) throws -> URLRequest {
        var request = openAIRequest("/audio/speech", key: key)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("audio/mpeg", forHTTPHeaderField: "Accept")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "model": speechModel, "voice": speechVoice, "input": text, "response_format": "mp3",
        ])
        return request
    }

    /// The multipart upload the backend builds for /agent/transcribe (transcribeAudio in proxy.ts).
    static func openAITranscription(key: String, wav: Data, language: String?, prompt: String?, boundary: String = "openclicky-\(UUID().uuidString)") -> URLRequest {
        var request = openAIRequest("/audio/transcriptions", key: key)
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n".utf8))
        field("model", transcriptionModel)
        field("response_format", "json")
        if let language, !language.isEmpty { field("language", language) }
        if let prompt, !prompt.isEmpty { field("prompt", prompt) }
        body.append(Data("--\(boundary)--\r\n".utf8))
        request.httpBody = body
        return request
    }

    /// A Realtime client secret (createRealtimeSession in proxy.ts); the answer has the same shape as the backend's.
    static func openAIRealtimeSecret(key: String, instructions: String?, voice: String?, expiresInSeconds: Int = 600) throws -> URLRequest {
        var request = openAIRequest("/realtime/client_secrets", key: key)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        var session: [String: Any] = ["type": "realtime", "model": realtimeModel]
        if let instructions, !instructions.isEmpty { session["instructions"] = instructions }
        if let voice, !voice.isEmpty { session["audio"] = ["output": ["voice": voice]] }
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "expires_after": ["anchor": "created_at", "seconds": expiresInSeconds], "session": session,
        ] as [String: Any])
        return request
    }
}
