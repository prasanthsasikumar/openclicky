//
//  OpenAIAudioTranscriptionProvider.swift
//  OpenClicky
//
//  AI transcription provider backed by OpenAI's audio transcription API.
//

import AVFoundation
import Foundation

struct OpenAIAudioTranscriptionProviderError: LocalizedError {
    let message: String

    var errorDescription: String? {
        message
    }
}

final class OpenAIAudioTranscriptionProvider: BuddyTranscriptionProvider {
    // Transcription goes through the OpenClicky backend (POST /agent/transcribe), which holds the
    // OpenAI key and picks the model. The app only needs the user's OpenClicky token.
    let displayName = "OpenClicky"
    let requiresSpeechRecognitionPermission = false

    var isConfigured: Bool {
        OpenClickyConfiguration.isConfigured
    }

    var unavailableExplanation: String? {
        guard !isConfigured else { return nil }
        return "OpenClicky transcription needs a token in ~/.openclicky/shell.json."
    }

    func startStreamingSession(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> any BuddyStreamingTranscriptionSession {
        guard isConfigured else {
            throw OpenAIAudioTranscriptionProviderError(
                message: unavailableExplanation ?? "OpenClicky transcription is not configured."
            )
        }

        return OpenAIAudioTranscriptionSession(
            keyterms: keyterms,
            onTranscriptUpdate: onTranscriptUpdate,
            onFinalTranscriptReady: onFinalTranscriptReady,
            onError: onError
        )
    }

    /// On near-silent audio the model sometimes returns its own prompt (verbatim or nearly). A
    /// transcript that is mostly made of the prompt's words is not something the user said.
    static func looksLikeEchoedPrompt(_ transcriptText: String, prompt: String?) -> Bool {
        guard let prompt else { return false }
        let words = { (text: String) -> [String] in
            text.lowercased()
                .components(separatedBy: CharacterSet.alphanumerics.inverted)
                .filter { $0.count > 2 }
        }
        let transcriptWords = words(transcriptText)
        guard transcriptWords.count >= 4 else { return false }
        let promptWords = Set(words(prompt))
        let sharedWordCount = transcriptWords.filter { promptWords.contains($0) }.count
        return Double(sharedWordCount) / Double(transcriptWords.count) >= 0.8
    }
}

private final class OpenAIAudioTranscriptionSession: BuddyStreamingTranscriptionSession {
    let finalTranscriptFallbackDelaySeconds: TimeInterval = 8.0

    private struct TranscriptionResponse: Decodable {
        let text: String
    }

    private static var transcriptionURL: URL { URL(string: "\(OpenClickyConfiguration.backendBaseURL)/agent/transcribe")! }
    private static let targetSampleRate = 16_000

    private let keyterms: [String]
    private let onTranscriptUpdate: (String) -> Void
    private let onFinalTranscriptReady: (String) -> Void
    private let onError: (Error) -> Void

    private let stateQueue = DispatchQueue(label: "com.openclicky.openai.transcription")
    private let audioPCM16Converter = BuddyPCM16AudioConverter(
        targetSampleRate: Double(targetSampleRate)
    )
    private let urlSession: URLSession

    private var bufferedPCM16AudioData = Data()
    /// Loudest sample magnitude seen (0…32767): audio that never rises above the noise floor is
    /// not uploaded, because the model answers silence with its prompt or a made-up phrase.
    private var peakSampleMagnitude: Int16 = 0
    private static let silencePeakThreshold: Int16 = 400
    private var hasRequestedFinalTranscript = false
    private var hasDeliveredFinalTranscript = false
    private var isCancelled = false
    private var transcriptionUploadTask: Task<Void, Never>?

    init(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) {
        self.keyterms = keyterms
        self.onTranscriptUpdate = onTranscriptUpdate
        self.onFinalTranscriptReady = onFinalTranscriptReady
        self.onError = onError

        let urlSessionConfiguration = URLSessionConfiguration.default
        urlSessionConfiguration.timeoutIntervalForRequest = 45
        urlSessionConfiguration.timeoutIntervalForResource = 90
        urlSessionConfiguration.waitsForConnectivity = true
        self.urlSession = URLSession(configuration: urlSessionConfiguration)
    }

    func appendAudioBuffer(_ audioBuffer: AVAudioPCMBuffer) {
        guard let audioPCM16Data = audioPCM16Converter.convertToPCM16Data(from: audioBuffer),
              !audioPCM16Data.isEmpty else {
            return
        }

        let bufferPeak = audioPCM16Data.withUnsafeBytes { rawBuffer -> Int16 in
            rawBuffer.bindMemory(to: Int16.self).reduce(Int16(0)) { peak, sample in
                max(peak, sample == Int16.min ? Int16.max : abs(sample))
            }
        }
        stateQueue.async {
            guard !self.hasRequestedFinalTranscript, !self.isCancelled else { return }
            self.bufferedPCM16AudioData.append(audioPCM16Data)
            self.peakSampleMagnitude = max(self.peakSampleMagnitude, bufferPeak)
        }
    }

    func requestFinalTranscript() {
        stateQueue.async {
            guard !self.hasRequestedFinalTranscript, !self.isCancelled else { return }
            self.hasRequestedFinalTranscript = true

            let bufferedPCM16AudioData = self.bufferedPCM16AudioData
            let recordedSeconds = Double(bufferedPCM16AudioData.count) / Double(Self.targetSampleRate * MemoryLayout<Int16>.size)
            let peakSampleMagnitude = self.peakSampleMagnitude
            print("[OpenAI Transcription] recorded \(String(format: "%.1f", recordedSeconds)) s, peak \(peakSampleMagnitude)/32767")
            if peakSampleMagnitude < Self.silencePeakThreshold {
                print("[OpenAI Transcription] audio is silent (the microphone may be held by another engine); not uploading")
                self.transcriptionUploadTask = Task { [weak self] in
                    self?.deliverFinalTranscript("")
                }
                return
            }
            self.transcriptionUploadTask = Task { [weak self] in
                await self?.transcribeBufferedAudio(bufferedPCM16AudioData)
            }
        }
    }

    func cancel() {
        stateQueue.async {
            self.isCancelled = true
            self.bufferedPCM16AudioData.removeAll(keepingCapacity: false)
        }

        transcriptionUploadTask?.cancel()
        urlSession.invalidateAndCancel()
    }

    private func transcribeBufferedAudio(_ bufferedPCM16AudioData: Data) async {
        guard !Task.isCancelled else { return }

        let trimmedAudioDataIsEmpty = stateQueue.sync {
            isCancelled || bufferedPCM16AudioData.isEmpty
        }

        if trimmedAudioDataIsEmpty {
            deliverFinalTranscript("")
            return
        }

        let wavAudioData = BuddyWAVFileBuilder.buildWAVData(
            fromPCM16MonoAudio: bufferedPCM16AudioData,
            sampleRate: Self.targetSampleRate
        )

        do {
            var transcriptText = try await requestTranscription(for: wavAudioData)
            guard !stateQueue.sync(execute: { isCancelled }) else { return }

            if Self.looksLikeEchoedPrompt(transcriptText, prompt: transcriptionPromptText()) {
                print("[OpenAI Transcription] the model echoed its prompt instead of transcribing; treating as silence")
                transcriptText = ""
            }

            if !transcriptText.isEmpty {
                onTranscriptUpdate(transcriptText)
            }

            deliverFinalTranscript(transcriptText)
        } catch {
            guard !stateQueue.sync(execute: { isCancelled }) else { return }
            print("[OpenAI Transcription] ❌ Upload failed (audio size: \(wavAudioData.count) bytes): \(error.localizedDescription)")
            onError(error)
        }
    }

    private func requestTranscription(for wavAudioData: Data) async throws -> String {
        var request = URLRequest(url: Self.transcriptionURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&request)

        var requestBody: [String: Any] = [
            "audio": wavAudioData.base64EncodedString(),
            "mime": "audio/wav",
            "language": "en"
        ]
        if let contextualPrompt = transcriptionPromptText() {
            requestBody["prompt"] = contextualPrompt
        }
        request.httpBody = try JSONSerialization.data(withJSONObject: requestBody)

        let (responseData, response) = try await urlSession.data(for: request)

        guard let httpResponse = response as? HTTPURLResponse else {
            throw OpenAIAudioTranscriptionProviderError(
                message: "OpenClicky transcription returned an invalid response."
            )
        }

        guard (200...299).contains(httpResponse.statusCode) else {
            let responseText = String(data: responseData, encoding: .utf8) ?? "Unknown error"
            throw OpenAIAudioTranscriptionProviderError(
                message: "OpenClicky transcription failed (HTTP \(httpResponse.statusCode)): \(responseText)"
            )
        }

        if let transcriptionResponse = try? JSONDecoder().decode(
            TranscriptionResponse.self,
            from: responseData
        ) {
            return transcriptionResponse.text.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        throw OpenAIAudioTranscriptionProviderError(
            message: "OpenClicky transcription returned an empty transcript."
        )
    }

    private func transcriptionPromptText() -> String? {
        let normalizedKeyterms = keyterms
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }

        guard !normalizedKeyterms.isEmpty else { return nil }

        return """
        This is a short push-to-talk transcript for a coding and product app. Expect product names, technical terms, and app-specific vocabulary such as: \(normalizedKeyterms.joined(separator: ", ")).
        """
    }

    private static func looksLikeEchoedPrompt(_ transcriptText: String, prompt: String?) -> Bool {
        OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt(transcriptText, prompt: prompt)
    }

    private func deliverFinalTranscript(_ transcriptText: String) {
        guard !hasDeliveredFinalTranscript else { return }
        hasDeliveredFinalTranscript = true
        onFinalTranscriptReady(transcriptText)
    }

    deinit {
        // Not `cancel()`: that enqueues a block capturing `self` on the state queue, and releasing
        // that block after this deinit has run trips Swift's "deallocated with non-zero retain
        // count" abort (the app quit right after every dictation). Nothing else can still be queued
        // here — pending blocks hold `self` strongly and would have kept it alive — so only the
        // upload needs stopping.
        transcriptionUploadTask?.cancel()
        urlSession.invalidateAndCancel()
    }
}
