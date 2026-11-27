//
//  MeetingTranscriptionController.swift
//  OpenClicky
//
//  Long-form, local meeting transcription using the same Apple Speech provider as dictation.
//

import AVFoundation
import Combine
import Foundation
import Speech

@MainActor
final class MeetingTranscriptionController: ObservableObject {
    enum State: Equatable {
        case idle
        case requestingPermission
        case recording
        case finishing
        case failed(String)
    }

    static let shared = MeetingTranscriptionController()

    @Published private(set) var state: State = .idle
    @Published private(set) var transcript = ""
    @Published var languageCode = "en-IN"

    private let store: ProductivityStore
    private var audioEngine: AVAudioEngine?
    private var session: (any BuddyStreamingTranscriptionSession)?
    private var startedAt: Date?
    private var finalFallback: Task<Void, Never>?

    init(store: ProductivityStore? = nil) {
        self.store = store ?? .shared
    }

    var isRecording: Bool { state == .recording }
    var isBusy: Bool { state == .requestingPermission || state == .finishing }

    func start() {
        guard state == .idle || isFailed else { return }
        state = .requestingPermission
        transcript = ""
        Task { await prepareAndStart() }
    }

    func stop() {
        guard state == .recording else { return }
        state = .finishing
        stopAudioCapture()
        session?.requestFinalTranscript()
        let delay = session?.finalTranscriptFallbackDelaySeconds ?? 2
        finalFallback?.cancel()
        finalFallback = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            await self?.finish(with: self?.transcript ?? "")
        }
    }

    func cancel() {
        finalFallback?.cancel()
        finalFallback = nil
        stopAudioCapture()
        session?.cancel()
        session = nil
        startedAt = nil
        transcript = ""
        state = .idle
    }

    private var isFailed: Bool {
        if case .failed = state { return true }
        return false
    }

    private func prepareAndStart() async {
        let microphoneAllowed = await AVCaptureDevice.requestAccess(for: .audio)
        guard microphoneAllowed else {
            state = .failed("Microphone access is required for meeting transcription.")
            return
        }

        let speechStatus = await Self.requestSpeechAuthorization()
        guard speechStatus == .authorized else {
            state = .failed("Speech Recognition access is required for local transcription.")
            return
        }

        do {
            let provider = AppleSpeechTranscriptionProvider(preferredLocale: Locale(identifier: languageCode))
            let newSession = try await provider.startStreamingSession(
                keyterms: [],
                onTranscriptUpdate: { [weak self] text in
                    Task { @MainActor in self?.transcript = text }
                },
                onFinalTranscriptReady: { [weak self] text in
                    Task { @MainActor in await self?.finish(with: text) }
                },
                onError: { [weak self] error in
                    Task { @MainActor in self?.fail(error.localizedDescription) }
                }
            )

            let engine = AVAudioEngine()
            let input = engine.inputNode
            let format = input.outputFormat(forBus: 0)
            guard format.sampleRate > 0, format.channelCount > 0 else {
                throw AppleSpeechTranscriptionProviderError(message: "The selected microphone has no audio input.")
            }
            input.installTap(onBus: 0, bufferSize: 1_024, format: format) { buffer, _ in
                newSession.appendAudioBuffer(buffer)
            }
            engine.prepare()
            try engine.start()

            session = newSession
            audioEngine = engine
            startedAt = Date()
            state = .recording
        } catch {
            fail(error.localizedDescription)
        }
    }

    private func finish(with finalText: String) async {
        guard state == .finishing || state == .recording else { return }
        finalFallback?.cancel()
        finalFallback = nil
        stopAudioCapture()
        session?.cancel()
        session = nil

        let cleaned = finalText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !cleaned.isEmpty {
            let start = startedAt ?? Date()
            store.addMeeting(MeetingTranscriptRecord(
                title: Self.meetingTitle(for: start),
                transcript: cleaned,
                startedAt: start,
                endedAt: Date(),
                languageCode: languageCode
            ))
            transcript = cleaned
        }
        startedAt = nil
        state = .idle
    }

    private func fail(_ message: String) {
        stopAudioCapture()
        session?.cancel()
        session = nil
        startedAt = nil
        state = .failed(message)
    }

    private func stopAudioCapture() {
        guard let engine = audioEngine else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        audioEngine = nil
    }

    private static func requestSpeechAuthorization() async -> SFSpeechRecognizerAuthorizationStatus {
        if SFSpeechRecognizer.authorizationStatus() != .notDetermined {
            return SFSpeechRecognizer.authorizationStatus()
        }
        return await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { status in continuation.resume(returning: status) }
        }
    }

    private static func meetingTitle(for date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return "Meeting · \(formatter.string(from: date))"
    }
}
