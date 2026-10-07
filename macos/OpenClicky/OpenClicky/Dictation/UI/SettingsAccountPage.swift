//
//  SettingsAccountPage.swift
//  OpenClicky
//
//  Settings → account (was account + plan & usage): who you are and what it costs. Sign in or
//  out (accounts are invite-only), the plan and what each engine costs, your own openai key,
//  and the backend host and token from shell.json, read-only.
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
                id: "account.signIn", page: .account, section: "you",
                title: "sign in to openclicky", detail: "an account gives you openai via openclicky and model polish without your own keys. this mac as the engine never needs one.",
                keywords: ["account", "sign in", "log in", "email", "password", "invite"],
                isRelevant: { OpenClickyAuthSession.shared.accountEmail == nil },
                view: AnyView(SignInForm())),
            SettingsItem(
                id: "account.inviteOnly", page: .account, section: "you",
                title: "invite-only", detail: "accounts are invite-only for now. your own keys work without one.",
                keywords: ["invite", "waitlist", "own key"],
                chrome: .bare,
                isRelevant: { OpenClickyAuthSession.shared.accountEmail == nil },
                view: AnyView(SettingsFootnote(text: "accounts are invite-only for now. your own keys work without one: a sarvam key under voice, or an openai key below."))),
            SettingsItem(
                id: "account.plan", page: .account, section: "plan & usage",
                title: "plan", detail: isSignedIn ? "credits this period." : "no account needed on this mac.",
                keywords: ["billing", "credits", "usage", "plan", "cost", "price"],
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
                keywords: ["openai", "assemblyai", "credits", "metered", "engine"],
                view: AnyView(BackendEngineUsageRow())),
            SettingsItem(
                id: "account.ownOpenAIKey", page: .account, section: "your keys",
                title: "use my own openai key", detail: "sent to the openclicky backend with each request and used instead of your credits; nothing is metered. kept in ~/.openclicky/shell.json.",
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
                    Text("personal workspace · signing out keeps your history on this mac.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
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

private struct SignInForm: View {
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var isSigningIn = false
    @State private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("sign in to openclicky").font(Paper.rowTitle).foregroundStyle(Paper.ink)
            Text("an account gives you openai via openclicky and model polish without your own keys. this mac as the engine never needs one.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField("email", text: $email).textFieldStyle(.roundedBorder).frame(maxWidth: 360)
            SecureField("password", text: $password).textFieldStyle(.roundedBorder).frame(maxWidth: 360)
                .onSubmit(signIn)
            if let failure { Text(failure).font(Paper.caption).foregroundStyle(Paper.danger) }
            Button(isSigningIn ? "signing in…" : "sign in", action: signIn)
                .buttonStyle(PaperPillButtonStyle(prominent: true))
                .disabled(isSigningIn || email.isEmpty || password.isEmpty)
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical + 4)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func signIn() {
        guard !isSigningIn, !email.isEmpty, !password.isEmpty else { return }
        isSigningIn = true
        failure = nil
        Task {
            let didSignIn = await authSession.signIn(email: email.trimmingCharacters(in: .whitespaces), password: password)
            isSigningIn = false
            if didSignIn {
                password = ""
                ShellSettingsRevision.shared.noteChanged()
            } else {
                failure = authSession.lastErrorText ?? "couldn't sign in. check your email and password."
            }
        }
    }
}

// MARK: - plan & usage

private struct PlanSummary: View {
    @StateObject private var billing = BillingStatusModel()
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        VStack(alignment: .leading, spacing: 8) {
            if let summary = billing.summary {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("openclicky").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                    SettingsTag(text: summary.byok ? "your keys" : summary.plan)
                }
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text("\(summary.used)").font(.system(size: 30, weight: .regular, design: .serif)).foregroundStyle(Paper.ink)
                    Text("/ \(summary.limit) credits this period").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                }
                Text("renews \(summary.periodEnd)").font(Paper.micro).foregroundStyle(Paper.inkTertiary)
            } else if OpenClickyConfiguration.usesOwnKeys {
                Text("your own keys").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                Text("openclicky meters nothing: every request runs on the keys in shell.json.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            } else if authSession.accountEmail == nil && !OpenClickyConfiguration.isConfigured {
                Text("no account needed on this mac.").font(Paper.cardTitle).foregroundStyle(Paper.ink)
                Text("this mac as the engine needs none. sign in above for openai via openclicky and model polish, or add a sarvam key under voice.")
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
            detail = "counted against your plan’s credits."
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
            SettingsRow(title: "use my own openai key", detail: "sent to the openclicky backend with each request and used instead of your credits; nothing is metered. kept in ~/.openclicky/shell.json.") { EmptyView() }
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
