//
//  OpenClickyAuthSession.swift
//  OpenClicky
//
//  Sign in with an email and password (Supabase Auth, whose details the backend publishes at
//  GET /auth/config) and keep the session alive: the access token goes into shell.json as `token`
//  (what every backend request sends) together with the refresh token, and it is refreshed before
//  it expires. Nobody has to touch shell.json by hand.
//

import Combine
import Foundation

@MainActor
final class OpenClickyAuthSession: ObservableObject {
    static let shared = OpenClickyAuthSession()

    @Published private(set) var isRefreshing = false
    @Published private(set) var lastErrorText: String?

    enum SignUpState: Equatable { case idle, sending, awaitingConfirmation(email: String), signedIn, failed(String), full }
    @Published private(set) var signUpState: SignUpState = .idle
    /// Whether the backend takes new accounts (GET /auth/config); nil until it has answered.
    @Published private(set) var accountsOpen: Bool?

    private var refreshTimer: Timer?
    /// Refresh this long before the access token expires (Supabase tokens last an hour).
    private let refreshLeadSeconds: TimeInterval = 10 * 60

    struct AuthConfig: Decodable {
        let supabaseUrl: String
        let publishableKey: String
        /// Older backends omit these.
        let accountsOpen: Bool?
        let confirmRedirectUrl: String?
        /// The backend's /auth/reset page, where a password-reset link lands.
        var resetRedirectUrl: String? = nil
    }

    private struct TokenResponse: Decodable {
        let access_token: String
        let refresh_token: String
        let expires_in: Double
        let user: User?
        struct User: Decodable { let email: String? }
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
        Task { await refreshIfNeeded() }
    }

    /// `logsFailure` is false while waiting for a confirmation link, where "Email not confirmed"
    /// every five seconds is expected and would only fill the log.
    func signIn(email: String, password: String, logsFailure: Bool = true) async -> Bool {
        lastErrorText = nil
        do {
            let config = try await authConfig()
            let session = try await requestToken(config: config, grant: "password", body: ["email": email, "password": password])
            store(session, config: config, email: session.user?.email ?? email)
            print("🔐 Signed in as \(session.user?.email ?? email)")
            AccountProfileStore.shared.refresh()
            return true
        } catch {
            lastErrorText = Self.describe(error)
            if logsFailure { print("🔐 Sign-in failed: \(lastErrorText ?? "")") }
            return false
        }
    }

    nonisolated static func signUpRequest(backendBaseURL: String, email: String, password: String) -> URLRequest? {
        guard let url = URL(string: "\(backendBaseURL)/auth/signup") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["email": email, "password": password])
        return request
    }

    /// "create a free account" is offered unless the backend has said sign-up is closed; an
    /// unknown answer keeps it (the sheet then says when accounts are full).
    nonisolated static func offersCreateAccount(accountsOpen: Bool?) -> Bool { accountsOpen != false }

    /// Asks the backend whether it takes new accounts; a failure leaves the last answer.
    func refreshAccountsOpen() async {
        guard let config = try? await authConfig() else { return }
        accountsOpen = config.accountsOpen
    }

    /// Supabase's recover call, with the link sent to the backend's reset page when it names one.
    nonisolated static func recoverRequest(config: AuthConfig, email: String) -> URLRequest? {
        guard var components = URLComponents(string: "\(config.supabaseUrl)/auth/v1/recover") else { return nil }
        if let redirect = config.resetRedirectUrl, !redirect.isEmpty {
            components.queryItems = [URLQueryItem(name: "redirect_to", value: redirect)]
        }
        guard let url = components.url else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(config.publishableKey, forHTTPHeaderField: "apikey")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["email": email])
        return request
    }

    /// A second tap while a sign-up is being sent or waiting for its link must not start a second
    /// request and a second poll.
    nonisolated static func canStartSignUp(from state: SignUpState) -> Bool {
        switch state {
        case .sending, .awaitingConfirmation: return false
        case .idle, .signedIn, .failed, .full: return true
        }
    }

    /// A sign-up stopped because its task was cancelled (the sheet closed), not because it failed.
    nonisolated static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        return false
    }

    /// Creates the account through the backend (it owns the account cap); Supabase then emails a
    /// confirmation link. Polls a password sign-in until the link has been tapped, so the person
    /// never has to come back and type. Run it from a task the caller cancels when the person
    /// walks away: a cancelled sign-up goes back to `.idle`.
    func signUp(email: String, password: String) async {
        // Checked and flipped before the first await, so two calls on the main actor can never both pass.
        guard Self.canStartSignUp(from: signUpState) else { return }
        signUpState = .sending
        do {
            let config = try await authConfig()
            accountsOpen = config.accountsOpen
            guard config.accountsOpen != false else { signUpState = .full; return }
            guard let request = Self.signUpRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: email, password: password) else {
                signUpState = .failed("creating an account isn't available right now.")
                return
            }
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200..<300).contains(status) else {
                let json = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
                let error = json["error"] as? String ?? ""
                if error == "accounts_full" {
                    signUpState = .full
                } else if status == 404 {
                    signUpState = .failed("creating an account isn't available right now.")
                } else {
                    signUpState = .failed(error.isEmpty ? "couldn't create the account right now — try again in a minute." : error)
                }
                return
            }
            signUpState = .awaitingConfirmation(email: email)
            if await waitForConfirmation(email: email, password: password) {
                signUpState = .signedIn
            } else if Task.isCancelled {
                signUpState = .idle
            } else {
                signUpState = .failed("the link wasn't opened in time — sign in once you have tapped it.")
            }
        } catch {
            if Self.isCancellation(error) || Task.isCancelled {
                signUpState = .idle
            } else {
                // The underlying error ("The Internet connection appears to be offline.", a JSON
                // decoding failure) is for the log, not for the person signing up.
                print("🔐 Sign-up failed: \(Self.describe(error))")
                signUpState = .failed("couldn't reach openclicky right now — try again in a minute.")
            }
        }
    }

    /// A fresh sheet starts from the form: a finished, failed or full attempt from before is
    /// forgotten, one still in flight is left alone.
    func forgetSettledSignUp() {
        if Self.canStartSignUp(from: signUpState) { signUpState = .idle }
    }

    /// Polls a password sign-in until the confirmation link has been tapped. Returns false on
    /// timeout or cancellation. The "Email not confirmed" failures along the way are expected, so
    /// they are not left in `lastErrorText`.
    func waitForConfirmation(email: String, password: String, pollEvery seconds: Double = 5, timeout: Double = 15 * 60) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline, !Task.isCancelled {
            if await signIn(email: email, password: password, logsFailure: false) { return true }
            lastErrorText = nil
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
        return false
    }

    func recover(email: String) async -> Bool {
        guard let config = try? await authConfig(), let request = Self.recoverRequest(config: config, email: email) else { return false }
        let status = ((try? await URLSession.shared.data(for: request))?.1 as? HTTPURLResponse)?.statusCode ?? 0
        return (200..<300).contains(status)
    }

    func signOut() {
        OpenClickyConfiguration.update { settings in
            settings.token = ""
            settings.refreshToken = nil
            settings.tokenExpiresAt = nil
            settings.accountEmail = nil
        }
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
            store(session, config: config, email: session.user?.email ?? settings.accountEmail ?? "")
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

    private func store(_ session: TokenResponse, config: AuthConfig, email: String) {
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
