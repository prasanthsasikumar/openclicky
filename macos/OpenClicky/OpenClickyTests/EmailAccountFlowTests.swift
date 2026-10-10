import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct EmailAccountFlowTests {
    private let device = String(repeating: "d", count: 64)
    private func json(_ s: String) -> Data { Data(s.utf8) }

    @Test func startPostsTheEmailAndDevice() throws {
        let request = try #require(OpenClickyAuthSession.startRequest(backendBaseURL: "https://api.example", email: "gran@example.com", device: device))
        #expect(request.url?.absoluteString == "https://api.example/auth/start")
        #expect(request.httpMethod == "POST")
        let body = try JSONSerialization.jsonObject(with: try #require(request.httpBody)) as? [String: String]
        #expect(body == ["email": "gran@example.com", "device": device])
    }

    @Test func eachAnswerBecomesOneOutcome() {
        let signedIn = OpenClickyAuthSession.outcome(status: 200, body: json(#"{"status":"signed_in","session":{"access_token":"a","refresh_token":"r","expires_in":3600},"confirmed":false}"#), email: "g@x.co")
        #expect(signedIn == .signedIn(.init(access_token: "a", refresh_token: "r", expires_in: 3600), confirmed: false))
        #expect(OpenClickyAuthSession.outcome(status: 200, body: json(#"{"status":"code_sent"}"#), email: "g@x.co") == .codeSent)
        #expect(OpenClickyAuthSession.outcome(status: 402, body: json(#"{"error":"accounts_full"}"#), email: "g@x.co") == .failed(AccountLimitError.accountsFull.message))
        #expect(OpenClickyAuthSession.outcome(status: 402, body: json(#"{"error":"device_limit"}"#), email: "g@x.co") == .failed(AccountLimitError.deviceLimit.message))
        #expect(OpenClickyAuthSession.outcome(status: 400, body: json(#"{"error":"bad_email"}"#), email: "g") == .failed("that doesn't look like an email address."))
        #expect(OpenClickyAuthSession.outcome(status: 401, body: json(#"{"error":"bad_code"}"#), email: "g@x.co") == .failed("that code didn't work — check it, or ask for a new one."))
        #expect(OpenClickyAuthSession.outcome(status: 429, body: json(#"{"error":"slow_down"}"#), email: "g@x.co") == .failed("too many tries — wait a few minutes and try again."))
        #expect(OpenClickyAuthSession.outcome(status: 502, body: json("{}"), email: "g@x.co") == .failed("couldn't reach openclicky right now — try again in a minute."))
        #expect(OpenClickyAuthSession.outcome(status: 200, body: json("not json"), email: "g@x.co") == .failed("couldn't reach openclicky right now — try again in a minute."))
    }

    @Test func codeAndResendRequests() throws {
        let code = try #require(OpenClickyAuthSession.codeRequest(backendBaseURL: "https://api.example", email: "g@x.co", code: "123456"))
        #expect(code.url?.path == "/auth/code")
        #expect((try JSONSerialization.jsonObject(with: try #require(code.httpBody)) as? [String: String]) == ["email": "g@x.co", "code": "123456"])
        let resend = try #require(OpenClickyAuthSession.resendRequest(backendBaseURL: "https://api.example", token: "tok"))
        #expect(resend.url?.path == "/auth/resend")
        #expect(resend.value(forHTTPHeaderField: "Authorization") == "Bearer tok")
    }

    @Test func onlyASendInFlightBlocksANewStart() {
        #expect(!OpenClickyAuthSession.canStart(from: .sending))
        #expect(OpenClickyAuthSession.canStart(from: .idle))
        #expect(OpenClickyAuthSession.canStart(from: .needsCode(email: "g@x.co")))
        #expect(OpenClickyAuthSession.canStart(from: .failed("no")))
    }

    @Test func aCancelledStartReturnsToIdle() {
        #expect(OpenClickyAuthSession.isCancellation(CancellationError()))
        #expect(OpenClickyAuthSession.stateAfterCancellation(.sending) == .idle)
        #expect(OpenClickyAuthSession.stateAfterCancellation(.needsCode(email: "g@x.co")) == .needsCode(email: "g@x.co"))
    }
}
