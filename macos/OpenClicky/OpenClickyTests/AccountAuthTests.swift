import Foundation
import Testing
@testable import OpenClicky

struct AccountAuthTests {
    let config = OpenClickyAuthSession.AuthConfig(supabaseUrl: "https://db.example", publishableKey: "pk", accountsOpen: true, confirmRedirectUrl: "https://api.example/auth/confirmed")

    @Test func signUpGoesThroughTheBackend() throws {
        let request = try #require(OpenClickyAuthSession.signUpRequest(backendBaseURL: "https://api.example", email: "gran@example.com", password: "long-enough-1"))
        #expect(request.url?.absoluteString == "https://api.example/auth/signup")
        #expect(request.httpMethod == "POST")
        let body = try JSONSerialization.jsonObject(with: request.httpBody!) as! [String: String]
        #expect(body == ["email": "gran@example.com", "password": "long-enough-1"])
    }

    @Test func oldBackendsWithoutTheFieldDecode() throws {
        let decoded = try JSONDecoder().decode(OpenClickyAuthSession.AuthConfig.self, from: Data(#"{"supabaseUrl":"u","publishableKey":"k"}"#.utf8))
        #expect(decoded.accountsOpen == nil)
        #expect(decoded.confirmRedirectUrl == nil)
    }

    @MainActor @Test func waitingForConfirmationStopsWhenCancelled() async {
        let task = Task { @MainActor in
            await OpenClickyAuthSession.shared.waitForConfirmation(email: "a@b.c", password: "x", pollEvery: 0.01, timeout: 60)
        }
        task.cancel()
        #expect(await task.value == false)
    }
}
