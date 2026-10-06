//
//  SarvamSpeechClientTests.swift
//  OpenClickyTests
//
//  Request shapes and message decoding for Sarvam's REST and streaming speech APIs, with no
//  network. The live check is `--openclicky-smoke-transcribe`.
//

import Foundation
import Testing
@testable import OpenClicky

struct SarvamSpeechClientTests {
    @Test func theTranscriptionRequestIsMultipartWithTheKeyInAHeader() {
        let wav = Data([1, 2, 3, 4])
        let request = SarvamSpeechClient.transcriptionRequest(
            wav: wav, language: DictationLanguage.named("ml-IN"), keyterms: ["FlowsXR"], key: " key123\n", host: SarvamSpeechClient.host, boundary: "B")
        #expect(request.url?.absoluteString == "https://api.sarvam.ai/speech-to-text")
        #expect(request.value(forHTTPHeaderField: "api-subscription-key") == "key123")
        let body = String(decoding: request.httpBody ?? Data(), as: UTF8.self)
        #expect(body.contains("name=\"language_code\"\r\n\r\nml-IN"))
        #expect(body.contains("name=\"model\"\r\n\r\nsaaras:v4"))
        #expect(body.contains("name=\"keyterms\"\r\n\r\nFlowsXR"))
        #expect(body.contains("filename=\"take.wav\""))
        #expect(body.hasSuffix("\r\n--B--\r\n"))
    }

    @Test func autoDetectIsSentAsUnknown() {
        #expect(SarvamSpeechClient.languageCode(for: .auto) == "unknown")
        #expect(SarvamSpeechClient.languageCode(for: DictationLanguage.named("hi-IN")) == "hi-IN")
    }

    @Test func theStreamingSocketCarriesLanguageModelAndRate() {
        let request = SarvamSpeechClient.streamingRequest(language: DictationLanguage.named("hi-IN"), keyterms: ["Kochi", "FlowsXR"], key: "k")
        let url = request.url!.absoluteString
        #expect(url.hasPrefix("wss://api.sarvam.ai/speech-to-text/ws?"))
        #expect(url.contains("language-code=hi-IN"))
        #expect(url.contains("model=saaras:v4"))
        #expect(url.contains("sample_rate=16000"))
        #expect(url.contains("keyterms=Kochi,FlowsXR"))
        #expect(request.value(forHTTPHeaderField: "Api-Subscription-Key") == "k")
    }

    @Test func audioMessagesAreBase64WAVAt16k() throws {
        let message = SarvamSpeechClient.streamingAudioMessage(pcm16: Data([0, 0, 1, 0]))
        let object = try #require(JSONSerialization.jsonObject(with: Data(message.utf8)) as? [String: Any])
        let audio = try #require(object["audio"] as? [String: String])
        #expect(audio["sample_rate"] == "16000")
        #expect(audio["encoding"] == "audio/wav")
        let wav = try #require(Data(base64Encoded: audio["data"] ?? ""))
        #expect(String(decoding: wav.prefix(4), as: UTF8.self) == "RIFF")
        #expect(wav.count == 44 + 4)
    }

    @Test func serverMessagesDecode() {
        #expect(SarvamSpeechClient.decodeStreamingMessage("{\"type\":\"data\",\"data\":{\"transcript\":\"namaste\",\"language_code\":\"hi-IN\"}}") == .transcript("namaste"))
        #expect(SarvamSpeechClient.decodeStreamingMessage("{\"type\":\"error\",\"data\":{\"error\":\"bad key\",\"code\":\"x\"}}") == .error("bad key"))
        #expect(SarvamSpeechClient.decodeStreamingMessage("{\"type\":\"events\",\"data\":{\"signal_type\":\"START_SPEECH\"}}") == .other)
        #expect(SarvamSpeechClient.decodeStreamingMessage("not json") == .other)
    }

    @Test func refusalsAreToldApart() {
        #expect(SarvamSpeechError.refusal(status: 403, body: Data()) == .keyRefused)
        #expect(SarvamSpeechError.refusal(status: 429, body: Data("{\"error\":{\"code\":\"insufficient_quota_error\"}}".utf8)) == .outOfCredits)
        #expect(SarvamSpeechError.refusal(status: 429, body: Data()) == .busy)
        #expect(SarvamSpeechError.refusal(status: 500, body: Data("{\"error\":{\"message\":\"boom\"}}".utf8)) == .refused(status: 500, message: "boom"))
    }

    @Test func theChatRequestIsOpenAIShaped() throws {
        let request = SarvamSpeechClient.chatRequest(system: "s", user: "u", maxTokens: 10, key: "k", host: SarvamSpeechClient.host)
        #expect(request.url?.absoluteString == "https://api.sarvam.ai/v1/chat/completions")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer k")
        let body = try #require(JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(body["model"] as? String == "sarvam-105b")
        #expect((body["messages"] as? [[String: String]])?.map { $0["role"] } == ["system", "user"])
    }

    @Test func longTakesAreSplitIntoPiecesInOrder() {
        let pcm = Data(repeating: 7, count: 16_000 * 2 * 60 + 10)
        let pieces = SarvamUploadSession.split(pcm16: pcm, sampleRate: 16_000, pieceSeconds: 28)
        #expect(pieces.count == 3)
        #expect(pieces[0].count == 16_000 * 2 * 28)
        #expect(pieces[2].count == 16_000 * 2 * 4 + 10)
        #expect(pieces.reduce(0) { $0 + $1.count } == pcm.count)
    }

    @Test func peakSeesTheLoudestSample() {
        var pcm = Data()
        for sample in [Int16(12), Int16(-900), Int16(300)] { withUnsafeBytes(of: sample.littleEndian) { pcm.append(contentsOf: $0) } }
        #expect(SarvamUploadSession.peak(of: pcm) == 900)
    }
}

struct SarvamRealtimeClientTests {
    @Test func theRealtimeSocketUsesManualEndpointingAndRawPCM() {
        let request = SarvamSpeechClient.realtimeRequest(language: .auto, keyterms: [], key: "k")
        let url = request.url!.absoluteString
        #expect(url.hasPrefix("wss://api.sarvam.ai/speech-to-text-realtime/ws?"))
        #expect(url.contains("language_code=auto"))
        #expect(url.contains("model=saaras:v3-realtime"))
        #expect(url.contains("endpointing=manual"))
        #expect(url.contains("encoding=linear16"))
        #expect(request.value(forHTTPHeaderField: "Api-Subscription-Key") == "k")
    }

    @Test func realtimeMessagesEncodeAndDecode() throws {
        let audio = try #require(JSONSerialization.jsonObject(with: Data(SarvamSpeechClient.realtimeAudioMessage(pcm16: Data([1, 0])).utf8)) as? [String: String])
        #expect(audio["event"] == "audio_input")
        #expect(audio["audio"] == Data([1, 0]).base64EncodedString())
        #expect(SarvamSpeechClient.realtimeEventMessage("flush") == "{\"event\":\"flush\"}")
        #expect(SarvamSpeechClient.decodeRealtimeMessage("{\"event\":\"transcript.partial\",\"utterance_idx\":0,\"text\":\"tech week\"}") == .partial("tech week"))
        #expect(SarvamSpeechClient.decodeRealtimeMessage("{\"event\":\"transcript.final\",\"utterance_idx\":0,\"text\":\"Tech Week starts.\"}") == .final("Tech Week starts."))
        #expect(SarvamSpeechClient.decodeRealtimeMessage("{\"event\":\"error\",\"code\":\"invalid_config\",\"is_fatal\":false,\"message\":\"no\"}") == .error("no", fatal: false))
        #expect(SarvamSpeechClient.decodeRealtimeMessage("{\"event\":\"session.end\",\"request_id\":\"r\"}") == .sessionEnd)
        #expect(SarvamSpeechClient.decodeRealtimeMessage("{\"event\":\"pong\"}") == .other)
    }
}
