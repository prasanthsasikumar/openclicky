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
    @Published private(set) var voiceState: CompanionVoiceState = .idle {
        didSet {
            // A guide whose next step never came (the request failed or was cancelled) is over:
            // nothing is being said or fetched and no step waits, so the stop strip must not stay.
            if voiceState == .idle && guidedStepWatcher == nil && guideStepUnderWay != nil {
                guideStepUnderWay = nil
            }
        }
    }
    @Published private(set) var lastTranscript: String?
    /// Who is answering the question being spoken ("Claude Code", "free credits"), for the island.
    @Published private(set) var answeringSourceName: String?
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

    // MARK: - Onboarding Prompt Bubble

    /// Text streamed character-by-character on the cursor after the onboarding demo.
    @Published var cursorCaptionText: String = ""
    @Published var cursorCaptionOpacity: Double = 0.0
    @Published var isCursorCaptionVisible: Bool = false

    let buddyDictationManager = BuddyDictationManager()
    let globalPushToTalkShortcutMonitor = GlobalPushToTalkShortcutMonitor()
    let overlayWindowManager = OverlayWindowManager()
    // Response text is now displayed inline on the cursor overlay via
    // streamingResponseText, so no separate response overlay manager is needed.

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

    /// Always-on listening (server VAD, barge-in) instead of push-to-talk. Session-only: a launch
    /// always starts in push-to-talk, so a forgotten hands-free toggle never greets the user out
    /// of nowhere the next day. (The old persisted key is cleared so nothing reads it again.)
    @Published var isAlwaysListening: Bool = {
        UserDefaults.standard.removeObject(forKey: "isOpenClickyAlwaysListening")
        return false
    }()

    /// The menu bar icon is off by default: the notch HUD is the app's home. The onboarding panel
    /// still opens on its own for a first run; permissions are asked for on the island.
    /// On by default since dictation: the menu bar is where the window, history and settings open
    /// from. Shown once more to everyone who had it off before the dictation release (the
    /// `dictation.menuBarIconShownOnce` mark), who can turn it off again in the HUD's settings.
    @Published var isMenuBarIconVisible: Bool = {
        let defaults = UserDefaults.standard
        if !defaults.bool(forKey: "dictation.menuBarIconShownOnce") {
            defaults.set(true, forKey: "dictation.menuBarIconShownOnce")
            defaults.set(true, forKey: "isOpenClickyMenuBarIconVisible")
            return true
        }
        return defaults.object(forKey: "isOpenClickyMenuBarIconVisible") == nil ? true : defaults.bool(forKey: "isOpenClickyMenuBarIconVisible")
    }()

    func setMenuBarIconVisible(_ visible: Bool) {
        isMenuBarIconVisible = visible
        UserDefaults.standard.set(visible, forKey: "isOpenClickyMenuBarIconVisible")
    }

    /// Where the island lives: the notch, a side drawer, or hidden while idle.
    @Published private(set) var hudPlacement: HUDPlacement = .stored

    func setHUDPlacement(_ placement: HUDPlacement) {
        hudPlacement = placement
        UserDefaults.standard.set(placement.rawValue, forKey: HUDPlacement.defaultsKey)
        notchHUDManager.setPlacement(placement)
        // With the island hidden the menu bar icon is the way into the panel and the window.
        if placement == .hidden { setMenuBarIconVisible(true) }
    }

    #if DEBUG
    /// The UI tour shows the busy strip without talking.
    func debugSetVoiceState(_ state: CompanionVoiceState) { voiceState = state }
    #endif

    // MARK: - Quitting

    private var isQuitting = false

    /// Quits with a goodbye: a short chime and a line typed into the island that tells how to
    /// bring OpenClicky back (no voice), then the app terminates. A second quit while the goodbye
    /// shows quits at once.
    func quitWithFarewell() {
        guard !isQuitting else {
            NSApp.terminate(nil)
            return
        }
        isQuitting = true
        let farewell = Farewell.random()
        notchHUDManager.closeTextComposer()
        notchHUDManager.showGoodbye(farewell.text)
        systemSpeechSynthesizer.stopSpeaking(at: .immediate)
        dictationTakeController.earcons.play(.goodbye)
        // Long enough to type the line (30 ms a character) and read it, never long enough to feel stuck.
        let typingSeconds = Double(farewell.text.count) * 0.03
        let lingerSeconds = min(max(typingSeconds + 1.6, 2.6), 4.5)
        DispatchQueue.main.asyncAfter(deadline: .now() + lingerSeconds) {
            NSApp.terminate(nil)
        }
    }

    let realtimeVoiceClient = RealtimeVoiceClient()
    /// The user's skill library (~/.openclicky/skills) plus the bundled app-teaching skills.
    let skillLibraryStore = SkillLibraryStore()
    private var realtimeLevelCancellable: AnyCancellable?
    private var didGreetRealtime = false
    private var didConfigureRealtimeCallbacks = false

    private var usesRealtimeVoice: Bool { isRealtimeVoiceEnabled && OpenClickyConfiguration.isConfigured && AccountCapabilities.current().usesRealtime }

    func setRealtimeVoiceEnabled(_ enabled: Bool) {
        isRealtimeVoiceEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: "isOpenClickyRealtimeVoiceEnabled")
        if enabled { warmUpRealtimeVoice() } else { realtimeVoiceClient.disconnect(reason: "realtime voice turned off") }
    }

    /// Settings → Voice picked another fast voice vendor: the next connection is made with it.
    func realtimeProviderChanged() {
        warmUpRealtimeVoice()
    }

    func setAlwaysListening(_ enabled: Bool) {
        isAlwaysListening = enabled
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
                    let languageName = ReplyLanguage.englishName(for: ReplyLanguage.currentCode)
                    self.realtimeVoiceClient.requestResponse(instructions: "Greet the user in \(languageName) in one short sentence as OpenClicky.")
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
                AppLog.append("talk: you said: \(text.prefix(300))")
                TeacherPlugin.startLesson(for: text, capture: self.realtimeVoiceClient.lastScreenCapture)
            case .assistant:
                AppLog.append("talk: openclicky said: \(text.prefix(300))")
                TeacherPlugin.writeAnswerIfTheyCannotHear(text, questionWasTyped: self.lastQuestionWasTyped, capture: self.realtimeVoiceClient.lastScreenCapture)
                self.rememberExchange(userTranscript: self.lastTranscript ?? "", assistantResponse: text)
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
        realtimeVoiceClient.onStopListeningRequested = { [weak self] in
            self?.endHandsFreeBySignOff()
        }
        realtimeVoiceClient.frontAppProvider = { NSWorkspace.shared.frontmostApplication?.processIdentifier }
        // A socket that died under a turn used to leave voiceState on processing for good, which
        // also refused dictation. Unstick it and say why; the next press reconnects.
        realtimeVoiceClient.onTurnHeardNothing = { [weak self] in
            guard let self, self.usesRealtimeVoice, self.voiceState == .processing else { return }
            self.voiceState = .idle
            self.streamCursorCaption("Didn't catch that. Hold ⌃ control + ⌥ option while you talk.", holdSeconds: 5)
            self.scheduleTransientHideIfNeeded()
        }
        realtimeVoiceClient.onConnectionLost = { [weak self] reason in
            guard let self, self.usesRealtimeVoice else { return }
            AppLog.append("realtime connection lost: \(reason)")
            guard self.voiceState != .idle else { return }
            self.voiceState = .idle
            self.streamCursorCaption("Lost the connection to \(self.realtimeVoiceClient.providerName). Press again to reconnect.", holdSeconds: 6)
            self.scheduleTransientHideIfNeeded()
        }
        realtimeVoiceClient.screenContextProvider = {
            await CompanionScreenCaptureUtility.captureCursorScreenContext()
        }
        realtimeVoiceClient.instructionsProvider = { [weak self] in
            guard let self else { return RealtimeVoiceClient.defaultInstructions }
            return Self.composeTalkInstructions(
                base: Self.withReplyLanguage(self.realtimeVoiceClient.baseInstructions + Self.pluginPromptLines()),
                skillsBlock: self.talkSkillsBlock())
        }
        realtimeVoiceClient.writingTargetProvider = {
            await WritingTarget.captureFromFrontApp()
        }
        realtimeVoiceClient.pluginToolsProvider = {
            TeacherPlugin.isEnabled ? TeacherPlugin.realtimeToolDefinitions() : []
        }
        realtimeVoiceClient.onPluginToolCall = { toolName, arguments, capture in
            await TeacherPlugin.perform(toolName: toolName, arguments: arguments, capture: capture)
        }
        TeacherBoard.shared.onLabelWritten = { [weak self] screenLocation, displayFrame in
            self?.pointAt(screenLocation: screenLocation, displayFrame: displayFrame, label: nil)
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
        realtimeVoiceClient.onMacAction = { [weak self] action in
            guard let self else { return .failed("OpenClicky is not available") }
            let outcome = await self.macActionRunner.perform(action)
            if outcome.didSomething { self.lastSuccessfulQuickActionAt = Date() }
            return outcome
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
        // Apps the user answered "No" to on the connect card keep their skill out of the prompt.
        let declinedSkillIds = appConnectPromptController.declinedSkillIds
        let appSkill = AppSkillMatcher.match(front, in: skillLibraryStore.appSkills.filter { $0.isForTalk && !declinedSkillIds.contains($0.id) })
        let block = SkillPromptBuilder.build(activeSkills: skillLibraryStore.activeTalkSkills, appSkill: appSkill, front: front)
        if let appSkill { print("🧩 Skills: app skill \"\(appSkill.name)\" for \(front.bundleIdentifier ?? "?")\(front.url?.host.map { " / " + $0 } ?? "")") }
        return block
    }

    /// Base prompt + skills block, separated by a blank line; the base alone when there is nothing to add.
    /// Both lanes' prompts end with the one language rule. See ReplyLanguage.swift.
    nonisolated static func withReplyLanguage(_ base: String, code: String? = nil) -> String {
        base + "\n\n" + ReplyLanguage.instruction(for: code ?? ReplyLanguage.normalizedCode(OpenClickyConfiguration.settings.language))
    }

    nonisolated static func composeTalkInstructions(base: String, skillsBlock: String) -> String {
        let trimmed = skillsBlock.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? base : base + "\n\n" + trimmed
    }

    // MARK: - Notch HUD + the pointer's flights (OpenClicky)

    /// The notch HUD: a lip under the notch that opens on hover / while busy.
    let notchHUDManager = NotchHUDManager()
    /// Performs the fast local actions the Realtime session asks for (see MacActions.swift).
    private let macActionRunner = MacActionRunner.live(
        workspaceDirectory: URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)
    )
    /// The local ask rung (`LocalAskRung`: Apple's on-device model tried before the cloud for quick
    /// actions and rewrites). Built on first use and kept; while the Mac has no model (Intel, an
    /// older macOS, Apple Intelligence off or still downloading) it stays nil and is tried again
    /// at the next ask, so a model that finishes downloading is picked up without a relaunch.
    private var builtLocalAskRung: LocalAskRung?
    private var localAskRung: LocalAskRung? {
        if builtLocalAskRung == nil, let model = LocalAskModelAvailability.makeModel() {
            // The app list and the default browser are read here, on the main actor, so the rung's
            // child task resolves a spoken name without touching AppKit.
            let installedAppNames = InstalledAppResolver.installedAppNamesOnThisMac()
            let defaultBrowserName = InstalledAppResolver.defaultBrowserNameOnThisMac()
            builtLocalAskRung = LocalAskRung(model: model, resolveApp: { spokenName in
                InstalledAppResolver.resolve(spoken: spokenName, installedAppNames: installedAppNames, defaultBrowserName: defaultBrowserName)
            })
            AppLog.append("local rung: ready (\(model.displayName))")
        }
        return builtLocalAskRung
    }
    static let localAnswerSourceName = "this Mac"
    static let textAlreadyReadsWellSentence = "that already reads well, so i left it as it is."
    /// The permission cards the island shows one at a time until all four are granted.
    let permissionPromptController = PermissionPromptController()
    /// The "drag me into the Accessibility list" panel the accessibility card opens.
    private let accessibilityDragPanelController = AccessibilityDragPanelController()

    /// Opens the "Connect <app> to OpenClicky" card in the HUD when a supported app or site comes to the front.
    let appConnectPromptController = AppConnectPromptController()

    /// The leg of a buddy flight in progress, if any. Each display has its own overlay, so a
    /// flight to another display is split at the display edges; the overlay whose screen matches
    /// `screenFrame` animates the leg and reports back when it is done.
    @Published private(set) var cursorFlightLeg: CursorFlightLeg?
    private var cursorFlightSequence = 0

    /// Where a freshly shown overlay should start the buddy (the notch) before flying out.
    var cursorLaunchOriginScreenLocation: CGPoint?

    /// What "dock the pointer in the island" left in UserDefaults. The option is gone (2026-10-28:
    /// keeping the pointer parked added nothing; it rests out of sight and comes back on a shake or
    /// when Clicky needs it), so the key is only read once, to clear it.
    static let retiredDockedPointerKey = "isOpenClickyCursorDocked"

    /// Clears the retired dock flag; true when there was one to clear (someone who had docked).
    static func forgetRetiredDockSetting(in defaults: UserDefaults) -> Bool {
        guard defaults.object(forKey: retiredDockedPointerKey) != nil else { return false }
        defaults.removeObject(forKey: retiredDockedPointerKey)
        return true
    }

    // MARK: Cursor flights between displays

    /// Starts a flight from the overlay on `fromScreenFrame` to the mouse pointer. When the mouse is
    /// on another display the flight gets two legs: to the nearest edge of this display, then
    /// across the mouse's display.
    func beginCursorFlight(_ purpose: CursorFlightLeg.Purpose, fromScreenFrame: CGRect) {
        guard cursorFlightLeg == nil else { return }
        let (destination, destinationScreenFrame) = cursorFlightDestination(for: purpose, fallbackScreenFrame: fromScreenFrame)
        cursorFlightSequence += 1
        cursorFlightLeg = CursorFlightPlanner.firstLeg(
            purpose: purpose,
            fromScreenFrame: fromScreenFrame,
            destination: destination,
            destinationScreenFrame: destinationScreenFrame,
            sequence: cursorFlightSequence
        )
    }

    /// Called by the overlay that finished a non-final leg: the destination display's overlay
    /// takes over from the matching edge.
    func continueCursorFlight(afterLeg finishedLeg: CursorFlightLeg) {
        guard finishedLeg == cursorFlightLeg, !finishedLeg.isFinalLeg else { return }
        let (destination, destinationScreenFrame) = cursorFlightDestination(for: finishedLeg.purpose, fallbackScreenFrame: finishedLeg.screenFrame)
        cursorFlightSequence += 1
        cursorFlightLeg = CursorFlightPlanner.finalLeg(
            purpose: finishedLeg.purpose,
            afterExitPoint: finishedLeg.endScreenLocation,
            destination: destination,
            destinationScreenFrame: destinationScreenFrame,
            sequence: cursorFlightSequence
        )
    }

    /// Called by the overlay when the flight it was animating ended (or was cancelled).
    func finishCursorFlight(onScreenFrame screenFrame: CGRect) {
        guard let cursorFlightLeg, cursorFlightLeg.screenFrame == screenFrame else { return }
        self.cursorFlightLeg = nil
    }

    private func cursorFlightDestination(for purpose: CursorFlightLeg.Purpose, fallbackScreenFrame: CGRect) -> (CGPoint, CGRect) {
        switch purpose {
        case .toMouse:
            let mouseLocation = NSEvent.mouseLocation
            let mouseScreenFrame = NSScreen.screens.first(where: { $0.frame.contains(mouseLocation) })?.frame ?? fallbackScreenFrame
            return (mouseLocation, mouseScreenFrame)
        }
    }

    /// The connect card's "Yes": make sure Codex is logged into Composio (opens Composio's page in
    /// the browser the first time on this Mac), then hand the agent the account-connection task.
    private func connectIntegrationThenRun(_ task: String) {
        Task { @MainActor in
            do {
                let status = try await openClickyAgentClient.integrationsStatus()
                if status.configured && !status.loggedIn {
                    agentActivityText = "Signing in to Composio…"
                    try await openClickyAgentClient.loginIntegration()
                    agentActivityText = nil
                }
            } catch {
                agentActivityText = nil
                print("⚠️ Composio login failed: \(error.localizedDescription)")
            }
            submitTextToAgent(task)
        }
    }

    /// Text entry point into the agent lane (result-card follow-ups, future text mode). Captures the
    /// screen like the voice path does, resumes `threadId` when given, and speaks the result.
    /// `startsNewThread` (the Agents tab's composer with no thread selected) forgets the thread the
    /// last turn ran on, so the request opens a fresh one instead of continuing it.
    func submitTextToAgent(_ text: String, threadId: String? = nil, startsNewThread: Bool = false) {
        let trimmedText = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedText.isEmpty, OpenClickyConfiguration.isConfigured else { return }
        // An account has no agent: the backend would refuse it with not_on_plan, so say that plainly.
        guard AccountCapabilities.current().usesAgent else {
            speakWithSystemVoice(AccountLimitError.notOnPlan.message)
            return
        }
        interruptCurrentAnswer(includingAgentWork: true)
        if let threadId { lastAgentThreadId = threadId } else if startsNewThread { lastAgentThreadId = nil }
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
            await waitForSystemVoiceNoticeToEnd()
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
        let location = Self.screenLocation(forScreenshotPoint: screenshotPoint, in: capture)
        pointAt(screenLocation: location, displayFrame: capture.displayFrame, label: label)
        let formatted = { (value: CGFloat) in String(format: "%.0f", value) }
        print("🎯 Element pointing: (\(formatted(screenshotPoint.x)), \(formatted(screenshotPoint.y))) → \"\(label ?? "element")\"")
    }

    /// Fly the buddy to a place in AppKit screen coordinates (the Teacher's labels, which it
    /// placed itself, have no screenshot point).
    func pointAt(screenLocation location: CGPoint, displayFrame: CGRect, label: String?) {
        // Granting a permission is the person's choice, never a step: no pointing at macOS's prompts.
        let mainDisplayHeight = NSScreen.screens.first?.frame.height ?? 0
        if SystemPromptGuard.isOnSystemPrompt(appKitPoint: location, mainDisplayHeight: mainDisplayHeight) {
            AppLog.append("point: not shown, it was on a macOS permission prompt")
            return
        }
        // The spinner (processing) hides the triangle, so the flight would be invisible; the
        // buddy is visible in idle and responding.
        if voiceState == .processing { voiceState = .idle }

        let trimmedLabel = label?.trimmingCharacters(in: .whitespacesAndNewlines)
        detectedElementBubbleText = (trimmedLabel?.isEmpty == false) ? trimmedLabel : nil
        detectedElementDisplayFrame = displayFrame
        detectedElementScreenLocation = location
        // A guide checks the click that finishes this step against it.
        guidedStepPointedLocation = location
        detectedElementPointToken &+= 1
        ClickyAnalytics.trackElementPointed(elementLabel: trimmedLabel)
    }

    /// The prompt lines the switched-on plugins add to both voice lanes.
    static func pluginPromptLines() -> String {
        TeacherPlugin.isEnabled ? "\n\n" + TeacherPlugin.promptInstruction : ""
    }

    /// Moves the pointer for the ask lane's [POINT:…] tag, on the screen the tag names (the cursor's
    /// screen when it names none).
    private func pointAtAnswerTag(_ pointTag: String, screenCaptures: [CompanionScreenCapture]) {
        let parseResult = Self.parsePointingCoordinates(from: pointTag)
        guard let pointCoordinate = parseResult.coordinate else { return }
        let targetScreenCapture: CompanionScreenCapture? = {
            if let screenNumber = parseResult.screenNumber, screenNumber >= 1 && screenNumber <= screenCaptures.count {
                return screenCaptures[screenNumber - 1]
            }
            return screenCaptures.first(where: { $0.isCursorScreen })
        }()
        guard let targetScreenCapture else { return }
        pointAt(screenshotPoint: pointCoordinate, label: parseResult.elementLabel, in: targetScreenCapture)
    }

    // Both read the backend URL per request: built once from it, they kept asking the old
    // backend after shell.json changed while everything else followed the new one.
    private lazy var claudeAPI: ClaudeAPI = {
        return ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" }, model: selectedModel)
    }()

    private lazy var answerVoicePlayer: AnswerVoicePlayer = {
        return AnswerVoicePlayer(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/tts" })
    }()

    /// Conversation history so Claude remembers prior exchanges within a session.
    /// Each entry is the user's transcript and Claude's response.
    private var conversationHistory: [(userTranscript: String, assistantResponse: String)] = []
    /// The last question sent down the ask lane and when: the same one again soon after means the
    /// answer missed what they meant (`RepeatedQuestion`).
    private var lastAskQuestion: (transcript: String, askedAt: Date)?
    /// Account notices (a limit, the free service's trouble) already spoken this session: each is
    /// said once, then the lane falls back silently.
    private var spokenAccountNotices: Set<String> = []

    /// The question in flight was typed (the composer), not spoken: they may not be able to hear
    /// either, so the Teacher writes the answer down (`TeacherListening`).
    private var lastQuestionWasTyped = false

    /// The currently running AI response task, if any. Cancelled when the user
    /// speaks again so a new response can begin immediately.
    private var currentResponseTask: Task<Void, Never>?

    /// The answer being spoken (the ask lane's, or the agent's result), kept so a new question
    /// stops it outright. Stopping only the player ends the current piece "normally", and the queue
    /// went on to the next piece (and kept fetching every remaining piece from /tts).
    private var activeAnswerSpeaker: StreamingAnswerSpeaker?
    private var activeAnswerPlayback: Task<Void, Error>?

    /// The island's stop button: silence whatever Clicky is saying. Agent work keeps going.
    func stopSpeaking() {
        guard voiceState == .responding else { return }
        AppLog.append("ask: stopped speaking from the island")
        interruptCurrentAnswer(includingAgentWork: false)
        realtimeVoiceClient.stopSpeaking()
        // A cancelled answer skips its own reset to idle.
        voiceState = .idle
        scheduleTransientHideIfNeeded()
    }

    /// Silences everything this manager is saying and abandons the answer in progress: the queued
    /// pieces of a spoken answer, the response task, the answer voice, and a system-voice or
    /// recorded notice. `includingAgentWork` also ends a running agent turn: a new question
    /// replaces it, while the island's stop button only silences the voice and leaves the agent
    /// working. The realtime session is left alone: its next turn cancels its own reply, and only
    /// the stop button silences it outright.
    private func interruptCurrentAnswer(includingAgentWork: Bool) {
        stopWatchingForGuidedStep()
        settingsTour.stop()
        stopActiveAnswer()
        currentResponseTask?.cancel()
        isAskInFlight = false
        askIslandStatus = nil
        if includingAgentWork { openClickyAgentClient.cancel() }
        answerVoicePlayer.stopPlayback()
        stopSystemVoice()
    }

    /// Stops the spoken answer, its remaining /tts fetches, and its pointer — before the player is
    /// stopped, so the queue cannot move on to the next piece.
    private func stopActiveAnswer() {
        activeAnswerSpeaker?.cancel()
        activeAnswerPlayback?.cancel()
        activeAnswerSpeaker = nil
        activeAnswerPlayback = nil
    }

    private var shortcutTransitionCancellable: AnyCancellable?
    private var voiceStateCancellable: AnyCancellable?
    /// Loads Kokoro when a question starts (KokoroVoice.prepareForAnswer).
    private var kokoroPreparationCancellable: AnyCancellable?
    /// Counts uses for the island's one-time "open at login?" offer (FirstRunTourController.noteUse).
    private var useCountingCancellables = Set<AnyCancellable>()
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

    /// The four flags as the permission cards see them.
    var permissionStatus: PermissionStatus {
        PermissionStatus(
            microphone: hasMicrophonePermission,
            accessibility: hasAccessibilityPermission,
            screenRecording: hasScreenRecordingPermission,
            // While the quiet capture is checking, its card is held back: after the Screen
            // Recording relaunch it flashed up for a second before the check said yes.
            screenContent: hasScreenContentPermission || isCheckingScreenContentQuietly
        )
    }

    /// Whether the blue cursor overlay is currently visible on screen.
    /// Used by the panel to show accurate status text ("Active" vs "Ready").
    @Published private(set) var isOverlayVisible: Bool = false

    /// Puts the cursor overlay on every display. `hasShownOverlayBefore` is set first so the
    /// overlay skips its first-launch welcome bubble: every caller here shows the buddy for a
    /// reason of its own (a question, a caption, a flight), never as a first launch.
    private func showOverlayOnAllScreens() {
        overlayWindowManager.hasShownOverlayBefore = true
        overlayWindowManager.showOverlay(onScreens: NSScreen.screens, companionManager: self)
        isOverlayVisible = true
    }

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
            showOverlayOnAllScreens()
        } else {
            overlayWindowManager.hideOverlay()
            isOverlayVisible = false
        }
    }

    // MARK: - Pointer presence

    /// When the pointer is on screen while it follows the mouse. Persisted to UserDefaults.
    @Published private(set) var pointerPresence: PointerPresence =
        PointerPresence(rawValue: UserDefaults.standard.string(forKey: "openClickyPointerPresence") ?? "") ?? .always

    /// False while the pointer is resting away (the mouse has been still, or has not been shaken
    /// yet). The overlays fade the following pointer with it; flights and conversations ignore it.
    @Published private(set) var isPointerAwake: Bool = true

    private var pointerPresenceTracker = PointerPresenceTracker(presence: .always)
    private var pointerPresenceTimer: Timer?

    func setPointerPresence(_ presence: PointerPresence) {
        pointerPresence = presence
        UserDefaults.standard.set(presence.rawValue, forKey: "openClickyPointerPresence")
        pointerPresenceTracker = PointerPresenceTracker(presence: presence)
        isPointerAwake = pointerPresenceTracker.isAwake
        startPointerPresenceMonitor()
    }

    /// One use: counted for the island's "open at login?" offer, and for the pointer's default.
    private func noteUse() {
        firstRunTour.noteUse()
        hidePointerUntilShakeOnceItIsFamiliar()
    }

    /// After the first few uses the pointer stops following the mouse all the time and waits for a
    /// shake, unless the person picked a setting themselves. Said once, after the answer or paste.
    private func hidePointerUntilShakeOnceItIsFamiliar() {
        let useCount = UserDefaults.standard.integer(forKey: FirstRunTourController.useCountKey)
        let personChoseOne = UserDefaults.standard.string(forKey: "openClickyPointerPresence") != nil
        guard let newPresence = PointerPresenceDefault.presenceToSwitchTo(
            useCount: useCount, personChoseOne: personChoseOne, current: pointerPresence) else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [weak self] in
            guard let self, self.voiceState == .idle, self.pointerPresence == .always,
                  UserDefaults.standard.string(forKey: "openClickyPointerPresence") == nil else { return }
            AppLog.append("pointer: hides until a shake now, after \(useCount) uses")
            self.setPointerPresence(newPresence)
            self.showQuietNotice("from now on i'll stay out of the way. shake the mouse when you want me. you can change this in settings, clicky.")
        }
    }

    /// Show the pointer now; it rests away again as the presence setting says.
    func wakePointer() {
        pointerPresenceTracker.wake(at: ProcessInfo.processInfo.systemUptime)
        if !isPointerAwake { isPointerAwake = true }
    }

    /// Samples the mouse at 30 Hz while a presence other than "always" is chosen; "always" needs
    /// no sampling at all.
    private func startPointerPresenceMonitor() {
        pointerPresenceTimer?.invalidate()
        pointerPresenceTimer = nil
        guard pointerPresence != .always else {
            isPointerAwake = true
            return
        }
        pointerPresenceTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let awake = self.pointerPresenceTracker.update(mouse: NSEvent.mouseLocation, at: ProcessInfo.processInfo.systemUptime)
                if awake != self.isPointerAwake { self.isPointerAwake = awake }
            }
        }
    }

    /// Whether the user has completed onboarding at least once. Persisted
    /// to UserDefaults so the Start button only appears on first launch.
    var hasCompletedOnboarding: Bool {
        get { UserDefaults.standard.bool(forKey: "hasCompletedOnboarding") }
        set { UserDefaults.standard.set(newValue, forKey: "hasCompletedOnboarding") }
    }

    /// An account hears on the Mac (Apple speech); own keys keep the configured provider.
    private func applyHearingForAccount() {
        buddyDictationManager.replaceTranscriptionProvider(
            AccountCapabilities.current().hearsOnDevice
                ? AppleSpeechTranscriptionProvider()
                : BuddyTranscriptionProviderFactory.makeDefaultProvider()
        )
    }

    func start() {
        refreshAllPermissions()
        pointerPresenceTracker = PointerPresenceTracker(presence: pointerPresence)
        isPointerAwake = pointerPresenceTracker.isAwake
        startPointerPresenceMonitor()
        print("🔑 OpenClicky start, accessibility: \(hasAccessibilityPermission), screen: \(hasScreenRecordingPermission), mic: \(hasMicrophonePermission), screenContent: \(hasScreenContentPermission), onboarded: \(hasCompletedOnboarding)")
        startPermissionPolling()
        bindVoiceStateObservation()
        bindAudioPowerLevel()
        bindShortcutTransitions()
        bindAskDictationOutcome()
        applyHearingForAccount()
        // A key or token changed in shell.json: the open Realtime session was minted with the old
        // one, so replace it rather than keep using it until the next relaunch.
        OpenClickyConfiguration.startWatchingSettingsFile()
        // Chrome and the Electron apps report nothing focused until told an assistive client is
        // here; every one coming to the front is told, so dictation and rewrites can read its box.
        ChromiumAccessibility.startWakingFrontApps()
        NotificationCenter.default.addObserver(
            forName: OpenClickyConfiguration.credentialsChangedNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                AppLog.append("shell.json credentials changed; reconnecting realtime")
                self.applyHearingForAccount()
                if self.usesRealtimeVoice { self.warmUpRealtimeVoice() }
            }
        }
        // The backend's /billing/me answer decides the account profile; it can arrive after launch.
        NotificationCenter.default.addObserver(
            forName: AccountProfileStore.profileChangedNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.applyHearingForAccount() }
        }
        AccountProfileStore.shared.showNotice = { [weak self] notice in self?.showQuietNotice(notice) }
        AccountProfileStore.shared.start()
        // Eagerly touch the Claude API so its TLS warmup handshake completes
        // well before the onboarding demo fires at ~40s into the video.
        _ = claudeAPI

        // If the user already completed onboarding AND all permissions are
        // still granted, show the cursor overlay immediately. If permissions
        // were revoked (e.g. signing change), don't show the cursor — the
        // island's permission cards ask for them back first.
        // Someone who had the pointer docked gets it back the way everyone has it: resting out of
        // sight until a shake or Clicky needs it ("the pointer shows" decides).
        if Self.forgetRetiredDockSetting(in: .standard) {
            AppLog.append("pointer: the retired dock setting was cleared")
        }
        if hasCompletedOnboarding && allPermissionsGranted && isClickyCursorEnabled {
            showOverlayOnAllScreens()
        }

        // OpenClicky: the notch HUD is always available; it needs no permissions.
        notchHUDManager.show(companionManager: self)
        startDictation()
        startPermissionPrompts()
        if OpenClickyConfiguration.connectorsEnabled {
            appConnectPromptController.start(
                skillLibraryStore: skillLibraryStore,
                notchHUDManager: notchHUDManager,
                submitToAgent: { [weak self] task in self?.connectIntegrationThenRun(task) }
            )
        }

        // OpenClicky: keep the Realtime voice session warm so talking is instant.
        if hasCompletedOnboarding && allPermissionsGranted {
            warmUpRealtimeVoice()
        }
    }

    func clearDetectedElementLocation() {
        detectedElementScreenLocation = nil
        detectedElementDisplayFrame = nil
        detectedElementBubbleText = nil
    }

    func stop() {
        orbPanelManager.stop()
        clipboardHistoryMonitor.stop()
        permissionPromptController.stop()
        appConnectPromptController.stop()
        notchHUDManager.hide()
        realtimeVoiceClient.disconnect(reason: nil)
        globalPushToTalkShortcutMonitor.stop()
        buddyDictationManager.cancelCurrentDictation()
        overlayWindowManager.hideOverlay()
        transientHideTask?.cancel()

        interruptCurrentAnswer(includingAgentWork: false)
        currentResponseTask = nil
        shortcutTransitionCancellable?.cancel()
        companionShortcutCancellable?.cancel()
        dismissCursorCaption()
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
            print("🔑 Permissions, accessibility: \(hasAccessibilityPermission), screen: \(hasScreenRecordingPermission), mic: \(hasMicrophonePermission), screenContent: \(hasScreenContentPermission)")
        }

        // Track individual permission grants as they happen
        if !previouslyHadAccessibility && hasAccessibilityPermission {
            ClickyAnalytics.trackPermissionGranted(permission: "accessibility")
            // First of the permission cards: from here the pointer can show, and help with the rest.
            showPointerDuringSetupIfWanted(flyingFromTheNotch: true)
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

        permissionPromptController.update(with: permissionStatus)
    }

    /// Wires the permission cards to the island: the drag helper they open, and the one request
    /// macOS has no call for (Screen Content is granted by actually capturing something).
    private func startPermissionPrompts() {
        permissionPromptController.dragHelper = accessibilityDragPanelController
        permissionPromptController.performRequest = { [weak self] step in
            guard let self else { return }
            if step == .screenContent {
                self.requestScreenContentPermission()
            } else {
                PermissionPromptController.requestFromSystem(step)
            }
        }
        permissionPromptController.openSettings = { [weak self] in self?.showSettings() }
        // Back after the Screen Recording relaunch: check Screen Content now, before any card shows.
        checkScreenContentQuietlyOnce()
        // Accessibility already granted (a relaunch partway through setup): the pointer is back.
        if hasAccessibilityPermission { showPointerDuringSetupIfWanted(flyingFromTheNotch: false) }
        permissionPromptController.start(notchHUDManager: notchHUDManager)
        permissionPromptController.update(with: permissionStatus)
        // The welcome tour waits for the last permission, then takes the island.
        firstRunTour.start(companionManager: self)
        // Kokoro comes into memory only when a question starts: pressing the ask shortcut or sending
        // a typed question. Hands-free answers in OpenAI's realtime voice and never needs it.
        kokoroPreparationCancellable = $voiceState
            .removeDuplicates()
            .sink { [weak self] state in
                guard let self, state == .listening || state == .processing, !self.isAlwaysListening else { return }
                KokoroVoice.shared.prepareForAnswer()
                // The on-device model warms while the person is still speaking, so the local ask
                // rung's decision fits inside the pause that follows the release.
                if state == .listening, self.dictationSettings.answerSimpleRequestsOnThisMac {
                    self.localAskRung?.model.prewarm()
                }
            }
        // A spoken answer and a dictation that produced words each count as a use.
        $voiceState
            .removeDuplicates()
            .filter { $0 == .responding }
            .sink { [weak self] _ in self?.noteUse() }
            .store(in: &useCountingCancellables)
        dictationTakeController.$lastTake
            .compactMap { $0 }
            .filter { $0.status != .failed && !$0.displayText.isEmpty }
            .sink { [weak self] _ in self?.noteUse() }
            .store(in: &useCountingCancellables)
        // Kokoro and Parakeet download on their own once setup is long done.
        // A few seconds in, so the launch itself stays quick.
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self else { return }
            self.startAutomaticModelDownloads(tourIsFinished: self.firstRunTour.isFinished)
        }
    }

    /// The models that download on their own once the welcome tour is done: Kokoro's voice and
    /// Parakeet's hearing.
    func startAutomaticModelDownloads(tourIsFinished: Bool) {
        KokoroVoice.shared.startAutomaticSetup(tourIsFinished: tourIsFinished)
        ParakeetDictation.shared.startAutomaticSetup(tourIsFinished: tourIsFinished, engine: dictationSettings.engine,
                                                     bareLanguageCode: dictationSettings.language.bareCode)
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
                print("🔑 Screen content capture result, width: \(image.width), height: \(image.height), didCapture: \(didCapture)")
                await MainActor.run {
                    isRequestingScreenContent = false
                    guard didCapture else { return }
                    hasScreenContentPermission = true
                    UserDefaults.standard.set(true, forKey: "hasScreenContentPermission")
                    ClickyAnalytics.trackPermissionGranted(permission: "screen_content")

                    // If onboarding was already completed, show the cursor overlay now
                    if hasCompletedOnboarding && allPermissionsGranted && !isOverlayVisible && isClickyCursorEnabled {
                        showOverlayOnAllScreens()
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
                guard let self else { return }
                self.refreshAllPermissions()
                self.checkScreenContentQuietlyOnce()
            }
        }
    }

    private var didCheckScreenContentQuietly = false
    private var isCheckingScreenContentQuietly = false

    /// Screen Recording is on (often after the relaunch it needs) but Screen Content has not been
    /// recorded as granted: try one capture before the island asks. With Screen Recording allowed
    /// it simply works, and the card that would ask "pick a display" never shows.
    private func checkScreenContentQuietlyOnce() {
        guard !didCheckScreenContentQuietly, hasScreenRecordingPermission, !hasScreenContentPermission, !isRequestingScreenContent else { return }
        didCheckScreenContentQuietly = true
        isCheckingScreenContentQuietly = true
        AppLog.append("permissions: screen recording is on; checking screen content with one capture")
        requestScreenContentPermission()
        // The capture answers within a moment; the card comes back only if it said no.
        Task { @MainActor [weak self] in
            while self?.isRequestingScreenContent == true { try? await Task.sleep(nanoseconds: 100_000_000) }
            guard let self else { return }
            self.isCheckingScreenContentQuietly = false
            self.permissionPromptController.update(with: self.permissionStatus)
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
        companionShortcutCancellable = globalPushToTalkShortcutMonitor
            .companionShortcutPublisher
            .receive(on: DispatchQueue.main)
            .sink { [weak self] shortcutEvent in
                self?.handleCompanionShortcut(shortcutEvent)
            }
    }

    // MARK: - Text, Dictate, Hands-free (HeyClicky's other shortcuts)

    private var companionShortcutCancellable: AnyCancellable?

    /// True while a dictation take is listening or transcribing: the words go where the cursor is,
    /// not to OpenClicky. Mirrors the take controller for the HUD.
    @Published private(set) var isDictatingToFrontApp = false

    /// Dictation: the orb, the engines, the formatting, history. Owned here so the shortcut tap,
    /// the Realtime microphone and the window all meet in one place.
    let dictationSettings = DictationSettings.shared
    let dictationSpaceStore = DictationSpaceStore()
    let dictationTakeStore: TakeStore? = {
        do { return try TakeStore() } catch {
            AppLog.append("take store unavailable: \(error.localizedDescription)")
            return nil
        }
    }()
    let orbModel = OrbModel()
    lazy var orbPanelManager = OrbPanelManager(model: orbModel, settings: dictationSettings)
    lazy var dictationTakeController: DictationTakeController = {
        let controller = DictationTakeController(settings: dictationSettings, spaceStore: dictationSpaceStore, takeStore: dictationTakeStore, orb: orbModel)
        controller.beforeMicrophoneOpens = { [weak self] in
            guard let self else { return }
            self.isDictatingToFrontApp = true
            // The Realtime engine's voice-processing input must be gone first, or the dictation
            // engine records silence (and an upload model then echoes its prompt). Only when a
            // session is open: releasing an idle engine would just delay the first words.
            if self.realtimeVoiceClient.isConnected {
                await self.realtimeVoiceClient.releaseMicrophoneForDictation()
            }
        }
        controller.afterMicrophoneCloses = { [weak self] in
            self?.isDictatingToFrontApp = false
            self?.realtimeVoiceClient.resumeListeningAfterDictation()
        }
        controller.onNoTextBoxAtPress = { [weak self] message in
            guard let self else { return }
            // The pointer may be resting out of sight ("after a shake"): it is the one speaking.
            AppLog.append("pointer: asked for a text box (overlay \(self.isOverlayVisible ? "shown" : "hidden"), presence \(self.pointerPresence.rawValue), awake \(self.isPointerAwake), voice \(self.voiceState))")
            self.wakePointer()
            self.showQuietNotice(message)
        }
        return controller
    }()

    private var dictationKeyCancellable: AnyCancellable?
    private lazy var clipboardHistoryMonitor = ClipboardHistoryMonitor(settings: dictationSettings, takeStore: dictationTakeStore) { [weak self] in
        self?.dictationTakeController.historyDidChange()
    }
    private var dictationWindowController: DictationWindowController?

    /// OpenClicky's one window: your words (record, history, dictionary, snippets, styles) and
    /// the settings pages. A settings page wins over a section.
    func showDictationWindow(section: DictationSection? = nil, settingsPage: DictationSettingsPage? = nil) {
        if dictationWindowController == nil { dictationWindowController = DictationWindowController(companionManager: self) }
        // The HUD would otherwise stay open over the window, since the pointer is still on it.
        notchHUDManager.dismissPanels()
        dictationWindowController?.show(section: section, settingsPage: settingsPage)
    }

    /// The window at a settings page (General when none is named): ⌘,, "Settings…", the island.
    /// Opened at General for the first time after the welcome tour, it plays the Settings tour once.
    func showSettings(_ page: DictationSettingsPage = .general) {
        showDictationWindow(settingsPage: page)
        if page == .general, firstRunTour.isFinished, !SettingsTourController.hasPlayed {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in self?.startSettingsTour() }
        }
    }

    // MARK: - Settings tour

    /// The one-minute tour of the Settings window (SettingsTour.swift).
    private(set) lazy var settingsTour = SettingsTourController(companionManager: self)

    /// Opens the window at General and plays the tour (General › Startup › Settings tour).
    func startSettingsTour() {
        showDictationWindow(settingsPage: .general)
        guard let windowController = dictationWindowController, let window = windowController.window else { return }
        interruptCurrentAnswer(includingAgentWork: false)
        settingsTour.start(in: window, model: windowController.model)
    }

    func stopSettingsTour() { settingsTour.stop() }

    /// While true, the pointer stays on what it points at until it is sent somewhere else, instead
    /// of flying back to the mouse after a few seconds (each tour step is longer than that).
    @Published private(set) var pointerStaysOnTarget = false
    /// Bumped for each simulated click; the overlay draws a ripple where the pointer is.
    @Published private(set) var pointerClickToken = 0

    func beginSettingsTourPointing() {
        pointerStaysOnTarget = true
        if !isOverlayVisible { showOverlayOnAllScreens() }
    }

    /// The tour is over: the pointer goes back to the mouse a moment later.
    func endSettingsTourPointing() {
        pointerStaysOnTarget = false
        guard let lastLocation = detectedElementScreenLocation else { return }
        // Pointing at the same spot again starts the normal hold, after which it flies back.
        pointForSettingsTour(at: lastLocation, bubble: detectedElementBubbleText ?? "")
    }

    func pointForSettingsTour(at screenLocation: CGPoint, bubble: String) {
        let displayFrame = NSScreen.screens.first { $0.frame.contains(screenLocation) }?.frame
            ?? NSScreen.main?.frame ?? .zero
        if !isOverlayVisible { showOverlayOnAllScreens() }
        detectedElementBubbleText = bubble.isEmpty ? nil : bubble
        detectedElementDisplayFrame = displayFrame
        detectedElementScreenLocation = screenLocation
        detectedElementPointToken &+= 1
    }

    func showPointerClick() {
        pointerClickToken &+= 1
    }

    // MARK: - First-run tour

    /// The island's welcome: meet the pointer, then dictate, ask, hand over a task.
    let firstRunTour = FirstRunTourController()

    /// True when the last question ended in a failure sentence instead of an answer, so the tour
    /// does not take "something went wrong", said aloud, for asking Clicky working.
    @Published private(set) var lastQuestionFailed = false

    /// When a quick action (open an app, make a folder…) last did what was asked; the tour's
    /// "hand me a task" step watches it.
    @Published private(set) var lastSuccessfulQuickActionAt: Date?

    /// During setup (the tour not done yet), the pointer shows as soon as Accessibility is in,
    /// flying out of the notch the first time, so it is there for the remaining cards.
    private func showPointerDuringSetupIfWanted(flyingFromTheNotch: Bool) {
        // Only a first run: someone who used OpenClicky before has finished onboarding already.
        guard !firstRunTour.isFinished, !hasCompletedOnboarding, isClickyCursorEnabled, !isOverlayVisible else { return }
        if flyingFromTheNotch { cursorLaunchOriginScreenLocation = notchHUDManager.dockPoint }
        showOverlayOnAllScreens()
    }

    /// The tour's first moment: the pointer flies out of the notch to the mouse and says hello
    /// (already out since Accessibility was granted, it just says hello).
    func introducePointerForTour() {
        if !isOverlayVisible {
            cursorLaunchOriginScreenLocation = notchHUDManager.dockPoint
            showOverlayOnAllScreens()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { [weak self] in
            guard let self else { return }
            self.streamCursorCaption("hi! i’m clicky. i follow your pointer, and point at things when you ask.", holdSeconds: 6)
            // "Hi! I'm Clicky. Let me show you three quick things.", recorded and bundled.
            self.recordedVoice.play(.tourHello)
        }
    }

    /// Flies the pointer to the tour card's main button ("let’s go") with a short bubble, for
    /// someone who has not moved since the hello.
    func pointAtTourPrimaryButton(saying bubbleText: String) {
        guard let (location, displayFrame) = notchHUDManager.tourPrimaryButtonLocation() else { return }
        if !isOverlayVisible {
            showOverlayOnAllScreens()
        }
        detectedElementBubbleText = bubbleText
        detectedElementScreenLocation = location
        detectedElementDisplayFrame = displayFrame
        detectedElementPointToken &+= 1
    }

    /// The tour's "watch this" before asking: a screenshot, one specific thing on screen pointed
    /// at, and a few words about it said out loud, so asking makes sense before it is tried.
    /// `onFinished` runs once the pointer and the voice are done (or the demo could not run).
    func performTourDemo(onFinished: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            defer { onFinished() }
            do {
                guard let parseResult = try await pointAtSomethingOnScreenForDemo(showingTheOverlayIfHidden: true) else { return }
                AppLog.append("tour demo: pointing at \(parseResult.elementLabel ?? "something")")
                // Said in the answering voice; quietly skipped when no voice route is set up.
                if let demoCommentAudio = try? await answerVoicePlayer.fetchAudio(for: parseResult.spokenText) {
                    try? await answerVoicePlayer.playToCompletion(demoCommentAudio)
                }
            } catch {
                AppLog.append("tour demo: could not run (\((error as NSError).domain) \((error as NSError).code))")
            }
        }
    }

    /// Starts the orb and keeps the event tap's dictation key in step with Settings.
    func startDictation() {
        dictationSpaceStore.startWatching()
        globalPushToTalkShortcutMonitor.dictationKey = dictationSettings.dictationKey
        dictationKeyCancellable = dictationSettings.$dictationKey.sink { [weak self] key in
            self?.globalPushToTalkShortcutMonitor.dictationKey = key
        }
        orbPanelManager.onOrbClicked = { [weak self] in self?.dictationTakeController.orbClicked() }
        orbPanelManager.start()
        // The microphone chosen in settings is the one both ways in hear with: fn dictation and the
        // ⌃⌥ question. The question used the Mac's default input, which can be a headset or an
        // unplugged jack, and heard "no" while dictation, on the built-in microphone, heard fine.
        dictationSettings.preferredMicrophoneUIDDidChange = { [weak self] uid in
            self?.dictationTakeController.dictationManager.preferredMicrophoneUID = uid
            self?.buddyDictationManager.preferredMicrophoneUID = uid
            self?.realtimeVoiceClient.preferredMicrophoneUID = uid
        }
        dictationTakeController.dictationManager.preferredMicrophoneUID = dictationSettings.preferredMicrophoneUID
        buddyDictationManager.preferredMicrophoneUID = dictationSettings.preferredMicrophoneUID
        realtimeVoiceClient.preferredMicrophoneUID = dictationSettings.preferredMicrophoneUID
        AppUpdater.shared.start()
        clipboardHistoryMonitor.start()
    }

    private func handleCompanionShortcut(_ shortcutEvent: CompanionShortcutEvent) {
        switch shortcutEvent {
        case .textComposerRequested:
            openTextComposer()
        case .handsFreeToggleRequested:
            toggleHandsFree()
        case .dictationPressed, .dictationEditPressed, .dictationEditModifierJoined, .dictationReleased, .dictationDoubleTapped:
            dictationTakeController.handle(shortcutEvent)
        case .escapePressed:
            // A dictation take in progress keeps esc to itself (it cancels the take, as always);
            // otherwise esc stops whatever the ask lane is doing.
            switch AskLaneEscape.target(dictationTakeInProgress: dictationTakeController.isTakeInProgress, askLaneCanBeStopped: askLaneCanBeStopped,
                                        realtimeIsBusy: usesRealtimeVoice && realtimeVoiceClient.isBusy) {
            case .dictationTake: dictationTakeController.handle(shortcutEvent)
            case .askLane:
                stopAskLane(from: "esc")
                TeacherPlugin.clearBoard(reason: "esc")
            case .realtime:
                // The fast voice lane: the reply stops, and an action still working says nothing
                // when it finishes (its output stays in the conversation for the next turn).
                AppLog.append("realtime: stopped (esc)")
                realtimeVoiceClient.stopSpeaking()
                voiceState = .idle
                showAskCaption("stopped", holdSeconds: 1.5)
                TeacherPlugin.clearBoard(reason: "esc")
            case .nothing:
                // Nothing else to stop: esc takes a lesson off the screen.
                TeacherPlugin.clearBoard(reason: "esc")
            }
        case .talkPressed, .talkReleased:
            // Delivered through shortcutTransitionPublisher.
            break
        }
    }

    /// Control tapped twice: the notch island opens a one-line text field.
    func openTextComposer() {
        notchHUDManager.openTextComposer()
        // Typing takes a few seconds: time enough to warm the on-device model for the local rung.
        if dictationSettings.answerSimpleRequestsOnThisMac { localAskRung?.model.prewarm() }
    }

    /// A typed request goes where a spoken one would: the Realtime session (it answers out loud,
    /// points, and can call the agent) or, without Realtime, the Claude teacher lane.
    func submitTypedRequest(_ text: String) {
        let trimmedText = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedText.isEmpty else { return }
        transientHideTask?.cancel()
        transientHideTask = nil
        if !isClickyCursorEnabled && !isOverlayVisible {
            showOverlayOnAllScreens()
        }
        interruptCurrentAnswer(includingAgentWork: true)
        clearDetectedElementLocation()
        dismissCursorCaption()
        TeacherPlugin.clearBoard(reason: "a new question")
        lastQuestionWasTyped = true
        lastTranscript = trimmedText
        ClickyAnalytics.trackUserMessageSent(characterCount: trimmedText.count)
        print("⌨️ Typed request: \(trimmedText)")

        guard usesRealtimeVoice else {
            sendTranscriptToClaudeWithScreenshot(transcript: trimmedText)
            return
        }
        voiceState = .processing
        Task { [weak self] in
            guard let self else { return }
            do {
                try await self.realtimeVoiceClient.connectIfNeeded(mode: self.isAlwaysListening ? .alwaysOn : .pushToTalk)
                await self.realtimeVoiceClient.sendTextTurn(trimmedText)
                TeacherPlugin.startLesson(for: trimmedText, capture: self.realtimeVoiceClient.lastScreenCapture)
            } catch {
                print("⚠️ Realtime unavailable for the typed request (\(error.localizedDescription)); using the teacher lane")
                self.sendTranscriptToClaudeWithScreenshot(transcript: trimmedText)
            }
        }
    }

    /// fn tapped twice with control held: always-on listening on or off (Realtime only).
    func toggleHandsFree() {
        guard usesRealtimeVoice else {
            // Say the real reason: a free account cannot use realtime at all, whatever the switch says.
            let reason = AccountCapabilities.current().usesRealtime
                ? "hands-free needs fast voice. turn it on in settings, clicky."
                : "hands-free needs your own openai or gemini key. add it in settings, account."
            streamCursorCaption(reason, holdSeconds: 6)
            return
        }
        setAlwaysListening(!isAlwaysListening)
        announceHandsFreeChange(how: nil)
    }

    /// "that's all, thanks" while hands-free is on: the Realtime model called `stop_listening` and
    /// its goodbye has played. Ends hands-free exactly as the shortcut does.
    private func endHandsFreeBySignOff() {
        guard isAlwaysListening else { return }
        setAlwaysListening(false)
        announceHandsFreeChange(how: "by voice")
    }

    /// The caption and log line for a hands-free change; `how` tells the log a voice sign-off from
    /// the shortcut.
    private func announceHandsFreeChange(how: String?) {
        let feedback = isAlwaysListening
            ? "hands-free is on. just talk, i'm listening. hold control and tap fn twice to stop."
            : "hands-free is off. hold control + option to talk."
        streamCursorCaption(feedback, holdSeconds: 6)
        let state = isAlwaysListening ? "on" : "off"
        let line = how.map { "hands-free \(state) (\($0))" } ?? "hands-free \(state)"
        print("👂 \(line)")
        AppLog.append(line)
    }

    private func handleShortcutTransition(_ transition: BuddyPushToTalkShortcut.ShortcutTransition) {
        if usesRealtimeVoice {
            handleRealtimeShortcutTransition(transition)
            return
        }
        switch transition {
        case .pressed:
            lastQuestionWasTyped = false
            // The fn take has its own manager: a take in progress (a tapped one keeps the microphone
            // after the key is up) is the dictation this guard means too.
            guard !buddyDictationManager.isDictationInProgress, !dictationTakeController.isTakeInProgress else {
                AppLog.append("ask: ⌃⌥ ignored, the microphone is still busy with dictation")
                showAskCaption("you’re still dictating. tap \(dictationSettings.dictationKey.keycapLabel) to finish, then ask again.", holdSeconds: 4)
                return
            }
            AppLog.append("ask: ⌃⌥ pressed, listening")
            // A Chromium browser in front gets told an assistive client is here now, so by the
            // release its text box is readable for "rephrase this" (ChromiumAccessibility).
            ChromiumAccessibility.wakeFrontApp()
            lastQuestionFailed = false
            // A new press owns the outcome from here: whatever the last release was waiting for is over.
            askOutcome = .listening
            askPressWasStopped = false
            askLiveWordsShownSincePress = false

            // Cancel any pending transient hide so the overlay stays visible
            transientHideTask?.cancel()
            transientHideTask = nil

            // If the cursor is hidden, bring it back transiently for this interaction
            if !isClickyCursorEnabled && !isOverlayVisible {
                showOverlayOnAllScreens()
            }

            // Cancel any in-progress response, agent run, and TTS from a previous utterance
            interruptCurrentAnswer(includingAgentWork: true)
            clearDetectedElementLocation()

            // Dismiss the onboarding prompt / caption if it's showing
            dismissCursorCaption()
            // Something by the pointer while the key is held: an engine that uploads on release
            // shows no words until then, and the waveform alone did not read as "listening".
            showAskCaption("listening…", holdSeconds: 60)
    

            ClickyAnalytics.trackPushToTalkStarted()

            pendingKeyboardShortcutStartTask?.cancel()
            pendingKeyboardShortcutStartTask = Task {
                await buddyDictationManager.startPushToTalkFromKeyboardShortcut(
                    currentDraftText: "",
                    updateDraftText: { [weak self] partialTranscript in
                        // The words appear by the pointer as they are heard, so the person can see
                        // it is listening (and what it heard) before letting go.
                        guard let self, self.askOutcome != .resolved else { return }
                        if !partialTranscript.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                            self.askLiveWordsShownSincePress = true
                        }
                        self.showAskLiveTranscript(partialTranscript)
                    },
                    submitDraftText: { [weak self] finalTranscript in
                        // Esc while it was still listening or transcribing: the words go nowhere.
                        guard let self, !self.askPressWasStopped else { return }
                        self.lastTranscript = finalTranscript
                        self.askOutcome = .resolved
                        let question = finalTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
                        AppLog.append("ask: heard \(question.count) chars")
                        guard !question.isEmpty else {
                            self.showAskCaption("didn't catch that. hold ⌃⌥ and ask again.", holdSeconds: 3)
                            return
                        }
                        ClickyAnalytics.trackUserMessageSent(characterCount: finalTranscript.count)
                        // Show what was heard and that an answer is coming, until the voice starts.
                        self.showAskCaption("“\(question)” · thinking…", holdSeconds: 30)
                        self.sendTranscriptToClaudeWithScreenshot(transcript: finalTranscript)
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
            AppLog.append("ask: ⌃⌥ released")
            guard askOutcome == .listening else { return }
            if buddyDictationManager.isFinalizingTranscript {
                // The session decides what was heard (an uploading engine only starts now); the
                // outcome is shown when it ends (`askDictationSessionEnded`), not guessed on a timer.
                askOutcome = .transcribing
                if !askLiveWordsShownSincePress {
                    showAskCaption("transcribing…", holdSeconds: 30)
                }
            } else {
                // Nothing was recording (a tap, or a start that never got going). An error from the
                // start lands first and is shown instead (`showAskDictationError`).
                askOutcome = .transcribing
                DispatchQueue.main.async { [weak self] in self?.askDictationSessionEnded() }
            }
        case .none:
            break
        }
    }

    // MARK: - Companion Prompt

    static let companionVoiceResponseSystemPrompt = """
    you're openclicky, a friendly always-on companion that lives in the user's menu bar. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation, you remember everything they've said before.

    rules:
    - keep it short: one or two short sentences, about thirty words at most. people listen to this, and a long answer is hard to follow by ear.
    - for a task with several steps on screen, guide them one step at a time (see guided steps below).
    - if they ask you to explain more or go deeper, say more, but still keep it under about eighty words.
    - all lowercase, casual, warm. no emojis.
    - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, em dashes, or formatting. just natural speech.
    - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
    - if the user's question relates to what's on their screen, reference specific things you see.
    - if the screenshot doesn't seem relevant to their question, just answer the question directly.
    - you can help with anything: coding, writing, general knowledge, brainstorming.
    - never say "simply" or "just".
    - don't read out code verbatim. describe what the code does or what needs to change conversationally.
    - don't end with questions like "want me to explain more?" or "should i show you?", and don't add extra tips they didn't ask for. stop when the answer is given.
    - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is, prioritize that one but reference others if relevant.

    element pointing:
    you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user, if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

    don't point at things when it would be pointless, like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

    when you point, START your response with a coordinate tag, BEFORE your spoken text, so the cursor flies there as your voice begins. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

    format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important, without the screen number, the cursor will point at the wrong place.

    if pointing wouldn't help, start with [POINT:none].

    examples:
    - user asks how to color grade in final cut: "[POINT:1100,42:color inspector] you'll want to open the color inspector, it's right up in the top right area of the toolbar. click that and you'll get all the color wheels and curves."
    - user asks what html is: "[POINT:none] html stands for hypertext markup language, it's basically the skeleton of every web page."
    - user asks how to commit in xcode: "[POINT:285,11:source control] see that source control menu up top? click that and hit commit, or you can use command option c as a shortcut."
    - element is on screen 2 (not where cursor is): "[POINT:400,300:terminal:screen2] that's over on your other monitor, see the terminal window?"

    guided steps:
    when they ask how to do something that takes more than one step on screen, give only the first step, point at it, and end your reply with \(GuidedStepMarker.marker). a step is one click, or one thing to type, nothing more (a right-click that opens a menu is one step, the menu item is the next). openclicky then waits for them to do it (a click, or return or tab after typing) and asks you on its own with "\(GuidedStepRules.nextStepRequestOpening) …" and a fresh screenshot. look at the new screen and give the next step the same way: one step, pointed at, ending with \(GuidedStepMarker.marker) while more steps remain. leave \(GuidedStepMarker.marker) off the last step, and off any answer that isn't a step. if the new screen shows they clicked something else or went somewhere unexpected, gently help them back, still ending with \(GuidedStepMarker.marker). when openclicky's question starts with "\(GuidedStepRules.offTargetRequestOpening) …", their click landed away from where you pointed; your pointing is only approximate, so it may still have been right. check the new screen: if it was the right thing, give the next step as usual; if not, gently point them back, still ending with \(GuidedStepMarker.marker). if the screen hasn't changed the way that step should change it, it didn't work yet: point at the same thing again and say to try once more. if you can't tell whether the step worked, say what they should see and what to do then, and end with \(GuidedStepMarker.marker) so you keep waiting with them. never end a step with a question. if a permission, password or security prompt is on screen and it isn't what they asked about, it is not a step: never point at it or tell them which button to pick. reply as usual, the tag first pointing at the first step of their task, then a few words that the prompt is theirs to decide, then the step, ending with \(GuidedStepMarker.marker). for a step that is typing, say what to type and to press tab or click the next thing when they're done.
    example: user asks how to attach a file in mail: "[POINT:612,88:attach button] click the paperclip at the top of your message. \(GuidedStepMarker.marker)"

    not sure:
    if you can't find the control they need on the screen, or can't tell which one it is, don't guess with the pointer. start your reply with \(UnsureMarker.marker), send no point tag at all, and say in words what they should look for (its name, what it looks like, roughly where it usually is), or ask them to tell you what they see. leave \(GuidedStepMarker.marker) off that reply.
    example: "\(UnsureMarker.marker) i can't spot the display settings on this screen. look for a gear icon called system settings in your dock, or tell me what you see."

    keep your first sentence short: it is spoken while the rest of your answer is still being written.
    """

    // MARK: - AI Response Pipeline

    /// Captures a screenshot, sends it along with the transcript to Claude,
    /// and plays the response aloud via ElevenLabs TTS. The cursor stays in
    /// the spinner/processing state until TTS audio begins playing.
    /// Claude's response may include a [POINT:x,y:label] tag which triggers
    /// the buddy to fly to that element on screen.
    private func sendTranscriptToClaudeWithScreenshot(transcript: String, isGuidedStepContinuation: Bool = false) {
        interruptCurrentAnswer(includingAgentWork: false)
        // Anything the person asks starts afresh; only OpenClicky's own "what's next?" continues a guide.
        if !isGuidedStepContinuation {
            guidedStepsGivenSoFar = 0
            guideStepUnderWay = nil
            guidedStepGoal = transcript
            TeacherPlugin.clearBoard(reason: "a new question")
        }
        guidedStepPointedLocation = nil

        isAskInFlight = true
        // A guide's own "what's next?" never pauses: the person already let the task go ahead, and
        // a pause after every click would only slow the steps down.
        let pauseSeconds = AskSendPause.seconds(isEnabled: dictationSettings.pauseBeforeSendingScreen, isGuidedStepContinuation: isGuidedStepContinuation)
        currentResponseTask = Task {
            // Stay in processing (spinner) state — no streaming text displayed
            voiceState = .processing

            do {
                // Where the mouse was when they let go: what "this" points at.
                let mouseLocationAtRelease = NSEvent.mouseLocation

                // The local ask rung: Apple's on-device model gets one short chance to recognise a
                // quick action or a rewrite before anything is captured or sent (design:
                // docs/superpowers/specs/2026-10-27-on-device-ask-rung-design.md). It runs inside
                // the pause when there is one, so a cloud question is never slower than today; a
                // local decision does the thing, and the pause (and the screenshot) never happen.
                var localRungOutcome: String?
                var earlyWritingTarget: WritingTarget?
                // Captured once: a second look costs up to 2.5 s with a Chromium app in front.
                var writingTargetWasCaptured = false
                var pauseAlreadyWaitedOut = false
                if !isGuidedStepContinuation, dictationSettings.answerSimpleRequestsOnThisMac, let localAskRung {
                    switch LocalAskRung.gate(transcript: transcript, replyLanguageCode: ReplyLanguage.currentCode) {
                    case .skip(let reason):
                        AppLog.append("local rung: gate skipped (\(reason))")
                        localRungOutcome = "gate_skipped"
                    case .tryModel(let mentionsRewriting):
                        // A rewrite needs the text now; the cloud path reuses what was captured.
                        if mentionsRewriting {
                            earlyWritingTarget = await WritingTarget.captureFromFrontApp()
                            writingTargetWasCaptured = true
                        }
                        guard !Task.isCancelled else { return }
                        // Inside the pause the model may take a little longer than the pause; with
                        // the pause off it gets the pause's length and no more.
                        let budgetSeconds = pauseSeconds == nil ? LocalAskRung.budgetWithoutPauseSeconds : max(pauseSeconds ?? 0, LocalAskRung.defaultBudgetSeconds)
                        let decision: LocalAskDecision
                        if let pauseSeconds {
                            // The same pause and caption as below, with the model deciding meanwhile.
                            AppLog.append("ask: pausing \(pauseSeconds) s before sending the screen")
                            askIslandStatus = AskSendPause.caption
                            showAskCaptionAtOnce(AskSendPause.caption, holdSeconds: pauseSeconds + 1)
                            async let pauseRanOut = AskSendPause.waitOut(seconds: pauseSeconds)
                            decision = await localAskRung.decide(transcript: transcript, writingTarget: earlyWritingTarget, budgetSeconds: budgetSeconds)
                            if case .cloud = decision {
                                guard await pauseRanOut else { return }
                                askIslandStatus = nil
                                showAskCaption("“\(transcript)” · thinking…", holdSeconds: 30)
                                pauseAlreadyWaitedOut = true
                            }
                        } else {
                            decision = await localAskRung.decide(transcript: transcript, writingTarget: earlyWritingTarget, budgetSeconds: budgetSeconds)
                        }
                        if case .cloud(let reason) = decision {
                            localRungOutcome = "cloud_" + reason.lowercased().replacingOccurrences(of: ":", with: "").split(separator: " ").joined(separator: "_")
                        } else {
                            guard !Task.isCancelled else { return }
                            await performLocalDecision(decision, transcript: transcript, writingTarget: earlyWritingTarget)
                            await finishAskTurn()
                            return
                        }
                    }
                }

                if let pauseSeconds, !pauseAlreadyWaitedOut {
                    // A moment to call it off before anything leaves the Mac. Esc or a click on the
                    // island cancels this task, and nothing is captured or sent.
                    AppLog.append("ask: pausing \(pauseSeconds) s before sending the screen")
                    askIslandStatus = AskSendPause.caption
                    showAskCaptionAtOnce(AskSendPause.caption, holdSeconds: pauseSeconds + 1)
                    let pauseRanOut = await AskSendPause.waitOut(seconds: pauseSeconds)
                    guard pauseRanOut else { return }
                    askIslandStatus = nil
                    showAskCaption("“\(transcript)” · thinking…", holdSeconds: 30)
                }
                // Captured after the pause, so what is sent is what is on screen now.
                // Capture all connected screens so the AI has full context (an account sends at most two)
                let allScreenCaptures: [CompanionScreenCapture]
                do {
                    allScreenCaptures = try await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()
                } catch {
                    // Told apart from a backend failure, so the person is sent to the right place.
                    throw AskScreenCaptureError(underlyingError: error)
                }
                let screenCaptures = Self.screensForAsk(allScreenCaptures, capabilities: AccountCapabilities.current())

                guard !Task.isCancelled else { return }

                // OpenClicky two-tier routing: the gate decides whether this is a quick question
                // (teacher lane below: Claude + pointing) or real work (agent lane: Codex thread).
                // The agent lane needs the openclicky tool (not in the downloaded app); without it the
                // quick actions below are offered to whoever answers instead.
                let agentLaneRuns = isAgentModeEnabled && OpenClickyConfiguration.isConfigured
                    && AccountCapabilities.current().usesAgent && OpenClickyAgentClient.isToolInstalled
                if agentLaneRuns {
                    agentActivityText = "deciding…"
                    let lane = await openClickyAgentClient.classifyLane(for: transcript)
                    guard !Task.isCancelled else { return }
                    print("🧭 OpenClicky gate: \(lane)")
                    if lane == "agent" {
                        try await runOpenClickyAgentLane(transcript: transcript, screenCaptures: screenCaptures)
                        if !Task.isCancelled {
                            isAskInFlight = false
                            voiceState = .idle
                            scheduleTransientHideIfNeeded()
                        }
                        return
                    }
                    agentActivityText = nil
                }

                var labeledImages = screenCaptures.map(Self.imageLabeledWithItsPixelSize)
                // The pointer's place in its screenshot goes into the question, and a close-up of
                // what is around it rides along, so "what is this?" is about the thing pointed at
                // and not the window with focus.
                let pointer = PointerInCapture.locate(mouse: mouseLocationAtRelease, in: screenCaptures)
                if let pointer, let pointedCapture = screenCaptures.first(where: { $0.label == pointer.screenLabel }),
                   let closeUp = PointerCloseUp.jpeg(from: pointedCapture.imageData, around: pointer) {
                    labeledImages.append((data: closeUp, label: PointerCloseUp.label()))
                }
                let secondsSinceLastQuestion = lastAskQuestion.map { Date().timeIntervalSince($0.askedAt) } ?? .infinity
                let isRepeatedQuestion = RepeatedQuestion.isRepeat(transcript, of: lastAskQuestion?.transcript, secondsSincePrevious: secondsSinceLastQuestion)
                lastAskQuestion = (transcript: transcript, askedAt: Date())
                if isRepeatedQuestion { AppLog.append("ask: the same question again; answering about what is under the pointer") }
                if !isGuidedStepContinuation {
                    TeacherPlugin.startLesson(for: transcript, capture: screenCaptures.first(where: { $0.isCursorScreen }) ?? screenCaptures.first)
                }
                // The text a "rephrase this" would replace: the highlight, or the box the cursor is in
                // (already captured when the local rung looked at a rewrite).
                let writingTarget: WritingTarget?
                if writingTargetWasCaptured {
                    writingTarget = earlyWritingTarget
                } else {
                    writingTarget = await WritingTarget.captureFromFrontApp()
                }
                guard !Task.isCancelled else { return }

                // Pass conversation history so Claude remembers prior exchanges
                let historyForAPI = conversationHistory.map { entry in
                    (userPlaceholder: entry.userTranscript, assistantResponse: entry.assistantResponse)
                }

                // Offered wherever the agent lane doesn't run (an account, or no openclicky tool on
                // this Mac), and to a Claude Code or Codex plan through PlanToolProtocol's [ACTION: …] lines.
                let tools = Self.offersQuickActionsToAskLane(
                    agentLaneRuns: agentLaneRuns,
                    answeringSource: ProviderRouter.route(for: .claude, credentials: ProviderRouter.currentCredentials()).answeringSource
                ) ? MacAction.anthropicToolDefinitions() : []
                let toolsLine = tools.isEmpty ? "" : "\n\n" + Self.accountToolsInstruction
                // Offered whatever the lane setup, but only when there is text to write over;
                // without one the model is told not to read a rewrite out.
                var askTools = writingTarget == nil ? tools : tools + [WriteInPlace.anthropicToolDefinition()]
                if TeacherPlugin.isEnabled { askTools += TeacherPlugin.anthropicToolDefinitions() }
                let writeInPlaceLine = "\n\n" + (writingTarget == nil ? WriteInPlace.noTargetPromptInstruction : WriteInPlace.promptInstruction)
                // The answer is spoken while it is still being written: each finished sentence goes to
                // the voice, and the pointer moves when the voice starts (the prompt puts the tag first).
                let answerSpeaker = makeAnswerSpeaker(
                    onVoiceStarts: { [weak self] in
                        self?.voiceState = .responding
                        self?.dismissCursorCaption()
                        AppLog.append("ask: voice started")
                    },
                    onPointTag: { [weak self] pointTag in self?.pointAtAnswerTag(pointTag, screenCaptures: screenCaptures) }
                )
                let answerPlayback = Task { @MainActor in try await answerSpeaker.play() }
                activeAnswerSpeaker = answerSpeaker
                activeAnswerPlayback = answerPlayback
                let askStartedAt = Date()
                answeringSourceName = ProviderRouter.route(for: .claude, credentials: ProviderRouter.currentCredentials()).answeringSource?.shortName
                AppLog.append("ask: sending to claude (\(screenCaptures.count) screen(s))")

                let fullResponseText: String
                let toolCalls: [ClaudeToolCall]
                do {
                    (fullResponseText, toolCalls, _) = try await claudeAPI.analyzeImageStreaming(
                        images: labeledImages,
                        systemPrompt: Self.composeTalkInstructions(base: Self.withReplyLanguage(Self.companionVoiceResponseSystemPrompt + Self.pluginPromptLines()) + toolsLine + writeInPlaceLine, skillsBlock: talkSkillsBlock()),
                        conversationHistory: historyForAPI,
                        userPrompt: Self.askUserPrompt(transcript: transcript, writingTarget: writingTarget, pointer: pointer, isRepeatedQuestion: isRepeatedQuestion),
                        tools: askTools,
                        onTextChunk: { accumulatedAnswer in
                            answerSpeaker.receive(accumulatedAnswer: accumulatedAnswer)
                        }
                    )
                } catch {
                    answerSpeaker.cancel()
                    answerPlayback.cancel()
                    throw error
                }
                AppLog.append("ask: answer \(fullResponseText.count) chars in \(String(format: "%.1f", Date().timeIntervalSince(askStartedAt))) s")
                // Stopped (esc, the island, a new question) while the answer was coming in: nothing
                // it asked for may run. Each call is checked too, since a rewrite or an action awaits.
                guard !Task.isCancelled else {
                    answerSpeaker.cancel()
                    answerPlayback.cancel()
                    return
                }
                var toolOutcomes: [String] = []
                for call in toolCalls.prefix(2) {
                    guard !Task.isCancelled else { break }
                    // The drawing is the answer's picture, not something to say: nothing is added
                    // to the spoken reply.
                    if TeacherPlugin.toolNames.contains(call.name) {
                        let cursorCapture = screenCaptures.first(where: { $0.isCursorScreen }) ?? screenCaptures.first
                        _ = await TeacherPlugin.perform(toolName: call.name, arguments: call.arguments, capture: cursorCapture)
                        continue
                    }
                    if call.name == WriteInPlace.toolName {
                        guard let writingTarget else { continue }
                        let rewrite = await WriteInPlace.replace(writingTarget, with: call.arguments["text"] as? String ?? "")
                        if rewrite.didReplace { lastSuccessfulQuickActionAt = Date() }
                        toolOutcomes.append(rewrite.spokenSentence)
                        continue
                    }
                    switch MacAction.parse(toolName: call.name, arguments: call.arguments) {
                    case .action(let action):
                        let outcome = await macActionRunner.perform(action)
                        if outcome.didSomething { lastSuccessfulQuickActionAt = Date() }
                        toolOutcomes.append(outcome.spokenSentence)
                    case .badArguments(let outcome): toolOutcomes.append(outcome.spokenSentence)
                    case .notAFastAction: toolOutcomes.append(AccountLimitError.notOnPlan.message)
                    }
                }

                guard !Task.isCancelled else {
                    answerSpeaker.cancel()
                    answerPlayback.cancel()
                    return
                }
                answerSpeaker.finish(closingSentences: toolOutcomes)

                // The tag and the markers are stripped so they don't confuse future context.
                // A reply that is not sure what to point at never points, and is never a guide's
                // step: the guide ends there and the person asks again with what they see. Keeping
                // the guide waiting for a click would only guess at what the next click means.
                let replyIsUnsure = UnsureMarker.containsMarker(fullResponseText)
                let replyIsAGuidedStep = !replyIsUnsure && GuidedStepMarker.containsMarker(fullResponseText)
                let parseResult = Self.parsePointingCoordinates(
                    from: UnsureMarker.removingMarkers(from: GuidedStepMarker.removingMarkers(from: fullResponseText)))
                let pointedCoordinate = replyIsUnsure ? nil : parseResult.coordinate
                let spokenText = Self.spokenReply(text: parseResult.spokenText, toolOutcomes: toolOutcomes)
                if replyIsUnsure {
                    AppLog.append("ask: not sure what to point at\(parseResult.coordinate == nil ? "" : " (its point tag ignored)")")
                    // The marker came after a tag the pointer already flew to: bring it back.
                    clearDetectedElementLocation()
                    showAskIslandStatus(UnsureMarker.islandStatus, holdSeconds: 10)
                } else if parseResult.coordinate == nil {
                    print("🎯 Element pointing: \(parseResult.elementLabel ?? "no element")")
                }

                if spokenText.isEmpty {
                    // Nothing to say (an empty answer, or a bare tag): the queue never starts the
                    // voice, so "thinking…" would stay up in silence. An empty turn is not history.
                    AppLog.append("ask: empty answer (\(pointedCoordinate == nil ? "no point" : "point only"))")
                    guideStepUnderWay = nil
                    dismissCursorCaption()
                    if pointedCoordinate == nil {
                        lastQuestionFailed = true
                        speakWithSystemVoice(Self.emptyAnswerSentence)
                    }
                } else {
                    rememberExchange(userTranscript: transcript, assistantResponse: spokenText)
                    TeacherPlugin.writeAnswerIfTheyCannotHear(spokenText, questionWasTyped: lastQuestionWasTyped,
                                                              capture: screenCaptures.first(where: { $0.isCursorScreen }) ?? screenCaptures.first)
                    if replyIsAGuidedStep {
                        waitForTheGuidedStepToBeDone()
                    } else {
                        // The last step of a guide, or an answer that was never one.
                        guideStepUnderWay = nil
                    }
                }

                print("🧠 Conversation history: \(conversationHistory.count) exchanges")
                // An answer spent some of the allowance: the profile store notices crossing 80%.
                if AccountCapabilities.current().kind == .account { AccountProfileStore.shared.refresh() }

                ClickyAnalytics.trackAIResponseReceived(characterCount: spokenText.count, localRung: localRungOutcome)

                // Wait for the voice to finish. A question asked meanwhile cancels this task, and the
                // cancellation is passed on to the playback.
                try await waitForAnswerVoice(answerPlayback: answerPlayback, answerSpeaker: answerSpeaker)
            } catch where Self.isCancellation(error) || Task.isCancelled {
                // User spoke again — response was interrupted. Cancelling the task that reads the
                // answer stream ends it with URLError.cancelled, not a CancellationError.
            } catch {
                AppLog.append("ask: failed: \((error as NSError).domain) \((error as NSError).code)")
                lastQuestionFailed = true
                dismissCursorCaption()
                if let providerError = error as? AnswerProviderError {
                    // A key or plan the person set up refused: say which and why, in text.
                    AppLog.append("ask: \(providerError.source.rawValue) refused (\(providerError.status))")
                    showAskCaption("\(providerError.source.displayName) couldn’t answer: \(providerError.message)", holdSeconds: 8)
                } else if let unavailable = error as? ProviderRouteUnavailable {
                    // Not an outage: nothing is set up to answer yet. Said in text beside the
                    // pointer, not in the system voice, which reads a setup note like an alarm.
                    showAskCaption(unavailable.sentence, holdSeconds: 8)
                } else if let signInError = error as? BackendSignInError {
                    // The backend refused the token and BackendSignIn has already renewed it once and
                    // asked again: the sign-in is over, or could not be renewed right now.
                    speakWithSystemVoice(OpenClickyAuthSession.spokenSentence(after: signInError))
                } else if let refusal = Self.refusedQuestion(error), Self.isSignInRejection(refusal) {
                    // A 401 BackendSignIn passed through (an own key's refusal relayed by the backend,
                    // or a request sent with no token), or Anthropic refusing an own key directly.
                    speakWithSystemVoice(await OpenClickyAuthSession.shared.sentenceAfterBackendRejection(responseBody: refusal.body))
                } else if Self.isAccountNotice(error) {
                    if let notice = Self.accountNoticeToSpeak(for: error, alreadySpoken: &spokenAccountNotices) {
                        speakWithSystemVoice(notice)
                    }
                } else {
                    ClickyAnalytics.trackResponseError(error: error.localizedDescription)
                    print("⚠️ Companion response error: \(error)")
                    speakWithSystemVoice(Self.askFailureSentence(
                        for: error, screenRecordingAllowed: WindowPositionManager.hasScreenRecordingPermission()))
                }
            }

            await finishAskTurn()
        }
    }

    /// The end of an ask turn, local or cloud: a notice in the system voice is still being said, so
    /// the turn is over when it ends; a cancelled turn (esc, a new question) was reset by its stopper.
    private func finishAskTurn() async {
        await waitForSystemVoiceNoticeToEnd()
        if !Task.isCancelled {
            isAskInFlight = false
            voiceState = .idle
            scheduleTransientHideIfNeeded()
        }
    }

    /// Waits for an answer's voice to finish. A voice that fails (the account's allowance spent, a
    /// vendor down) leaves what was not said to the system voice; a cancellation is passed on to the
    /// playback and rethrown for the caller's own handling.
    private func waitForAnswerVoice(answerPlayback: Task<Void, Error>, answerSpeaker: StreamingAnswerSpeaker) async throws {
        do {
            try await withTaskCancellationHandler {
                try await answerPlayback.value
            } onCancel: {
                answerPlayback.cancel()
            }
        } catch let failure as SpeechQueue.Failure {
            // 402 is the account's voice allowance (tts_budget): the system voice is the plan, not a failure.
            let nsError = failure.underlyingError as NSError
            let voiceRefusal = failure.underlyingError as? BackendHTTPError
            let isVoiceAllowanceSpent = voiceRefusal?.service == .voice && voiceRefusal?.status == 402
            if !isVoiceAllowanceSpent {
                ClickyAnalytics.trackTTSError(error: nsError.localizedDescription)
            }
            print("⚠️ TTS via backend failed (\(nsError.localizedDescription)); using the system voice")
            AppLog.append("ask: voice failed (\(nsError.code)); using the system voice")
            if !Task.isCancelled {
                // The success path dismisses "thinking…" when the voice starts; the system
                // voice starts here, so the caption goes now.
                dismissCursorCaption()
                speakWithSystemVoice(failure.unspokenText.joined(separator: " "))
                answerSpeaker.voiceFellBack()
            }
        }
    }

    /// A request the local ask rung handled on this Mac: do it, say what happened in the usual
    /// voice, remember the exchange so a cloud follow-up knows, and count it. Nothing was captured
    /// and nothing was sent; the island says "speaking · this Mac" for the turn.
    private func performLocalDecision(_ decision: LocalAskDecision, transcript: String, writingTarget: WritingTarget?) async {
        askIslandStatus = nil
        answeringSourceName = Self.localAnswerSourceName
        showAskCaption("“\(transcript)” · on this mac", holdSeconds: 10)
        let sentence: String
        let outcomeForAnalytics: String
        switch decision {
        case .quickAction(let toolName, let arguments):
            outcomeForAnalytics = "local_action"
            if case .action(let action) = MacAction.parse(toolName: toolName, arguments: arguments) {
                let outcome = await macActionRunner.perform(action)
                if outcome.didSomething { lastSuccessfulQuickActionAt = Date() }
                sentence = outcome.spokenSentence
            } else {
                // The mapper already ran the parser; this is belt and braces.
                AppLog.append("local rung: \(toolName) did not parse after all")
                sentence = Self.emptyAnswerSentence
            }
        case .rewrite(let newText):
            outcomeForAnalytics = "local_rewrite"
            if let writingTarget {
                let rewrite = await WriteInPlace.replace(writingTarget, with: newText)
                if rewrite.didReplace { lastSuccessfulQuickActionAt = Date() }
                sentence = rewrite.spokenSentence
            } else {
                sentence = Self.emptyAnswerSentence
            }
        case .textAlreadyReadsWell:
            outcomeForAnalytics = "local_rewrite_unchanged"
            sentence = Self.textAlreadyReadsWellSentence
        case .cloud:
            return
        }
        guard !Task.isCancelled else { return }
        // The outcome sentence is product copy (never the person's words), so it may be logged.
        AppLog.append("local rung: said \"\(sentence)\"")

        let answerSpeaker = makeAnswerSpeaker(
            onVoiceStarts: { [weak self] in
                self?.voiceState = .responding
                self?.dismissCursorCaption()
                AppLog.append("ask: voice started (this mac)")
            },
            onPointTag: { _ in }
        )
        let answerPlayback = Task { @MainActor in try await answerSpeaker.play() }
        activeAnswerSpeaker = answerSpeaker
        activeAnswerPlayback = answerPlayback
        answerSpeaker.receiveWholeReply(sentence)
        rememberExchange(userTranscript: transcript, assistantResponse: sentence)
        ClickyAnalytics.trackAIResponseReceived(characterCount: sentence.count, localRung: outcomeForAnalytics)
        do {
            try await waitForAnswerVoice(answerPlayback: answerPlayback, answerSpeaker: answerSpeaker)
        } catch where Self.isCancellation(error) || Task.isCancelled {
            // A new question or esc: the stopper reset the state.
        } catch {
            AppLog.append("local rung: voice failed: \((error as NSError).domain) \((error as NSError).code)")
        }
    }

    // MARK: - Guided steps

    /// Steps given in the guide under way (0 when none is), so a runaway guide ends (`GuidedStepRules.maximumSteps`).
    private var guidedStepsGivenSoFar = 0
    /// Watches for the person doing the step just given; nil when no step is waiting.
    private var guidedStepWatcher: GuidedStepWatcher?
    /// What the person asked that started the guide, repeated in every "what's next?".
    private var guidedStepGoal = ""
    /// Where the pointer last pointed for the answer under way (AppKit global screen coordinates),
    /// so the click that finishes a step can be checked against it. Cleared with each new request.
    private var guidedStepPointedLocation: CGPoint?
    /// The step of the guide under way (1, 2, …) from the first step until the guide ends; nil when
    /// there is no guide. The island turns into the guide's stop strip and the pointer carries
    /// "esc to stop" while it is set.
    @Published private(set) var guideStepUnderWay: Int?

    /// The reply was one step of a guide: once the person does it, ask for the next step on a fresh screenshot.
    private func waitForTheGuidedStepToBeDone() {
        guidedStepsGivenSoFar += 1
        guard guidedStepsGivenSoFar < GuidedStepRules.maximumSteps else {
            AppLog.append("guide: stopped after \(guidedStepsGivenSoFar) steps")
            guideStepUnderWay = nil
            return
        }
        stopWatchingForGuidedStep()
        AppLog.append("guide: step \(guidedStepsGivenSoFar) given, waiting for a click or return")
        let watcher = GuidedStepWatcher(
            onStepDone: { [weak self] clickLocation in
                guard let self else { return }
                let clickCheck = GuidedStepClickCheck.verdict(pointedAt: self.guidedStepPointedLocation, clickedAt: clickLocation)
                AppLog.append("guide: step \(self.guidedStepsGivenSoFar) done (\(clickCheck)), asking for the next")
                self.guidedStepWatcher = nil
                if case .offTarget = clickCheck {
                    self.showAskCaption(GuidedStepClickCheck.offTargetCaption, holdSeconds: 4)
                }
                self.sendTranscriptToClaudeWithScreenshot(
                    transcript: GuidedStepRules.nextStepRequest(forGoal: self.guidedStepGoal, clickCheck: clickCheck),
                    isGuidedStepContinuation: true)
            },
            onEnded: { [weak self] reason in
                guard let self else { return }
                AppLog.append("guide: ended (\(reason.rawValue)) after \(self.guidedStepsGivenSoFar) steps")
                self.guidedStepWatcher = nil
                // Esc means stop: the step still being said goes quiet too, as esc does anywhere else.
                if reason == .escape {
                    self.stopAskLane(from: "esc during a guide")
                    return
                }
                self.guidedStepsGivenSoFar = 0
                self.guideStepUnderWay = nil
            }
        )
        guidedStepWatcher = watcher
        guideStepUnderWay = guidedStepsGivenSoFar
        watcher.start()
    }

    /// The island's "stop guide": ends the guide wherever it is (waiting for a click, looking at the
    /// screen for the next step, or saying it) and silences it. It works from the first step on: a
    /// guide is only known to be one once its first step comes back, and before that the question
    /// on its way is what there is to stop.
    func stopGuide() {
        guard guideStepUnderWay != nil || isAskInFlight else { return }
        AppLog.append("guide: stopped from the island after \(guidedStepsGivenSoFar) steps")
        stopAskLane(from: "the island")
        realtimeVoiceClient.stopSpeaking()
    }

    // MARK: - Stopping the ask lane

    /// From the moment the ask lane takes a question (the pause before sending included) until its
    /// answer has been said, failed, or was stopped. Esc and a click on the island stop it.
    @Published private(set) var isAskInFlight = false
    /// What the island's status says instead of the ask lane's state, while set: the pause before
    /// sending the screen, or a reply that was not sure what to point at.
    @Published private(set) var askIslandStatus: String?
    private var askIslandStatusExpiry: Task<Void, Never>?

    /// Puts `status` on the island for a while (a new question or a stop takes it away sooner).
    private func showAskIslandStatus(_ status: String, holdSeconds: TimeInterval) {
        askIslandStatus = status
        askIslandStatusExpiry?.cancel()
        askIslandStatusExpiry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(holdSeconds))
            guard !Task.isCancelled, let self, self.askIslandStatus == status else { return }
            self.askIslandStatus = nil
        }
    }
    /// The ⌃⌥ press was stopped with esc while it was still listening or transcribing: words the
    /// session hands over after all are dropped.
    private var askPressWasStopped = false

    /// Whether esc has anything in the ask lane to stop: a press still listening or transcribing, a
    /// question on its way or being answered, or a guide.
    var askLaneCanBeStopped: Bool {
        AskLaneEscape.canStop(pressIsSettled: askOutcome == .resolved, isAskInFlight: isAskInFlight, guideStep: guideStepUnderWay)
    }

    /// Esc, or a click on the island while a question is on its way: what a new question does,
    /// without the question. The request in flight is cancelled (so nothing it asked for runs), the
    /// voice and the pointer stop, a guide ends, and the island goes back to rest. Returns false
    /// when there was nothing to stop.
    @discardableResult
    func stopAskLane(from source: String) -> Bool {
        guard askLaneCanBeStopped else { return false }
        AppLog.append("ask: stopped (\(source))")
        if askOutcome != .resolved {
            askOutcome = .resolved
            askPressWasStopped = true
            pendingKeyboardShortcutStartTask?.cancel()
            pendingKeyboardShortcutStartTask = nil
            buddyDictationManager.cancelCurrentDictation(preserveDraftText: false)
        }
        guidedStepsGivenSoFar = 0
        guideStepUnderWay = nil
        interruptCurrentAnswer(includingAgentWork: true)
        clearDetectedElementLocation()
        // A cancelled answer skips its own reset to idle.
        voiceState = .idle
        showAskCaption("stopped", holdSeconds: 1.5)
        scheduleTransientHideIfNeeded()
        return true
    }

    private func stopWatchingForGuidedStep() {
        guidedStepWatcher?.stop()
        guidedStepWatcher = nil
    }

    /// The screenshot for a question could not be taken (Screen Recording off, or ScreenCaptureKit
    /// failing), as opposed to the backend failing.
    struct AskScreenCaptureError: Error {
        let underlyingError: Error
    }

    static let emptyAnswerSentence = "i didn't get an answer. please ask again."

    /// The question as sent: what was said, plus the text it may be about, where the pointer is,
    /// and whether it is the same question again. A text to write over carries the exact words
    /// (the highlight in full, or the box's text); otherwise a highlight is quoted the short way,
    /// as context for "what does this mean". Each line of context sits in its own brackets.
    static func askUserPrompt(transcript: String, writingTarget: WritingTarget?, pointer: PointerInCapture? = nil, isRepeatedQuestion: Bool = false) -> String {
        var contextLines: [String] = []
        if let writingTarget {
            contextLines.append(writingTarget.contextLine)
        } else if let selection = SelectedTextReader.currentSelection() {
            contextLines.append(SelectedTextReader.contextLine(for: selection))
        }
        if let pointer { contextLines.append(pointer.promptLine) }
        if isRepeatedQuestion { contextLines.append(RepeatedQuestion.promptLine) }
        guard !contextLines.isEmpty else { return transcript }
        return transcript + "\n\n" + contextLines.map { "[" + $0 + "]" }.joined(separator: "\n")
    }

    /// An interrupted request: Swift's CancellationError, or URLSession's own (-999) when the task
    /// reading the answer stream was cancelled by a new question.
    nonisolated static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        let nsError = error as NSError
        return nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled
    }

    /// What the companion says when a question fails for a reason that is not the account's
    /// (sign-in and limits have their own sentences). The token sentence is kept for the backend
    /// refusing the token; a capture failure or a dropped connection says what actually happened.
    static func askFailureSentence(for error: Error, screenRecordingAllowed: Bool) -> String {
        // No key for Claude and no account: say which key would do.
        if let unavailable = error as? ProviderRouteUnavailable { return unavailable.sentence }
        if error is AskScreenCaptureError {
            return screenRecordingAllowed
                ? "i couldn't see your screen just now. please ask again."
                : "screen recording is off for openclicky. turn it on in system settings, privacy and security, screen recording."
        }
        if let urlError = error as? URLError {
            switch urlError.code {
            case .notConnectedToInternet, .networkConnectionLost, .timedOut, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed:
                return "i couldn't reach openclicky. check your internet and ask again."
            default:
                break
            }
        }
        if let refusal = refusedQuestion(error), refusal.status == 401 || refusal.status == 403 {
            return "something went wrong talking to the openclicky backend. check the backend and your token in the settings file."
        }
        // Not an HTTP refusal: the stream had started when Anthropic reported the failure.
        let nsError = error as NSError
        if nsError.domain == "ClaudeAPI" && nsError.code == ClaudeAPI.midStreamErrorCode {
            return "the answer stopped partway. please ask again."
        }
        return "something went wrong getting your answer. please ask again."
    }

    // MARK: - Realtime push-to-talk

    private func handleRealtimeShortcutTransition(_ transition: BuddyPushToTalkShortcut.ShortcutTransition) {
        switch transition {
        case .pressed:
            lastQuestionWasTyped = false
            guard !isDictatingToFrontApp else { return }
            lastQuestionFailed = false
            dismissCursorCaption()
            transientHideTask?.cancel()
            transientHideTask = nil
            if !isClickyCursorEnabled && !isOverlayVisible {
                showOverlayOnAllScreens()
            }
            interruptCurrentAnswer(includingAgentWork: true)
            clearDetectedElementLocation()
            TeacherPlugin.clearBoard(reason: "a new question")
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
                    AppLog.append("realtime unavailable: \(error.localizedDescription)")
                    self.voiceState = .idle
                    self.lastQuestionFailed = true
                    self.speakWithSystemVoice("Realtime voice is unavailable right now.")
                    // Why, on screen: "check the backend" sent people looking at the network
                    // when the answer was a rejected key.
                    self.streamCursorCaption(error.localizedDescription, holdSeconds: 12)
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
        await speakWholeReply(spokenText)
    }

    /// The speaker for one spoken reply: its pieces are fetched and played by the answer voice
    /// player, the next one fetched while the current one plays.
    private func makeAnswerSpeaker(onVoiceStarts: @escaping () -> Void, onPointTag: @escaping (String) -> Void) -> StreamingAnswerSpeaker {
        StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(
                fetchAudio: { [answerVoicePlayer] pieceText in try await answerVoicePlayer.fetchAudio(for: pieceText) },
                playAudio: { [answerVoicePlayer] audioData in try await answerVoicePlayer.playToCompletion(audioData) }
            ),
            onVoiceStarts: onVoiceStarts,
            onPointTag: onPointTag
        )
    }

    /// Speaks a reply that is already whole (the agent's result) the way a streamed answer is
    /// spoken: in pieces through `SpeechQueue`, kept as the active answer so the island's stop
    /// button and a new question silence it. It used to go out as one /tts request whose call
    /// returned as soon as playback began, so nothing could stop it and `.responding` was set
    /// after the voice had already started. Returns once the reply has been said, was interrupted,
    /// or the voice failed and the system voice has said what was left.
    private func speakWholeReply(_ replyText: String) async {
        let answerSpeaker = makeAnswerSpeaker(
            onVoiceStarts: { [weak self] in
                self?.voiceState = .responding
                self?.dismissCursorCaption()
            },
            // The agent's reply carries no [POINT:…] tag; the chunker would only strip one.
            onPointTag: { _ in }
        )
        let answerPlayback = Task { @MainActor in try await answerSpeaker.play() }
        activeAnswerSpeaker = answerSpeaker
        activeAnswerPlayback = answerPlayback
        answerSpeaker.receiveWholeReply(replyText)
        do {
            // A question asked meanwhile cancels the calling task; the cancellation is passed on.
            try await withTaskCancellationHandler {
                try await answerPlayback.value
            } onCancel: {
                answerPlayback.cancel()
            }
        } catch let failure as SpeechQueue.Failure {
            guard !Task.isCancelled else { return }
            let nsError = failure.underlyingError as NSError
            print("⚠️ TTS failed after agent run (\(nsError.localizedDescription)); using the system voice")
            AppLog.append("agent: voice failed (\(nsError.code)); using the system voice")
            speakWithSystemVoice(failure.unspokenText.joined(separator: " "))
            await waitForSystemVoiceNoticeToEnd()
        } catch {
            // Interrupted by a new question or the stop button.
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
        rememberExchange(userTranscript: transcript, assistantResponse: spokenText)
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
            while answerVoicePlayer.isPlaying || systemSpeechSynthesizer.isSpeaking || recordedVoice.isPlaying {
                try? await Task.sleep(nanoseconds: 200_000_000)
                guard !Task.isCancelled else { return }
            }

            // Wait for pointing animation to finish (location is cleared
            // when the buddy flies back to the cursor)
            while detectedElementScreenLocation != nil {
                try? await Task.sleep(nanoseconds: 200_000_000)
                guard !Task.isCancelled else { return }
            }

            // Wait for a caption the buddy is still typing out or holding
            while isCursorCaptionVisible {
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
    /// Tells when a system-voice utterance has ended, finished or stopped.
    private let systemSpeechFinishObserver = SystemSpeechFinishObserver()

    /// The bundled recordings of the lines said the same way every time (RecordedVoice.swift).
    let recordedVoice = RecordedVoice()

    /// Bumped for every notice spoken and every interruption, so the end of an older notice (one
    /// cut off by the next, say) never sets a newer turn back to idle.
    private var systemVoiceNoticeNumber = 0
    private var isSystemVoiceNoticeSpeaking = false
    /// Turns waiting for the notice to end before they go back to idle (`waitForSystemVoiceNoticeToEnd`).
    private var systemVoiceNoticeEndWaiters: [CheckedContinuation<Void, Never>] = []

    /// Says `text` in this Mac's voice, or plays its recording when it is one of the fixed notices.
    /// The state is `.responding` while it talks and goes back to `.idle` when it ends: nothing set
    /// it back before, so the island stayed "speaking" after a notice until the next question.
    private func speakWithSystemVoice(_ text: String) {
        answeringSourceName = nil
        stopSystemVoice()
        let noticeNumber = systemVoiceNoticeNumber
        isSystemVoiceNoticeSpeaking = true
        let noticeEnded: () -> Void = { [weak self] in self?.systemVoiceNoticeEnded(noticeNumber: noticeNumber) }
        voiceState = .responding
        // A fixed notice that has a recording plays it, in the app's own voice.
        if let line = RecordedVoice.line(forSpokenText: text), recordedVoice.play(line, onFinished: noticeEnded) {
            return
        }
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = MacVoice.currentVoice()
        utterance.rate = MacVoice.speakingRate(for: utterance.voice)
        systemSpeechFinishObserver.watch(utterance, onEnded: noticeEnded)
        systemSpeechSynthesizer.delegate = systemSpeechFinishObserver
        systemSpeechSynthesizer.speak(utterance)
    }

    private func systemVoiceNoticeEnded(noticeNumber: Int) {
        guard noticeNumber == systemVoiceNoticeNumber, isSystemVoiceNoticeSpeaking else { return }
        isSystemVoiceNoticeSpeaking = false
        resumeSystemVoiceNoticeEndWaiters()
        // A notice is the last thing a turn says: once it has been said, the turn is over.
        guard voiceState == .responding else { return }
        voiceState = .idle
        scheduleTransientHideIfNeeded()
    }

    /// Stops a notice being said in the system voice or played from its recording.
    private func stopSystemVoice() {
        systemVoiceNoticeNumber += 1
        isSystemVoiceNoticeSpeaking = false
        systemSpeechSynthesizer.stopSpeaking(at: .immediate)
        recordedVoice.stop()
        resumeSystemVoiceNoticeEndWaiters()
    }

    /// Returns once the notice being said has ended or was stopped; at once when none is.
    private func waitForSystemVoiceNoticeToEnd() async {
        guard isSystemVoiceNoticeSpeaking else { return }
        await withCheckedContinuation { continuation in
            systemVoiceNoticeEndWaiters.append(continuation)
        }
    }

    private func resumeSystemVoiceNoticeEndWaiters() {
        let waiters = systemVoiceNoticeEndWaiters
        systemVoiceNoticeEndWaiters = []
        waiters.forEach { $0.resume() }
    }

    /// The ask lane offers the quick actions where nothing else does them: whenever the agent lane
    /// doesn't run (a free account, or no openclicky tool on this Mac), and to a plan, which the
    /// agent lane's gate never sees as the one answering.
    static func offersQuickActionsToAskLane(agentLaneRuns: Bool, answeringSource: AnswerSource?) -> Bool {
        if !agentLaneRuns { return true }
        return answeringSource == .claudeCodePlan || answeringSource == .codexPlan
    }

    /// Shared by the Realtime prompt and the ask lane's quick-actions line: a command is done, a
    /// question is taught, and nothing is ever sent.
    static let commandOrQuestionInstruction = "When the user tells you to do something you have a tool for (\"open…\", \"play…\", \"minimize…\", \"search for…\", \"check my email\", \"draft an email\"), do it with the tool and confirm in a few words. Do not explain how they could do it themselves and do not point. When they ask how to do something (\"how do I…\", \"where is…\", \"show me how\"), teach: explain and point, and do not do it for them. If you are not sure which they meant, ask in one short question. You never send email: a draft is left open for the user to read and send."

    static let accountToolsInstruction = "When the user asks you to open an app or a website, search the web, make or show a folder, change the volume, control playback or play a song, minimize everything, check your email or draft an email, call the matching tool instead of describing the steps. For anything else you can only explain and point. " + commandOrQuestionInstruction

    /// What the companion says: the answer, then what each quick action did.
    static func spokenReply(text: String, toolOutcomes: [String]) -> String {
        let answer = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return ([answer] + toolOutcomes).filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// The refusal of a question (ClaudeAPI's /chat, or Anthropic on an own key), as it came; nil
    /// for anything else, including the voice's own refusals.
    static func refusedQuestion(_ error: Error) -> BackendHTTPError? {
        guard let refusal = error as? BackendHTTPError, refusal.service == .answers else { return nil }
        return refusal
    }

    /// The plain sentence for an account limit, when the ask lane's Claude call answered 402.
    static func limitSentence(for error: Error) -> String? {
        guard let refusal = refusedQuestion(error), refusal.status == 402 else { return nil }
        let message = AccountLimitError.from(status: 402, body: refusal.body)?.message ?? ""
        return message.isEmpty ? AccountLimitError.personalLimit.message : message
    }

    /// The question was refused with a 401: the account's token (expired, or issued before a move),
    /// or the person's own key, refused by Anthropic or relayed by the backend.
    static func isSignInRejection(_ error: Error) -> Bool {
        refusedQuestion(error)?.status == 401
    }

    /// The account's answer to a failed ask: a 402 limit, or the free service's 503 trouble.
    static func isAccountNotice(_ error: Error) -> Bool {
        guard let refusal = refusedQuestion(error) else { return false }
        return refusal.status == 402 || isServiceTrouble(refusal)
    }

    /// The sentence for an account notice the first time this session; nil afterwards (and for any
    /// other error), so a lane that keeps hitting the same limit falls back silently.
    static func accountNoticeToSpeak(for error: Error, alreadySpoken: inout Set<String>) -> String? {
        let sentence: String?
        if let refusal = refusedQuestion(error), isServiceTrouble(refusal) {
            sentence = AccountLimitError.serviceTroubleMessage
        } else {
            sentence = limitSentence(for: error)
        }
        guard let sentence, alreadySpoken.insert(sentence).inserted else { return nil }
        return sentence
    }

    private static func isServiceTrouble(_ refusal: BackendHTTPError) -> Bool {
        refusal.status == 503 && AccountLimitError.isServiceTrouble(status: refusal.status, body: refusal.body)
    }

    /// The screens a question carries: the cursor's screen first, and for an account at most one
    /// other (the grant takes two images).
    static func screensForAsk(_ captures: [CompanionScreenCapture], capabilities: AccountCapabilities) -> [CompanionScreenCapture] {
        let cursorFirst = captures.filter(\.isCursorScreen) + captures.filter { !$0.isCursorScreen }
        guard let limit = capabilities.maxAskScreens else { return cursorFirst }
        return Array(cursorFirst.prefix(limit))
    }

    /// One quiet caption (the running-low notice), typed out the way the Home tab's (i) is.
    func showQuietNotice(_ message: String) {
        showCaptionBesideMouse(message, holdSeconds: 8)
        scheduleTransientHideIfNeeded()
    }

    /// Speaks a hardcoded error message with the system voice when the agent lane fails. It uses
    /// the synthesizer every new question stops: a separate NSSpeechSynthesizer used to keep
    /// talking over the next question.
    private func speakCreditsErrorFallback() {
        speakWithSystemVoice("something went wrong talking to the openclicky backend. check the backend and your token in the settings file.")
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

    /// Parses a [POINT:x,y:label:screenN] or [POINT:none] tag from the start or the end of Claude's
    /// response (the ask prompt puts it first so the pointer can move as the voice starts; the
    /// onboarding demo puts it last). Returns the spoken text (tag removed) and the optional
    /// coordinate + label + screen number.
    static func parsePointingCoordinates(from responseText: String) -> PointingParseResult {
        // No recursion: a leading tag is cut off, then the trailing tag is looked for once in what is
        // left. Moving the leading tag to the end and parsing again recursed forever (and crashed the
        // app) when two tags stood with nothing else between them.
        var leadingTag: String?
        var textAfterLeadingTag = responseText
        if let leadingTagRange = responseText.range(of: #"^\s*\[POINT:[^\]]*\]"#, options: .regularExpression) {
            leadingTag = responseText[leadingTagRange].trimmingCharacters(in: .whitespacesAndNewlines)
            textAfterLeadingTag = String(responseText[leadingTagRange.upperBound...])
        }

        var trailingTag: String?
        var spokenText = textAfterLeadingTag
        if let trailingMatch = pointTagRegex(anchoredAtEnd: true).firstMatch(
            in: textAfterLeadingTag, range: NSRange(textAfterLeadingTag.startIndex..., in: textAfterLeadingTag)),
           let trailingTagRange = Range(trailingMatch.range, in: textAfterLeadingTag) {
            trailingTag = textAfterLeadingTag[trailingTagRange].trimmingCharacters(in: .whitespacesAndNewlines)
            spokenText = String(textAfterLeadingTag[..<trailingTagRange.lowerBound])
        }
        spokenText = spokenText.trimmingCharacters(in: .whitespacesAndNewlines)

        // The first tag that reads as one decides where to point (the prompt puts it first).
        let firstReadableTag = [leadingTag, trailingTag].compactMap { $0 }.lazy.compactMap(readPointTag).first
        guard let firstReadableTag else {
            // No tag found at all (a malformed leading tag is still never spoken)
            return PointingParseResult(spokenText: leadingTag == nil ? responseText : spokenText, coordinate: nil, elementLabel: nil, screenNumber: nil)
        }
        return PointingParseResult(
            spokenText: spokenText,
            coordinate: firstReadableTag.coordinate,
            elementLabel: firstReadableTag.elementLabel,
            screenNumber: firstReadableTag.screenNumber
        )
    }

    /// Matches [POINT:none] or [POINT:123,456:label] or [POINT:123,456:label:screen2]: at the end
    /// of the text (with trailing whitespace), or as the whole string.
    private static func pointTagRegex(anchoredAtEnd: Bool) -> NSRegularExpression {
        let tagPattern = #"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]"#
        let pattern = anchoredAtEnd ? tagPattern + #"\s*$"# : "^" + tagPattern + "$"
        return try! NSRegularExpression(pattern: pattern, options: [])
    }

    /// One tag on its own, read: nil when it is not a well-formed tag; a nil coordinate for [POINT:none].
    private static func readPointTag(_ pointTag: String) -> (coordinate: CGPoint?, elementLabel: String?, screenNumber: Int?)? {
        guard let match = pointTagRegex(anchoredAtEnd: false).firstMatch(in: pointTag, range: NSRange(pointTag.startIndex..., in: pointTag)) else {
            return nil
        }
        // Check if it's [POINT:none]
        guard let xRange = Range(match.range(at: 1), in: pointTag),
              let yRange = Range(match.range(at: 2), in: pointTag),
              let x = Double(pointTag[xRange]),
              let y = Double(pointTag[yRange]) else {
            return (coordinate: nil, elementLabel: "none", screenNumber: nil)
        }
        var elementLabel: String? = nil
        if let labelRange = Range(match.range(at: 3), in: pointTag) {
            elementLabel = String(pointTag[labelRange]).trimmingCharacters(in: .whitespaces)
        }
        var screenNumber: Int? = nil
        if let screenRange = Range(match.range(at: 4), in: pointTag) {
            screenNumber = Int(pointTag[screenRange])
        }
        return (coordinate: CGPoint(x: x, y: y), elementLabel: elementLabel, screenNumber: screenNumber)
    }

    // MARK: - Onboarding

    /// OpenClicky: upstream played a hosted intro video here (the original author's stream) and
    /// scheduled a demo 40s in. OpenClicky ships no video: after the welcome bubble we run the
    /// pointing demo once, then stream in the "press control + option" prompt. Kept the name so
    /// the overlay's call site is unchanged.
    func setupOnboardingVideo() {
        ClickyAnalytics.trackOnboardingDemoTriggered()
        performOnboardingDemoInteraction()
        DispatchQueue.main.asyncAfter(deadline: .now() + 6.0) { [weak self] in
            guard let self, !self.isCursorCaptionVisible else { return }
            self.startOnboardingPromptStream()
        }
    }

    private func startOnboardingPromptStream() {
        streamCursorCaption("press control + option and introduce yourself")
    }

    // MARK: - Ask lane feedback

    /// Where the current ⌃⌥ press is: held, waiting for the session's final words, or settled (a
    /// question was handed over, or "didn't catch that" / an error was shown). Only an unsettled
    /// press reacts to the dictation manager, which the fn lane shares.
    enum AskOutcome { case resolved, listening, transcribing }
    private var askOutcome: AskOutcome = .resolved
    private var askLiveWordsShownSincePress = false
    private var askDictationOutcomeCancellables: Set<AnyCancellable> = []

    /// Follows the dictation session for the ask lane: its end without a question means nothing was
    /// heard, and its error message (microphone or speech denied, an engine failure) is the reason.
    private func bindAskDictationOutcome() {
        // Delivered on the next main-queue turn: the manager publishes "no longer finalizing" just
        // before it hands over the final words in the same call, so the question is in by then.
        buddyDictationManager.$isFinalizingTranscript
            .removeDuplicates()
            .filter { !$0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.askDictationSessionEnded() }
            .store(in: &askDictationOutcomeCancellables)
        buddyDictationManager.$lastErrorMessage
            .compactMap { $0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] errorMessage in self?.showAskDictationError(errorMessage) }
            .store(in: &askDictationOutcomeCancellables)
    }

    private func askDictationSessionEnded() {
        guard askOutcome == .transcribing else { return }
        askOutcome = .resolved
        AppLog.append("ask: nothing heard")
        showAskCaption("didn't catch that. hold ⌃⌥ and ask again.", holdSeconds: 3)
    }

    private func showAskDictationError(_ errorMessage: String) {
        guard askOutcome != .resolved else { return }
        askOutcome = .resolved
        AppLog.append("ask: dictation failed: \(errorMessage)")
        showAskCaption(errorMessage, holdSeconds: 6)
    }

    /// A caption by the pointer for the ask lane (what was heard, "thinking…", a problem).
    private func showAskCaption(_ message: String, holdSeconds: TimeInterval) {
        streamCursorCaption(message, holdSeconds: holdSeconds)
    }

    /// A caption shown whole at once: one that has to be readable
    /// within the moment it stands for (the pause before sending), where typing it out would not be.
    private func showAskCaptionAtOnce(_ message: String, holdSeconds: TimeInterval) {
        showAskLiveTranscript(message)
        scheduleCursorCaptionDismiss(after: holdSeconds)
    }

    /// The question as it is being heard, shown at once (no typewriter): it changes many times a second.
    private func showAskLiveTranscript(_ partialTranscript: String) {
        let heardSoFar = partialTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !heardSoFar.isEmpty else { return }
        cursorCaptionTypingTimer?.invalidate()
        cursorCaptionTypingTimer = nil
        cursorCaptionDismissWorkItem?.cancel()
        cursorCaptionDismissWorkItem = nil
        cursorCaptionText = heardSoFar
        cursorCaptionOpacity = 1.0
        isCursorCaptionVisible = true
    }

    // MARK: - Cursor caption (typewriter text next to the buddy)

    private var cursorCaptionTypingTimer: Timer?
    private var cursorCaptionDismissWorkItem: DispatchWorkItem?

    /// Types `message` out next to the buddy one character at a time (the onboarding prompt, the
    /// Home tab's (i), hands-free feedback), holds it for `holdSeconds`, then fades it out.
    func streamCursorCaption(_ message: String, holdSeconds: TimeInterval = 10) {
        cursorCaptionTypingTimer?.invalidate()
        cursorCaptionDismissWorkItem?.cancel()
        cursorCaptionText = ""
        isCursorCaptionVisible = true
        cursorCaptionOpacity = 0.0

        withAnimation(.easeIn(duration: 0.4)) {
            cursorCaptionOpacity = 1.0
        }

        let characters = Array(message)
        var nextCharacterIndex = 0
        cursorCaptionTypingTimer = Timer.scheduledTimer(withTimeInterval: 0.03, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            guard nextCharacterIndex < characters.count else {
                timer.invalidate()
                self.scheduleCursorCaptionDismiss(after: holdSeconds)
                return
            }
            self.cursorCaptionText.append(characters[nextCharacterIndex])
            nextCharacterIndex += 1
        }
    }

    private func scheduleCursorCaptionDismiss(after seconds: TimeInterval) {
        cursorCaptionDismissWorkItem?.cancel()
        let dismissWorkItem = DispatchWorkItem { [weak self] in self?.dismissCursorCaption() }
        cursorCaptionDismissWorkItem = dismissWorkItem
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: dismissWorkItem)
    }

    func dismissCursorCaption() {
        cursorCaptionTypingTimer?.invalidate()
        cursorCaptionTypingTimer = nil
        cursorCaptionDismissWorkItem?.cancel()
        cursorCaptionDismissWorkItem = nil
        guard isCursorCaptionVisible else { return }
        withAnimation(.easeOut(duration: 0.3)) {
            cursorCaptionOpacity = 0.0
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            guard let self, self.cursorCaptionOpacity == 0.0 else { return }
            self.isCursorCaptionVisible = false
            self.cursorCaptionText = ""
        }
    }

    /// A caption beside the mouse pointer on the display in use, with the overlay brought up for it
    /// if the pointer was out of sight (a quiet notice while it rests).
    private func showCaptionBesideMouse(_ message: String, holdSeconds: TimeInterval) {
        if !isOverlayVisible {
            transientHideTask?.cancel()
            transientHideTask = nil
            showOverlayOnAllScreens()
        }
        streamCursorCaption(message, holdSeconds: holdSeconds)
    }

    /// The Home tab's (i): the buddy types out what OpenClicky does, next to itself; a hidden
    /// buddy is shown for the duration.
    func explainWhatOpenClickyDoes() {
        let message = "hi, i'm openclicky. hold control + option and ask about anything on your screen. "
            + "i'll answer out loud and point at what you need. tap control twice to type instead, "
            + "hold fn to dictate into any app, and real work goes to the agent."
        if !isOverlayVisible {
            transientHideTask?.cancel()
            transientHideTask = nil
            showOverlayOnAllScreens()
        }
        streamCursorCaption(message, holdSeconds: 8)
        scheduleTransientHideIfNeeded()
    }

    /// The prompt for the pointing demo (onboarding and the tour's "watch this"): one specific thing
    /// near the middle of the screen, a few words about it, and the [POINT:…] tag last.
    private static let onboardingDemoSystemPrompt = """
    you're openclicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding. look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text", be specific about exactly what you see.

    make a short quirky 3-6 word observation about the specific thing you picked, something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen, just react to it. keep it to 6 words max, no exceptions.

    CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no menu bar items, no dock icons, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.

    respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.

    format: your comment [POINT:x,y:label]

    the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
    """

    /// Captures a screenshot and asks Claude to find something interesting to
    /// point at, then triggers the buddy's flight animation. Used during
    /// onboarding to demo the pointing feature after the welcome bubble.
    func performOnboardingDemoInteraction() {
        // Don't interrupt an active voice response
        guard voiceState == .idle || voiceState == .responding else { return }

        Task {
            do {
                // The overlay asked for this demo, so it is already showing.
                guard let parseResult = try await pointAtSomethingOnScreenForDemo(showingTheOverlayIfHidden: false) else {
                    print("🎯 Onboarding demo: no element to point at")
                    return
                }
                print("🎯 Onboarding demo: pointing at \"\(parseResult.elementLabel ?? "element")\", \"\(parseResult.spokenText)\"")
            } catch {
                print("⚠️ Onboarding demo error: \(error)")
            }
        }
    }

    /// The pointing demo shared by the onboarding and the tour's "watch this": a screenshot of the
    /// cursor's screen only (Claude must not pick something on a display the buddy is not on),
    /// Claude picks one specific thing and comments on it, and the buddy flies there with that
    /// comment in its bubble instead of a random phrase. Returns what Claude said, or nil when
    /// there was no cursor screen or nothing to point at.
    private func pointAtSomethingOnScreenForDemo(showingTheOverlayIfHidden: Bool) async throws -> PointingParseResult? {
        let screenCaptures = try await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()
        guard let cursorScreenCapture = screenCaptures.first(where: { $0.isCursorScreen }) else { return nil }
        let (fullResponseText, _, _) = try await claudeAPI.analyzeImageStreaming(
            images: [Self.imageLabeledWithItsPixelSize(cursorScreenCapture)],
            systemPrompt: Self.onboardingDemoSystemPrompt,
            userPrompt: "look around my screen and find something interesting to point at",
            onTextChunk: { _ in }
        )
        let parseResult = Self.parsePointingCoordinates(from: fullResponseText)
        guard let pointCoordinate = parseResult.coordinate else { return nil }
        if showingTheOverlayIfHidden && !isOverlayVisible {
            showOverlayOnAllScreens()
        }
        detectedElementBubbleText = parseResult.spokenText
        detectedElementScreenLocation = Self.screenLocation(forScreenshotPoint: pointCoordinate, in: cursorScreenCapture)
        detectedElementDisplayFrame = cursorScreenCapture.displayFrame
        detectedElementPointToken &+= 1
        return parseResult
    }

    /// A screenshot with its label and real pixel size ("… (image dimensions: 1280x800 pixels)"),
    /// so the coordinates Claude answers with are in the image it saw; we scale from screenshot
    /// pixels to display points ourselves.
    static func imageLabeledWithItsPixelSize(_ capture: CompanionScreenCapture) -> (data: Data, label: String) {
        let dimensionInfo = " (image dimensions: \(capture.screenshotWidthInPixels)x\(capture.screenshotHeightInPixels) pixels)"
        return (data: capture.imageData, label: capture.label + dimensionInfo)
    }

    /// Remembers one exchange for the ask lane's next question, keeping only the last 10 so the
    /// context does not grow without bound over a long session.
    private func rememberExchange(userTranscript: String, assistantResponse: String) {
        conversationHistory.append((userTranscript: userTranscript, assistantResponse: assistantResponse))
        if conversationHistory.count > Self.maximumRememberedExchanges {
            conversationHistory.removeFirst(conversationHistory.count - Self.maximumRememberedExchanges)
        }
    }

    private static let maximumRememberedExchanges = 10
}

/// Tells the companion when the system-voice utterance it is watching has ended: spoken to the
/// end, or stopped (the next notice, a new question, the stop button). Only the latest utterance
/// counts, so the end of one cut off by the next is not mistaken for the new one's.
private final class SystemSpeechFinishObserver: NSObject, AVSpeechSynthesizerDelegate {
    private weak var watchedUtterance: AVSpeechUtterance?
    private var onWatchedUtteranceEnded: (() -> Void)?

    /// `utterance` is about to be spoken; `onEnded` runs once when it ends.
    func watch(_ utterance: AVSpeechUtterance, onEnded: @escaping () -> Void) {
        watchedUtterance = utterance
        onWatchedUtteranceEnded = onEnded
    }

    func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        DispatchQueue.main.async { self.utteranceEnded(utterance) }
    }

    func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        DispatchQueue.main.async { self.utteranceEnded(utterance) }
    }

    private func utteranceEnded(_ utterance: AVSpeechUtterance) {
        guard utterance === watchedUtterance else { return }
        let onEnded = onWatchedUtteranceEnded
        watchedUtterance = nil
        onWatchedUtteranceEnded = nil
        onEnded?()
    }
}
