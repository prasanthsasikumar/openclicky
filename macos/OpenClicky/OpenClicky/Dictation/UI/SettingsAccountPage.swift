//
//  SettingsAccountPage.swift
//  OpenClicky
//
//  Settings → account (was account + plan & usage): who you are and what it costs. Create a free
//  account or sign in (both through AccountSheet), or sign out; how much of the free allowance is
//  used and what each engine costs; your own openai key; and the backend host and token from
//  shell.json, read-only.
//

import AppKit
import SwiftUI

extension SettingsCatalog {
    func accountItems() -> [SettingsItem] {
        let isSignedIn = OpenClickyAuthSession.shared.accountEmail != nil
        return [
            SettingsItem(
                id: "account.signedIn", page: .account, section: "you",
                title: "signed in", detail: "sign out; history stays safe on this mac.",
                keywords: ["account", "sign out", "log out", "email"],
                isRelevant: { OpenClickyAuthSession.shared.accountEmail != nil },
                view: AnyView(SignedInAccountRows())),
            SettingsItem(
                id: "account.start", page: .account, section: "you",
                title: "a free openclicky account", detail: "polished dictation and spoken answers, on us — no keys to find.",
                keywords: ["account", "sign in", "sign up", "create", "log in", "email", "password", "free"],
                isRelevant: { OpenClickyAuthSession.shared.accountEmail == nil },
                view: AnyView(AccountStartRow())),
            SettingsItem(
                id: "account.noAccountNeeded", page: .account, section: "you",
                title: "no account needed", detail: "dictation on this mac works without an account.",
                keywords: ["offline", "own key", "without"],
                chrome: .bare,
                isRelevant: { OpenClickyAuthSession.shared.accountEmail == nil },
                view: AnyView(SettingsFootnote(text: "dictation on this mac works without an account; your own keys work without one too."))),
            SettingsItem(
                id: "account.plan", page: .account, section: "plan & usage",
                title: "plan", detail: isSignedIn ? "how much of this month's free allowance is used." : "no account needed on this mac.",
                keywords: ["billing", "allowance", "usage", "plan", "cost", "price", "limit"],
                view: AnyView(PlanSummary())),
            SettingsItem(
                id: "account.usage.takes", page: .account, section: "plan & usage",
                title: "takes", detail: "as long as you like; the quiet-take timeout ends a silent one.",
                keywords: ["length", "limit"],
                view: AnyView(SettingsRow(title: "takes", detail: "as long as you like; the quiet-take timeout ends a silent one.") { EmptyView() })),
            SettingsItem(
                id: "account.usage.offline", page: .account, section: "plan & usage",
                title: "this mac", detail: "unlimited, on this mac, no account.",
                keywords: ["offline", "free", "engine"],
                view: AnyView(SettingsRow(title: "this mac", detail: "unlimited, on this mac, no account.") { SettingsTag(text: "free", tint: Paper.success) })),
            SettingsItem(
                id: "account.usage.sarvam", page: .account, section: "plan & usage",
                title: "sarvam", detail: "billed by sarvam to your key — see dashboard.sarvam.ai.",
                keywords: ["sarvam", "billing", "engine"],
                view: AnyView(SettingsRow(title: "sarvam", detail: "billed by sarvam to your key — see dashboard.sarvam.ai.") {
                    Button("dashboard ↗") { openExternalLink("https://dashboard.sarvam.ai") }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "account.usage.backend", page: .account, section: "plan & usage",
                title: "openai and assemblyai, via openclicky", detail: "",
                keywords: ["openai", "assemblyai", "allowance", "metered", "engine"],
                view: AnyView(BackendEngineUsageRow())),
            SettingsItem(
                id: "account.ownOpenAIKey", page: .account, section: "your keys",
                title: "use my own openai key", detail: "sent to the openclicky backend with each request and used instead of your free allowance; nothing is metered. kept in ~/.openclicky/shell.json.",
                keywords: ["openai", "api key", "byok", "own key", "openaiApiKey"],
                view: AnyView(OwnOpenAIKeyEditor())),
            SettingsItem(
                id: "account.backendHost", page: .account, section: "backend",
                title: "backend host", detail: "where openclicky’s model calls go. change it in shell.json.",
                keywords: ["backend", "server", "url", "host", "shell.json"],
                view: AnyView(BackendDetailsRow(kind: .host))),
            SettingsItem(
                id: "account.backendToken", page: .account, section: "backend",
                title: "token", detail: "the session token every request carries; written by sign-in.",
                keywords: ["token", "bearer", "shell.json", "session"],
                view: AnyView(BackendDetailsRow(kind: .token))),
        ]
    }
}

struct SettingsFootnote: View {
    let text: String
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        Text(settingsHighlighted(text, query: searchQuery))
            .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.top, 4).padding(.leading, 2)
    }
}

// MARK: - you

private struct SignedInAccountRows: View {
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    var body: some View {
        if let accountEmail = authSession.accountEmail {
            HStack(spacing: 14) {
                ZStack {
                    Circle().fill(Paper.accentSoft)
                    Text(String(accountEmail.prefix(1)).lowercased()).font(Paper.body(16, weight: .semibold)).foregroundStyle(Paper.ink)
                }
                .frame(width: 40, height: 40)
                .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(accountEmail).font(Paper.rowTitle).foregroundStyle(Paper.ink)
                    Text("signing out keeps your history on this mac.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                }
                Spacer()
                Button("sign out") {
                    authSession.signOut()
                    ShellSettingsRevision.shared.noteChanged()
                }
                .buttonStyle(PaperPillButtonStyle())
            }
            .padding(.horizontal, Paper.Metric.rowHorizontal)
            .padding(.vertical, Paper.Metric.rowVertical)
        }
    }
}

/// Signed out: the two ways in, each opening the account sheet. "create" is hidden while the
/// backend takes no new accounts.
private struct AccountStartRow: View {
    @State private var sheetMode: AccountSheet.Mode?
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("a free openclicky account").font(Paper.rowTitle).foregroundStyle(Paper.ink)
            Text("polished dictation and spoken answers, on us — no keys to find.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                if OpenClickyAuthSession.offersCreateAccount(accountsOpen: authSession.accountsOpen) {
                    Button("create a free account") { sheetMode = .create }
                        .buttonStyle(PaperPillButtonStyle(prominent: true))
                }
                Button("sign in") { sheetMode = .signIn }
                    .buttonStyle(PaperPillButtonStyle())
            }
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical + 4)
        .frame(maxWidth: .infinity, alignment: .leading)
        .task { await authSession.refreshAccountsOpen() }
        .sheet(item: $sheetMode) { mode in
            // The own-key field is on this page, behind the sheet: closing it is the way there.
            AccountSheet(startIn: mode, onDone: { sheetMode = nil }, onUseOwnKey: { sheetMode = nil })
        }
    }
}

// MARK: - plan & usage

private struct PlanSummary: View {
    @StateObject private var billing = BillingStatusModel()
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @State private var showsDetails = false

    var body: some View {
        let _ = shellSettingsRevision.revision
        VStack(alignment: .leading, spacing: 8) {
            if let summary = billing.summary {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("openclicky").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                    SettingsTag(text: summary.byok ? "your keys" : summary.isMetered ? "free account" : "not metered")
                }
                if summary.byok {
                    Text("openclicky meters nothing: every request runs on your own key.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                } else if !summary.isMetered {
                    Text("this backend meters nothing: every request runs without an allowance.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                } else if summary.isSwitchedOff {
                    Text(BillingSummary.switchedOffMessage)
                        .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    AccountUsageBar(summary: summary)
                    DisclosureGroup(isExpanded: $showsDetails) {
                        Text(summary.usageDetails())
                            .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.top, 4)
                    } label: {
                        Text("details").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    }
                    .pointerCursor()
                }
            } else if OpenClickyConfiguration.usesOwnKeys {
                Text("your own keys").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                Text("openclicky meters nothing: every request runs on the keys in shell.json.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            } else if authSession.accountEmail == nil && !OpenClickyConfiguration.isConfigured {
                Text("no account needed on this mac.").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                Text("dictation on this mac needs none — a free account above adds polish and spoken answers.")
                    .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text(billing.errorText ?? "loading…").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            }
        }
        .padding(Paper.Metric.rowHorizontal + 4)
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear { billing.refresh() }
        .onChange(of: shellSettingsRevision.revision) { _, _ in billing.refresh() }
    }
}

private struct BackendEngineUsageRow: View {
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        let detail: String
        if OpenClickyConfiguration.usesOwnKeys {
            detail = "your own openai key pays for these; not metered."
        } else if OpenClickyConfiguration.isConfigured {
            detail = "counted against your free allowance."
        } else {
            detail = "need an account or your own openai key."
        }
        return SettingsRow(title: "openai and assemblyai, via openclicky", detail: detail) { EmptyView() }
    }
}

private struct OwnOpenAIKeyEditor: View {
    @State private var openaiKey = OpenClickyConfiguration.settings.openaiApiKey ?? ""
    @State private var status: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SettingsRow(title: "use my own openai key", detail: "sent to the openclicky backend with each request and used instead of your free allowance; nothing is metered. kept in ~/.openclicky/shell.json.") { EmptyView() }
            HStack(spacing: 8) {
                SecureField("sk-…", text: $openaiKey)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 320)
                    .onSubmit(saveKey)
                    .accessibilityLabel("openai key")
                Button("save") { saveKey() }.buttonStyle(PaperPillButtonStyle(prominent: true))
                if let status { Text(status).font(Paper.micro).foregroundStyle(Paper.success) }
            }
            .padding(.horizontal, Paper.Metric.rowHorizontal)
            .padding(.bottom, Paper.Metric.rowVertical)
        }
    }

    private func saveKey() {
        let trimmed = openaiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        OpenClickyConfiguration.update { $0.openaiApiKey = trimmed.isEmpty ? nil : trimmed }
        // A changed key is a changed credential: a live realtime session reconnects with it.
        NotificationCenter.default.post(name: OpenClickyConfiguration.credentialsChangedNotification, object: nil)
        status = trimmed.isEmpty ? "key removed" : "✓ saved"
    }
}

// MARK: - backend

private struct BackendDetailsRow: View {
    enum Kind { case host, token }
    let kind: Kind
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        switch kind {
        case .host:
            SettingsRow(title: "backend host", detail: "where openclicky’s model calls go. change it in shell.json.") {
                HStack(spacing: 8) {
                    Text(OpenClickyConfiguration.backendHostDescription)
                        .font(Paper.key).foregroundStyle(Paper.ink)
                        .textSelection(.enabled)
                        .help(OpenClickyConfiguration.backendBaseURL)
                    Button("show shell.json") { OpenClickyConfiguration.revealSettingsFile() }.buttonStyle(PaperPillButtonStyle())
                }
            }
        case .token:
            SettingsRow(title: "token", detail: "the session token every request carries; written by sign-in.") {
                Text(maskedToken).font(Paper.key).foregroundStyle(OpenClickyConfiguration.token == nil ? Paper.inkTertiary : Paper.ink)
            }
        }
    }

    private var maskedToken: String {
        guard let token = OpenClickyConfiguration.token else { return "none" }
        return "••••" + String(token.suffix(4))
    }
}
