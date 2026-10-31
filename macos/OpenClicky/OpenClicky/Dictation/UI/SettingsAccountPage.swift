//
//  SettingsAccountPage.swift
//  OpenClicky
//
//  Settings → Account: who you are and what it costs. Sign in (or set up a free account with just
//  an email, in AccountSheet) or sign out; how much of the free allowance is used and what each
//  engine costs; and the backend host and token from shell.json.
//  Settings → Keys & Plans: your own provider keys (each one testable) and the plans you already
//  pay for. They have their own page because the account is optional and keys aren't an account:
//  "add a key under Account" read like "sign up".
//

import AppKit
import SwiftUI

extension SettingsCatalog {
    func accountItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "account.profile",
                title: "Account", detail: "Sign in or out. A free account adds AI polish and spoken answers; dictation on this Mac works without one.",
                keywords: ["account", "sign in", "sign up", "sign out", "log in", "log out", "create", "email", "code", "free"],
                view: AnyView(AccountProfileRow())),
            SettingsItem(
                id: "account.plan",
                title: "Free allowance", detail: "How much of this month’s free allowance is used.",
                keywords: ["billing", "allowance", "usage", "plan", "cost", "price", "limit"],
                view: AnyView(PlanSummary())),
            SettingsItem(
                id: "account.usage.sarvam",
                title: "Sarvam", detail: "Billed by Sarvam to your key.",
                keywords: ["sarvam", "billing", "engine", "dashboard"],
                isRelevant: { OpenClickyConfiguration.settings.sarvamKey?.isEmpty == false },
                view: AnyView(SettingsRow(title: "Sarvam", detail: "Billed by Sarvam to your key.") {
                    Button("Dashboard ↗") { openExternalLink("https://dashboard.sarvam.ai") }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "account.usage.backend",
                title: "OpenAI and AssemblyAI", detail: "Through OpenClicky.",
                keywords: ["openai", "assemblyai", "allowance", "metered", "engine"],
                view: AnyView(BackendEngineUsageRow())),
            SettingsItem(
                id: "account.backendHost",
                title: "Host", detail: "Where OpenClicky’s model calls go. Change it in shell.json.",
                keywords: ["backend", "server", "url", "host", "shell.json"],
                view: AnyView(BackendDetailsRow(kind: .host))),
            SettingsItem(
                id: "account.backendToken",
                title: "Session token", detail: "Written when you sign in.",
                keywords: ["token", "bearer", "shell.json", "session"],
                view: AnyView(BackendDetailsRow(kind: .token))),
        ]
    }

    func keysItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "account.keys",
                title: "Your keys", detail: "Keys from AI providers (they answer, clean up and speak) and voice services.",
                keywords: ["api key", "byok", "own key", "provider", "add key", "openai", "openaiApiKey", "anthropic", "claude",
                           "anthropicApiKey", "openrouter", "llama", "deepseek", "gemini", "google", "ai studio", "groq",
                           "mistral", "xai", "grok", "sarvam", "saaras", "elevenlabs", "polly", "google cloud", "voice", "test"],
                view: AnyView(ProviderKeysList())),
            SettingsItem(
                id: "account.keysNote",
                title: "Where keys go", detail: "Your keys go straight to that provider, never through OpenClicky, and are used instead of the free credits. Stored in ~/.openclicky/shell.json.",
                keywords: ["shell.json", "byok"],
                chrome: .footer,
                view: AnyView(SettingsFootnote(text: "Your keys go straight to that provider, never through OpenClicky, and are used instead of the free credits. Stored in ~/.openclicky/shell.json."))),
            SettingsItem(
                id: "account.claudeCodePlan",
                title: "Claude Code (your Claude plan)", detail: "Answer questions through Claude Code, signed in to your Claude plan.",
                keywords: ["claude code", "claude plan", "pro", "max", "subscription", "anthropic"],
                view: AnyView(PlanSubscriptionRow(plan: .claudeCodePlan))),
            SettingsItem(
                id: "account.codexPlan",
                title: "Codex (your ChatGPT plan)", detail: "Answer questions through Codex, signed in with ChatGPT.",
                keywords: ["codex", "chatgpt", "plus", "pro", "subscription", "openai"],
                view: AnyView(PlanSubscriptionRow(plan: .codexPlan))),
            SettingsItem(
                id: "account.plansNote",
                title: "What a plan can do", detail: "A plan answers, points and does quick actions like opening apps; bigger tasks need the agent.",
                keywords: ["subscription", "plan"],
                chrome: .footer,
                view: AnyView(SettingsFootnote(text: "A plan answers questions, points, and does quick actions like opening apps or making folders. Bigger tasks need the agent. Codex answers in one go, a few seconds after you ask."))),
        ]
    }
}

// MARK: - you

/// Signed in: the address, sign out, and the confirmation code while the address is unconfirmed.
/// Signed out: "Not signed in" and Sign In…, which opens the account sheet.
private struct AccountProfileRow: View {
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    // Read so a guest started in the background (no email) redraws the row.
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @StateObject private var billing = BillingStatusModel()
    @State private var showsAccountSheet = false
    @State private var resendNote: String?
    @State private var resendWork = AccountWorkSlot()

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            ZStack {
                Circle().fill(authSession.accountEmail == nil ? Color(nsColor: .tertiaryLabelColor) : SettingsLook.accent)
                Text(initial).font(.system(size: 18, weight: .semibold)).foregroundStyle(.white)
            }
            .frame(width: 44, height: 44)
            .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                if let accountEmail = authSession.accountEmail {
                    Text(accountEmail).font(.system(size: 15, weight: .semibold))
                    Text("Signing out keeps your history on this Mac.").font(.system(size: 12)).foregroundStyle(.secondary)
                    if billing.summary?.isUnconfirmed == true {
                        HStack(spacing: 6) {
                            Text(resendNote ?? "Unconfirmed. Check your inbox for the code.")
                                .font(SettingsLook.caption).foregroundStyle(.secondary)
                            Button("Resend", action: resend).buttonStyle(.link).font(SettingsLook.caption).pointerCursor()
                        }
                        GuestConfirmationCodeField(onConfirmed: { billing.refresh() })
                    }
                } else if authSession.isGuestWithoutEmail {
                    Text("Trying OpenClicky").font(.system(size: 15, weight: .semibold))
                    Text("Free on OpenClicky’s credits, on this Mac. Add your email to keep the account, or your own key for more.")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text("Not signed in").font(.system(size: 15, weight: .semibold))
                    Text("A free account adds AI polish and spoken answers. Dictation on this Mac works without one.")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    // Signed out by a dead sign-in, not by the person: say why, where they look.
                    if let sessionExpiredNotice = authSession.sessionExpiredNotice {
                        Text(sessionExpiredNotice).font(SettingsLook.caption).foregroundStyle(SettingsLook.danger)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            Spacer(minLength: 12)
            if authSession.accountEmail != nil {
                Button("Sign Out") {
                    authSession.signOut()
                    ShellSettingsRevision.shared.noteChanged()
                }
                .pointerCursor()
            } else {
                Button(authSession.isGuestWithoutEmail ? "Add Email…" : "Sign In…") { showsAccountSheet = true }
                    .buttonStyle(.borderedProminent)
                    .tint(SettingsLook.accent)
                    .pointerCursor()
            }
        }
        .padding(.vertical, 6)
        .onAppear { if OpenClickyConfiguration.hasAccountToken { billing.refresh() } }
        .onDisappear { resendWork.cancel() }
        .task { await authSession.refreshAccountsOpen() }
        .sheet(isPresented: $showsAccountSheet) {
            // The own-key field is on this page, behind the sheet: closing it is the way there.
            AccountSheet(onDone: { showsAccountSheet = false }, onUseOwnKey: { showsAccountSheet = false })
        }
    }

    private var initial: String {
        if let accountEmail = authSession.accountEmail { return String(accountEmail.prefix(1)).uppercased() }
        return String(NSFullUserName().prefix(1)).uppercased()
    }

    private func resend() {
        resendWork.start {
            let sent = await authSession.resendLink()
            resendNote = sent ? "Sent. Check your inbox." : "Couldn’t send it. Try again in a minute."
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
        VStack(alignment: .leading, spacing: 6) {
            if let summary = billing.summary {
                if summary.byok {
                    meterHeader(trailing: "Your keys")
                    Text("OpenClicky meters nothing: every request runs on your own key.").font(SettingsLook.caption).foregroundStyle(.secondary)
                } else if !summary.isMetered {
                    meterHeader(trailing: "Not metered")
                    Text("This backend meters nothing: every request runs without an allowance.").font(SettingsLook.caption).foregroundStyle(.secondary)
                } else if summary.isSwitchedOff {
                    meterHeader(trailing: "Paused")
                    Text(BillingSummary.switchedOffMessage)
                        .font(SettingsLook.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    meterHeader(trailing: "\(Int((summary.fractionUsed * 100).rounded()))% used")
                    ProgressView(value: min(1, max(0, summary.fractionUsed)))
                        .progressViewStyle(.linear)
                        .tint(SettingsLook.accent)
                    DisclosureGroup(isExpanded: $showsDetails) {
                        Text(summary.usageDetails())
                            .font(SettingsLook.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.top, 4)
                    } label: {
                        Text("Details").font(SettingsLook.caption).foregroundStyle(.secondary)
                    }
                }
            } else if OpenClickyConfiguration.usesOwnKeys {
                meterHeader(trailing: "Your keys")
                Text("No account needed: your keys in shell.json go straight to OpenAI and Anthropic, and nothing is metered.")
                    .font(SettingsLook.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else if !OpenClickyConfiguration.hasAccountToken {
                // No sign-in, so there is no allowance to fetch (a plan or another key can make the
                // app "configured" without one; waiting for a summary then said Loading… forever).
                meterHeader(trailing: "Sign in to see usage")
                ProgressView(value: 0).progressViewStyle(.linear).tint(SettingsLook.accent)
            } else {
                meterHeader(trailing: billing.errorText ?? "Loading…")
            }
        }
        .padding(.vertical, 2)
        .onAppear { billing.refresh() }
        .onChange(of: shellSettingsRevision.revision) { _, _ in billing.refresh() }
    }

    private func meterHeader(trailing: String) -> some View {
        HStack {
            Text("Free allowance").font(SettingsLook.rowTitle)
            Spacer()
            Text(trailing).font(.system(size: 12)).foregroundStyle(.secondary)
        }
    }
}

private struct BackendEngineUsageRow: View {
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        let detail: String
        let badge: String
        if OpenClickyConfiguration.usesOwnKeys {
            detail = OpenClickyConfiguration.hasAccountToken
                ? "OpenAI runs straight on your own key, not metered. AssemblyAI is on your account."
                : "OpenAI runs straight on your own key, not metered. AssemblyAI needs an account."
            badge = "Your key"
        } else if OpenClickyConfiguration.hasAccountToken {
            detail = "Through OpenClicky, counted against your free allowance."
            badge = "Allowance"
        } else {
            detail = "Through OpenClicky. Needs an account or your own OpenAI key."
            badge = "Needs account"
        }
        return SettingsRow(title: "OpenAI and AssemblyAI", detail: detail) {
            Text(badge).font(.system(size: 12, design: .monospaced)).foregroundStyle(.secondary)
        }
    }
}

// MARK: - your keys

/// A provider a key can be saved for: where it lives in shell.json and how its row reads.
/// Not private: the Spotify row's mapping is unit-tested.
enum KeyProvider: String, CaseIterable, Identifiable {
    case openAI, anthropic, openRouter, gemini, groq, mistral, xai, sarvam, elevenLabs, googleCloud, amazonPolly, spotify

    var id: String { rawValue }

    var title: String {
        switch self {
        case .openAI: return "OpenAI"
        case .anthropic: return "Anthropic (Claude)"
        case .openRouter: return "OpenRouter"
        case .gemini: return "Google Gemini"
        case .groq: return "Groq"
        case .mistral: return "Mistral"
        case .xai: return "xAI (Grok)"
        case .sarvam: return "Sarvam"
        case .elevenLabs: return "ElevenLabs"
        case .googleCloud: return "Google Cloud voice"
        case .amazonPolly: return "Amazon Polly"
        case .spotify: return "Spotify"
        }
    }

    var detail: String {
        switch self {
        case .openAI: return "Answers, fast voice and hands-free."
        case .anthropic: return "Claude answers your questions about the screen."
        case .openRouter: return "One key for Claude, GPT, Gemini, Llama and more."
        case .gemini: return "Answers and a voice. From Google AI Studio."
        case .groq: return "Very fast answers on open models."
        case .mistral: return "Mistral’s own models."
        case .xai: return "Grok, from xAI."
        case .sarvam: return "Speaks answers in a natural voice (Bulbul), and the Sarvam dictation engine for Indian languages."
        case .elevenLabs: return "Speaks answers in ElevenLabs’ voice, on your own ElevenLabs account."
        case .googleCloud: return "Speaks answers. A Google Cloud API key with the Text-to-Speech API switched on; an AI Studio key won’t do."
        case .amazonPolly: return "Speaks answers in Amazon’s neural voices. Paste access key ID:secret key, then :region if it isn’t us-east-1."
        case .spotify: return "Finds the song you asked for. Paste client id:client secret from developer.spotify.com; without it OpenClicky’s own lookup is used."
        }
    }

    var placeholder: String {
        switch self {
        case .openAI: return "sk-…"
        case .anthropic: return "sk-ant-…"
        case .openRouter: return "sk-or-…"
        case .gemini: return "AIza…"
        case .groq: return "gsk_…"
        case .mistral: return "Mistral key"
        case .xai: return "xai-…"
        case .sarvam: return "Sarvam key"
        case .elevenLabs: return "sk_…"
        case .googleCloud: return "AIza…"
        case .amazonPolly: return "AKIA…:secret"
        case .spotify: return "id:secret"
        }
    }

    var keyPath: WritableKeyPath<OpenClickyShellSettings, String?> {
        switch self {
        case .openAI: return \.openaiApiKey
        case .anthropic: return \.anthropicApiKey
        case .openRouter: return \.openRouterApiKey
        case .gemini: return \.geminiApiKey
        case .groq: return \.groqApiKey
        case .mistral: return \.mistralApiKey
        case .xai: return \.xaiApiKey
        case .sarvam: return \.sarvamKey
        case .elevenLabs: return \.elevenLabsApiKey
        case .googleCloud: return \.googleCloudApiKey
        case .amazonPolly: return \.amazonPollyCredentials
        case .spotify: return \.spotifyCredentials
        }
    }

    /// The source this key answers questions as; the voice-only keys answer nothing.
    var answerSource: AnswerSource? {
        switch self {
        case .openAI: return .openAI
        case .anthropic: return .anthropic
        case .openRouter: return .openRouter
        case .gemini: return .gemini
        case .groq: return .groq
        case .mistral: return .mistral
        case .xai: return .xai
        case .sarvam, .elevenLabs, .googleCloud, .amazonPolly, .spotify: return nil
        }
    }

    /// The voice this key speaks answers in, for its Test button.
    var speechVoice: SpeechVoiceChoice? {
        switch self {
        case .sarvam: return .sarvam
        case .elevenLabs: return .elevenLabs
        case .googleCloud: return .google
        case .amazonPolly: return .polly
        default: return nil
        }
    }

    var savedKey: String? {
        let key = (OpenClickyConfiguration.settings[keyPath: keyPath] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return key.isEmpty ? nil : key
    }
}

/// Your keys: one row per saved key (Test, Remove) and an "Add a key" row whose menu holds the
/// providers without one, so the list stays short however many providers there are.
private struct ProviderKeysList: View {
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @State private var newProvider: KeyProvider = .openAI
    @State private var newKey = ""
    @State private var addStatus: String?
    /// What the last Test or Save said, per provider.
    @State private var rowStatus: [KeyProvider: String] = [:]
    @State private var checking: Set<KeyProvider> = []
    @State private var pendingRemoval: KeyProvider?
    @ObservedObject private var dictationSettings = DictationSettings.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        let saved = KeyProvider.allCases.filter { $0.savedKey != nil }
        // Sarvam is for Indian languages: offered to add only in India (a saved one always shows).
        let unsaved = KeyProvider.allCases.filter { $0.savedKey == nil && ($0 != .sarvam || dictationSettings.region.offersIndianOptions) }
        // A group, not a stack: the form draws each key as its own row, with its separators.
        Group {
            ForEach(saved) { provider in savedRow(provider) }
            Group {
                if unsaved.isEmpty {
                    SettingsRow(title: "Every provider has a key", detail: addStatus) { EmptyView() }
                } else {
                    addRow(unsaved)
                }
            }
            .onAppear { if !unsaved.contains(newProvider), let first = unsaved.first { newProvider = first } }
            .confirmationDialog("Remove the \(pendingRemoval?.title ?? "") key?", isPresented: Binding(
                get: { pendingRemoval != nil }, set: { if !$0 { pendingRemoval = nil } })) {
                Button("Remove", role: .destructive) { if let pendingRemoval { remove(pendingRemoval) } }
            } message: {
                Text("It’s deleted from ~/.openclicky/shell.json. Keep a copy if you’ll need it again.")
            }
        }
    }

    private func savedRow(_ provider: KeyProvider) -> some View {
        let masked = "••••" + String((provider.savedKey ?? "").suffix(4))
        return SettingsRow(title: provider.title, detail: rowStatus[provider] ?? "\(provider.detail) \(masked)") {
            HStack(spacing: 8) {
                Button(checking.contains(provider) ? "Testing…" : "Test") { test(provider) }
                    .disabled(checking.contains(provider))
                    .help(provider.speechVoice == nil ? "Ask it what colour a red square is" : "Have it say a word")
                    .pointerCursor(isEnabled: !checking.contains(provider))
                Button("Remove") { pendingRemoval = provider }
                    .pointerCursor()
            }
        }
    }

    private func addRow(_ unsaved: [KeyProvider]) -> some View {
        SettingsRow(title: "Add a key", detail: addStatus ?? newProvider.detail) {
            HStack(spacing: 8) {
                Picker("Provider", selection: $newProvider) {
                    ForEach(unsaved) { Text($0.title).tag($0) }
                }
                .labelsHidden()
                .fixedSize()
                .onChange(of: newProvider) { _, _ in addStatus = nil }
                .pointerCursor()
                SecureField("", text: $newKey, prompt: Text(newProvider.placeholder))
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 130)
                    .onSubmit(add)
                    .accessibilityLabel("\(newProvider.title) key")
                Button("Add", action: add)
                    .fixedSize()
                    .disabled(newKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    .pointerCursor(isEnabled: !(newKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty))
            }
            .layoutPriority(1)
        }
    }

    private func add() {
        let provider = newProvider
        let trimmed = newKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        save(trimmed, for: provider)
        newKey = ""
        addStatus = nil
        if let next = KeyProvider.allCases.first(where: { $0.savedKey == nil }) { newProvider = next }
        // Find out straight away whether it works.
        test(provider)
    }

    private func remove(_ provider: KeyProvider) {
        save(nil, for: provider)
        rowStatus[provider] = nil
        addStatus = "\(provider.title) key removed."
        newProvider = provider
    }

    private func save(_ key: String?, for provider: KeyProvider) {
        OpenClickyConfiguration.update { $0[keyPath: provider.keyPath] = key }
        // A changed key is a changed credential: a live realtime session reconnects with it.
        NotificationCenter.default.post(name: OpenClickyConfiguration.credentialsChangedNotification, object: nil)
        ShellSettingsRevision.shared.noteChanged()
    }

    private func test(_ provider: KeyProvider) {
        guard provider.savedKey != nil else { return }
        checking.insert(provider)
        rowStatus[provider] = "Checking…"
        Task {
            let sentence: String
            if let source = provider.answerSource {
                sentence = await AnswerSourceCheck.run(source, credentials: ProviderRouter.currentCredentials(),
                                                       modelOverrides: OpenClickyConfiguration.settings.answerModels ?? [:]).sentence
            } else if let voice = provider.speechVoice {
                // A voice key is checked by having it say one word.
                do {
                    _ = try await CloudVoiceSpeaker.audio(for: "ok", voice: voice)
                    sentence = "✓ Works. It can speak your answers."
                } catch {
                    sentence = error.localizedDescription
                }
            } else if provider == .spotify {
                // Spotify credentials are checked by looking one song up.
                let route = MusicLookupRoute.route(credentials: provider.savedKey, backendURL: nil)
                let tracks = await MusicLookup(route: route).search("test")
                sentence = tracks.first.map { "✓ Works. Found \($0.name)." } ?? "✗ Spotify refused the credentials."
            } else {
                sentence = "Saved."
            }
            rowStatus[provider] = sentence
            checking.remove(provider)
        }
    }
}

// MARK: - subscriptions

/// A plan the person already pays for: whether its CLI is here and signed in, and the switch that
/// lets it answer questions.
private struct PlanSubscriptionRow: View {
    let plan: AnswerSource
    @State private var status: PlanCLIStatus?
    @State private var isOn = false
    @State private var checkSentence: String?
    @State private var isChecking = false

    private var title: String { plan.displayName }

    var body: some View {
        SettingsRow(title: title, detail: detail) {
            HStack(spacing: 8) {
                if status == .ready {
                    Button(isChecking ? "Testing…" : "Test", action: test)
                        .disabled(isChecking)
                        .help("Ask it what colour a red square is")
                        .pointerCursor(isEnabled: !isChecking)
                }
                if status == .notInstalled {
                    Button("Get It ↗") { openExternalLink(plan == .claudeCodePlan ? "https://claude.com/product/claude-code" : "https://developers.openai.com/codex/cli") }
                        .pointerCursor()
                }
                SettingsSwitch(isOn: Binding(get: { isOn }, set: { newValue in
                    isOn = newValue
                    OpenClickyConfiguration.update { settings in
                        if plan == .claudeCodePlan { settings.useClaudeCodePlan = newValue } else { settings.useCodexPlan = newValue }
                    }
                    ShellSettingsRevision.shared.noteChanged()
                }))
                .disabled(status != .ready && !isOn)
            }
        }
        .task {
            isOn = plan == .claudeCodePlan ? OpenClickyConfiguration.settings.useClaudeCodePlan == true : OpenClickyConfiguration.settings.useCodexPlan == true
            status = plan == .claudeCodePlan ? await PlanCLIs.claudeCodeStatus() : await PlanCLIs.codexStatus()
        }
    }

    private func test() {
        isChecking = true
        checkSentence = "Checking… a plan takes a few seconds."
        Task {
            checkSentence = await AnswerSourceCheck.run(plan, credentials: ProviderRouter.currentCredentials(),
                                                        modelOverrides: OpenClickyConfiguration.settings.answerModels ?? [:]).sentence
            isChecking = false
        }
    }

    private var detail: String {
        if let checkSentence { return checkSentence }
        let command = plan == .claudeCodePlan ? "claude" : "codex login"
        switch status {
        case .none: return "Checking…"
        case .notInstalled?: return plan == .claudeCodePlan ? "Claude Code isn’t installed on this Mac." : "Codex isn’t installed on this Mac."
        case .installedSignedOut?: return "Installed, not signed in. Run “\(command)” in Terminal and sign in."
        case .ready?: return isOn ? "Signed in. Answering with your plan when it’s the one picked (Settings → Clicky)." : "Signed in. Switch on to answer with your plan."
        }
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
            SettingsRow(title: "Host", detail: OpenClickyConfiguration.backendHostDescription) {
                Button("Show shell.json") { OpenClickyConfiguration.revealSettingsFile() }
                    .help(OpenClickyConfiguration.backendBaseURL)
                    .pointerCursor()
            }
        case .token:
            SettingsRow(title: "Session token", detail: "Written when you sign in.") {
                Text(maskedToken).font(SettingsLook.key).foregroundStyle(.secondary)
            }
        }
    }

    private var maskedToken: String {
        guard let token = OpenClickyConfiguration.token else { return "None" }
        return "••••" + String(token.suffix(4))
    }
}
