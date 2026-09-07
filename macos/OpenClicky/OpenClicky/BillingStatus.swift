//
//  BillingStatus.swift
//  OpenClicky
//
//  The Settings "Account" section: whether this Mac uses its own provider keys (bring your own
//  key, nothing metered), or which plan it is on and how many credits are left this period
//  (GET /billing/me), with Subscribe / Manage buttons that open Stripe Checkout / the customer
//  portal in the browser.
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
    @Published var isBusy = false

    func refresh() {
        guard OpenClickyConfiguration.isConfigured else {
            errorText = "sign in first (token in shell.json)"
            return
        }
        Task {
            do {
                var request = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)/billing/me")!)
                OpenClickyConfiguration.authorize(&request)
                let (data, response) = try await URLSession.shared.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
                summary = try JSONDecoder().decode(BillingSummary.self, from: data)
                errorText = nil
            } catch {
                errorText = "couldn't load your plan (\(error.localizedDescription))"
            }
        }
    }

    func openCheckout(plan: String) { openBillingPage(path: "/billing/checkout", body: ["plan": plan]) }
    func openPortal() { openBillingPage(path: "/billing/portal", body: [:]) }

    /// Asks the backend for a Stripe page URL and opens it in the browser.
    private func openBillingPage(path: String, body: [String: String]) {
        isBusy = true
        Task {
            defer { isBusy = false }
            do {
                var request = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)\(path)")!)
                request.httpMethod = "POST"
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = try JSONSerialization.data(withJSONObject: body)
                OpenClickyConfiguration.authorize(&request)
                let (data, _) = try await URLSession.shared.data(for: request)
                guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let urlString = json["url"] as? String,
                      let url = URL(string: urlString) else {
                    let message = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
                    throw NSError(domain: "OpenClickyBilling", code: 1, userInfo: [NSLocalizedDescriptionKey: message ?? "no URL returned"])
                }
                NSWorkspace.shared.open(url)
            } catch {
                errorText = "couldn't open the billing page (\(error.localizedDescription))"
            }
        }
    }
}

/// Rows for the Settings tab. The row builders are passed in so this section uses the same row
/// styles as the rest of Settings (they are private to NotchHUDPanels.swift).
struct NotchAccountSection<Row: View, ActionRow: View>: View {
    @StateObject private var model = BillingStatusModel()
    let row: (_ systemImage: String, _ title: String, _ value: String) -> Row
    let action: (_ systemImage: String, _ title: String, _ detail: String?, _ action: @escaping () -> Void) -> ActionRow

    var body: some View {
        Group {
            if OpenClickyConfiguration.usesOwnKeys {
                row("key.fill", "Keys", "your own (not metered)")
                action("doc.text", "Change keys", "openaiApiKey / anthropicApiKey in shell.json") { OpenClickyConfiguration.revealSettingsFile() }
            } else if let summary = model.summary {
                row("creditcard", "Plan", summary.plan.capitalized + (summary.status == "free" || summary.status == "unmetered" ? "" : " · \(summary.status)"))
                if summary.limit > 0 {
                    row("gauge", "Credits", "\(summary.used) / \(summary.limit) used · resets \(Self.shortDate(summary.periodEnd))")
                }
                if summary.plan == "free" || summary.plan == "unmetered" {
                    action("sparkles", "Subscribe", "Starter or Pro, billed monthly") { model.openCheckout(plan: "starter") }
                } else {
                    action("person.crop.circle", "Manage subscription", "Invoices, upgrade, cancel") { model.openPortal() }
                }
                action("key", "Use my own API key instead", "Add openaiApiKey to shell.json") { OpenClickyConfiguration.revealSettingsFile() }
                if let errorText = model.errorText {
                    row("exclamationmark.triangle", "Billing", errorText)
                }
            } else {
                row("creditcard", "Plan", model.errorText ?? "loading…")
            }
        }
        .onAppear { model.refresh() }
    }

    private static func shortDate(_ iso: String) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = formatter.date(from: iso) ?? ISO8601DateFormatter().date(from: iso)
        guard let date else { return iso }
        return date.formatted(date: .abbreviated, time: .omitted)
    }
}
