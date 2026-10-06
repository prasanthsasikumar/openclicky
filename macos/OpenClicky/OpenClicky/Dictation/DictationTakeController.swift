//
//  DictationTakeController.swift
//  OpenClicky
//
//  One take at a time: the dictation key goes down, the microphone opens, words arrive, the key
//  comes up, the words are formatted in the front app's style and pasted where the cursor is, and
//  the take is written to history. Holding the key is one take; a tap starts a take that the next
//  tap ends; two quick taps or esc discard it; control joining the held key turns the take into a
//  Hey Clicky edit of the selection. The orb shows every step.
//

import AppKit
import Combine
import Foundation

/// Builds the transcription provider the settings ask for, and says why one cannot be used.
enum DictationEngineResolver {
    @MainActor
    static func makeProvider(for choice: DictationEngineChoice, settings: DictationSettings) -> any BuddyTranscriptionProvider {
        switch choice {
        case .offline:
            return AppleSpeechTranscriptionProvider()
        case .sarvam:
            return SarvamTranscriptionProvider(language: { settings.language })
        case .openclicky:
            return OpenAIAudioTranscriptionProvider()
        case .assemblyai:
            return AssemblyAIStreamingTranscriptionProvider()
        }
    }

    /// Nil when the engine can be used; otherwise one line saying what is missing.
    static func unavailableReason(for choice: DictationEngineChoice) -> String? {
        switch choice {
        case .offline:
            return nil
        case .sarvam:
            let key = OpenClickyConfiguration.settings.sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            return key.isEmpty ? "needs your sarvam key" : nil
        case .openclicky, .assemblyai:
            return OpenClickyConfiguration.isConfigured ? nil : "needs an openclicky account or your openai key"
        }
    }

    /// The model that polishes takes and answers Hey Clicky: Sarvam with a key, else the backend
    /// with an account, else none.
    static func makePolisher() -> (any TakePolisher)? {
        let sarvamKey = OpenClickyConfiguration.settings.sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !sarvamKey.isEmpty { return SarvamTakePolisher(client: SarvamSpeechClient(key: sarvamKey)) }
        if OpenClickyConfiguration.isConfigured { return BackendTakePolisher() }
        return nil
    }
}

@MainActor
final class DictationTakeController: ObservableObject {

    enum TakeMode { case dictate, edit }

    /// What the controller is doing, for the UI that is not the orb (menu bar, record page).
    @Published private(set) var isTakeInProgress = false
    @Published private(set) var lastTake: TakeRecord?
    /// Bumped when history changes, so the window's pages reload.
    @Published private(set) var historyVersion = 0

    let settings: DictationSettings
    let spaceStore: DictationSpaceStore
    let takeStore: TakeStore?
    let orb: OrbModel
    let earcons: DictationEarconPlayer
    private(set) var dictationManager: BuddyDictationManager

    /// Called before the microphone opens (the Realtime engine must let go of it) and after a take.
    var beforeMicrophoneOpens: (() async -> Void)?
    var afterMicrophoneCloses: (() -> Void)?
    /// A take's words when the window wants them (the onboarding's first take, the record page box).
    var onTakeFinished: ((TakeRecord) -> Void)?

    private var cancellables: Set<AnyCancellable> = []
    private var currentEngine: DictationEngineChoice
    private var pendingStartTask: Task<Void, Never>?
    private var inactivityTimer: Timer?
    private var lastLoudMomentAt = Date()

    // The take in progress.
    private var takeID = UUID()
    private var takeMode: TakeMode = .dictate
    private var takeStartedAt = Date()
    private var takeStartedByTap = false
    private var takeIsToggle = false
    private var takeWasCancelled = false
    /// The engine delivered its final words; the "nothing heard" fallback must stand down.
    private var takeReceivedFinalTranscript = false
    private var fieldAtPress = FocusedFieldSnapshot.unknown
    private var nearbyTermsAtPress: [String] = []

    init(settings: DictationSettings, spaceStore: DictationSpaceStore, takeStore: TakeStore?, orb: OrbModel) {
        self.settings = settings
        self.spaceStore = spaceStore
        self.takeStore = takeStore
        self.orb = orb
        self.earcons = DictationEarconPlayer(settings: settings)
        self.currentEngine = settings.engine
        self.dictationManager = BuddyDictationManager(transcriptionProvider: DictationEngineResolver.makeProvider(for: settings.engine, settings: settings))
        bind()
    }

    private func bind() {
        settings.$engine
            .removeDuplicates()
            .sink { [weak self] engine in
                guard let self, engine != self.currentEngine, !self.isTakeInProgress else { return }
                self.currentEngine = engine
                self.dictationManager.replaceTranscriptionProvider(DictationEngineResolver.makeProvider(for: engine, settings: self.settings))
                AppLog.append("dictation engine → \(engine.rawValue)")
            }
            .store(in: &cancellables)
        dictationManager.$currentAudioPowerLevel
            .receive(on: DispatchQueue.main)
            .sink { [weak self] level in
                guard let self else { return }
                self.orb.audioLevel = level
                if level > 0.08 { self.lastLoudMomentAt = Date() }
            }
            .store(in: &cancellables)
        dictationManager.$lastErrorMessage
            .compactMap { $0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] message in self?.failTake(message) }
            .store(in: &cancellables)
        // A session that ended without a transcript (nothing said, permission denied) leaves the
        // controller thinking a take is open; the manager's state says otherwise.
        dictationManager.$isKeyboardShortcutSessionActiveOrFinalizing
            .receive(on: DispatchQueue.main)
            .sink { [weak self] active in
                guard let self, self.isTakeInProgress, !active, self.pendingStartTask == nil else { return }
                // Give the final transcript callback a tick to land first.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
                    guard let self, self.isTakeInProgress, !self.takeReceivedFinalTranscript,
                          !self.dictationManager.isDictationInProgress else { return }
                    self.failTake("didn't catch that")
                }
            }
            .store(in: &cancellables)
    }

    var engineDisplayName: String { dictationManager.transcriptionProviderDisplayName }

    // MARK: keyboard

    /// Feeds a shortcut event; true when it was one of dictation's.
    @discardableResult
    func handle(_ event: CompanionShortcutEvent) -> Bool {
        switch event {
        case .dictationPressed:
            if isTakeInProgress {
                // A second press while a tapped take listens: this press ends it.
                if takeIsToggle { stopTake() }
                return true
            }
            beginTake(mode: .dictate)
            return true
        case .dictationEditModifierJoined:
            guard isTakeInProgress, takeMode == .dictate else { return true }
            takeMode = .edit
            orb.phase = .editListening
            orb.hint = "say your edit, then let go"
            return true
        case .dictationReleased(let wasTap):
            guard isTakeInProgress, !takeIsToggle else { return true }
            if wasTap {
                // A tap: keep listening until the next tap (or a double tap cancels).
                takeIsToggle = true
                orb.hint = "say it, then tap \(settings.dictationKey.keycapLabel)"
                return true
            }
            stopTake()
            return true
        case .dictationDoubleTapped:
            cancelTake(reason: "cancelled")
            return true
        case .escapePressed:
            guard isTakeInProgress else { return false }
            cancelTake(reason: "cancelled")
            return true
        case .talkPressed, .talkReleased, .textComposerRequested, .handsFreeToggleRequested:
            return false
        }
    }

    /// The orb was clicked: like a tap of the key.
    func orbClicked() {
        if isTakeInProgress {
            stopTake()
        } else {
            beginTake(mode: .dictate)
            takeIsToggle = true
            orb.hint = "say it, then tap the orb"
        }
    }

    // MARK: the take

    private func beginTake(mode: TakeMode) {
        guard !isTakeInProgress, !dictationManager.isDictationInProgress else { return }
        if let reason = DictationEngineResolver.unavailableReason(for: settings.engine) {
            earcons.play(.blocked)
            orb.phase = .failed(reason)
            scheduleIdle(after: 2.5)
            return
        }
        isTakeInProgress = true
        takeID = UUID()
        takeMode = mode
        takeStartedAt = Date()
        takeIsToggle = false
        takeWasCancelled = false
        takeReceivedFinalTranscript = false
        lastLoudMomentAt = Date()
        fieldAtPress = FocusedFieldReader.snapshot()
        nearbyTermsAtPress = settings.readNearbyText ? FocusedFieldReader.nearbyTerms() : []
        orb.liveTranscript = ""
        orb.isBoxOpen = settings.orbRestsExpanded && orb.boxText != nil
        orb.phase = mode == .edit ? .editListening : .listening
        orb.hint = "tap / release to transcribe"
        earcons.play(.start)
        earcons.tap()
        startInactivityTimer()
        AppLog.append("take \(takeID.uuidString.prefix(8)) started (\(mode)) in \(fieldAtPress.appName ?? "?") field=\(fieldAtPress.role ?? "none") editable=\(fieldAtPress.isEditable)")

        pendingStartTask?.cancel()
        pendingStartTask = Task { [weak self] in
            guard let self else { return }
            await self.beforeMicrophoneOpens?()
            guard !Task.isCancelled else { return }
            await self.dictationManager.startPushToTalkFromKeyboardShortcut(
                currentDraftText: "",
                updateDraftText: { [weak self] partial in self?.orb.liveTranscript = partial },
                submitDraftText: { [weak self] final in
                    guard let self else { return }
                    self.takeReceivedFinalTranscript = true
                    Task { await self.finishTake(rawText: final) }
                })
            self.pendingStartTask = nil
        }
    }

    private func stopTake() {
        guard isTakeInProgress else { return }
        inactivityTimer?.invalidate()
        let heldFor = Date().timeIntervalSince(takeStartedAt)
        AppLog.append("take \(takeID.uuidString.prefix(8)) stopped after \(String(format: "%.1f", heldFor)) s")
        earcons.play(.stop)
        orb.phase = .working(takeMode == .edit ? "finishing your edit" : "moving your words")
        orb.hint = nil
        if let pendingStartTask {
            // Released before the microphone opened: nothing was said.
            pendingStartTask.cancel()
            self.pendingStartTask = nil
            dictationManager.cancelCurrentDictation(preserveDraftText: false)
            failTake("didn't catch that")
            return
        }
        dictationManager.stopPushToTalkFromKeyboardShortcut()
    }

    func cancelTake(reason: String) {
        guard isTakeInProgress else { return }
        takeWasCancelled = true
        inactivityTimer?.invalidate()
        pendingStartTask?.cancel()
        pendingStartTask = nil
        dictationManager.cancelCurrentDictation(preserveDraftText: false)
        earcons.play(.blocked)
        orb.phase = .failed(reason)
        orb.liveTranscript = ""
        orb.hint = nil
        AppLog.append("take \(takeID.uuidString.prefix(8)) cancelled: \(reason)")
        endTake()
        scheduleIdle(after: 1.4)
    }

    private func failTake(_ message: String) {
        guard isTakeInProgress else { return }
        inactivityTimer?.invalidate()
        earcons.play(.error)
        orb.phase = .failed(message)
        orb.liveTranscript = ""
        AppLog.append("take \(takeID.uuidString.prefix(8)) failed: \(message)")
        if !settings.incognito, !message.hasPrefix("didn't catch") {
            let record = TakeRecord(id: takeID, createdAt: takeStartedAt, mode: takeMode == .edit ? .edit : .dictate, status: .failed,
                                    rawText: orb.liveTranscript, formattedText: "", appBundleID: fieldAtPress.appBundleID,
                                    appName: fieldAtPress.appName, language: settings.language.bareCode, engine: settings.engine.rawValue,
                                    durationSeconds: Date().timeIntervalSince(takeStartedAt), failureReason: message)
            try? takeStore?.insert(record)
            historyVersion += 1
        }
        endTake()
        scheduleIdle(after: 2.5)
    }

    private func endTake() {
        isTakeInProgress = false
        takeIsToggle = false
        afterMicrophoneCloses?()
    }

    private func finishTake(rawText: String) async {
        guard isTakeInProgress, !takeWasCancelled else { return }
        let raw = rawText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else {
            failTake("didn't catch that")
            return
        }
        let duration = Date().timeIntervalSince(takeStartedAt)
        let space = spaceStore.space
        let style = space.style(forAppBundleID: fieldAtPress.appBundleID)
        let context = TakeFormattingContext(
            style: style, dictionary: space.dictionary, shortcuts: space.shortcuts, appName: fieldAtPress.appName,
            language: settings.language, script: settings.script, nearbyTerms: nearbyTermsAtPress)
        let polisher = DictationEngineResolver.makePolisher()

        let outputText: String
        var formattingDegraded = false
        if takeMode == .edit {
            orb.phase = .working("finishing your edit")
            guard let polisher else {
                failTake("hey clicky needs a model: add a sarvam key or sign in")
                return
            }
            let selection = fieldAtPress.selectedText ?? lastTake?.displayText ?? ""
            guard !selection.isEmpty else {
                failTake("select some text first, or dictate something to edit")
                return
            }
            do {
                outputText = try await HeyClickyEditor.edit(selection: selection, instruction: raw, context: context, polisher: polisher)
            } catch {
                failTake("couldn't finish your edit")
                return
            }
        } else {
            let formatted = await TakeFormatter.format(raw, context: context, polisher: polisher, wantsModel: settings.polishWithModel)
            outputText = formatted.text
            formattingDegraded = formatted.formattingDegraded
        }
        guard isTakeInProgress, !takeWasCancelled else { return }

        // Paste where the cursor was when the key went down, if that was a text box.
        var pasteOutcome = TakeRecord.PasteOutcome.none
        var pasteMessage = "moved to text box"
        if fieldAtPress.isSecure {
            pasteOutcome = .leftInOrb
            pasteMessage = "not pasting into a password field"
        } else if fieldAtPress.isEditable || !AXIsProcessTrusted() || fieldAtPress.role == nil {
            // Editable, or unknown (no permission / the app exposes nothing): try the paste.
            switch FrontAppTextInserter.insert(outputText) {
            case .typed:
                let landing = await PasteLanding.verify(text: outputText, before: fieldAtPress)
                switch landing {
                case .verified: pasteOutcome = .verified
                case .posted: pasteOutcome = .posted
                case .noTarget: pasteOutcome = .leftInOrb
                case .leftOnPasteboard: pasteOutcome = .leftOnPasteboard
                }
            case .leftOnPasteboard:
                pasteOutcome = .leftOnPasteboard
                pasteMessage = "copied — press ⌘V (allow accessibility to paste for you)"
            }
        } else {
            pasteOutcome = .leftInOrb
            pasteMessage = "no text box found, copy from here"
        }

        let record = TakeRecord(id: takeID, createdAt: takeStartedAt, mode: takeMode == .edit ? .edit : .dictate, status: .complete,
                                rawText: takeMode == .edit ? raw : raw, formattedText: outputText, appBundleID: fieldAtPress.appBundleID,
                                appName: fieldAtPress.appName, language: settings.language.bareCode, engine: settings.engine.rawValue,
                                durationSeconds: duration, pasteOutcome: pasteOutcome)
        lastTake = record
        if !settings.incognito {
            try? takeStore?.insert(record)
            historyVersion += 1
        }
        onTakeFinished?(record)

        orb.boxText = outputText
        orb.liveTranscript = ""
        let showBox = pasteOutcome == .leftInOrb || pasteOutcome == .leftOnPasteboard
            || (pasteOutcome == .posted && settings.orbOpensBoxWhenPasteUnverified) || settings.orbRestsExpanded
        orb.isBoxOpen = showBox
        earcons.play(pasteOutcome == .verified || pasteOutcome == .posted ? .complete : .notify)
        earcons.tap()
        orb.phase = .done(formattingDegraded && pasteOutcome != .leftInOrb ? "moved to text box · cleaned up locally" : pasteMessage)
        AppLog.append("take \(takeID.uuidString.prefix(8)) done: \(outputText.count) chars, paste=\(pasteOutcome.rawValue), engine=\(settings.engine.rawValue), degraded=\(formattingDegraded)")
        endTake()
        scheduleIdle(after: 1.8)
    }

    private func scheduleIdle(after seconds: TimeInterval) {
        let expectedPhase = orb.phase
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard let self, !self.isTakeInProgress, self.orb.phase == expectedPhase else { return }
            self.orb.phase = .idle
            self.orb.hint = self.idleHint
        }
    }

    /// The hint pill under a resting orb ("tooltips" in Settings → the orb).
    var idleHint: String { "tap / hold \(settings.dictationKey.keycapLabel) to talk" }

    /// Asks the configured model a question over the takes that match it (history → "press enter to ask").
    func askHistory(_ question: String) async -> String {
        guard let polisher = DictationEngineResolver.makePolisher() else {
            return "asking needs a model: add a sarvam key or sign in under settings."
        }
        let words = question.split(whereSeparator: { $0.isWhitespace }).map(String.init).filter { $0.count > 3 }
        var candidates = (try? takeStore?.recent(limit: 60, query: words.joined(separator: " "))) ?? []
        if candidates.isEmpty { candidates = (try? takeStore?.recent(limit: 60)) ?? [] }
        guard !candidates.isEmpty else { return "nothing in history to ask about yet." }
        let formatter = DateFormatter()
        formatter.dateFormat = "EEE d MMM, h:mm a"
        let context = candidates.prefix(60).map { take in
            "[\(formatter.string(from: take.createdAt))] (\(take.appName ?? "?")) \(take.displayText.replacingOccurrences(of: "\n", with: " ").prefix(400))"
        }.joined(separator: "\n")
        let system = "You answer questions about a person's own dictation history. Use only the takes given; quote the relevant words and say when they were said. If nothing matches, say so in one sentence. Answer in two or three sentences, plainly."
        do {
            return try await polisher.polish(system: system, user: "Question: \(question)\n\nTakes:\n\(context)")
        } catch {
            return "couldn't ask right now: \(error.localizedDescription)"
        }
    }

    /// "inactivity timeout": a take with that long of silence ends on its own.
    private func startInactivityTimer() {
        inactivityTimer?.invalidate()
        guard settings.inactivityTimeoutMinutes > 0 else { return }
        let limit = TimeInterval(settings.inactivityTimeoutMinutes * 60)
        inactivityTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isTakeInProgress else { return }
                if Date().timeIntervalSince(self.lastLoudMomentAt) >= limit {
                    AppLog.append("take \(self.takeID.uuidString.prefix(8)) ended by the inactivity timeout")
                    self.stopTake()
                }
            }
        }
    }

    // MARK: history actions used by the window

    func pasteLast() {
        guard let text = lastTake?.displayText ?? (try? takeStore?.recent(limit: 1).first?.displayText) ?? nil else { return }
        _ = FrontAppTextInserter.insert(text)
    }

    func historyDidChange() {
        historyVersion += 1
    }
}

/// Hey Clicky: an instruction applied to a piece of text by the configured model.
enum HeyClickyEditor {
    static func edit(selection: String, instruction: String, context: TakeFormattingContext, polisher: any TakePolisher) async throws -> String {
        let system = """
        You edit text for someone who dictated an instruction. Apply the instruction to the text and return only the edited text: no preamble, no quotes, no explanation, no markdown fences. Keep the language and script of the text unless the instruction changes them. \
        Style for this app (\(context.style.name)): \(context.style.rules)
        """
        let user = "Instruction: \(instruction)\n\nText:\n\(selection)"
        let answer = try await polisher.polish(system: system, user: user)
        let cleaned = TakeFormatter.unwrapModelAnswer(answer)
        guard !cleaned.isEmpty else { throw SarvamSpeechError.unreadable("the edit came back empty") }
        return cleaned
    }
}
