//
//  FirstRunTour.swift
//  OpenClicky
//
//  The first-run tour, told entirely from the island. Once the permission cards are done the
//  pointer flies out of the notch to the mouse and introduces itself; then one card at a time
//  shows the keys (animated, pressed and held) for each thing OpenClicky does (dictate, ask, hand
//  over a task) and moves on only when it has seen that thing work: a take with words, a spoken
//  answer, an agent task or quick action. Speech recognition is asked for before the first take.
//  Nobody types anything to start: a Mac with no key and no account gets a guest account on
//  OpenClicky's credits (no email), and only at the end may the person add an email, or skip it.
//  Before asking, Clicky shows how: it points at something on the screen and says a few words.
//  Each card's line is narrated from a bundled recording (RecordedVoice). The step is kept in
//  UserDefaults, so the Screen Recording relaunch picks the tour up where it was.
//

import AppKit
import Combine
import Speech
import SwiftUI

enum FirstRunTourStep: String {
    case meet, dictate, talk, act, account, done
    /// Not part of the tour: asked once, after a few uses (`noteUse`).
    case openAtLogin

    /// The three practice steps get a progress dash each.
    var practiceIndex: Int? {
        switch self {
        case .dictate: return 0
        case .talk: return 1
        case .act: return 2
        case .meet, .account, .done, .openAtLogin: return nil
        }
    }
}

/// What the island's tour card shows.
struct FirstRunTourCard: Equatable {
    enum FieldKind: Equatable { case email, code }

    var step: FirstRunTourStep
    var title: String
    var detail: String
    /// Keycaps drawn pressed and held, or an SF Symbol when there are no keys to show.
    var keys: [String] = []
    var symbol: String?
    /// A line under the copy: what was heard, or why it did not work.
    var status: String?
    var statusIsGood = false
    /// The dictate step names the microphone it listens through, with a way to change it; it is
    /// drawn louder once that microphone seems to hear nothing.
    var microphoneName: String?
    var microphoneNeedsAttention = false
    /// The account step's field on the island: an email, then a code if the email has an account.
    var field: FieldKind?
    var primaryTitle: String?
    var secondaryTitle: String
}

@MainActor
final class FirstRunTourController: ObservableObject {
    @Published private(set) var card: FirstRunTourCard?
    /// What is typed in the account step's field.
    @Published var fieldText = ""

    private static let stepKey = "openclicky.firstRunTour.step"
    private static let doneKey = "openclicky.firstRunTour.done"

    fileprivate weak var companionManager: CompanionManager?
    fileprivate var step: FirstRunTourStep?
    fileprivate var status: String?
    fileprivate var statusIsGood = false
    fileprivate var offersMicrophoneChange = false
    private var permissionTimer: Timer?
    private var advanceWorkItem: DispatchWorkItem?
    private var cancellables = Set<AnyCancellable>()
    private var previousVoiceState: CompanionVoiceState = .idle

    /// The tour has been completed (or skipped, or predates this Mac's first run).
    var isFinished: Bool { UserDefaults.standard.bool(forKey: Self.doneKey) }

    // MARK: Starting

    /// Called once at launch, after the island and the permission cards are up.
    func start(companionManager: CompanionManager) {
        self.companionManager = companionManager
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: Self.doneKey) { return }
        let storedStep = defaults.string(forKey: Self.stepKey).flatMap(FirstRunTourStep.init(rawValue:))
        // Someone who used OpenClicky before the tour existed has been through the old welcome.
        if storedStep == nil && (companionManager.dictationSettings.hasCompletedDictationOnboarding || companionManager.hasCompletedOnboarding) {
            defaults.set(true, forKey: Self.doneKey)
            return
        }
        observe(companionManager)
        waitForPermissions(thenStartAt: storedStep ?? .meet)
    }

    /// The menu bar's "Replay Welcome…" and Settings → General: the whole tour again.
    func replay() {
        guard let companionManager else { return }
        UserDefaults.standard.set(false, forKey: Self.doneKey)
        if cancellables.isEmpty { observe(companionManager) }
        waitForPermissions(thenStartAt: .meet)
    }

    private func waitForPermissions(thenStartAt firstStep: FirstRunTourStep) {
        permissionTimer?.invalidate()
        if companionManager?.allPermissionsGranted == true {
            go(to: firstStep)
            return
        }
        // The permission cards own the island until every permission is in.
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
            Task { @MainActor in
                guard let self, self.companionManager?.allPermissionsGranted == true else { return }
                timer.invalidate()
                self.go(to: firstStep)
            }
        }
    }

    // MARK: What the tour watches for

    private func observe(_ companionManager: CompanionManager) {
        cancellables.removeAll()
        companionManager.dictationTakeController.$lastTake
            .dropFirst()
            .compactMap { $0 }
            .sink { [weak self] take in self?.takeFinished(take) }
            .store(in: &cancellables)
        companionManager.$voiceState
            .removeDuplicates()
            .sink { [weak self] voiceState in self?.voiceStateChanged(voiceState) }
            .store(in: &cancellables)
        companionManager.$agentActivityText
            .compactMap { $0 }
            .sink { [weak self] _ in self?.taskStarted() }
            .store(in: &cancellables)
        companionManager.$lastSuccessfulQuickActionAt
            .compactMap { $0 }
            .sink { [weak self] _ in self?.taskStarted() }
            .store(in: &cancellables)
        // A take that listens for a while and hears nothing: most likely the wrong microphone.
        companionManager.dictationTakeController.$state
            .removeDuplicates()
            .sink { [weak self] takeState in self?.takeStateChanged(takeState) }
            .store(in: &cancellables)
        // A take that failed ("didn't catch that") never becomes a lastTake; the orb says it.
        companionManager.orbModel.$phase
            .sink { [weak self] phase in
                guard let self, self.step == .dictate, case .failed = phase else { return }
                DispatchQueue.main.async { self.couldNotHear() }
            }
            .store(in: &cancellables)
        companionManager.orbModel.$audioLevel
            .sink { [weak self] level in
                guard let self, self.isWatchingForSilence else { return }
                self.loudestLevelThisTake = max(self.loudestLevelThisTake, level)
            }
            .store(in: &cancellables)
        companionManager.dictationSettings.$preferredMicrophoneUID
            .dropFirst()
            .sink { [weak self] _ in DispatchQueue.main.async { self?.publish() } }
            .store(in: &cancellables)
        companionManager.dictationSettings.$dictationKey
            .dropFirst()
            .sink { [weak self] _ in DispatchQueue.main.async { self?.publish() } }
            .store(in: &cancellables)
        OpenClickyAuthSession.shared.$emailFlow
            .removeDuplicates()
            .sink { [weak self] flow in DispatchQueue.main.async { self?.emailFlowChanged(flow) } }
            .store(in: &cancellables)
        // Whether new accounts are taken decides if the account step is offered at all.
        Task { await OpenClickyAuthSession.shared.refreshAccountsOpen() }
    }

    private func takeFinished(_ take: TakeRecord) {
        guard step == .dictate else { return }
        let words = take.displayText.trimmingCharacters(in: .whitespacesAndNewlines)
        let wordCount = words.split(whereSeparator: { $0.isWhitespace }).count
        // A real first take: a sentence (one word like "no" is what silence turns into) from a
        // microphone that heard a voice.
        if take.status == .complete && wordCount >= 2 && loudestLevelThisTake >= Self.voiceLevel {
            succeed(with: "✓ heard you: “\(words.prefix(60))\(words.count > 60 ? "…" : "")”", saying: .tourDictateDone)
        } else if take.status == .complete && wordCount < 2 && loudestLevelThisTake >= Self.voiceLevel {
            status = "i only got “\(words)”. say a whole sentence, like the one above."
            statusIsGood = false
            offersMicrophoneChange = false
            publish()
        } else {
            couldNotHear()
        }
    }

    /// A take ended with nothing usable: most often the wrong microphone.
    private func couldNotHear() {
        guard let companionManager else { return }
        // The first press opened macOS's speech recognition question; the take under it was never
        // going to hear anything, and the microphone is not to blame.
        if speechWasUndeterminedAtTakeStart {
            speechWasUndeterminedAtTakeStart = false
            status = speechNeedsAllowing ? nil : "speech recognition is on. hold \(companionManager.dictationSettings.dictationKey.keycapLabel) and try again."
            statusIsGood = false
            offersMicrophoneChange = false
            publish()
            return
        }
        let name = MicrophoneMenu.deviceName(settings: companionManager.dictationSettings)
        status = "i couldn’t hear you through \(name). choose the microphone you’re using."
        statusIsGood = false
        offersMicrophoneChange = true
        publish()
    }

    /// The built-in microphone is there and is not the one listening: offer it in one click.
    private var suggestsBuiltInMicrophone: Bool {
        offersMicrophoneChange && MicrophoneDevices.builtInMicrophoneIsAnAlternative(
            preferredUID: companionManager?.dictationSettings.preferredMicrophoneUID)
    }

    /// The offline engine needs Speech Recognition, which macOS asks for on first use.
    private var speechNeedsAllowing: Bool {
        companionManager?.dictationSettings.engine == .offline && SFSpeechRecognizer.authorizationStatus() == .notDetermined
    }
    private var speechWasUndeterminedAtTakeStart = false

    private var isWatchingForSilence = false
    private var loudestLevelThisTake: CGFloat = 0
    private var silenceCheckWorkItem: DispatchWorkItem?

    /// Below this the orb's level is room noise, not a voice.
    private static let voiceLevel: CGFloat = 0.05

    private func takeStateChanged(_ takeState: DictationTakeController.TakeState) {
        guard step == .dictate else { return }
        if takeState == .listening {
            companionManager?.recordedVoice.stop()
            speechWasUndeterminedAtTakeStart = speechNeedsAllowing
            isWatchingForSilence = true
            loudestLevelThisTake = 0
            silenceCheckWorkItem?.cancel()
            let check = DispatchWorkItem { [weak self] in self?.checkForSilence() }
            silenceCheckWorkItem = check
            DispatchQueue.main.asyncAfter(deadline: .now() + 4, execute: check)
        } else {
            isWatchingForSilence = false
            silenceCheckWorkItem?.cancel()
        }
    }

    private func checkForSilence() {
        guard step == .dictate, isWatchingForSilence, loudestLevelThisTake < Self.voiceLevel,
              !speechWasUndeterminedAtTakeStart, let companionManager else { return }
        let name = MicrophoneMenu.deviceName(settings: companionManager.dictationSettings)
        status = "i can’t hear anything through \(name). choose the microphone you’re using."
        statusIsGood = false
        offersMicrophoneChange = true
        publish()
    }

    private func voiceStateChanged(_ voiceState: CompanionVoiceState) {
        defer { previousVoiceState = voiceState }
        if voiceState == .listening { companionManager?.recordedVoice.stop() }
        // An answer was spoken and finished: that is asking Clicky, working.
        if step == .talk && previousVoiceState == .responding && voiceState == .idle && companionManager?.lastQuestionFailed != true {
            succeed(with: "✓ that’s asking clicky. it can point at things too.", saying: .tourTalkDone)
        }
    }

    private func emailFlowChanged(_ flow: OpenClickyAuthSession.EmailFlowState) {
        guard step == .account else { return }
        switch flow {
        case .signedIn:
            ShellSettingsRevision.shared.noteChanged()
            fieldText = ""
            succeed(with: "✓ saved. there’s a code in your inbox to confirm it whenever you like.")
        case .needsCode(let email):
            fieldText = ""
            status = "that email has an account. type the 6-digit code i sent to \(email)."
            statusIsGood = false
            publish()
            focusField()
        case .sending, .checkingCode:
            status = "one moment…"
            statusIsGood = false
            publish()
        case .failed(let message):
            status = message
            statusIsGood = false
            publish()
        case .idle:
            break
        }
    }

    /// The tour's last note before "all set", on a free account: it has limited credits, and an
    /// AI subscription the person already pays for can be used instead by adding its key.
    private var needsFreeAccountNote: Bool {
        // Questions are answered on OpenClicky's credits (no key or plan of their own answers).
        ProviderRouter.route(for: .claude, credentials: ProviderRouter.currentCredentials()) == .backend
    }

    /// No key and no account (the guest start did not work): the account card asks for an email
    /// instead, which is how asking gets switched on.
    private var accountCardAsksForEmail: Bool { !OpenClickyConfiguration.isConfigured }

    private var idleWatchTimer: Timer?

    /// Runs `action` once the mouse has stayed put for 2 s, starting `delay` seconds from now.
    private func watchForIdleMouse(after delay: TimeInterval, action: @escaping @MainActor () -> Void) {
        idleWatchTimer?.invalidate()
        var lastLocation = NSEvent.mouseLocation
        var stillSince = Date().addingTimeInterval(delay)
        idleWatchTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] timer in
            Task { @MainActor in
                guard let self else { timer.invalidate(); return }
                let location = NSEvent.mouseLocation
                if hypot(location.x - lastLocation.x, location.y - lastLocation.y) > 2 {
                    lastLocation = location
                    stillSince = max(Date(), stillSince)
                    return
                }
                guard Date().timeIntervalSince(stillSince) >= 2 else { return }
                timer.invalidate()
                self.idleWatchTimer = nil
                action()
            }
        }
    }

    /// Clicky is pointing at something and talking about it before the talk step's "your turn".
    private var isShowingDemo = false
    /// The step "set up" left from (asking or a task with nothing set up), to come back to once
    /// the email gave the Mac an account.
    private var stepAfterAccount: FirstRunTourStep?

    private func focusField() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            self?.companionManager?.notchHUDManager.focusTourField()
        }
    }

    private func taskStarted() {
        guard step == .act else { return }
        succeed(with: "✓ on it. results show up in the island.", saying: .tourActDone)
    }

    // MARK: Moving through the steps

    private func succeed(with message: String, saying line: RecordedVoice.Line? = nil) {
        status = message
        statusIsGood = true
        offersMicrophoneChange = false
        if let line { companionManager?.recordedVoice.play(line) }
        publish()
        scheduleAdvance(after: 2.6)
    }

    private func scheduleAdvance(after seconds: TimeInterval) {
        advanceWorkItem?.cancel()
        let workItem = DispatchWorkItem { [weak self] in self?.advance() }
        advanceWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: workItem)
    }

    private func advance() {
        switch step {
        case .meet: go(to: .dictate)
        case .dictate: go(to: .talk)
        case .talk: go(to: .act)
        case .act: go(to: needsFreeAccountNote ? .account : .done)
        case .account:
            let next = stepAfterAccount ?? .done
            stepAfterAccount = nil
            go(to: next)
        case .done, .none: finish()
        case .openAtLogin: closeOpenAtLoginOffer()
        }
    }

    private func go(to newStep: FirstRunTourStep) {
        advanceWorkItem?.cancel()
        step = newStep
        companionManager?.dictationTakeController.holdsTakesOnTheIsland = newStep == .dictate
        idleWatchTimer?.invalidate()
        status = nil
        statusIsGood = false
        offersMicrophoneChange = false
        UserDefaults.standard.set(newStep.rawValue, forKey: Self.stepKey)
        isShowingDemo = false
        switch newStep {
        case .meet:
            companionManager?.hasCompletedOnboarding = true
            companionManager?.introducePointerForTour()
            // Still after the hello? The pointer goes and points at "let’s go".
            watchForIdleMouse(after: 4) { [weak self] in
                guard let self, self.step == .meet else { return }
                self.companionManager?.pointAtTourPrimaryButton(saying: "tap let’s go")
            }
            // Nothing to type to get going: a Mac with no key and no account starts as a guest on
            // OpenClicky's credits, so asking and tasks work in this very tour.
            if !OpenClickyConfiguration.isConfigured {
                Task { [weak self] in
                    await OpenClickyAuthSession.shared.startGuestWithoutEmail()
                    self?.publish()
                }
            }
        case .dictate:
            if companionManager?.dictationSettings.dictationKey == .fn && !speechNeedsAllowing {
                companionManager?.recordedVoice.play(.tourDictate)
            }
        case .talk:
            if OpenClickyConfiguration.isConfigured, let companionManager {
                // Watch first: Clicky points at something and says a few words, then it's your turn.
                isShowingDemo = true
                companionManager.performTourDemo { [weak self] in
                    guard let self, self.step == .talk, self.isShowingDemo else { return }
                    self.isShowingDemo = false
                    companionManager.recordedVoice.play(.tourTalk)
                    self.publish()
                }
            }
        case .act:
            if OpenClickyConfiguration.isConfigured { companionManager?.recordedVoice.play(.tourAct) }
        case .account:
            fieldText = ""
            if accountCardAsksForEmail {
                OpenClickyAuthSession.shared.forgetSettledFlow()
                focusField()
            } else {
                companionManager?.recordedVoice.play(.tourAccount)
            }
        case .done:
            companionManager?.recordedVoice.play(.tourDone)
        case .openAtLogin:
            break
        }
        publish()
    }

    // MARK: Open at login

    nonisolated static let useCountKey = "openclicky.useCount"
    nonisolated static let openAtLoginAskedKey = "openclicky.openAtLoginAsked"
    /// Uses (a dictation with words, a spoken answer) before the island asks about opening at login.
    nonisolated static let usesBeforeOpenAtLoginOffer = 5

    /// Whether this is the moment to ask: enough uses, setup done, not asked before, not already on.
    nonisolated static func shouldOfferOpenAtLogin(useCount: Int, tourIsFinished: Bool, alreadyAsked: Bool, isOpenAtLogin: Bool) -> Bool {
        useCount >= usesBeforeOpenAtLoginOffer && tourIsFinished && !alreadyAsked && !isOpenAtLogin
    }

    /// Counts one use; on the fifth, once the take or answer is over, the island asks (once) whether
    /// OpenClicky should open when the Mac starts, so the dictation key always works.
    func noteUse() {
        let defaults = UserDefaults.standard
        let useCount = defaults.integer(forKey: Self.useCountKey) + 1
        defaults.set(useCount, forKey: Self.useCountKey)
        guard step == nil, Self.shouldOfferOpenAtLogin(useCount: useCount, tourIsFinished: isFinished,
                                                       alreadyAsked: defaults.bool(forKey: Self.openAtLoginAskedKey),
                                                       isOpenAtLogin: SMAppServiceBridge.isEnabled) else { return }
        // A moment after the answer or the paste, not on top of it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) { [weak self] in
            guard let self, self.step == nil, self.companionManager?.voiceState == .idle else { return }
            UserDefaults.standard.set(true, forKey: Self.openAtLoginAskedKey)
            AppLog.append("open at login: offered after \(useCount) uses")
            self.step = .openAtLogin
            self.status = nil
            self.statusIsGood = false
            self.publish()
        }
    }

    private func closeOpenAtLoginOffer() {
        advanceWorkItem?.cancel()
        step = nil
        status = nil
        publish()
    }

    private func finish() {
        advanceWorkItem?.cancel()
        idleWatchTimer?.invalidate()
        companionManager?.dictationTakeController.holdsTakesOnTheIsland = false
        step = nil
        let defaults = UserDefaults.standard
        defaults.set(true, forKey: Self.doneKey)
        defaults.removeObject(forKey: Self.stepKey)
        companionManager?.dictationSettings.hasCompletedDictationOnboarding = true
        publish()
        // Setup is done: the models can download in the background now.
        companionManager?.startAutomaticModelDownloads(tourIsFinished: true)
    }

    // MARK: The card's buttons

    func primaryAction() {
        guard let step, let companionManager else { return }
        switch step {
        case .meet:
            go(to: .dictate)
        case .dictate:
            if speechNeedsAllowing {
                SFSpeechRecognizer.requestAuthorization { _ in
                    DispatchQueue.main.async { [weak self] in self?.publish() }
                }
            } else if needsFnFreed {
                FnKeyGuard.freeFn()
                publish()
            } else if suggestsBuiltInMicrophone {
                companionManager.dictationSettings.preferredMicrophoneUID = MicrophoneDevices.builtInMicrophoneUID
                if companionManager.dictationTakeController.isTakeInProgress {
                    companionManager.dictationTakeController.cancelTake(reason: "cancelled")
                }
                status = "now listening through \(MicrophoneMenu.deviceName(settings: companionManager.dictationSettings)). try again."
                statusIsGood = false
                offersMicrophoneChange = false
                publish()
            }
        case .account:
            if accountCardAsksForEmail {
                submitField()
            } else {
                // "add my key": Settings → Keys & Account has the key fields; the tour carries on.
                companionManager.showSettings(.account)
                advance()
            }
        case .talk, .act:
            // Tried it (or would rather not): "next" moves on, as the success would.
            guard !OpenClickyConfiguration.isConfigured else { advance(); return }
            if OpenClickyAuthSession.shared.accountsOpen != false {
                stepAfterAccount = step
                go(to: .account)
            } else {
                companionManager.showSettings(.account)
            }
        case .done:
            companionManager.showSettings(.general)
            finish()
        case .openAtLogin:
            let isOn = SMAppServiceBridge.set(enabled: true)
            AppLog.append("open at login: \(isOn ? "switched on" : "refused")")
            status = isOn ? "✓ done. i’ll be here whenever your mac starts." : "macOS didn’t allow it. you can switch it on in settings → general."
            statusIsGood = isOn
            publish()
            if isOn { scheduleAdvance(after: 2.4) }
        }
    }

    func secondaryAction() {
        guard let step else { return }
        switch step {
        case .meet: finish()
        case .done: finish()
        case .dictate, .account, .talk, .act: advance()
        case .openAtLogin: closeOpenAtLoginOffer()
        }
    }

    /// The account step's field: an email starts a free account (or asks for a code when that
    /// email has one); a code finishes signing in.
    func submitField() {
        let auth = OpenClickyAuthSession.shared
        let typed = fieldText.trimmingCharacters(in: .whitespacesAndNewlines)
        if case .needsCode = auth.emailFlow {
            guard EmailAccountForm.canSubmitCode(typed) else {
                status = "the code is the 6 digits in the email."
                publish()
                return
            }
            Task { await auth.submitCode(typed) }
        } else {
            guard EmailAccountForm.canSubmitEmail(typed) else {
                status = "that doesn’t look like an email yet."
                publish()
                return
            }
            Task { await auth.start(email: typed) }
        }
    }

    /// The dictate step's "change microphone" link.
    func changeMicrophone() {
        guard let companionManager else { return }
        let microphoneBefore = companionManager.dictationSettings.preferredMicrophoneUID
        MicrophoneMenu.present(settings: companionManager.dictationSettings)
        // A take still listening keeps the microphone it opened; end it so the next one uses the
        // one just chosen.
        if companionManager.dictationSettings.preferredMicrophoneUID != microphoneBefore,
           companionManager.dictationTakeController.isTakeInProgress {
            companionManager.dictationTakeController.cancelTake(reason: "cancelled")
        }
        status = "now listening through \(MicrophoneMenu.deviceName(settings: companionManager.dictationSettings)). try again."
        statusIsGood = false
        offersMicrophoneChange = true
        publish()
    }

    /// What to try on the task step. The agent (your own keys) can work in apps; a free account
    /// has the quick actions (folders, apps, sites, volume).
    private var taskExamples: String {
        if AccountCapabilities.current().usesAgent {
            return "hold control + option and say “add a note to my notes: buy milk”, or “organize my downloads folder so it’s easier to find things”."
        }
        return "hold control + option and say “make a folder on my desktop called hello clicky”, or “open notes”."
    }

    /// fn is the dictation key but macOS still opens the emoji picker with it.
    private var needsFnFreed: Bool {
        companionManager?.dictationSettings.dictationKey == .fn && !FnKeyGuard.isFnFree
    }

    // MARK: The card

    fileprivate func publish() {
        card = step.map(makeCard)
        companionManager?.notchHUDManager.setTourCard(card)
    }

    private func makeCard(for step: FirstRunTourStep) -> FirstRunTourCard {
        let key = companionManager?.dictationSettings.dictationKey.keycapLabel ?? "fn"
        let canAsk = OpenClickyConfiguration.isConfigured
        switch step {
        case .meet:
            return FirstRunTourCard(
                step: .meet, title: "hi, i’m clicky",
                detail: "your pointer’s new sidekick. i’ll show you three things. it takes about a minute.",
                symbol: "hand.wave.fill",
                primaryTitle: "let’s go", secondaryTitle: "skip tour")
        case .dictate where speechNeedsAllowing:
            return FirstRunTourCard(
                step: .dictate, title: "first, let me use speech recognition",
                detail: "macOS asks once. it lets me turn your voice into words, right here on this mac.",
                symbol: "waveform", status: status, statusIsGood: statusIsGood,
                primaryTitle: "allow", secondaryTitle: "skip")
        case .dictate:
            let fnNote = needsFnFreed ? "macOS opens emoji on fn. let me switch that off first." : nil
            return FirstRunTourCard(
                step: .dictate, title: "hold \(key) and say something",
                detail: "try “hello openclicky, this is my first take”, then let go.",
                keys: [key], status: status ?? fnNote, statusIsGood: statusIsGood,
                microphoneName: companionManager.map { MicrophoneMenu.deviceName(settings: $0.dictationSettings) },
                microphoneNeedsAttention: offersMicrophoneChange,
                primaryTitle: needsFnFreed ? "free fn" : (suggestsBuiltInMicrophone ? "use built-in mic" : nil), secondaryTitle: "skip")
        case .account where accountCardAsksForEmail:
            let needsCode: Bool = { if case .needsCode = OpenClickyAuthSession.shared.emailFlow { return true } else { return false } }()
            return FirstRunTourCard(
                step: .account, title: "switch on asking",
                detail: "add your email for a free account and i’ll answer questions and do things for you. no password.",
                symbol: "envelope.fill", status: status, statusIsGood: statusIsGood,
                field: needsCode ? .code : .email,
                primaryTitle: needsCode ? "confirm" : "start", secondaryTitle: "skip")
        case .account:
            return FirstRunTourCard(
                step: .account, title: "you’re on a free account",
                detail: "it has a limited number of free credits. if you already pay for openai, claude or another ai, add that key in settings and use it right here.",
                symbol: "sparkles", primaryTitle: "add my key", secondaryTitle: "continue")
        case .talk where isShowingDemo:
            return FirstRunTourCard(
                step: .talk, title: "watch this",
                detail: "i’m looking at your screen. i’ll point at something and tell you about it.",
                symbol: "eye.fill", secondaryTitle: "skip")
        case .talk:
            return FirstRunTourCard(
                step: .talk, title: canAsk ? "your turn: ask me something" : "you can ask me things too",
                detail: canAsk
                    ? "hold control + option and ask “what’s this?” about anything on your screen. i answer out loud and point."
                    : "about anything on your screen, out loud, and i’ll point at it. add your email to switch it on, free.",
                keys: ["⌃", "⌥"], status: status, statusIsGood: statusIsGood,
                primaryTitle: canAsk ? "next" : "add email", secondaryTitle: "skip")
        case .act:
            return FirstRunTourCard(
                step: .act, title: canAsk ? "hand me a task" : "i can do things for you too",
                detail: canAsk ? taskExamples : "open apps, make folders, and more, just by asking. add your email to switch it on, free.",
                keys: ["⌃", "⌥"], status: status, statusIsGood: statusIsGood,
                primaryTitle: canAsk ? "next" : "add email", secondaryTitle: "skip")
        case .done:
            return FirstRunTourCard(
                step: .done, title: "you’re all set",
                detail: "hover the notch any time. keys, microphone and the rest live in settings.",
                symbol: "checkmark.circle.fill",
                primaryTitle: "open settings", secondaryTitle: "done")
        case .openAtLogin:
            return FirstRunTourCard(
                step: .openAtLogin, title: "open me when your mac starts?",
                detail: "then \(key) works from the moment you log in, without opening me first.",
                symbol: "power", status: status, statusIsGood: statusIsGood,
                primaryTitle: statusIsGood ? nil : "yes, open at login", secondaryTitle: statusIsGood ? "close" : "not now")
        }
    }
}

#if DEBUG
extension FirstRunTourController {
    /// The UI tour shows each card without walking the steps (nothing is saved).
    func debugShow(_ shownStep: FirstRunTourStep?, status shownStatus: String? = nil, isGood: Bool = false, companionManager: CompanionManager) {
        self.companionManager = companionManager
        step = shownStep
        status = shownStatus
        statusIsGood = isGood
        offersMicrophoneChange = shownStep == .dictate && shownStatus != nil && !isGood
        publish()
    }
}
#endif

// MARK: - The island card

struct FirstRunTourCardView: View {
    let card: FirstRunTourCard
    @ObservedObject var controller: FirstRunTourController
    var topInset: CGFloat = 0

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            illustration
                .frame(width: 84, height: 56)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 3) {
                Text(card.title)
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(DS.HUD.text)
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
                Text(card.detail)
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text2)
                    .lineSpacing(1)
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
                if let field = card.field {
                    NotchComposerTextField(
                        text: $controller.fieldText,
                        placeholder: field == .email ? "you@example.com" : "6-digit code",
                        onSubmit: { controller.submitField() },
                        onEscape: {}
                    )
                    .frame(height: 22)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DS.HUD.surfaceRaised))
                    .padding(.top, 4)
                }
                if let status = card.status {
                    Text(status)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(card.statusIsGood ? Color(red: 0.4, green: 0.85, blue: 0.5) : DS.HUD.pointer)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 2)
                }
                if let microphoneName = card.microphoneName {
                    Button(action: { controller.changeMicrophone() }) {
                        HStack(spacing: 4) {
                            Image(systemName: "mic.fill").font(.system(size: 10))
                            Text("listening through \(microphoneName) ·").lineLimit(1)
                            Text("change").underline()
                        }
                        .font(.system(size: 12, weight: card.microphoneNeedsAttention ? .semibold : .regular))
                        .foregroundColor(card.microphoneNeedsAttention ? DS.HUD.text : DS.HUD.text3)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    .padding(.top, 2)
                }
                if let practiceIndex = card.step.practiceIndex {
                    progressDashes(current: practiceIndex).padding(.top, 5)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            VStack(spacing: 6) {
                if let primaryTitle = card.primaryTitle {
                    Button(action: { controller.primaryAction() }) {
                        Text(primaryTitle)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundColor(.black)
                            .padding(.horizontal, 14)
                            .frame(minWidth: 72)
                            .frame(height: 30)
                            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(DS.HUD.pointer))
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                }
                Button(action: { controller.secondaryAction() }) {
                    Text(card.secondaryTitle)
                        .font(.system(size: 12))
                        .foregroundColor(DS.HUD.text2)
                        .frame(minWidth: 72)
                        .frame(height: 24)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }
            .fixedSize()
        }
        .padding(.top, topInset + 12)
        .padding(.horizontal, DS.HUD.bodySidePadding)
        .padding(.bottom, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    @ViewBuilder
    private var illustration: some View {
        if !card.keys.isEmpty {
            TourKeycaps(keys: card.keys)
        } else if let symbol = card.symbol {
            Image(systemName: symbol)
                .font(.system(size: 26, weight: .semibold))
                .foregroundColor(DS.HUD.pointer)
                .frame(width: 56, height: 56)
                .background(Circle().fill(DS.HUD.pointerTint))
        }
    }

    private func progressDashes(current: Int) -> some View {
        HStack(spacing: 5) {
            ForEach(0..<3, id: \.self) { index in
                Capsule()
                    .fill(index <= current ? DS.HUD.pointer : DS.HUD.surfaceRaised)
                    .frame(width: 18, height: 3)
            }
        }
    }
}

/// The step's keys as keycaps, pressed down together and held, then let go, over and over —
/// the gesture the card asks for.
struct TourKeycaps: View {
    let keys: [String]

    var body: some View {
        TimelineView(.animation) { timeline in
            let press = KeyboardSketch.pressAmount(at: timeline.date.timeIntervalSinceReferenceDate)
            HStack(spacing: 5) {
                ForEach(Array(keys.enumerated()), id: \.offset) { _, key in
                    Text(key)
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundColor(press > 0.5 ? .black : DS.HUD.text)
                        .frame(width: keys.count > 1 ? 36 : 48, height: 36)
                        .background(
                            RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .fill(press > 0.5 ? DS.HUD.pointer : DS.HUD.surfaceRaised)
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .strokeBorder(Color.white.opacity(0.18), lineWidth: 1)
                        )
                        .shadow(color: .black.opacity(0.5), radius: 0, x: 0, y: (1 - press) * 3)
                        .offset(y: press * 3)
                }
            }
        }
    }
}
