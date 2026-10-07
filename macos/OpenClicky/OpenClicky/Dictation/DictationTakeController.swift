//
//  DictationTakeController.swift
//  OpenClicky
//
//  One take at a time: the dictation key goes down, the microphone opens, words arrive, the key
//  comes up, the words are formatted in the front app's style and pasted where the cursor was, and
//  the take is written to history. Holding the key is one take; a tap starts a take that the next
//  tap ends; two quick taps or esc discard it; control joining the held key turns the take into a
//  Hey Clicky edit of the selection. The orb shows every step.
//
//  The take is a small state machine (idle → starting → listening → finishing → idle) and every
//  asynchronous continuation carries the take's id: a take that was cancelled, or that ended while
//  a model was still polishing it, can never paste into the one that came after.
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
            return AppleSpeechTranscriptionProvider(preferredLocale: settings.language.bareCode == nil ? nil : Locale(identifier: settings.language.code))
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

    /// Whether a take's words may go to a model: the switch, and — for the offline engine, whose
    /// promise is that nothing leaves the Mac — only when that was explicitly allowed.
    @MainActor
    static func wantsModelPolish(settings: DictationSettings) -> Bool {
        guard settings.polishWithModel else { return false }
        if settings.engine == .offline { return settings.polishOfflineTakes }
        return true
    }
}

@MainActor
final class DictationTakeController: ObservableObject {

    enum TakeMode { case dictate, edit }

    /// Where the take is.
    enum TakeState: Equatable {
        case idle
        /// The key is down; the microphone is being opened.
        case starting
        case listening
        /// The key came up; the engine, the formatter and the paste are at work.
        case finishing
    }

    @Published private(set) var state: TakeState = .idle
    @Published private(set) var lastTake: TakeRecord?
    /// Bumped when history changes, so the window's pages reload.
    @Published private(set) var historyVersion = 0
    /// A failed take whose audio is on disk and can be heard again (history's "retry").
    @Published private(set) var retryableTakeID: UUID?
    @Published private(set) var isRetrying = false

    var isTakeInProgress: Bool { state != .idle }

    let settings: DictationSettings
    let spaceStore: DictationSpaceStore
    let takeStore: TakeStore?
    let orb: OrbModel
    let earcons: DictationEarconPlayer
    let audioStore: TakeAudioStore
    private(set) var dictationManager: any DictationCapturing
    private let host: DictationHost

    /// Called before the microphone opens (the Realtime engine must let go of it) and after a take.
    var beforeMicrophoneOpens: (() async -> Void)?
    var afterMicrophoneCloses: (() -> Void)?
    /// A take's words when the window wants them (the onboarding's first take, the record page box).
    var onTakeFinished: ((TakeRecord) -> Void)?

    private var cancellables: Set<AnyCancellable> = []
    private var currentEngine: DictationEngineChoice
    private var pendingStartTask: Task<Void, Never>?
    /// A Hey Clicky press (control already down) waits out the tap window before it opens the
    /// microphone: a quick tap of the key with control is the hands-free gesture, not an edit.
    private var pendingEditStartWork: DispatchWorkItem?
    private var inactivityTimer: Timer?
    private var lastLoudMomentAt = Date()
    /// Closes the orb's transcript box a moment after it opened for a take.
    private var boxCloseWork: DispatchWorkItem?
    /// The box stays this long after a take whose words had nowhere to go; a click on it or a new
    /// take ends that early. "rest with the box open" keeps it instead.
    static let boxLingerSeconds: TimeInterval = 8

    // The take in progress.
    private var takeID = UUID()
    private var takeMode: TakeMode = .dictate
    private var takeStartedAt = Date()
    private var takeIsToggle = false
    private var takeReceivedFinalTranscript = false
    private var fieldAtPress = FocusedFieldSnapshot.unknown
    private var nearbyTermsAtPress: [String] = []

    init(settings: DictationSettings, spaceStore: DictationSpaceStore, takeStore: TakeStore?, orb: OrbModel,
         capture: (any DictationCapturing)? = nil, host: DictationHost = DictationHost(), audioStore: TakeAudioStore = TakeAudioStore()) {
        self.settings = settings
        self.spaceStore = spaceStore
        self.takeStore = takeStore
        self.orb = orb
        self.host = host
        self.audioStore = audioStore
        self.earcons = DictationEarconPlayer(settings: settings)
        self.currentEngine = settings.engine
        self.dictationManager = capture ?? BuddyDictationManager(transcriptionProvider: DictationEngineResolver.makeProvider(for: settings.engine, settings: settings))
        self.dictationManager.audioRetentionSink = { buffer in audioStore.append(buffer) }
        orb.onQuickRewrite = { [weak self] instruction in Task { await self?.rewriteLastTake(instruction: instruction) } }
        orb.onPasteFromBox = { [weak self] in self?.pasteFromBox() }
        bind()
    }

    /// The rewrites the box offers: Kivi's casual and formal keys, as chips, plus shorter.
    static let quickRewrites: [(label: String, instruction: String)] = [
        ("formal", "Rewrite it formally: full sentences, full forms, no slang, polite."),
        ("casual", "Rewrite it casually: lowercase, shorthand welcome, light punctuation, warm."),
        ("shorter", "Make it shorter: the fewest words that still say it, nothing added."),
    ]

    /// A chip under the box: the last take is rewritten by the model and the box shows the result;
    /// history keeps the previous text as a revision.
    func rewriteLastTake(instruction label: String) async {
        guard !orb.isRewriting, let text = orb.boxText, !text.isEmpty,
              let rewrite = Self.quickRewrites.first(where: { $0.label == label }),
              let polisher = host.makePolisher() else { return }
        boxCloseWork?.cancel()
        orb.isRewriting = true
        defer { orb.isRewriting = false }
        let space = spaceStore.space
        let context = TakeFormattingContext(
            style: space.style(forAppBundleID: lastTake?.appBundleID), dictionary: space.dictionary, shortcuts: space.shortcuts,
            appName: lastTake?.appName, language: settings.language, script: settings.script)
        do {
            let rewritten = try await HeyClickyEditor.edit(selection: text, instruction: rewrite.instruction, context: context, polisher: polisher)
            orb.boxText = rewritten
            orb.boxReason = "\(label) · paste or copy"
            orb.isBoxOpen = true
            if let last = lastTake, !settings.incognito {
                try? takeStore?.revise(takeID: last.id, newText: rewritten, editor: "quick-\(label)")
                lastTake?.formattedText = rewritten
                historyVersion += 1
            }
            AppLog.append("quick rewrite (\(label)): \(rewritten.count) chars")
        } catch {
            orb.boxReason = "couldn't rewrite: \(error.localizedDescription)"
        }
        scheduleBoxClose(after: 20)
    }

    /// "paste" in the box: the words go to the app in front (the orb never takes focus).
    func pasteFromBox() {
        guard let text = orb.boxText, !text.isEmpty else { return }
        guard host.frontAppBundleID() != Bundle.main.bundleIdentifier else {
            orb.boxReason = "click into the app you want it in, then paste"
            return
        }
        switch host.paste(text) {
        case .typed: orb.boxReason = "pasted"
        case .leftOnPasteboard: orb.boxReason = "copied — press ⌘V"
        }
        if let last = lastTake, last.pasteOutcome == .leftInOrb, !settings.incognito {
            var pasted = last
            pasted.pasteOutcome = .posted
            try? takeStore?.insert(pasted)
            lastTake = pasted
            historyVersion += 1
        }
        scheduleBoxClose(after: 3)
    }

    private func bind() {
        settings.$engine.combineLatest(settings.$languageCode)
            .dropFirst()
            .sink { [weak self] engine, _ in
                guard let self, !self.isTakeInProgress else { return }
                self.currentEngine = engine
                self.dictationManager.replaceTranscriptionProvider(DictationEngineResolver.makeProvider(for: engine, settings: self.settings))
                AppLog.append("dictation engine → \(engine.rawValue)")
            }
            .store(in: &cancellables)
        dictationManager.audioPowerLevelPublisher
            .receive(on: DispatchQueue.main)
            .sink { [weak self] level in
                guard let self else { return }
                self.orb.audioLevel = level
                if level > 0.08 { self.lastLoudMomentAt = Date() }
            }
            .store(in: &cancellables)
        dictationManager.errorMessagePublisher
            .compactMap { $0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] message in
                // An error belongs to the take whose session is open; one that lands after the
                // engine already delivered its words (a late socket close) is noise.
                guard let self, self.isTakeInProgress, !self.takeReceivedFinalTranscript else { return }
                self.failTake(message, takeID: self.takeID)
            }
            .store(in: &cancellables)
        // A session that ended without a transcript (nothing said, permission denied) leaves the
        // controller thinking a take is open; the manager's state says otherwise.
        dictationManager.sessionActivityPublisher
            .receive(on: DispatchQueue.main)
            .sink { [weak self] active in
                guard let self, self.isTakeInProgress, !active, self.pendingStartTask == nil else { return }
                let endedTakeID = self.takeID
                // Give the final transcript callback a tick to land first.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
                    guard let self, self.isTakeInProgress, self.takeID == endedTakeID, !self.takeReceivedFinalTranscript,
                          !self.dictationManager.isDictationInProgress else { return }
                    self.failTake("didn't catch that", takeID: endedTakeID)
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
            switch state {
            case .idle:
                beginTake(mode: .dictate)
            case .listening where takeIsToggle:
                // A second press while a tapped take listens: this press ends it.
                stopTake()
            case .starting, .listening, .finishing:
                break
            }
            return true

        case .dictationEditPressed:
            // Control was already down when the key went down: a Hey Clicky press, unless it
            // turns out to be a tap (the hands-free gesture), which the tap window decides.
            guard state == .idle else { return true }
            pendingEditStartWork?.cancel()
            let work = DispatchWorkItem { [weak self] in
                guard let self, self.state == .idle else { return }
                self.pendingEditStartWork = nil
                self.beginTake(mode: .edit)
            }
            pendingEditStartWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + CompanionShortcutRecognizer.tapMaxHoldSeconds + 0.05, execute: work)
            return true

        case .dictationEditModifierJoined:
            switch state {
            case .starting, .listening:
                guard takeMode == .dictate else { break }
                takeMode = .edit
                orb.phase = .editListening
                orb.hint = "say your edit, then let go"
            case .idle, .finishing:
                break
            }
            return true

        case .dictationReleased(let wasTap):
            if let pendingEditStartWork {
                // Released inside the tap window: not an edit, nothing was opened.
                pendingEditStartWork.cancel()
                self.pendingEditStartWork = nil
                return true
            }
            switch state {
            case .idle, .finishing:
                break
            case .starting, .listening:
                if takeIsToggle { break }
                if wasTap {
                    // A tap: keep listening until the next tap (or a double tap cancels).
                    takeIsToggle = true
                    orb.hint = "say it, then tap \(settings.dictationKey.keycapLabel)"
                } else {
                    stopTake()
                }
            }
            return true

        case .dictationDoubleTapped:
            if isTakeInProgress { cancelTake(reason: "cancelled") }
            return true

        case .escapePressed:
            guard isTakeInProgress else { return false }
            cancelTake(reason: "cancelled")
            return true

        case .handsFreeToggleRequested:
            // The gesture's taps never opened a take (see dictationEditModifierJoined); the
            // companion toggles always-on listening.
            pendingEditStartWork?.cancel()
            pendingEditStartWork = nil
            return false

        case .talkPressed, .talkReleased, .textComposerRequested:
            return false
        }
    }

    /// The orb was clicked: like a tap of the key.
    func orbClicked() {
        switch state {
        case .idle:
            beginTake(mode: .dictate)
            takeIsToggle = true
            orb.hint = "say it, then tap the orb"
        case .starting, .listening:
            stopTake()
        case .finishing:
            break
        }
    }

    // MARK: the take

    private func beginTake(mode: TakeMode) {
        guard state == .idle, !dictationManager.isDictationInProgress else { return }
        if let reason = host.engineUnavailableReason(settings.engine) {
            earcons.play(.blocked)
            orb.phase = .failed(reason)
            scheduleIdle(after: 2.5)
            return
        }
        let thisTakeID = UUID()
        takeID = thisTakeID
        state = .starting
        takeMode = mode
        takeStartedAt = Date()
        takeIsToggle = false
        takeReceivedFinalTranscript = false
        lastLoudMomentAt = Date()
        // The focused element is read now (a few AX calls); the window's captions, which can take
        // hundreds of milliseconds in a browser, are read once the microphone is open.
        fieldAtPress = host.readFocusedField()
        nearbyTermsAtPress = []
        audioStore.beginCapture()
        boxCloseWork?.cancel()
        dictationManager.updateContextualKeyterms(spaceStore.space.dictionary.map(\.written))
        orb.liveTranscript = ""
        orb.isBoxOpen = settings.orbRestsExpanded && orb.boxText != nil
        orb.phase = mode == .edit ? .editListening : .listening
        orb.hint = mode == .edit ? "say your edit, then let go" : "tap / release to transcribe"
        earcons.play(.start)
        earcons.tap()
        startInactivityTimer()
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) started (\(mode)) in \(fieldAtPress.appName ?? "?") field=\(fieldAtPress.role ?? "none") editable=\(fieldAtPress.isEditable)")

        pendingStartTask?.cancel()
        pendingStartTask = Task { [weak self] in
            guard let self else { return }
            await self.beforeMicrophoneOpens?()
            guard !Task.isCancelled, self.takeID == thisTakeID, self.state == .starting else { return }
            await self.dictationManager.startPushToTalkFromKeyboardShortcut(
                currentDraftText: "",
                updateDraftText: { [weak self] partial in
                    guard let self, self.takeID == thisTakeID else { return }
                    self.orb.liveTranscript = partial
                },
                submitDraftText: { [weak self] final in
                    guard let self, self.takeID == thisTakeID else { return }
                    self.takeReceivedFinalTranscript = true
                    Task { await self.finishTake(rawText: final, takeID: thisTakeID) }
                })
            guard self.takeID == thisTakeID else { return }
            self.pendingStartTask = nil
            if self.state == .starting { self.state = .listening }
            if self.settings.readNearbyText {
                let readNearbyTerms = self.host.readNearbyTerms
                let terms = await Task.detached(priority: .utility) { readNearbyTerms() }.value
                if self.takeID == thisTakeID { self.nearbyTermsAtPress = terms }
            }
        }
    }

    private func stopTake() {
        guard state == .starting || state == .listening else { return }
        let thisTakeID = takeID
        inactivityTimer?.invalidate()
        let heldFor = Date().timeIntervalSince(takeStartedAt)
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) stopped after \(String(format: "%.1f", heldFor)) s")
        earcons.play(.stop)
        orb.phase = .working(takeMode == .edit ? "finishing your edit" : "moving your words")
        orb.hint = nil
        if state == .starting, let pendingStartTask {
            // Released before the microphone opened: nothing was said.
            pendingStartTask.cancel()
            self.pendingStartTask = nil
            dictationManager.cancelCurrentDictation(preserveDraftText: false)
            state = .finishing
            failTake("didn't catch that", takeID: thisTakeID)
            return
        }
        state = .finishing
        dictationManager.stopPushToTalkFromKeyboardShortcut()
    }

    func cancelTake(reason: String) {
        guard isTakeInProgress else { return }
        let thisTakeID = takeID
        inactivityTimer?.invalidate()
        pendingStartTask?.cancel()
        pendingStartTask = nil
        dictationManager.cancelCurrentDictation(preserveDraftText: false)
        earcons.play(.blocked)
        orb.phase = .failed(reason)
        orb.liveTranscript = ""
        orb.hint = nil
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) cancelled: \(reason)")
        _ = audioStore.endCapture()
        endTake()
        scheduleIdle(after: 1.4)
    }

    private func failTake(_ message: String, takeID failedTakeID: UUID) {
        guard isTakeInProgress, takeID == failedTakeID else { return }
        inactivityTimer?.invalidate()
        earcons.play(.error)
        orb.phase = .failed(message)
        orb.liveTranscript = ""
        AppLog.append("take \(failedTakeID.uuidString.prefix(8)) failed: \(message)")
        let pcm = audioStore.endCapture()
        if !settings.incognito, !message.hasPrefix("didn't catch") {
            let record = TakeRecord(id: failedTakeID, createdAt: takeStartedAt, mode: takeMode == .edit ? .edit : .dictate, status: .failed,
                                    rawText: orb.liveTranscript, formattedText: "", appBundleID: fieldAtPress.appBundleID,
                                    appName: fieldAtPress.appName, language: settings.language.bareCode, engine: settings.engine.rawValue,
                                    durationSeconds: Date().timeIntervalSince(takeStartedAt), failureReason: message)
            try? takeStore?.insert(record)
            historyVersion += 1
            // The words are still in the audio: keep it so the take can be heard again.
            if settings.retainFailedTakeAudio, settings.engine != .offline, pcm.count > 16_000 {
                do {
                    try audioStore.retain(takeID: failedTakeID, pcm16: pcm)
                    retryableTakeID = failedTakeID
                    orb.phase = .failed("\(message) · take saved, retry from history")
                } catch {
                    AppLog.append("take \(failedTakeID.uuidString.prefix(8)) audio not retained: \(error.localizedDescription)")
                }
            }
        }
        endTake()
        scheduleIdle(after: 2.5)
    }

    private func endTake() {
        state = .idle
        takeIsToggle = false
        afterMicrophoneCloses?()
    }

    /// Still this take, still wanted: false once it was cancelled or another take began.
    private func isCurrent(_ id: UUID) -> Bool {
        takeID == id && state == .finishing
    }

    private func finishTake(rawText: String, takeID thisTakeID: UUID) async {
        guard takeID == thisTakeID, state == .listening || state == .starting || state == .finishing else { return }
        state = .finishing
        _ = audioStore.endCapture()
        let raw = rawText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty else {
            failTake("didn't catch that", takeID: thisTakeID)
            return
        }
        let duration = Date().timeIntervalSince(takeStartedAt)
        let space = spaceStore.space
        let style = space.style(forAppBundleID: fieldAtPress.appBundleID)
        let context = TakeFormattingContext(
            style: style, dictionary: space.dictionary, shortcuts: space.shortcuts, appName: fieldAtPress.appName,
            language: settings.language, script: settings.script, nearbyTerms: nearbyTermsAtPress)
        let polisher = host.makePolisher()
        let mode = takeMode
        let field = fieldAtPress

        let outputText: String
        var formattingDegraded = false
        if mode == .edit {
            orb.phase = .working("finishing your edit")
            guard let polisher else {
                failTake("hey clicky needs a model: add a sarvam key or sign in", takeID: thisTakeID)
                return
            }
            let selection = field.selectedText ?? lastTake?.displayText ?? ""
            guard !selection.isEmpty else {
                failTake("select some text first, or dictate something to edit", takeID: thisTakeID)
                return
            }
            do {
                outputText = try await HeyClickyEditor.edit(selection: selection, instruction: raw, context: context, polisher: polisher)
            } catch {
                guard isCurrent(thisTakeID) else { return }
                failTake("couldn't finish your edit", takeID: thisTakeID)
                return
            }
        } else {
            let formatted = await TakeFormatter.format(raw, context: context, polisher: polisher, wantsModel: DictationEngineResolver.wantsModelPolish(settings: settings))
            outputText = formatted.text
            formattingDegraded = formatted.formattingDegraded
        }
        // Cancelled, or another take began, while the model was at work: these words are dropped.
        guard isCurrent(thisTakeID) else {
            AppLog.append("take \(thisTakeID.uuidString.prefix(8)) finished after it was cancelled; words dropped")
            return
        }

        // Paste where the cursor was when the key went down — and only there.
        let frontNow = host.frontAppBundleID()
        var pasteOutcome = TakeRecord.PasteOutcome.none
        var pasteMessage = "moved to text box"
        if field.isSecure || host.isSecureInputOn() {
            pasteOutcome = .leftInOrb
            pasteMessage = "not pasting into a password field"
        } else if let target = field.appBundleID, let frontNow, target != frontNow {
            pasteOutcome = .leftInOrb
            pasteMessage = "the app changed — copy from here"
        } else if field.isEditable || !host.isAccessibilityTrusted() || field.role == nil {
            // Editable, or unknown (no permission / the app exposes nothing): try the paste.
            switch host.paste(outputText) {
            case .typed:
                let landing = await host.verifyPaste(outputText, field)
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
        guard takeID == thisTakeID else { return }

        let record = TakeRecord(id: thisTakeID, createdAt: takeStartedAt, mode: mode == .edit ? .edit : .dictate, status: .complete,
                                rawText: raw, formattedText: outputText, appBundleID: field.appBundleID,
                                appName: field.appName, language: settings.language.bareCode, engine: settings.engine.rawValue,
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
        orb.boxReason = pasteOutcome == .verified || pasteOutcome == .posted ? "your last take" : pasteMessage
        orb.quickRewrites = host.makePolisher() == nil ? [] : Self.quickRewrites.map(\.label)
        orb.isBoxOpen = showBox
        scheduleBoxClose()
        earcons.play(pasteOutcome == .verified || pasteOutcome == .posted ? .complete : .notify)
        earcons.tap()
        if formattingDegraded, pasteOutcome == .verified || pasteOutcome == .posted { pasteMessage = "moved to text box · cleaned up locally" }
        orb.phase = .done(pasteMessage)
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) done: \(outputText.count) chars, paste=\(pasteOutcome.rawValue), engine=\(settings.engine.rawValue), degraded=\(formattingDegraded)")
        endTake()
        scheduleIdle(after: 1.8)
    }

    /// The box is for the moment the words had nowhere to go; it does not stay on screen.
    private func scheduleBoxClose(after seconds: TimeInterval = DictationTakeController.boxLingerSeconds) {
        boxCloseWork?.cancel()
        guard orb.isBoxOpen, !settings.orbRestsExpanded else { return }
        let work = DispatchWorkItem { [weak self] in
            guard let self, !self.isTakeInProgress, !self.orb.isRewriting, !self.settings.orbRestsExpanded else { return }
            self.orb.isBoxOpen = false
        }
        boxCloseWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
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

    /// "inactivity timeout": a take with that long of silence ends on its own.
    private func startInactivityTimer() {
        inactivityTimer?.invalidate()
        guard settings.inactivityTimeoutMinutes > 0 else { return }
        let limit = TimeInterval(settings.inactivityTimeoutMinutes * 60)
        inactivityTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.state == .listening else { return }
                if Date().timeIntervalSince(self.lastLoudMomentAt) >= limit {
                    AppLog.append("take \(self.takeID.uuidString.prefix(8)) ended by the inactivity timeout")
                    self.stopTake()
                }
            }
        }
    }

    // MARK: asking history

    /// The takes a history question is answered from: those containing every longer word of it
    /// (empty when none do, and the model is then given the latest takes instead).
    func historyMatches(for question: String) -> [TakeRecord] {
        let words = question.split(whereSeparator: { $0.isWhitespace }).map(String.init).filter { $0.count > 3 }
        guard !words.isEmpty else { return [] }
        return (try? takeStore?.recent(limit: 60, query: words.joined(separator: " "))) ?? []
    }

    /// Asks the configured model a question over the takes that match it (history → "press enter to ask").
    func askHistory(_ question: String) async -> String {
        guard let polisher = host.makePolisher() else {
            return "asking needs a model: add a sarvam key or sign in under settings."
        }
        var candidates = historyMatches(for: question)
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

    // MARK: retrying a failed take

    func canRetry(takeID: UUID) -> Bool { audioStore.hasAudio(for: takeID) }

    /// Hears a failed take's retained audio again with the current engine, formats it, and puts the
    /// words in the orb's box and in history (the cursor has moved on, so nothing is pasted).
    func retry(takeID: UUID) async {
        guard !isTakeInProgress, !isRetrying, audioStore.hasAudio(for: takeID) else { return }
        if let reason = host.engineUnavailableReason(settings.engine) {
            orb.phase = .failed(reason)
            scheduleIdle(after: 2.5)
            return
        }
        isRetrying = true
        orb.phase = .working("retrying your recording")
        defer { isRetrying = false }
        let provider = DictationEngineResolver.makeProvider(for: settings.engine, settings: settings)
        let original = try? takeStore?.fetch(id: takeID)
        do {
            let raw = try await TakeAudioStore.transcribe(fileURL: audioStore.url(for: takeID), provider: provider, keyterms: spaceStore.space.dictionary.map(\.written))
            guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                orb.phase = .failed("didn't catch that")
                scheduleIdle(after: 2.5)
                return
            }
            let space = spaceStore.space
            let context = TakeFormattingContext(
                style: space.style(forAppBundleID: original?.appBundleID), dictionary: space.dictionary, shortcuts: space.shortcuts,
                appName: original?.appName, language: settings.language, script: settings.script)
            let formatted = await TakeFormatter.format(raw, context: context, polisher: host.makePolisher(), wantsModel: DictationEngineResolver.wantsModelPolish(settings: settings))
            let record = TakeRecord(id: takeID, createdAt: original?.createdAt ?? Date(), mode: .dictate, status: .complete,
                                    rawText: raw, formattedText: formatted.text, appBundleID: original?.appBundleID, appName: original?.appName,
                                    language: settings.language.bareCode, engine: settings.engine.rawValue,
                                    durationSeconds: original?.durationSeconds ?? 0, pasteOutcome: .leftInOrb)
            if !settings.incognito { try? takeStore?.insert(record) }
            historyVersion += 1
            lastTake = record
            audioStore.discard(takeID: takeID)
            if retryableTakeID == takeID { retryableTakeID = nil }
            orb.boxText = formatted.text
            orb.boxReason = "retried · copy from here"
            orb.isBoxOpen = true
            scheduleBoxClose()
            earcons.play(.complete)
            orb.phase = .done("retried · copy from the box")
            AppLog.append("take \(takeID.uuidString.prefix(8)) retried: \(formatted.text.count) chars")
            scheduleIdle(after: 2.5)
        } catch {
            orb.phase = .failed("couldn't retry: \(error.localizedDescription)")
            AppLog.append("take \(takeID.uuidString.prefix(8)) retry failed: \(error.localizedDescription)")
            scheduleIdle(after: 3)
        }
    }

    // MARK: history actions used by the window

    /// Pastes the last take into the app in front, after the window has given focus back.
    func pasteLast() {
        guard let text = lastTake?.displayText ?? (try? takeStore?.recent(limit: 1, mode: .dictate).first?.displayText) ?? nil else { return }
        NSApp.hide(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { _ = FrontAppTextInserter.insert(text) }
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
