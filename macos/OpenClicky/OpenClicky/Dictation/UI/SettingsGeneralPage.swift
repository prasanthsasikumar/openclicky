//
//  SettingsGeneralPage.swift
//  OpenClicky
//
//  Settings → General: how OpenClicky starts. The menu bar icon (always on while the island is
//  hidden), open at login, the tours and reduce motion; appearance is the toolbar's button
//  (AppearanceToolbarMenu, its row only for search); the version, updates and help rows come from
//  SettingsAboutPage.swift. Where the island lives is built here and shown on Island.
//

import AppKit
import Combine
import SwiftUI

extension SettingsCatalog {
    func generalItems() -> [SettingsItem] {
        let dictationKeyName = settings.dictationKey.keycapLabel
        return [
            SettingsItem(
                id: "general.appearance",
                title: "Appearance", detail: "The orb and its transcript box follow this. The island stays dark.",
                keywords: ["light", "dark", "auto", "match the mac", "theme", "mode", "colour", "color"],
                view: AnyView(AppearancePicker(settings: settings))),
            SettingsItem(
                id: "general.hudPlacement",
                title: "Where it lives", detail: "The notch, a drawer on the left or right edge, or hidden until you use a shortcut.",
                keywords: ["notch", "island", "hud", "side", "edge", "drawer", "hide", "hidden", "position", "placement", "shortcuts only"],
                view: AnyView(HUDPlacementRow(companionManager: companionManager))),
            SettingsItem(
                id: "general.menuBarIcon",
                title: "Show in menu bar", detail: "Off is fine: the island is OpenClicky’s home.",
                keywords: ["menu bar", "status item", "icon", "tray"],
                view: AnyView(MenuBarIconRow(companionManager: companionManager))),
            SettingsItem(
                id: "general.appIcon",
                title: "App icon", detail: "Its colour in the Dock and in this window. Finder keeps the red one.",
                keywords: ["icon", "colour", "color", "dock", "app icon", "signal", "ultraviolet", "sunset", "paper", "aqua", "purple", "blue", "pink", "red"],
                view: AnyView(AppIconColorPicker(settings: settings))),
            SettingsItem(
                id: "general.openAtLogin",
                title: "Open at login", detail: "So \(dictationKeyName) always works.",
                keywords: ["startup", "launch", "restart", "login item", "boot"],
                view: AnyView(SettingsRow(title: "Open at login", detail: "So \(dictationKeyName) always works.") { LoginItemToggle() })),
            SettingsItem(
                id: "general.walkthrough",
                title: "Welcome tour", detail: "Clicky shows you dictating, asking and handing over a task, from the island.",
                keywords: ["onboarding", "setup", "replay", "tour", "first run"],
                view: AnyView(SettingsRow(title: "Welcome tour", detail: "Clicky shows you dictating, asking and handing over a task, from the island.") {
                    Button("Replay…") { companionManager.firstRunTour.replay() }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "general.settingsTour",
                title: "Settings tour", detail: "A one-minute look around this window: Clicky points at what matters most.",
                keywords: ["tour", "crash course", "show me around", "help", "guide", "replay", "walkthrough"],
                view: AnyView(SettingsRow(title: "Settings tour", detail: "A one-minute look around this window: Clicky points at what matters most.") {
                    Button("Play") { companionManager.startSettingsTour() }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "general.reduceMotion",
                title: "Reduce motion", detail: "The orb and the island appear and leave without animating.",
                keywords: ["animation", "motion", "accessibility", "calm"],
                view: AnyView(SettingsRow(title: "Reduce motion", detail: "The orb and the island appear and leave without animating.") {
                    SettingsToggle(settings: settings, keyPath: \.reduceAnimation)
                })),
            SettingsItem(
                id: "about.autoUpdate",
                title: "Check for updates daily", detail: "Updates come through Sparkle.",
                keywords: ["update", "updates", "sparkle", "automatic"],
                view: AnyView(AutomaticUpdatesRow())),
        ]
    }
}

// MARK: - appearance

/// Light, dark or auto, from a button at the window's top right: the window, the orb and its
/// transcript box follow it (the island stays dark). The icon shows the current choice.
struct AppearanceToolbarMenu: View {
    @ObservedObject var settings: DictationSettings

    private func symbol(for appearance: DictationAppearance) -> String {
        switch appearance {
        case .light: return "sun.max"
        case .dark: return "moon"
        case .system: return "circle.lefthalf.filled"
        }
    }

    private func title(for appearance: DictationAppearance) -> String {
        switch appearance {
        case .light: return "Light"
        case .dark: return "Dark"
        case .system: return "Auto"
        }
    }

    var body: some View {
        Menu {
            Picker("Appearance", selection: $settings.appearance) {
                ForEach(DictationAppearance.allCases) { appearance in
                    Label(title(for: appearance), systemImage: symbol(for: appearance)).tag(appearance)
                }
            }
            .pickerStyle(.inline)
        } label: {
            Image(systemName: symbol(for: settings.appearance))
        }
        .menuIndicator(.hidden)
        .help("Appearance: \(title(for: settings.appearance)). The orb follows it; the island stays dark.")
        .accessibilityLabel("Appearance")
        .pointerCursor()
    }
}

/// The app icon's colour: one tile per colour, the pick shown in the Dock at once.
private struct AppIconColorPicker: View {
    @ObservedObject var settings: DictationSettings
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(settingsHighlighted("App icon", query: searchQuery)).font(SettingsLook.rowTitle)
                Text(settingsHighlighted("Its colour in the Dock and in this window. Finder keeps the red one.", query: searchQuery))
                    .font(SettingsLook.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 14) {
                ForEach(AppIconColor.allCases) { color in
                    AppIconColorTile(color: color, isSelected: settings.appIconColor == color) { settings.appIconColor = color }
                }
            }
        }
        .padding(.vertical, 4)
    }
}

private struct AppIconColorTile: View {
    let color: AppIconColor
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(color.imageName)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 52, height: 52)
                    .padding(3)
                    .background(
                        RoundedRectangle(cornerRadius: 14, style: .continuous)
                            .strokeBorder(isSelected ? SettingsLook.accent : Color.clear, lineWidth: 2)
                    )
                Text(color.title)
                    .font(.system(size: 11, weight: isSelected ? .semibold : .regular))
                    .foregroundStyle(isSelected ? .primary : .secondary)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(color.title) app icon")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
        .pointerCursor()
    }
}

private struct AppearancePicker: View {
    @ObservedObject var settings: DictationSettings
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(settingsHighlighted("Appearance", query: searchQuery)).font(SettingsLook.rowTitle)
                Text(settingsHighlighted("The orb and its transcript box follow this. The island stays dark.", query: searchQuery))
                    .font(SettingsLook.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 16)
            HStack(spacing: 12) {
                ForEach(DictationAppearance.allCases) { appearance in
                    AppearanceTile(appearance: appearance, isSelected: settings.appearance == appearance) { settings.appearance = appearance }
                }
            }
        }
        .padding(.vertical, 4)
    }
}

private struct AppearanceTile: View {
    let appearance: DictationAppearance
    let isSelected: Bool
    let action: () -> Void

    private var label: String {
        switch appearance {
        case .light: return "Light"
        case .dark: return "Dark"
        case .system: return "Auto"
        }
    }

    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                ZStack {
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .fill(appearance == .dark ? Color(white: 0.17) : Color(white: 0.95))
                    if appearance == .system {
                        Path { path in path.move(to: CGPoint(x: 68, y: 0)); path.addLine(to: CGPoint(x: 68, y: 46)); path.addLine(to: CGPoint(x: 0, y: 46)); path.closeSubpath() }
                            .fill(Color(white: 0.17))
                            .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
                    }
                    Capsule().fill(SettingsLook.accent).frame(width: 28, height: 18)
                }
                .frame(width: 68, height: 46)
                .overlay(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .strokeBorder(isSelected ? SettingsLook.accent : Color(nsColor: .separatorColor), lineWidth: isSelected ? 2 : 0.5)
                )
                Text(label)
                    .font(.system(size: 11, weight: isSelected ? .semibold : .regular))
                    .foregroundStyle(isSelected ? .primary : .secondary)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
        .pointerCursor()
    }
}

// MARK: - the island and the menu bar

extension HUDPlacement {
    /// The pop-up's label in the Settings window ("Left edge").
    var settingsTitle: String {
        switch self {
        case .notch: return "Notch"
        case .left: return "Left edge"
        case .right: return "Right edge"
        case .hidden: return "Hidden"
        }
    }

    var settingsDetail: String {
        switch self {
        case .notch: return "Hangs from the notch. Hover to peek, click to open."
        case .left: return "A drawer on the left edge of every screen. Hover the handle to open."
        case .right: return "A drawer on the right edge of every screen. Hover the handle to open."
        case .hidden: return "Nothing on screen until you use a shortcut. The menu bar icon opens the panel."
        }
    }
}

/// Follows `hudPlacement` on its own instead of observing the whole companion manager, which
/// republishes on every audio level.
private struct HUDPlacementRow: View {
    let companionManager: CompanionManager
    @State private var placement: HUDPlacement

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        _placement = State(initialValue: companionManager.hudPlacement)
    }

    var body: some View {
        SettingsRow(title: "Where it lives", detail: placement.settingsDetail) {
            Picker("", selection: Binding(get: { placement }, set: { companionManager.setHUDPlacement($0) })) {
                ForEach(HUDPlacement.allCases) { placement in Text(placement.settingsTitle).tag(placement) }
            }
            .labelsHidden()
            .fixedSize()
            .pointerCursor()
        }
        .onReceive(companionManager.$hudPlacement) { placement = $0 }
    }
}


/// The component the open island shows when nothing else is going on.

/// Forced on while the island is hidden: the menu bar icon is then the only way into the panel.
private struct MenuBarIconRow: View {
    let companionManager: CompanionManager
    @State private var isVisible: Bool
    @State private var placement: HUDPlacement

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        _isVisible = State(initialValue: companionManager.isMenuBarIconVisible)
        _placement = State(initialValue: companionManager.hudPlacement)
    }

    var body: some View {
        let isForcedOn = placement == .hidden
        SettingsRow(title: "Show in menu bar",
                    detail: isForcedOn ? "Always on while OpenClicky is hidden, so you can still reach it." : "Off is fine: the island is OpenClicky’s home.") {
            SettingsSwitch(isOn: Binding(get: { isVisible || isForcedOn }, set: { companionManager.setMenuBarIconVisible($0) }))
                .disabled(isForcedOn)
        }
        .onReceive(companionManager.$isMenuBarIconVisible) { isVisible = $0 }
        .onReceive(companionManager.$hudPlacement) { placement = $0 }
    }
}

// MARK: - startup

/// The login item, through SMAppService.
enum LoginItem {
    static var isEnabled: Bool { SMAppServiceBridge.isEnabled }
    @discardableResult
    static func set(enabled: Bool) -> Bool { SMAppServiceBridge.set(enabled: enabled) }
}

private struct LoginItemToggle: View {
    @State private var launchesAtLogin = LoginItem.isEnabled

    var body: some View {
        SettingsSwitch(isOn: Binding(get: { launchesAtLogin }, set: { isOn in launchesAtLogin = LoginItem.set(enabled: isOn) }))
            .onAppear { launchesAtLogin = LoginItem.isEnabled }
    }
}
