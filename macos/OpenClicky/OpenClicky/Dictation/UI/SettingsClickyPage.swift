//
//  SettingsClickyPage.swift
//  OpenClicky
//
//  Settings → Clicky and Settings → Voice. Clicky: who answers (the source and the Claude
//  model), the pointer (shown or not, how visible), and agent mode with its
//  workspace. Voice: the language Clicky speaks and listens in, who speaks the answers, and fast
//  voice with hands-free. Voice has its own page because "where do I change the voice" is the
//  question people come to Settings with.
//

import AppKit
import Combine
import AVFoundation
import SwiftUI

extension SettingsCatalog {
    /// Settings → Clicky: who answers, the pointer, and agent mode. How Clicky sounds is on Voice.
    func clickyItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "clicky.answerSource", page: .clicky, section: "Answers",
                title: "Answers come from", detail: "Automatic uses your own keys first, then plans you switched on, then the free credits.",
                keywords: ["provider", "model", "openai", "anthropic", "claude", "openrouter", "gemini", "groq", "mistral", "grok", "codex", "subscription", "plan", "answers"],
                view: AnyView(AnswerSourceRow())),
            SettingsItem(
                id: "clicky.model", page: .clicky, section: "Answers",
                title: "Claude model", detail: "When Claude answers: Sonnet is quicker, Opus thinks harder.",
                keywords: ["model", "claude", "sonnet", "opus", "smarter", "quicker"],
                view: AnyView(ClaudeModelRow(companionManager: companionManager))),
            SettingsItem(
                id: "clicky.onThisMac", page: .clicky, section: "Answers",
                title: OnThisMacRow.title, detail: OnThisMacRow.detail,
                keywords: ["apple intelligence", "on device", "local", "private", "offline", "this mac", "credits", "quick actions", "open app", "volume", "rewrite"],
                view: AnyView(OnThisMacRow(settings: settings))),
            SettingsItem(
                id: "clicky.showPointer", page: .clicky, section: "Pointer",
                title: "Show the pointer", detail: "Off: it appears only while you talk.",
                keywords: ["pointer", "cursor", "buddy", "orange", "visible", "hide"],
                view: AnyView(CompanionSwitchRow(
                    title: "Show the pointer", detail: "Off: it appears only while you talk.",
                    publisher: companionManager.$isClickyCursorEnabled.eraseToAnyPublisher(),
                    initialValue: companionManager.isClickyCursorEnabled,
                    set: { [companionManager] in companionManager.setClickyCursorEnabled($0) }))),
            SettingsItem(
                id: "clicky.pointerPresence", page: .clicky, section: "Pointer",
                title: "The pointer shows", detail: "Always, while the mouse moves, or after a shake.",
                keywords: ["pointer", "cursor", "always", "while the mouse moves", "after a shake", "shake", "hide"],
                view: AnyView(PointerPresenceOptions(companionManager: companionManager))),
            SettingsItem(
                id: "clicky.agentMode", page: .clicky, section: "Agent",
                title: "Agent mode", detail: "Clicky can hand work to Codex.",
                keywords: ["agent", "codex", "tasks", "automation"],
                view: AnyView(CompanionSwitchRow(
                    title: "Agent mode", detail: "Clicky can hand work to Codex.",
                    publisher: companionManager.$isAgentModeEnabled.eraseToAnyPublisher(),
                    initialValue: companionManager.isAgentModeEnabled,
                    set: { [companionManager] in companionManager.setAgentModeEnabled($0) }))),
            SettingsItem(
                id: "clicky.workspace", page: .clicky, section: "Agent",
                title: "Workspace", detail: "Where agents keep their files.",
                keywords: ["workspace", "folder", "codex", "files", "directory"],
                view: AnyView(AgentWorkspaceRow())),
        ]
    }

    /// Settings → Voice: the language Clicky speaks and listens in, who speaks the answers (Kokoro
    /// and the Mac voice with their own rows), and fast voice with hands-free.
    func voiceItems() -> [SettingsItem] {
        let usesRealtime = AccountCapabilities.current().usesRealtime
        var items: [SettingsItem] = [
            SettingsItem(
                id: "clicky.language", page: .voice, section: "Language",
                title: "Language", detail: "Clicky speaks and listens in this.",
                keywords: ["language", "english", "hindi", "tamil", "spanish", "french", "speak", "reply", "listen"],
                view: AnyView(ReplyLanguageRow(settings: settings))),
            SettingsItem(
                id: "clicky.speechVoice", page: .voice, section: "Voice",
                title: "Voice", detail: "Who speaks the answers: the cloud voice, or this Mac’s own voice, free and offline.",
                keywords: ["voice", "speech", "tts", "text to speech", "elevenlabs", "eleven labs", "offline", "on device",
                           "mac voice", "premium voice", "siri", "credits", "allowance", "left"],
                view: AnyView(SpeechVoiceRows(openKeysPage: { [companionManager] in companionManager.showSettings(.keys) }))),
        ]
        // Fast voice runs on OpenAI Realtime or Gemini Live, which need an own OpenAI or Gemini key;
        // without one its switch would do nothing, so the row shows the way to a key instead.
        if usesRealtime {
            items += [
                SettingsItem(
                    id: "clicky.fastVoice", page: .voice, section: "Fast voice",
                    title: "Fast voice", detail: "Talks back instantly, over OpenAI Realtime or Gemini Live.",
                    keywords: ["realtime", "voice", "fast", "openai", "gemini", "live"],
                    view: AnyView(CompanionSwitchRow(
                        title: "Fast voice", detail: "Talks back instantly, over OpenAI Realtime or Gemini Live.",
                        publisher: companionManager.$isRealtimeVoiceEnabled.eraseToAnyPublisher(),
                        initialValue: companionManager.isRealtimeVoiceEnabled,
                        set: { [companionManager] in companionManager.setRealtimeVoiceEnabled($0) }))),
            ]
            // Only worth a choice when both can run here.
            let credentials = ProviderCredentials(settings: OpenClickyConfiguration.settings)
            if credentials.geminiKey != nil, credentials.openAIKey != nil || credentials.accountToken != nil {
                items.append(SettingsItem(
                    id: "clicky.realtimeProvider", page: .voice, section: "Fast voice",
                    title: "Fast voice with", detail: "Who talks back in fast voice and hands-free.",
                    keywords: ["realtime", "gemini", "openai", "live", "provider", "fast voice", "hands-free"],
                    view: AnyView(RealtimeProviderRow(changed: { [companionManager] in companionManager.realtimeProviderChanged() }))))
            }
        }
        // Hands-free works on every lane that can answer: instantly with fast voice, otherwise
        // the standard lane hears each turn on this Mac once the person stops talking.
        if usesRealtime || OpenClickyConfiguration.isConfigured {
            let detail = usesRealtime
                ? "Hands-free: just talk, no keys. Instant with fast voice on."
                : "Hands-free: just talk, no keys. Clicky answers once you stop talking."
            items.append(SettingsItem(
                id: "clicky.alwaysListening", page: .voice, section: "Fast voice",
                title: "Always listening", detail: detail,
                keywords: ["hands-free", "hands free", "always on", "listening"],
                view: AnyView(CompanionSwitchRow(
                    title: "Always listening", detail: detail,
                    publisher: companionManager.$isAlwaysListening.eraseToAnyPublisher(),
                    initialValue: companionManager.isAlwaysListening,
                    set: { [companionManager] in companionManager.setAlwaysListening($0) }))))
        }
        if !usesRealtime {
            items.append(SettingsItem(
                id: "clicky.fastVoiceNeedsKey", page: .voice, section: "Fast voice",
                title: "Fast voice", detail: "Instant replies need your own OpenAI or Gemini key. Without one, Clicky answers in the standard voice.",
                keywords: ["realtime", "voice", "fast", "hands-free", "always listening", "openai", "gemini", "key"],
                view: AnyView(SettingsRow(title: "Fast voice", detail: "Instant replies need your own OpenAI or Gemini key. Without one, Clicky answers in the standard voice.") {
                    Button("Add Key…") { companionManager.showSettings(.keys) }
                        .pointerCursor()
                })))
        }
        // "Check my email" and "draft an email": which inbox they go to.
        items += [
            SettingsItem(
                id: "clicky.emailApp", page: .voice, section: "Doing things",
                title: "Email", detail: "Which inbox “check my email” and “draft an email” use.",
                keywords: ["email", "mail", "gmail", "inbox", "draft", "apple mail"],
                view: AnyView(EmailAppRow())),
            SettingsItem(
                id: "clicky.emailNeverSent", page: .voice, section: "Doing things",
                title: EmailAppRow.footerText, keywords: ["send", "email"],
                chrome: .footer,
                view: AnyView(Text(EmailAppRow.footerText).font(SettingsLook.caption).foregroundStyle(.secondary))),
        ]
        return items
    }
}

/// Which inbox "check my email" and "draft an email" use: automatic (Apple Mail when it has
/// accounts here, else Gmail), Apple Mail, or Gmail. `emailApp` in shell.json.
struct EmailAppRow: View {
    static let footerText = "Nothing is ever sent by voice."
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    /// The picker's tag for a saved setting; anything unknown shows as automatic.
    static func tag(forSetting setting: String?) -> String {
        guard let setting, EmailApp(rawValue: setting) != nil else { return "auto" }
        return setting
    }

    /// The setting a picked tag saves; automatic saves nothing.
    static func settingValue(forTag tag: String) -> String? {
        EmailApp(rawValue: tag)?.rawValue
    }

    var body: some View {
        let _ = shellSettingsRevision.revision
        SettingsRow(title: "Email", detail: "Which inbox “check my email” and “draft an email” use. Automatic is Apple Mail when it’s set up, else Gmail.") {
            Picker("", selection: Binding(
                get: { Self.tag(forSetting: OpenClickyConfiguration.settings.emailApp) },
                set: { tag in
                    OpenClickyConfiguration.update { $0.emailApp = Self.settingValue(forTag: tag) }
                    ShellSettingsRevision.shared.noteChanged()
                }
            )) {
                Text("Automatic").tag("auto")
                Text("Apple Mail").tag("mail")
                Text("Gmail").tag("gmail")
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
        }
    }
}

/// Who runs fast voice and hands-free when both an OpenAI and a Gemini key (or an account) are
/// here: automatic (OpenAI first), OpenAI Realtime or Gemini Live. `realtimeProvider` in shell.json.
struct RealtimeProviderRow: View {
    let changed: () -> Void
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    /// The picker's tag for a saved setting; anything unknown shows as automatic.
    static func tag(forSetting setting: String?) -> String {
        guard let setting, RealtimeVoiceProvider(rawValue: setting) != nil else { return "auto" }
        return setting
    }

    var body: some View {
        let _ = shellSettingsRevision.revision
        SettingsRow(title: "Fast voice with", detail: "Who talks back in fast voice and hands-free. Automatic is OpenAI, whose pointing is tuned best.") {
            Picker("", selection: Binding(
                get: { Self.tag(forSetting: OpenClickyConfiguration.settings.realtimeProvider) },
                set: { tag in
                    OpenClickyConfiguration.update { $0.realtimeProvider = RealtimeVoiceProvider(rawValue: tag)?.rawValue }
                    ShellSettingsRevision.shared.noteChanged()
                    changed()
                }
            )) {
                Text("Automatic").tag("auto")
                ForEach(RealtimeVoiceProvider.allCases, id: \.rawValue) { provider in
                    Text(provider == .openAI ? "OpenAI Realtime" : "Gemini Live").tag(provider.rawValue)
                }
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
        }
    }
}

/// A switch for one of the companion's own settings. It follows the one published value instead
/// of observing the whole companion manager, which republishes on every audio level.
private struct CompanionSwitchRow: View {
    let title: String
    let detail: String
    let publisher: AnyPublisher<Bool, Never>
    let set: (Bool) -> Void
    @State private var isOn: Bool

    init(title: String, detail: String, publisher: AnyPublisher<Bool, Never>, initialValue: Bool, set: @escaping (Bool) -> Void) {
        self.title = title
        self.detail = detail
        self.publisher = publisher
        self.set = set
        _isOn = State(initialValue: initialValue)
    }

    var body: some View {
        SettingsRow(title: title, detail: detail) {
            SettingsSwitch(isOn: Binding(get: { isOn }, set: { set($0) }))
        }
        .onReceive(publisher) { isOn = $0 }
    }
}

/// Always / while the mouse moves / after a shake, as radio options under "Show the pointer".
private struct PointerPresenceOptions: View {
    let companionManager: CompanionManager
    @State private var presence: PointerPresence
    @State private var isPointerShown: Bool
    @Environment(\.settingsSearchQuery) private var searchQuery

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        _presence = State(initialValue: companionManager.pointerPresence)
        _isPointerShown = State(initialValue: companionManager.isClickyCursorEnabled)
    }

    private func title(for option: PointerPresence) -> String {
        switch option {
        case .always: return "Always"
        case .whileMoving: return "While the mouse moves"
        case .onShake: return "After a shake"
        }
    }

    private func detail(for option: PointerPresence) -> String {
        switch option {
        case .always: return "Follows the mouse everywhere."
        case .whileMoving: return "Fades a few seconds after the mouse rests."
        case .onShake: return "Hidden until you shake, or until Clicky needs it."
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(PointerPresence.allCases) { option in
                Button(action: { companionManager.setPointerPresence(option) }) {
                    HStack(alignment: .top, spacing: 10) {
                        SettingsRadioDot(isSelected: presence == option).padding(.top, 1)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(settingsHighlighted(title(for: option), query: searchQuery)).font(SettingsLook.rowTitle)
                            Text(settingsHighlighted(detail(for: option), query: searchQuery))
                                .font(SettingsLook.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(title(for: option))
                .accessibilityHint(detail(for: option))
                .accessibilityAddTraits(presence == option ? .isSelected : [])
                .pointerCursor()
            }
        }
        .padding(.vertical, 4)
        .disabled(!isPointerShown)
        .opacity(isPointerShown ? 1 : 0.45)
        .onReceive(companionManager.$pointerPresence) { presence = $0 }
        .onReceive(companionManager.$isClickyCursorEnabled) { isPointerShown = $0 }
    }
}

/// Who answers questions: automatic, or one source that is set up. Shows who is answering now.
/// Settings → Clicky → Answers: the local ask rung's switch. The detail says what the rung does and,
/// when Apple's model is not available on this Mac, why not (the switch is then disabled); a
/// person whose ⌃⌥ goes to the Realtime lane is told it does not apply to them. Read when drawn,
/// as the "Answers come from" row reads the credentials.
struct OnThisMacRow: View {
    @ObservedObject var settings: DictationSettings

    static let title = "Simple requests stay on this Mac"
    static let detail = "Opening apps and websites, folders, volume, playback and rewrites are understood by Apple Intelligence on this Mac: no screenshot is sent and no answer credits are used."
    static let realtimeNote = "Not used while your OpenAI key answers out loud."

    var body: some View {
        let unavailableReason = LocalAskModelAvailability.unavailableReason()
        let goesToRealtime = OpenClickyConfiguration.isConfigured && AccountCapabilities.current().usesRealtime
        SettingsRow(title: Self.title, detail: Self.detailShown(unavailableReason: unavailableReason, goesToRealtime: goesToRealtime)) {
            SettingsToggle(settings: settings, keyPath: \.answerSimpleRequestsOnThisMac)
                .disabled(unavailableReason != nil)
        }
    }

    static func detailShown(unavailableReason: String?, goesToRealtime: Bool) -> String {
        if let unavailableReason { return "Not available: \(unavailableReason)." }
        return goesToRealtime ? detail + " " + realtimeNote : detail
    }
}

private struct AnswerSourceRow: View {
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        let credentials = ProviderRouter.currentCredentials()
        let available = credentials.answerSourcesInOrder
        let picked = OpenClickyConfiguration.settings.answerSource.flatMap(AnswerSource.init(rawValue:))
        SettingsRow(title: "Answers come from", detail: "Now: \(nowAnswering(credentials))") {
            Picker("", selection: Binding(
                get: { picked.map(\.rawValue) ?? "auto" },
                set: { choice in
                    OpenClickyConfiguration.update { $0.answerSource = choice == "auto" ? nil : choice }
                    ShellSettingsRevision.shared.noteChanged()
                }
            )) {
                Text("Automatic").tag("auto")
                if !available.isEmpty { Divider() }
                ForEach(available) { source in Text(source.displayName).tag(source.rawValue) }
                // A pick that is no longer set up stays visible, so the menu does not jump.
                if let picked, !available.contains(picked) { Text("\(picked.displayName) (not set up)").tag(picked.rawValue) }
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
        }
    }

    private func nowAnswering(_ credentials: ProviderCredentials) -> String {
        switch ProviderRouter.route(for: .claude, credentials: credentials) {
        case .anthropicDirect: return AnswerSource.anthropic.displayName
        case .backend: return AnswerSource.openClickyAccount.displayName
        case .answerProvider(let source): return source.displayName
        case .openAIDirect: return AnswerSource.openAI.displayName
        case .unavailable: return "nothing set up yet"
        }
    }
}

/// Who speaks the answers (`speechVoice` in shell.json, SpeechVoiceChoice): Automatic, every
/// voice there is a key or an account for, Kokoro (picking it downloads it) and this Mac. The line
/// under it names who speaks now and what it costs. Kokoro and the Mac voice each get their own rows
/// under it; a group, so the form draws them as rows.
private struct SpeechVoiceRows: View {
    let openKeysPage: () -> Void
    @ObservedObject private var dictationSettings = DictationSettings.shared
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @ObservedObject private var kokoro = KokoroVoice.shared
    @StateObject private var billing = BillingStatusModel()

    var body: some View {
        let _ = shellSettingsRevision.revision
        let _ = kokoro.state
        let settings = OpenClickyConfiguration.settings
        let picked = SpeechVoiceChoice(stored: settings.speechVoice)
        let offered = SpeechVoiceChoice.offered(settings: settings, hasAccount: OpenClickyConfiguration.hasAccountToken,
                                                languageCode: ReplyLanguage.currentCode)
        Group {
            SettingsRow(title: "Voice", detail: "Now: \(nowSpeakingSentence(picked: picked))") {
                Picker("", selection: Binding(
                    get: { picked },
                    set: { choice in
                        OpenClickyConfiguration.update { $0.speechVoice = choice.storedValue }
                        ShellSettingsRevision.shared.noteChanged()
                        // Picking Kokoro downloads it if it isn't yet, then warms it up.
                        if choice == .kokoro { KokoroVoice.shared.download() }
                        KokoroVoice.shared.warmUpIfSpeaking()
                    }
                )) {
                    ForEach(offered) { Text($0.title).tag($0) }
                }
                .labelsHidden().fixedSize()
                .pointerCursor()
            }
            .onAppear { billing.refresh() }
            // Also while the automatic download runs, so it can be watched or cancelled.
            if picked == .kokoro || kokoro.state.isBusy { KokoroRows() }
            if SpeechVoiceChoice.current() == .mac || picked == .mac { MacVoiceRow() }
            if offered.count < SpeechVoiceChoice.allCases.count {
                SettingsRow(title: "More voices", detail: moreVoicesSentence) {
                    Button("Add a Key…", action: openKeysPage)
                        .fixedSize()
                        .pointerCursor()
                }
            }
        }
    }

    /// Which keys bring more voices. Sarvam's voice speaks Indian languages, so it is named only in India.
    private var moreVoicesSentence: String {
        let vendors = dictationSettings.region.offersIndianOptions
            ? "OpenAI, ElevenLabs, Gemini, Google Cloud, Amazon Polly or Sarvam"
            : "OpenAI, ElevenLabs, Gemini, Google Cloud or Amazon Polly"
        return "Add an \(vendors) key under Keys & Plans, and its voice shows up here."
    }

    /// Who speaks now and what it costs, for the picked choice.
    private func nowSpeakingSentence(picked: SpeechVoiceChoice) -> String {
        let speaking = SpeechVoiceChoice.current()
        if picked != .automatic && picked != speaking {
            switch picked {
            case .kokoro: return "this Mac’s voice until Kokoro is downloaded."
            default: return "this Mac’s voice: \(picked.title) needs its key under Keys & Plans."
            }
        }
        switch speaking {
        case .openAI: return "OpenAI, on your own key."
        case .elevenLabs: return "ElevenLabs, on your own key."
        case .google: return "Google Cloud, on your own key."
        case .gemini: return "Google Gemini, on your own key."
        case .polly: return "Amazon Polly, on your own key."
        case .sarvam: return "Sarvam, on your own key."
        case .kokoro: return "Kokoro, on this Mac. Free, offline, nothing counted."
        case .mac, .automatic: return "this Mac’s voice. Free, offline, nothing counted."
        case .account: return freeVoiceSentence
        }
    }

    /// The free voice and what is left of its allowance.
    private var freeVoiceSentence: String {
        guard let summary = billing.summary, summary.isMetered, !summary.byok else { return "ElevenLabs, through OpenClicky." }
        if summary.isSwitchedOff { return "ElevenLabs, on the free credits, which are used up for now. This Mac’s voice works meanwhile." }
        // The voice has its own allowance in spoken characters, apart from the money one: it
        // can run out while most of the money is left.
        if summary.isUnconfirmed { return "ElevenLabs, on the starter voice allowance. Add your email under Account to keep it." }
        guard summary.ttsCharsLimit > 0 else { return "ElevenLabs, on the free credits." }
        let left = max(0, 100 - Int((Double(summary.ttsCharsMonth) / Double(summary.ttsCharsLimit) * 100).rounded()))
        if left == 0 { return "ElevenLabs, on the free credits. This month’s voice allowance is used, so this Mac speaks until it comes back." }
        return "ElevenLabs, on the free credits. \(left)% of this month’s voice allowance is left."
    }
}

/// Kokoro's rows: the download (progress, cancel, retry), then which speaker, a preview, and
/// removing the download.
private struct KokoroRows: View {
    @ObservedObject private var kokoro = KokoroVoice.shared
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        switch kokoro.state {
        case .notInstalled:
            SettingsRow(title: "Kokoro", detail: "An open voice model that runs on this Mac, free and offline. \(KokoroVoice.downloadSizeText) to download.") {
                Button("Download") { kokoro.download() }.fixedSize().pointerCursor()
            }
        case .downloading(let fraction):
            SettingsRow(title: "Downloading Kokoro", detail: "\(Int((fraction * 100).rounded()))% of \(KokoroVoice.downloadSizeText). Answers keep the current voice until it’s ready.") {
                HStack(spacing: 8) {
                    ProgressView(value: fraction).frame(width: 120)
                    Button("Cancel") { kokoro.cancelDownload() }.fixedSize().pointerCursor()
                }
            }
        case .unpacking:
            SettingsRow(title: "Unpacking Kokoro", detail: "Almost there.") { ProgressView().controlSize(.small) }
        case .failed(let message):
            SettingsRow(title: "Kokoro", detail: message) {
                Button("Try Again") { kokoro.download() }.fixedSize().pointerCursor()
            }
        case .installed:
            let speakers = KokoroVoice.speakers.filter { $0.languageCode == ReplyLanguage.currentCode }
            let current = KokoroVoice.speaker(forLanguage: ReplyLanguage.currentCode, picked: OpenClickyConfiguration.settings.kokoroSpeaker)
            SettingsRow(title: "Kokoro voice", detail: "The first answer after launch takes a few seconds while the model loads.") {
                HStack(spacing: 8) {
                    Picker("", selection: Binding(
                        get: { current?.id ?? -1 },
                        set: { speakerID in
                            OpenClickyConfiguration.update { $0.kokoroSpeaker = speakerID }
                            ShellSettingsRevision.shared.noteChanged()
                            VoicePreview.shared.play(.kokoro)
                        }
                    )) {
                        ForEach(speakers) { Text($0.label).tag($0.id) }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 200)
                    .pointerCursor()
                    Button("Preview") { VoicePreview.shared.play(.kokoro) }.fixedSize().pointerCursor()
                }
            }
            SettingsRow(title: "Kokoro download", detail: "About 400 MB in Application Support.") {
                Button("Remove") {
                    kokoro.remove()
                    if SpeechVoiceChoice(stored: OpenClickyConfiguration.settings.speechVoice) == .kokoro {
                        OpenClickyConfiguration.update { $0.speechVoice = nil }
                        ShellSettingsRevision.shared.noteChanged()
                    }
                }
                .fixedSize()
                .pointerCursor()
            }
        }
    }
}

/// The Mac voice: which one, a preview, and where to download the better ones.
private struct MacVoiceRow: View {
    @State private var choices = MacVoice.choices()
    @State private var pickedID = MacVoice.currentVoice()?.identifier ?? ""

    var body: some View {
        Group {
            SettingsRow(title: "Mac voice", detail: "Picking one says a line in it.") {
                HStack(spacing: 8) {
                    Picker("", selection: Binding(
                        get: { pickedID },
                        set: { identifier in
                            pickedID = identifier
                            OpenClickyConfiguration.update { $0.macVoiceIdentifier = identifier }
                            VoicePreview.shared.play(.mac)
                        }
                    )) {
                        ForEach(choices) { Text($0.label).tag($0.id) }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 220)
                    .pointerCursor()
                    Button("Preview") { VoicePreview.shared.play(.mac) }.fixedSize().pointerCursor()
                }
            }
            SettingsRow(title: "Better voices", detail: "Premium and Enhanced voices are small models macOS downloads once, then they run offline. Get Voices opens Read & Speak (Spoken Content on older macOS): System voice, Manage Voices.") {
                Button("Get Voices…") {
                    openExternalLink("x-apple.systempreferences:com.apple.Accessibility-Settings.extension?SpokenContent")
                }
                .fixedSize()
                .pointerCursor()
            }
        }
        // A voice downloaded in System Settings shows up when the window comes back.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            choices = MacVoice.choices()
        }
    }
}

/// Says one line in a voice, for the Preview buttons.
@MainActor
private final class VoicePreview {
    static let shared = VoicePreview()
    private var player: AVAudioPlayer?

    func play(_ voice: SpeechVoiceChoice) {
        Task {
            guard let data = try? await CloudVoiceSpeaker.audio(for: "hi, i'm clicky. this is how i sound.", voice: voice) else { return }
            player = try? AVAudioPlayer(data: data)
            player?.play()
        }
    }
}

/// The one language OpenClicky speaks and listens in (`language` in shell.json).
private struct ReplyLanguageRow: View {
    @ObservedObject var settings: DictationSettings
    @State private var languageCode = ReplyLanguage.currentCode

    var body: some View {
        SettingsRow(title: "Language", detail: "Clicky speaks and listens in this. Dictation has its own, on the Dictation page.") {
            Picker("", selection: Binding(
                get: { languageCode },
                set: { chosenCode in
                    languageCode = chosenCode
                    OpenClickyConfiguration.update { $0.language = chosenCode }
                }
            )) {
                ForEach(ReplyLanguage.choices(region: settings.region, currentCode: languageCode)) { choice in Text(choice.name).tag(choice.code) }
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
        }
    }
}

private struct ClaudeModelRow: View {
    let companionManager: CompanionManager
    @State private var model: String

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        _model = State(initialValue: companionManager.selectedModel)
    }

    var body: some View {
        SettingsRow(title: "Claude model", detail: "When Claude answers: Sonnet is quicker, Opus thinks harder.") {
            Picker("", selection: Binding(get: { model }, set: { companionManager.setSelectedModel($0) })) {
                Text("Sonnet").tag("claude-sonnet-4-6")
                Text("Opus").tag("claude-opus-4-6")
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .tint(SettingsLook.accent)
            .pointerCursor()
        }
        .onReceive(companionManager.$selectedModel) { model = $0 }
    }
}

/// The agents' workspace folder (`workspace` in shell.json), with a folder picker.
private struct AgentWorkspaceRow: View {
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    var body: some View {
        let _ = shellSettingsRevision.revision
        SettingsRow(title: "Workspace", detail: OpenClickyConfiguration.workspacePath.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
            HStack(spacing: 8) {
                Button("Show in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)) }
                    .pointerCursor()
                Button("Change…", action: chooseFolder)
                    .pointerCursor()
            }
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)
        panel.prompt = "Use Folder"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        OpenClickyConfiguration.update { $0.workspace = url.path }
        ShellSettingsRevision.shared.noteChanged()
    }
}
