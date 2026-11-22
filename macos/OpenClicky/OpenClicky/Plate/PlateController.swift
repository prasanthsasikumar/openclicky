//
//  PlateController.swift
//  OpenClicky
//
//  Runs the day's offers end to end: once a day (Settings → Clicky, off until
//  switched on) it gathers the digest, has the cards written, and announces them on the island;
//  the deck deals them; "go ahead" queues the card's brief for the agent, one task at a time; the
//  deck closing asks once whether the set was worth offering. The menu bar's "What Could You Take
//  On Today?" does the same on request, whatever the switch says.
//

import AppKit
import Combine
import Foundation

@MainActor
final class PlateController {
    private weak var companionManager: CompanionManager?
    private let deck = PlateDeckWindowController()
    private let sounds = PlateSoundPlayer()
    private var store = PlateStore.standard
    private(set) var batch: PlateBatch?
    private var isWriting = false
    private var lastAttemptAt: Date?
    private var checkTimer: Timer?
    private var wakeObserver: NSObjectProtocol?
    private var voiceStateSubscription: AnyCancellable?
    private var noteDismissal: DispatchWorkItem?
    /// Briefs said yes to and not yet handed to the agent, which runs one task at a time.
    private var queue: [String] = []
    /// The agent is on one of ours, until the companion is idle again.
    private var isRunningTask = false
    /// The sample cards are up (`--openclicky-plate-smoke`): nothing is saved or run.
    private var isSample = false

    /// A failed morning is tried again no sooner than this.
    static let retryInterval: TimeInterval = 3 * 3600
    static let checkInterval: TimeInterval = 600

    var waitingCount: Int { batch?.waiting.count ?? 0 }

    func start(companionManager: CompanionManager) {
        self.companionManager = companionManager
        sounds.isOn = { [weak companionManager] in companionManager?.dictationSettings.sounds ?? true }
        let today = PlateSchedule.dayKey(for: Date())
        if let saved = store.loadCurrent(), saved.dayKey == today { batch = saved }
        checkTimer?.invalidate()
        checkTimer = Timer.scheduledTimer(withTimeInterval: Self.checkInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkTheMorning() }
        }
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 20_000_000_000)
                self?.checkTheMorning()
            }
        }
        // An agent task ends with the companion idle again: that is when the next one may start.
        voiceStateSubscription = companionManager.$voiceState
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.companionBecameIdleMaybe() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 45) { [weak self] in self?.checkTheMorning() }
    }

    func stop() {
        checkTimer?.invalidate()
        checkTimer = nil
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        wakeObserver = nil
        voiceStateSubscription = nil
        deck.hide()
    }

    // MARK: - The morning

    /// Writes and announces today's cards when they are due, the switch is on, and nothing else
    /// has the island. Quiet when there is nothing to offer.
    func checkTheMorning(now: Date = Date()) {
        guard let companionManager, companionManager.dictationSettings.suggestTasks, !isSample else { return }
        guard !isWriting, !deck.isOpen, companionManager.voiceState == .idle, !companionManager.notchHUDManager.isSetupInProgress else { return }
        let today = PlateSchedule.dayKey(for: now)
        if let batch, batch.dayKey == today {
            if !batch.wasAnnounced && !batch.waiting.isEmpty { announce() }
            return
        }
        guard PlateSchedule.isDue(now: now, lastBatchDayKey: batch?.dayKey) else { return }
        if let lastAttemptAt, now.timeIntervalSince(lastAttemptAt) < Self.retryInterval { return }
        Task { _ = await writeToday(now: now, announces: true) }
    }

    /// The menu bar item: today's waiting cards, or a fresh set written while the island says so.
    func showOnRequest() {
        guard !isWriting, !deck.isOpen else { return }
        if let batch, batch.dayKey == PlateSchedule.dayKey(for: Date()), !batch.waiting.isEmpty {
            openDeck()
            return
        }
        setIslandCard(.note("Looking at what I could take on…", isWorking: true))
        Task {
            let failure = await writeToday(now: Date(), announces: false)
            if let failure {
                showNote(Self.sentence(for: failure))
            } else {
                openDeck()
            }
        }
    }

    static func sentence(for failure: PlateWriter.Failure) -> String {
        switch failure {
        case .nothingToGoOn: return "Nothing to go on yet. Ask me again after a day's work."
        case .noAgent: return "This needs your own key or plan, so I can do the jobs too."
        case .noReply: return "I couldn't come up with offers just now."
        case .nothingOffered: return "Nothing worth offering today."
        }
    }

    /// Returns why no cards were written, or nil when today's batch is ready.
    private func writeToday(now: Date, announces: Bool) async -> PlateWriter.Failure? {
        isWriting = true
        lastAttemptAt = now
        defer { isWriting = false }
        let context = await PlateContext.gather(now: now)
        let previous = batch ?? store.loadCurrent()
        switch await PlateWriter.write(context: context, previous: previous, now: now) {
        case .success(let suggestions):
            let fresh = PlateBatch(dayKey: PlateSchedule.dayKey(for: now), suggestions: suggestions)
            batch = fresh
            store.save(fresh)
            if announces { announce() }
            return nil
        case .failure(let failure):
            AppLog.append("plate: no cards today (\(failure))")
            return failure
        }
    }

    // MARK: - The island and the deck

    private func announce() {
        guard var current = batch, !current.waiting.isEmpty else { return }
        current.wasAnnounced = true
        batch = current
        if !isSample { store.save(current) }
        let waiting = current.waiting
        setIslandCard(.announce(dayPart: PlateSchedule.dayPart(for: Date()), count: waiting.count, titles: Array(waiting.prefix(3).map(\.title))))
        AppLog.append("plate: announced \(waiting.count) cards")
    }

    func notNow() {
        setIslandCard(nil)
        AppLog.append("plate: not now")
    }

    func openDeck(takesKeyboard: Bool = true) {
        guard let companionManager, let waiting = batch?.waiting, !waiting.isEmpty else { return }
        let hud = companionManager.notchHUDManager
        guard let screenFrame = hud.primaryScreenFrame else { return }
        setIslandCard(.deck(count: waiting.count))
        deck.show(
            cards: waiting, screenFrame: screenFrame, notchPoint: hud.dockPoint,
            reduceMotion: companionManager.dictationSettings.reduceAnimation || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
            sounds: sounds, takesKeyboard: takesKeyboard,
            onTake: { [weak self] in self?.decide($0, status: .taken) },
            onSkip: { [weak self] in self?.decide($0, status: .skipped) },
            onClosed: { [weak self] in self?.deckClosed() })
    }

    /// The island header's ✕.
    func closeDeck() {
        deck.close()
    }

    private func decide(_ suggestion: PlateSuggestion, status: PlateSuggestion.Status) {
        guard var current = batch, let index = current.suggestions.firstIndex(where: { $0.id == suggestion.id }) else { return }
        current.suggestions[index].status = status
        current.suggestions[index].adjustment = suggestion.adjustment
        batch = current
        if !isSample { store.save(current) }
        AppLog.append("plate: \(status.rawValue) a card\(suggestion.adjustment == nil ? "" : " with a change") (\(current.waiting.count) left)")
        if deck.isOpen { setIslandCard(.deck(count: max(current.waiting.count, 0))) }
        guard status == .taken, !isSample else { return }
        queue.append(PlatePrompt.agentBrief(for: current.suggestions[index]))
        runNextTask()
    }

    private func deckClosed() {
        guard let current = batch else { return setIslandCard(nil) }
        if current.hasDecision && current.rating == nil {
            setIslandCard(.feedback)
        } else {
            setIslandCard(nil)
        }
    }

    func rate(_ rating: PlateRating) {
        if var current = batch {
            current.rating = rating
            batch = current
            if !isSample { store.save(current) }
        }
        AppLog.append("plate: rated \(rating.rawValue)")
        if rating == .liked { thank(rating) } else { setIslandCard(.tell) }
    }

    /// The line typed after a rating (empty: skipped).
    func tell(_ comment: String) {
        let trimmed = comment.trimmingCharacters(in: .whitespacesAndNewlines)
        if var current = batch, !trimmed.isEmpty {
            current.comment = String(trimmed.prefix(300))
            batch = current
            if !isSample { store.save(current) }
            AppLog.append("plate: told what would be better (\(trimmed.count) chars)")
        }
        thank(batch?.rating ?? .okay)
    }

    /// The feedback question ran out or was closed unanswered.
    func dismissFeedback() {
        setIslandCard(nil)
    }

    private func thank(_ rating: PlateRating) {
        showNote(nil, thanks: rating == .liked ? "Good. More in that spirit tomorrow." : "Noted. Tomorrow's will be closer.")
    }

    private func showNote(_ text: String?, thanks: String? = nil) {
        noteDismissal?.cancel()
        if let thanks { setIslandCard(.thanks(thanks)) } else if let text { setIslandCard(.note(text, isWorking: false)) }
        let dismissal = DispatchWorkItem { [weak self] in self?.setIslandCard(nil) }
        noteDismissal = dismissal
        DispatchQueue.main.asyncAfter(deadline: .now() + (thanks == nil ? 4 : 2.4), execute: dismissal)
    }

    private func setIslandCard(_ card: PlateIslandCard?) {
        noteDismissal?.cancel()
        companionManager?.notchHUDManager.setPlateCard(card)
    }

    // MARK: - The agent

    private func runNextTask() {
        guard let companionManager, !isRunningTask, !queue.isEmpty, companionManager.voiceState == .idle else { return }
        let brief = queue.removeFirst()
        isRunningTask = true
        AppLog.append("plate: a task goes to the agent (\(brief.count) chars, \(queue.count) waiting)")
        companionManager.submitTextToAgent(brief, startsNewThread: true, attachesScreen: false)
        // Refused before it began (no agent on this account): nothing will come back to idle.
        if companionManager.voiceState == .idle { isRunningTask = false }
    }

    private func companionBecameIdleMaybe() {
        guard let companionManager, companionManager.voiceState == .idle else { return }
        guard isRunningTask || !queue.isEmpty else { return }
        isRunningTask = false
        // A breath between tasks, so the result just spoken is not cut off by the next one starting.
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.runNextTask() }
    }

    // MARK: - Samples (debug and screenshots)

    #if DEBUG
    /// `--openclicky-plate-smoke [announce|deck]`: six sample cards, nothing written to disk and
    /// nothing handed to the agent.
    func showSample(openingDeck: Bool) {
        isSample = true
        batch = PlateBatch(dayKey: PlateSchedule.dayKey(for: Date()), suggestions: Self.sampleSuggestions)
        if openingDeck { openDeck() } else { announce() }
    }

    /// `--openclicky-plate-smoke demo`: the sample deck plays itself (a card read, one accepted,
    /// one binned, the rest gathered) without the pointer or the keyboard, for a recording.
    func playDemo() {
        isSample = true
        batch = PlateBatch(dayKey: PlateSchedule.dayKey(for: Date()), suggestions: Self.sampleSuggestions)
        announce()
        var clock = 2.6
        func at(_ delay: TimeInterval, _ work: @escaping @MainActor (PlateDeckModel) -> Void) {
            clock += delay
            DispatchQueue.main.asyncAfter(deadline: .now() + clock) { [weak self] in
                guard let model = self?.deck.model else { return }
                work(model)
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + clock) { [weak self] in self?.openDeck(takesKeyboard: false) }
        at(1.9) { model in model.pointerMoved(to: model.slots[1].center) }
        at(2.2) { model in model.take(model.cards[1]) }
        at(1.6) { model in model.pointerMoved(to: model.slots[3].center) }
        at(1.6) { model in model.skip(model.cards[3]) }
        at(1.4) { model in model.pointerMoved(to: model.slots[0].center) }
        at(1.6) { model in model.pointerMoved(to: nil) }
        at(0.5) { model in model.close() }
    }

    static let sampleSuggestions: [PlateSuggestion] = [
        sample("s1", "I'll draft the release notes for the next version from this week's commits", "Twelve commits landed since the last tag.",
               ["Read the commits since the tag", "Group them by platform", "Write the notes in plain words"], 15, "a draft of the notes", .draft),
        sample("s2", "I'll read the 5 open issues and rank them by how many people they block", "New reports came in after the last release.",
               ["Read each issue and its thread", "Find duplicates", "Rank by people affected"], 20, "a ranked list", .list),
        sample("s3", "I'll study 20 launch posts that did well and outline yours", "The launch plan is still a draft.",
               ["Collect 20 top posts", "Note what each opens with", "Outline yours the same way"], 35, "an outline with examples", .research),
        sample("s4", "I'll compare 6 code signing services and tell you which fits", "The Windows builds ship unsigned.",
               ["List 6 services", "Compare price and wait time", "Check the open source rules"], 25, "a comparison table", .numbers),
        sample("s5", "I'll prepare notes for Thursday's design review", "It's on your calendar for Thursday at 10.",
               ["Gather what changed since last time", "Pull the open questions", "Fit it on one page"], 15, "a one page brief", .schedule),
        sample("s6", "I'll draft the email asking the grant office about the deadline", "Your notes say to ask them this week.",
               ["Find the programme's contact", "Write three short paragraphs", "Leave it for you to send"], 10, "an email ready to send", .message),
    ]

    private static func sample(_ id: String, _ title: String, _ detail: String, _ steps: [String], _ minutes: Int, _ result: String, _ kind: PlateSuggestion.Kind) -> PlateSuggestion {
        var suggestion = PlateSuggestion(id: id, title: title, detail: detail, prompt: "sample", kind: kind)
        suggestion.steps = steps
        suggestion.minutes = minutes
        suggestion.result = result
        return suggestion
    }
    #endif
}
