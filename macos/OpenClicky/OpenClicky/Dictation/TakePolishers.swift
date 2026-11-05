//
//  TakePolishers.swift
//  OpenClicky
//
//  The models that tidy a take and make Hey Clicky edits, besides Apple's on-device one (in
//  `AppleModelTakePolisher.swift`, which needs FoundationModels): Sarvam's chat on a Sarvam key,
//  OpenAI on an own OpenAI key or the backend's chat route, Claude Haiku on an own Anthropic key,
//  any other answer key through the ask lane's provider, and the backend's /v1/polish for accounts.
//  `DictationEngineResolver.makePolisher` picks one. Each says honestly who refused: an error
//  blamed on the wrong vendor sends people to the wrong key.
//

import Foundation

/// Why a vendor reached directly on the person's own key did not tidy or edit the words. Neutral
/// on purpose: Anthropic and the other answer keys used to throw the backend's error ("openclicky
/// refused the clean-up") and Hey Clicky threw Sarvam's for any model.
enum TakePolishError: LocalizedError, Equatable {
    /// The vendor answered with a refusal (`status` is its HTTP status, 0 when there was none).
    case refused(vendor: String, status: Int, message: String)
    /// The vendor answered, but there was no text in it.
    case emptyAnswer(vendor: String)
    /// Nothing was sent: the route this polisher needs is not set up (the sentence says what is missing).
    case notSetUp(String)

    var errorDescription: String? {
        switch self {
        case let .refused(vendor, status, message):
            let statusPart = status == 0 ? "" : " (\(status))"
            let trimmedMessage = message.trimmingCharacters(in: .whitespacesAndNewlines)
            return "\(vendor) refused the request\(statusPart)" + (trimmedMessage.isEmpty ? "." : ": \(trimmedMessage)")
        case let .emptyAnswer(vendor):
            return "\(vendor) sent back no text."
        case let .notSetUp(sentence):
            return sentence
        }
    }
}

/// Polishes through Sarvam's chat completions with the user's key.
struct SarvamTakePolisher: TakePolisher {
    let client: SarvamSpeechClient
    var displayName: String { "Sarvam" }
    func polish(system: String, user: String) async throws -> String {
        try await client.complete(system: system, user: user)
    }
}

/// Why the backend's chat route did not polish a take. Its own type: these are OpenClicky's
/// answers, and logging them as Sarvam's ("sarvam refused (401)") sent people to the wrong key.
enum BackendPolishError: LocalizedError, Equatable {
    case invalidBackendURL
    case refused(status: Int, message: String)
    case unreadable

    var errorDescription: String? {
        switch self {
        case .invalidBackendURL: return "the openclicky backend address in settings isn't a valid url."
        case .refused(let status, let message): return "openclicky refused the clean-up (\(status)): \(message)"
        case .unreadable: return "openclicky's clean-up answer had no text in it."
        }
    }
}

/// Polishes through OpenAI's chat completions: straight to OpenAI on the person's own OpenAI key,
/// else through the OpenClicky backend's OpenAI-shaped chat route.
struct BackendTakePolisher: TakePolisher {
    var displayName: String { "OpenClicky" }
    func polish(system: String, user: String) async throws -> String {
        let body: [String: Any] = [
            "model": "gpt-4o-mini",
            "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
            "temperature": 0.2,
        ]
        let data: Data
        let response: URLResponse
        // Which side answered decides whose error a refusal is: OpenAI's own (the person's key) or
        // OpenClicky's (the backend).
        let wentStraightToOpenAI: Bool
        switch ProviderRouter.current(for: .openAIChat) {
        case .openAIDirect:
            // The person's own OpenAI key: the same chat completion, sent to OpenAI itself.
            guard let openAIKey = ProviderRouter.currentCredentials().openAIKey else { throw TakePolishError.notSetUp(ProviderRouter.needsOpenAIKeySentence) }
            var request = DirectProviderRequests.openAIChatCompletions(key: openAIKey)
            request.timeoutInterval = 20
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            (data, response) = try await URLSession.shared.data(for: request)
            wentStraightToOpenAI = true
        case .unavailable(let sentence):
            throw TakePolishError.notSetUp(sentence)
        case .backend, .anthropicDirect, .answerProvider:
            guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/v1/chat/completions") else {
                throw BackendPolishError.invalidBackendURL
            }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 20
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            OpenClickyConfiguration.authorize(&request)
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            // A refused sign-in is refreshed once and sent again (BackendSignIn); what is left here is the backend's answer.
            (data, response) = try await BackendSignIn.send(request) { try await URLSession.shared.data(for: $0) }
            wentStraightToOpenAI = false
        }
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let message = String(decoding: data.prefix(200), as: UTF8.self)
            if wentStraightToOpenAI { throw TakePolishError.refused(vendor: "openai", status: status, message: message) }
            throw BackendPolishError.refused(status: status, message: message)
        }
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let choices = answer["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String else {
            if wentStraightToOpenAI { throw TakePolishError.emptyAnswer(vendor: "openai") }
            throw BackendPolishError.unreadable
        }
        return content
    }
}

// MARK: - Accounts

/// Polish and Hey Clicky edits for signed-in accounts failed: a limit (typed, so the take still
/// pastes with the local clean-up) or the backend unavailable.
enum AccountPolishError: Error, Equatable {
    case limit(AccountLimitError)
    case unavailable(Int)
}

extension AccountPolishError: LocalizedError {
    /// A plain sentence for the orb and Hey Clicky edits, never "error 0".
    var errorDescription: String? {
        if case .limit(let limit) = self, !limit.message.isEmpty { return limit.message }
        if case .unavailable(503) = self { return AccountLimitError.serviceTroubleMessage }
        return "couldn't reach openclicky right now."
    }
}

/// Polish and Hey Clicky edits for signed-in accounts: the app says what to do and sends the
/// words; the backend picks the model (a cheap one) and bills the grant.
struct AccountTakePolisher: TakePolisher {
    var purpose: String = "polish"
    var send: (URLRequest) async throws -> (Data, URLResponse) = { try await URLSession.shared.data(for: $0) }
    /// What a 401 does to the session (refresh once, sign out on the second); injectable for tests.
    var signIn: BackendSignIn.SignInRejectionHandling = .live
    /// Puts the stored sign-in on the request; injectable so tests carry a token of their own
    /// (a request sent without one gets its 401 back, never a refresh).
    var authorize: (inout URLRequest) -> Void = { OpenClickyConfiguration.authorize(&$0) }

    var displayName: String { "OpenClicky" }

    func polish(system: String, user: String) async throws -> String {
        guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/v1/polish") else { throw AccountPolishError.unavailable(0) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        authorize(&request)
        request.httpBody = try JSONSerialization.data(withJSONObject: ["purpose": purpose, "system": system, "text": user])
        // A refused sign-in is refreshed once and sent again; a second refusal throws
        // `BackendSignInError.expired`, whose sentence is the sign-in one, not "couldn't reach".
        let (data, response) = try await BackendSignIn.send(request, using: send, signIn: signIn)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if let limit = AccountLimitError.from(status: status, body: data) { throw AccountPolishError.limit(limit) }
        guard (200..<300).contains(status),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw AccountPolishError.unavailable(status) }
        return text
    }
}

// MARK: - Own keys

/// Polish and edits on the person's own Anthropic key, straight to Anthropic: the request the
/// backend's /v1/polish sends upstream (polish.ts), on Claude Haiku. No account involved.
struct AnthropicTakePolisher: TakePolisher {
    let anthropicKey: String
    var send: @Sendable (URLRequest) async throws -> (Data, URLResponse) = { try await URLSession.shared.data(for: $0) }

    var displayName: String { "Claude (your key)" }

    func polish(system: String, user: String) async throws -> String {
        var request = try DirectProviderRequests.anthropicPolish(key: anthropicKey, system: system, text: user)
        request.timeoutInterval = 20
        let (data, response) = try await send(request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            AppLog.append("polish (own anthropic key): anthropic answered \(status)")
            throw TakePolishError.refused(vendor: "anthropic", status: status, message: String(decoding: data.prefix(200), as: UTF8.self))
        }
        guard let text = DirectProviderRequests.anthropicReplyText(from: data) else {
            AppLog.append("polish (own anthropic key): the reply had no whole text")
            throw TakePolishError.emptyAnswer(vendor: "anthropic")
        }
        return text
    }
}

/// Polish and edits on any other answer key the person added (OpenRouter, Gemini, Groq, Mistral,
/// xAI, OpenCode Zen), straight to that vendor through the ask lane's own provider. A quick model where the
/// default answer model is a slow one: the clean-up gives up after `TakeFormatter.modelTimeoutSeconds`.
/// The two plans are left out: starting their CLI alone takes longer than that.
struct AnswerKeyTakePolisher: TakePolisher {
    let source: AnswerSource
    let credentials: ProviderCredentials

    /// The keys that can polish, in the order they are tried; the fast ones first.
    static let sourcesInOrder: [AnswerSource] = [.groq, .gemini, .mistral, .openRouter, .xai, .openCode]

    /// The model asked to tidy a take, where it differs from the answer model.
    static func polishModel(for source: AnswerSource) -> String? {
        switch source {
        case .openRouter: return "openai/gpt-4.1-mini"
        case .mistral: return "mistral-small-latest"
        case .openCode: return "glm-5.3-flash"
        default: return nil
        }
    }

    /// The first source with a key, nil without any.
    static func firstAvailable(credentials: ProviderCredentials) -> AnswerKeyTakePolisher? {
        sourcesInOrder.first { credentials.key(for: $0) != nil }.map { AnswerKeyTakePolisher(source: $0, credentials: credentials) }
    }

    var displayName: String { "\(source.shortName) (your key)" }

    func polish(system: String, user: String) async throws -> String {
        var models: [String: String] = [:]
        if let model = Self.polishModel(for: source) { models[source.rawValue] = model }
        let provider = try await AnswerProviderFactory.make(for: source, credentials: credentials, modelOverrides: models)
        let request = AnswerRequest(images: [], systemPrompt: system, history: [], userPrompt: user, tools: [], maxTokens: 1024)
        let vendorName = source.shortName.lowercased()
        do {
            let result = try await provider.answer(request, onTextChunk: { _ in })
            let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { throw TakePolishError.emptyAnswer(vendor: vendorName) }
            return text
        } catch let error as AnswerProviderError {
            AppLog.append("polish (own \(source.rawValue) key): \(error.status)")
            throw TakePolishError.refused(vendor: vendorName, status: error.status, message: error.message)
        }
    }
}

// MARK: - Saying why the clean-up was skipped

/// Why a take was pasted with only the local clean-up, said once per session (every take hitting
/// the same limit would otherwise repeat it). The orb still says "cleaned up locally"; this is the
/// reason behind it, shown as the companion's quiet caption. Nil for a reason not worth saying
/// (a timeout, a passing server error): the log has those.
enum PolishFailureNotice {
    static func sentence(for error: Error) -> String? {
        switch error {
        case BackendSignInError.expired:
            // Signing out already said so, on the account page and in a caption.
            return nil
        case AccountPolishError.limit(let limit):
            return limit.message.isEmpty ? nil : limit.message
        case AccountPolishError.unavailable(503):
            return AccountLimitError.serviceTroubleMessage
        case BackendPolishError.refused(let status, _) where status == 401 || status == 403:
            // The backend relays a refusal of the person's own provider key with its status.
            return ownKeyRefusedSentence
        case TakePolishError.refused(_, let status, _) where status == 401 || status == 403:
            // A vendor reached directly is only ever reached on the person's own key.
            return ownKeyRefusedSentence
        default:
            return nil
        }
    }

    static let ownKeyRefusedSentence = "your own api key was refused, so your words were cleaned up locally. check the key in settings, keys & plans."

    /// Sentences already shown this session.
    @MainActor private static var shownSentences: Set<String> = []

    /// Returns the sentence when it is new this session (and marks it shown), nil otherwise.
    @MainActor static func sentenceToShow(for error: Error, alreadyShown: inout Set<String>) -> String? {
        guard let sentence = sentence(for: error), !alreadyShown.contains(sentence) else { return nil }
        alreadyShown.insert(sentence)
        return sentence
    }

    @MainActor static func note(_ error: Error) {
        guard let sentence = sentenceToShow(for: error, alreadyShown: &shownSentences) else { return }
        AccountProfileStore.shared.showNotice?(sentence)
    }
}
