//
//  BillingStatus.swift
//  OpenClicky
//
//  What the account has used (GET /billing/me) and the sentences that say it: the free
//  allowance this month and today, the shared budget, the spoken characters. Also the island's
//  account rows in Settings → more: sign in, or the allowance and sign out; with an own key in
//  shell.json, "not metered".
//

import AppKit
import Combine
import SwiftUI

struct BillingSummary: Decodable, Equatable {
    let byok: Bool
    let spentMonthUsd: Double
    let monthlyLimitUsd: Double
    let spentTodayUsd: Double
    let dailyLimitUsd: Double
    let ttsCharsMonth: Int
    let ttsCharsLimit: Int
    let monthEnd: String
    let dayEnd: String
    let budgetExhausted: Bool
    let blocked: Bool
}

/// Where the free allowance stands, most pressing first: each case has its own sentence because
/// "used up this month" is wrong when only today's share or the shared budget ran out.
enum AllowanceStanding: Equatable { case plenty, runningLow, usedUpThisMonth, usedUpToday, sharedBudgetUsedUp, paused }

extension BillingSummary {
    var allowanceStanding: AllowanceStanding {
        if blocked { return .paused }
        if fractionUsed >= 1 { return .usedUpThisMonth }
        if budgetExhausted { return .sharedBudgetUsedUp }
        if dailyLimitUsd > 0 && spentTodayUsd >= dailyLimitUsd { return .usedUpToday }
        return level == .runningLow ? .runningLow : .plenty
    }

    /// One calm line under the usage bar and in the island ("plenty left this month · resets on
    /// nov 1"). `resetDay` is `monthResetDay(...)`; nil when the date could not be read.
    func allowanceSentence(resetDay: String?) -> String {
        let onResetDay = resetDay.map { "on \($0)" } ?? "on the 1st"
        switch allowanceStanding {
        case .plenty: return "plenty left this month · resets \(onResetDay)"
        case .runningLow: return "running low this month · resets \(onResetDay)"
        case .usedUpThisMonth: return "this month's free allowance is used — it comes back \(onResetDay)"
        case .usedUpToday: return "today's free allowance is used — it comes back tomorrow"
        case .sharedBudgetUsedUp: return "the free allowance is used up for now"
        case .paused: return "this account is paused — dictation still works"
        }
    }

    /// The day the month's allowance comes back, as "nov 1". Months are UTC calendar months, so
    /// the date is read in UTC: midnight on the 1st is still the 31st in the Americas otherwise.
    func monthResetDay(locale: Locale = .current) -> String? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let date = fractional.date(from: monthEnd) ?? ISO8601DateFormatter().date(from: monthEnd),
              let utc = TimeZone(identifier: "UTC") else { return nil }
        let style = Date.FormatStyle(locale: locale, timeZone: utc).day().month(.abbreviated)
        return date.formatted(style).lowercased()
    }

    /// The "details" line: "$4.50 of $10 this month · $0.20 of $2 today · 300 spoken characters of 20,000".
    func usageDetails(locale: Locale = .current) -> String {
        func dollars(_ amount: Double) -> String {
            amount == amount.rounded() ? "$\(Int(amount))" : String(format: "$%.2f", amount)
        }
        func count(_ number: Int) -> String { number.formatted(.number.locale(locale)) }
        return "\(dollars(spentMonthUsd)) of \(dollars(monthlyLimitUsd)) this month · \(dollars(spentTodayUsd)) of \(dollars(dailyLimitUsd)) today · \(count(ttsCharsMonth)) spoken characters of \(count(ttsCharsLimit))"
    }
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
                errorText = (error as NSError).code == 401 ? "your sign-in ran out — sign in again." : "couldn't load your allowance right now."
                print("💳 Billing status failed: \(error.localizedDescription)")
            }
        }
    }
}

/// Rows for the Settings tab. The row builders are passed in so this section uses the same row
/// styles as the rest of Settings (they are private to NotchHUDPanels.swift). Creating an account
/// happens in the window (`openAccountPage`), where the account sheet has room.
struct NotchAccountSection<Row: View, ActionRow: View>: View {
    @StateObject private var model = BillingStatusModel()
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var isSigningIn = false
    @State private var signInFailureText: String?

    let row: (_ systemImage: String, _ title: String, _ value: String) -> Row
    let action: (_ systemImage: String, _ title: String, _ detail: String?, _ action: @escaping () -> Void) -> ActionRow
    let openAccountPage: () -> Void

    var body: some View {
        Group {
            if OpenClickyConfiguration.usesOwnKeys {
                row("key.fill", "keys", "your own (not metered)")
                action("doc.text", "change keys", "openaiApiKey / anthropicApiKey in shell.json") { OpenClickyConfiguration.revealSettingsFile() }
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
            Text("sign in to your openclicky account.")
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
                    Text(isSigningIn ? "signing in…" : "sign in")
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
            Button(action: openAccountPage) {
                Text("no account yet? create a free one in settings.")
                    .font(.system(size: 10))
                    .foregroundColor(Color.white.opacity(0.55))
            }
            .buttonStyle(.plain)
            .pointerCursor()
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
                signInFailureText = AccountSheet.friendlySignInMessage(authSession.lastErrorText)
            }
        }
    }

    @ViewBuilder
    private var signedInRows: some View {
        if let accountEmail = authSession.accountEmail {
            row("person.crop.circle", "account", accountEmail)
        } else {
            row("person.crop.circle", "account", "token from shell.json")
        }
        if let summary = model.summary {
            row("gauge", "allowance", summary.allowanceSentence(resetDay: summary.monthResetDay()))
        } else if let errorText = model.errorText {
            row("exclamationmark.triangle", "allowance", errorText)
        } else {
            row("gauge", "allowance", "loading…")
        }
        if authSession.accountEmail != nil {
            action("rectangle.portrait.and.arrow.right", "sign out", nil) { authSession.signOut() }
        }
        action("key", "use my own key instead", "in settings → account", openAccountPage)
    }
}
