//
//  OpenClickyAuthSession.swift
//  OpenClicky
//
//  Sign in with an email and password (Supabase Auth, whose details the backend publishes at
//  GET /auth/config) and keep the session alive: the access token goes into shell.json as `token`
//  (what every backend request sends) together with the refresh token, and it is refreshed before
//  it expires. Invitees never touch shell.json by hand.
//

import Combine
import Foundation

@MainActor
final class OpenClickyAuthSession: ObservableObject {
    static let shared = OpenClickyAuthSession()

    @Published private(set) var isRefreshing = false
    @Published private(set) var lastErrorText: String?

    private var refreshTimer: Timer?
    /// Refresh this long before the access token expires (Supabase tokens last an hour).
    private let refreshLeadSeconds: TimeInterval = 10 * 60

    private struct AuthConfig: Decodable {
        let supabaseUrl: String
        let publishableKey: String
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

    func signIn(email: String, password: String) async -> Bool {
        lastErrorText = nil
        do {
            let config = try await authConfig()
            let session = try await requestToken(config: config, grant: "password", body: ["email": email, "password": password])
            store(session, config: config, email: session.user?.email ?? email)
            print("🔐 Signed in as \(session.user?.email ?? email)")
            return true
        } catch {
            lastErrorText = Self.describe(error)
            print("🔐 Sign-in failed: \(lastErrorText ?? "")")
            return false
        }
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
        let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/auth/config")!
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw NSError(domain: "OpenClickyAuth", code: 1, userInfo: [NSLocalizedDescriptionKey: "this backend has no sign-in configured"])
        }
        return try JSONDecoder().decode(AuthConfig.self, from: data)
    }

    private func requestToken(config: AuthConfig, grant: String, body: [String: String]) async throws -> TokenResponse {
        var request = URLRequest(url: URL(string: "\(config.supabaseUrl)/auth/v1/token?grant_type=\(grant)")!)
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
