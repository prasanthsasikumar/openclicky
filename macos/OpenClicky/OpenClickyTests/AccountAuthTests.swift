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
}
