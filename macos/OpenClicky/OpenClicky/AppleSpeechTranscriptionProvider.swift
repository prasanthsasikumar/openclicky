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
    /// Dictation's pinned language (Settings → engine), when one is chosen; else the reply language.
    let preferredLocale: Locale?

    init(preferredLocale: Locale? = nil) {
        self.preferredLocale = preferredLocale
    }

    func startStreamingSession(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> any BuddyStreamingTranscriptionSession {
        // macOS 26's dictation model where the system has it for this language: the model behind
        // the system's own dictation, far more accurate than SFSpeechRecognizer — which, in the
        // sibling app Saathi, typed "12245" for a spoken sentence. Both are on-device.
        if #available(macOS 26, *), !Self.forcesOlderRecognizer {
            do {
                return try await SpeechAnalyzerDictationSession.start(
                    preferredLocale: preferredLocale,
                    onTranscriptUpdate: onTranscriptUpdate,
                    onFinalTranscriptReady: onFinalTranscriptReady,
                    onError: onError)
            } catch {
                print("🎙️ Apple Speech: SpeechAnalyzer unavailable (\(error.localizedDescription)), using SFSpeechRecognizer")
                AppLog.append("transcription (this mac): SpeechAnalyzer unavailable (\(error.localizedDescription)), using SFSpeechRecognizer")
            }
        }
        guard let speechRecognizer = Self.makeBestAvailableSpeechRecognizer(preferredLocale: preferredLocale) else {
            throw AppleSpeechTranscriptionProviderError(message: "dictation is not available on this mac.")
        }

        return try AppleSpeechTranscriptionSession(
            speechRecognizer: speechRecognizer,
            onTranscriptUpdate: onTranscriptUpdate,
            onFinalTranscriptReady: onFinalTranscriptReady,
            onError: onError
        )
    }

    /// Debug: `OPENCLICKY_FORCE_SFSPEECH=1` uses SFSpeechRecognizer on macOS 26 too, which is what
    /// every Mac before macOS 26 runs.
    private static var forcesOlderRecognizer: Bool {
        #if DEBUG
        return ProcessInfo.processInfo.environment["OPENCLICKY_FORCE_SFSPEECH"] == "1"
        #else
        return false
        #endif
    }

    private static func makeBestAvailableSpeechRecognizer(preferredLocale: Locale?) -> SFSpeechRecognizer? {
        // A pinned language, so both lanes hear the language they answer in. Automatic hears this
        // Mac's own language: there is no single code to pin.
        let preferredLocales = ReplyLanguage.followsTheSpeaker
            ? [preferredLocale, Locale.autoupdatingCurrent, Locale(identifier: "en-US")]
            : [
                preferredLocale,
                ReplyLanguage.recognitionLocale,
                Locale(identifier: ReplyLanguage.currentCode),
                Locale(identifier: "en-US")
            ]

        for preferredLocale in preferredLocales.compactMap({ $0 }) {
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
    /// Every stretch the recogniser has closed so far in this take, joined.
    private var finishedStretchesText = ""
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
            let stretchText = result.bestTranscription.formattedString
            latestRecognizedText = SpokenStretches.joined(finishedStretchesText, stretchText)
            // After a pause the recogniser closes the stretch (it attaches metadata) and starts the
            // next one from nothing; without keeping the closed ones, a take of three sentences
            // pasted only the last (macOS before 26, where this recogniser is the offline engine).
            if result.speechRecognitionMetadata != nil, !result.isFinal {
                finishedStretchesText = latestRecognizedText
            }
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
        preferredLocale: Locale?,
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> SpeechAnalyzerDictationSession {
        let requestedLocale = preferredLocale ?? (ReplyLanguage.followsTheSpeaker ? Locale.autoupdatingCurrent : ReplyLanguage.recognitionLocale)
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

/// Joining the recogniser's stretches into one take.
enum SpokenStretches {
    /// `finished` then `current`. A stretch ends on a pause, and the recogniser often leaves its
    /// full stop off; one goes in when the next stretch starts with a capital, as a new sentence.
    static func joined(_ finished: String, _ current: String) -> String {
        let finishedTrimmed = finished.trimmingCharacters(in: .whitespacesAndNewlines)
        let currentTrimmed = current.trimmingCharacters(in: .whitespacesAndNewlines)
        if finishedTrimmed.isEmpty { return currentTrimmed }
        if currentTrimmed.isEmpty { return finishedTrimmed }
        let finishedEndsASentence = finishedTrimmed.last.map { ".!?,;:…".contains($0) } ?? false
        let currentStartsASentence = currentTrimmed.first?.isUppercase ?? false
        let separator = !finishedEndsASentence && currentStartsASentence ? ". " : " "
        return finishedTrimmed + separator + currentTrimmed
    }
}
