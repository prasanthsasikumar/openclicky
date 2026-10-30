//
//  SettingsPages.swift
//  OpenClicky
//
//  The settings pages of OpenClicky's one window (DictationWindow.swift), drawn from the Claude
//  Design "OpenClicky Settings Proposal": pages with coloured sidebar icons, each a grouped
//  form, and the native look they share. Every row comes from `SettingsCatalog`, so a page and a
//  search result draw the same control.
//

import AppKit
import Combine
import SwiftUI

/// The settings pages, in sidebar order. Your two pages (Your words, How it writes) sit among them,
/// after the pages that say `showsBeforeYourPages`. The order follows how someone thinks about
/// OpenClicky: what it is (General), the two things it does (Dictation, Clicky) and how Clicky
/// sounds (Voice), what you said and how it is written (your pages), then how it looks, the keys
/// you press, the AI keys you pay with, your account, privacy and about.
/// docs/superpowers/specs/2026-10-12-settings-revamp.md has the reasoning.
enum DictationSettingsPage: String, CaseIterable, Identifiable {
    case general, dictation, clicky, voice, appearance, music, widgets, calendar, tools, orb, keyboard, keys, account, privacy, about
    var id: String { rawValue }

    /// True for the pages the sidebar lists above your two pages: what you set up first (General,
    /// Dictation, Clicky, Voice) comes before what you dictated; the rest follows it.
    var showsBeforeYourPages: Bool {
        switch self {
        case .general, .dictation, .clicky, .voice: return true
        case .appearance, .music, .widgets, .calendar, .tools, .orb, .keyboard, .keys, .account, .privacy, .about: return false
        }
    }

    /// The settings pages above your two pages in the sidebar, in order.
    static var pagesBeforeYourPages: [DictationSettingsPage] { allCases.filter(\.showsBeforeYourPages) }

    /// The settings pages below your two pages in the sidebar, in order.
    static var pagesAfterYourPages: [DictationSettingsPage] { allCases.filter { !$0.showsBeforeYourPages } }

    var title: String {
        switch self {
        case .general: return "General"
        case .dictation: return "Dictation"
        case .clicky: return "Clicky"
        case .voice: return "Voice"
        case .orb: return "The Orb"
        case .appearance: return "Appearance"
        case .music: return "Music"
        case .widgets: return "Widgets"
        case .calendar: return "Calendar"
        case .tools: return "Tools"
        case .keyboard: return "Shortcuts"
        case .keys: return "Keys & Plans"
        case .account: return "Account"
        case .privacy: return "Privacy & Security"
        case .about: return "About"
        }
    }

    /// The page's header card: one line on what lives here.
    var subtitle: String {
        switch self {
        case .general: return "Starting up, the island's place and the menu bar."
        case .dictation: return "How a take is heard, written and pasted."
        case .clicky: return "The pointer that answers, points and does things for you."
        case .voice: return "How Clicky sounds when it talks back."
        case .appearance: return "How the island looks when it opens."
        case .music: return "What's playing, on the island with its controls."
        case .widgets: return "Small tiles beside the calendar on the island."
        case .calendar: return "Your week on the island, and adding events from it."
        case .tools: return "Handy extras on the island, each with its own button at the top."
        case .orb: return "The orb at the bottom of the screen while you dictate."
        case .keyboard: return "The keys that dictate, talk and type to Clicky."
        case .keys: return "Your own AI keys and plans, and what each one pays for."
        case .account: return "Your OpenClicky account, if you use one."
        case .privacy: return "What OpenClicky may use, and what stays on this Mac."
        case .about: return "Version, updates and where to get help."
        }
    }

    /// The sidebar icon, white on `tint`.
    var symbol: String {
        switch self {
        case .general: return "gearshape.fill"
        case .dictation: return "waveform"
        case .clicky: return "cursorarrow"
        case .voice: return "speaker.wave.2.fill"
        case .orb: return "capsule.fill"
        case .appearance: return "circle.lefthalf.filled"
        case .music: return "music.note"
        case .widgets: return "square.grid.2x2.fill"
        case .calendar: return "calendar"
        case .tools: return "switch.2"
        case .keyboard: return "command"
        case .keys: return "key.fill"
        case .account: return "person.crop.circle.fill"
        case .privacy: return "hand.raised.fill"
        case .about: return "info"
        }
    }

    var tint: Color {
        switch self {
        case .general: return Color(red: 0.557, green: 0.557, blue: 0.576)
        case .dictation: return Color(red: 1.0, green: 0.231, blue: 0.188)
        case .clicky: return Color(red: 1.0, green: 0.584, blue: 0.0)
        case .voice: return Color(red: 0.996, green: 0.176, blue: 0.333)
        case .orb: return Color(red: 0.11, green: 0.11, blue: 0.118)
        case .appearance: return Color(red: 0.11, green: 0.11, blue: 0.118)
        case .music: return Color(red: 0.98, green: 0.18, blue: 0.33)
        case .widgets: return Color(red: 0.0, green: 0.478, blue: 1.0)
        case .calendar: return Color(red: 1.0, green: 0.231, blue: 0.188)
        case .tools: return Color(red: 0.557, green: 0.557, blue: 0.576)
        case .keyboard: return Color(red: 0.388, green: 0.388, blue: 0.4)
        case .keys: return Color(red: 0.545, green: 0.42, blue: 0.0)
        case .account: return Color(red: 0.204, green: 0.78, blue: 0.349)
        case .privacy: return Color(red: 0.0, green: 0.478, blue: 1.0)
        case .about: return Color(red: 0.682, green: 0.682, blue: 0.698)
        }
    }
}

/// One page (or the search results) as a grouped form: a section per group, its footer under it.
struct SettingsFormView: View {
    let groups: [SettingsSectionGroup]
    /// The page's header card above its first group; none for search results.
    var page: DictationSettingsPage?
    /// The Settings tour asks for a row to be scrolled into view before it points at it.
    @ObservedObject private var tourTargets = SettingsTourTargets.shared

    var body: some View {
        ScrollViewReader { scrollProxy in
        Form {
            if let page {
                Section { SettingsPageHeader(page: page) }
            }
            ForEach(groups) { group in
                Section {
                    ForEach(group.items) { item in
                        item.view
                            .settingsTourTarget(item.id)
                            .id(item.id)
                    }
                } header: {
                    if !group.title.isEmpty { Text(group.title) }
                } footer: {
                    if !group.footers.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(group.footers) { footer in footer.view }
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onChange(of: tourTargets.scrollRequest) { requestedRowID in
            guard let requestedRowID else { return }
            withAnimation(.easeInOut(duration: 0.3)) { scrollProxy.scrollTo(requestedRowID, anchor: .center) }
            tourTargets.scrollRequest = nil
        }
        }
    }
}

// MARK: - Look

/// The settings window's colours and type: the system's own, so the window reads as part of
/// macOS in light and dark.
enum SettingsLook {
    /// The proposal's orange accent: #D24E2A in light, #E8653F in dark.
    static let accent = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            ? NSColor(srgbRed: 0.91, green: 0.396, blue: 0.247, alpha: 1)
            : NSColor(srgbRed: 0.824, green: 0.306, blue: 0.165, alpha: 1)
    })

    static let rowTitle = Font.system(size: 13)
    static let caption = Font.system(size: 11)
    static let micro = Font.system(size: 10)
    static let key = Font.system(size: 12, design: .monospaced)
    static func body(_ size: CGFloat, weight: Font.Weight = .regular) -> Font { .system(size: size, weight: weight) }

    static let inkTertiary = Color(nsColor: .tertiaryLabelColor)
    static let success = Color(nsColor: .systemGreen)
    static let danger = Color(nsColor: .systemRed)
    static let selection = accent.opacity(0.12)
    static let highlighter = Color.yellow.opacity(0.35)
    static let hairline = Color(nsColor: .separatorColor)
    static let warning = Color(nsColor: .systemOrange)

    enum Metric {
        /// The form gives every row its horizontal insets; rows add only this vertical breathing room.
        static let rowVertical: CGFloat = 2
        static let controlRadius: CGFloat = 5
    }
}

/// A native switch, small like System Settings' own. It carries the pointer cursor itself, so every
/// switch in the window shows it without each row having to remember.
struct SettingsSwitch: View {
    @Binding var isOn: Bool

    var body: some View {
        Toggle("", isOn: $isOn)
            .toggleStyle(.switch)
            .controlSize(.small)
            .labelsHidden()
            .tint(Color(nsColor: .controlAccentColor))
            .pointerCursor()
    }
}

/// Keycaps for a shortcut row ("⌃ ⌥ hold"): one cap per key and the gesture beside them.
struct SettingsKeys: View {
    let keys: [String]
    var gesture: String?

    var body: some View {
        HStack(spacing: 4) {
            ForEach(Array(keys.enumerated()), id: \.offset) { _, key in
                Text(key)
                    .font(.system(size: 12))
                    .frame(minWidth: 22, minHeight: 20)
                    .padding(.horizontal, 5)
                    .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color(nsColor: .controlColor)))
                    .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).strokeBorder(Color(nsColor: .separatorColor)))
            }
            if let gesture {
                Text(gesture).font(.system(size: 12)).foregroundStyle(.secondary).padding(.leading, 4)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

/// The card at the top of a page, like System Settings': the page's icon large in its coloured
/// tile, its name, and a line on what it holds.
struct SettingsPageHeader: View {
    let page: DictationSettingsPage

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: page.symbol)
                .font(.system(size: 19, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 40, height: 40)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(page.tint.gradient))
            VStack(alignment: .leading, spacing: 2) {
                Text(page.title).font(.system(size: 15, weight: .semibold))
                Text(page.subtitle).font(.system(size: 12)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}

/// A feature's icon beside its row: a small coloured tile with a white symbol.
struct SettingsFeatureTile: View {
    let symbol: String
    let tint: Color

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: 22, height: 22)
            .background(RoundedRectangle(cornerRadius: 5.5, style: .continuous).fill(tint.gradient))
            .accessibilityHidden(true)
    }
}
