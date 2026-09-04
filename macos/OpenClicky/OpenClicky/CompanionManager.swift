//
//  CompanionManager.swift
//  OpenClicky
//
//  Central state manager for the companion voice mode. Owns the push-to-talk
//  pipeline (dictation manager + global shortcut monitor + overlay) and
//  exposes observable voice state for the panel UI.
//

import AVFoundation
import Combine
import Foundation
import PostHog
import ScreenCaptureKit
import SwiftUI

/// Snapshot of a finished agent turn, shown in the notch HUD's result card.
struct OpenClickyAgentResultSummary: Equatable {
    let threadId: String
    let title: String
    let text: String
    let artifacts: [String]
    let status: String
    let finishedAt: Date
}

enum CompanionVoiceState {
    case idle
    case listening
    case processing
    case responding
}

@MainActor
final class CompanionManager: ObservableObject {
    @Published private(set) var voiceState: CompanionVoiceState = .idle
    @Published private(set) var lastTranscript: String?
    @Published private(set) var currentAudioPowerLevel: CGFloat = 0
    @Published private(set) var hasAccessibilityPermission = false
    @Published private(set) var hasScreenRecordingPermission = false
    @Published private(set) var hasMicrophonePermission = false
    @Published private(set) var hasScreenContentPermission = false

    /// Screen location (global AppKit coords) of a detected UI element the
    /// buddy should fly to and point at. Parsed from Claude's response;
    /// observed by BlueCursorView to trigger the flight animation.
    @Published var detectedElementScreenLocation: CGPoint?
    /// The display frame (global AppKit coords) of the screen the detected
    /// element is on, so BlueCursorView knows which screen overlay should animate.
    @Published var detectedElementDisplayFrame: CGRect?
    /// Custom speech bubble text for the pointing animation. When set,
    /// BlueCursorView uses this instead of a random pointer phrase.
    @Published var detectedElementBubbleText: String?
    /// Bumped on every `pointAt` so the overlay re-flies even when the model points at the
    /// same coordinates twice in a row (the location alone would not change).
    @Published var detectedElementPointToken: Int = 0

    // MARK: - Onboarding Video State (shared across all screen overlays)

    @Published var onboardingVideoPlayer: AVPlayer?
    @Published var showOnboardingVideo: Bool = false
    @Published var onboardingVideoOpacity: Double = 0.0
    private var onboardingVideoEndObserver: NSObjectProtocol?
    private var onboardingDemoTimeObserver: Any?

    // MARK: - Onboarding Prompt Bubble

    /// Text streamed character-by-character on the cursor after the onboarding video ends.
    @Published var onboardingPromptText: String = ""
    @Published var onboardingPromptOpacity: Double = 0.0
    @Published var showOnboardingPrompt: Bool = false

    let buddyDictationManager = BuddyDictationManager()
    let globalPushToTalkShortcutMonitor = GlobalPushToTalkShortcutMonitor()
    let overlayWindowManager = OverlayWindowManager()
    // Response text is now displayed inline on the cursor overlay via
    // streamingResponseText, so no separate response overlay manager is needed.

    /// Base URL for the OpenClicky backend (the key-holding proxy). All API requests route
    /// through this so keys never ship in the app binary. Configured in ~/.openclicky/shell.json.
    private static var workerBaseURL: String { OpenClickyConfiguration.backendBaseURL }

    /// Runs the `openclicky` CLI for the agent lane (gate + Codex thread).
    private let openClickyAgentClient = OpenClickyAgentClient()

    /// Codex thread reused across agent-lane requests in this session so follow-ups resume context.
    private var lastAgentThreadId: String?

    /// Latest milestone from a running agent turn ("ran: …", "started thread …"), shown in the panel.
    @Published private(set) var agentActivityText: String?

    /// Files the last agent turn created or changed.
    @Published private(set) var lastAgentArtifacts: [String] = []

    /// The last completed agent turn, for the notch HUD's result card.
    @Published private(set) var lastAgentResult: OpenClickyAgentResultSummary?

    /// The floating card that shows an agent's result and takes follow-ups (top-right of the screen).
    let agentResultPanelManager = AgentResultPanelManager()

    /// Agent mode: a cheap gate classifies each utterance; "do work" requests go to a Codex
    /// thread via the OpenClicky CLI instead of the teacher (Claude + pointing) lane.
    @Published var isAgentModeEnabled: Bool = UserDefaults.standard.object(forKey: "isOpenClickyAgentModeEnabled") == nil
        ? true
        : UserDefaults.standard.bool(forKey: "isOpenClickyAgentModeEnabled")

    func setAgentModeEnabled(_ enabled: Bool) {
        isAgentModeEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: "isOpenClickyAgentModeEnabled")
    }

    // MARK: - Realtime voice (OpenClicky fast lane)

    /// Speech-to-speech over OpenAI Realtime instead of transcribe → Claude → TTS. On by default
    /// when a backend token exists; falls back to the classic lane if the session cannot open.
    @Published var isRealtimeVoiceEnabled: Bool = UserDefaults.standard.object(forKey: "isOpenClickyRealtimeVoiceEnabled") == nil
        ? true
        : UserDefaults.standard.bool(forKey: "isOpenClickyRealtimeVoiceEnabled")

    /// Always-on listening (server VAD, barge-in) instead of push-to-talk.
    @Published var isAlwaysListening: Bool = UserDefaults.standard.bool(forKey: "isOpenClickyAlwaysListening")

    /// The menu bar icon is off by default: the notch HUD is the app's home. The onboarding /
    /// permissions panel still opens on its own when something needs attention.
    @Published var isMenuBarIconVisible: Bool = UserDefaults.standard.bool(forKey: "isOpenClickyMenuBarIconVisible")

    func setMenuBarIconVisible(_ visible: Bool) {
        isMenuBarIconVisible = visible
        UserDefaults.standard.set(visible, forKey: "isOpenClickyMenuBarIconVisible")
    }

    let realtimeVoiceClient = RealtimeVoiceClient()
    /// The user's skill library (~/.openclicky/skills) plus the bundled app-teaching skills.
    let skillLibraryStore = SkillLibraryStore()
    private var realtimeLevelCancellable: AnyCancellable?
    private var didGreetRealtime = false
    private var didConfigureRealtimeCallbacks = false

    private var usesRealtimeVoice: Bool { isRealtimeVoiceEnabled && OpenClickyConfiguration.isConfigured }

    func setRealtimeVoiceEnabled(_ enabled: Bool) {
        isRealtimeVoiceEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: "isOpenClickyRealtimeVoiceEnabled")
        if enabled { warmUpRealtimeVoice() } else { realtimeVoiceClient.disconnect(reason: "realtime voice turned off") }
    }

    func setAlwaysListening(_ enabled: Bool) {
        isAlwaysListening = enabled
        UserDefaults.standard.set(enabled, forKey: "isOpenClickyAlwaysListening")
        warmUpRealtimeVoice()
    }

    /// Open the Realtime session now so the first key press only starts audio (HeyClicky's warm-up).
    private func warmUpRealtimeVoice() {
        guard usesRealtimeVoice else { return }
        configureRealtimeCallbacksIfNeeded()
        let mode: RealtimeVoiceClient.TurnMode = isAlwaysListening ? .alwaysOn : .pushToTalk
        realtimeVoiceClient.disconnect(reason: nil)
        realtimeVoiceClient.keepWarm(mode: mode)
        if isAlwaysListening {
            Task { [weak self] in
                guard let self else { return }
                try? await self.realtimeVoiceClient.connectIfNeeded(mode: .alwaysOn)
                self.realtimeVoiceClient.startListeningContinuously()
                if !self.didGreetRealtime {
                    self.didGreetRealtime = true
                    self.realtimeVoiceClient.requestResponse(instructions: "Greet the user in English in one short sentence as OpenClicky.")
                }
            }
        }
    }

    private func configureRealtimeCallbacksIfNeeded() {
        guard !didConfigureRealtimeCallbacks else { return }
        didConfigureRealtimeCallbacks = true
        realtimeVoiceClient.onTranscript = { [weak self] role, text in
            guard let self else { return }
            switch role {
            case .user:
                self.lastTranscript = text
                print("🗣️ (realtime) \(text)")
            case .assistant:
                self.conversationHistory.append((userTranscript: self.lastTranscript ?? "", assistantResponse: text))
                if self.conversationHistory.count > 10 { self.conversationHistory.removeFirst(self.conversationHistory.count - 10) }
                print("🔊 (realtime) \(text)")
            }
        }
        realtimeVoiceClient.onResponseStarted = { [weak self] in
            guard let self, self.usesRealtimeVoice else { return }
            if self.voiceState != .listening { self.voiceState = .responding }
        }
        realtimeVoiceClient.onResponseFinished = { [weak self] in
            guard let self, self.usesRealtimeVoice, self.voiceState != .listening else { return }
            self.voiceState = .idle
            self.scheduleTransientHideIfNeeded()
        }
        realtimeVoiceClient.onEvent = { line in print("🎙️ \(line)") }
        realtimeVoiceClient.screenContextProvider = {
            await CompanionScreenCaptureUtility.captureCursorScreenContext()
        }
        realtimeVoiceClient.instructionsProvider = { [weak self] in
            Self.composeTalkInstructions(base: RealtimeVoiceClient.defaultInstructions, skillsBlock: self?.talkSkillsBlock() ?? "")
        }
        realtimeVoiceClient.onPointAt = { [weak self] screenshotPoint, label, capture in
            self?.pointAt(screenshotPoint: screenshotPoint, label: label, in: capture)
        }
        realtimeVoiceClient.onAgentTask = { [weak self] task in
            guard let self else { return "OpenClicky is not available." }
            self.agentActivityText = "starting agent…"
            let screenCaptures = (try? await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()) ?? []
            do {
                return try await self.performAgentTask(transcript: task, screenCaptures: screenCaptures)
            } catch {
                self.agentActivityText = nil
                return "The agent could not run: \(error.localizedDescription)"
            }
        }
        realtimeLevelCancellable = realtimeVoiceClient.$inputLevel.sink { [weak self] level in
            guard let self, self.usesRealtimeVoice, self.voiceState == .listening else { return }
            self.currentAudioPowerLevel = level
        }
    }

    // MARK: - Skills in the talk lanes (OpenClicky)

    /// The prompt block for the skills that apply to this turn: the user's activated talk skills
    /// plus the app-teaching skill matching the app (or browser site) in front. Empty when none.
    func talkSkillsBlock() -> String {
        let front = FrontmostAppObserver.current(excludingBundleIdentifier: Bundle.main.bundleIdentifier)
        let appSkill = AppSkillMatcher.match(front, in: skillLibraryStore.appSkills.filter { $0.isForTalk })
        let block = SkillPromptBuilder.build(activeSkills: skillLibraryStore.activeTalkSkills, appSkill: appSkill, front: front)
        if let appSkill { print("🧩 Skills: app skill \"\(appSkill.name)\" for \(front.bundleIdentifier ?? "?")\(front.url?.host.map { " / " + $0 } ?? "")") }
        return block
    }

    /// Base prompt + skills block, separated by a blank line; the base alone when there is nothing to add.
    nonisolated static func composeTalkInstructions(base: String, skillsBlock: String) -> String {
        let trimmed = skillsBlock.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? base : base + "\n\n" + trimmed
    }

    // MARK: - Notch HUD + docked cursor (OpenClicky)

    /// The notch HUD: a lip under the notch that opens on hover / while busy.
    let notchHUDManager = NotchHUDManager()

    /// True while the buddy lives in the notch HUD instead of following the mouse.
    @Published private(set) var isCursorDocked: Bool = false

    /// Set to start the docking flight; observed by BlueCursorView on the screen that owns the point.
    @Published var cursorDockTargetScreenLocation: CGPoint?

    /// Where a freshly shown overlay should start the buddy (the notch) before flying out.
    var cursorLaunchOriginScreenLocation: CGPoint?

    private var isDockingInProgress = false

    /// Dock the buddy in the notch, or release it back to the mouse.
    func setCursorDocked(_ docked: Bool) {
        if docked { dockCursorToNotch() } else { undockCursorFromNotch() }
    }

    private func dockCursorToNotch() {
        guard !isCursorDocked, !isDockingInProgress else { return }
        guard voiceState == .idle else { return }
        isDockingInProgress = true
        if !isOverlayVisible {
            overlayWindowManager.hasShownOverlayBefore = true
            overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
            isOverlayVisible = true
        }
        transientHideTask?.cancel()
        transientHideTask = nil
        clearDetectedElementLocation()
        // Set on the next runloop turn so a freshly created overlay observes the change.
        let dockPoint = notchHUDManager.dockPoint
        DispatchQueue.main.async {
            self.cursorDockTargetScreenLocation = dockPoint
        }
        print("🎯 Docking cursor to notch at \(dockPoint)")
    }

    /// Called by the overlay once the flight into the notch has finished.
    func finishDockingCursor() {
        isDockingInProgress = false
        isCursorDocked = true
        UserDefaults.standard.set(true, forKey: "isOpenClickyCursorDocked")
        overlayWindowManager.hideOverlay()
        isOverlayVisible = false
        cursorDockTargetScreenLocation = nil
    }

    private func undockCursorFromNotch() {
        guard isCursorDocked else { return }
        isCursorDocked = false
        UserDefaults.standard.set(false, forKey: "isOpenClickyCursorDocked")
        cursorDockTargetScreenLocation = nil
        // Re-create the overlay with the buddy starting at the notch; it flies to the mouse.
        cursorLaunchOriginScreenLocation = notchHUDManager.dockPoint
        overlayWindowManager.hasShownOverlayBefore = true
        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
        isOverlayVisible = true
        print("🎯 Cursor released from the notch")
    }

    /// Text entry point into the agent lane (result-card follow-ups, future text mode). Captures the
    /// screen like the voice path does, resumes `threadId` when given, and speaks the result.
    func submitTextToAgent(_ text: String, threadId: String? = nil) {
        let trimmedText = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedText.isEmpty, OpenClickyConfiguration.isConfigured else { return }
        currentResponseTask?.cancel()
        openClickyAgentClient.cancel()
        elevenLabsTTSClient.stopPlayback()
        if let threadId { lastAgentThreadId = threadId }
        lastTranscript = trimmedText
        currentResponseTask = Task {
            voiceState = .processing
            let screenCaptures = (try? await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()) ?? []
            do {
                try await runOpenClickyAgentLane(transcript: trimmedText, screenCaptures: screenCaptures)
            } catch is CancellationError {
            } catch {
                print("⚠️ Agent follow-up error: \(error)")
                agentActivityText = nil
                speakCreditsErrorFallback()
            }
            if !Task.isCancelled {
                voiceState = .idle
                scheduleTransientHideIfNeeded()
            }
        }
    }

    /// Opens the result card for a thread (Agents tab → Open Agent).
    func openAgentResultCard(threadId: String) {
        agentResultPanelManager.show(threadId: threadId, companionManager: self)
    }

    // MARK: - Pointing (shared by the Claude teacher lane and the Realtime `point_at` tool)

    /// Maps a point in a screenshot's pixel space (origin top-left) onto AppKit global screen
    /// coordinates (origin bottom-left of the main display) for the display that was captured.
    /// Out-of-range coordinates are clamped to the screenshot.
    nonisolated static func screenLocation(forScreenshotPoint point: CGPoint, in capture: CompanionScreenCapture) -> CGPoint {
        let screenshotWidth = CGFloat(max(capture.screenshotWidthInPixels, 1))
        let screenshotHeight = CGFloat(max(capture.screenshotHeightInPixels, 1))
        let displayWidth = CGFloat(capture.displayWidthInPoints)
        let displayHeight = CGFloat(capture.displayHeightInPoints)
        let displayFrame = capture.displayFrame

        // NaN would survive min/max; treat it as the origin. Infinite values clamp to the edges.
        let clampedX = point.x.isNaN ? 0 : max(0, min(point.x, screenshotWidth))
        let clampedY = point.y.isNaN ? 0 : max(0, min(point.y, screenshotHeight))

        // Scale from screenshot pixels to display points, then flip to AppKit's bottom-left origin.
        let displayLocalX = clampedX * (displayWidth / screenshotWidth)
        let displayLocalY = clampedY * (displayHeight / screenshotHeight)
        let appKitY = displayHeight - displayLocalY

        return CGPoint(x: displayLocalX + displayFrame.origin.x, y: appKitY + displayFrame.origin.y)
    }

    /// Fly the buddy to `screenshotPoint` on the captured display and show `label` in its bubble.
    /// Safe to call while a reply is being spoken (Realtime `point_at`) or once the teacher lane
    /// has its answer; a second call while the buddy is already pointing retargets it.
    func pointAt(screenshotPoint: CGPoint, label: String?, in capture: CompanionScreenCapture) {
        // The spinner (processing) hides the triangle, so the flight would be invisible; the
        // buddy is visible in idle and responding.
        if voiceState == .processing { voiceState = .idle }
        launchDockedCursorForPointing()

        let location = Self.screenLocation(forScreenshotPoint: screenshotPoint, in: capture)
        let trimmedLabel = label?.trimmingCharacters(in: .whitespacesAndNewlines)
        detectedElementBubbleText = (trimmedLabel?.isEmpty == false) ? trimmedLabel : nil
        detectedElementDisplayFrame = capture.displayFrame
        detectedElementScreenLocation = location
        detectedElementPointToken &+= 1
        ClickyAnalytics.trackElementPointed(elementLabel: trimmedLabel)
        let formatted = { (value: CGFloat) in String(format: "%.0f", value) }
        print("🎯 Element pointing: (\(formatted(screenshotPoint.x)), \(formatted(screenshotPoint.y))) → \"\(trimmedLabel ?? "element")\"")
    }

    /// While docked, pointing needs the buddy on screen: launch it from the notch, let the
    /// normal navigation fly it to the element, and it returns to the notch afterwards.
    private func launchDockedCursorForPointing() {
        guard isCursorDocked, !isOverlayVisible else { return }
        cursorLaunchOriginScreenLocation = notchHUDManager.dockPoint
        cursorDockTargetScreenLocation = notchHUDManager.dockPoint
        overlayWindowManager.hasShownOverlayBefore = true
        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
        isOverlayVisible = true
    }

    private lazy var claudeAPI: ClaudeAPI = {
        return ClaudeAPI(proxyURL: "\(Self.workerBaseURL)/chat", model: selectedModel)
    }()

    private lazy var elevenLabsTTSClient: ElevenLabsTTSClient = {
        return ElevenLabsTTSClient(proxyURL: "\(Self.workerBaseURL)/tts")
    }()

    /// Conversation history so Claude remembers prior exchanges within a session.
    /// Each entry is the user's transcript and Claude's response.
    private var conversationHistory: [(userTranscript: String, assistantResponse: String)] = []

    /// The currently running AI response task, if any. Cancelled when the user
    /// speaks again so a new response can begin immediately.
    private var currentResponseTask: Task<Void, Never>?

    private var shortcutTransitionCancellable: AnyCancellable?
    private var voiceStateCancellable: AnyCancellable?
    private var audioPowerCancellable: AnyCancellable?
    private var accessibilityCheckTimer: Timer?
    private var pendingKeyboardShortcutStartTask: Task<Void, Never>?
    /// Scheduled hide for transient cursor mode — cancelled if the user
    /// speaks again before the delay elapses.
    private var transientHideTask: Task<Void, Never>?

    /// True when all three required permissions (accessibility, screen recording,
    /// microphone) are granted. Used by the panel to show a single "all good" state.
    var allPermissionsGranted: Bool {
        hasAccessibilityPermission && hasScreenRecordingPermission && hasMicrophonePermission && hasScreenContentPermission
    }

    /// Whether the blue cursor overlay is currently visible on screen.
    /// Used by the panel to show accurate status text ("Active" vs "Ready").
    @Published private(set) var isOverlayVisible: Bool = false

    /// The Claude model used for voice responses. Persisted to UserDefaults.
    @Published var selectedModel: String = UserDefaults.standard.string(forKey: "selectedClaudeModel") ?? "claude-sonnet-4-6"

    func setSelectedModel(_ model: String) {
        selectedModel = model
        UserDefaults.standard.set(model, forKey: "selectedClaudeModel")
        claudeAPI.model = model
    }

    /// User preference for whether the OpenClicky cursor should be shown.
    /// When toggled off, the overlay is hidden and push-to-talk is disabled.
    /// Persisted to UserDefaults so the choice survives app restarts.
    @Published var isClickyCursorEnabled: Bool = UserDefaults.standard.object(forKey: "isClickyCursorEnabled") == nil
        ? true
        : UserDefaults.standard.bool(forKey: "isClickyCursorEnabled")

    func setClickyCursorEnabled(_ enabled: Bool) {
        isClickyCursorEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: "isClickyCursorEnabled")
        transientHideTask?.cancel()
        transientHideTask = nil

        if enabled {
            overlayWindowManager.hasShownOverlayBefore = true
            overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
            isOverlayVisible = true
        } else {
            overlayWindowManager.hideOverlay()
            isOverlayVisible = false
        }
    }

    /// Whether the user has completed onboarding at least once. Persisted
    /// to UserDefaults so the Start button only appears on first launch.
    var hasCompletedOnboarding: Bool {
        get { UserDefaults.standard.bool(forKey: "hasCompletedOnboarding") }
        set { UserDefaults.standard.set(newValue, forKey: "hasCompletedOnboarding") }
    }

    /// Whether the user has submitted their email during onboarding.
    @Published var hasSubmittedEmail: Bool = UserDefaults.standard.bool(forKey: "hasSubmittedEmail")

    /// Submits the user's email to FormSpark and identifies them in PostHog.
    func submitEmail(_ email: String) {
        let trimmedEmail = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedEmail.isEmpty else { return }

        hasSubmittedEmail = true
        UserDefaults.standard.set(true, forKey: "hasSubmittedEmail")
        // OpenClicky: upstream posted the email to the original developer's form and identified the
        // user in PostHog. Neither happens here — the email stays on this machine.
    }

    func start() {
        refreshAllPermissions()
        print("🔑 OpenClicky start — accessibility: \(hasAccessibilityPermission), screen: \(hasScreenRecordingPermission), mic: \(hasMicrophonePermission), screenContent: \(hasScreenContentPermission), onboarded: \(hasCompletedOnboarding)")
        startPermissionPolling()
        bindVoiceStateObservation()
        bindAudioPowerLevel()
        bindShortcutTransitions()
        // Eagerly touch the Claude API so its TLS warmup handshake completes
        // well before the onboarding demo fires at ~40s into the video.
        _ = claudeAPI

        // If the user already completed onboarding AND all permissions are
        // still granted, show the cursor overlay immediately. If permissions
        // were revoked (e.g. signing change), don't show the cursor — the
        // panel will show the permissions UI instead.
        if hasCompletedOnboarding && allPermissionsGranted && isClickyCursorEnabled {
            if UserDefaults.standard.bool(forKey: "isOpenClickyCursorDocked") {
                // Restore the docked state without an animation: the buddy is simply home.
                isCursorDocked = true
            } else {
                overlayWindowManager.hasShownOverlayBefore = true
                overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
                isOverlayVisible = true
            }
        }

        // OpenClicky: the notch HUD is always available; it needs no permissions.
        notchHUDManager.show(companionManager: self)

        // OpenClicky: keep the Realtime voice session warm so talking is instant.
        if hasCompletedOnboarding && allPermissionsGranted {
            warmUpRealtimeVoice()
        }
    }

    /// Called by BlueCursorView after the buddy finishes its pointing
    /// animation and returns to cursor-following mode.
    /// Triggers the onboarding sequence — dismisses the panel and restarts
    /// the overlay so the welcome animation and intro video play.
    func triggerOnboarding() {
        // Post notification so the panel manager can dismiss the panel
        NotificationCenter.default.post(name: .clickyDismissPanel, object: nil)

        // Mark onboarding as completed so the Start button won't appear
        // again on future launches — the cursor will auto-show instead
        hasCompletedOnboarding = true

        ClickyAnalytics.trackOnboardingStarted()

        // Show the overlay for the first time — isFirstAppearance triggers
        // the welcome animation and onboarding video
        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
        isOverlayVisible = true
    }

    /// Replays the onboarding experience from the "Watch Onboarding Again"
    /// footer link. Same flow as triggerOnboarding but the cursor overlay
    /// is already visible so we just restart the welcome animation and video.
    func replayOnboarding() {
        NotificationCenter.default.post(name: .clickyDismissPanel, object: nil)
        ClickyAnalytics.trackOnboardingReplayed()
        // Tear down any existing overlays and recreate with isFirstAppearance = true
        overlayWindowManager.hasShownOverlayBefore = false
        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
        isOverlayVisible = true
    }

    func clearDetectedElementLocation() {
        detectedElementScreenLocation = nil
        detectedElementDisplayFrame = nil
        detectedElementBubbleText = nil
    }

    func stop() {
        notchHUDManager.hide()
        realtimeVoiceClient.disconnect(reason: nil)
        globalPushToTalkShortcutMonitor.stop()
        buddyDictationManager.cancelCurrentDictation()
        overlayWindowManager.hideOverlay()
        transientHideTask?.cancel()

        currentResponseTask?.cancel()
        currentResponseTask = nil
        shortcutTransitionCancellable?.cancel()
        voiceStateCancellable?.cancel()
        audioPowerCancellable?.cancel()
        accessibilityCheckTimer?.invalidate()
        accessibilityCheckTimer = nil
    }

    func refreshAllPermissions() {
        let previouslyHadAccessibility = hasAccessibilityPermission
        let previouslyHadScreenRecording = hasScreenRecordingPermission
        let previouslyHadMicrophone = hasMicrophonePermission
        let previouslyHadAll = allPermissionsGranted

        let currentlyHasAccessibility = WindowPositionManager.hasAccessibilityPermission()
        hasAccessibilityPermission = currentlyHasAccessibility

        if currentlyHasAccessibility {
            globalPushToTalkShortcutMonitor.start()
        } else {
            globalPushToTalkShortcutMonitor.stop()
        }

        hasScreenRecordingPermission = WindowPositionManager.hasScreenRecordingPermission()

        let micAuthStatus = AVCaptureDevice.authorizationStatus(for: .audio)
        hasMicrophonePermission = micAuthStatus == .authorized

        // Debug: log permission state on changes
        if previouslyHadAccessibility != hasAccessibilityPermission
            || previouslyHadScreenRecording != hasScreenRecordingPermission
            || previouslyHadMicrophone != hasMicrophonePermission {
            print("🔑 Permissions — accessibility: \(hasAccessibilityPermission), screen: \(hasScreenRecordingPermission), mic: \(hasMicrophonePermission), screenContent: \(hasScreenContentPermission)")
        }

        // Track individual permission grants as they happen
        if !previouslyHadAccessibility && hasAccessibilityPermission {
            ClickyAnalytics.trackPermissionGranted(permission: "accessibility")
        }
        if !previouslyHadScreenRecording && hasScreenRecordingPermission {
            ClickyAnalytics.trackPermissionGranted(permission: "screen_recording")
        }
        if !previouslyHadMicrophone && hasMicrophonePermission {
            ClickyAnalytics.trackPermissionGranted(permission: "microphone")
        }
        // Screen content permission is persisted — once the user has approved the
        // SCShareableContent picker, we don't need to re-check it.
        if !hasScreenContentPermission {
            hasScreenContentPermission = UserDefaults.standard.bool(forKey: "hasScreenContentPermission")
        }

        if !previouslyHadAll && allPermissionsGranted {
            ClickyAnalytics.trackAllPermissionsGranted()
        }
    }

    /// Triggers the macOS screen content picker by performing a dummy
    /// screenshot capture. Once the user approves, we persist the grant
    /// so they're never asked again during onboarding.
    @Published private(set) var isRequestingScreenContent = false

    func requestScreenContentPermission() {
        guard !isRequestingScreenContent else { return }
        isRequestingScreenContent = true
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first else {
                    await MainActor.run { isRequestingScreenContent = false }
                    return
                }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let config = SCStreamConfiguration()
                config.width = 320
                config.height = 240
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                // Verify the capture actually returned real content — a 0x0 or
                // fully-empty image means the user denied the prompt.
                let didCapture = image.width > 0 && image.height > 0
                print("🔑 Screen content capture result — width: \(image.width), height: \(image.height), didCapture: \(didCapture)")
                await MainActor.run {
                    isRequestingScreenContent = false
                    guard didCapture else { return }
                    hasScreenContentPermission = true
                    UserDefaults.standard.set(true, forKey: "hasScreenContentPermission")
                    ClickyAnalytics.trackPermissionGranted(permission: "screen_content")

                    // If onboarding was already completed, show the cursor overlay now
                    if hasCompletedOnboarding && allPermissionsGranted && !isOverlayVisible && isClickyCursorEnabled {
                        overlayWindowManager.hasShownOverlayBefore = true
                        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
                        isOverlayVisible = true
                    }
                }
            } catch {
                print("⚠️ Screen content permission request failed: \(error)")
                await MainActor.run { isRequestingScreenContent = false }
            }
        }
    }

    // MARK: - Private

    /// Triggers the system microphone prompt if the user has never been asked.
    /// Once granted/denied the status sticks and polling picks it up.
    private func promptForMicrophoneIfNotDetermined() {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined else { return }
        AVCaptureDevice.requestAccess(for: .audio) { [weak self] granted in
            Task { @MainActor [weak self] in
                self?.hasMicrophonePermission = granted
            }
        }
    }

    /// Polls all permissions frequently so the UI updates live after the
    /// user grants them in System Settings. Screen Recording is the exception —
    /// macOS requires an app restart for that one to take effect.
    private func startPermissionPolling() {
        accessibilityCheckTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.refreshAllPermissions()
            }
        }
    }

    private func bindAudioPowerLevel() {
        audioPowerCancellable = buddyDictationManager.$currentAudioPowerLevel
            .receive(on: DispatchQueue.main)
            .sink { [weak self] powerLevel in
                self?.currentAudioPowerLevel = powerLevel
            }
    }

    private func bindVoiceStateObservation() {
        voiceStateCancellable = buddyDictationManager.$isRecordingFromKeyboardShortcut
            .combineLatest(
                buddyDictationManager.$isFinalizingTranscript,
                buddyDictationManager.$isPreparingToRecord
            )
            .receive(on: DispatchQueue.main)
            .sink { [weak self] isRecording, isFinalizing, isPreparing in
                guard let self else { return }
                // Don't override .responding — the AI response pipeline
                // manages that state directly until streaming finishes.
                guard self.voiceState != .responding else { return }

                if isFinalizing {
                    self.voiceState = .processing
                } else if isRecording {
                    self.voiceState = .listening
                } else if isPreparing {
                    self.voiceState = .processing
                } else {
                    self.voiceState = .idle
                    // If the user pressed and released the hotkey without
                    // saying anything, no response task runs — schedule the
                    // transient hide here so the overlay doesn't get stuck.
                    // Only do this when no response is in flight, otherwise
                    // the brief idle gap between recording and processing
                    // would prematurely hide the overlay.
                    if self.currentResponseTask == nil {
                        self.scheduleTransientHideIfNeeded()
                    }
                }
            }
    }

    private func bindShortcutTransitions() {
        shortcutTransitionCancellable = globalPushToTalkShortcutMonitor
            .shortcutTransitionPublisher
            .receive(on: DispatchQueue.main)
            .sink { [weak self] transition in
                self?.handleShortcutTransition(transition)
            }
    }

    private func handleShortcutTransition(_ transition: BuddyPushToTalkShortcut.ShortcutTransition) {
        if usesRealtimeVoice {
            handleRealtimeShortcutTransition(transition)
            return
        }
        switch transition {
        case .pressed:
            guard !buddyDictationManager.isDictationInProgress else { return }
            // Don't register push-to-talk while the onboarding video is playing
            guard !showOnboardingVideo else { return }

            // Cancel any pending transient hide so the overlay stays visible
            transientHideTask?.cancel()
            transientHideTask = nil

            // If the cursor is hidden, bring it back transiently for this interaction
            // (unless it is docked in the notch — the HUD shows the voice state instead)
            if !isClickyCursorEnabled && !isOverlayVisible && !isCursorDocked {
                overlayWindowManager.hasShownOverlayBefore = true
                overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
                isOverlayVisible = true
            }

            // Dismiss the menu bar panel so it doesn't cover the screen
            NotificationCenter.default.post(name: .clickyDismissPanel, object: nil)

            // Cancel any in-progress response, agent run, and TTS from a previous utterance
            currentResponseTask?.cancel()
            openClickyAgentClient.cancel()
            elevenLabsTTSClient.stopPlayback()
            systemSpeechSynthesizer.stopSpeaking(at: .immediate)
            clearDetectedElementLocation()

            // Dismiss the onboarding prompt if it's showing
            if showOnboardingPrompt {
                withAnimation(.easeOut(duration: 0.3)) {
                    onboardingPromptOpacity = 0.0
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                    self.showOnboardingPrompt = false
                    self.onboardingPromptText = ""
                }
            }
    

            ClickyAnalytics.trackPushToTalkStarted()

            pendingKeyboardShortcutStartTask?.cancel()
            pendingKeyboardShortcutStartTask = Task {
                await buddyDictationManager.startPushToTalkFromKeyboardShortcut(
                    currentDraftText: "",
                    updateDraftText: { _ in
                        // Partial transcripts are hidden (waveform-only UI)
                    },
                    submitDraftText: { [weak self] finalTranscript in
                        self?.lastTranscript = finalTranscript
                        print("🗣️ Companion received transcript: \(finalTranscript)")
                        ClickyAnalytics.trackUserMessageSent(transcript: finalTranscript)
                        self?.sendTranscriptToClaudeWithScreenshot(transcript: finalTranscript)
                    }
                )
            }
        case .released:
            // Cancel the pending start task in case the user released the shortcut
            // before the async startPushToTalk had a chance to begin recording.
            // Without this, a quick press-and-release drops the release event and
            // leaves the waveform overlay stuck on screen indefinitely.
            ClickyAnalytics.trackPushToTalkReleased()
            pendingKeyboardShortcutStartTask?.cancel()
            pendingKeyboardShortcutStartTask = nil
            buddyDictationManager.stopPushToTalkFromKeyboardShortcut()
        case .none:
            break
        }
    }

    // MARK: - Companion Prompt

    private static let companionVoiceResponseSystemPrompt = """
    you're openclicky, a friendly always-on companion that lives in the user's menu bar. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation — you remember everything they've said before.

    rules:
    - default to one or two sentences. be direct and dense. BUT if the user asks you to explain more, go deeper, or elaborate, then go all out — give a thorough, detailed explanation with no length limit.
    - all lowercase, casual, warm. no emojis.
    - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, or formatting — just natural speech.
    - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
    - if the user's question relates to what's on their screen, reference specific things you see.
    - if the screenshot doesn't seem relevant to their question, just answer the question directly.
    - you can help with anything — coding, writing, general knowledge, brainstorming.
    - never say "simply" or "just".
    - don't read out code verbatim. describe what the code does or what needs to change conversationally.
    - focus on giving a thorough, useful explanation. don't end with simple yes/no questions like "want me to explain more?" or "should i show you?" — those are dead ends that force the user to just say yes.
    - instead, when it fits naturally, end by planting a seed — mention something bigger or more ambitious they could try, a related concept that goes deeper, or a next-level technique that builds on what you just explained. make it something worth coming back for, not a question they'd just nod to. it's okay to not end with anything extra if the answer is complete on its own.
    - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is — prioritize that one but reference others if relevant.

    element pointing:
    you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user — if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

    don't point at things when it would be pointless — like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

    when you point, append a coordinate tag at the very end of your response, AFTER your spoken text. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

    format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important — without the screen number, the cursor will point at the wrong place.

    if pointing wouldn't help, append [POINT:none].

    examples:
    - user asks how to color grade in final cut: "you'll want to open the color inspector — it's right up in the top right area of the toolbar. click that and you'll get all the color wheels and curves. [POINT:1100,42:color inspector]"
    - user asks what html is: "html stands for hypertext markup language, it's basically the skeleton of every web page. curious how it connects to the css you're looking at? [POINT:none]"
    - user asks how to commit in xcode: "see that source control menu up top? click that and hit commit, or you can use command option c as a shortcut. [POINT:285,11:source control]"
    - element is on screen 2 (not where cursor is): "that's over on your other monitor — see the terminal window? [POINT:400,300:terminal:screen2]"
    """

    // MARK: - AI Response Pipeline

    /// Captures a screenshot, sends it along with the transcript to Claude,
    /// and plays the response aloud via ElevenLabs TTS. The cursor stays in
    /// the spinner/processing state until TTS audio begins playing.
    /// Claude's response may include a [POINT:x,y:label] tag which triggers
    /// the buddy to fly to that element on screen.
    private func sendTranscriptToClaudeWithScreenshot(transcript: String) {
        currentResponseTask?.cancel()
        elevenLabsTTSClient.stopPlayback()

        currentResponseTask = Task {
            // Stay in processing (spinner) state — no streaming text displayed
            voiceState = .processing

            do {
                // Capture all connected screens so the AI has full context
                let screenCaptures = try await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()

                guard !Task.isCancelled else { return }

                // OpenClicky two-tier routing: the gate decides whether this is a quick question
                // (teacher lane below: Claude + pointing) or real work (agent lane: Codex thread).
                if isAgentModeEnabled && OpenClickyConfiguration.isConfigured {
                    agentActivityText = "deciding…"
                    let lane = await openClickyAgentClient.classifyLane(for: transcript)
                    guard !Task.isCancelled else { return }
                    print("🧭 OpenClicky gate: \(lane)")
                    if lane == "agent" {
                        try await runOpenClickyAgentLane(transcript: transcript, screenCaptures: screenCaptures)
                        if !Task.isCancelled {
                            voiceState = .idle
                            scheduleTransientHideIfNeeded()
                        }
                        return
                    }
                    agentActivityText = nil
                }

                // Build image labels with the actual screenshot pixel dimensions
                // so Claude's coordinate space matches the image it sees. We
                // scale from screenshot pixels to display points ourselves.
                let labeledImages = screenCaptures.map { capture in
                    let dimensionInfo = " (image dimensions: \(capture.screenshotWidthInPixels)x\(capture.screenshotHeightInPixels) pixels)"
                    return (data: capture.imageData, label: capture.label + dimensionInfo)
                }

                // Pass conversation history so Claude remembers prior exchanges
                let historyForAPI = conversationHistory.map { entry in
                    (userPlaceholder: entry.userTranscript, assistantResponse: entry.assistantResponse)
                }

                let (fullResponseText, _) = try await claudeAPI.analyzeImageStreaming(
                    images: labeledImages,
                    systemPrompt: Self.composeTalkInstructions(base: Self.companionVoiceResponseSystemPrompt, skillsBlock: talkSkillsBlock()),
                    conversationHistory: historyForAPI,
                    userPrompt: transcript,
                    onTextChunk: { _ in
                        // No streaming text display — spinner stays until TTS plays
                    }
                )

                guard !Task.isCancelled else { return }

                // Parse the [POINT:...] tag from Claude's response
                let parseResult = Self.parsePointingCoordinates(from: fullResponseText)
                let spokenText = parseResult.spokenText

                // Pick the screen capture matching Claude's screen number,
                // falling back to the cursor screen if not specified.
                let targetScreenCapture: CompanionScreenCapture? = {
                    if let screenNumber = parseResult.screenNumber,
                       screenNumber >= 1 && screenNumber <= screenCaptures.count {
                        return screenCaptures[screenNumber - 1]
                    }
                    return screenCaptures.first(where: { $0.isCursorScreen })
                }()

                if let pointCoordinate = parseResult.coordinate, let targetScreenCapture {
                    pointAt(screenshotPoint: pointCoordinate, label: parseResult.elementLabel, in: targetScreenCapture)
                } else {
                    print("🎯 Element pointing: \(parseResult.elementLabel ?? "no element")")
                }

                // Save this exchange to conversation history (with the point tag
                // stripped so it doesn't confuse future context)
                conversationHistory.append((
                    userTranscript: transcript,
                    assistantResponse: spokenText
                ))

                // Keep only the last 10 exchanges to avoid unbounded context growth
                if conversationHistory.count > 10 {
                    conversationHistory.removeFirst(conversationHistory.count - 10)
                }

                print("🧠 Conversation history: \(conversationHistory.count) exchanges")

                ClickyAnalytics.trackAIResponseReceived(response: spokenText)

                // Play the response via TTS. Keep the spinner (processing state)
                // until the audio actually starts playing, then switch to responding.
                if !spokenText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    do {
                        try await elevenLabsTTSClient.speakText(spokenText)
                        // speakText returns after player.play() — audio is now playing
                        voiceState = .responding
                    } catch {
                        ClickyAnalytics.trackTTSError(error: error.localizedDescription)
                        print("⚠️ TTS via backend failed (\(error.localizedDescription)); using the system voice")
                        speakWithSystemVoice(spokenText)
                    }
                }
            } catch is CancellationError {
                // User spoke again — response was interrupted
            } catch {
                ClickyAnalytics.trackResponseError(error: error.localizedDescription)
                print("⚠️ Companion response error: \(error)")
                speakCreditsErrorFallback()
            }

            if !Task.isCancelled {
                voiceState = .idle
                scheduleTransientHideIfNeeded()
            }
        }
    }

    // MARK: - Realtime push-to-talk

    private func handleRealtimeShortcutTransition(_ transition: BuddyPushToTalkShortcut.ShortcutTransition) {
        switch transition {
        case .pressed:
            guard !showOnboardingVideo else { return }
            transientHideTask?.cancel()
            transientHideTask = nil
            if !isClickyCursorEnabled && !isOverlayVisible && !isCursorDocked {
                overlayWindowManager.hasShownOverlayBefore = true
                overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
                isOverlayVisible = true
            }
            NotificationCenter.default.post(name: .clickyDismissPanel, object: nil)
            currentResponseTask?.cancel()
            openClickyAgentClient.cancel()
            elevenLabsTTSClient.stopPlayback()
            systemSpeechSynthesizer.stopSpeaking(at: .immediate)
            clearDetectedElementLocation()
            ClickyAnalytics.trackPushToTalkStarted()
            voiceState = .listening
            Task { [weak self] in
                guard let self else { return }
                do {
                    try await self.realtimeVoiceClient.connectIfNeeded(mode: self.isAlwaysListening ? .alwaysOn : .pushToTalk)
                    guard self.voiceState == .listening else { return }
                    self.realtimeVoiceClient.beginPushToTalk()
                } catch {
                    print("⚠️ Realtime unavailable (\(error.localizedDescription))")
                    self.voiceState = .idle
                    self.speakWithSystemVoice("Realtime voice is unavailable right now. Check the backend.")
                }
            }
        case .released:
            ClickyAnalytics.trackPushToTalkReleased()
            guard voiceState == .listening else { return }
            voiceState = .processing
            realtimeVoiceClient.endPushToTalk()
        case .none:
            break
        }
    }

    // MARK: - OpenClicky Agent Lane

    /// Hands the request to a Codex thread through the `openclicky` CLI, attaching the cursor
    /// screen as image context, then speaks the agent's final message. The spinner stays up for
    /// the whole run; milestones are published to the panel via `agentActivityText`.
    private func runOpenClickyAgentLane(transcript: String, screenCaptures: [CompanionScreenCapture]) async throws {
        let spokenText = try await performAgentTask(transcript: transcript, screenCaptures: screenCaptures)
        guard !Task.isCancelled else { return }
        do {
            try await elevenLabsTTSClient.speakText(spokenText)
            voiceState = .responding
        } catch {
            print("⚠️ TTS via backend failed after agent run (\(error.localizedDescription)); using the system voice")
            speakWithSystemVoice(spokenText)
        }
    }

    /// The agent lane core, shared by the classic voice path and the Realtime `send_to_agent` tool:
    /// attach the cursor screen, run the Codex thread (resumed across turns), publish milestones and
    /// the result card, and return the sentence to speak.
    private func performAgentTask(transcript: String, screenCaptures: [CompanionScreenCapture]) async throws -> String {
        var screenshotPath: String?
        if let cursorScreenCapture = screenCaptures.first(where: { $0.isCursorScreen }) ?? screenCaptures.first {
            let screenshotURL = FileManager.default.temporaryDirectory
                .appendingPathComponent("openclicky-\(UUID().uuidString).jpg")
            if (try? cursorScreenCapture.imageData.write(to: screenshotURL)) != nil {
                screenshotPath = screenshotURL.path
            }
        }

        agentActivityText = "starting agent…"
        print("🤖 OpenClicky agent lane: \(transcript)")
        let result = try await openClickyAgentClient.runAgent(
            task: transcript,
            screenshotPath: screenshotPath,
            threadId: lastAgentThreadId,
            onEvent: { [weak self] milestone in
                self?.agentActivityText = milestone
            }
        )

        if let threadId = result.threadId {
            lastAgentThreadId = threadId
        }
        lastAgentArtifacts = result.artifacts
        agentActivityText = nil
        lastAgentResult = OpenClickyAgentResultSummary(
            threadId: result.threadId ?? lastAgentThreadId ?? "",
            title: String(transcript.prefix(60)),
            text: result.text.trimmingCharacters(in: .whitespacesAndNewlines),
            artifacts: result.artifacts,
            status: result.status,
            finishedAt: Date()
        )

        var spokenText = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
        if spokenText.isEmpty {
            spokenText = result.status == "completed"
                ? "done."
                : "the agent stopped early: \(result.errorMessage ?? result.status)"
        }
        if !result.artifacts.isEmpty {
            let fileCount = result.artifacts.count
            spokenText += " i saved \(fileCount) \(fileCount == 1 ? "file" : "files")."
        }

        // Keep the teacher lane aware of what the agent did.
        conversationHistory.append((userTranscript: transcript, assistantResponse: spokenText))
        if conversationHistory.count > 10 {
            conversationHistory.removeFirst(conversationHistory.count - 10)
        }
        return spokenText
    }

    /// If the cursor is in transient mode (user toggled "Show OpenClicky" off),
    /// waits for TTS playback and any pointing animation to finish, then
    /// fades out the overlay after a 1-second pause. Cancelled automatically
    /// if the user starts another push-to-talk interaction.
    private func scheduleTransientHideIfNeeded() {
        guard !isClickyCursorEnabled && isOverlayVisible else { return }

        transientHideTask?.cancel()
        transientHideTask = Task {
            // Wait for TTS audio to finish playing
            while elevenLabsTTSClient.isPlaying || systemSpeechSynthesizer.isSpeaking {
                try? await Task.sleep(nanoseconds: 200_000_000)
                guard !Task.isCancelled else { return }
            }

            // Wait for pointing animation to finish (location is cleared
            // when the buddy flies back to the cursor)
            while detectedElementScreenLocation != nil {
                try? await Task.sleep(nanoseconds: 200_000_000)
                guard !Task.isCancelled else { return }
            }

            // Pause 1s after everything finishes, then fade out
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled else { return }
            overlayWindowManager.fadeOutAndHideOverlay()
            isOverlayVisible = false
        }
    }

    /// OpenClicky: speak with the built-in macOS voice when the backend has no text-to-speech
    /// route (for example an OpenRouter-only backend). Keeps the conversation audible.
    private let systemSpeechSynthesizer = AVSpeechSynthesizer()

    private func speakWithSystemVoice(_ text: String) {
        let utterance = AVSpeechUtterance(string: text)
        utterance.rate = AVSpeechUtteranceDefaultSpeechRate
        systemSpeechSynthesizer.stopSpeaking(at: .immediate)
        systemSpeechSynthesizer.speak(utterance)
        voiceState = .responding
    }

    /// Speaks a hardcoded error message using macOS system TTS when API
    /// credits run out. Uses NSSpeechSynthesizer so it works even when
    /// ElevenLabs is down.
    private func speakCreditsErrorFallback() {
        let utterance = "Something went wrong talking to the OpenClicky backend. Check the backend and your token in the settings file."
        let synthesizer = NSSpeechSynthesizer()
        synthesizer.startSpeaking(utterance)
        voiceState = .responding
    }

    // MARK: - Point Tag Parsing

    /// Result of parsing a [POINT:...] tag from Claude's response.
    struct PointingParseResult {
        /// The response text with the [POINT:...] tag removed — this is what gets spoken.
        let spokenText: String
        /// The parsed pixel coordinate, or nil if Claude said "none" or no tag was found.
        let coordinate: CGPoint?
        /// Short label describing the element (e.g. "run button"), or "none".
        let elementLabel: String?
        /// Which screen the coordinate refers to (1-based), or nil to default to cursor screen.
        let screenNumber: Int?
    }

    /// Parses a [POINT:x,y:label:screenN] or [POINT:none] tag from the end of Claude's response.
    /// Returns the spoken text (tag removed) and the optional coordinate + label + screen number.
    static func parsePointingCoordinates(from responseText: String) -> PointingParseResult {
        // Match [POINT:none] or [POINT:123,456:label] or [POINT:123,456:label:screen2]
        let pattern = #"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$"#

        guard let regex = try? NSRegularExpression(pattern: pattern, options: []),
              let match = regex.firstMatch(in: responseText, range: NSRange(responseText.startIndex..., in: responseText)) else {
            // No tag found at all
            return PointingParseResult(spokenText: responseText, coordinate: nil, elementLabel: nil, screenNumber: nil)
        }

        // Remove the tag from the spoken text
        let tagRange = Range(match.range, in: responseText)!
        let spokenText = String(responseText[..<tagRange.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)

        // Check if it's [POINT:none]
        guard match.numberOfRanges >= 3,
              let xRange = Range(match.range(at: 1), in: responseText),
              let yRange = Range(match.range(at: 2), in: responseText),
              let x = Double(responseText[xRange]),
              let y = Double(responseText[yRange]) else {
            return PointingParseResult(spokenText: spokenText, coordinate: nil, elementLabel: "none", screenNumber: nil)
        }

        var elementLabel: String? = nil
        if match.numberOfRanges >= 4, let labelRange = Range(match.range(at: 3), in: responseText) {
            elementLabel = String(responseText[labelRange]).trimmingCharacters(in: .whitespaces)
        }

        var screenNumber: Int? = nil
        if match.numberOfRanges >= 5, let screenRange = Range(match.range(at: 4), in: responseText) {
            screenNumber = Int(responseText[screenRange])
        }

        return PointingParseResult(
            spokenText: spokenText,
            coordinate: CGPoint(x: x, y: y),
            elementLabel: elementLabel,
            screenNumber: screenNumber
        )
    }

    // MARK: - Onboarding

    /// OpenClicky: upstream played a hosted intro video here (the original author's stream) and
    /// scheduled a demo 40s in. OpenClicky ships no video: after the welcome bubble we run the
    /// pointing demo once, then stream in the "press control + option" prompt. Kept the name so
    /// the overlay's call site is unchanged.
    func setupOnboardingVideo() {
        showOnboardingVideo = false
        onboardingVideoPlayer = nil
        ClickyAnalytics.trackOnboardingDemoTriggered()
        performOnboardingDemoInteraction()
        DispatchQueue.main.asyncAfter(deadline: .now() + 6.0) { [weak self] in
            guard let self, !self.showOnboardingPrompt else { return }
            self.startOnboardingPromptStream()
        }
    }

    func tearDownOnboardingVideo() {
        showOnboardingVideo = false
        if let timeObserver = onboardingDemoTimeObserver {
            onboardingVideoPlayer?.removeTimeObserver(timeObserver)
            onboardingDemoTimeObserver = nil
        }
        onboardingVideoPlayer?.pause()
        onboardingVideoPlayer = nil
        if let observer = onboardingVideoEndObserver {
            NotificationCenter.default.removeObserver(observer)
            onboardingVideoEndObserver = nil
        }
    }

    private func startOnboardingPromptStream() {
        let message = "press control + option and introduce yourself"
        onboardingPromptText = ""
        showOnboardingPrompt = true
        onboardingPromptOpacity = 0.0

        withAnimation(.easeIn(duration: 0.4)) {
            onboardingPromptOpacity = 1.0
        }

        var currentIndex = 0
        Timer.scheduledTimer(withTimeInterval: 0.03, repeats: true) { timer in
            guard currentIndex < message.count else {
                timer.invalidate()
                // Auto-dismiss after 10 seconds
                DispatchQueue.main.asyncAfter(deadline: .now() + 10.0) {
                    guard self.showOnboardingPrompt else { return }
                    withAnimation(.easeOut(duration: 0.3)) {
                        self.onboardingPromptOpacity = 0.0
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                        self.showOnboardingPrompt = false
                        self.onboardingPromptText = ""
                    }
                }
                return
            }
            let index = message.index(message.startIndex, offsetBy: currentIndex)
            self.onboardingPromptText.append(message[index])
            currentIndex += 1
        }
    }

    /// Gradually raises an AVPlayer's volume from its current level to the
    /// target over the specified duration, creating a smooth audio fade-in.
    private static let onboardingDemoSystemPrompt = """
    you're openclicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding — look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text" — be specific about exactly what you see.

    make a short quirky 3-6 word observation about the specific thing you picked — something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen — just react to it. keep it to 6 words max, no exceptions.

    CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no menu bar items, no dock icons, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.

    respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.

    format: your comment [POINT:x,y:label]

    the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
    """

    /// Captures a screenshot and asks Claude to find something interesting to
    /// point at, then triggers the buddy's flight animation. Used during
    /// onboarding to demo the pointing feature while the intro video plays.
    func performOnboardingDemoInteraction() {
        // Don't interrupt an active voice response
        guard voiceState == .idle || voiceState == .responding else { return }

        Task {
            do {
                let screenCaptures = try await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()

                // Only send the cursor screen so Claude can't pick something
                // on a different monitor that we can't point at.
                guard let cursorScreenCapture = screenCaptures.first(where: { $0.isCursorScreen }) else {
                    print("🎯 Onboarding demo: no cursor screen found")
                    return
                }

                let dimensionInfo = " (image dimensions: \(cursorScreenCapture.screenshotWidthInPixels)x\(cursorScreenCapture.screenshotHeightInPixels) pixels)"
                let labeledImages = [(data: cursorScreenCapture.imageData, label: cursorScreenCapture.label + dimensionInfo)]

                let (fullResponseText, _) = try await claudeAPI.analyzeImageStreaming(
                    images: labeledImages,
                    systemPrompt: Self.onboardingDemoSystemPrompt,
                    userPrompt: "look around my screen and find something interesting to point at",
                    onTextChunk: { _ in }
                )

                let parseResult = Self.parsePointingCoordinates(from: fullResponseText)

                guard let pointCoordinate = parseResult.coordinate else {
                    print("🎯 Onboarding demo: no element to point at")
                    return
                }

                let displayFrame = cursorScreenCapture.displayFrame
                let globalLocation = Self.screenLocation(forScreenshotPoint: pointCoordinate, in: cursorScreenCapture)

                // Set custom bubble text so the pointing animation uses Claude's
                // comment instead of a random phrase
                detectedElementBubbleText = parseResult.spokenText
                detectedElementScreenLocation = globalLocation
                detectedElementDisplayFrame = displayFrame
                detectedElementPointToken &+= 1
                print("🎯 Onboarding demo: pointing at \"\(parseResult.elementLabel ?? "element")\" — \"\(parseResult.spokenText)\"")
            } catch {
                print("⚠️ Onboarding demo error: \(error)")
            }
        }
    }
}
