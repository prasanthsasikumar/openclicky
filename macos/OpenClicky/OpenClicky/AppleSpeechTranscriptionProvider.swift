//
//  AppleSpeechTranscriptionProvider.swift
//  OpenClicky
//
//  Local fallback transcription provider backed by Apple's Speech framework.
//

import AVFoundation
import Foundation
import os
import Speech

struct AppleSpeechTranscriptionProviderError: LocalizedError {
    let message: String

    var errorDescription: String? {
        message
    }
}

final class AppleSpeechTranscriptionProvider: BuddyTranscriptionProvider {
    let displayName = "Apple Speech"
    let requiresSpeechRecognitionPermission = true
    let isConfigured = true
    let unavailableExplanation: String? = nil

    func startStreamingSession(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> any BuddyStreamingTranscriptionSession {
        // macOS 26's dictation model where the system has it for this language: the model behind
        // the system's own dictation, far more accurate than SFSpeechRecognizer — which, in the
        // sibling app Saathi, typed "12245" for a spoken sentence. Both are on-device.
        if #available(macOS 26, *) {
            do {
                return try await SpeechAnalyzerDictationSession.start(
                    onTranscriptUpdate: onTranscriptUpdate,
                    onFinalTranscriptReady: onFinalTranscriptReady,
                    onError: onError)
            } catch {
                print("🎙️ Apple Speech: SpeechAnalyzer unavailable (\(error.localizedDescription)), using SFSpeechRecognizer")
            }
        }
        guard let speechRecognizer = Self.makeBestAvailableSpeechRecognizer() else {
            throw AppleSpeechTranscriptionProviderError(message: "dictation is not available on this mac.")
        }

        return try AppleSpeechTranscriptionSession(
            speechRecognizer: speechRecognizer,
            onTranscriptUpdate: onTranscriptUpdate,
            onFinalTranscriptReady: onFinalTranscriptReady,
            onError: onError
        )
    }

    private static func makeBestAvailableSpeechRecognizer() -> SFSpeechRecognizer? {
        // The language OpenClicky is set to speak, not this Mac's: the two lanes must hear the
        // same language they answer in.
        let preferredLocales = [
            ReplyLanguage.recognitionLocale,
            Locale(identifier: ReplyLanguage.currentCode),
            Locale(identifier: "en-US")
        ]

        for preferredLocale in preferredLocales {
            if let speechRecognizer = SFSpeechRecognizer(locale: preferredLocale) {
                return speechRecognizer
            }
        }

        return SFSpeechRecognizer()
    }
}

private final class AppleSpeechTranscriptionSession: NSObject, BuddyStreamingTranscriptionSession {
    let finalTranscriptFallbackDelaySeconds: TimeInterval = 1.8

    private let recognitionRequest: SFSpeechAudioBufferRecognitionRequest
    private var recognitionTask: SFSpeechRecognitionTask?
    private let onTranscriptUpdate: (String) -> Void
    private let onFinalTranscriptReady: (String) -> Void
    private let onError: (Error) -> Void

    private var latestRecognizedText = ""
    private var hasRequestedFinalTranscript = false
    private var hasDeliveredFinalTranscript = false

    init(
        speechRecognizer: SFSpeechRecognizer,
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) throws {
        self.recognitionRequest = SFSpeechAudioBufferRecognitionRequest()
        self.onTranscriptUpdate = onTranscriptUpdate
        self.onFinalTranscriptReady = onFinalTranscriptReady
        self.onError = onError

        super.init()

        recognitionRequest.shouldReportPartialResults = true
        recognitionRequest.taskHint = .dictation
        recognitionRequest.addsPunctuation = true

        if speechRecognizer.supportsOnDeviceRecognition {
            recognitionRequest.requiresOnDeviceRecognition = true
        }

        recognitionTask = speechRecognizer.recognitionTask(with: recognitionRequest) { [weak self] result, error in
            self?.handleRecognitionEvent(result: result, error: error)
        }
    }

    func appendAudioBuffer(_ audioBuffer: AVAudioPCMBuffer) {
        guard !hasRequestedFinalTranscript else { return }
        recognitionRequest.append(audioBuffer)
    }

    func requestFinalTranscript() {
        guard !hasRequestedFinalTranscript else { return }
        hasRequestedFinalTranscript = true
        recognitionRequest.endAudio()
    }

    func cancel() {
        recognitionTask?.cancel()
        recognitionTask = nil
    }

    private func handleRecognitionEvent(
        result: SFSpeechRecognitionResult?,
        error: Error?
    ) {
        if let result {
            latestRecognizedText = result.bestTranscription.formattedString
            onTranscriptUpdate(latestRecognizedText)

            if result.isFinal {
                deliverFinalTranscriptIfNeeded(latestRecognizedText)
                return
            }
        }

        guard let error else { return }

        if hasRequestedFinalTranscript && !latestRecognizedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            deliverFinalTranscriptIfNeeded(latestRecognizedText)
        } else {
            onError(error)
        }
    }

    private func deliverFinalTranscriptIfNeeded(_ transcriptText: String) {
        guard !hasDeliveredFinalTranscript else { return }
        hasDeliveredFinalTranscript = true
        onFinalTranscriptReady(transcriptText)
    }

    deinit {
        cancel()
    }
}

/// Apple's current dictation model through `SpeechAnalyzer` (macOS 26). Audio arrives on the
/// CoreAudio render thread (`BuddyDictationManager`'s tap), so everything `appendAudioBuffer`
/// touches is behind `state`'s lock or is the stream continuation, which is thread-safe.
@available(macOS 26, *)
private final class SpeechAnalyzerDictationSession: BuddyStreamingTranscriptionSession, @unchecked Sendable {
    let finalTranscriptFallbackDelaySeconds: TimeInterval = 2.5

    private struct State {
        var converter: AVAudioConverter?
        var converterInputFormat: AVAudioFormat?
        var finalizedText = ""
        var volatileText = ""
        var hasRequestedFinalTranscript = false
    }
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let analyzer: SpeechAnalyzer
    private let analyzerFormat: AVAudioFormat
    private let audioInput: AsyncStream<AnalyzerInput>.Continuation
    private var resultsTask: Task<Void, Never>?
    private let onTranscriptUpdate: (String) -> Void
    private let onFinalTranscriptReady: (String) -> Void
    private let onError: (Error) -> Void

    static func start(
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> SpeechAnalyzerDictationSession {
        let requestedLocale = ReplyLanguage.recognitionLocale
        var supportedLocale = await DictationTranscriber.supportedLocale(equivalentTo: requestedLocale)
        if supportedLocale == nil {
            supportedLocale = await DictationTranscriber.supportedLocale(equivalentTo: Locale(identifier: ReplyLanguage.currentCode))
        }
        guard let supportedLocale else {
            throw AppleSpeechTranscriptionProviderError(message: "dictation cannot listen in \(requestedLocale.identifier) on this mac.")
        }
        let transcriber = DictationTranscriber(locale: supportedLocale, preset: .progressiveLongDictation)
        if let assetInstallationRequest = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            try await assetInstallationRequest.downloadAndInstall()
        }
        guard let analyzerFormat = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
            throw AppleSpeechTranscriptionProviderError(message: "dictation has no audio format it can take.")
        }
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        try await analyzer.prepareToAnalyze(in: analyzerFormat)
        let (audioStream, audioInput) = AsyncStream<AnalyzerInput>.makeStream()
        let session = SpeechAnalyzerDictationSession(
            analyzer: analyzer, analyzerFormat: analyzerFormat, audioInput: audioInput,
            onTranscriptUpdate: onTranscriptUpdate, onFinalTranscriptReady: onFinalTranscriptReady, onError: onError)
        session.resultsTask = Task { [weak session] in
            do {
                for try await result in transcriber.results {
                    session?.receive(text: String(result.text.characters), isFinal: result.isFinal)
                }
            } catch {
                session?.onError(error)
            }
        }
        try await analyzer.start(inputSequence: audioStream)
        return session
    }

    private init(
        analyzer: SpeechAnalyzer,
        analyzerFormat: AVAudioFormat,
        audioInput: AsyncStream<AnalyzerInput>.Continuation,
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) {
        self.analyzer = analyzer
        self.analyzerFormat = analyzerFormat
        self.audioInput = audioInput
        self.onTranscriptUpdate = onTranscriptUpdate
        self.onFinalTranscriptReady = onFinalTranscriptReady
        self.onError = onError
    }

    /// Progressive results: a volatile guess that keeps being replaced, then a final stretch that
    /// is kept. What has been said so far is every final stretch plus the current guess.
    private func receive(text: String, isFinal: Bool) {
        let transcriptSoFar = state.withLock { sessionState -> String in
            if isFinal {
                sessionState.finalizedText += text
                sessionState.volatileText = ""
            } else {
                sessionState.volatileText = text
            }
            return sessionState.finalizedText + sessionState.volatileText
        }
        onTranscriptUpdate(transcriptSoFar)
    }

    func appendAudioBuffer(_ audioBuffer: AVAudioPCMBuffer) {
        let analyzerFormat = self.analyzerFormat
        let inputFormat = audioBuffer.format
        // The converter is made from the first buffer's format and reused; the tap's format does
        // not change within one dictation.
        let converter = state.withLock { sessionState -> AVAudioConverter? in
            guard !sessionState.hasRequestedFinalTranscript else { return nil }
            if sessionState.converter == nil || sessionState.converterInputFormat != inputFormat {
                sessionState.converter = AVAudioConverter(from: inputFormat, to: analyzerFormat)
                sessionState.converterInputFormat = inputFormat
            }
            return sessionState.converter
        }
        guard let converter else { return }
        let sampleRateRatio = analyzerFormat.sampleRate / audioBuffer.format.sampleRate
        let convertedCapacity = AVAudioFrameCount(Double(audioBuffer.frameLength) * sampleRateRatio) + 64
        guard let convertedBuffer = AVAudioPCMBuffer(pcmFormat: analyzerFormat, frameCapacity: convertedCapacity) else { return }
        var hasHandedOverInput = false
        var conversionError: NSError?
        // `convert` calls this block synchronously on this thread; the buffer only crosses the
        // `@Sendable` boundary in a box because the compiler cannot see that.
        let inputBufferBox = AudioBufferBox(audioBuffer)
        converter.convert(to: convertedBuffer, error: &conversionError) { _, inputStatus in
            if hasHandedOverInput {
                inputStatus.pointee = .noDataNow
                return nil
            }
            hasHandedOverInput = true
            inputStatus.pointee = .haveData
            return inputBufferBox.buffer
        }
        guard conversionError == nil, convertedBuffer.frameLength > 0 else { return }
        audioInput.yield(AnalyzerInput(buffer: convertedBuffer))
    }

    func requestFinalTranscript() {
        let alreadyRequested = state.withLock { sessionState -> Bool in
            defer { sessionState.hasRequestedFinalTranscript = true }
            return sessionState.hasRequestedFinalTranscript
        }
        guard !alreadyRequested else { return }
        audioInput.finish()
        let analyzer = self.analyzer
        Task { [weak self] in
            try? await analyzer.finalizeAndFinishThroughEndOfInput()
            await self?.resultsTask?.value
            guard let self else { return }
            let finalTranscript = self.state.withLock { $0.finalizedText + $0.volatileText }
            self.onFinalTranscriptReady(finalTranscript.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    func cancel() {
        audioInput.finish()
        resultsTask?.cancel()
        let analyzer = self.analyzer
        Task { await analyzer.cancelAndFinishNow() }
    }
}

/// See `SpeechAnalyzerDictationSession.appendAudioBuffer`.
private final class AudioBufferBox: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
    init(_ buffer: AVAudioPCMBuffer) { self.buffer = buffer }
}
