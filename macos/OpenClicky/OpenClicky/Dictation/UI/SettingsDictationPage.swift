//
//  SettingsDictationPage.swift
//  OpenClicky
//
//  Settings → Dictation: everything about typing with your voice, in the order someone sets it
//  up. The key you hold, who hears you, in which language, whether AI tidies the words, through
//  which microphone, when a quiet take ends, and the tones. Each engine says where the audio goes;
//  its key lives under Keys & Plans. Language and clean-up used to sit in Styles; they moved here
//  because "where do I change the language" is asked on this page.
//

import AVFoundation
import SwiftUI

extension SettingsCatalog {
    func dictationItems() -> [SettingsItem] {
        let region = settings.region
        let engineItems = DictationEngineChoice.allCases.map { choice in
            SettingsItem(
                id: "dictation.engine.\(choice.rawValue)",
                title: choice.settingsTitle, detail: choice.settingsDetail,
                keywords: choice.searchKeywords,
                // Sarvam hears Indian languages: offered in India, and wherever it is already chosen.
                isRelevant: { [settings] in choice != .sarvam || region.offersIndianOptions || settings.engine == .sarvam },
                view: AnyView(VoiceProviderOption(choice: choice, settings: settings, companionManager: companionManager)))
        }
        return dictationKeyItems() + engineItems + [
            SettingsItem(
                id: "dictation.language",
                title: "Language", detail: "Pins every engine to one language, or lets it detect.",
                keywords: ["language", "english", "hindi", "tamil", "spanish", "french", "german", "pin", "auto-detect", "detect"],
                view: AnyView(DictationLanguageRow(settings: settings))),
            SettingsItem(
                id: "dictation.script",
                title: "Script for Indian languages", detail: "Same words, written two ways.",
                keywords: ["script", "roman", "native", "devanagari", "hinglish", "transliteration"],
                // Only India, and only while the words could be in an Indian language.
                isRelevant: { [settings] in
                    settings.region.offersIndianOptions && (settings.language == .auto || settings.language.isIndian)
                },
                view: AnyView(DictationScriptRow(settings: settings))),
            SettingsItem(
                id: "dictation.cleanUp",
                title: "Clean up my words with AI", detail: "Adds punctuation and fixes numbers.",
                keywords: ["polish", "clean up", "cleanup", "clean-up", "ai", "punctuation", "grammar", "apple intelligence", "model", "offline takes"],
                view: AnyView(DictationCleanUpRow(settings: settings))),
            SettingsItem(
                id: "dictation.input",
                title: "Input", detail: "Automatic follows the Mac’s default, but skips the headphone jack when nothing there can hear. Clicky listens through it too.",
                keywords: ["microphone", "mic", "device", "input", "headset", "airpods"],
                view: AnyView(MicrophoneInputRow(settings: settings))),
            SettingsItem(
                id: "dictation.micTest",
                title: "Test your microphone", detail: "Hear whether the chosen input picks you up.",
                keywords: ["microphone", "mic", "level", "test"],
                view: AnyView(MicrophoneTestRow(settings: settings))),
            SettingsItem(
                id: "dictation.inactivityTimeout",
                title: "End a quiet take after", detail: "A tapped take finishes on its own after this much silence.",
                keywords: ["inactivity", "timeout", "silence", "minutes", "quiet"],
                view: AnyView(SettingsRow(title: "End a quiet take after", detail: "A tapped take finishes on its own after this much silence.") {
                    InactivityTimeoutPicker(settings: settings)
                })),
            SettingsItem(
                id: "dictation.pasteLast",
                title: "Paste last take", detail: "Put your last take at the cursor again.",
                keywords: ["paste", "again", "re-insert", "last"],
                view: AnyView(SettingsRow(title: "Paste last take", detail: "Put your last take at the cursor again.") {
                    Button("Paste Now") { companionManager.dictationTakeController.pasteLast() }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "dictation.sounds",
                title: "Sounds", detail: "Tones when a take starts, stops or finishes.",
                keywords: ["sound", "tone", "earcon", "audio", "beep", "mute", "quiet"],
                view: AnyView(SettingsRow(title: "Sounds", detail: "Tones when a take starts, stops or finishes.") {
                    SettingsToggle(settings: settings, keyPath: \.sounds)
                })),
            SettingsItem(
                id: "dictation.haptics",
                title: "Haptics", detail: "A trackpad tap on start, stop and result.",
                keywords: ["trackpad", "vibration", "tap", "haptic"],
                view: AnyView(SettingsRow(title: "Haptics", detail: "A trackpad tap on start, stop and result.") {
                    SettingsToggle(settings: settings, keyPath: \.haptics)
                })),
        ]
    }
}

// MARK: - who hears you
//
// Each engine's name, tag, detail, badge and search words are properties of
// `DictationEngineChoice` (DictationSettings.swift), so this page and the island read one copy.

/// One engine as a radio option: the button, its name and tag, what it does, and on the right
/// what it still needs. The chosen engine that can't run yet says why, with the way to fix it.
private struct VoiceProviderOption: View {
    let choice: DictationEngineChoice
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @Environment(\.settingsSearchQuery) private var searchQuery
    @ObservedObject private var parakeet = ParakeetDictation.shared

    private var isSelected: Bool { settings.engine == choice }

    var body: some View {
        // Read so the note redraws once a key or a sign-in lands in shell.json.
        let _ = shellSettingsRevision.revision
        let unavailableReason = DictationEngineResolver.unavailableReason(for: choice)
        VStack(alignment: .leading, spacing: 6) {
            Button(action: {
                settings.engine = choice
                if choice == .parakeet { ParakeetDictation.shared.download() }
            }) {
                HStack(alignment: .top, spacing: 10) {
                    SettingsRadioDot(isSelected: isSelected).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 8) {
                            Text(settingsHighlighted(choice.settingsTitle, query: searchQuery)).font(SettingsLook.rowTitle)
                            SettingsTag(text: choice.audioDestinationTag, isPositive: choice.isOnDevice)
                        }
                        Text(settingsHighlighted(choice.settingsDetail, query: searchQuery))
                            .font(SettingsLook.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 12)
                    if unavailableReason != nil {
                        Text(choice.unavailableBadge)
                            .font(SettingsLook.caption).foregroundStyle(isSelected ? SettingsLook.danger : .secondary)
                            .help(unavailableReason ?? "")
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(choice.settingsTitle), \(choice.audioDestinationTag)")
            .accessibilityHint(unavailableReason ?? choice.settingsDetail)
            .accessibilityAddTraits(isSelected ? .isSelected : [])

            if choice == .parakeet, isSelected || parakeet.state.isBusy || parakeet.state == .installed {
                ParakeetDownloadRow().padding(.leading, 24)
            } else if isSelected, let unavailableReason {
                HStack(spacing: 8) {
                    Text(unavailableReason).font(SettingsLook.caption).foregroundStyle(SettingsLook.danger)
                        .fixedSize(horizontal: false, vertical: true)
                    Button(choice == .sarvam ? "Add Key…" : "Sign In…") { companionManager.showSettings(.account) }
                        .controlSize(.small)
                }
                .padding(.leading, 24)
            }
        }
        .padding(.vertical, 2)
    }
}

/// Parakeet's model under its option: download (progress, cancel, retry), or remove it.
private struct ParakeetDownloadRow: View {
    @ObservedObject private var parakeet = ParakeetDictation.shared

    var body: some View {
        HStack(spacing: 8) {
            switch parakeet.state {
            case .notInstalled:
                caption("Not downloaded yet. Takes stay with Apple’s recogniser until it is.")
                Button("Download \(ParakeetDictation.downloadSizeText)") { parakeet.download() }.controlSize(.small)
            case .downloading(let fraction):
                caption("Downloading, \(Int((fraction * 100).rounded()))%. Takes stay with Apple’s recogniser until it’s ready.")
                ProgressView(value: fraction).frame(width: 100)
                Button("Cancel") { parakeet.cancelDownload() }.controlSize(.small)
            case .unpacking:
                caption("Unpacking. Almost there.")
                ProgressView().controlSize(.small)
            case .failed(let message):
                Text(message).font(SettingsLook.caption).foregroundStyle(SettingsLook.danger)
                Button("Try Again") { parakeet.download() }.controlSize(.small)
            case .installed:
                caption("Downloaded. It loads while you speak, so takes start straight away.")
                Button("Remove") { parakeet.remove() }.controlSize(.small)
            }
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(SettingsLook.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
}

/// A radio button drawn like the system's, in the window's accent.
struct SettingsRadioDot: View {
    let isSelected: Bool

    var body: some View {
        ZStack {
            Circle().fill(isSelected ? SettingsLook.accent : Color(nsColor: .controlColor))
            Circle().strokeBorder(isSelected ? Color.clear : Color(nsColor: .separatorColor), lineWidth: 0.5)
            if isSelected { Circle().fill(Color.white).frame(width: 6, height: 6) }
        }
        .frame(width: 14, height: 14)
        .accessibilityHidden(true)
    }
}

private struct InactivityTimeoutPicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.inactivityTimeoutMinutes) {
            Text("1 minute").tag(1); Text("3 minutes").tag(3); Text("5 minutes").tag(5); Text("10 minutes").tag(10); Text("Never").tag(0)
        }
        .labelsHidden().fixedSize()
        .pointerCursor()
    }
}

// MARK: - microphone

private struct MicrophoneInputRow: View {
    @ObservedObject var settings: DictationSettings
    @State private var devices = MicrophoneDevices.available()

    var body: some View {
        SettingsRow(title: "Input", detail: "Automatic follows the Mac’s default, skipping a headphone jack with no microphone. Now: \(currentName).") {
            Picker("", selection: Binding(get: { settings.preferredMicrophoneUID ?? "" }, set: { settings.preferredMicrophoneUID = $0.isEmpty ? nil : $0 })) {
                Text("Automatic (\(MicrophoneDevices.effectiveInputName(preferredUID: nil) ?? "none"))").tag("")
                ForEach(devices) { device in Text(device.name).tag(device.uid) }
            }
            .labelsHidden().frame(maxWidth: 260)
            .pointerCursor()
        }
        .onAppear { devices = MicrophoneDevices.available() }
    }

    private var currentName: String {
        if let uid = settings.preferredMicrophoneUID, let device = devices.first(where: { $0.uid == uid }) { return device.name }
        return MicrophoneDevices.effectiveInputName(preferredUID: nil) ?? "no microphone found"
    }
}

private struct MicrophoneTestRow: View {
    @ObservedObject var settings: DictationSettings
    @State private var isTesting = false
    @State private var testLevel: CGFloat = 0
    @State private var testEngine: AVAudioEngine?
    /// Why the last test could not start. The button used to do nothing at all, which looked
    /// exactly like a microphone that hears nothing (a tester, 2026-10-27).
    @State private var testStartProblem: String?

    var body: some View {
        SettingsRow(title: "Test your microphone", detail: testRowDetail) {
            HStack(spacing: 12) {
                if isTesting { OrbLevelBars(level: testLevel, color: SettingsLook.success).frame(width: 40, height: 16) }
                Button(isTesting ? "Stop" : "Speak to Test") { isTesting ? stopTest() : startTest() }
                    .pointerCursor()
            }
        }
        .onDisappear(perform: stopTest)
    }

    private var testRowDetail: String {
        if isTesting { return "Speak, and the bars should follow your voice." }
        if let testStartProblem { return testStartProblem }
        return "Hear whether the chosen input picks you up."
    }

    private func startTest() {
        testStartProblem = nil
        let engine = AVAudioEngine()
        MicrophoneDevices.apply(preferredUID: settings.preferredMicrophoneUID, to: engine)
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        let microphoneName = MicrophoneDevices.effectiveInputName(preferredUID: settings.preferredMicrophoneUID) ?? "no microphone"
        AppLog.append("microphone test: \(microphoneName), \(Int(format.sampleRate)) Hz, \(format.channelCount) ch, permission \(AVCaptureDevice.authorizationStatus(for: .audio).rawValue)")
        guard format.sampleRate > 0 else {
            testStartProblem = "\(microphoneName) gave no audio. Check Privacy & Security › Microphone, or pick another input."
            AppLog.append("microphone test: no input format")
            return
        }
        let testLevelBinding = $testLevel
        input.installTap(onBus: 0, bufferSize: 1024, format: format, block: Self.makeLevelTap { level in
            DispatchQueue.main.async { testLevelBinding.wrappedValue = level }
        })
        engine.prepare()
        do {
            try engine.start()
            testEngine = engine
            isTesting = true
        } catch {
            input.removeTap(onBus: 0)
            testStartProblem = "\(microphoneName) wouldn't start (\((error as NSError).code)). Pick another input, or reconnect it."
            AppLog.append("microphone test: start failed: \(error)")
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

// MARK: - language and clean-up

/// Auto-detect or one pinned language, from the languages the region offers. Pinning again comes
/// back to the language last pinned.
private struct DictationLanguageRow: View {
    @ObservedObject var settings: DictationSettings
    /// The language last pinned, so "Pin one" comes back to it after a turn on auto-detect. Same
    /// key the Styles page used, so the remembered language survives the move.
    @AppStorage("dictation.lastPinnedLanguage") private var lastPinnedLanguageCode = ""

    var body: some View {
        SettingsRow(title: "Language", detail: "Pins every engine to one language, or lets it detect.") {
            HStack(spacing: 8) {
                Picker("", selection: Binding(
                    get: { settings.language == .auto },
                    set: { isAuto in
                        if isAuto {
                            settings.languageCode = DictationLanguage.auto.code
                        } else if settings.language == .auto {
                            settings.languageCode = languageToPin.code
                        }
                    })) {
                    Text("Auto-detect").tag(true)
                    Text("Pin one").tag(false)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize().tint(SettingsLook.accent)
                .pointerCursor()
                .accessibilityLabel("Language")

                if settings.language != .auto {
                    Picker("", selection: Binding(
                        get: { settings.languageCode },
                        set: { code in settings.languageCode = code; lastPinnedLanguageCode = code })) {
                        ForEach(DictationLanguage.pinnableChoices(region: settings.region, current: settings.language)) { language in
                            Text(language.name == language.nativeName ? language.name : "\(language.name) · \(language.nativeName)").tag(language.code)
                        }
                    }
                    .labelsHidden().fixedSize()
                    .pointerCursor()
                    .accessibilityLabel("Pinned language")
                }
            }
        }
    }

    /// The last pinned language if the region still offers it, else the region's English.
    private var languageToPin: DictationLanguage {
        let lastPinned = DictationLanguage.named(lastPinnedLanguageCode)
        let offered = DictationLanguage.pinnableChoices(region: settings.region, current: .auto)
        return offered.contains(lastPinned) ? lastPinned : DictationLanguage.defaultPinned(region: settings.region)
    }
}

private struct DictationScriptRow: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        SettingsRow(title: "Script for Indian languages", detail: "Same words, written two ways. The AI clean-up writes it.") {
            Picker("", selection: $settings.script) {
                Text("Native · नमस्ते, आप कैसे हैं?").tag(DictationScript.native)
                Text("Roman · namaste, aap kaise hain?").tag(DictationScript.roman)
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
            .accessibilityLabel("Script for Indian languages")
        }
    }
}

/// One switch for the model clean-up, in plain words. Underneath there are two settings (takes
/// heard by an online engine, and takes heard on this Mac); the switch sets both, and shows what
/// the takes will actually do with the engine in use.
struct DictationCleanUpRow: View {
    @ObservedObject var settings: DictationSettings
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared

    /// The switch is on exactly when takes are polished: an upgrader whose two old settings
    /// disagree sees what the takes do, not one half of it.
    static func cleanUpSwitchShowsOn(settings: DictationSettings) -> Bool {
        DictationEngineResolver.wantsModelPolish(settings: settings)
    }

    var body: some View {
        let _ = shellSettingsRevision.revision
        let hasModel = DictationEngineResolver.makePolisher() != nil
        let needsModelNote = hasModel ? "" : " Needs an AI key (Settings → Keys & Account) or a free OpenClicky account."
        let tidiedOnThisMac = AppleModelTakePolisherAvailability.isAvailable(languageCode: settings.languageCode)
        let whereWordsAreTidied = tidiedOnThisMac ? "Apple Intelligence tidies your words on this Mac" : "Your words are sent online to be tidied"
        let cleanUpIsOn = Binding<Bool>(
            get: { Self.cleanUpSwitchShowsOn(settings: settings) },
            set: { isOn in
                settings.polishWithModel = isOn
                settings.choosePolishOfflineTakes(isOn)
            })
        SettingsRow(
            title: "Clean up my words with AI",
            detail: "Adds punctuation and fixes numbers. " + whereWordsAreTidied + ", so they appear when you let go. Off: words type straight in as you speak." + needsModelNote) {
            SettingsSwitch(isOn: cleanUpIsOn)
        }
    }
}
