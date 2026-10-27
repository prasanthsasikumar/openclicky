//
//  BuddyDictationManager.swift
//  OpenClicky
//
//  The microphone-and-engine side of push to talk, shared by dictation (one manager per take
//  controller, behind `DictationCapturing`) and the companion's ⌃⌥ talk lane. Captures microphone
//  audio with AVAudioEngine, routes it into the active transcription provider, and hands the
//  partial and final transcripts back to the caller's draft callbacks.
//

import AppKit
import AVFoundation
import Combine
import Foundation
import Speech

enum BuddyPushToTalkShortcut {
    /// Talk (control + option) going down or coming up, as `GlobalPushToTalkShortcutMonitor` publishes it.
    enum ShortcutTransition {
        case none
        case pressed
        case released
    }
}

private struct BuddyDictationDraftCallbacks {
    let updateDraftText: (String) -> Void
    let submitDraftText: (String) -> Void
}

/// The three callbacks a transcription session reports through.
struct BuddyTranscriptionSessionCallbacks {
    let onTranscriptUpdate: (String) -> Void
    let onFinalTranscriptReady: (String) -> Void
    let onError: (Error) -> Void
}

@MainActor
final class BuddyDictationManager: NSObject, ObservableObject {
    private static let defaultFinalTranscriptFallbackDelaySeconds: TimeInterval = 2.4

    @Published private(set) var isRecordingFromKeyboardShortcut = false
    @Published private(set) var isKeyboardShortcutSessionActiveOrFinalizing = false
    @Published private(set) var isFinalizingTranscript = false
    @Published private(set) var isPreparingToRecord = false
    @Published private(set) var currentAudioPowerLevel: CGFloat = 0
    @Published private(set) var transcriptionProviderDisplayName = ""
    @Published var lastErrorMessage: String?

    var isDictationInProgress: Bool {
        isPreparingToRecord || isRecordingFromKeyboardShortcut || isFinalizingTranscript
    }

    var needsInitialPermissionPrompt: Bool {
        if transcriptionProvider.requiresSpeechRecognitionPermission {
            return AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined
                || SFSpeechRecognizer.authorizationStatus() == .notDetermined
        }

        return AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined
    }

    private var transcriptionProvider: any BuddyTranscriptionProvider
    private var audioEngine = AVAudioEngine()
    /// True once `audioEngine` was pointed at a microphone other than the default; it stays there,
    /// so the next take on the default gets a new engine.
    private var audioEngineHasChosenMicrophone = false
    private var activeTranscriptionSession: (any BuddyStreamingTranscriptionSession)?
    /// Which session the callbacks may speak for. Each session gets a new identity before it is
    /// opened, and it is cleared when the session ends, so a late partial, final or error from an
    /// old session (a slow upload, a socket closing) can never reach the take that came after.
    private(set) var activeSessionIdentity: UUID?
    /// True from a started push-to-talk session until it is finished, cancelled or reset.
    private var isPushToTalkSessionOpen = false
    private var draftCallbacks: BuddyDictationDraftCallbacks?
    private var draftTextBeforeCurrentDictation = ""
    private var latestRecognizedText = ""
    private var shouldAutomaticallySubmitFinalDraft = false
    private var hasFinishedCurrentDictationSession = false
    private var finalizeFallbackWorkItem: DispatchWorkItem?
    private var pendingStartRequestIdentifier = UUID()
    private var contextualKeyterms: [String] = []
    private var activePermissionRequestTask: Task<Bool, Never>?
    /// Timestamp of the last completed permission request, used to debounce
    /// rapid follow-up requests that arrive before macOS updates its cache.
    private var lastPermissionRequestCompletedAt: Date?

    override convenience init() {
        self.init(transcriptionProvider: BuddyTranscriptionProviderFactory.makeDefaultProvider())
    }

    /// A manager around one provider; the dictation take controller picks its engine this way.
    init(transcriptionProvider: any BuddyTranscriptionProvider) {
        self.transcriptionProvider = transcriptionProvider
        self.transcriptionProviderDisplayName = transcriptionProvider.displayName
        super.init()
    }

    /// Swaps the engine between takes (Settings → Dictation). Ignored while a take is in progress.
    func replaceTranscriptionProvider(_ provider: any BuddyTranscriptionProvider) {
        guard !isDictationInProgress else { return }
        transcriptionProvider = provider
        transcriptionProviderDisplayName = provider.displayName
    }

    /// The input device to record from (its CoreAudio UID); nil = the system default.
    var preferredMicrophoneUID: String?
    /// Every captured buffer, on the render thread, for a caller that keeps the take's audio
    /// (dictation retains a failed take so it can be retried). Captured when the tap is installed.
    var audioRetentionSink: (@Sendable (AVAudioPCMBuffer) -> Void)?

    func updateContextualKeyterms(_ contextualKeyterms: [String]) {
        self.contextualKeyterms = contextualKeyterms
    }

    func startPushToTalkFromKeyboardShortcut(
        currentDraftText: String,
        updateDraftText: @escaping (String) -> Void,
        submitDraftText: @escaping (String) -> Void
    ) async {
        guard !isDictationInProgress else { return }

        print("🎙️ BuddyDictationManager: start requested")

        if needsInitialPermissionPrompt {
            print("🎙️ BuddyDictationManager: requesting initial permissions")
            NSApplication.shared.activate(ignoringOtherApps: true)

            do {
                try await Task.sleep(for: .milliseconds(200))
            } catch {
                // If the task is cancelled while the app is being activated,
                // we can safely continue into the permission request.
            }
        }

        let startRequestIdentifier = UUID()
        pendingStartRequestIdentifier = startRequestIdentifier

        lastErrorMessage = nil
        isPreparingToRecord = true

        guard await requestMicrophoneAndSpeechPermissionsWithoutDuplicatePrompts() else {
            AppLog.append("dictation: not started (\(transcriptionProvider.displayName)): \(lastErrorMessage ?? "permissions missing or denied")")
            isPreparingToRecord = false
            return
        }
        guard !Task.isCancelled else {
            print("🎙️ BuddyDictationManager: start cancelled (shortcut released during permission check)")
            isPreparingToRecord = false
            return
        }
        guard pendingStartRequestIdentifier == startRequestIdentifier else {
            print("🎙️ BuddyDictationManager: start request superseded")
            isPreparingToRecord = false
            return
        }

        draftTextBeforeCurrentDictation = currentDraftText
        latestRecognizedText = ""
        draftCallbacks = BuddyDictationDraftCallbacks(
            updateDraftText: updateDraftText,
            submitDraftText: submitDraftText
        )
        isPushToTalkSessionOpen = true
        // With words already in the caller's draft the dictation is added to them, not sent.
        shouldAutomaticallySubmitFinalDraft = currentDraftText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        hasFinishedCurrentDictationSession = false
        isFinalizingTranscript = false
        isRecordingFromKeyboardShortcut = true
        isKeyboardShortcutSessionActiveOrFinalizing = true
        currentAudioPowerLevel = 0

        guard !Task.isCancelled else {
            print("🎙️ BuddyDictationManager: start cancelled (shortcut released before recording began)")
            resetSessionState()
            return
        }

        do {
            try await startRecognitionSession()
            guard !Task.isCancelled else {
                print("🎙️ BuddyDictationManager: start cancelled (shortcut released during session start)")
                tearDownRecordingAndSession()
                resetSessionState()
                return
            }
            isPreparingToRecord = false
            print("🎙️ BuddyDictationManager: recognition session started")
        } catch {
            isPreparingToRecord = false
            lastErrorMessage = userFacingErrorMessage(
                from: error,
                fallback: "couldn't start voice input. try again."
            )
            AppLog.append("dictation: failed to start recognition session (\(transcriptionProvider.displayName)): \(error)")
            // The provider may have opened its session and the tap may be installed before the
            // audio engine refused to start: both go, as on a cancelled start, so nothing keeps
            // feeding (or billing) a session no take is listening to.
            tearDownRecordingAndSession()
            resetSessionState()
        }
    }

    func stopPushToTalkFromKeyboardShortcut() {
        pendingStartRequestIdentifier = UUID()

        guard isPushToTalkSessionOpen else {
            isPreparingToRecord = false
            return
        }
        guard !isFinalizingTranscript else { return }

        print("🎙️ BuddyDictationManager: stop requested")

        isRecordingFromKeyboardShortcut = false
        isFinalizingTranscript = true

        let finalTranscriptFallbackDelaySeconds = activeTranscriptionSession?.finalTranscriptFallbackDelaySeconds
            ?? Self.defaultFinalTranscriptFallbackDelaySeconds

        audioEngine.stop()
        audioEngine.inputNode.removeTap(onBus: 0)
        activeTranscriptionSession?.requestFinalTranscript()

        finalizeFallbackWorkItem?.cancel()
        let shouldSubmitFinalDraftWhenFallbackTriggers = shouldAutomaticallySubmitFinalDraft
        let fallbackWorkItem = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                self?.finishCurrentDictationSessionIfNeeded(
                    shouldSubmitFinalDraft: shouldSubmitFinalDraftWhenFallbackTriggers
                )
            }
        }
        finalizeFallbackWorkItem = fallbackWorkItem
        DispatchQueue.main.asyncAfter(
            deadline: .now() + finalTranscriptFallbackDelaySeconds,
            execute: fallbackWorkItem
        )
    }

    func cancelCurrentDictation(preserveDraftText: Bool = true) {
        pendingStartRequestIdentifier = UUID()

        guard isDictationInProgress else { return }

        finalizeFallbackWorkItem?.cancel()
        finalizeFallbackWorkItem = nil

        if preserveDraftText {
            let currentDraftText = composeDraftText(withTranscribedText: latestRecognizedText)
            draftCallbacks?.updateDraftText(currentDraftText)
        }

        tearDownRecordingAndSession()
        resetSessionState()
    }

    /// Stops the microphone, removes the tap and cancels the open transcription session.
    private func tearDownRecordingAndSession() {
        audioEngine.stop()
        audioEngine.inputNode.removeTap(onBus: 0)
        activeTranscriptionSession?.cancel()
    }

    /// Gives the session about to open a new identity; callbacks made for an earlier one go quiet.
    func beginNewSessionIdentity() -> UUID {
        let sessionIdentity = UUID()
        activeSessionIdentity = sessionIdentity
        return sessionIdentity
    }

    /// The callbacks for the session with `sessionIdentity`. Each checks, on the main actor, that
    /// its session is still the active one and drops what it carries otherwise.
    func makeTranscriptionCallbacks(forSessionIdentity sessionIdentity: UUID) -> BuddyTranscriptionSessionCallbacks {
        BuddyTranscriptionSessionCallbacks(
            onTranscriptUpdate: { [weak self] transcriptText in
                Task { @MainActor in
                    guard let self, self.activeSessionIdentity == sessionIdentity else { return }
                    self.latestRecognizedText = transcriptText
                    // Live words for the orb; the companion's callers ignore partials.
                    self.draftCallbacks?.updateDraftText(self.composeDraftText(withTranscribedText: transcriptText))
                }
            },
            onFinalTranscriptReady: { [weak self] transcriptText in
                Task { @MainActor in
                    guard let self, self.activeSessionIdentity == sessionIdentity else { return }
                    self.latestRecognizedText = transcriptText

                    if self.isFinalizingTranscript {
                        self.finishCurrentDictationSessionIfNeeded(
                            shouldSubmitFinalDraft: self.shouldAutomaticallySubmitFinalDraft
                        )
                    }
                }
            },
            onError: { [weak self] error in
                Task { @MainActor in
                    guard let self, self.activeSessionIdentity == sessionIdentity else { return }
                    self.handleRecognitionError(error)
                }
            }
        )
    }

    private func startRecognitionSession() async throws {
        activeTranscriptionSession?.cancel()
        activeTranscriptionSession = nil

        print("🎙️ BuddyDictationManager: opening transcription provider \(transcriptionProvider.displayName)")

        let callbacks = makeTranscriptionCallbacks(forSessionIdentity: beginNewSessionIdentity())
        let activeTranscriptionSession = try await transcriptionProvider.startStreamingSession(
            keyterms: buildTranscriptionKeyterms(),
            onTranscriptUpdate: callbacks.onTranscriptUpdate,
            onFinalTranscriptReady: callbacks.onFinalTranscriptReady,
            onError: callbacks.onError
        )

        self.activeTranscriptionSession = activeTranscriptionSession
        print("🎙️ BuddyDictationManager: provider ready, starting audio engine")

        // Settings → microphone: a chosen input device, else the system default.
        let usesChosenMicrophone = MicrophoneDevices.choiceDiffersFromDefault(preferredUID: preferredMicrophoneUID)
        if audioEngineHasChosenMicrophone && !usesChosenMicrophone {
            audioEngine = AVAudioEngine()
        }
        audioEngineHasChosenMicrophone = usesChosenMicrophone
        MicrophoneDevices.apply(preferredUID: preferredMicrophoneUID, to: audioEngine)
        let inputNode = audioEngine.inputNode
        let inputFormat = inputNode.outputFormat(forBus: 0)
        // Which microphone a take records from, so a log of takes that heard nothing says whether
        // the input was a silent device (a virtual one, the headphone jack) or the engine.
        AppLog.append("microphone: \(MicrophoneDevices.effectiveInputName(preferredUID: preferredMicrophoneUID) ?? "none"), \(Int(inputFormat.sampleRate)) Hz, \(inputFormat.channelCount) ch")

        inputNode.removeTap(onBus: 0)
        // AVAudioEngine calls this block on the CoreAudio render thread, but `AVAudioNodeTapBlock`
        // is not `@Sendable`, so the compiler silently treats the closure as main-actor isolated
        // like the rest of this class and reports nothing. Reading `self.activeTranscriptionSession`
        // in here therefore raced the main actor reassigning it in `startRecognitionSession` and
        // nilling it in `finishCurrentDictationSessionIfNeeded` — invisibly, in both directions.
        // The session is captured by value instead: this tap feeds the session it was installed
        // for, and nothing else, for as long as it is installed.
        let audioRetentionSink = self.audioRetentionSink
        inputNode.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self, activeTranscriptionSession] buffer, _ in
            activeTranscriptionSession.appendAudioBuffer(buffer)
            audioRetentionSink?(buffer)
            self?.updateAudioPowerLevel(from: buffer)
        }

        audioEngine.prepare()
        try audioEngine.start()
    }

    private func handleRecognitionError(_ error: Error) {
        if hasFinishedCurrentDictationSession {
            return
        }

        if isFinalizingTranscript && !latestRecognizedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            finishCurrentDictationSessionIfNeeded(
                shouldSubmitFinalDraft: shouldAutomaticallySubmitFinalDraft
            )
        } else {
            AppLog.append("dictation: recognition error (\(transcriptionProvider.displayName)): \(error)")
            lastErrorMessage = userFacingErrorMessage(
                from: error,
                fallback: "couldn't transcribe that. try again."
            )
            cancelCurrentDictation(preserveDraftText: false)
        }
    }

    private func finishCurrentDictationSessionIfNeeded(shouldSubmitFinalDraft: Bool) {
        guard !hasFinishedCurrentDictationSession else { return }
        hasFinishedCurrentDictationSession = true

        finalizeFallbackWorkItem?.cancel()
        finalizeFallbackWorkItem = nil

        let finalDraftText = composeDraftText(withTranscribedText: latestRecognizedText)
        let finalTranscriptText = latestRecognizedText.trimmingCharacters(in: .whitespacesAndNewlines)
        let currentDraftCallbacks = draftCallbacks

        if !shouldSubmitFinalDraft && !finalDraftText.isEmpty {
            currentDraftCallbacks?.updateDraftText(finalDraftText)
        }

        tearDownRecordingAndSession()
        resetSessionState()

        guard shouldSubmitFinalDraft else { return }
        guard !finalTranscriptText.isEmpty else { return }

        currentDraftCallbacks?.submitDraftText(finalDraftText)
    }

    private func composeDraftText(withTranscribedText transcribedText: String) -> String {
        let trimmedTranscriptText = transcribedText.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmedTranscriptText.isEmpty else {
            return draftTextBeforeCurrentDictation
        }

        let trimmedExistingDraftText = draftTextBeforeCurrentDictation
            .trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmedExistingDraftText.isEmpty else {
            return trimmedTranscriptText
        }

        if draftTextBeforeCurrentDictation.hasSuffix(" ") || draftTextBeforeCurrentDictation.hasSuffix("\n") {
            return draftTextBeforeCurrentDictation + trimmedTranscriptText
        }

        return draftTextBeforeCurrentDictation + " " + trimmedTranscriptText
    }

    private func resetSessionState() {
        pendingStartRequestIdentifier = UUID()
        activeTranscriptionSession = nil
        activeSessionIdentity = nil
        draftCallbacks = nil
        isPushToTalkSessionOpen = false
        draftTextBeforeCurrentDictation = ""
        latestRecognizedText = ""
        shouldAutomaticallySubmitFinalDraft = false
        hasFinishedCurrentDictationSession = false
        isPreparingToRecord = false
        isRecordingFromKeyboardShortcut = false
        isKeyboardShortcutSessionActiveOrFinalizing = false
        isFinalizingTranscript = false
        currentAudioPowerLevel = 0
    }

    private func buildTranscriptionKeyterms() -> [String] {
        let baseKeyterms = [
            "OpenClicky",
            "Codex",
            "Claude",
            "Anthropic",
            "OpenAI",
            "SwiftUI",
            "Xcode",
            "Vercel",
            "Next.js",
            "localhost"
        ]

        let combinedKeyterms = baseKeyterms + contextualKeyterms
        var uniqueNormalizedKeyterms = Set<String>()
        var orderedKeyterms: [String] = []

        for keyterm in combinedKeyterms {
            let trimmedKeyterm = keyterm.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmedKeyterm.isEmpty else { continue }

            let normalizedKeyterm = trimmedKeyterm.lowercased()
            if uniqueNormalizedKeyterms.contains(normalizedKeyterm) {
                continue
            }

            uniqueNormalizedKeyterms.insert(normalizedKeyterm)
            orderedKeyterms.append(trimmedKeyterm)
        }

        return orderedKeyterms
    }

    /// `nonisolated` because the audio tap calls it on the render thread. It reads nothing off
    /// `self` before hopping to the main queue, which is what makes that safe — keep it that way.
    private nonisolated func updateAudioPowerLevel(from audioBuffer: AVAudioPCMBuffer) {
        guard let channelData = audioBuffer.floatChannelData else { return }

        let channelSamples = channelData[0]
        let frameCount = Int(audioBuffer.frameLength)
        guard frameCount > 0 else { return }

        var summedSquares: Float = 0
        for sampleIndex in 0..<frameCount {
            let sample = channelSamples[sampleIndex]
            summedSquares += sample * sample
        }

        let rootMeanSquare = sqrt(summedSquares / Float(frameCount))
        let boostedLevel = min(max(rootMeanSquare * 10.2, 0), 1)

        DispatchQueue.main.async { [weak self] in
            // `DispatchQueue.main.async` lands on the main thread, so the main actor's state is
            // genuinely ours here; `assumeIsolated` is how that is stated to the compiler now that
            // the caller is nonisolated, and it traps rather than corrupts if it ever stops holding.
            MainActor.assumeIsolated {
                guard let self else { return }

                let smoothedAudioPowerLevel = max(
                    CGFloat(boostedLevel),
                    self.currentAudioPowerLevel * 0.72
                )
                self.currentAudioPowerLevel = smoothedAudioPowerLevel
            }
        }
    }

    private func requestMicrophoneAndSpeechPermissionsIfNeeded() async -> Bool {
        let hasMicrophonePermission = await requestMicrophonePermissionIfNeeded()
        guard hasMicrophonePermission else {
            lastErrorMessage = "microphone permission is required for push to talk."
            return false
        }

        guard transcriptionProvider.requiresSpeechRecognitionPermission else {
            return true
        }

        let hasSpeechRecognitionPermission = await requestSpeechRecognitionPermissionIfNeeded()
        guard hasSpeechRecognitionPermission else {
            lastErrorMessage = "speech recognition permission is required for push to talk."
            return false
        }

        return true
    }

    /// macOS can show the microphone/speech sheet again if we accidentally fan out
    /// multiple permission requests before the first one finishes. We keep exactly
    /// one in-flight request task so rapid repeat presses all await the same result.
    ///
    /// After the task completes, we skip re-requesting for a short cooldown period
    /// so macOS has time to update its authorization cache. This prevents the
    /// permission dialog from popping up again on rapid follow-up presses.
    private func requestMicrophoneAndSpeechPermissionsWithoutDuplicatePrompts() async -> Bool {
        // If a permission request is already in-flight, reuse it.
        if let activePermissionRequestTask {
            return await activePermissionRequestTask.value
        }

        // If we just finished a permission request very recently, skip re-requesting.
        // macOS can briefly report .notDetermined even after the user tapped Allow,
        // so we trust the cached result for a short window.
        if let lastPermissionRequestCompletedAt,
           Date().timeIntervalSince(lastPermissionRequestCompletedAt) < 1.0 {
            return cachedPermissionsAllowRecording()
        }

        let permissionRequestTask = Task { @MainActor in
            await self.requestMicrophoneAndSpeechPermissionsIfNeeded()
        }

        activePermissionRequestTask = permissionRequestTask

        let hasPermissions = await permissionRequestTask.value
        activePermissionRequestTask = nil
        lastPermissionRequestCompletedAt = Date()
        return hasPermissions
    }

    /// The cooldown's answer, without asking macOS again. A refusal says why, as the full request
    /// does: a silent `false` left the dictation take waiting for a session that never opened
    /// ("moving your words" on the orb, the key dead until esc).
    private func cachedPermissionsAllowRecording() -> Bool {
        let microphoneStatus = AVCaptureDevice.authorizationStatus(for: .audio)
        if microphoneStatus == .denied || microphoneStatus == .restricted {
            lastErrorMessage = "microphone permission is required for push to talk."
            return false
        }
        if transcriptionProvider.requiresSpeechRecognitionPermission {
            let speechStatus = SFSpeechRecognizer.authorizationStatus()
            if speechStatus == .denied || speechStatus == .restricted {
                lastErrorMessage = "speech recognition permission is required for push to talk."
                return false
            }
        }
        return true
    }

    private func requestMicrophonePermissionIfNeeded() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return true
        case .notDetermined:
            return await withCheckedContinuation { continuation in
                AVCaptureDevice.requestAccess(for: .audio) { isGranted in
                    continuation.resume(returning: isGranted)
                }
            }
        case .denied, .restricted:
            return false
        @unknown default:
            return false
        }
    }

    private func requestSpeechRecognitionPermissionIfNeeded() async -> Bool {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized:
            return true
        case .notDetermined:
            return await withCheckedContinuation { continuation in
                SFSpeechRecognizer.requestAuthorization { authorizationStatus in
                    continuation.resume(returning: authorizationStatus == .authorized)
                }
            }
        case .denied, .restricted:
            return false
        @unknown default:
            return false
        }
    }

    private func userFacingErrorMessage(from error: Error, fallback: String) -> String {
        if let localizedError = error as? LocalizedError,
           let errorDescription = localizedError.errorDescription?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !errorDescription.isEmpty {
            return errorDescription
        }

        let errorDescription = error.localizedDescription.trimmingCharacters(in: .whitespacesAndNewlines)
        if !errorDescription.isEmpty,
           errorDescription != "The operation couldn’t be completed." {
            return errorDescription
        }

        return fallback
    }
}
