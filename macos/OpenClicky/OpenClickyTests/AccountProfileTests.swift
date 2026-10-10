import AppKit
import Foundation
import Testing
@testable import OpenClicky

/// The account profile comes from the backend's /billing/me answer, never from a guess.
struct AccountProfileTests {
    private func freshDefaults() -> UserDefaults {
        UserDefaults(suiteName: "openclicky-profile-tests-\(UUID().uuidString)")!
    }

    private func summary(plan: String?, onPlan: Bool? = true, spent: Double = 0, blocked: Bool = false, byok: Bool = false) -> BillingSummary {
        BillingSummary(byok: byok, spentMonthUsd: spent, monthlyLimitUsd: 10, spentTodayUsd: 0, dailyLimitUsd: 2, ttsCharsMonth: 0, ttsCharsLimit: 20000,
                       monthEnd: "2026-11-01T00:00:00.000Z", dayEnd: "", budgetExhausted: false, blocked: blocked, plan: plan, onPlan: onPlan)
    }

    // MARK: C1 — plan → profile

    @Test func eachPlanMapsToAProfile() {
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: true, plan: "grant") == .account)
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: true, plan: "byok") == .ownKeys)
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: true, plan: "unmetered") == .ownKeys)
    }

    @Test func beforeAnyAnswerNothingIsTakenAway() {
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: true, plan: nil) == .ownKeys)
        #expect(AccountCapabilities.kind(ownOpenAIKey: true, signedIn: true, plan: "grant") == .ownKeys)
    }

    @Test func noTokenIsSignedOut() {
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: false, plan: "grant") == .signedOut)
        #expect(AccountCapabilities.kind(ownOpenAIKey: false, signedIn: false, plan: nil) == .signedOut)
    }

    @Test func currentReadsThePersistedPlan() {
        let defaults = freshDefaults()
        var settings = OpenClickyShellSettings()
        settings.token = "jwt"
        #expect(AccountCapabilities.current(settings: settings, defaults: defaults).kind == .ownKeys)
        defaults.set("grant", forKey: AccountProfileStore.planKey)
        #expect(AccountCapabilities.current(settings: settings, defaults: defaults).kind == .account)
        settings.token = ""
        #expect(AccountCapabilities.current(settings: settings, defaults: defaults).kind == .signedOut)
        settings.token = "jwt"
        settings.openaiApiKey = "sk-own"
        #expect(AccountCapabilities.current(settings: settings, defaults: defaults).kind == .ownKeys)
    }

    @MainActor @Test func theStorePersistsTheBackendsAnswer() {
        let defaults = freshDefaults()
        let store = AccountProfileStore(defaults: defaults)
        store.absorb(summary(plan: "grant", onPlan: false))
        #expect(defaults.string(forKey: AccountProfileStore.planKey) == "grant")
        #expect(store.onPlan == false)
        store.absorb(summary(plan: "unmetered"))
        #expect(defaults.string(forKey: AccountProfileStore.planKey) == "unmetered")
    }

    @Test func summariesDecodeWithAndWithoutThePlan() throws {
        let old = #"{"byok":false,"spentMonthUsd":0,"monthlyLimitUsd":10,"spentTodayUsd":0,"dailyLimitUsd":2,"ttsCharsMonth":0,"ttsCharsLimit":0,"monthEnd":"","dayEnd":"","budgetExhausted":false,"blocked":false}"#
        let decodedOld = try JSONDecoder().decode(BillingSummary.self, from: Data(old.utf8))
        #expect(decodedOld.plan == nil && decodedOld.onPlan == nil && decodedOld.isMetered)
        let new = #"{"plan":"unmetered","onPlan":true,"byok":false,"spentMonthUsd":0,"monthlyLimitUsd":0,"spentTodayUsd":0,"dailyLimitUsd":0,"ttsCharsMonth":0,"ttsCharsLimit":0,"monthEnd":"","dayEnd":"","budgetExhausted":false,"blocked":false}"#
        let decodedNew = try JSONDecoder().decode(BillingSummary.self, from: Data(new.utf8))
        #expect(decodedNew.plan == "unmetered" && decodedNew.onPlan == true && !decodedNew.isMetered)
    }

    // MARK: C7 — a login that isn't switched on

    @Test func aGrantLoginWithoutAnAccountRowIsTold() {
        #expect(summary(plan: "grant", onPlan: false).isSwitchedOff)
        #expect(!summary(plan: "grant", onPlan: true).isSwitchedOff)
        #expect(!summary(plan: "grant", onPlan: false, blocked: true).isSwitchedOff) // paused has its own sentence
        #expect(!summary(plan: "unmetered", onPlan: true).isSwitchedOff)
        #expect(NotchSettingsView.planDescription(kind: .account, summary: summary(plan: "grant", onPlan: false)) == BillingSummary.switchedOffMessage)
        #expect(BillingSummary.switchedOffMessage == "this login isn't switched on for openclicky yet — write to hello@flowsxr.com, or use your own key.")
        #expect(NotchSettingsView.planDescription(kind: .account, summary: summary(plan: "grant", spent: 2.1)) == "account · 21% used")
        #expect(NotchSettingsView.planDescription(kind: .ownKeys, summary: summary(plan: "unmetered")) == "not metered")
    }

    // MARK: §3.5 — the running-low notice, once a month

    @Test func theRunningLowNoticeIsDueOnceAMonth() {
        let low = summary(plan: "grant", spent: 8.5)
        #expect(low.runningLowNoticeMonth(lastNoticeMonth: nil) == "2026-11-01T00:00:00.000Z")
        #expect(low.runningLowNoticeMonth(lastNoticeMonth: "2026-11-01T00:00:00.000Z") == nil)
        #expect(low.runningLowNoticeMonth(lastNoticeMonth: "2026-10-01T00:00:00.000Z") != nil)
        #expect(summary(plan: "grant", spent: 5).runningLowNoticeMonth(lastNoticeMonth: nil) == nil)
        #expect(summary(plan: "grant", spent: 10).runningLowNoticeMonth(lastNoticeMonth: nil) == nil) // used up, not running low
        #expect(summary(plan: "unmetered", spent: 9).runningLowNoticeMonth(lastNoticeMonth: nil) == nil)
    }

    @MainActor @Test func theStoreShowsTheNoticeOnlyOnceAMonth() {
        let store = AccountProfileStore(defaults: freshDefaults())
        var shown: [String] = []
        store.showNotice = { shown.append($0) }
        store.absorb(summary(plan: "grant", spent: 8.5))
        store.absorb(summary(plan: "grant", spent: 9))
        #expect(shown == [BillingSummary.runningLowNotice])
    }

    // MARK: C4 — two screenshots on the grant

    private func capture(_ name: String, cursor: Bool) -> CompanionScreenCapture {
        CompanionScreenCapture(imageData: Data(name.utf8), label: name, isCursorScreen: cursor, displayWidthInPoints: 1, displayHeightInPoints: 1,
                               displayFrame: .zero, screenshotWidthInPixels: 1, screenshotHeightInPixels: 1)
    }

    @MainActor @Test func anAccountSendsTheCursorScreenAndAtMostOneOther() {
        let screens = [capture("a", cursor: false), capture("b", cursor: false), capture("c", cursor: true)]
        let account = CompanionManager.screensForAsk(screens, capabilities: AccountCapabilities(kind: .account))
        #expect(account.map(\.label) == ["c", "a"])
        let own = CompanionManager.screensForAsk(screens, capabilities: AccountCapabilities(kind: .ownKeys))
        #expect(own.map(\.label) == ["c", "a", "b"])
        #expect(CompanionManager.screensForAsk([capture("only", cursor: true)], capabilities: AccountCapabilities(kind: .account)).count == 1)
    }

    // MARK: §6.3 — limit sentences once per session; 503 trouble

    @MainActor @Test func limitSentencesAreSpokenOncePerSessionPerKind() {
        func claudeError(_ code: Int, _ body: String) -> NSError {
            NSError(domain: "ClaudeAPI", code: code, userInfo: [NSLocalizedDescriptionKey: "API Error (\(code)): \(body)"])
        }
        var spoken: Set<String> = []
        let daily = claudeError(402, #"{"error":"daily_limit"}"#)
        #expect(CompanionManager.accountNoticeToSpeak(for: daily, alreadySpoken: &spoken) == AccountLimitError.dailyLimit.message)
        #expect(CompanionManager.accountNoticeToSpeak(for: daily, alreadySpoken: &spoken) == nil)
        let notOnPlan = claudeError(402, #"{"error":"not_on_plan"}"#)
        #expect(CompanionManager.accountNoticeToSpeak(for: notOnPlan, alreadySpoken: &spoken) == AccountLimitError.notOnPlan.message)
        let trouble = claudeError(503, #"{"error":"unavailable"}"#)
        #expect(CompanionManager.isAccountNotice(trouble))
        #expect(CompanionManager.accountNoticeToSpeak(for: trouble, alreadySpoken: &spoken) == "openclicky's free service is having trouble — try again in a minute.")
        #expect(CompanionManager.accountNoticeToSpeak(for: trouble, alreadySpoken: &spoken) == nil)
        #expect(!CompanionManager.isAccountNotice(claudeError(503, "upstream down")))
        #expect(!CompanionManager.isAccountNotice(claudeError(500, #"{"error":"daily_limit"}"#)))
    }

    @MainActor @Test func polishSaysTheFreeServiceIsHavingTrouble() {
        #expect(AccountPolishError.unavailable(503).errorDescription == AccountLimitError.serviceTroubleMessage)
        #expect(AccountPolishError.unavailable(500).errorDescription == "couldn't reach openclicky right now.")
        #expect(BillingStatusModel.errorSentence(for: NSError(domain: "OpenClickyBilling", code: 503)) == AccountLimitError.serviceTroubleMessage)
    }

    // MARK: C6 — engines and skills the grant doesn't cover

    @Test func backendEnginesNeedYourOwnKeyOnAnAccount() {
        for engine in [DictationEngineChoice.openclicky, .assemblyai] {
            #expect(DictationEngineResolver.unavailableReason(for: engine, kind: .account, isConfigured: true, sarvamKey: nil) == "needs your own key")
            #expect(DictationEngineResolver.unavailableReason(for: engine, kind: .ownKeys, isConfigured: true, sarvamKey: nil) == nil)
            #expect(DictationEngineResolver.unavailableReason(for: engine, kind: .signedOut, isConfigured: false, sarvamKey: nil) == "needs an openclicky account or your openai key")
        }
        #expect(DictationEngineResolver.unavailableReason(for: .offline, kind: .account, isConfigured: true, sarvamKey: nil) == nil)
    }

    @Test func teachingSkillsNeedsYourOwnKeyOnAnAccount() {
        #expect(AccountCapabilities(kind: .account).skillTeachingUnavailableHint == "teaching skills needs your own key for now.")
        #expect(AccountCapabilities(kind: .ownKeys).skillTeachingUnavailableHint == nil)
        #expect(SkillLibraryError.needsOwnKey("teaching skills needs your own key for now.").errorDescription == "teaching skills needs your own key for now.")
    }

    // MARK: C5 — polish of takes heard on this Mac

    @MainActor @Test func accountsPolishOfflineTakesUntilThePersonChooses() {
        let defaults = freshDefaults()
        let settings = DictationSettings(defaults: defaults)
        #expect(!settings.polishOfflineTakes)
        settings.applyAccountPolishDefault(kind: .account)
        #expect(settings.polishOfflineTakes)
        #expect(defaults.object(forKey: "dictation.polishOfflineTakes") == nil) // the default is not stored
        settings.applyAccountPolishDefault(kind: .ownKeys)
        #expect(!settings.polishOfflineTakes)
        settings.choosePolishOfflineTakes(false)
        settings.applyAccountPolishDefault(kind: .account)
        #expect(!settings.polishOfflineTakes) // an explicit choice is never overridden
    }

    @MainActor @Test func aStoredChoiceFromBeforeTheFlagCountsAsChosen() {
        let defaults = freshDefaults()
        defaults.set(false, forKey: "dictation.polishOfflineTakes")
        let settings = DictationSettings(defaults: defaults)
        settings.applyAccountPolishDefault(kind: .account)
        #expect(!settings.polishOfflineTakes)
    }

    // MARK: C8 / §9 — closed sign-up

    @Test func createAccountIsHiddenOnlyWhenTheBackendSaysClosed() {
        #expect(OpenClickyAuthSession.offersCreateAccount(accountsOpen: true))
        #expect(OpenClickyAuthSession.offersCreateAccount(accountsOpen: nil))
        #expect(!OpenClickyAuthSession.offersCreateAccount(accountsOpen: false))
    }
}
