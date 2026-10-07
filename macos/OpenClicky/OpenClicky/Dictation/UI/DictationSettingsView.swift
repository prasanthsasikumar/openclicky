//
//  DictationSettingsView.swift
//  OpenClicky
//
//  Settings: a second rail (openclicky: general, shortcuts, the orb, microphone, permissions,
//  engine; you: privacy & data, plan & usage, account; about) and one page at a time.
//

import AppKit
import Combine
import AVFoundation
import Speech
import SwiftUI

struct DictationSettingsView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var search = ""

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
    }

    var body: some View {
        HStack(spacing: 0) {
            settingsRail.frame(width: 230)
            Rectangle().fill(Paper.hairline).frame(width: 1)
            Group {
                switch model.settingsPage {
                case .general: GeneralSettingsPage(settings: settings, companionManager: companionManager)
                case .shortcuts: ShortcutsSettingsPage(settings: settings, companionManager: companionManager)
                case .orb: OrbSettingsPage(settings: settings)
                case .microphone: MicrophoneSettingsPage(settings: settings)
                case .permissions: PermissionsSettingsPage(settings: settings, companionManager: companionManager)
                case .engine: EngineSettingsPage(settings: settings, companionManager: companionManager)
                case .privacy: PrivacySettingsPage(settings: settings, companionManager: companionManager)
                case .plan: PlanSettingsPage()
                case .account: AccountSettingsPage(model: model)
                case .about: AboutSettingsPage()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var settingsRail: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary)
                TextField("search settings", text: $search).textFieldStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.ink)
            }
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 8).fill(Paper.cardRaised))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Paper.hairline))
            .padding(.horizontal, 12).padding(.top, 44)

            HStack(spacing: 10) {
                ZStack {
                    Circle().fill(Paper.accentSoft)
                    Text(String((authSession.accountEmail ?? NSFullUserName()).prefix(1)).lowercased()).font(Paper.body(13, weight: .semibold)).foregroundStyle(Paper.ink)
                }
                .frame(width: 34, height: 34)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(authSession.accountEmail.map { String($0.split(separator: "@").first ?? "") } ?? NSFullUserName()).font(Paper.body(12, weight: .medium)).foregroundStyle(Paper.ink).lineLimit(1)
                        Text(settings.engine == .offline ? "offline" : settings.engine.rawValue).font(Paper.mono(9)).foregroundStyle(Paper.ink)
                            .padding(.horizontal, 5).padding(.vertical, 1).background(Capsule().fill(Paper.highlighter))
                    }
                    Text("personal").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                }
            }
            .padding(.horizontal, 14).padding(.top, 14)

            Text("openclicky").font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary).padding(.leading, 14).padding(.top, 18).padding(.bottom, 4)
            ForEach(pages(DictationSettingsPage.appPages)) { page in railButton(page) }
            Text("you").font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary).padding(.leading, 14).padding(.top, 14).padding(.bottom, 4)
            ForEach(pages(DictationSettingsPage.youPages)) { page in railButton(page) }
            if pages([.about]).isEmpty == false { railButton(.about).padding(.top, 10) }
            Spacer()
            Rectangle().fill(Paper.hairline).frame(height: 1).padding(.horizontal, 12)
            Text("openclicky \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "") (\(Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? ""))")
                .font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary).padding(14)
        }
        .background(Paper.rail.opacity(0.6))
    }

    private func pages(_ pages: [DictationSettingsPage]) -> [DictationSettingsPage] {
        guard !search.trimmingCharacters(in: .whitespaces).isEmpty else { return pages }
        return pages.filter { $0.title.localizedCaseInsensitiveContains(search) }
    }

    private func railButton(_ page: DictationSettingsPage) -> some View {
        let selected = model.settingsPage == page
        return Button(action: { model.settingsPage = page }) {
            HStack(spacing: 9) {
                Image(systemName: page.symbol).font(.system(size: 11)).frame(width: 14)
                Text(page.title).font(Paper.body(13, weight: selected ? .medium : .regular))
                Spacer()
            }
            .foregroundStyle(selected ? Paper.ink : Paper.inkSecondary)
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(selected ? Paper.selection : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain).pointerCursor()
        .padding(.horizontal, 10)
    }
}

/// A settings page's scaffold: the title, an optional reset, and the sections.
private struct SettingsPageScaffold<Content: View>: View {
    let title: String
    var onReset: (() -> Void)?
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(title).font(Paper.title(22)).foregroundStyle(Paper.ink)
                    Spacer()
                    if let onReset {
                        Button(action: onReset) { Label("reset", systemImage: "arrow.counterclockwise") }.buttonStyle(PaperPillButtonStyle())
                    }
                }
                .padding(.top, 44)
                content
            }
            .padding(.horizontal, 34).padding(.bottom, 40)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

// MARK: - general

private struct GeneralSettingsPage: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @State private var launchesAtLogin = LoginItem.isEnabled

    var body: some View {
        SettingsPageScaffold(title: "general", onReset: { settings.resetGeneralToDefaults() }) {
            PaperSectionLabel(text: "appearance")
            PaperCard {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 14) {
                        ForEach(DictationAppearance.allCases) { appearance in
                            AppearanceSwatch(appearance: appearance, selected: settings.appearance == appearance) { settings.appearance = appearance }
                        }
                    }
                    Text("the orb and its transcript box follow this too.").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                }
                .padding(18)
            }
            PaperSectionLabel(text: "behavior")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "open at restart", detail: "open openclicky automatically when you log in to your mac.") {
                        Toggle("", isOn: Binding(get: { launchesAtLogin }, set: { on in launchesAtLogin = LoginItem.set(enabled: on) })).toggleStyle(.checkbox).labelsHidden()
                    }
                    PaperDivider()
                    PaperRow(title: "inactivity timeout", detail: "end a take after this long with no speech.") {
                        Picker("", selection: $settings.inactivityTimeoutMinutes) {
                            Text("1 min").tag(1); Text("3 min").tag(3); Text("5 min").tag(5); Text("10 min").tag(10); Text("never").tag(0)
                        }
                        .labelsHidden().frame(width: 90)
                    }
                    PaperDivider()
                    PaperRow(title: "reduce animation", detail: "calms the orb, box, and pill motion for a snappier feel.") { PaperToggle(isOn: $settings.reduceAnimation) }
                }
            }
            PaperSectionLabel(text: "getting started")
            PaperCard {
                PaperRow(title: "welcome walkthrough", detail: "replay the first-run setup: permissions, your key, your engine, a first take.") {
                    Button("replay") { companionManager.showDictationOnboarding(replay: true) }.buttonStyle(PaperPillButtonStyle())
                }
            }
        }
    }
}

private struct AppearanceSwatch: View {
    let appearance: DictationAppearance
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 8).fill(appearance == .dark ? Color(white: 0.12) : Color(white: 0.96))
                    if appearance == .system {
                        Path { path in path.move(to: CGPoint(x: 0, y: 70)); path.addLine(to: CGPoint(x: 160, y: 0)); path.addLine(to: CGPoint(x: 160, y: 70)); path.closeSubpath() }
                            .fill(Color(white: 0.12)).clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                    HStack(spacing: 6) {
                        RoundedRectangle(cornerRadius: 3).fill(appearance == .dark ? Color(white: 0.2) : Color(white: 0.88)).frame(width: 36)
                        VStack(alignment: .leading, spacing: 4) {
                            RoundedRectangle(cornerRadius: 2).fill(Paper.success.opacity(0.6)).frame(width: 40, height: 4)
                            RoundedRectangle(cornerRadius: 2).fill(Color.gray.opacity(0.4)).frame(width: 70, height: 4)
                            RoundedRectangle(cornerRadius: 2).fill(Color.gray.opacity(0.3)).frame(width: 55, height: 4)
                        }
                        Spacer()
                    }
                    .padding(10)
                }
                .frame(width: 160, height: 70)
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(selected ? Paper.success : Paper.hairline, lineWidth: selected ? 2 : 1))
                HStack(spacing: 4) {
                    if selected { Circle().fill(Paper.success).frame(width: 5, height: 5) }
                    Text(appearance.rawValue).font(Paper.body(12)).foregroundStyle(Paper.ink)
                }
            }
        }
        .buttonStyle(.plain).pointerCursor()
    }
}

/// The login item, through SMAppService.
enum LoginItem {
    static var isEnabled: Bool { SMAppServiceBridge.isEnabled }
    @discardableResult
    static func set(enabled: Bool) -> Bool { SMAppServiceBridge.set(enabled: enabled) }
}

// MARK: - shortcuts

private struct ShortcutsSettingsPage: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @State private var fnUsage = FnKeyGuard.currentUsage()

    var body: some View {
        SettingsPageScaffold(title: "shortcuts", onReset: { settings.resetShortcutsToDefaults() }) {
            PaperSectionLabel(text: "the dictation key")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "dictation key", detail: "hold to dictate anywhere. tap to start a take, tap again to finish.") {
                        HStack(spacing: 8) {
                            Picker("", selection: $settings.dictationKey) {
                                ForEach(DictationHotkey.allCases) { key in Text(key.displayName).tag(key) }
                            }
                            .labelsHidden().frame(width: 150)
                        }
                    }
                    PaperDivider()
                    PaperRow(title: "“hey clicky” mode", detail: "dictation key + control, held together: say an edit for the selected text.") {
                        HStack(spacing: 4) { Keycap(text: settings.dictationKey.keycapLabel); Text("+").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary); Keycap(text: "left ⌃") }
                    }
                    PaperDivider()
                    PaperRow(title: "cancel take", detail: "discard an in-progress take.") {
                        HStack(spacing: 6) { Keycap(text: "esc"); Text("or").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary); Keycap(text: "double-tap \(settings.dictationKey.keycapLabel)") }
                    }
                    if settings.dictationKey == .fn {
                        PaperDivider()
                        PaperRow(title: fnUsage == .doNothing ? "fn is free for dictation" : "macOS also uses fn to \(fnUsage?.description ?? "do something")",
                                 detail: fnUsage == .doNothing ? "macOS's own fn action is off (keyboard settings → “press 🌐 key to: do nothing”)." : "every press would also open that. let openclicky set it to do nothing; you can give it back any time.") {
                            if fnUsage == .doNothing {
                                Button("give fn back to macOS") { FnKeyGuard.restoreFn(); fnUsage = FnKeyGuard.currentUsage() }.buttonStyle(PaperPillButtonStyle())
                            } else {
                                Button("free the fn key") { FnKeyGuard.freeFn(); fnUsage = FnKeyGuard.currentUsage() }.buttonStyle(PaperPillButtonStyle(prominent: true))
                            }
                        }
                    }
                }
            }
            PaperSectionLabel(text: "text")
            PaperCard {
                PaperRow(title: "paste last take", detail: "re-insert your last take where the cursor is.") {
                    Button("paste now") { companionManager.dictationTakeController.pasteLast() }.buttonStyle(PaperPillButtonStyle())
                }
            }
            PaperSectionLabel(text: "the companion")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "talk to openclicky", detail: "ask about what is on screen; it answers out loud and points.") { HStack(spacing: 4) { Keycap(text: "⌃"); Text("+").font(Paper.body(11)); Keycap(text: "⌥") } }
                    PaperDivider()
                    PaperRow(title: "type to openclicky", detail: "a one-line composer in the notch.") { Keycap(text: "tap ⌃ twice") }
                    PaperDivider()
                    PaperRow(title: "hands-free", detail: "always-on listening with realtime voice.") { Keycap(text: "tap \(settings.dictationKey.keycapLabel) + ⌃ twice") }
                    PaperDivider()
                    PaperRow(title: "the pointer shows", detail: "while the mouse moves: it slips away a few seconds after the mouse rests, like the cursor over a video. after a shake: it stays out of sight until you shake the mouse.") {
                        Picker("", selection: Binding(get: { companionManager.pointerPresence }, set: { companionManager.setPointerPresence($0) })) {
                            ForEach(PointerPresence.allCases) { presence in Text(presence.label).tag(presence) }
                        }
                        .labelsHidden().frame(width: 170)
                    }
                }
            }
        }
        .onAppear { fnUsage = FnKeyGuard.currentUsage() }
    }
}

// MARK: - the orb

private struct OrbSettingsPage: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        SettingsPageScaffold(title: "the orb", onReset: { settings.resetOrbToDefaults() }) {
            PaperCard {
                ZStack(alignment: .bottom) {
                    RoundedRectangle(cornerRadius: 10).fill(LinearGradient(colors: [Color(red: 0.85, green: 0.9, blue: 0.95), Color(red: 0.95, green: 0.88, blue: 0.85)], startPoint: .top, endPoint: .bottom))
                        .frame(height: 220)
                        .overlay(alignment: .top) {
                            HStack(spacing: 10) {
                                Image(systemName: "apple.logo").font(.system(size: 9))
                                Text("Messages  File  Edit  View  Window  Help").font(.system(size: 8))
                                Spacer()
                                Text("Fri 9:41").font(.system(size: 8))
                            }
                            .padding(.horizontal, 10).frame(height: 16).background(Color.white.opacity(0.5)).clipShape(RoundedRectangle(cornerRadius: 10))
                        }
                    VStack(spacing: 6) {
                        OrbPreview(look: settings.orbLook, theme: settings.orbTheme, size: settings.orbSize)
                        Text(settings.orbPosition == nil ? "rests at the bottom of your screen" : "resting where you dragged it").font(Paper.body(10)).foregroundStyle(Paper.inkSecondary)
                        if settings.orbPosition != nil { Button("back to the bottom") { settings.orbPosition = nil }.buttonStyle(PaperPillButtonStyle()) }
                    }
                    .padding(.bottom, 14)
                }
                .padding(18)
            }
            PaperSectionLabel(text: "look")
            PaperCard {
                HStack(spacing: 12) {
                    ForEach(OrbLook.allCases) { look in
                        Button(action: { settings.orbLook = look }) {
                            VStack(spacing: 8) {
                                OrbPreview(look: look, theme: settings.orbTheme, size: .full)
                                Text(look.rawValue).font(Paper.body(12)).foregroundStyle(Paper.ink)
                            }
                            .frame(maxWidth: .infinity).padding(.vertical, 16)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Paper.cardRaised))
                            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(settings.orbLook == look ? Paper.success : Paper.hairline, lineWidth: settings.orbLook == look ? 2 : 1))
                        }
                        .buttonStyle(.plain).pointerCursor()
                    }
                }
                .padding(14)
            }
            PaperSectionLabel(text: "theme")
            PaperCard {
                HStack(spacing: 12) {
                    ForEach(OrbTheme.allCases) { theme in
                        Button(action: { settings.orbTheme = theme }) {
                            VStack(spacing: 8) {
                                OrbPreview(look: .pill, theme: theme, size: .mini)
                                Text(theme.rawValue).font(Paper.body(12)).foregroundStyle(Paper.ink)
                            }
                            .frame(width: 72).padding(.vertical, 14)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Paper.cardRaised))
                            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(settings.orbTheme == theme ? Paper.success : Paper.hairline, lineWidth: settings.orbTheme == theme ? 2 : 1))
                        }
                        .buttonStyle(.plain).pointerCursor()
                    }
                    Spacer()
                }
                .padding(14)
            }
            PaperSectionLabel(text: "shape & place")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "size", detail: nil) { PaperSegments(options: [(OrbSize.full, "full"), (OrbSize.mini, "mini")], selection: $settings.orbSize) }
                    PaperDivider()
                    PaperRow(title: "show the orb", detail: "hide it and the dictation key still works everywhere.") { PaperToggle(isOn: $settings.orbVisible) }
                    PaperDivider()
                    PaperRow(title: "hide when not in use", detail: "appears when you start a take and hides when you're done.") { PaperToggle(isOn: $settings.orbHidesWhenIdle) }
                    PaperDivider()
                    PaperRow(title: "rest with the box open", detail: "otherwise it rests small.") { PaperToggle(isOn: $settings.orbRestsExpanded) }
                    PaperDivider()
                    PaperRow(title: "paste visibility · orb box", detail: "open the box when openclicky cannot confirm a paste landed.") { PaperToggle(isOn: $settings.orbOpensBoxWhenPasteUnverified) }
                    PaperDivider()
                    PaperRow(title: "free to drag", detail: "turn off to pin the orb in place.") { PaperToggle(isOn: $settings.orbIsDraggable) }
                }
            }
            PaperSectionLabel(text: "feedback")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "tooltips", detail: "the hint under the orb.") { PaperToggle(isOn: $settings.tooltips) }
                    PaperDivider()
                    PaperRow(title: "sounds", detail: "tones when recording starts, stops, or finishes.") { PaperToggle(isOn: $settings.sounds) }
                    PaperDivider()
                    PaperRow(title: "haptics", detail: "a trackpad tap on start, stop, and results.") { PaperToggle(isOn: $settings.haptics) }
                }
            }
        }
    }
}

struct OrbPreview: View {
    let look: OrbLook
    let theme: OrbTheme
    let size: OrbSize

    private var fill: Color {
        switch theme {
        case .black: return Color(red: 0.16, green: 0.16, blue: 0.16)
        case .coral: return Color(red: 0.89, green: 0.33, blue: 0.21)
        case .mist: return Color(white: 0.93)
        }
    }
    private var ink: Color { theme == .mist ? Color(white: 0.2) : Color.white.opacity(0.92) }

    var body: some View {
        let pill = OrbMetrics.pillSize(size)
        Group {
            if look == .classic {
                ZStack {
                    Circle().fill(fill)
                    OrbMarkShape().fill(ink).frame(width: pill.height * 0.6, height: pill.height * 0.6)
                }
                .frame(width: pill.height * 1.4, height: pill.height * 1.4)
            } else if look == .pixel {
                ZStack {
                    Circle().fill(fill)
                    OrbMarkShape().fill(ink).frame(width: pill.height * 0.6, height: pill.height * 0.6)
                        .mask(PixelGrid().fill(.black))
                }
                .frame(width: pill.height * 1.4, height: pill.height * 1.4)
            } else {
                ZStack {
                    Capsule().fill(fill)
                    HStack(spacing: 6) {
                        RoundedRectangle(cornerRadius: 2).fill(ink).frame(width: 9, height: 2.5)
                        RoundedRectangle(cornerRadius: 2).fill(ink).frame(width: 9, height: 2.5)
                    }
                }
                .frame(width: pill.width, height: pill.height)
            }
        }
        .shadow(color: .black.opacity(0.2), radius: 6, y: 2)
    }
}

private struct PixelGrid: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let cell: CGFloat = 3
        var y = rect.minY
        while y < rect.maxY {
            var x = rect.minX
            while x < rect.maxX {
                path.addRect(CGRect(x: x, y: y, width: cell - 0.8, height: cell - 0.8))
                x += cell
            }
            y += cell
        }
        return path
    }
}

// MARK: - microphone

private struct MicrophoneSettingsPage: View {
    @ObservedObject var settings: DictationSettings
    @State private var devices = MicrophoneDevices.available()
    @State private var isTesting = false
    @State private var testLevel: CGFloat = 0
    @State private var testEngine: AVAudioEngine?

    var body: some View {
        SettingsPageScaffold(title: "microphone", onReset: { settings.preferredMicrophoneUID = nil }) {
            PaperSectionLabel(text: "input")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "input device", detail: "now: \(currentName).") {
                        Picker("", selection: Binding(get: { settings.preferredMicrophoneUID ?? "" }, set: { settings.preferredMicrophoneUID = $0.isEmpty ? nil : $0 })) {
                            Text("system default (\(MicrophoneDevices.defaultInputName() ?? "none"))").tag("")
                            ForEach(devices) { device in Text(device.name).tag(device.uid) }
                        }
                        .labelsHidden().frame(width: 280)
                    }
                    PaperDivider()
                    PaperRow(title: "test your mic", detail: isTesting ? "say something — the bars follow your voice." : nil) {
                        HStack(spacing: 12) {
                            if isTesting { OrbLevelBars(level: testLevel, color: Paper.success).frame(width: 40, height: 16) }
                            Button(isTesting ? "stop" : "speak to test") { isTesting ? stopTest() : startTest() }.buttonStyle(PaperPillButtonStyle())
                        }
                    }
                }
            }
        }
        .onAppear { devices = MicrophoneDevices.available() }
        .onDisappear(perform: stopTest)
    }

    private var currentName: String {
        if let uid = settings.preferredMicrophoneUID, let device = devices.first(where: { $0.uid == uid }) { return device.name }
        return MicrophoneDevices.defaultInputName() ?? "no microphone found"
    }

    private func startTest() {
        let engine = AVAudioEngine()
        MicrophoneDevices.apply(preferredUID: settings.preferredMicrophoneUID, to: engine)
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0 else { return }
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in
            guard let channel = buffer.floatChannelData?[0] else { return }
            var sum: Float = 0
            for index in 0..<Int(buffer.frameLength) { sum += channel[index] * channel[index] }
            let rms = sqrt(sum / Float(max(1, buffer.frameLength)))
            DispatchQueue.main.async { testLevel = CGFloat(min(1, rms * 10)) }
        }
        engine.prepare()
        if (try? engine.start()) != nil {
            testEngine = engine
            isTesting = true
        }
    }

    private func stopTest() {
        testEngine?.inputNode.removeTap(onBus: 0)
        testEngine?.stop()
        testEngine = nil
        isTesting = false
    }
}

// MARK: - permissions

private struct PermissionsSettingsPage: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @State private var microphoneGranted = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    @State private var accessibilityGranted = AXIsProcessTrusted()
    @State private var speechStatus = SFSpeechRecognizer.authorizationStatus()
    private let timer = Timer.publish(every: 1.5, on: .main, in: .common).autoconnect()

    var body: some View {
        SettingsPageScaffold(title: "permissions") {
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "microphone", detail: "openclicky listens only when you activate it. with the offline engine, audio never leaves this mac.") {
                        statusOrButton(granted: microphoneGranted) {
                            AVCaptureDevice.requestAccess(for: .audio) { _ in }
                            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") { NSWorkspace.shared.open(url) }
                        }
                    }
                    PaperDivider()
                    PaperRow(title: "accessibility", detail: "lets openclicky paste takes into other apps and read nearby text.") {
                        statusOrButton(granted: accessibilityGranted, grantedText: "active this session") { WindowPositionManager.requestAccessibilityPermission() }
                    }
                    PaperDivider()
                    PaperRow(title: "speech recognition", detail: "apple's on-device recogniser, for the offline engine.") {
                        statusOrButton(granted: speechStatus == .authorized) {
                            SFSpeechRecognizer.requestAuthorization { status in DispatchQueue.main.async { speechStatus = status } }
                            if speechStatus == .denied, let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_SpeechRecognition") { NSWorkspace.shared.open(url) }
                        }
                    }
                    PaperDivider()
                    PaperRow(title: "read nearby text", detail: "helps spell names and terms on screen right. turning this off stops nearby-text capture.") { PaperToggle(isOn: $settings.readNearbyText) }
                }
            }
            if !accessibilityGranted {
                Text("can't see openclicky in the accessibility list? the island's permission card can drag it in for you, and reset a stale row.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
            }
        }
        .onReceive(timer) { _ in
            microphoneGranted = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
            accessibilityGranted = AXIsProcessTrusted()
            speechStatus = SFSpeechRecognizer.authorizationStatus()
        }
    }

    @ViewBuilder
    private func statusOrButton(granted: Bool, grantedText: String = "granted", action: @escaping () -> Void) -> some View {
        if granted {
            HStack(spacing: 4) { Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)); Text(grantedText).font(Paper.body(12)) }.foregroundStyle(Paper.success)
        } else {
            Button("grant", action: action).buttonStyle(PaperPillButtonStyle(prominent: true))
        }
    }
}

// MARK: - engine

private struct EngineSettingsPage: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @State private var sarvamKey = OpenClickyConfiguration.settings.sarvamKey ?? ""
    @State private var keyCheck: String?

    var body: some View {
        SettingsPageScaffold(title: "engine") {
            PaperSectionLabel(text: "who hears you")
            PaperCard {
                VStack(spacing: 0) {
                    ForEach(DictationEngineChoice.allCases) { choice in
                        let reason = DictationEngineResolver.unavailableReason(for: choice)
                        Button(action: { settings.engine = choice }) {
                            HStack(alignment: .top, spacing: 14) {
                                Image(systemName: settings.engine == choice ? "largecircle.fill.circle" : "circle").font(.system(size: 14)).foregroundStyle(settings.engine == choice ? Paper.success : Paper.inkTertiary).padding(.top, 1)
                                VStack(alignment: .leading, spacing: 3) {
                                    HStack(spacing: 8) {
                                        Text(choice.displayName).font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                                        if choice == .offline { Text("private").font(Paper.mono(9)).padding(.horizontal, 5).padding(.vertical, 1).background(Capsule().fill(Paper.highlighter)).foregroundStyle(Paper.ink) }
                                        if let reason { Text(reason).font(Paper.body(11)).foregroundStyle(Paper.danger) }
                                    }
                                    Text(choice.detail).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                                }
                                Spacer()
                            }
                            .padding(.horizontal, 18).padding(.vertical, 12).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain).pointerCursor()
                        if choice != DictationEngineChoice.allCases.last { PaperDivider() }
                    }
                }
            }
            PaperSectionLabel(text: "sarvam")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "your sarvam key", detail: "from dashboard.sarvam.ai. kept in ~/.openclicky/shell.json, sent only to sarvam.") {
                        SecureField("api-subscription-key", text: $sarvamKey)
                            .textFieldStyle(.roundedBorder).frame(width: 260)
                            .onSubmit(saveKey)
                    }
                    PaperDivider()
                    HStack {
                        if let keyCheck { Text(keyCheck).font(Paper.body(11)).foregroundStyle(keyCheck.hasPrefix("ok") ? Paper.success : Paper.danger) }
                        Spacer()
                        Button("test the key") { testKey() }.buttonStyle(PaperPillButtonStyle())
                        Button("save") { saveKey() }.buttonStyle(PaperPillButtonStyle(prominent: true))
                    }
                    .padding(.horizontal, 18).padding(.vertical, 12)
                }
            }
            PaperSectionLabel(text: "how your words are cleaned up")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "polish with a model", detail: "punctuation, numbers, your style's rules — through sarvam with your key, or your openclicky account. applies to the network engines; off: local rules only.") { PaperToggle(isOn: $settings.polishWithModel) }
                    PaperDivider()
                    PaperRow(title: "also polish offline takes", detail: "the offline engine keeps your voice on this mac; with this on, its words are sent to the model above for cleanup. off by default.") { PaperToggle(isOn: $settings.polishOfflineTakes) }
                    PaperDivider()
                    PaperRow(title: "language", detail: "pins every engine to one language; auto lets them detect.") {
                        Picker("", selection: $settings.languageCode) {
                            ForEach(DictationLanguage.choices) { language in Text(language.code == "auto" ? "auto-detect" : "\(language.name) · \(language.nativeName)").tag(language.code) }
                        }
                        .labelsHidden().frame(width: 200)
                    }
                }
            }
        }
    }

    private func saveKey() {
        let trimmed = sarvamKey.trimmingCharacters(in: .whitespacesAndNewlines)
        OpenClickyConfiguration.update { $0.sarvamKey = trimmed.isEmpty ? nil : trimmed }
        keyCheck = trimmed.isEmpty ? "key removed" : "saved"
    }

    private func testKey() {
        let trimmed = sarvamKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { keyCheck = "paste a key first"; return }
        keyCheck = "checking…"
        Task {
            do {
                let answer = try await SarvamSpeechClient(key: trimmed).complete(system: "Reply with the single word: ok", user: "ping", maxTokens: 5)
                keyCheck = "ok — sarvam answered “\(answer.prefix(20))”"
                saveKey()
                keyCheck = "ok — key accepted and saved"
            } catch {
                keyCheck = error.localizedDescription
            }
        }
    }
}

// MARK: - privacy & data

private struct PrivacySettingsPage: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @State private var confirmDelete = false

    var body: some View {
        SettingsPageScaffold(title: "privacy & data") {
            PaperSectionLabel(text: "your data")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "keep my memory on this mac only", detail: "history, dictionary and styles stay here. with the offline engine and \"also polish offline takes\" off (the default), nothing leaves this mac at all.") { PaperToggle(isOn: $settings.keepMemoryOnThisMac) }
                    PaperDivider()
                    PaperRow(title: "incognito", detail: "takes still paste, but nothing is saved to history.") { PaperToggle(isOn: $settings.incognito) }
                    PaperDivider()
                    PaperRow(title: "clipboard history", detail: "capture what you copy, so it shows in history too.") { PaperToggle(isOn: $settings.clipboardHistoryEnabled) }
                    PaperDivider()
                    PaperRow(title: "retry failed dictations", detail: "keeps the recording of a take a network engine couldn't finish on this mac (up to 100 takes or 1 GB) until you retry or delete it. turning this off removes them now.") {
                        PaperToggle(isOn: Binding(get: { settings.retainFailedTakeAudio }, set: { on in
                            settings.retainFailedTakeAudio = on
                            if !on { companionManager.dictationTakeController.audioStore.discardAll() }
                        }))
                    }
                    PaperDivider()
                    PaperRow(title: "delete my dictation data…", detail: "wipes every take from this mac now. your dictionary and shortcuts stay.") {
                        Button("delete history…") { confirmDelete = true }.buttonStyle(PaperPillButtonStyle(destructive: true))
                    }
                }
            }
            PaperSectionLabel(text: "where things live")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "history", detail: TakeStore.defaultFileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                        Button("show") { NSWorkspace.shared.activateFileViewerSelecting([TakeStore.defaultFileURL]) }.buttonStyle(PaperPillButtonStyle())
                    }
                    PaperDivider()
                    PaperRow(title: "styles, dictionary, shortcuts", detail: DictationSpaceStore.defaultDirectoryURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                        Button("show") { NSWorkspace.shared.open(DictationSpaceStore.defaultDirectoryURL) }.buttonStyle(PaperPillButtonStyle())
                    }
                    PaperDivider()
                    PaperRow(title: "keys and account", detail: "~/.openclicky/shell.json") {
                        Button("show") { OpenClickyConfiguration.revealSettingsFile() }.buttonStyle(PaperPillButtonStyle())
                    }
                }
            }
            Text("read the privacy policy in the project's README.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
        }
        .confirmationDialog("delete every take on this mac?", isPresented: $confirmDelete) {
            Button("delete all history", role: .destructive) {
                try? companionManager.dictationTakeStore?.deleteAll()
                companionManager.dictationTakeController.audioStore.discardAll()
                companionManager.dictationTakeController.historyDidChange()
            }
        } message: { Text("this can't be undone.") }
    }
}

// MARK: - plan & usage

private struct PlanSettingsPage: View {
    @StateObject private var billing = BillingStatusModel()
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    var body: some View {
        SettingsPageScaffold(title: "plan & usage") {
            PaperCard {
                VStack(alignment: .leading, spacing: 14) {
                    if let summary = billing.summary {
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            Text("openclicky").font(Paper.title(18)).foregroundStyle(Paper.ink)
                            Text(summary.byok ? "your keys" : summary.plan).font(Paper.mono(10)).padding(.horizontal, 6).padding(.vertical, 2).background(Capsule().fill(Paper.highlighter)).foregroundStyle(Paper.ink)
                        }
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Text("\(summary.used)").font(.system(size: 34, weight: .medium, design: .serif)).foregroundStyle(Paper.ink)
                            Text("/ \(summary.limit) credits this period").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                        }
                        Text("renews \(summary.periodEnd)").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
                    } else if OpenClickyConfiguration.usesOwnKeys(OpenClickyConfiguration.settings) {
                        Text("your own keys").font(Paper.title(18)).foregroundStyle(Paper.ink)
                        Text("openclicky meters nothing: every request runs on the keys in shell.json.").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                    } else if authSession.accountEmail == nil {
                        Text("no account").font(Paper.title(18)).foregroundStyle(Paper.ink)
                        Text("the offline engine needs none. sign in under account for the openclicky engine and model polish, or add a sarvam key under engine.").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                    } else {
                        Text(billing.errorText ?? "loading…").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                    }
                }
                .padding(20)
            }
            PaperSectionLabel(text: "dictation on this mac")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "takes", detail: "as long as you like; the inactivity timeout ends a quiet one.") { EmptyView() }
                    PaperDivider()
                    PaperRow(title: "offline engine", detail: "unlimited, on this mac, no account.") { EmptyView() }
                    PaperDivider()
                    PaperRow(title: "sarvam engine", detail: "billed by sarvam to your key — see dashboard.sarvam.ai.") { EmptyView() }
                }
            }
        }
        .onAppear { billing.refresh() }
    }
}

// MARK: - account

private struct AccountSettingsPage: View {
    @ObservedObject var model: DictationWindowModel
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var isSigningIn = false
    @State private var failure: String?

    var body: some View {
        SettingsPageScaffold(title: "account") {
            if let accountEmail = authSession.accountEmail {
                PaperCard {
                    HStack(spacing: 14) {
                        ZStack {
                            Circle().fill(Paper.accentSoft)
                            Text(String(accountEmail.prefix(1)).lowercased()).font(Paper.body(16, weight: .semibold)).foregroundStyle(Paper.ink)
                        }
                        .frame(width: 44, height: 44)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(accountEmail).font(Paper.body(14, weight: .medium)).foregroundStyle(Paper.ink)
                            Text("personal workspace").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                        }
                        Spacer()
                    }
                    .padding(20)
                }
                PaperSectionLabel(text: "session")
                PaperCard {
                    PaperRow(title: "sign out", detail: "history stays safe on this mac.") {
                        Button("sign out") { authSession.signOut() }.buttonStyle(PaperPillButtonStyle())
                    }
                }
            } else {
                PaperCard {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("sign in to openclicky").font(Paper.title(18)).foregroundStyle(Paper.ink)
                        Text("an account gives you the openclicky engine and model polish without your own keys. the offline engine never needs one.").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                        TextField("enter your email", text: $email).textFieldStyle(.roundedBorder)
                        SecureField("enter your password", text: $password).textFieldStyle(.roundedBorder)
                        if let failure { Text(failure).font(Paper.body(11)).foregroundStyle(Paper.danger) }
                        HStack {
                            Spacer()
                            Button(isSigningIn ? "signing in…" : "sign in") {
                                isSigningIn = true
                                failure = nil
                                Task {
                                    let ok = await authSession.signIn(email: email.trimmingCharacters(in: .whitespaces), password: password)
                                    isSigningIn = false
                                    if !ok { failure = authSession.lastErrorText ?? "couldn't sign in. check your email and password." }
                                }
                            }
                            .buttonStyle(PaperPillButtonStyle(prominent: true)).disabled(isSigningIn || email.isEmpty || password.isEmpty)
                        }
                    }
                    .padding(20)
                }
                Text("accounts are invite-only for now. your own keys work without one: a sarvam key under engine, or openaiApiKey in shell.json.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
            }
        }
    }
}

// MARK: - about

private struct AboutSettingsPage: View {
    @ObservedObject private var updater = AppUpdater.shared

    var body: some View {
        SettingsPageScaffold(title: "about") {
            PaperCard {
                VStack(spacing: 0) {
                    HStack(spacing: 14) {
                        if let icon = NSApp.applicationIconImage { Image(nsImage: icon).resizable().frame(width: 52, height: 52) }
                        OpenClickyWordmark()
                        Spacer()
                        Text("version \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "") (\(Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? ""))").font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary)
                    }
                    .padding(18)
                    PaperDivider()
                    PaperRow(title: "software updates", detail: updater.isConfigured ? "checks daily for the latest version. \(updater.lastCheckDescription)." : "this build has no update feed (a development build).") {
                        Button("check now") { updater.checkNow() }.buttonStyle(PaperPillButtonStyle()).disabled(!updater.isConfigured)
                    }
                }
            }
            PaperSectionLabel(text: "the project")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "openclicky on github", detail: "open source, mit. issues and pull requests welcome.") {
                        Button("open ↗") { open("https://github.com/prasanthsasikumar/openclicky") }.buttonStyle(PaperPillButtonStyle())
                    }
                    PaperDivider()
                    PaperRow(title: "report a bug", detail: "the log at ~/Library/Logs/OpenClicky/app.log says what each take did.") {
                        HStack(spacing: 8) {
                            Button("show log") { NSWorkspace.shared.activateFileViewerSelecting([AppLog.fileURL]) }.buttonStyle(PaperPillButtonStyle())
                            Button("open issues ↗") { open("https://github.com/prasanthsasikumar/openclicky/issues") }.buttonStyle(PaperPillButtonStyle())
                        }
                    }
                    PaperDivider()
                    PaperRow(title: "privacy", detail: "with the offline engine nothing leaves this mac unless you turn on \"also polish offline takes\". other engines send audio to the provider you chose, with your key or account.") { EmptyView() }
                }
            }
        }
        .onAppear { updater.start() }
    }

    private func open(_ url: String) {
        if let url = URL(string: url) { NSWorkspace.shared.open(url) }
    }
}
