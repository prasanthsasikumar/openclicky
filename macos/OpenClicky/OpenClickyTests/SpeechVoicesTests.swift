//
//  SpeechVoicesTests.swift
//  OpenClickyTests
//
//  Who speaks the answers (SpeechVoices.swift): what the picker offers, which voice Automatic and
//  a missing key end up with, each vendor's request, Amazon's request signing against AWS's own
//  test vector, and Kokoro's speakers and WAV output. No network.
//

import Foundation
import Testing
@testable import OpenClicky

struct SpeechVoicesTests {
    private func settings(_ edit: (inout OpenClickyShellSettings) -> Void = { _ in }) -> OpenClickyShellSettings {
        var settings = OpenClickyShellSettings()
        edit(&settings)
        return settings
    }

    @Test func olderStoredValuesMeanAutomatic() {
        #expect(SpeechVoiceChoice(stored: nil) == .automatic)
        #expect(SpeechVoiceChoice(stored: "cloud") == .automatic)
        #expect(SpeechVoiceChoice(stored: "mac") == .mac)
        #expect(SpeechVoiceChoice(stored: "kokoro") == .kokoro)
        #expect(SpeechVoiceChoice.automatic.storedValue == nil)
        #expect(SpeechVoiceChoice.polly.storedValue == "polly")
    }

    @Test func automaticPrefersOwnKeysThenTheFreeVoiceThenOfflineVoices() {
        func resolve(_ shell: OpenClickyShellSettings, account: Bool = false, kokoro: Bool = false) -> SpeechVoiceChoice {
            SpeechVoiceChoice.resolve(settings: shell, hasAccount: account, kokoroInstalled: kokoro, languageCode: "en")
        }
        #expect(resolve(settings()) == .mac)
        #expect(resolve(settings(), kokoro: true) == .kokoro)
        // The account's ElevenLabs voice comes before Kokoro, even once Kokoro is downloaded.
        #expect(resolve(settings(), account: true, kokoro: true) == .account)
        #expect(resolve(settings(), account: true) == .account)
        #expect(resolve(settings { $0.sarvamKey = "s" }, account: true) == .sarvam)
        #expect(resolve(settings { $0.geminiApiKey = "g"; $0.sarvamKey = "s" }) == .sarvam)
        #expect(resolve(settings { $0.geminiApiKey = "g" }, account: true) == .gemini)
        #expect(resolve(settings { $0.elevenLabsApiKey = "e"; $0.sarvamKey = "s" }) == .elevenLabs)
        #expect(resolve(settings { $0.openaiApiKey = "o"; $0.elevenLabsApiKey = "e" }) == .openAI)
        #expect(resolve(settings { $0.amazonPollyCredentials = "AKIA:secret" }) == .polly)
    }

    @Test func aPickedVoiceThatCannotSpeakFallsBackToThisMac() {
        let pickedGoogle = settings { $0.speechVoice = "google" }
        #expect(SpeechVoiceChoice.resolve(settings: pickedGoogle, hasAccount: true, kokoroInstalled: false, languageCode: "en") == .mac)
        let pickedKokoro = settings { $0.speechVoice = "kokoro" }
        #expect(SpeechVoiceChoice.resolve(settings: pickedKokoro, hasAccount: false, kokoroInstalled: false, languageCode: "en") == .mac)
        #expect(SpeechVoiceChoice.resolve(settings: pickedKokoro, hasAccount: false, kokoroInstalled: true, languageCode: "en") == .kokoro)
        // Kokoro doesn't speak Tamil; Sarvam does.
        #expect(SpeechVoiceChoice.resolve(settings: pickedKokoro, hasAccount: false, kokoroInstalled: true, languageCode: "ta") == .mac)
        let pickedSarvam = settings { $0.speechVoice = "sarvam"; $0.sarvamKey = "s" }
        #expect(SpeechVoiceChoice.resolve(settings: pickedSarvam, hasAccount: false, kokoroInstalled: false, languageCode: "ta") == .sarvam)
    }


    @Test func kokoroInALanguageItDoesntSpeakCarriesOnAsAutomatic() {
        let kokoro = settings { $0.speechVoice = "kokoro" }
        #expect(SpeechVoiceChoice.resolve(settings: kokoro, hasAccount: true, kokoroInstalled: true, languageCode: "ta") == .account)
    }

    @Test func thePickerOffersTheVoicesThereAreKeysForAndKeepsThePickedOne() {
        let bare = SpeechVoiceChoice.offered(settings: settings(), hasAccount: false, languageCode: "en")
        #expect(bare == [.automatic, .kokoro, .mac])
        let withKeys = SpeechVoiceChoice.offered(settings: settings { $0.elevenLabsApiKey = "e"; $0.googleCloudApiKey = "g" }, hasAccount: true, languageCode: "en")
        #expect(withKeys == [.automatic, .account, .elevenLabs, .google, .kokoro, .mac])
        let pickedWithoutKey = SpeechVoiceChoice.offered(settings: settings { $0.speechVoice = "polly" }, hasAccount: false, languageCode: "en")
        #expect(pickedWithoutKey.contains(.polly))
    }

    @Test func amazonCredentialsAreOneLineWithAnOptionalRegion() {
        #expect(AWSCredentials(stored: "AKIA123:abc/def+ghi") == AWSCredentials(accessKeyID: "AKIA123", secretAccessKey: "abc/def+ghi", region: "us-east-1"))
        #expect(AWSCredentials(stored: " AKIA123 : secret : ap-south-1\n")?.region == "ap-south-1")
        #expect(AWSCredentials(stored: "AKIA123") == nil)
        #expect(AWSCredentials(stored: nil) == nil)
    }

    /// AWS's Signature Version 4 test suite, "get-vanilla".
    @Test func signingMatchesAWSsOwnTestVector() {
        var request = URLRequest(url: URL(string: "https://example.amazonaws.com/")!)
        request.httpMethod = "GET"
        let credentials = AWSCredentials(accessKeyID: "AKIDEXAMPLE", secretAccessKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", region: "us-east-1")
        AWSSignatureV4.sign(&request, credentials: credentials, service: "service", date: Date(timeIntervalSince1970: 1_440_938_160))
        #expect(request.value(forHTTPHeaderField: "X-Amz-Date") == "20150830T123600Z")
        #expect(request.value(forHTTPHeaderField: "Authorization") ==
                "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31")
    }

    @Test func pollyAsksForANeuralVoiceInTheRegionAndSignsForPolly() throws {
        let credentials = AWSCredentials(accessKeyID: "AKIA1", secretAccessKey: "s", region: "eu-west-1")
        let request = try CloudVoiceRequests.pollySpeech(credentials: credentials, text: "hi", voiceID: "Joanna")
        #expect(request.url?.absoluteString == "https://polly.eu-west-1.amazonaws.com/v1/speech")
        let body = try #require(try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any])
        #expect(body["Engine"] as? String == "neural")
        #expect(body["VoiceId"] as? String == "Joanna")
        #expect(request.value(forHTTPHeaderField: "Authorization")?.contains("/eu-west-1/polly/aws4_request, SignedHeaders=content-type;host;x-amz-date,") == true)
        #expect(CloudVoiceRequests.pollyVoice(forLanguage: "hi") == "Kajal")
        #expect(CloudVoiceRequests.pollyVoice(forLanguage: "ta") == nil)
    }

    @Test func elevenLabsAndGoogleRequestsCarryTheKeyInAHeader() throws {
        let eleven = try CloudVoiceRequests.elevenLabsSpeech(key: "sk_1", text: "hi")
        #expect(eleven.url?.path == "/v1/text-to-speech/\(CloudVoiceRequests.elevenLabsVoiceID)")
        #expect(eleven.value(forHTTPHeaderField: "xi-api-key") == "sk_1")
        let google = try CloudVoiceRequests.googleSpeech(key: "AIza1", text: "hi", languageCode: "hi")
        #expect(google.value(forHTTPHeaderField: "X-Goog-Api-Key") == "AIza1")
        #expect(google.url?.query == nil)
        let body = try #require(try JSONSerialization.jsonObject(with: google.httpBody ?? Data()) as? [String: Any])
        #expect((body["voice"] as? [String: Any])?["languageCode"] as? String == "hi-IN")
        let audio = Data("mp3".utf8)
        let answer = try JSONSerialization.data(withJSONObject: ["audioContent": audio.base64EncodedString()])
        #expect(try CloudVoiceRequests.decodeGoogleSpeech(answer) == audio)
    }

    // The account's free voice (/tts, relayed to ElevenLabs) and an own ElevenLabs key ask for the
    // same model and settings, from one body builder.
    @Test func theFreeVoiceAndAnOwnElevenLabsKeySendTheSameBody() throws {
        let body = try #require(try JSONSerialization.jsonObject(with: CloudVoiceRequests.elevenLabsSpeechBody(text: "hi")) as? [String: Any])
        #expect(body["text"] as? String == "hi")
        #expect(body["model_id"] as? String == "eleven_flash_v2_5")
        let voiceSettings = try #require(body["voice_settings"] as? [String: Any])
        #expect(voiceSettings["stability"] as? Double == 0.5)
        #expect(voiceSettings["similarity_boost"] as? Double == 0.75)
        let ownKeyRequest = try CloudVoiceRequests.elevenLabsSpeech(key: "sk_1", text: "hi")
        let ownKeyBody = try #require(try JSONSerialization.jsonObject(with: ownKeyRequest.httpBody ?? Data()) as? NSDictionary)
        #expect(ownKeyBody == body as NSDictionary)
    }

    @Test func geminiAudioComesBackAsWAVWhateverItWasSentAs() throws {
        func answer(_ mimeType: String, _ audio: Data) throws -> Data {
            try JSONSerialization.data(withJSONObject: ["candidates": [["content": ["parts": [["inlineData": ["mimeType": mimeType, "data": audio.base64EncodedString()]]]]]]])
        }
        let wav = Data("RIFF....WAVE".utf8)
        #expect(try CloudVoiceRequests.decodeGeminiSpeech(answer("audio/wav", wav)) == wav)
        let pcm = Data(repeating: 1, count: 480)
        let wrapped = try CloudVoiceRequests.decodeGeminiSpeech(answer("audio/L16;codec=pcm;rate=24000", pcm))
        #expect(wrapped.prefix(4) == Data("RIFF".utf8))
        #expect(wrapped.count == pcm.count + 44)
        let request = try CloudVoiceRequests.geminiSpeech(key: "g", text: "hi")
        #expect(request.value(forHTTPHeaderField: "x-goog-api-key") == "g")
    }

    @Test func kokoroSpeaksTheReplyLanguageWithThePickedSpeakerWhenItFits() {
        #expect(KokoroVoice.speaker(forLanguage: "en", picked: nil)?.name == "Heart")
        #expect(KokoroVoice.speaker(forLanguage: "en", picked: 26)?.name == "George")
        // A British speaker picked for English doesn't carry over to Hindi.
        #expect(KokoroVoice.speaker(forLanguage: "hi", picked: 26)?.languageCode == "hi")
        #expect(KokoroVoice.speaker(forLanguage: "ta", picked: nil) == nil)
    }

    @Test func kokoroSamplesBecomeA16BitWAV() {
        let samples: [Float] = [0, 0.5, -0.5, 2]
        let wav = samples.withUnsafeBufferPointer { KokoroEngine.wav(samples: $0, sampleRate: 24_000) }
        #expect(wav.prefix(4) == Data("RIFF".utf8))
        #expect(wav.count == 44 + samples.count * 2)
        // Out-of-range samples are clipped, not wrapped.
        let last = wav.suffix(2).withUnsafeBytes { $0.loadUnaligned(as: Int16.self) }
        #expect(last == Int16.max)
    }
}
