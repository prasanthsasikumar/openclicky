//
//  AccountCapabilities.swift
//  OpenClicky
//
//  One answer to "what can this person use?", so every lane asks the same question: a free
//  account (signed in, on the grant: Apple hears, Claude thinks, ElevenLabs speaks, no realtime,
//  no agent), own keys (everything, unmetered), or signed out (offline dictation only).
//
//  The backend decides which: GET /billing/me answers `plan` ("grant", "byok" or "unmetered"),
//  AccountProfileStore keeps the last answer, and nothing is taken away on a guess — until the
//  backend has said "grant", a signed-in Mac keeps every capability.
//

import Combine
import Foundation

enum AccountKind: Equatable { case ownKeys, account, signedOut }

struct AccountCapabilities: Equatable {
    let kind: AccountKind

    static func current(settings: OpenClickyShellSettings = OpenClickyConfiguration.settings, defaults: UserDefaults = .standard) -> AccountCapabilities {
        let signedIn = !settings.token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        return AccountCapabilities(kind: kind(
            ownOpenAIKey: OpenClickyConfiguration.usesOwnKeys(settings), signedIn: signedIn,
            plan: defaults.string(forKey: AccountProfileStore.planKey)))
    }

    /// An own OpenAI key always means own keys; no token means signed out; otherwise the backend's
    /// last answer: only "grant" is a free account. "byok", "unmetered" and no answer yet all keep
    /// full capabilities.
    static func kind(ownOpenAIKey: Bool, signedIn: Bool, plan: String?) -> AccountKind {
        if ownOpenAIKey { return .ownKeys }
        guard signedIn else { return .signedOut }
        return plan == "grant" ? .account : .ownKeys
    }

    var usesRealtime: Bool { kind == .ownKeys }
    var usesAgent: Bool { kind == .ownKeys }
    var hearsOnDevice: Bool { kind != .ownKeys }
    var polishPath: String { kind == .ownKeys ? "/v1/chat/completions" : "/v1/polish" }
    /// The Agents tab's line under a disabled composer; signed out has nothing to explain.
    var agentUnavailableHint: String? { kind == .account ? "agent tasks need your own key for now." : nil }
    /// "teach a skill" drafts the skill on OpenAI (/skills/create), which the grant does not cover.
    var skillTeachingUnavailableHint: String? { kind == .account ? "teaching skills needs your own key for now." : nil }
    /// At most two screenshots go with an account's question (the backend refuses more).
    var maxAskScreens: Int? { kind == .account ? 2 : nil }
}

enum AccountLimitError: String, Equatable {
    case personalLimit = "personal_limit", dailyLimit = "daily_limit", monthlyBudget = "monthly_budget"
    case notOnPlan = "not_on_plan", accountsFull = "accounts_full", blocked = "blocked", ttsBudget = "tts_budget"

    static func from(status: Int, body: Data) -> AccountLimitError? {
        guard status == 402,
              let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let raw = json["error"] as? String else { return nil }
        return AccountLimitError(rawValue: raw)
    }

    var message: String {
        switch self {
        case .personalLimit: return "you've used this month's free allowance — it comes back on the 1st. dictation still works."
        case .dailyLimit: return "you've used today's free allowance — it comes back tomorrow. dictation still works."
        case .monthlyBudget: return "openclicky's free allowance is used up for now — you can keep going with your own key."
        case .notOnPlan: return "that needs your own key for now."
        case .accountsFull: return "openclicky's free accounts are full right now — you can use your own key instead."
        case .blocked: return "this account is paused. dictation still works."
        case .ttsBudget: return ""
        }
    }

    /// A grant route answered 503 `unavailable`: the ledger itself could not be reached.
    static let serviceTroubleMessage = "openclicky's free service is having trouble — try again in a minute."

    /// True for the backend's 503 `{"error":"unavailable"}` on a grant route.
    static func isServiceTrouble(status: Int, body: Data) -> Bool {
        guard status == 503,
              let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return false }
        return json["error"] as? String == "unavailable"
    }
}

enum UsageLevel: Equatable { case plenty, runningLow, usedUp }

extension BillingSummary {
    /// Requests are metered on the grant. Older backends send no plan: byok was their only signal.
    var isMetered: Bool { plan.map { $0 == "grant" } ?? !byok }
    /// A grant login with no OpenClicky account row: the backend refuses it until someone adds it.
    var isSwitchedOff: Bool { plan == "grant" && onPlan == false && !blocked }
    static let switchedOffMessage = "this login isn't switched on for openclicky yet — write to hello@flowsxr.com, or use your own key."
    static let runningLowNotice = "your free allowance is running low this month — dictation still works offline."

    /// The month to remember when the "running low" notice is due: a metered account past 80% (and
    /// not yet used up) whose notice has not been shown this month. Nil when nothing is due.
    func runningLowNoticeMonth(lastNoticeMonth: String?) -> String? {
        guard isMetered, level == .runningLow, !monthEnd.isEmpty, monthEnd != lastNoticeMonth else { return nil }
        return monthEnd
    }

    var fractionUsed: Double {
        guard monthlyLimitUsd > 0 else { return 0 }
        return min(1, (spentMonthUsd / monthlyLimitUsd * 100).rounded() / 100)
    }
    var level: UsageLevel {
        if budgetExhausted || blocked || fractionUsed >= 1 || (dailyLimitUsd > 0 && spentTodayUsd >= dailyLimitUsd) { return .usedUp }
        return fractionUsed >= 0.8 ? .runningLow : .plenty
    }
}

/// The backend's last word on this login (GET /billing/me), fetched at launch, when credentials
/// change and after sign-in, and kept in UserDefaults so the next launch starts from it.
@MainActor
final class AccountProfileStore: ObservableObject {
    static let shared = AccountProfileStore()
    nonisolated static let planKey = "openclicky.account.plan"
    nonisolated static let runningLowNoticeKey = "openclicky.account.runningLowNoticeMonth"
    /// Posted when the backend's answer changed what this login can use.
    static let profileChangedNotification = Notification.Name("openClickyAccountProfileChanged")

    /// Whether a grant login is switched on (nil until the backend has said).
    @Published private(set) var onPlan: Bool?
    /// Shows the once-a-month "running low" notice (the companion's quiet caption).
    var showNotice: ((String) -> Void)?

    private let defaults: UserDefaults
    private var lastKind: AccountKind
    private var credentialsObserver: NSObjectProtocol?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        lastKind = AccountCapabilities.current(defaults: defaults).kind
    }

    func start() {
        guard credentialsObserver == nil else { return }
        credentialsObserver = NotificationCenter.default.addObserver(
            forName: OpenClickyConfiguration.credentialsChangedNotification, object: nil, queue: .main
        ) { _ in
            Task { @MainActor in AccountProfileStore.shared.refresh() }
        }
        refresh()
    }

    func refresh() {
        guard OpenClickyConfiguration.isConfigured else {
            onPlan = nil
            noteKind()
            return
        }
        Task {
            do { absorb(try await BillingStatusModel.fetchSummary()) }
            catch { print("💳 Account profile: \(error.localizedDescription)") }
        }
    }

    /// Every /billing/me answer lands here, whoever asked for it.
    func absorb(_ summary: BillingSummary) {
        if let plan = summary.plan { defaults.set(plan, forKey: Self.planKey) }
        onPlan = summary.onPlan
        noteKind()
        if let month = summary.runningLowNoticeMonth(lastNoticeMonth: defaults.string(forKey: Self.runningLowNoticeKey)) {
            defaults.set(month, forKey: Self.runningLowNoticeKey)
            showNotice?(BillingSummary.runningLowNotice)
        }
    }

    private func noteKind() {
        let kind = AccountCapabilities.current(defaults: defaults).kind
        guard kind != lastKind else { return }
        lastKind = kind
        NotificationCenter.default.post(name: Self.profileChangedNotification, object: nil)
    }
}
