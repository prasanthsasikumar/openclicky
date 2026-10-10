import Foundation
import Testing
@testable import OpenClicky

struct AccountTakePolisherTests {
    private func response(_ status: Int) -> HTTPURLResponse {
        HTTPURLResponse(url: URL(string: "https://x/v1/polish")!, statusCode: status, httpVersion: nil, headerFields: nil)!
    }

    @Test func sendsPurposeSystemAndTextAndNoModel() async throws {
        var captured: URLRequest?
        let polisher = AccountTakePolisher(purpose: "polish", send: { request in
            captured = request
            return (Data(#"{"text":"See you at seven."}"#.utf8), response(200))
        })
        let text = try await polisher.polish(system: "fix punctuation", user: "see you at seven")
        #expect(text == "See you at seven.")
        let body = try JSONSerialization.jsonObject(with: captured!.httpBody!) as! [String: Any]
        #expect(body["purpose"] as? String == "polish")
        #expect(body["text"] as? String == "see you at seven")
        #expect(body["model"] == nil)
        #expect(captured!.url!.path.hasSuffix("/v1/polish"))
    }

    @Test func polish402FallsBackToLocalFormatting() async {
        let polisher = AccountTakePolisher(purpose: "polish", send: { _ in
            (Data(#"{"error":"daily_limit"}"#.utf8), response(402))
        })
        await #expect(throws: AccountPolishError.limit(.dailyLimit)) {
            try await polisher.polish(system: "s", user: "words")
        }
    }

    @Test func polishErrorsReadAsPlainSentences() {
        #expect(AccountPolishError.limit(.dailyLimit).localizedDescription == AccountLimitError.dailyLimit.message)
        #expect(AccountPolishError.unavailable(0).localizedDescription == "couldn't reach openclicky right now.")
        #expect(AccountPolishError.unavailable(503).localizedDescription == AccountLimitError.serviceTroubleMessage)
        #expect(AccountPolishError.unavailable(500).localizedDescription == "couldn't reach openclicky right now.")
    }
}
