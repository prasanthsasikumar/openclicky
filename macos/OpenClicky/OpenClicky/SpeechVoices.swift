//
//  SpeechVoices.swift
//  OpenClicky
//
//  Who speaks the answers. The person picks in Settings → Clicky → Voice from what they have: the
//  free voice on an account, a voice key (OpenAI, ElevenLabs, Google Cloud, Amazon Polly, Sarvam),
//  Kokoro (an open model downloaded only when picked), or this Mac's own voice. Automatic takes the
//  first that can speak, own keys first. Every cloud request goes straight to its vendor from this
//  Mac; none of these keys is ever sent to the OpenClicky backend. Request building is pure so it is
//  tested without a network (SpeechVoicesTests).
//

import CryptoKit
import Foundation

enum SpeechVoiceChoice: String, CaseIterable, Identifiable {
    case automatic
    case account
    case openAI = "openai"
    case elevenLabs = "elevenlabs"
    case google
    case gemini
    case polly
    case sarvam
    case kokoro
    case mac

    var id: String { rawValue }

    /// shell.json's `speechVoice`; nil, "auto" and the older "cloud" are Automatic.
    init(stored: String?) {
        self = stored.flatMap(SpeechVoiceChoice.init(rawValue:)) ?? .automatic
    }

    /// The value written to shell.json: nil for Automatic.
    var storedValue: String? { self == .automatic ? nil : rawValue }

    var title: String {
        switch self {
        case .automatic: return "Automatic"
        case .account: return "Free voice (ElevenLabs)"
        case .openAI: return "OpenAI"
        case .elevenLabs: return "ElevenLabs (your key)"
        case .google: return "Google Cloud"
        case .gemini: return "Google Gemini"
        case .polly: return "Amazon Polly"
        case .sarvam: return "Sarvam"
        case .kokoro: return "Kokoro (on this Mac)"
        case .mac: return "This Mac"
        }
    }

    /// Automatic's order: own keys first (the person chose to pay for them), then the account's
    /// ElevenLabs voice (FlowsXR's grant pays for it, with a generous monthly allowance), then Kokoro
    /// if someone downloaded it, then this Mac. Gemini comes last of the keys: its voice takes about
    /// four seconds a sentence.
    static let automaticOrder: [SpeechVoiceChoice] = [.openAI, .elevenLabs, .google, .polly, .sarvam, .gemini, .account, .kokoro, .mac]

    /// Whether this voice can speak right now in `languageCode` with these settings.
    static func canSpeak(_ choice: SpeechVoiceChoice, settings: OpenClickyShellSettings, hasAccount: Bool,
                         kokoroInstalled: Bool, languageCode: String) -> Bool {
        func has(_ key: String?) -> Bool { !(key?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "").isEmpty }
        switch choice {
        case .automatic: return false
        case .account: return hasAccount
        case .openAI: return has(settings.openaiApiKey)
        case .elevenLabs: return has(settings.elevenLabsApiKey)
        case .google: return has(settings.googleCloudApiKey)
        case .gemini: return has(settings.geminiApiKey)
        case .polly:
            return AWSCredentials(stored: settings.amazonPollyCredentials) != nil && CloudVoiceRequests.pollyVoice(forLanguage: languageCode) != nil
        case .sarvam: return has(settings.sarvamKey) && SarvamSpeechClient.speechLanguageCode(forReplyLanguage: languageCode) != nil
        case .kokoro: return kokoroInstalled && KokoroVoice.speaker(forLanguage: languageCode, picked: nil) != nil
        case .mac: return true
        }
    }

    /// The voices the picker offers: Automatic, every voice there is a key or account for, Kokoro
    /// (picking it downloads it), and this Mac. The one already picked stays listed even if its key
    /// went, so the picker never shows a blank.
    static func offered(settings: OpenClickyShellSettings, hasAccount: Bool, languageCode: String) -> [SpeechVoiceChoice] {
        let picked = SpeechVoiceChoice(stored: settings.speechVoice)
        return allCases.filter { choice in
            switch choice {
            case .automatic, .mac: return true
            case .kokoro: return choice == picked || KokoroVoice.speaker(forLanguage: languageCode, picked: nil) != nil
            default:
                return choice == picked || canSpeak(choice, settings: settings, hasAccount: hasAccount, kokoroInstalled: false, languageCode: languageCode)
            }
        }
    }

    /// The voice that speaks now: the one picked if it can, else this Mac's (a missing key or a
    /// Kokoro still downloading); Automatic, the first in `automaticOrder` that can. Kokoro in a
    /// language it doesn't speak (it became the voice on its own, then the language changed) goes
    /// on as Automatic. While the account voice rests (`AccountVoiceRest`: two slow or failed answers
    /// in a row) Automatic passes it by; a voice picked by hand is never passed by.
    static func resolve(settings: OpenClickyShellSettings, hasAccount: Bool, kokoroInstalled: Bool, languageCode: String,
                        accountVoiceIsResting: Bool = false) -> SpeechVoiceChoice {
        let picked = SpeechVoiceChoice(stored: settings.speechVoice)
        let kokoroCantSpeakLanguage = picked == .kokoro && KokoroVoice.speaker(forLanguage: languageCode, picked: nil) == nil
        let automaticCandidates = accountVoiceIsResting ? automaticOrder.filter { $0 != .account } : automaticOrder
        let candidates = picked == .automatic || kokoroCantSpeakLanguage ? automaticCandidates : [picked]
        return candidates.first {
            canSpeak($0, settings: settings, hasAccount: hasAccount, kokoroInstalled: kokoroInstalled, languageCode: languageCode)
        } ?? .mac
    }

    /// `resolve` with this Mac's settings.
    @MainActor static func current() -> SpeechVoiceChoice {
        resolve(settings: OpenClickyConfiguration.settings, hasAccount: OpenClickyConfiguration.hasAccountToken,
                kokoroInstalled: KokoroVoice.isInstalled, languageCode: ReplyLanguage.currentCode,
                accountVoiceIsResting: AccountVoiceRest.isResting)
    }

    /// The voice that takes over when the account voice is late or fails: Kokoro when it is
    /// downloaded and speaks the language (offline, under a second a sentence once loaded), else this Mac.
    static func fastestLocalVoice(kokoroInstalled: Bool, languageCode: String) -> SpeechVoiceChoice {
        canSpeak(.kokoro, settings: OpenClickyShellSettings(), hasAccount: false, kokoroInstalled: kokoroInstalled,
                 languageCode: languageCode) ? .kokoro : .mac
    }
}

// MARK: - Amazon credentials

/// Amazon Polly's credentials as one pasted line, "accessKeyId:secretKey" or
/// "accessKeyId:secretKey:region" (us-east-1 when left out). Secret keys may contain "/" and "+",
/// never ":".
struct AWSCredentials: Equatable {
    let accessKeyID: String
    let secretAccessKey: String
    let region: String

    init?(stored: String?) {
        let parts = (stored ?? "").trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ":").map {
            $0.trimmingCharacters(in: .whitespaces)
        }
        guard parts.count == 2 || parts.count == 3, !parts[0].isEmpty, !parts[1].isEmpty else { return nil }
        accessKeyID = parts[0]
        secretAccessKey = parts[1]
        region = parts.count == 3 && !parts[2].isEmpty ? parts[2] : "us-east-1"
    }

    init(accessKeyID: String, secretAccessKey: String, region: String) {
        self.accessKeyID = accessKeyID
        self.secretAccessKey = secretAccessKey
        self.region = region
    }
}

/// AWS Signature Version 4 for one request with a body (Polly's SynthesizeSpeech), from
/// docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html.
enum AWSSignatureV4 {
    static func sign(_ request: inout URLRequest, credentials: AWSCredentials, service: String, date: Date = Date()) {
        guard let url = request.url, let host = url.host else { return }
        let amzDate = timestamp(date, format: "yyyyMMdd'T'HHmmss'Z'")
        let dateStamp = timestamp(date, format: "yyyyMMdd")
        let payloadHash = hex(SHA256.hash(data: request.httpBody ?? Data()))
        request.setValue(amzDate, forHTTPHeaderField: "X-Amz-Date")

        // Every header set on the request is signed, lowercased and sorted, plus host.
        var headers: [String: String] = ["host": host]
        for (name, value) in request.allHTTPHeaderFields ?? [:] {
            headers[name.lowercased()] = value.trimmingCharacters(in: .whitespaces)
        }
        let signedHeaderNames = headers.keys.sorted()
        let canonicalHeaders = signedHeaderNames.map { "\($0):\(headers[$0] ?? "")\n" }.joined()
        let signedHeaders = signedHeaderNames.joined(separator: ";")
        let path = url.path.isEmpty ? "/" : url.path
        let query = canonicalQuery(url)
        let canonicalRequest = [request.httpMethod ?? "GET", path, query, canonicalHeaders, signedHeaders, payloadHash].joined(separator: "\n")

        let scope = "\(dateStamp)/\(credentials.region)/\(service)/aws4_request"
        let stringToSign = ["AWS4-HMAC-SHA256", amzDate, scope, hex(SHA256.hash(data: Data(canonicalRequest.utf8)))].joined(separator: "\n")

        var key = SymmetricKey(data: Data("AWS4\(credentials.secretAccessKey)".utf8))
        for part in [dateStamp, credentials.region, service, "aws4_request"] {
            key = SymmetricKey(data: Data(HMAC<SHA256>.authenticationCode(for: Data(part.utf8), using: key)))
        }
        let signature = hex(HMAC<SHA256>.authenticationCode(for: Data(stringToSign.utf8), using: key))
        request.setValue("AWS4-HMAC-SHA256 Credential=\(credentials.accessKeyID)/\(scope), SignedHeaders=\(signedHeaders), Signature=\(signature)",
                         forHTTPHeaderField: "Authorization")
    }

    private static func canonicalQuery(_ url: URL) -> String {
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedQueryItems ?? []
        return items.map { "\($0.name)=\($0.value ?? "")" }.sorted().joined(separator: "&")
    }

    private static func timestamp(_ date: Date, format: String) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = format
        return formatter.string(from: date)
    }

    private static func hex<D: Sequence>(_ digest: D) -> String where D.Element == UInt8 {
        digest.map { String(format: "%02x", $0) }.joined()
    }
}

// MARK: - Requests

enum CloudVoiceRequests {
    // MARK: ElevenLabs (the person's own key)

    /// Sarah, a premade voice: the free tier refuses library voices.
    static let elevenLabsVoiceID = "EXAVITQu4vr4xnSDxMaL"
    static let elevenLabsModel = "eleven_flash_v2_5"

    static func elevenLabsSpeech(key: String, text: String) throws -> URLRequest {
        var request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/text-to-speech/\(elevenLabsVoiceID)?output_format=mp3_44100_128")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue(key, forHTTPHeaderField: "xi-api-key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("audio/mpeg", forHTTPHeaderField: "Accept")
        request.httpBody = try elevenLabsSpeechBody(text: text)
        return request
    }

    /// The ElevenLabs text-to-speech body, for an own key here and for the account's free voice
    /// (the backend's /tts relays it to ElevenLabs), so both ask for the same model and settings.
    static func elevenLabsSpeechBody(text: String) throws -> Data {
        try JSONSerialization.data(withJSONObject: [
            "text": text, "model_id": elevenLabsModel,
            "voice_settings": ["stability": 0.5, "similarity_boost": 0.75],
        ])
    }

    // MARK: Google Cloud Text-to-Speech

    /// The region each reply language is spoken in, and a Neural2 voice where one is chosen; other
    /// languages get Google's default voice for the region.
    static func googleVoice(forLanguage languageCode: String) -> (languageCode: String, name: String?) {
        switch languageCode {
        case "en": return ("en-US", "en-US-Neural2-F")
        case "hi": return ("hi-IN", "hi-IN-Neural2-A")
        case "zh": return ("cmn-CN", nil)
        case "pt": return ("pt-BR", nil)
        case "ja": return ("ja-JP", nil)
        case "ko": return ("ko-KR", nil)
        case "bn", "gu", "kn", "ml", "mr", "ta", "te", "pa": return ("\(languageCode)-IN", nil)
        default: return ("\(languageCode)-\(languageCode.uppercased())", nil)
        }
    }

    static func googleSpeech(key: String, text: String, languageCode: String) throws -> URLRequest {
        var request = URLRequest(url: URL(string: "https://texttospeech.googleapis.com/v1/text:synthesize")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue(key, forHTTPHeaderField: "X-Goog-Api-Key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let voice = googleVoice(forLanguage: languageCode)
        var voiceObject: [String: Any] = ["languageCode": voice.languageCode]
        if let name = voice.name { voiceObject["name"] = name }
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "input": ["text": text], "voice": voiceObject, "audioConfig": ["audioEncoding": "MP3"],
        ])
        return request
    }

    /// Google answers with the MP3 as base64 in `audioContent`.
    static func decodeGoogleSpeech(_ data: Data) throws -> Data {
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let content = answer["audioContent"] as? String,
              let audio = Data(base64Encoded: content), !audio.isEmpty else {
            throw CloudVoiceError.unreadable("Google")
        }
        return audio
    }

    // MARK: Google Gemini (the AI Studio key that also answers questions)

    static let geminiSpeechModel = "gemini-3.8-flash-tts"
    static let geminiVoiceName = "Kore"

    static func geminiSpeech(key: String, text: String) throws -> URLRequest {
        var request = URLRequest(url: URL(string: "https://generativelanguage.googleapis.com/v1beta/models/\(geminiSpeechModel):generateContent")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "contents": [["parts": [["text": text]]]],
            "generationConfig": [
                "responseModalities": ["AUDIO"],
                "speechConfig": ["voiceConfig": ["prebuiltVoiceConfig": ["voiceName": geminiVoiceName]]],
            ],
        ])
        return request
    }

    /// Gemini's audio is inline base64: WAV from the 3.8 models, raw 16-bit PCM ("audio/L16;
    /// rate=24000", little-endian whatever the label says) from older ones, which is wrapped in WAV.
    static func decodeGeminiSpeech(_ data: Data) throws -> Data {
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let candidates = answer["candidates"] as? [[String: Any]],
              let content = candidates.first?["content"] as? [String: Any],
              let parts = content["parts"] as? [[String: Any]],
              let inline = parts.compactMap({ $0["inlineData"] as? [String: Any] }).first,
              let encoded = inline["data"] as? String,
              let audio = Data(base64Encoded: encoded), !audio.isEmpty else {
            throw CloudVoiceError.unreadable("Gemini")
        }
        let mimeType = (inline["mimeType"] as? String ?? "").lowercased()
        guard mimeType.contains("l16") || mimeType.contains("pcm") else { return audio }
        let rate = mimeType.range(of: #"rate=(\d+)"#, options: .regularExpression)
            .flatMap { Int(mimeType[$0].dropFirst("rate=".count)) } ?? 24_000
        return BuddyWAVFileBuilder.buildWAVData(fromPCM16MonoAudio: audio, sampleRate: rate)
    }

    // MARK: Amazon Polly

    /// A neural Polly voice for each reply language it speaks; nil where Polly has none.
    static func pollyVoice(forLanguage languageCode: String) -> String? {
        switch languageCode {
        case "en": return "Joanna"
        case "hi": return "Kajal"
        case "fr": return "Lea"
        case "de": return "Vicki"
        case "es": return "Lucia"
        case "it": return "Bianca"
        case "ja": return "Kazuha"
        case "ko": return "Seoyeon"
        case "pt": return "Camila"
        case "zh": return "Zhiyu"
        default: return nil
        }
    }

    static func pollySpeech(credentials: AWSCredentials, text: String, voiceID: String, date: Date = Date()) throws -> URLRequest {
        var request = URLRequest(url: URL(string: "https://polly.\(credentials.region).amazonaws.com/v1/speech")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "Engine": "neural", "OutputFormat": "mp3", "Text": text, "VoiceId": voiceID,
        ])
        AWSSignatureV4.sign(&request, credentials: credentials, service: "polly", date: date)
        return request
    }
}

enum CloudVoiceError: Error, LocalizedError, Equatable {
    case noKey(String)
    case refused(String, status: Int, message: String)
    case unreadable(String)

    var errorDescription: String? {
        switch self {
        case let .noKey(vendor): return "\(vendor) needs a key under Settings → Keys & Plans."
        case let .refused(vendor, status, message): return "\(vendor) refused (\(status)): \(message)"
        case let .unreadable(vendor): return "\(vendor)'s answer had no audio in it."
        }
    }
}

// MARK: - Speaking

/// The audio for one piece of an answer in a voice that needs no account (the account's voice goes
/// through the backend, in AnswerVoicePlayer). Shared by the answers and the keys' Test buttons.
enum CloudVoiceSpeaker {
    @MainActor
    static func audio(for text: String, voice: SpeechVoiceChoice, settings: OpenClickyShellSettings = OpenClickyConfiguration.settings,
                      languageCode: String = ReplyLanguage.currentCode, session: URLSession = .shared) async throws -> Data {
        func key(_ value: String?, _ vendor: String) throws -> String {
            let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !trimmed.isEmpty else { throw CloudVoiceError.noKey(vendor) }
            return trimmed
        }
        switch voice {
        case .openAI:
            return try await send(DirectProviderRequests.openAISpeech(key: key(settings.openaiApiKey, "OpenAI"), text: text), vendor: "OpenAI", session: session)
        case .elevenLabs:
            return try await send(CloudVoiceRequests.elevenLabsSpeech(key: key(settings.elevenLabsApiKey, "ElevenLabs"), text: text), vendor: "ElevenLabs", session: session)
        case .google:
            let data = try await send(CloudVoiceRequests.googleSpeech(key: key(settings.googleCloudApiKey, "Google"), text: text, languageCode: languageCode), vendor: "Google", session: session)
            return try CloudVoiceRequests.decodeGoogleSpeech(data)
        case .gemini:
            let data = try await send(CloudVoiceRequests.geminiSpeech(key: key(settings.geminiApiKey, "Gemini"), text: text), vendor: "Gemini", session: session)
            return try CloudVoiceRequests.decodeGeminiSpeech(data)
        case .polly:
            guard let credentials = AWSCredentials(stored: settings.amazonPollyCredentials) else { throw CloudVoiceError.noKey("Amazon Polly") }
            guard let voiceID = CloudVoiceRequests.pollyVoice(forLanguage: languageCode) else { throw CloudVoiceError.unreadable("Amazon Polly") }
            return try await send(CloudVoiceRequests.pollySpeech(credentials: credentials, text: text, voiceID: voiceID), vendor: "Amazon Polly", session: session)
        case .sarvam:
            guard let languageCode = SarvamSpeechClient.speechLanguageCode(forReplyLanguage: languageCode) else { throw CloudVoiceError.unreadable("Sarvam") }
            return try await SarvamSpeechClient(key: key(settings.sarvamKey, "Sarvam")).speech(text: text, languageCode: languageCode)
        case .kokoro:
            return try await KokoroVoice.shared.audio(for: text, languageCode: languageCode)
        case .mac, .automatic, .account:
            return try await MacVoice.shared.audio(for: text)
        }
    }

    private static func send(_ request: URLRequest, vendor: String, session: URLSession) async throws -> Data {
        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            throw CloudVoiceError.refused(vendor, status: status, message: String(decoding: data.prefix(300), as: UTF8.self))
        }
        guard !data.isEmpty else { throw CloudVoiceError.unreadable(vendor) }
        return data
    }
}
