//
//  AccountCapabilities.swift
//  OpenClicky
//
//  One answer to "what can this person use?", so every lane asks the same question: a free
//  account (signed in, on the grant: Apple hears, Claude thinks, ElevenLabs speaks, no realtime,
//  no agent), own keys (everything, unmetered), or signed out (offline dictation only).
//

import Foundation

enum AccountKind: Equatable { case ownKeys, account, signedOut }

struct AccountCapabilities: Equatable {
    let kind: AccountKind

    static func current(settings: OpenClickyShellSettings = OpenClickyConfiguration.settings) -> AccountCapabilities {
        if OpenClickyConfiguration.usesOwnKeys(settings) { return AccountCapabilities(kind: .ownKeys) }
        if OpenClickyConfiguration.isConfigured { return AccountCapabilities(kind: .account) }
        return AccountCapabilities(kind: .signedOut)
    }

    var usesRealtime: Bool { kind == .ownKeys }
    var usesAgent: Bool { kind == .ownKeys }
    var hearsOnDevice: Bool { kind != .ownKeys }
    var polishPath: String { kind == .ownKeys ? "/v1/chat/completions" : "/v1/polish" }
    /// The Agents tab's line under a disabled composer; signed out has nothing to explain.
    var agentUnavailableHint: String? { kind == .account ? "agent tasks need your own key for now." : nil }
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
}

enum UsageLevel: Equatable { case plenty, runningLow, usedUp }

extension BillingSummary {
    var fractionUsed: Double {
        guard monthlyLimitUsd > 0 else { return 0 }
        return min(1, (spentMonthUsd / monthlyLimitUsd * 100).rounded() / 100)
    }
    var level: UsageLevel {
        if budgetExhausted || blocked || fractionUsed >= 1 || (dailyLimitUsd > 0 && spentTodayUsd >= dailyLimitUsd) { return .usedUp }
        return fractionUsed >= 0.8 ? .runningLow : .plenty
    }
}
