//
//  SettingsVoicePage.swift
//  OpenClicky
//
//  Settings → voice (was engine + microphone): who hears you, through which mic. Each provider
//  names where the audio goes; the sarvam key sits inline under sarvam, only while it is the
//  chosen one. Polish and language moved to the styles page, next to the rules the model reads.
//

import AVFoundation
import SwiftUI

extension SettingsCatalog {
    func voiceItems() -> [SettingsItem] {
        let providerItems = DictationEngineChoice.allCases.map { choice in
            SettingsItem(
                id: "voice.provider.\(choice.rawValue)", page: .voice, section: "who hears you",
                title: VoiceProviderCopy.title(for: choice), detail: VoiceProviderCopy.detail(for: choice),
                keywords: VoiceProviderCopy.keywords(for: choice),
                view: AnyView(VoiceProviderCard(choice: choice, settings: settings)))
        }
        return providerItems + [
            SettingsItem(
                id: "voice.input", page: .voice, section: "microphone",
                title: "input", detail: "follows the mac’s default unless you pick one.",
                keywords: ["microphone", "mic", "device", "input", "headset", "airpods"],
                view: AnyView(MicrophoneInputRow(settings: settings))),
            SettingsItem(
                id: "voice.micTest", page: .voice, section: "microphone",
                title: "test your mic", detail: "speak — the bars should follow your voice.",
                keywords: ["microphone", "mic", "level", "test"],
                view: AnyView(MicrophoneTestRow(settings: settings))),
            SettingsItem(
                id: "voice.movedToStyles", page: .voice, section: "microphone",
                title: "looking for polish or language?", detail: "they moved to styles, next to the rules the model reads.",
                keywords: ["polish", "language", "model", "cleanup", "styles", "offline takes"],
                chrome: .bare,
                view: AnyView(MovedToStylesNote(windowModel: windowModel))),
        ]
    }
}

/// The provider cards' copy: each one says who actually hears you.
enum VoiceProviderCopy {
    static func title(for choice: DictationEngineChoice) -> String {
        switch choice {
        case .offline: return "this mac"
        case .sarvam: return "sarvam"
        case .openclicky: return "openai, via openclicky"
        case .assemblyai: return "assemblyai, via openclicky"
        }
    }

    static func tag(for choice: DictationEngineChoice) -> String {
        switch choice {
        case .offline: return "private · offline"
        case .sarvam: return "audio goes to sarvam"
        case .openclicky: return "audio goes to openai"
        case .assemblyai: return "audio goes to assemblyai"
        }
    }

    static func detail(for choice: DictationEngineChoice) -> String {
        switch choice {
        case .offline: return "apple’s on-device recogniser. nothing leaves this mac. no key, no account."
        case .sarvam: return "saaras hears eleven indian languages. billed by sarvam to your key."
        case .openclicky: return "needs an openclicky account or your own openai key."
        case .assemblyai: return "words appear as you speak. needs the openclicky backend."
        }
    }

    /// The short badge on the right while the provider can't be used yet.
    static func unavailableBadge(for choice: DictationEngineChoice) -> String {
        switch choice {
        case .offline: return ""
        case .sarvam: return "needs a key"
        case .openclicky: return "needs account"
        case .assemblyai: return "needs backend"
        }
    }

    static func keywords(for choice: DictationEngineChoice) -> [String] {
        let common = ["engine", "provider", "transcription", "speech", "who hears you", tag(for: choice)]
        switch choice {
        case .offline: return common + ["offline", "local", "apple", "private", "on device"]
        case .sarvam: return common + ["sarvam", "saaras", "key", "api key", "indian", "hindi"]
        case .openclicky: return common + ["openai", "openclicky", "account", "backend", "cloud"]
        case .assemblyai: return common + ["assemblyai", "streaming", "backend", "cloud"]
        }
    }
}

// MARK: - who hears you

private struct VoiceProviderCard: View {
    let choice: DictationEngineChoice
    @ObservedObject var settings: DictationSettings
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @Environment(\.settingsSearchQuery) private var searchQuery

    private var isSelected: Bool { settings.engine == choice }

    var body: some View {
        // Read so the badge redraws once a key or a sign-in lands in shell.json.
        let _ = shellSettingsRevision.revision
        let unavailableReason = DictationEngineResolver.unavailableReason(for: choice)
        VStack(alignment: .leading, spacing: 10) {
            Button(action: { settings.engine = choice }) {
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                        .font(.system(size: 14))
                        .foregroundStyle(isSelected ? Paper.accentFill : Paper.inkTertiary)
                        .padding(.top, 1)
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(spacing: 8) {
                            Text(settingsHighlighted(VoiceProviderCopy.title(for: choice), query: searchQuery))
                                .font(Paper.rowTitle).foregroundStyle(Paper.ink)
                            SettingsTag(text: VoiceProviderCopy.tag(for: choice), tint: choice == .offline ? Paper.success : Paper.inkSecondary)
                        }
                        Text(settingsHighlighted(VoiceProviderCopy.detail(for: choice), query: searchQuery))
                            .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 12)
                    if unavailableReason != nil {
                        Text(VoiceProviderCopy.unavailableBadge(for: choice))
                            .font(Paper.micro).foregroundStyle(isSelected ? Paper.danger : Paper.inkTertiary)
                            .help(unavailableReason ?? "")
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()
            .accessibilityLabel("\(VoiceProviderCopy.title(for: choice)), \(VoiceProviderCopy.tag(for: choice))")
            .accessibilityHint(unavailableReason ?? VoiceProviderCopy.detail(for: choice))
            .accessibilityAddTraits(isSelected ? .isSelected : [])

            if isSelected && choice == .sarvam {
                SarvamKeyEditor()
                    .padding(.leading, 26)
            } else if isSelected, let unavailableReason {
                Text(unavailableReason + (choice == .sarvam ? "" : " — sign in under account."))
                    .font(Paper.caption).foregroundStyle(Paper.danger)
                    .padding(.leading, 26)
            }
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical)
        .background(isSelected ? Paper.selection.opacity(0.35) : Color.clear)
    }
}

/// The sarvam key: field, test, save, and what happened. Kept in ~/.openclicky/shell.json.
private struct SarvamKeyEditor: View {
    @State private var sarvamKey = OpenClickyConfiguration.settings.sarvamKey ?? ""
    @State private var keyCheck: KeyCheck = .idle

    enum KeyCheck: Equatable {
        case idle, checking, works, saved, removed
        case failed(String)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                SecureField("api-subscription-key", text: $sarvamKey)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 300)
                    .onSubmit(saveKey)
                    .accessibilityLabel("sarvam key")
                Button("test") { testKey() }.buttonStyle(PaperPillButtonStyle()).disabled(keyCheck == .checking)
                Button("save") { saveKey() }.buttonStyle(PaperPillButtonStyle(prominent: true))
            }
            HStack(spacing: 4) {
                statusText
                Text("get one at").foregroundStyle(Paper.inkTertiary)
                Button("dashboard.sarvam.ai") { openExternalLink("https://dashboard.sarvam.ai") }
                    .buttonStyle(.plain).foregroundStyle(Paper.accentFill).pointerCursor()
                Text("· stored in ~/.openclicky/shell.json").foregroundStyle(Paper.inkTertiary)
            }
            .font(Paper.micro)
        }
    }

    @ViewBuilder
    private var statusText: some View {
        switch keyCheck {
        case .idle:
            if !(OpenClickyConfiguration.settings.sarvamKey ?? "").isEmpty { Text("key saved ·").foregroundStyle(Paper.inkSecondary) }
        case .checking: Text("checking… ·").foregroundStyle(Paper.inkSecondary)
        case .works: Text("✓ key works ·").foregroundStyle(Paper.success)
        case .saved: Text("✓ saved ·").foregroundStyle(Paper.success)
        case .removed: Text("key removed ·").foregroundStyle(Paper.inkSecondary)
        case .failed(let message): Text("\(message) ·").foregroundStyle(Paper.danger).lineLimit(2)
        }
    }

    private func saveKey() {
        let trimmed = sarvamKey.trimmingCharacters(in: .whitespacesAndNewlines)
        OpenClickyConfiguration.update { $0.sarvamKey = trimmed.isEmpty ? nil : trimmed }
        ShellSettingsRevision.shared.noteChanged()
        keyCheck = trimmed.isEmpty ? .removed : .saved
    }

    private func testKey() {
        let trimmed = sarvamKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { keyCheck = .failed("paste a key first"); return }
        keyCheck = .checking
        Task {
            do {
                _ = try await SarvamSpeechClient(key: trimmed).complete(system: "Reply with the single word: ok", user: "ping", maxTokens: 5)
                // A key that answers is worth keeping.
                saveKey()
                keyCheck = .works
            } catch {
                keyCheck = .failed(error.localizedDescription)
            }
        }
    }
}

// MARK: - microphone

private struct MicrophoneInputRow: View {
    @ObservedObject var settings: DictationSettings
    @State private var devices = MicrophoneDevices.available()

    var body: some View {
        SettingsRow(title: "input", detail: "follows the mac’s default unless you pick one. now: \(currentName).") {
            Picker("", selection: Binding(get: { settings.preferredMicrophoneUID ?? "" }, set: { settings.preferredMicrophoneUID = $0.isEmpty ? nil : $0 })) {
                Text("mac default (\(MicrophoneDevices.defaultInputName() ?? "none"))").tag("")
                ForEach(devices) { device in Text(device.name).tag(device.uid) }
            }
            .labelsHidden().frame(width: 260)
        }
        .onAppear { devices = MicrophoneDevices.available() }
    }

    private var currentName: String {
        if let uid = settings.preferredMicrophoneUID, let device = devices.first(where: { $0.uid == uid }) { return device.name }
        return MicrophoneDevices.defaultInputName() ?? "no microphone found"
    }
}

private struct MicrophoneTestRow: View {
    @ObservedObject var settings: DictationSettings
    @State private var isTesting = false
    @State private var testLevel: CGFloat = 0
    @State private var testEngine: AVAudioEngine?

    var body: some View {
        SettingsRow(title: "test your mic", detail: isTesting ? "speak — the bars should follow your voice." : "hear whether the chosen input picks you up.") {
            HStack(spacing: 12) {
                if isTesting { OrbLevelBars(level: testLevel, color: Paper.success).frame(width: 40, height: 16) }
                Button(isTesting ? "stop" : "speak to test") { isTesting ? stopTest() : startTest() }.buttonStyle(PaperPillButtonStyle())
            }
        }
        .onDisappear(perform: stopTest)
    }

    private func startTest() {
        let engine = AVAudioEngine()
        MicrophoneDevices.apply(preferredUID: settings.preferredMicrophoneUID, to: engine)
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0 else { return }
        let testLevelBinding = $testLevel
        input.installTap(onBus: 0, bufferSize: 1024, format: format, block: Self.makeLevelTap { level in
            DispatchQueue.main.async { testLevelBinding.wrappedValue = level }
        })
        engine.prepare()
        if (try? engine.start()) != nil {
            testEngine = engine
            isTesting = true
        }
    }

    /// The tap runs on the CoreAudio render thread, so it is built outside the view's main-actor
    /// isolation and touches nothing but the buffer and the callback it was given.
    nonisolated private static func makeLevelTap(onLevel: @escaping @Sendable (CGFloat) -> Void) -> AVAudioNodeTapBlock {
        return { buffer, _ in
            guard let channel = buffer.floatChannelData?[0] else { return }
            var sum: Float = 0
            for index in 0..<Int(buffer.frameLength) { sum += channel[index] * channel[index] }
            let rms = sqrt(sum / Float(max(1, buffer.frameLength)))
            onLevel(CGFloat(min(1, rms * 10)))
        }
    }

    private func stopTest() {
        testEngine?.inputNode.removeTap(onBus: 0)
        testEngine?.stop()
        testEngine = nil
        isTesting = false
    }
}

private struct MovedToStylesNote: View {
    @ObservedObject var windowModel: DictationWindowModel
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(spacing: 4) {
            Text(settingsHighlighted("looking for polish or language? they moved to", query: searchQuery))
            Button("styles") { windowModel.section = .styles }
                .buttonStyle(.plain).foregroundStyle(Paper.accentFill).pointerCursor()
                .accessibilityHint("opens the styles page")
            Text(settingsHighlighted(", next to the rules the model reads.", query: searchQuery))
        }
        .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
        .padding(.top, 8).padding(.leading, 2)
    }
}
