//
//  SarvamSpeechClient.swift
//  OpenClicky
//
//  Sarvam's speech and chat APIs with the user's own key: Saaras hears a take (REST, one WAV), the
//  streaming socket hears it as it is spoken, and a chat completion polishes the words. Written
//  from docs.sarvam.ai as read on 2026-10-06; the REST transcription shape is the one Saathi has
//  already run against a real key. Request building is pure so it can be tested without a network.
//

import Foundation

enum SarvamSpeechError: Error, LocalizedError, Equatable {
    case noKey
    case keyRefused
    case outOfCredits
    case busy
    case refused(status: Int, message: String)
    case unreadable(String)
    case unreachable(String)

    var errorDescription: String? {
        switch self {
        case .noKey: return "sarvam needs your key. paste it under settings → engine."
        case .keyRefused: return "sarvam did not accept the key. check it under settings → engine."
        case .outOfCredits: return "sarvam says this key is out of credits (dashboard.sarvam.ai)."
        case .busy: return "sarvam is busy. try again in a moment."
        case let .refused(status, message): return "sarvam refused (\(status)): \(message)"
        case let .unreadable(what): return "sarvam's answer could not be read: \(what)"
        case let .unreachable(reason): return "couldn't reach sarvam: \(reason)"
        }
    }

    /// Sarvam answers a bad key with 403 (sometimes 401), a spent account and a rate limit both with
    /// 429: only `error.code` tells the last two apart.
    static func refusal(status: Int, body: Data) -> SarvamSpeechError {
        let stated = ((try? JSONSerialization.jsonObject(with: body)) as? [String: Any])?["error"] as? [String: Any]
        let code = stated?["code"] as? String ?? ""
        let message = (stated?["message"] as? String) ?? String(decoding: body.prefix(300), as: UTF8.self)
        if code == "insufficient_quota_error" { return .outOfCredits }
        switch status {
        case 401: return .keyRefused
        case 403 where code.isEmpty || code == "invalid_api_key_error" || code == "authentication_error": return .keyRefused
        case 429, 503: return .busy
        default: return .refused(status: status, message: message)
        }
    }
}

struct SarvamSpeechClient: Sendable {
    static let host = URL(string: "https://api.sarvam.ai")!
    static let streamingHost = URL(string: "wss://api.sarvam.ai")!
    /// The REST transcription model; `saaras:v4` is Sarvam's current default.
    static let transcriptionModel = "saaras:v4"
    static let chatModel = "sarvam-105b"
    static let sampleRate = 16_000
    /// One session for every client: nothing cached, no cookies.
    static let session = URLSession(configuration: .ephemeral)

    let key: String
    let host: URL
    let urlSession: URLSession

    init(key: String, host: URL = SarvamSpeechClient.host, urlSession: URLSession = SarvamSpeechClient.session) {
        // A pasted key routinely brings a newline with it, and an untrimmed one is refused in a way
        // indistinguishable from a wrong one.
        self.key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        self.host = host
        self.urlSession = urlSession
    }

    /// The language Sarvam is told to expect: its BCP-47 code, or `unknown` to let it detect.
    static func languageCode(for language: DictationLanguage) -> String {
        language.code == "auto" ? "unknown" : language.code
    }

    // MARK: ears (REST)

    /// What was said in `wav` (PCM16 mono 16 kHz). Up to about thirty seconds per request.
    func transcribe(wav: Data, language: DictationLanguage, keyterms: [String] = []) async throws -> String {
        guard !key.isEmpty else { throw SarvamSpeechError.noKey }
        let data = try await send(Self.transcriptionRequest(wav: wav, language: language, keyterms: keyterms, key: key, host: host))
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let transcript = answer["transcript"] as? String else {
            throw SarvamSpeechError.unreadable("there was no transcript in it")
        }
        return transcript.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func transcriptionRequest(
        wav: Data, language: DictationLanguage, keyterms: [String], key: String, host: URL,
        boundary: String = "openclicky-\(UUID().uuidString)"
    ) -> URLRequest {
        var request = URLRequest(url: host.appendingPathComponent("speech-to-text"))
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue(key.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "api-subscription-key")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        field("model", transcriptionModel)
        field("language_code", languageCode(for: language))
        for keyterm in keyterms.prefix(50) { field("keyterms", keyterm) }
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"take.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        request.httpBody = body
        return request
    }

    // MARK: ears (streaming)

    /// The socket a streaming session opens: `/speech-to-text/ws` with the language, the model and
    /// the sample rate in the query, the key in a header.
    static func streamingRequest(language: DictationLanguage, keyterms: [String], key: String, host: URL = SarvamSpeechClient.streamingHost) -> URLRequest {
        var components = URLComponents(url: host.appendingPathComponent("speech-to-text/ws"), resolvingAgainstBaseURL: false)!
        var items = [
            URLQueryItem(name: "language-code", value: languageCode(for: language)),
            URLQueryItem(name: "model", value: transcriptionModel),
            URLQueryItem(name: "sample_rate", value: String(sampleRate)),
            URLQueryItem(name: "input_audio_codec", value: "wav"),
            URLQueryItem(name: "vad_signals", value: "true"),
        ]
        if !keyterms.isEmpty { items.append(URLQueryItem(name: "keyterms", value: keyterms.prefix(50).joined(separator: ","))) }
        components.queryItems = items
        var request = URLRequest(url: components.url!)
        request.setValue(key.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "Api-Subscription-Key")
        return request
    }

    /// One audio chunk as the socket wants it: a base64 WAV at 16 kHz.
    static func streamingAudioMessage(pcm16: Data) -> String {
        let wav = BuddyWAVFileBuilder.buildWAVData(fromPCM16MonoAudio: pcm16, sampleRate: sampleRate)
        let message: [String: Any] = ["audio": ["data": wav.base64EncodedString(), "sample_rate": String(sampleRate), "encoding": "audio/wav"]]
        return String(decoding: try! JSONSerialization.data(withJSONObject: message), as: UTF8.self)
    }

    static let streamingFlushMessage = "{\"type\":\"flush\"}"

    /// What a server frame means: a piece of transcript, an error, or something to ignore.
    enum StreamingServerMessage: Equatable {
        case transcript(String)
        case error(String)
        case other
    }

    static func decodeStreamingMessage(_ text: String) -> StreamingServerMessage {
        guard let object = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any],
              let type = object["type"] as? String else { return .other }
        let payload = object["data"] as? [String: Any] ?? [:]
        switch type {
        case "data":
            return .transcript((payload["transcript"] as? String) ?? "")
        case "error":
            return .error((payload["error"] as? String) ?? (payload["code"] as? String) ?? "unknown error")
        default:
            return .other
        }
    }

    // MARK: mind

    /// One chat completion: `system` then `user`; the assistant's text comes back.
    func complete(system: String, user: String, maxTokens: Int = 1024) async throws -> String {
        guard !key.isEmpty else { throw SarvamSpeechError.noKey }
        let data = try await send(Self.chatRequest(system: system, user: user, maxTokens: maxTokens, key: key, host: host))
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let choices = answer["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String else {
            throw SarvamSpeechError.unreadable("there was no message in it")
        }
        return content.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func chatRequest(system: String, user: String, maxTokens: Int, key: String, host: URL) -> URLRequest {
        var request = URLRequest(url: host.appendingPathComponent("v1/chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.setValue(key, forHTTPHeaderField: "api-subscription-key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: Any] = [
            "model": chatModel,
            "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
            "temperature": 0.2,
            "max_tokens": maxTokens,
            "reasoning_effort": "low",
        ]
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)
        return request
    }

    // MARK: plumbing

    private func send(_ request: URLRequest) async throws -> Data {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await urlSession.data(for: request)
        } catch {
            throw SarvamSpeechError.unreachable(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else { throw SarvamSpeechError.unreadable("not an HTTP response") }
        guard (200..<300).contains(http.statusCode) else { throw SarvamSpeechError.refusal(status: http.statusCode, body: data) }
        return data
    }
}
