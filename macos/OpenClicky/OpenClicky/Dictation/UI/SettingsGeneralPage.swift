//
//  SettingsGeneralPage.swift
//  OpenClicky
//
//  Settings → general: how openclicky looks and starts. Appearance, behavior (open at login,
//  the quiet-take timeout, reduce motion, the walkthrough) and about (version and updates,
//  the source, reporting a bug), which used to be a page of its own.
//

import AppKit
import SwiftUI

extension SettingsCatalog {
    func generalItems() -> [SettingsItem] {
        let dictationKeyName = settings.dictationKey.keycapLabel
        return [
            SettingsItem(
                id: "general.appearance", page: .general, section: "appearance",
                title: "appearance", detail: "light, dark, or match the mac. the orb and its transcript box follow this too; the notch stays black.",
                keywords: ["light", "dark", "match the mac", "theme", "mode", "colour", "color"],
                view: AnyView(AppearancePicker(settings: settings))),
            SettingsItem(
                id: "general.openAtLogin", page: .general, section: "behavior",
                title: "open at login", detail: "start openclicky when you log in, so \(dictationKeyName) always works.",
                keywords: ["startup", "launch", "restart", "login item", "boot"],
                view: AnyView(SettingsRow(title: "open at login", detail: "start openclicky when you log in, so \(dictationKeyName) always works.") { LoginItemToggle() })),
            SettingsItem(
                id: "general.inactivityTimeout", page: .general, section: "behavior",
                title: "end a quiet take after", detail: "a tapped take finishes on its own after this much silence.",
                keywords: ["inactivity", "timeout", "silence", "minutes"],
                view: AnyView(SettingsRow(title: "end a quiet take after", detail: "a tapped take finishes on its own after this much silence.") {
                    InactivityTimeoutPicker(settings: settings)
                })),
            SettingsItem(
                id: "general.reduceMotion", page: .general, section: "behavior",
                title: "reduce motion", detail: "the orb and its box appear and leave without animating.",
                keywords: ["animation", "motion", "accessibility", "calm"],
                view: AnyView(SettingsRow(title: "reduce motion", detail: "the orb and its box appear and leave without animating.") {
                    SettingsToggle(settings: settings, keyPath: \.reduceAnimation)
                })),
            SettingsItem(
                id: "general.walkthrough", page: .general, section: "behavior",
                title: "welcome walkthrough", detail: "permissions, engine, a first take — about two minutes.",
                keywords: ["onboarding", "setup", "replay", "tour", "first run"],
                view: AnyView(SettingsRow(title: "welcome walkthrough", detail: "permissions, engine, a first take — about two minutes.") {
                    Button("replay") { companionManager.showDictationOnboarding(replay: true) }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "general.version", page: .general, section: "about",
                title: AppVersion.displayString, detail: "software updates, checked daily.",
                keywords: ["version", "update", "updates", "sparkle", "check now", "about"],
                view: AnyView(AboutVersionRow())),
            SettingsItem(
                id: "general.openSource", page: .general, section: "about",
                title: "open source, MIT", detail: "issues and pull requests welcome.",
                keywords: ["github", "source", "license", "mit", "code"],
                view: AnyView(SettingsRow(title: "open source, MIT", detail: "issues and pull requests welcome.") {
                    Button("github ↗") { openExternalLink("https://github.com/prasanthsasikumar/openclicky") }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "general.reportBug", page: .general, section: "about",
                title: "report a bug", detail: "attach the log — it lists what each take did, not what you said.",
                keywords: ["bug", "log", "issue", "problem", "feedback", "app.log"],
                view: AnyView(SettingsRow(title: "report a bug", detail: "attach the log — it lists what each take did, not what you said.") {
                    HStack(spacing: 8) {
                        Button("show log") { NSWorkspace.shared.activateFileViewerSelecting([AppLog.fileURL]) }.buttonStyle(PaperPillButtonStyle())
                        Button("open issues ↗") { openExternalLink("https://github.com/prasanthsasikumar/openclicky/issues") }.buttonStyle(PaperPillButtonStyle())
                    }
                })),
        ]
    }
}

// MARK: - appearance

private struct AppearancePicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 14) {
                ForEach(DictationAppearance.allCases) { appearance in
                    AppearanceSwatch(appearance: appearance, isSelected: settings.appearance == appearance) { settings.appearance = appearance }
                }
            }
            Text("the orb and its transcript box follow this too. the notch stays black.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
        }
        .padding(Paper.Metric.rowHorizontal)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct AppearanceSwatch: View {
    let appearance: DictationAppearance
    let isSelected: Bool
    let action: () -> Void

    private var label: String {
        switch appearance {
        case .light: return "light"
        case .dark: return "dark"
        case .system: return "match the mac"
        }
    }

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 8).fill(appearance == .dark ? Color(white: 0.12) : Color(white: 0.96))
                    if appearance == .system {
                        Path { path in path.move(to: CGPoint(x: 0, y: 70)); path.addLine(to: CGPoint(x: 160, y: 0)); path.addLine(to: CGPoint(x: 160, y: 70)); path.closeSubpath() }
                            .fill(Color(white: 0.12)).clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                    HStack(spacing: 6) {
                        RoundedRectangle(cornerRadius: 3).fill(appearance == .dark ? Color(white: 0.2) : Color(white: 0.88)).frame(width: 36)
                        VStack(alignment: .leading, spacing: 4) {
                            RoundedRectangle(cornerRadius: 2).fill(Paper.accent.opacity(0.7)).frame(width: 40, height: 4)
                            RoundedRectangle(cornerRadius: 2).fill(Color.gray.opacity(0.4)).frame(width: 70, height: 4)
                            RoundedRectangle(cornerRadius: 2).fill(Color.gray.opacity(0.3)).frame(width: 55, height: 4)
                        }
                        Spacer()
                    }
                    .padding(10)
                }
                .frame(width: 160, height: 70)
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(isSelected ? Paper.accentFill : Paper.hairline, lineWidth: isSelected ? 2 : 1))
                Text(isSelected ? "✓ \(label)" : label)
                    .font(Paper.body(12, weight: isSelected ? .semibold : .regular)).foregroundStyle(Paper.ink)
            }
        }
        .buttonStyle(.plain).pointerCursor()
        .accessibilityLabel(label)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

// MARK: - behavior

/// The login item, through SMAppService.
enum LoginItem {
    static var isEnabled: Bool { SMAppServiceBridge.isEnabled }
    @discardableResult
    static func set(enabled: Bool) -> Bool { SMAppServiceBridge.set(enabled: enabled) }
}

private struct LoginItemToggle: View {
    @State private var launchesAtLogin = LoginItem.isEnabled

    var body: some View {
        PaperToggle(isOn: Binding(get: { launchesAtLogin }, set: { isOn in launchesAtLogin = LoginItem.set(enabled: isOn) }))
            .onAppear { launchesAtLogin = LoginItem.isEnabled }
    }
}

private struct InactivityTimeoutPicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.inactivityTimeoutMinutes) {
            Text("1 min").tag(1); Text("3 min").tag(3); Text("5 min").tag(5); Text("10 min").tag(10); Text("never").tag(0)
        }
        .labelsHidden().frame(width: 96)
    }
}

// MARK: - about

private struct AboutVersionRow: View {
    @ObservedObject private var updater = AppUpdater.shared

    var body: some View {
        SettingsRow(title: AppVersion.displayString,
                    detail: updater.isConfigured ? "checks daily. \(updater.lastCheckDescription)." : "this build has no update feed (a development build).") {
            Button("check now") { updater.checkNow() }
                .buttonStyle(PaperPillButtonStyle())
                .disabled(!updater.isConfigured)
        }
        .onAppear { updater.start() }
    }
}
