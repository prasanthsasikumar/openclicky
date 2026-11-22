import Foundation
import Testing
@testable import OpenClicky

struct AccountAuthTests {
    let config = OpenClickyAuthSession.AuthConfig(supabaseUrl: "https://db.example", publishableKey: "pk", accountsOpen: true, confirmRedirectUrl: "https://api.example/auth/confirmed")

    @Test func oldBackendsWithoutTheFieldDecode() throws {
        let decoded = try JSONDecoder().decode(OpenClickyAuthSession.AuthConfig.self, from: Data(#"{"supabaseUrl":"u","publishableKey":"k"}"#.utf8))
        #expect(decoded.accountsOpen == nil)
        #expect(decoded.confirmRedirectUrl == nil)
    }

    @Test func aQuestionNothingCanAnswerStartsAGuestFirst() {
        let nothingCanAnswer = ProviderRoute.unavailable(ProviderRouter.needsAnthropicKeySentence)
        #expect(OpenClickyAuthSession.shouldStartGuestBeforeAsking(answerRoute: nothingCanAnswer, hasAccountToken: false))
        // Signed in already (a guest whose route went away is refused by the backend, not replaced).
        #expect(!OpenClickyAuthSession.shouldStartGuestBeforeAsking(answerRoute: nothingCanAnswer, hasAccountToken: true))
    }

    @Test func aKeyOrAPlanNeverGetsAGuest() {
        for route in [ProviderRoute.anthropicDirect, .answerProvider(.claudeCodePlan), .answerProvider(.openAI), .backend] {
            #expect(!OpenClickyAuthSession.shouldStartGuestBeforeAsking(answerRoute: route, hasAccountToken: false))
        }
    }
}
