import Foundation
import Testing
@testable import OpenClicky

struct AccountCapabilitiesTests {
    @Test func decodesTheNewBillingSummary() throws {
        let json = #"{"byok":false,"spentMonthUsd":4.5,"monthlyLimitUsd":10,"spentTodayUsd":0.2,"dailyLimitUsd":2,"ttsCharsMonth":300,"ttsCharsLimit":20000,"monthEnd":"2026-11-01T00:00:00.000Z","dayEnd":"2026-10-09T00:00:00.000Z","budgetExhausted":false,"blocked":false}"#
        let summary = try JSONDecoder().decode(BillingSummary.self, from: Data(json.utf8))
        #expect(summary.fractionUsed == 0.45)
        #expect(summary.level == .plenty)
    }

    @Test func runningLowAtEightyPercentAndUsedUpAtTheLimit() throws {
        func summary(_ spent: Double, exhausted: Bool = false) -> BillingSummary {
            BillingSummary(byok: false, spentMonthUsd: spent, monthlyLimitUsd: 10, spentTodayUsd: 0, dailyLimitUsd: 2, ttsCharsMonth: 0, ttsCharsLimit: 20000, monthEnd: "", dayEnd: "", budgetExhausted: exhausted, blocked: false)
        }
        #expect(summary(8).level == .runningLow)
        #expect(summary(10).level == .usedUp)
        #expect(summary(1, exhausted: true).level == .usedUp)
    }

    @Test func accountProfileRoutesEveryLane() {
        let account = AccountCapabilities(kind: .account)
        #expect(!account.usesRealtime)
        #expect(!account.usesAgent)
        #expect(account.hearsOnDevice)
        #expect(account.polishPath == "/v1/polish")
        let own = AccountCapabilities(kind: .ownKeys)
        #expect(own.usesRealtime)
        #expect(own.polishPath == "/v1/chat/completions")
    }

    @Test func limitErrorsBecomePlainSentences() {
        let body = Data(#"{"error":"daily_limit","resets_at":"2026-10-09T00:00:00Z"}"#.utf8)
        let error = AccountLimitError.from(status: 402, body: body)
        #expect(error == .dailyLimit)
        #expect(error?.message == "you've used today's free allowance — it comes back tomorrow. dictation still works.")
        #expect(AccountLimitError.from(status: 500, body: body) == nil)
    }
}
