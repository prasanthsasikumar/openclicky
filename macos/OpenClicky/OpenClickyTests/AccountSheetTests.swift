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

    @Test func aSecondSignUpWaitsForTheFirst() {
        #expect(!OpenClickyAuthSession.canStartSignUp(from: .sending))
        #expect(!OpenClickyAuthSession.canStartSignUp(from: .awaitingConfirmation(email: "gran@example.com")))
        #expect(OpenClickyAuthSession.canStartSignUp(from: .idle))
        #expect(OpenClickyAuthSession.canStartSignUp(from: .failed("no")))
        #expect(OpenClickyAuthSession.canStartSignUp(from: .full))
    }

    @Test func aClosedSheetIsACancellationNotAFailure() {
        #expect(OpenClickyAuthSession.isCancellation(CancellationError()))
        #expect(OpenClickyAuthSession.isCancellation(URLError(.cancelled)))
        #expect(!OpenClickyAuthSession.isCancellation(URLError(.notConnectedToInternet)))
    }

    @Test func createNeedsEightCharactersAndSignInAnyPassword() {
        #expect(!AccountSheet.canSubmit(mode: .create, email: "gran@example.com", password: "1234567"))
        #expect(AccountSheet.canSubmit(mode: .create, email: "gran@example.com", password: "12345678"))
        #expect(AccountSheet.canSubmit(mode: .signIn, email: "gran@example.com", password: "123"))
        #expect(!AccountSheet.canSubmit(mode: .signIn, email: "gran", password: "12345678"))
    }

    @Test func signInFailuresReadAsOnePlainSentence() {
        #expect(AccountSheet.friendlySignInMessage("Invalid login credentials") == "that email and password don't match — try again, or reset the password.")
        #expect(AccountSheet.friendlySignInMessage("Email not confirmed") == "this email isn't confirmed yet — tap the link we sent you first.")
        #expect(AccountSheet.friendlySignInMessage("The Internet connection appears to be offline.") == "couldn't sign in right now — try again in a minute.")
        #expect(AccountSheet.friendlySignInMessage(nil) == "couldn't sign in right now — try again in a minute.")
    }

    @Test func theFullAccountsSentenceIsNeverEmpty() {
        #expect(!AccountLimitError.accountsFull.message.isEmpty)
    }
}
