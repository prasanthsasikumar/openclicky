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

    nonisolated static let unreachableMessage = "couldn't reach openclicky right now, try again in a minute."

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
    /// `device` is the same hash /auth/start sends, so the backend can tie the code sign-in to this Mac.
    nonisolated static func codeRequest(backendBaseURL: String, email: String, code: String, device: String?) -> URLRequest? {
        var body = ["email": email, "code": code]
        if let device { body["device"] = device }
        return post(backendBaseURL, "/auth/code", body)
    }
    nonisolated static func resendRequest(backendBaseURL: String, token: String) -> URLRequest? {
        post(backendBaseURL, "/auth/resend", nil, token: token)
    }
    /// A guest types the 6-digit code from the confirmation email; the guest's own token says who.
    nonisolated static func confirmRequest(backendBaseURL: String, token: String, code: String) -> URLRequest? {
        post(backendBaseURL, "/auth/confirm", ["code": code], token: token)
    }

    nonisolated static let badCodeMessage = "that code didn't work. check it, or ask for a new one."
    nonisolated static let accountsUnavailableMessage = "openclicky's accounts are having trouble right now, try again in a few minutes."

    /// "wait about 3 minutes" when the backend said how long (Retry-After, in seconds).
    nonisolated static func slowDownMessage(retryAfterSeconds: Int?) -> String {
        guard let retryAfterSeconds, retryAfterSeconds > 0 else { return "too many tries, wait a few minutes and try again." }
        let minutes = max(1, Int((Double(retryAfterSeconds) / 60).rounded(.up)))
        return "too many tries, wait about \(minutes) minute\(minutes == 1 ? "" : "s") and try again."
    }

    /// Retry-After as whole seconds (the HTTP-date form is not used by the backend).
    nonisolated static func retryAfterSeconds(of response: URLResponse?) -> Int? {
        guard let value = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Retry-After") else { return nil }
        return Int(value.trimmingCharacters(in: .whitespaces))
    }

    /// The sentence for a refused /auth/start, /auth/code or /auth/confirm.
    nonisolated static func failureMessage(status: Int, error: String?, retryAfterSeconds: Int?) -> String {
        switch error {
        case "accounts_full": return AccountLimitError.accountsFull.message
        case "device_limit": return AccountLimitError.deviceLimit.message
        case "bad_email": return "that doesn't look like an email address."
        case "bad_code": return badCodeMessage
        case "slow_down": return slowDownMessage(retryAfterSeconds: retryAfterSeconds)
        // The backend was reached; its account store was not. "can't reach" would send people to their wifi.
        case "accounts_unavailable": return accountsUnavailableMessage
        default: return status == 429 ? slowDownMessage(retryAfterSeconds: retryAfterSeconds) : unreachableMessage
        }
    }

    private nonisolated struct Reply: Decodable { let status: String?; let session: Session?; let confirmed: Bool?; let error: String? }

    /// The backend's answer to /auth/start or /auth/code, as one thing the form can show.
    nonisolated static func outcome(status: Int, body: Data, email: String, retryAfterSeconds: Int? = nil) -> StartOutcome {
        let reply = try? JSONDecoder().decode(Reply.self, from: body)
        if (200..<300).contains(status) {
            if reply?.status == "code_sent" { return .codeSent }
            if let session = reply?.session { return .signedIn(session, confirmed: reply?.confirmed ?? false) }
            return .failed(unreachableMessage)
        }
        return .failed(failureMessage(status: status, error: reply?.error, retryAfterSeconds: retryAfterSeconds))
    }

    nonisolated enum ConfirmOutcome: Equatable {
        /// Confirmed: the backend hands back a fresh session for the confirmed account.
        case signedIn(Session)
        case alreadyConfirmed
        /// An older backend without /auth/confirm: the link in the email is the way.
        case notSupported
        case failed(String)
    }

    nonisolated static func confirmOutcome(status: Int, body: Data, retryAfterSeconds: Int? = nil) -> ConfirmOutcome {
        let reply = try? JSONDecoder().decode(Reply.self, from: body)
        if (200..<300).contains(status) {
            if reply?.status == "already_confirmed" { return .alreadyConfirmed }
            if let session = reply?.session { return .signedIn(session) }
            return .failed(unreachableMessage)
        }
        if status == 404 { return .notSupported }
        return .failed(failureMessage(status: status, error: reply?.error, retryAfterSeconds: retryAfterSeconds))
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

    /// Try it with no email: a guest made from this Mac's device hash alone, on OpenClicky's
    /// credits (the allowance is per Mac, so reinstalling does not refill it). The email can come
    /// later through `start(email:)`, whose guest replaces this one. False when the backend wants
    /// an email after all (its guest room is full) or cannot be reached.
    @discardableResult
    func startGuestWithoutEmail() async -> Bool {
        guard !OpenClickyConfiguration.isConfigured, let device = DeviceIdentity.current,
              let request = Self.post(OpenClickyConfiguration.backendBaseURL, "/auth/guest", ["device": device]) else { return false }
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard case .signedIn(let session, _) = Self.outcome(status: status, body: data, email: "") else {
                AppLog.append("sign-in: no guest without an email (status \(status))")
                return false
            }
            store(session, email: "")
            AccountProfileStore.shared.refresh()
            ShellSettingsRevision.shared.noteChanged()
            AppLog.append("sign-in: started a guest with no email")
            return true
        } catch {
            AppLog.append("sign-in: guest request failed: \(Self.describe(error))")
            return false
        }
    }

    /// Whether a question should start a guest first: nothing can answer it (no own key, no plan,
    /// no answer source) and nobody is signed in. Someone who answers through a key or a plan has a
    /// route and never gets a guest.
    nonisolated static func shouldStartGuestBeforeAsking(answerRoute: ProviderRoute, hasAccountToken: Bool) -> Bool {
        guard !hasAccountToken, case .unavailable = answerRoute else { return false }
        return true
    }

    /// A question with nothing to answer it starts a guest first, the one the welcome tour makes, so
    /// someone who skipped or finished the tour without signing in gets an answer instead of "add
    /// your email". When the backend wants an email after all (a guest that already ran out on this
    /// Mac, the guest room full) the question goes on and is refused as before.
    func startGuestIfNothingCanAnswer() async {
        guard Self.shouldStartGuestBeforeAsking(answerRoute: ProviderRouter.current(for: .claude),
                                                hasAccountToken: OpenClickyConfiguration.hasAccountToken) else { return }
        AppLog.append("sign-in: nothing can answer this question; starting a guest first")
        await startGuestWithoutEmail()
    }

    /// Signed in as a guest that has not given an email yet.
    var isGuestWithoutEmail: Bool { OpenClickyConfiguration.hasAccountToken && accountEmail == nil }

    func submitCode(_ code: String) async {
        guard case .needsCode(let email) = emailFlow,
              let request = Self.codeRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: email, code: code.filter(\.isNumber), device: DeviceIdentity.current) else { return }
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
            switch Self.outcome(status: (response as? HTTPURLResponse)?.statusCode ?? 0, body: data, email: email, retryAfterSeconds: Self.retryAfterSeconds(of: response)) {
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
                AppLog.append("sign-in: account request failed: \(Self.describe(error))")
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
        let answer = try? await BackendSignIn.send(request) { try await URLSession.shared.data(for: $0) }
        let status = (answer?.1 as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(status) { watchConfirmation(); return true }
        return false
    }

    /// False once the backend answered 404 to /auth/confirm (an older backend): the code field
    /// goes away and the link in the email stays the way to confirm.
    @Published private(set) var confirmByCodeSupported = true

    /// A guest confirms with the 6-digit code from the email instead of the link. The link
    /// confirmed whoever had typed the address; the code proves this Mac's person reads that
    /// inbox. Returns the sentence to show under the field, nil when confirmed.
    func confirmEmail(code: String) async -> String? {
        await refreshIfNeeded()
        let token = OpenClickyConfiguration.settings.token
        let digits = code.filter(\.isNumber)
        guard !token.isEmpty, let request = Self.confirmRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, token: token, code: digits) else {
            return Self.unreachableMessage
        }
        // Not through BackendSignIn: this route's 401 is a wrong code, not a dead sign-in.
        let answer: (Data, URLResponse)
        do {
            answer = try await URLSession.shared.data(for: request)
        } catch {
            AppLog.append("sign-in: confirm by code failed: \(Self.describe(error))")
            return Self.isCancellation(error) ? nil : Self.unreachableMessage
        }
        let outcome = Self.confirmOutcome(status: (answer.1 as? HTTPURLResponse)?.statusCode ?? 0, body: answer.0,
                                          retryAfterSeconds: Self.retryAfterSeconds(of: answer.1))
        switch outcome {
        case .signedIn(let session):
            // The confirmed account's own session replaces the guest's.
            store(session, email: accountEmail ?? "")
            confirmationWatch?.cancel()
            confirmationWatch = nil
            emailFlow = .signedIn(confirmed: true)
            AccountProfileStore.shared.refresh()
            ShellSettingsRevision.shared.noteChanged()
            AppLog.append("sign-in: email confirmed by code")
            return nil
        case .alreadyConfirmed:
            AccountProfileStore.shared.refresh()
            objectWillChange.send()
            return nil
        case .notSupported:
            confirmByCodeSupported = false
            return nil
        case .failed(let message):
            return message
        }
    }

    /// "Use a different email": drops even a pending code step (`forgetSettledFlow` keeps that one).
    func resetFlow() { emailFlow = .idle; lastErrorText = nil }

    /// A fresh form starts empty: a finished or failed attempt is forgotten, one in flight is kept.
    /// So is "your sign-in expired": the form is where the person goes to find out why they were
    /// signed out, and opening it must not wipe the answer.
    func forgetSettledFlow() {
        guard !Self.flowSurvivesFreshForm(emailFlow) else { return }
        emailFlow = .idle; lastErrorText = nil
    }

    nonisolated static func flowSurvivesFreshForm(_ state: EmailFlowState) -> Bool {
        switch state {
        case .sending, .checkingCode, .needsCode: return true
        case .failed(let message): return message == sessionExpiredMessage
        default: return false
        }
    }

    /// The expiry sentence while the sign-out it explains is the last thing that happened.
    var sessionExpiredNotice: String? {
        emailFlow == .failed(Self.sessionExpiredMessage) ? Self.sessionExpiredMessage : nil
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

    func signOut() { signOut(becauseSignInExpired: false) }

    /// `becauseSignInExpired`: the backend or the sign-in server refused this sign-in for good.
    /// The person did not ask for it, so the account form, the log and a quiet caption all say why.
    func signOut(becauseSignInExpired: Bool) {
        let hadSession = OpenClickyConfiguration.hasAccountToken
        OpenClickyConfiguration.update { settings in
            settings.token = ""
            settings.refreshToken = nil
            settings.tokenExpiresAt = nil
            settings.accountEmail = nil
        }
        confirmationWatch?.cancel()
        confirmationWatch = nil
        tokenFromLastForcedRefresh = nil
        emailFlow = .idle
        lastErrorText = nil
        AppLog.append(becauseSignInExpired ? "sign-in: expired, signed out" : "sign-in: signed out")
        if becauseSignInExpired && hadSession { announceSignInExpired() }
        AccountProfileStore.shared.refresh()
    }

    /// Posted when a dead sign-in signed this Mac out; the object is the sentence.
    static let signInExpiredNotification = Notification.Name("openClickySignInExpired")

    private func announceSignInExpired() {
        emailFlow = .failed(Self.sessionExpiredMessage)
        lastErrorText = Self.sessionExpiredMessage
        NotificationCenter.default.post(name: Self.signInExpiredNotification, object: Self.sessionExpiredMessage)
        AccountProfileStore.shared.showNotice?(Self.sessionExpiredMessage)
    }

    nonisolated enum RefreshOutcome: Equatable { case refreshed, notNeeded, failed, signedOut }
    /// The refresh being run, so a second caller waits for it instead of reporting "not needed".
    private var refreshInFlight: Task<RefreshOutcome, Never>?

    /// Refreshes when the stored access token is within `refreshLeadSeconds` of expiring.
    @discardableResult
    func refreshIfNeeded(force: Bool = false) async -> RefreshOutcome {
        if let refreshInFlight { return await refreshInFlight.value }
        let settings = OpenClickyConfiguration.settings
        guard let refreshToken = settings.refreshToken?.nonEmpty else { return .notNeeded }
        let expiresAt = settings.tokenExpiresAt ?? 0
        guard force || Date().timeIntervalSince1970 > expiresAt - refreshLeadSeconds else { return .notNeeded }
        // Its own task: a caller cancelled mid-refresh (a new question) must not cut it short.
        let refresh = Task { @MainActor in
            await self.refresh(using: refreshToken, storedEmail: settings.accountEmail)
        }
        refreshInFlight = refresh
        isRefreshing = true
        let outcome = await refresh.value
        refreshInFlight = nil
        isRefreshing = false
        return outcome
    }

    private func refresh(using refreshToken: String, storedEmail: String?) async -> RefreshOutcome {
        do {
            let config = try await authConfig()
            let session = try await requestToken(config: config, grant: "refresh_token", body: ["refresh_token": refreshToken])
            store(Session(access_token: session.access_token, refresh_token: session.refresh_token, expires_in: session.expires_in),
                  email: Self.refreshedEmail(session, stored: storedEmail))
            AppLog.append("sign-in: session refreshed")
            return .refreshed
        } catch {
            let status = (error as NSError).userInfo[Self.httpStatusKey] as? Int ?? 0
            if Self.refreshRejectionEndsSession(status: status) {
                // The sign-in server refused this refresh token outright (expired, revoked, or from
                // another project after a move): it will never work again. Signing out shows the
                // sign-in form, instead of looking signed in while every request answers 401.
                AppLog.append("sign-in: refresh refused (\(status)); signing out")
                signOut(becauseSignInExpired: true)
                return .signedOut
            }
            lastErrorText = Self.describe(error)
            AppLog.append("sign-in: refresh failed, session kept: \(lastErrorText ?? "")")
            return .failed
        }
    }

    // MARK: - The backend refused the token (401)

    /// What one 401 from the backend calls for. `sentToken` is the token the refused request
    /// carried, when the caller knows it.
    nonisolated enum RejectionDecision: Equatable {
        /// No token at all: there is no sign-in to end.
        case nothingToSignOutOf
        /// The request went out with an older token than the one now stored; send it again.
        case alreadyRenewed
        /// Nothing can renew this token (pasted, invite, or one a refresh just produced): sign out.
        case signOut
        /// Refresh once and send it again.
        case refresh
    }

    nonisolated static func decisionAfterRejection(currentToken: String?, sentToken: String?, hasRefreshToken: Bool,
                                                   tokenFromLastForcedRefresh: String?) -> RejectionDecision {
        guard let currentToken, !currentToken.isEmpty else { return .nothingToSignOutOf }
        if let sentToken, !sentToken.isEmpty, sentToken != currentToken { return .alreadyRenewed }
        // A pasted or invite token has nothing to refresh with; repeating "ask again" would loop forever.
        guard hasRefreshToken else { return .signOut }
        // The token this refresh produced was refused too: the second 401 in a row ends it.
        if currentToken == tokenFromLastForcedRefresh { return .signOut }
        return .refresh
    }

    nonisolated enum RejectionOutcome: Equatable {
        /// A fresh token is stored; the request (or the question) can go again.
        case tryAgain
        /// Signed out; the person has to sign in again.
        case signedOut
        /// The refresh could not be tried (offline, sign-in server down); the session is kept.
        case couldNotCheck
    }

    /// The token the last 401-driven refresh produced: refused once more, it signs out.
    private var tokenFromLastForcedRefresh: String?

    /// Every backend caller's one answer to a 401: refresh once, and sign out when nothing can
    /// renew the token or the renewed token is refused as well.
    func handleBackendRejection(sentToken: String? = nil) async -> RejectionOutcome {
        let decision = Self.decisionAfterRejection(
            currentToken: OpenClickyConfiguration.token, sentToken: sentToken,
            hasRefreshToken: canRefresh, tokenFromLastForcedRefresh: tokenFromLastForcedRefresh)
        AppLog.append("sign-in: backend refused the token (401): \(decision)")
        switch decision {
        case .nothingToSignOutOf:
            return .signedOut
        case .alreadyRenewed:
            return .tryAgain
        case .signOut:
            signOut(becauseSignInExpired: true)
            return .signedOut
        case .refresh:
            switch await refreshIfNeeded(force: true) {
            case .refreshed:
                tokenFromLastForcedRefresh = OpenClickyConfiguration.token
                return .tryAgain
            case .signedOut:
                return .signedOut
            case .failed, .notNeeded:
                return .couldNotCheck
            }
        }
    }

    nonisolated static let askAgainSentence = "please ask that again."
    nonisolated static let spokenSignInExpiredSentence = "your openclicky sign-in has expired. sign in again in settings, account."
    nonisolated static let ownKeyRefusedSentence = "your own api key was refused. check it in settings, keys & plans."

    nonisolated static func spokenSentence(after outcome: RejectionOutcome) -> String {
        switch outcome {
        case .tryAgain: return askAgainSentence
        case .signedOut: return spokenSignInExpiredSentence
        case .couldNotCheck: return unreachableMessage
        }
    }

    /// What the ask lane says when `BackendSignIn` has already renewed a refused token once and
    /// asked again: the same sentences as `spokenSentence(after:)` for the outcome it ended on.
    nonisolated static func spokenSentence(after signInError: BackendSignInError) -> String {
        switch signInError {
        case .expired: return spokenSentence(after: .signedOut)
        case .couldNotRenew: return spokenSentence(after: .couldNotCheck)
        }
    }

    /// For a 401 the ask lane got back as it came (`BackendSignIn` passes a 401 through when it is
    /// an own key's refusal or the request carried no token; Anthropic refuses an own key directly):
    /// handles it and returns the sentence to speak. A 401 about the person's own provider key is
    /// not about the sign-in, and signing out over it would be wrong.
    func sentenceAfterBackendRejection(responseBody body: Data) async -> String {
        if BackendSignIn.isOwnKeyRefusal(body: body) {
            AppLog.append("sign-in: 401 came from the person's own provider key, sign-in kept")
            return Self.ownKeyRefusedSentence
        }
        // No account on this Mac, only own keys: there is no sign-in to expire, so never "sign in again".
        if !OpenClickyConfiguration.hasAccountToken && OpenClickyConfiguration.isConfigured {
            AppLog.append("sign-in: 401 on a mac with own keys and no account; nothing to sign out of")
            return Self.ownKeyRefusedSentence
        }
        return Self.spokenSentence(after: await handleBackendRejection())
    }

    nonisolated static let httpStatusKey = "OpenClickyAuthHTTPStatus"
    nonisolated static let sessionExpiredMessage = "your sign-in expired, sign in again with your email."

    /// A 4xx from the token endpoint means this refresh token is dead; a 429, a 5xx or no answer
    /// (status 0) may pass, so the session is kept and tried again later.
    nonisolated static func refreshRejectionEndsSession(status: Int) -> Bool {
        (400..<500).contains(status) && status != 429
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
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            throw NSError(domain: "OpenClickyAuth", code: 2, userInfo: [NSLocalizedDescriptionKey: message, Self.httpStatusKey: status])
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
