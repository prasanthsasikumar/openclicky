//
//  OpenClickyAuthSession.swift
//  OpenClicky
//
//  Email-first accounts. The one question is the email (POST /auth/start): a new email is signed in
//  at once, unconfirmed, with a link mailed to it; an email that already has an account is mailed a
//  6-digit code (POST /auth/code). No passwords. The session is kept alive (Supabase Auth, whose
//  details the backend publishes at GET /auth/config): the access token goes into shell.json as
//  `token` (what every backend request sends) together with the refresh token, and it is refreshed
//  before it expires. Nobody has to touch shell.json by hand.
//

import AppKit
import Combine
import Foundation

@MainActor
final class OpenClickyAuthSession: ObservableObject {
    static let shared = OpenClickyAuthSession()

    @Published private(set) var isRefreshing = false
    @Published private(set) var lastErrorText: String?

    nonisolated enum EmailFlowState: Equatable { case idle, sending, checkingCode(email: String), needsCode(email: String), signedIn(confirmed: Bool), failed(String) }
    @Published private(set) var emailFlow: EmailFlowState = .idle
    /// Whether the backend takes new accounts (GET /auth/config); nil until it has answered.
    @Published private(set) var accountsOpen: Bool?

    private var refreshTimer: Timer?
    private var activationObserver: NSObjectProtocol?
    private var isCheckingOnActivate = false
    /// Refresh this long before the access token expires (Supabase tokens last an hour).
    private let refreshLeadSeconds: TimeInterval = 10 * 60

    struct AuthConfig: Decodable {
        let supabaseUrl: String
        let publishableKey: String
        /// Older backends omit these.
        let accountsOpen: Bool?
        let confirmRedirectUrl: String?
    }

    nonisolated struct TokenResponse: Decodable {
        let access_token: String
        let refresh_token: String
        let expires_in: Double
        let user: User?
        struct User: Decodable { let email: String? }
    }

    /// The email to keep after a refresh. A guest whose address is still unconfirmed comes back
    /// with `"email": ""` (the address sits in the pending change), so an empty reply keeps the
    /// stored one instead of making the app look signed out.
    nonisolated static func refreshedEmail(_ reply: TokenResponse, stored: String?) -> String {
        let replied = reply.user?.email?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return replied.isEmpty ? (stored ?? "") : replied
    }

    /// Email of the signed-in account, when the session came from a sign-in (not a pasted token).
    var accountEmail: String? { OpenClickyConfiguration.settings.accountEmail?.nonEmpty }
    var canRefresh: Bool { OpenClickyConfiguration.settings.refreshToken?.nonEmpty != nil }

    /// Refreshes a stale session at launch and keeps refreshing on a timer.
    func start() {
        refreshTimer?.invalidate()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 5 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refreshIfNeeded() }
        }
        if activationObserver == nil {
            activationObserver = NotificationCenter.default.addObserver(
                forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main
            ) { _ in
                Task { @MainActor in OpenClickyAuthSession.shared.checkConfirmationOnActivate() }
            }
        }
        Task {
            await refreshIfNeeded()
            guard let summary = try? await BillingStatusModel.fetchSummary() else { return }
            AccountProfileStore.shared.absorb(summary)
            if summary.confirmed == false { watchConfirmation() }
        }
    }

    nonisolated static let unreachableMessage = "couldn't reach openclicky right now — try again in a minute."

    nonisolated struct Session: Decodable, Equatable { let access_token: String; let refresh_token: String; let expires_in: Double }
    nonisolated enum StartOutcome: Equatable { case signedIn(Session, confirmed: Bool), codeSent, failed(String) }

    private nonisolated static func post(_ backendBaseURL: String, _ path: String, _ body: [String: String]?, token: String? = nil) -> URLRequest? {
        guard let url = URL(string: "\(backendBaseURL)\(path)") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body { request.httpBody = try? JSONSerialization.data(withJSONObject: body) }
        return request
    }
    nonisolated static func startRequest(backendBaseURL: String, email: String, device: String) -> URLRequest? {
        post(backendBaseURL, "/auth/start", ["email": email, "device": device])
    }
    nonisolated static func codeRequest(backendBaseURL: String, email: String, code: String) -> URLRequest? {
        post(backendBaseURL, "/auth/code", ["email": email, "code": code])
    }
    nonisolated static func resendRequest(backendBaseURL: String, token: String) -> URLRequest? {
        post(backendBaseURL, "/auth/resend", nil, token: token)
    }

    /// The backend's answer to /auth/start or /auth/code, as one thing the form can show.
    nonisolated static func outcome(status: Int, body: Data, email: String) -> StartOutcome {
        struct Reply: Decodable { let status: String?; let session: Session?; let confirmed: Bool?; let error: String? }
        let reply = try? JSONDecoder().decode(Reply.self, from: body)
        if (200..<300).contains(status) {
            if reply?.status == "code_sent" { return .codeSent }
            if let session = reply?.session { return .signedIn(session, confirmed: reply?.confirmed ?? false) }
            return .failed(unreachableMessage)
        }
        switch reply?.error {
        case "accounts_full": return .failed(AccountLimitError.accountsFull.message)
        case "device_limit": return .failed(AccountLimitError.deviceLimit.message)
        case "bad_email": return .failed("that doesn't look like an email address.")
        case "bad_code": return .failed("that code didn't work — check it, or ask for a new one.")
        case "slow_down": return .failed("too many tries — wait a few minutes and try again.")
        default: return .failed(unreachableMessage)
        }
    }

    nonisolated static func canStart(from state: EmailFlowState) -> Bool {
        switch state {
        case .sending, .checkingCode: return false
        default: return true
        }
    }

    /// A closed form cancels its task: a send in flight goes back to the form, anything else stays.
    nonisolated static func stateAfterCancellation(_ state: EmailFlowState) -> EmailFlowState {
        switch state {
        case .sending: return .idle
        case .checkingCode(let email): return .needsCode(email: email)
        default: return state
        }
    }

    /// A reply (or cancellation) may change the form only while it still shows the state this
    /// request put it in; a reset, sign-out or newer request has moved on and must not be undone.
    nonisolated static func shouldApply(current: EmailFlowState, inFlight: EmailFlowState) -> Bool { current == inFlight }

    nonisolated static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        return false
    }

    /// The email form is always offered (the backend answers accounts_full when sign-up is closed);
    /// when it is known closed, this note goes under the form. An unknown answer shows nothing.
    static func signUpClosedNote(accountsOpen: Bool?) -> String? {
        accountsOpen == false ? AccountLimitError.accountsFull.message : nil
    }

    /// Asks the backend whether it takes new accounts; a failure leaves the last answer.
    func refreshAccountsOpen() async {
        guard let config = try? await authConfig() else { return }
        accountsOpen = config.accountsOpen
    }

    /// Setup's one question: the email. A new email is signed in at once (unconfirmed); an email
    /// that already has an account gets a 6-digit code (`needsCode`).
    func start(email: String) async {
        guard Self.canStart(from: emailFlow) else { return }
        let trimmed = email.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard let device = DeviceIdentity.current,
              let request = Self.startRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: trimmed, device: device) else {
            lastErrorText = Self.unreachableMessage
            emailFlow = .failed(Self.unreachableMessage); return
        }
        // "send a new code" asks again from the code step; a failure or cancellation goes back to it.
        let resendingCode = emailFlow == .needsCode(email: trimmed)
        await run(request, email: trimmed, inFlight: .sending, returnTo: resendingCode ? emailFlow : nil)
    }

    func submitCode(_ code: String) async {
        guard case .needsCode(let email) = emailFlow,
              let request = Self.codeRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: email, code: code.filter(\.isNumber)) else { return }
        await run(request, email: email, inFlight: .checkingCode(email: email))
    }

    /// `returnTo`, when set, is where a failed or cancelled request goes back to (instead of the
    /// email step), with the sentence under the code field.
    private func run(_ request: URLRequest, email: String, inFlight: EmailFlowState, returnTo: EmailFlowState? = nil) async {
        lastErrorText = nil
        emailFlow = inFlight
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard Self.shouldApply(current: emailFlow, inFlight: inFlight) else { return }
            switch Self.outcome(status: (response as? HTTPURLResponse)?.statusCode ?? 0, body: data, email: email) {
            case .signedIn(let session, let confirmed):
                store(session, email: email)
                emailFlow = .signedIn(confirmed: confirmed)
                AccountProfileStore.shared.refresh()
                ShellSettingsRevision.shared.noteChanged()
                if !confirmed { watchConfirmation() }
            case .codeSent:
                emailFlow = .needsCode(email: email)
            case .failed(let message):
                // A wrong code leaves the code field up, with the sentence under it.
                lastErrorText = message
                emailFlow = returnTo ?? (inFlight == .sending ? .failed(message) : .needsCode(email: email))
            }
        } catch {
            guard Self.shouldApply(current: emailFlow, inFlight: inFlight) else { return }
            if Self.isCancellation(error) || Task.isCancelled {
                // Back to whatever the form showed before this send (the email step, or the code step).
                emailFlow = returnTo ?? Self.stateAfterCancellation(inFlight)
            } else {
                print("🔐 Account request failed: \(Self.describe(error))")
                if let returnTo { lastErrorText = Self.unreachableMessage; emailFlow = returnTo }
                else { emailFlow = .failed(Self.unreachableMessage) }
            }
        }
    }

    /// Mails the confirmation link again.
    func resendLink() async -> Bool {
        await refreshIfNeeded()
        let token = OpenClickyConfiguration.settings.token
        guard !token.isEmpty, let request = Self.resendRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, token: token) else { return false }
        let status = ((try? await URLSession.shared.data(for: request))?.1 as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(status) { watchConfirmation(); return true }
        return false
    }

    /// "Use a different email": drops even a pending code step (`forgetSettledFlow` keeps that one).
    func resetFlow() { emailFlow = .idle; lastErrorText = nil }

    /// A fresh form starts empty: a finished or failed attempt is forgotten, one in flight is kept.
    func forgetSettledFlow() {
        switch emailFlow {
        case .sending, .checkingCode, .needsCode: return
        default: emailFlow = .idle; lastErrorText = nil
        }
    }

    /// The running confirmation watch; nil once it has finished or been cancelled.
    private var confirmationWatch: Task<Void, Never>?
    private var confirmationWatchID = UUID()
    /// While the email is unconfirmed, asks /billing/me once a minute for 15 minutes, so the
    /// "check your inbox" note disappears soon after the link is clicked.
    func watchConfirmation() {
        confirmationWatch?.cancel()
        let id = UUID()
        confirmationWatchID = id
        confirmationWatch = Task { @MainActor in
            defer { if confirmationWatchID == id { confirmationWatch = nil } }
            for _ in 0..<15 {
                try? await Task.sleep(nanoseconds: 60 * 1_000_000_000)
                guard !Task.isCancelled else { return }
                if let summary = try? await BillingStatusModel.fetchSummary() {
                    guard !Task.isCancelled else { return }
                    AccountProfileStore.shared.absorb(summary)
                    if summary.confirmed != false { objectWillChange.send(); return }
                }
            }
        }
    }

    /// Coming back to the app (often from the browser where the link was clicked) asks
    /// /billing/me once, unless the email is already known confirmed or a check is running.
    nonisolated static func shouldCheckOnActivate(lastConfirmed: Bool?, checkRunning: Bool) -> Bool {
        lastConfirmed == false && !checkRunning
    }

    func checkConfirmationOnActivate() {
        guard Self.shouldCheckOnActivate(lastConfirmed: AccountProfileStore.shared.lastConfirmed,
                                         checkRunning: confirmationWatch != nil || isCheckingOnActivate) else { return }
        isCheckingOnActivate = true
        Task { @MainActor in
            defer { isCheckingOnActivate = false }
            await refreshIfNeeded()
            guard let summary = try? await BillingStatusModel.fetchSummary() else { return }
            AccountProfileStore.shared.absorb(summary)
            if summary.confirmed != false { objectWillChange.send() }
        }
    }

    func signOut() {
        OpenClickyConfiguration.update { settings in
            settings.token = ""
            settings.refreshToken = nil
            settings.tokenExpiresAt = nil
            settings.accountEmail = nil
        }
        confirmationWatch?.cancel()
        confirmationWatch = nil
        emailFlow = .idle
        lastErrorText = nil
        print("🔐 Signed out")
        AccountProfileStore.shared.refresh()
    }

    /// Refreshes when the stored access token is within `refreshLeadSeconds` of expiring.
    func refreshIfNeeded(force: Bool = false) async {
        let settings = OpenClickyConfiguration.settings
        guard let refreshToken = settings.refreshToken?.nonEmpty, !isRefreshing else { return }
        let expiresAt = settings.tokenExpiresAt ?? 0
        guard force || Date().timeIntervalSince1970 > expiresAt - refreshLeadSeconds else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        do {
            let config = try await authConfig()
            let session = try await requestToken(config: config, grant: "refresh_token", body: ["refresh_token": refreshToken])
            store(Session(access_token: session.access_token, refresh_token: session.refresh_token, expires_in: session.expires_in),
                  email: Self.refreshedEmail(session, stored: settings.accountEmail))
            print("🔐 Session refreshed")
        } catch {
            lastErrorText = Self.describe(error)
            print("🔐 Session refresh failed: \(lastErrorText ?? "")")
        }
    }

    // MARK: - Supabase calls

    private func authConfig() async throws -> AuthConfig {
        // backendBaseURL is user-configurable (shell.json or an environment override), not a
        // compile-time literal, so a malformed value must throw instead of crashing the app.
        guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/auth/config") else {
            throw NSError(domain: "OpenClickyAuth", code: 3, userInfo: [NSLocalizedDescriptionKey: "backend URL is invalid"])
        }
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NSError(domain: "OpenClickyAuth", code: 1, userInfo: [NSLocalizedDescriptionKey: "this backend has no sign-in configured"])
        }
        return try JSONDecoder().decode(AuthConfig.self, from: data)
    }

    private func requestToken(config: AuthConfig, grant: String, body: [String: String]) async throws -> TokenResponse {
        // `config.supabaseUrl` comes straight from the backend's /auth/config JSON response — a
        // malformed or hostile value must throw instead of crashing the app.
        guard let tokenURL = URL(string: "\(config.supabaseUrl)/auth/v1/token?grant_type=\(grant)") else {
            throw NSError(domain: "OpenClickyAuth", code: 4, userInfo: [NSLocalizedDescriptionKey: "sign-in backend returned an invalid Supabase URL"])
        }
        var request = URLRequest(url: tokenURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(config.publishableKey, forHTTPHeaderField: "apikey")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            let json = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
            let message = (json["error_description"] as? String) ?? (json["msg"] as? String) ?? (json["error"] as? String) ?? "sign-in rejected"
            throw NSError(domain: "OpenClickyAuth", code: 2, userInfo: [NSLocalizedDescriptionKey: message])
        }
        return try JSONDecoder().decode(TokenResponse.self, from: data)
    }

    private func store(_ session: Session, email: String) {
        OpenClickyConfiguration.update { settings in
            settings.token = session.access_token
            settings.refreshToken = session.refresh_token
            settings.tokenExpiresAt = Date().timeIntervalSince1970 + session.expires_in
            settings.accountEmail = email
        }
    }

    private static func describe(_ error: Error) -> String {
        (error as NSError).userInfo[NSLocalizedDescriptionKey] as? String ?? error.localizedDescription
    }
}

private extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}
