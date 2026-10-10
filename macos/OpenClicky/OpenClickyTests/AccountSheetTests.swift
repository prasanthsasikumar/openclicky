import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct AccountSheetTests {
    private func summary(
        spentMonth: Double = 1, spentToday: Double = 0, budgetExhausted: Bool = false, blocked: Bool = false,
        monthEnd: String = "2026-11-01T00:00:00.000Z", byok: Bool = false
    ) -> BillingSummary {
        BillingSummary(byok: byok, spentMonthUsd: spentMonth, monthlyLimitUsd: 10, spentTodayUsd: spentToday, dailyLimitUsd: 2,
                       ttsCharsMonth: 1234, ttsCharsLimit: 20000, monthEnd: monthEnd, dayEnd: "2026-10-11T00:00:00.000Z",
                       budgetExhausted: budgetExhausted, blocked: blocked)
    }

    @Test func eachAllowanceStandingHasItsOwnSentence() {
        #expect(summary().allowanceSentence(resetDay: "nov 1") == "plenty left this month · resets on nov 1")
        #expect(summary(spentMonth: 8.5).allowanceSentence(resetDay: "nov 1") == "running low this month · resets on nov 1")
        #expect(summary(spentMonth: 10).allowanceSentence(resetDay: "nov 1") == "this month's free allowance is used — it comes back on nov 1")
        #expect(summary(spentToday: 2).allowanceSentence(resetDay: "nov 1") == "today's free allowance is used — it comes back tomorrow")
        #expect(summary(budgetExhausted: true).allowanceSentence(resetDay: "nov 1") == "the free allowance is used up for now")
        #expect(summary(blocked: true).allowanceSentence(resetDay: "nov 1") == "this account is paused — dictation still works")
        #expect(summary().allowanceSentence(resetDay: nil) == "plenty left this month · resets on the 1st")
    }

    @Test func onlyTodayOrTheSharedBudgetNeverSaysThisMonth() {
        #expect(summary(spentMonth: 3, spentToday: 2.4).allowanceStanding == .usedUpToday)
        #expect(summary(spentMonth: 3, budgetExhausted: true).allowanceStanding == .sharedBudgetUsedUp)
        // The month running out is the longer wait, so it is the one said.
        #expect(summary(spentMonth: 10, spentToday: 2, budgetExhausted: true).allowanceStanding == .usedUpThisMonth)
    }

    @Test func theResetDayIsReadInUTC() {
        let english = Locale(identifier: "en_US")
        #expect(summary().monthResetDay(locale: english) == "nov 1")
        #expect(summary(monthEnd: "2026-11-01T00:00:00Z").monthResetDay(locale: english) == "nov 1")
        #expect(summary(monthEnd: "soon").monthResetDay(locale: english) == nil)
    }

    @Test func detailsSpellOutTheNumbers() {
        let line = summary(spentMonth: 4.5, spentToday: 0.2).usageDetails(locale: Locale(identifier: "en_US"))
        #expect(line == "$4.50 of $10 this month · $0.20 of $2 today · 1,234 spoken characters of 20,000")
    }

    @Test func theIslandShowsHowMuchIsUsed() {
        #expect(NotchSettingsView.planDescription(kind: .account, summary: summary(spentMonth: 4.5)) == "account · 45% used")
        #expect(NotchSettingsView.planDescription(kind: .account, summary: nil) == "account")
        #expect(NotchSettingsView.planDescription(kind: .ownKeys, summary: nil) == "your own key")
        #expect(NotchSettingsView.planDescription(kind: .signedOut, summary: nil) == "not signed in")
    }

    @Test func aClosedSheetIsACancellationNotAFailure() {
        #expect(OpenClickyAuthSession.isCancellation(CancellationError()))
        #expect(OpenClickyAuthSession.isCancellation(URLError(.cancelled)))
        #expect(!OpenClickyAuthSession.isCancellation(URLError(.notConnectedToInternet)))
    }

    @Test func theFullAccountsSentenceIsNeverEmpty() {
        #expect(!AccountLimitError.accountsFull.message.isEmpty)
    }

    @Test func anUnconfirmedAccountSaysCheckYourInbox() {
        var guest = summary(spentMonth: 0.25)
        guest.confirmed = false; guest.guestSpentUsd = 0.25; guest.guestLimitUsd = 1
        #expect(guest.allowanceStanding == .unconfirmed)
        #expect(guest.allowanceSentence(resetDay: "nov 1") == "confirm your email to unlock the full free allowance · 25% of the starter used")
        guest.guestSpentUsd = 1
        #expect(guest.allowanceSentence(resetDay: "nov 1") == "the starter allowance is used — confirm your email to keep going")
    }

    @Test func confirmEmailAndDeviceLimitHaveSentences() {
        #expect(AccountLimitError.confirmEmail.message == "confirm your email to keep going — we sent you a link.")
        #expect(AccountLimitError.deviceLimit.message == "this mac already has two openclicky accounts — sign in with one of them.")
        #expect(AccountLimitError.from(status: 402, body: Data(#"{"error":"confirm_email"}"#.utf8)) == .confirmEmail)
    }
}

@MainActor
struct AccountWorkSlotTests {
    @Test func twoRapidSubmitsStartOneSignUp() async {
        let slot = AccountWorkSlot()
        var started = 0
        let first = slot.start { started += 1; try? await Task.sleep(nanoseconds: 50_000_000) }
        let second = slot.start { started += 1 }
        #expect(first)
        #expect(!second)
        #expect(slot.isRunning)
        while slot.isRunning { try? await Task.sleep(nanoseconds: 5_000_000) }
        #expect(started == 1)
        #expect(slot.start { started += 1 })
    }

    @Test func greenOnlyWhenNothingIsUsedUp() {
        let todayUsedUp = BillingSummary(byok: false, spentMonthUsd: 1, monthlyLimitUsd: 10, spentTodayUsd: 2, dailyLimitUsd: 2, ttsCharsMonth: 0, ttsCharsLimit: 20000, monthEnd: "", dayEnd: "", budgetExhausted: false, blocked: false)
        #expect(todayUsedUp.allowanceStanding != .plenty)
    }

    @Test func theEmailFormNeedsAnAddressAndTheCodeSixDigits() {
        #expect(!EmailAccountForm.canSubmitEmail("gran"))
        #expect(EmailAccountForm.canSubmitEmail(" gran@example.com "))
        #expect(!EmailAccountForm.canSubmitCode("12345"))
        #expect(EmailAccountForm.canSubmitCode("123 456"))
        #expect(!EmailAccountForm.canSubmitCode("12a456"))
    }

    /// API-key fields (Settings, onboarding's Sarvam step) legitimately use SecureField, so only the
    /// account files are checked.
    @Test func noPasswordFieldIsLeftInTheAccountUI() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("OpenClicky")
        let names: Set<String> = ["AccountSheet.swift", "EmailAccountForm.swift", "BillingStatus.swift", "SettingsAccountPage.swift"]
        let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)!.compactMap { $0 as? URL }.filter { names.contains($0.lastPathComponent) }
        #expect(files.count == names.count)
        for file in files {
            let text = try String(contentsOf: file, encoding: .utf8)
            #expect(!text.contains("SecureField") || file.lastPathComponent == "SettingsAccountPage.swift", "\(file.lastPathComponent) still has a password field")
            #expect(!text.lowercased().contains("forgot password"), "\(file.lastPathComponent) still offers a password reset")
            #expect(!text.contains("isSecure: true"), "\(file.lastPathComponent) still has a secure credential field")
        }
    }
}
