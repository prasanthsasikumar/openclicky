//
//  BillingStatus.swift
//  OpenClicky
//
//  The Settings "Account" section. Signed out: email + password fields (invite-only accounts,
//  created with `npm run admin -w backend -- invite`). Signed in: the plan and how many credits
//  are left this month (GET /billing/me). With an own key in shell.json: "not metered".
//

import AppKit
import Combine
import SwiftUI

struct BillingSummary: Decodable, Equatable {
    let byok: Bool
    let plan: String
    let status: String
    let used: Int
    let limit: Int
    let periodEnd: String
}

@MainActor
final class BillingStatusModel: ObservableObject {
    @Published var summary: BillingSummary?
    @Published var errorText: String?

    func refresh() {
        guard OpenClickyConfiguration.isConfigured else {
            summary = nil
            return
        }
        Task {
            do {
                // backendBaseURL is user-configurable (shell.json or an environment override), not
                // a compile-time literal, so a malformed value must throw instead of crashing the app.
                guard let billingURL = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/billing/me") else {
                    throw NSError(domain: "OpenClickyBilling", code: -1, userInfo: [NSLocalizedDescriptionKey: "backend URL is invalid"])
                }
                var request = URLRequest(url: billingURL)
                OpenClickyConfiguration.authorize(&request)
                let (data, response) = try await URLSession.shared.data(for: request)
                let statusCode = (response as? HTTPURLResponse)?.statusCode ?? 0
                guard statusCode == 200 else {
                    throw NSError(domain: "OpenClickyBilling", code: statusCode, userInfo: [NSLocalizedDescriptionKey: statusCode == 401 ? "session expired, sign in again" : "backend answered \(statusCode)"])
                }
                summary = try JSONDecoder().decode(BillingSummary.self, from: data)
                errorText = nil
            } catch {
                errorText = "couldn't load your plan (\(error.localizedDescription))"
            }
        }
    }
}

/// Rows for the Settings tab. The row builders are passed in so this section uses the same row
/// styles as the rest of Settings (they are private to NotchHUDPanels.swift).
struct NotchAccountSection<Row: View, ActionRow: View>: View {
    @StateObject private var model = BillingStatusModel()
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var isSigningIn = false
    @State private var signInFailureText: String?

    let row: (_ systemImage: String, _ title: String, _ value: String) -> Row
    let action: (_ systemImage: String, _ title: String, _ detail: String?, _ action: @escaping () -> Void) -> ActionRow

    var body: some View {
        Group {
            if OpenClickyConfiguration.usesOwnKeys {
                row("key.fill", "Keys", "your own (not metered)")
                action("doc.text", "Change keys", "openaiApiKey / anthropicApiKey in shell.json") { OpenClickyConfiguration.revealSettingsFile() }
            } else if !OpenClickyConfiguration.isConfigured {
                signInForm
            } else {
                signedInRows
            }
        }
        .onAppear { model.refresh() }
        .onReceive(authSession.objectWillChange) { _ in DispatchQueue.main.async { model.refresh() } }
    }

    private var signInForm: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Sign in with the account you were invited with.")
                .font(.system(size: 11))
                .foregroundColor(Color.white.opacity(0.6))
            credentialField(systemImage: "envelope", text: $email, placeholder: "email", isSecure: false)
            credentialField(systemImage: "lock", text: $password, placeholder: "password", isSecure: true)
            HStack {
                if let signInFailureText {
                    Text(signInFailureText)
                        .font(.system(size: 10.5))
                        .foregroundColor(DS.Colors.overlayCursorColor)
                        .lineLimit(2)
                }
                Spacer()
                Button(action: signIn) {
                    Text(isSigningIn ? "Signing in…" : "Sign in")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundColor(.white)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 6)
                        .background(Capsule().fill(Color(hex: "#262626")))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .disabled(isSigningIn || email.isEmpty || password.isEmpty)
            }
            Text("No account? Use your own OpenAI key instead: add openaiApiKey to shell.json.")
                .font(.system(size: 10))
                .foregroundColor(Color.white.opacity(0.45))
                .onTapGesture { OpenClickyConfiguration.revealSettingsFile() }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private func credentialField(systemImage: String, text: Binding<String>, placeholder: String, isSecure: Bool) -> some View {
        HStack(spacing: 8) {
            Image(systemName: systemImage).font(.system(size: 11)).foregroundColor(Color.white.opacity(0.6)).frame(width: 16)
            NotchComposerTextField(text: text, placeholder: placeholder, isSecure: isSecure, onSubmit: signIn, onEscape: {})
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white.opacity(0.06)))
    }

    private func signIn() {
        let trimmedEmail = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedEmail.isEmpty, !password.isEmpty, !isSigningIn else { return }
        isSigningIn = true
        signInFailureText = nil
        Task {
            let succeeded = await authSession.signIn(email: trimmedEmail, password: password)
            isSigningIn = false
            if succeeded {
                password = ""
                model.refresh()
            } else {
                signInFailureText = authSession.lastErrorText ?? "sign-in failed"
            }
        }
    }

    @ViewBuilder
    private var signedInRows: some View {
        if let accountEmail = authSession.accountEmail {
            row("person.crop.circle", "Account", accountEmail)
        } else {
            row("person.crop.circle", "Account", "token from shell.json")
        }
        if let summary = model.summary {
            let planLabel = summary.status == "unmetered" ? "not metered on this backend" : summary.plan.capitalized + (summary.status == "active" || summary.status == "free" ? "" : " · \(summary.status)")
            row("creditcard", "Plan", planLabel)
            if summary.limit > 0 || summary.status != "unmetered" {
                row("gauge", "Credits", "\(summary.used) / \(summary.limit) this month · resets \(Self.shortDate(summary.periodEnd))")
            }
        } else if let errorText = model.errorText {
            row("exclamationmark.triangle", "Plan", errorText)
        } else {
            row("creditcard", "Plan", "loading…")
        }
        if authSession.accountEmail != nil {
            action("rectangle.portrait.and.arrow.right", "Sign out", nil) { authSession.signOut() }
        }
        action("key", "Use my own API key instead", "Add openaiApiKey to shell.json") { OpenClickyConfiguration.revealSettingsFile() }
    }

    private static func shortDate(_ iso: String) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = formatter.date(from: iso) ?? ISO8601DateFormatter().date(from: iso)
        guard let date else { return iso }
        return date.formatted(date: .abbreviated, time: .omitted)
    }
}
