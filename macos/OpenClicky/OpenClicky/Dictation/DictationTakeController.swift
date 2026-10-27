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
        case .parakeet:
            return ParakeetTranscriptionProvider()
        case .sarvam:
            return SarvamTranscriptionProvider(language: { settings.language })
        case .openclicky:
            // The styles page's language, read when a take starts; auto sends none so the model detects it.
            return OpenAIAudioTranscriptionProvider(language: { settings.language.bareCode })
        case .assemblyai:
            return AssemblyAIStreamingTranscriptionProvider(language: { settings.language.bareCode })
        }
    }

    /// Nil when the engine can be used; otherwise one line saying what is missing.
    static func unavailableReason(for choice: DictationEngineChoice) -> String? {
        unavailableReason(for: choice, kind: AccountCapabilities.current().kind, isConfigured: OpenClickyConfiguration.isConfigured,
                          signedIn: OpenClickyConfiguration.hasAccountToken, sarvamKey: OpenClickyConfiguration.settings.sarvamKey)
    }

    /// The backend engines hear through OpenAI (/agent/transcribe, or OpenAI itself on an own key) and
    /// AssemblyAI (/transcribe-token, minted on OpenClicky's key: account only), neither of which a
    /// free account's grant covers.
    static func unavailableReason(for choice: DictationEngineChoice, kind: AccountKind, isConfigured: Bool, signedIn: Bool, sarvamKey: String?,
                                  parakeetInstalled: Bool = ParakeetDictation.isInstalled,
                                  bareLanguageCode: String? = DictationSettings.storedLanguage().bareCode) -> String? {
        switch choice {
        case .offline:
            return nil
        case .parakeet:
            return parakeetUnavailableReason(isInstalled: parakeetInstalled, bareLanguageCode: bareLanguageCode)
        case .sarvam:
            return sarvamUnavailableReason(sarvamKey: sarvamKey)
        case .openclicky, .assemblyai:
            guard isConfigured else { return "needs an openclicky account or your openai key" }
            if kind == .account { return "needs your own key" }
            if choice == .assemblyai && !signedIn { return "needs an openclicky account: assemblyai runs on openclicky's key" }
            return nil
        }
    }

    /// Parakeet's half of `unavailableReason`. Its provider reads the same function, so the provider
    /// and the resolver can never disagree about whether Parakeet can run.
    static func parakeetUnavailableReason(isInstalled: Bool, bareLanguageCode: String?) -> String? {
        if !ParakeetDictation.hears(bareLanguageCode: bareLanguageCode) { return parakeetLanguageNotHeardReason }
        return isInstalled ? nil : "needs its \(ParakeetDictation.downloadSizeText) download"
    }

    /// Installed but pinned to a language Parakeet does not hear: the island says this, not "download".
    static let parakeetLanguageNotHeardReason = "parakeet doesn't hear this language; pick auto-detect or a european language"

    /// Sarvam's half of `unavailableReason`; its provider reads the same function.
    static func sarvamUnavailableReason(sarvamKey: String?) -> String? {
        let trimmedSarvamKey = sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmedSarvamKey.isEmpty ? "needs your sarvam key" : nil
    }

    /// The ways to get a model, for a Hey Clicky edit or a history question asked with none set up.
    /// Apple's model comes first because it is free and needs nothing but the Mac.
    static let howToGetAModelSentence = "apple intelligence on this mac, one of your own keys (settings → account), or an openclicky account"

    /// The model that polishes takes and answers Hey Clicky: Apple's on-device model where the Mac
    /// has it, else Sarvam with a key, else the person's
    /// own OpenAI, Anthropic, Groq, Gemini, Mistral, OpenRouter or xAI key, else the backend with
    /// an account, else none.
    static func makePolisher() -> (any TakePolisher)? {
        // Apple's on-device model first: free, private and already on the Mac where it exists.
        #if canImport(FoundationModels)
        if #available(macOS 26, *), AppleModelTakePolisherAvailability.isAvailable(languageCode: DictationSettings.storedLanguage().code) {
            return AppleModelTakePolisher()
        }
        #endif
        let sarvamKey = OpenClickyConfiguration.settings.sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !sarvamKey.isEmpty { return SarvamTakePolisher(client: SarvamSpeechClient(key: sarvamKey)) }
        // Own keys first: an OpenAI key polishes on OpenAI (BackendTakePolisher routes it there),
        // an Anthropic key alone polishes on Claude Haiku at Anthropic, any other answer key on its
        // own vendor. No account needed for any of them.
        let credentials = ProviderRouter.currentCredentials()
        if credentials.openAIKey == nil {
            if let anthropicKey = credentials.anthropicKey { return AnthropicTakePolisher(anthropicKey: anthropicKey) }
            if let answerKeyPolisher = AnswerKeyTakePolisher.firstAvailable(credentials: credentials) { return answerKeyPolisher }
        }
        switch AccountCapabilities.current().kind {
        case .account: return AccountTakePolisher()
        case .ownKeys: return BackendTakePolisher()
        case .signedOut: return nil
        }
    }

    /// Whether a take's words may go to a model: the switch, and — for the offline engine, whose
    /// promise is that nothing leaves the Mac — only when that was explicitly allowed.
    /// Every reader of "clean up my words with ai" asks here (the styles switch, the record page,
    /// the take, live typing, the smoke run), so the switch never shows one thing while takes do another.
    @MainActor
    static func wantsModelPolish(settings: DictationSettings) -> Bool {
        wantsModelPolish(settings: settings, engine: settings.engine)
    }

    /// The same, for an engine other than the chosen one (the smoke run names its own).
    @MainActor
    static func wantsModelPolish(settings: DictationSettings, engine: DictationEngineChoice) -> Bool {
        guard settings.polishWithModel else { return false }
        if engine.isOnDevice { return settings.polishOfflineTakes }
        return true
    }
}

@MainActor
final class DictationTakeController: ObservableObject {
    /// How cautiously live typing commits words. The defaults are the live values; tests that
    /// exercise the algorithm pin their own.
    var liveTypingTiming = StableWordCommitter.Timing()
    /// Waits out `liveTypingTiming.pauseSeconds` after each partial; a new partial restarts it.
    private var liveTypingPauseTask: Task<Void, Never>?

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
    /// The key went down with no text box focused: the pointer says so beside the mouse, so the
    /// person knows to click into one instead of finding out from "copied, press ⌘V" at the end.
    var onNoTextBoxAtPress: ((String) -> Void)?
    /// How long a take that began outside any text box, and still has none when the key is let
    /// go, waits for one to be clicked into: people let go first and click after (tested 2026-10-13).
    var secondsToWaitForATextBoxAfterRelease: TimeInterval = 8
    /// The first-run tour's practice: takes are heard and recorded but never typed or pasted.
    var holdsTakesOnTheIsland = false

    private var cancellables: Set<AnyCancellable> = []
    private var currentEngine: DictationEngineChoice
    /// The language the engine in use was built for.
    private var currentLanguageCode: String
    private var pendingStartTask: Task<Void, Never>?
    /// A Hey Clicky press (control already down) waits out the tap window before it opens the
    /// microphone: a quick tap of the key with control is the hands-free gesture, not an edit.
    private var pendingEditStartWork: DispatchWorkItem?
    private var inactivityTimer: Timer?
    private var lastLoudMomentAt = Date()
    /// The loudest level the microphone reported during this take; nil until it reports any
    /// (the tests drive takes without a microphone).
    private var loudestLevelThisTake: CGFloat?
    /// How many levels the microphone reported this take: a real one reports many a second, so
    /// a handful is too little audio to call silent.
    private var levelSamplesThisTake = 0
    /// Below this the whole take was room noise: a microphone that hears nothing (the headphone
    /// jack with speakers in it), whose silence the offline recogniser wrote down as "no".
    static let silentTakeLevel: CGFloat = 0.03
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
    /// The focused element itself at press (Accessibility's identity): the paste goes only there,
    /// not into another field of the same app that has focus by release.
    private var focusedTargetAtPress: FocusedTextTarget?
    private var nearbyTermsAtPress: [String] = []
    /// Set when this take's words are typed as they settle (no model polish will run); nil otherwise.
    private var liveTyping: LiveTypingSession?
    /// The model that may polish this take, chosen once when the key goes down: live typing's
    /// decision, the clean-up and the box's rewrite chips all read the same answer, and building a
    /// polisher (Apple's model checks its availability) is not repeated for every step of a take.
    private var polisherForThisTake: (any TakePolisher)?
    /// A history write that failed is logged every time but said on the orb only once per run.
    private var hasShownHistoryWriteFailure = false

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
        self.currentLanguageCode = settings.languageCode
        self.dictationManager = capture ?? BuddyDictationManager(transcriptionProvider: DictationEngineResolver.makeProvider(for: settings.engine, settings: settings))
        self.dictationManager.audioRetentionSink = { buffer in audioStore.append(buffer) }
        orb.onQuickRewrite = { [weak self] instruction in Task { await self?.rewriteLastTake(instruction: instruction) } }
        orb.onPasteFromBox = { [weak self] in self?.pasteFromBox() }
        bind()
    }

    /// What the orb says when a Hey Clicky edit is asked for with the clean-up switch off.
    static let heyClickyNeedsCleanUpMessage = "hey clicky needs “clean up my words with ai” on (styles)"

    /// What the pointer says when a take starts with no text box focused.
    static let noTextBoxPointerMessage = "you're not in a text box. click into one and your words will go there."
    static let noTextBoxOrbHint = "click into a text box for your words"

    /// Accessibility can read the focused element and it is not a text box (a list, a button, a
    /// page's text): the words would have nowhere to go. Unknown (no permission, an app that
    /// exposes nothing) is not "no text box": the paste is tried then, as before.
    static func hasNoTextBoxToPasteInto(_ field: FocusedFieldSnapshot, isAccessibilityTrusted: Bool) -> Bool {
        isAccessibilityTrusted && field.role != nil && !field.isEditable && !field.isSecure
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
        let context = makeFormattingContext(appBundleID: lastTake?.appBundleID, appName: lastTake?.appName)
        do {
            let rewritten = try await HeyClickyEditor.edit(selection: text, instruction: rewrite.instruction, context: context, polisher: polisher)
            orb.boxText = rewritten
            orb.boxReason = "\(label) · paste or copy"
            orb.isBoxOpen = true
            if let last = lastTake, !settings.incognito {
                if let historyNote = writeToHistory("quick rewrite", takeID: last.id, { try $0.revise(takeID: last.id, newText: rewritten, editor: "quick-\(label)") }) {
                    orb.boxReason = "\(label) · paste or copy · \(historyNote)"
                }
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
        case .leftOnPasteboard: orb.boxReason = "copied, press ⌘V"
        }
        if let last = lastTake, last.pasteOutcome == .leftInOrb, !settings.incognito {
            var pasted = last
            pasted.pasteOutcome = .posted
            if let historyNote = writeToHistory("paste from the box", takeID: pasted.id, { try $0.insert(pasted) }) {
                orb.boxReason = "\(orb.boxReason ?? "pasted") · \(historyNote)"
            }
            lastTake = pasted
            historyVersion += 1
        }
        scheduleBoxClose(after: 3)
    }

    private func bind() {
        // @Published sends before the property changes, so the rebuild runs a moment later and reads
        // the settings themselves. A change made while a take listens is picked up by the next take.
        settings.$engine.combineLatest(settings.$languageCode)
            .dropFirst()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _, _ in
                self?.rebuildEngineIfSettingsChanged()
            }
            .store(in: &cancellables)
        dictationManager.audioPowerLevelPublisher
            .receive(on: DispatchQueue.main)
            .sink { [weak self] level in
                guard let self else { return }
                self.orb.audioLevel = level
                if level > 0.08 { self.lastLoudMomentAt = Date() }
                if self.isTakeInProgress {
                    self.loudestLevelThisTake = max(self.loudestLevelThisTake ?? 0, level)
                    self.levelSamplesThisTake += 1
                }
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
                    self.failTakeThatHeardNoWords(takeID: endedTakeID)
                }
            }
            .store(in: &cancellables)
    }

    /// Builds the engine again when the settings name another engine or language than the one in
    /// use. Never under a take; `beginTake` checks again, so a change made during one is not lost.
    private func rebuildEngineIfSettingsChanged() {
        guard !isTakeInProgress else { return }
        guard settings.engine != currentEngine || settings.languageCode != currentLanguageCode else { return }
        currentEngine = settings.engine
        currentLanguageCode = settings.languageCode
        dictationManager.replaceTranscriptionProvider(DictationEngineResolver.makeProvider(for: settings.engine, settings: settings))
        AppLog.append("dictation engine → \(settings.engine.rawValue), language \(settings.languageCode)")
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
                if var session = liveTyping, session.hasTyped {
                    // Words already went into the field: an edit now would rewrite a selection the
                    // person never meant, so the take stays a dictation — but typing stops here,
                    // since what is said from now on is an instruction and must never be typed.
                    // What was heard before ⌃ and not typed yet goes to the pasteboard at release.
                    markLiveTypingStopped(&session, reason: "too late for an edit", takeID: takeID)
                    session.editKeyJoinedAfterTyping = true
                    liveTyping = session
                    orb.hint = "already typing, stopped here. let go, then start an edit"
                    break
                }
                takeMode = .edit
                // An edit's words are an instruction, not text: nothing of it may be typed.
                stopLiveTyping(reason: "the take became a hey clicky edit", takeID: takeID)
                orb.phase = .editListening
                // With the clean-up off the edit is refused when the key comes up; say so now.
                orb.hint = DictationEngineResolver.wantsModelPolish(settings: settings)
                    ? "say your edit, then let go" : Self.heyClickyNeedsCleanUpMessage
            case .idle, .finishing:
                break
            }
            return true

        case .dictationReleased(let wasTap, let keyWasPressedDuringHold):
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
                if keyWasPressedDuringHold {
                    // The key was held for a shortcut (right ⌘ + C, fn + an arrow), not to talk:
                    // finishing would paste room noise or end in "didn't catch that".
                    cancelTake(reason: "cancelled, that was a shortcut")
                } else if wasTap {
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
        if mode == .edit && !DictationEngineResolver.wantsModelPolish(settings: settings) {
            // An edit is the selection sent to a model; with the clean-up off nothing may leave.
            earcons.play(.blocked)
            orb.phase = .failed(Self.heyClickyNeedsCleanUpMessage)
            scheduleIdle(after: 2.5)
            return
        }
        // An engine or language chosen while the last take listened takes effect now.
        rebuildEngineIfSettingsChanged()
        let thisTakeID = UUID()
        takeID = thisTakeID
        state = .starting
        takeMode = mode
        takeStartedAt = Date()
        takeIsToggle = false
        takeReceivedFinalTranscript = false
        lastLoudMomentAt = Date()
        loudestLevelThisTake = nil
        levelSamplesThisTake = 0
        // The focused element is read now (a few AX calls); the window's captions, which can take
        // hundreds of milliseconds in a browser, are read once the microphone is open. A Chromium
        // browser that was never told an assistive client is here answers with no element at all;
        // told now, it answers by the time the release looks for a text box again.
        host.wakeFrontAppAccessibility()
        fieldAtPress = host.readFocusedField()
        focusedTargetAtPress = host.readFocusedTextTarget()
        nearbyTermsAtPress = []
        polisherForThisTake = host.makePolisher()
        liveTyping = makeLiveTypingSessionIfWanted(mode: mode, focusedTarget: focusedTargetAtPress)
        audioStore.beginCapture()
        boxCloseWork?.cancel()
        dictationManager.updateContextualKeyterms(spaceStore.space.dictionary.map(\.written))
        orb.liveTranscript = ""
        orb.isBoxOpen = settings.orbRestsExpanded && orb.boxText != nil
        orb.phase = mode == .edit ? .editListening : .listening
        orb.hint = mode == .edit ? "say your edit, then let go" : "tap / release to transcribe"
        // An edit works on the selection or the last take, and the tour's practice is never
        // pasted: only a dictation needs somewhere to put its words.
        if mode == .dictate, !holdsTakesOnTheIsland,
           Self.hasNoTextBoxToPasteInto(fieldAtPress, isAccessibilityTrusted: host.isAccessibilityTrusted()) {
            orb.hint = Self.noTextBoxOrbHint
            onNoTextBoxAtPress?(Self.noTextBoxPointerMessage)
            AppLog.append("take \(thisTakeID.uuidString.prefix(8)): no text box at press; the pointer asked for one")
        }
        earcons.play(.start)
        earcons.tap()
        startInactivityTimer()
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) started (\(mode)) in \(fieldAtPress.appName ?? "?") field=\(fieldAtPress.role ?? "none") editable=\(fieldAtPress.isEditable) live=\(liveTyping != nil)")

        pendingStartTask?.cancel()
        pendingStartTask = Task { [weak self] in
            guard let self else { return }
            await self.beforeMicrophoneOpens?()
            guard !Task.isCancelled, self.takeID == thisTakeID, self.state == .starting else { return }
            await self.dictationManager.startPushToTalkFromKeyboardShortcut(
                currentDraftText: "",
                updateDraftText: { [weak self] partial in
                    guard let self, self.takeID == thisTakeID, self.isTakeInProgress else { return }
                    self.handlePartialTranscript(partial, takeID: thisTakeID)
                },
                submitDraftText: { [weak self] final in
                    guard let self, self.takeID == thisTakeID else { return }
                    self.takeReceivedFinalTranscript = true
                    Task { await self.finishTake(rawText: final, takeID: thisTakeID) }
                })
            guard self.takeID == thisTakeID else { return }
            self.pendingStartTask = nil
            if self.state == .starting { self.state = .listening }
            // The start came back without opening a session (microphone denied, a refused engine):
            // nothing will ever end this take, and presses are ignored while it lasts. The manager's
            // own reason usually arrives first through the error stream; this catches the rest.
            if !self.dictationManager.isDictationInProgress, !self.takeReceivedFinalTranscript {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
                    guard let self, self.takeID == thisTakeID, self.isTakeInProgress, !self.takeReceivedFinalTranscript,
                          !self.dictationManager.isDictationInProgress else { return }
                    self.failTake("couldn't open the microphone", takeID: thisTakeID)
                }
                return
            }
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
        // Words already typed live stay in the app (deleting in a terminal is unsafe); history
        // keeps what reached it.
        if let liveTyping, liveTyping.hasTyped {
            logLiveTypedTotal(liveTyping, takeID: thisTakeID)
            if let historyNote = recordInterruptedLiveTypedTake(liveTyping, takeID: thisTakeID, status: .cancelled, reason: reason) {
                orb.phase = .failed("\(reason) · \(historyNote)")
            }
        }
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
        if let liveTyping, liveTyping.hasTyped {
            // The typed words stay in the app; the take is recorded with them, and what was heard
            // but not typed yet goes to the pasteboard, as when typing stops.
            logLiveTypedTotal(liveTyping, takeID: failedTakeID)
            let historyNote = recordInterruptedLiveTypedTake(liveTyping, takeID: failedTakeID, status: .failed, reason: message)
            var failureLine = message
            let heardButUntyped = heardButUntypedText(liveTyping)
            if !heardButUntyped.isEmpty {
                host.copyToPasteboard(heardButUntyped)
                failureLine = "\(message). the rest is copied, press ⌘V"
            }
            if let historyNote { failureLine += " · \(historyNote)" }
            orb.phase = .failed(failureLine)
        } else if !settings.incognito, !message.hasPrefix("didn't catch") {
            let record = makeRecord(
                takeID: failedTakeID, createdAt: takeStartedAt, mode: takeMode == .edit ? .edit : .dictate, status: .failed,
                rawText: orb.liveTranscript, formattedText: "", appBundleID: fieldAtPress.appBundleID, appName: fieldAtPress.appName,
                durationSeconds: Date().timeIntervalSince(takeStartedAt), failureReason: message)
            if let historyNote = writeToHistory("failed take", takeID: failedTakeID, { try $0.insert(record) }) {
                orb.phase = .failed("\(message) · \(historyNote)")
            }
            historyVersion += 1
            // The words are still in the audio: keep it so the take can be heard again once the key,
            // the credits or the network are back. Only a network engine's take is kept (the rule in
            // `TakeAudioStore`'s header): an engine on this Mac has none of those to wait for, and a
            // retry would hear the same audio the same way.
            if settings.retainFailedTakeAudio, !settings.engine.isOnDevice, pcm.count > 16_000 {
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

    /// The engine gave back no words. When the microphone never heard a voice either, the
    /// microphone is the likely cause (a silent input records nothing for Parakeet to read), so the
    /// orb says so; the log keeps the loudest level either way.
    private func failTakeThatHeardNoWords(takeID failedTakeID: UUID) {
        let loudestLevelDescription = loudestLevelThisTake.map { String(format: "%.3f", Double($0)) } ?? "none"
        AppLog.append("take \(failedTakeID.uuidString.prefix(8)) no words (loudest \(loudestLevelDescription), \(levelSamplesThisTake) levels)")
        if let loudestLevelThisTake, levelSamplesThisTake >= 10, loudestLevelThisTake < Self.silentTakeLevel {
            failTake("didn't catch that. check the microphone", takeID: failedTakeID)
        } else {
            failTake("didn't catch that", takeID: failedTakeID)
        }
    }

    private func endTake() {
        state = .idle
        takeIsToggle = false
        liveTyping = nil
        polisherForThisTake = nil
        afterMicrophoneCloses?()
    }

    /// Still this take, still wanted: false once it was cancelled or another take began.
    private func isCurrent(_ id: UUID) -> Bool {
        takeID == id && state == .finishing
    }

    private func finishTake(rawText: String, takeID thisTakeID: UUID) async {
        guard takeID == thisTakeID, state == .listening || state == .starting || state == .finishing else { return }
        state = .finishing
        // The live session closes here: a partial that lands from now on is never typed, so
        // nothing can be typed during finishing and then typed or pasted again.
        let liveTypingAtFinish = liveTyping
        liveTyping = nil
        _ = audioStore.endCapture()
        var raw = rawText.trimmingCharacters(in: .whitespacesAndNewlines)
        if raw.isEmpty, let liveTyping = liveTypingAtFinish, liveTyping.hasTyped {
            // The engine's final came back empty but words were already typed: they are the take.
            raw = liveTyping.typedRawWords.joined(separator: " ")
        }
        guard !raw.isEmpty else {
            failTakeThatHeardNoWords(takeID: thisTakeID)
            return
        }
        // Words from a take the microphone never heard a voice in are the engine's guess at
        // silence, not what was said: nothing is pasted.
        if let loudestLevelThisTake, levelSamplesThisTake >= 10, loudestLevelThisTake < Self.silentTakeLevel, liveTypingAtFinish?.hasTyped != true {
            AppLog.append("take \(thisTakeID.uuidString.prefix(8)) heard only silence (loudest \(String(format: "%.3f", Double(loudestLevelThisTake)))); dropped \(raw.count) characters")
            failTake("didn't catch that. check the microphone", takeID: thisTakeID)
            return
        }
        let duration = Date().timeIntervalSince(takeStartedAt)
        let context = makeFormattingContext(appBundleID: fieldAtPress.appBundleID, appName: fieldAtPress.appName, nearbyTerms: nearbyTermsAtPress)
        // Read into locals: a new take can begin while a model is at work below.
        let polisher = polisherForThisTake
        let mode = takeMode
        let field = fieldAtPress

        if mode == .dictate, let liveTyping = liveTypingAtFinish, liveTyping.hasTyped {
            finishLiveTypedTake(rawText: raw, session: liveTyping, duration: duration, takeID: thisTakeID)
            return
        }

        let outputText: String
        var formattingDegraded = false
        if mode == .edit {
            guard let editedText = await applyHeyClickyEdit(instruction: raw, field: field, context: context, polisher: polisher, takeID: thisTakeID) else { return }
            outputText = editedText
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

        let delivery = await deliverWhereTheTakeBegan(outputText, field: field)
        let pasteOutcome = delivery.outcome
        var pasteMessage = delivery.message
        guard takeID == thisTakeID else { return }
        // Words with nowhere to go are put on the clipboard and the orb says so in one line; the
        // big box only opens when the person asked for it ("rest with the box open").
        if pasteOutcome == .leftOnPasteboard { host.copyToPasteboard(outputText) }

        let record = makeRecord(
            takeID: thisTakeID, createdAt: takeStartedAt, mode: mode == .edit ? .edit : .dictate, status: .complete,
            rawText: raw, formattedText: outputText, appBundleID: field.appBundleID, appName: field.appName,
            durationSeconds: duration, pasteOutcome: pasteOutcome)
        lastTake = record
        var historyNote: String?
        if !settings.incognito {
            historyNote = writeToHistory("take", takeID: thisTakeID, { try $0.insert(record) })
            historyVersion += 1
        }
        onTakeFinished?(record)

        orb.boxText = outputText
        orb.liveTranscript = ""
        let showBox = (pasteOutcome == .posted && settings.orbOpensBoxWhenPasteUnverified) || settings.orbRestsExpanded
        orb.boxReason = pasteOutcome == .verified || pasteOutcome == .posted ? "your last take" : pasteMessage
        orb.quickRewrites = quickRewriteLabels(polisher: polisher)
        orb.isBoxOpen = showBox
        scheduleBoxClose()
        earcons.play(pasteOutcome == .verified || pasteOutcome == .posted ? .complete : .notify)
        earcons.tap()
        if formattingDegraded, pasteOutcome == .verified || pasteOutcome == .posted { pasteMessage = "moved to text box · cleaned up locally" }
        if let historyNote { pasteMessage += " · \(historyNote)" }
        orb.phase = .done(pasteMessage)
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) done: \(outputText.count) chars, paste=\(pasteOutcome.rawValue), engine=\(settings.engine.rawValue), degraded=\(formattingDegraded)")
        endTake()
        scheduleIdle(after: 1.8)
    }

    /// A Hey Clicky take's words are an instruction for the selection (or the last take): the model
    /// rewrites that text. Nil when the edit could not be made; the take has already failed then,
    /// with the reason on the orb.
    private func applyHeyClickyEdit(instruction: String, field: FocusedFieldSnapshot, context: TakeFormattingContext,
                                    polisher: (any TakePolisher)?, takeID thisTakeID: UUID) async -> String? {
        orb.phase = .working("finishing your edit")
        // The selection would go to a model: only with "clean up my words with ai" on, the
        // switch the privacy page names as what keeps words on this mac.
        guard DictationEngineResolver.wantsModelPolish(settings: settings) else {
            failTake(Self.heyClickyNeedsCleanUpMessage, takeID: thisTakeID)
            return nil
        }
        guard let polisher else {
            failTake("hey clicky needs a model: \(DictationEngineResolver.howToGetAModelSentence)", takeID: thisTakeID)
            return nil
        }
        let selection = field.selectedText ?? lastTake?.displayText ?? ""
        guard !selection.isEmpty else {
            failTake("select some text first, or dictate something to edit", takeID: thisTakeID)
            return nil
        }
        do {
            return try await HeyClickyEditor.edit(selection: selection, instruction: instruction, context: context, polisher: polisher)
        } catch {
            if isCurrent(thisTakeID) {
                failTake("couldn't finish your edit: \(error.localizedDescription)", takeID: thisTakeID)
            }
            return nil
        }
    }

    /// Where a finished take's words go, and the orb's line about it. They are pasted where the
    /// cursor was when the key went down — and only there: never into a password field, another
    /// app, or another element of the same app; otherwise they are left on the pasteboard (the
    /// caller puts them there) or, for the tour's practice and a password field, only in the orb.
    /// The one exception: a take that began outside any text box goes to the text box focused now.
    private func deliverWhereTheTakeBegan(_ outputText: String, field fieldAtPress: FocusedFieldSnapshot) async -> (outcome: TakeRecord.PasteOutcome, message: String) {
        let pastedMessage = "moved to text box"
        if holdsTakesOnTheIsland {
            // The tour's practice take: shown on the island, never typed into the app in front
            // (a new user's first words went into their terminal).
            return (.leftInOrb, "practice take, kept here")
        }
        // The take began outside any text box and the pointer asked for one: a text box focused
        // now is the one the person clicked into for these words, so they go there. Nothing at
        // press was a place to protect. Every check below runs on that field instead.
        var field = fieldAtPress
        var followsTheTextBoxClickedDuringTheTake = false
        if Self.hasNoTextBoxToPasteInto(fieldAtPress, isAccessibilityTrusted: host.isAccessibilityTrusted()) {
            var fieldNow = host.readFocusedField()
            if !fieldNow.isEditable, !fieldNow.isSecure, secondsToWaitForATextBoxAfterRelease > 0 {
                // Still nowhere to put them: the words wait a few seconds for a click into a box.
                let deliveringTakeID = takeID
                orb.phase = .working(Self.noTextBoxOrbHint)
                AppLog.append("take \(deliveringTakeID.uuidString.prefix(8)): no text box at release; waiting \(Int(secondsToWaitForATextBoxAfterRelease)) s for one")
                let deadline = Date().addingTimeInterval(secondsToWaitForATextBoxAfterRelease)
                while Date() < deadline, takeID == deliveringTakeID {
                    try? await Task.sleep(for: .milliseconds(200))
                    fieldNow = host.readFocusedField()
                    if fieldNow.isEditable || fieldNow.isSecure { break }
                }
            }
            if fieldNow.isEditable || fieldNow.isSecure {
                field = fieldNow
                followsTheTextBoxClickedDuringTheTake = true
                AppLog.append("take \(takeID.uuidString.prefix(8)): no text box at press; pasting into the \(fieldNow.role ?? "?") clicked since, in \(fieldNow.appName ?? "?")")
            }
        }
        if field.isSecure || host.isSecureInputOn() {
            // Not copied either: a password should not sit on the clipboard (it can sync to other
            // devices). It stays in history only.
            return (.leftInOrb, "not pasting into a password field")
        }
        if let appAtPress = field.appBundleID, let appInFrontNow = host.frontAppBundleID(), appAtPress != appInFrontNow {
            return (.leftOnPasteboard, "the app changed. copied, press ⌘V")
        }
        if !followsTheTextBoxClickedDuringTheTake, focusMovedSincePress() {
            return (.leftOnPasteboard, "the focus moved. copied, press ⌘V")
        }
        // Editable, or unknown (no permission / the app exposes nothing): try the paste.
        guard field.isEditable || !host.isAccessibilityTrusted() || field.role == nil else {
            return (.leftOnPasteboard, "no text box. copied, press ⌘V")
        }
        switch host.paste(outputText) {
        case .leftOnPasteboard:
            return (.leftOnPasteboard, "copied, press ⌘V (allow accessibility to paste for you)")
        case .typed:
            switch await host.verifyPaste(outputText, field) {
            case .verified: return (.verified, pastedMessage)
            case .posted: return (.posted, pastedMessage)
            case .noTarget: return (.leftOnPasteboard, "couldn't see it land. copied, press ⌘V")
            case .leftOnPasteboard: return (.leftOnPasteboard, pastedMessage)
            }
        }
    }

    // MARK: a take's context, record and rewrite chips

    /// The formatting context for words going into an app: its style, the dictionary, the spoken
    /// shortcuts, the language and script; captions read near the cursor when there are any.
    private func makeFormattingContext(appBundleID: String?, appName: String?, nearbyTerms: [String] = []) -> TakeFormattingContext {
        let space = spaceStore.space
        return TakeFormattingContext(
            style: space.style(forAppBundleID: appBundleID), dictionary: space.dictionary, shortcuts: space.shortcuts,
            appName: appName, language: settings.language, script: settings.script, nearbyTerms: nearbyTerms)
    }

    /// A history row, stamped with the language and engine the settings name, so every kind of
    /// take (finished, failed, live-typed, interrupted, retried) records them the same way.
    private func makeRecord(takeID recordTakeID: UUID, createdAt: Date, mode: TakeRecord.Mode, status: TakeRecord.Status,
                            rawText: String, formattedText: String, appBundleID: String?, appName: String?,
                            durationSeconds: Double, pasteOutcome: TakeRecord.PasteOutcome = .none, failureReason: String? = nil) -> TakeRecord {
        TakeRecord(id: recordTakeID, createdAt: createdAt, mode: mode, status: status,
                   rawText: rawText, formattedText: formattedText, appBundleID: appBundleID, appName: appName,
                   language: settings.language.bareCode, engine: settings.engine.rawValue,
                   durationSeconds: durationSeconds, pasteOutcome: pasteOutcome, failureReason: failureReason)
    }

    /// The rewrite chips under the box. They only show when the AI clean-up is switched on (off
    /// means nothing goes online) and there is a model to rewrite with.
    private func quickRewriteLabels(polisher: (any TakePolisher)?) -> [String] {
        guard polisher != nil, DictationEngineResolver.wantsModelPolish(settings: settings) else { return [] }
        return Self.quickRewrites.map(\.label)
    }

    // MARK: live typing

    /// A take's words are typed as they settle only when: it is a dictation (not a Hey Clicky
    /// edit), no model will polish it (the same decision `TakeFormatter.format` makes), the focused
    /// element is a real text field (not a web page, where letters are shortcuts) and not a
    /// password field, it is not a terminal or OpenClicky's own window (`LiveTypingExclusions`),
    /// and secure input is off.
    private func makeLiveTypingSessionIfWanted(mode: TakeMode, focusedTarget: FocusedTextTarget?) -> LiveTypingSession? {
        guard mode == .dictate, !holdsTakesOnTheIsland, !fieldAtPress.isSecure, !host.isSecureInputOn(),
              let focusedTarget, focusedTarget.acceptsLiveTyping else { return nil }
        if let reason = LiveTypingExclusions.reasonNotToType(into: focusedTarget, fieldBundleID: fieldAtPress.appBundleID) {
            AppLog.append("take \(takeID.uuidString.prefix(8)) not typed live: \(reason); the words paste at release")
            return nil
        }
        let space = spaceStore.space
        let context = makeFormattingContext(appBundleID: fieldAtPress.appBundleID, appName: fieldAtPress.appName)
        let modelWillPolish = TakeFormatter.modelPassWillRun(
            context: context, polisher: polisherForThisTake, wantsModel: DictationEngineResolver.wantsModelPolish(settings: settings))
        guard !modelWillPolish else { return nil }
        let splitIntoNormalizedWords: (String) -> [String] = { phrase in
            TakeFormatter.normalizedForMatching(phrase).split(separator: " ").map(String.init)
        }
        let dictionaryAliases = space.dictionary.flatMap { [$0.written] + $0.heardAs }.map(splitIntoNormalizedWords)
        let shortcutTriggers = space.shortcuts.map { splitIntoNormalizedWords($0.trigger) }
        return LiveTypingSession(
            committer: StableWordCommitter(protectedPhrases: dictionaryAliases, takeStartTriggers: shortcutTriggers, timing: liveTypingTiming),
            focusedTargetAtPress: focusedTarget, formattingContext: context)
    }

    /// Another element of the same app has focus than at press (the search field instead of the
    /// message box): the paste would land there. Only a confident "different": when either read
    /// fails (no permission, an app that answers nothing) the paste goes ahead as before.
    private func focusMovedSincePress() -> Bool {
        guard let targetAtPress = focusedTargetAtPress, let targetNow = host.readFocusedTextTarget() else { return false }
        return !targetNow.isSameElement(as: targetAtPress)
    }

    /// A partial transcript: settled words are typed (when this take types live and the key is
    /// still held or the tapped take still listens) and the orb shows what has not been typed yet.
    private func handlePartialTranscript(_ partial: String, takeID thisTakeID: UUID) {
        guard var session = liveTyping else {
            orb.liveTranscript = partial
            return
        }
        // After a late ⌃ the words are an edit instruction: the take keeps what was heard before it.
        if !session.editKeyJoinedAfterTyping { session.latestPartial = partial }
        let takeIsStillListening = state == .starting || state == .listening
        if session.stoppedReason == nil, takeIsStillListening {
            commitAndTypeSettledWords(of: partial, personPaused: false, session: &session, takeID: thisTakeID)
        }
        liveTyping = session
        if session.stoppedReason == nil, takeIsStillListening { scheduleLiveTypingPauseCheck(after: partial, takeID: thisTakeID) }
        // The words are already appearing in the app: the orb shows only the listening pill. Once
        // typing has stopped (focus moved, a held key), the untyped words show so nothing is lost.
        orb.liveTranscript = session.stoppedReason == nil ? "" : untypedWords(of: session.latestPartial, session: session).joined(separator: " ")
    }

    /// When the person stops talking no new partial comes, so the last word would wait for the
    /// key's release. After a pause with the partial unchanged, it is typed too.
    private func scheduleLiveTypingPauseCheck(after partial: String, takeID thisTakeID: UUID) {
        liveTypingPauseTask?.cancel()
        let pauseNanoseconds = UInt64(max(0, liveTypingTiming.pauseSeconds) * 1_000_000_000)
        liveTypingPauseTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: pauseNanoseconds)
            guard !Task.isCancelled, let self, self.takeID == thisTakeID,
                  self.state == .starting || self.state == .listening,
                  var session = self.liveTyping, session.stoppedReason == nil, session.latestPartial == partial else { return }
            self.commitAndTypeSettledWords(of: partial, personPaused: true, session: &session, takeID: thisTakeID)
            self.liveTyping = session
        }
    }

    /// Shows the partial to the committer, then types every word that is committed but not typed
    /// yet: the words that settled now, plus any that waited for a held modifier to be let go.
    private func commitAndTypeSettledWords(of partial: String, personPaused: Bool, session: inout LiveTypingSession, takeID thisTakeID: UUID) {
        _ = session.committer.observe(partial: partial, at: host.now(), personPaused: personPaused)
        let committedButUntypedWords = Array(session.committer.committedWords.dropFirst(session.typedRawWords.count))
        if !committedButUntypedWords.isEmpty {
            typeLivePhrase(committedButUntypedWords, session: &session, takeID: thisTakeID)
        }
    }

    private func untypedWords(of transcript: String, session: LiveTypingSession) -> [String] {
        let transcriptWords = StableWordCommitter.words(of: transcript)
        return TranscriptAlignment.align(finalWords: transcriptWords, committedWords: session.typedRawWords).remainingWords(of: transcriptWords)
    }

    /// What the take heard that never reached the field, formatted, for the pasteboard.
    private func heardButUntypedText(_ session: LiveTypingSession) -> String {
        LivePhraseFormatter.textToType(
            phraseWords: untypedWords(of: session.latestPartial, session: session), context: session.formattingContext, typedSoFar: session.typedText)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Why typing must stop, checked before every typing step: keystrokes go to whatever has
    /// focus, so it must still be the very element the take began in (Accessibility's element
    /// and pid — NSWorkspace's frontmost app lags right after ⌘-Tab), still a text field, with
    /// secure input off.
    private func liveTypingStopReason(_ session: LiveTypingSession) -> String? {
        guard let focusedNow = host.readFocusedTextTarget() else { return "the text field lost focus" }
        if focusedNow.processID != session.focusedTargetAtPress.processID { return "the app changed" }
        if !focusedNow.isSameElement(as: session.focusedTargetAtPress) || !focusedNow.acceptsLiveTyping { return "the focus moved" }
        if host.isSecureInputOn() { return "secure input came on" }
        return nil
    }

    enum LivePhraseTypingResult { case typed, deferredForHeldModifier, stopped }

    /// Formats and types one phrase. A phrase is only ever typed whole: when typing stops, or
    /// waits for a held modifier, none of it was posted.
    @discardableResult
    private func typeLivePhrase(_ phraseWords: [String], session: inout LiveTypingSession, takeID thisTakeID: UUID) -> LivePhraseTypingResult {
        if let reason = liveTypingStopReason(session) {
            markLiveTypingStopped(&session, reason: reason, takeID: thisTakeID)
            return .stopped
        }
        guard LiveTypingModifierGuard.allowsTyping(heldFlags: host.heldModifierFlags(), dictationKey: settings.dictationKey) else {
            // Typed now, the letters could merge with the held key (⌃M is Return). They stay
            // pending and go with the next phrase, or at release.
            return .deferredForHeldModifier
        }
        let text = LivePhraseFormatter.textToType(phraseWords: phraseWords, context: session.formattingContext, typedSoFar: session.typedText)
        if LivePhraseFormatter.containsControlCharacters(text) {
            markLiveTypingStopped(&session, reason: "a line break can't be typed safely", takeID: thisTakeID)
            return .stopped
        }
        if !text.isEmpty {
            guard host.typeText(text) else {
                markLiveTypingStopped(&session, reason: "keystrokes could not be posted", takeID: thisTakeID)
                return .stopped
            }
            session.typedText += text
            session.typedPhraseCount += 1
        }
        session.typedRawWords.append(contentsOf: phraseWords)
        return .typed
    }

    private func markLiveTypingStopped(_ session: inout LiveTypingSession, reason: String, takeID thisTakeID: UUID) {
        guard session.stoppedReason == nil else { return }
        session.stoppedReason = reason
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) live typing stopped: \(reason)")
    }

    private func stopLiveTyping(reason: String, takeID thisTakeID: UUID) {
        guard var session = liveTyping else { return }
        markLiveTypingStopped(&session, reason: reason, takeID: thisTakeID)
        liveTyping = session
    }

    private func logLiveTypedTotal(_ session: LiveTypingSession, takeID thisTakeID: UUID) {
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) live-typed \(session.typedText.count) chars in \(session.typedPhraseCount) phrases")
    }

    /// The key came up on a take that was typed as it was spoken: only the words of the final
    /// transcript that were not typed yet are typed now. Never retyped, never deleted.
    private func finishLiveTypedTake(rawText finalTranscript: String, session startingSession: LiveTypingSession,
                                     duration: TimeInterval, takeID thisTakeID: UUID) {
        var session = startingSession
        // ⌃ joined after words were typed: the final ends in the spoken edit instruction, which is
        // neither typed nor copied; the take is what was heard up to ⌃.
        let raw = session.editKeyJoinedAfterTyping ? session.latestPartial : finalTranscript
        let finalWords = StableWordCommitter.words(of: raw)
        let alignment = TranscriptAlignment.align(finalWords: finalWords, committedWords: session.typedRawWords)
        if !alignment.alignedByWords {
            // Where the typed words end in the final cannot be told: typing on could skip or
            // repeat words, so everything after the last match goes to the pasteboard instead.
            markLiveTypingStopped(&session, reason: "the words were heard differently", takeID: thisTakeID)
        }
        let remainingWords = alignment.remainingWords(of: finalWords)
        if session.stoppedReason == nil, !remainingWords.isEmpty {
            // The dictation key is up by now; a modifier still held is someone else's.
            if typeLivePhrase(remainingWords, session: &session, takeID: thisTakeID) == .deferredForHeldModifier {
                markLiveTypingStopped(&session, reason: "a modifier key was held", takeID: thisTakeID)
            }
        }

        var pasteOutcome = TakeRecord.PasteOutcome.posted
        var message = "typed as you spoke"
        var untypedText = ""
        if let reason = session.stoppedReason {
            // Typing stopped before the end: the rest waits on the pasteboard for the person's ⌘V.
            untypedText = LivePhraseFormatter.textToType(
                phraseWords: remainingWords, context: session.formattingContext, typedSoFar: session.typedText)
            let untypedForPasteboard = untypedText.trimmingCharacters(in: .whitespacesAndNewlines)
            if !untypedForPasteboard.isEmpty {
                host.copyToPasteboard(untypedForPasteboard)
                pasteOutcome = .leftOnPasteboard
                message = "\(reason). the rest is copied, press ⌘V"
            }
        }
        logLiveTypedTotal(session, takeID: thisTakeID)

        // What reached the app, plus (when typing had to stop) the words left on the pasteboard.
        let formattedText = session.typedText + untypedText
        let record = makeRecord(
            takeID: thisTakeID, createdAt: takeStartedAt, mode: .dictate, status: .complete,
            rawText: raw, formattedText: formattedText, appBundleID: fieldAtPress.appBundleID, appName: fieldAtPress.appName,
            durationSeconds: duration, pasteOutcome: pasteOutcome)
        lastTake = record
        if !settings.incognito {
            if let historyNote = writeToHistory("live-typed take", takeID: thisTakeID, { try $0.insert(record) }) {
                message += " · \(historyNote)"
            }
            historyVersion += 1
        }
        onTakeFinished?(record)

        let untypedForBox = untypedText.trimmingCharacters(in: .whitespacesAndNewlines)
        orb.boxText = untypedForBox.isEmpty ? session.typedText : untypedForBox
        orb.liveTranscript = ""
        orb.boxReason = pasteOutcome == .posted ? "your last take" : message
        orb.quickRewrites = quickRewriteLabels(polisher: polisherForThisTake)
        // The rest is on the clipboard and the orb says so; the box opens only when asked for.
        orb.isBoxOpen = settings.orbRestsExpanded
        scheduleBoxClose()
        earcons.play(pasteOutcome == .posted ? .complete : .notify)
        earcons.tap()
        orb.phase = .done(message)
        AppLog.append("take \(thisTakeID.uuidString.prefix(8)) done: \(formattedText.count) chars, paste=\(pasteOutcome.rawValue), engine=\(settings.engine.rawValue), live")
        endTake()
        scheduleIdle(after: 1.8)
    }

    /// A cancelled or failed take whose first words were already typed: history keeps them.
    /// Returns the orb's note when that write failed (see `writeToHistory`).
    private func recordInterruptedLiveTypedTake(_ session: LiveTypingSession, takeID thisTakeID: UUID, status: TakeRecord.Status, reason: String) -> String? {
        guard !settings.incognito else { return nil }
        let record = makeRecord(
            takeID: thisTakeID, createdAt: takeStartedAt, mode: .dictate, status: status,
            rawText: session.typedRawWords.joined(separator: " "), formattedText: session.typedText,
            appBundleID: fieldAtPress.appBundleID, appName: fieldAtPress.appName,
            durationSeconds: Date().timeIntervalSince(takeStartedAt), pasteOutcome: .posted,
            failureReason: "\(reason) · words typed before it stay")
        let historyNote = writeToHistory("interrupted take", takeID: thisTakeID, { try $0.insert(record) })
        historyVersion += 1
        return historyNote
    }

    /// Writes to history. A failure (the file locked by another OpenClicky past the busy timeout,
    /// a full disk, a damaged database) is logged every time; the first one this run also returns
    /// the words the orb adds to its line, so a take missing from history is never a surprise.
    /// Without a store at all (it could not be opened) the History page says so instead.
    private func writeToHistory(_ whatIsWritten: String, takeID writtenTakeID: UUID, _ write: (TakeStore) throws -> Void) -> String? {
        guard let takeStore else { return nil }
        do {
            try write(takeStore)
            return nil
        } catch {
            AppLog.append("take \(writtenTakeID.uuidString.prefix(8)) not saved to history (\(whatIsWritten)): \(error.localizedDescription)")
            guard !hasShownHistoryWriteFailure else { return nil }
            hasShownHistoryWriteFailure = true
            return "not saved to history"
        }
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
            return "asking needs a model: \(DictationEngineResolver.howToGetAModelSentence)."
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
            let context = makeFormattingContext(appBundleID: original?.appBundleID, appName: original?.appName)
            let formatted = await TakeFormatter.format(raw, context: context, polisher: host.makePolisher(), wantsModel: DictationEngineResolver.wantsModelPolish(settings: settings))
            let record = makeRecord(
                takeID: takeID, createdAt: original?.createdAt ?? Date(), mode: .dictate, status: .complete,
                rawText: raw, formattedText: formatted.text, appBundleID: original?.appBundleID, appName: original?.appName,
                durationSeconds: original?.durationSeconds ?? 0, pasteOutcome: .leftInOrb)
            var historyNote: String?
            if !settings.incognito { historyNote = writeToHistory("retried take", takeID: takeID, { try $0.insert(record) }) }
            historyVersion += 1
            lastTake = record
            audioStore.discard(takeID: takeID)
            if retryableTakeID == takeID { retryableTakeID = nil }
            orb.boxText = formatted.text
            orb.boxReason = "retried · copy from here"
            orb.isBoxOpen = true
            scheduleBoxClose()
            earcons.play(.complete)
            orb.phase = .done("retried · copy from the box" + (historyNote.map { " · \($0)" } ?? ""))
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
        // Any model can make the edit, so the error names the one that did, not Sarvam's.
        guard !cleaned.isEmpty else { throw TakePolishError.emptyAnswer(vendor: polisher.displayName) }
        return cleaned
    }
}
