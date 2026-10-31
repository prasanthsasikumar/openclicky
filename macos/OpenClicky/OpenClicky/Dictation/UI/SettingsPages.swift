//
//  SettingsPages.swift
//  OpenClicky
//
//  The settings pages of OpenClicky's one window (DictationWindow.swift), drawn from the Claude
//  Design "OpenClicky settings — redesign mockups": six pages with coloured sidebar icons, each a
//  grouped form with its rarely changed rows folded into "More", and the native look they share.
//  Every row comes from `SettingsCatalog`, placed by `SettingsLayout`, so a page and a search
//  result draw the same control.
//

import AppKit
import Combine
import SwiftUI

/// The settings pages. Six are in the sidebar (`sidebarPages`, in its order); The orb and Calendars
/// are opened from a row on their parent page and keep the parent selected in the sidebar. The
/// order follows the Claude Design "OpenClicky settings — redesign mockups": the two things
/// OpenClicky does (Dictation, Clicky), where it lives (Island), what pays for it (Keys & Account),
/// what it may see (Privacy), then General. `SettingsLayout` says which rows each page holds.
enum DictationSettingsPage: String, CaseIterable, Identifiable {
    case dictation, clicky, island, account, privacy, general, orb, calendars
    var id: String { rawValue }

    /// The pages the sidebar lists, in order.
    static let sidebarPages: [DictationSettingsPage] = [.dictation, .clicky, .island, .account, .privacy, .general]

    /// The sidebar page a page opened from a row belongs to; a sidebar page is its own.
    var sidebarPage: DictationSettingsPage {
        switch self {
        case .orb: return .dictation
        case .calendars: return .island
        case .dictation, .clicky, .island, .account, .privacy, .general: return self
        }
    }

    var title: String {
        switch self {
        case .dictation: return "Dictation"
        case .clicky: return "Clicky"
        case .island: return "Island"
        case .account: return "Keys & Account"
        case .privacy: return "Privacy"
        case .general: return "General"
        case .orb: return "The orb"
        case .calendars: return "Calendars"
        }
    }

    /// The heading search results are listed under: "Dictation › The orb" for a page opened from a row.
    var searchTitle: String {
        sidebarPage == self ? title : "\(sidebarPage.title) › \(title)"
    }

    /// The page's header card: one line on what lives here.
    var subtitle: String {
        switch self {
        case .dictation: return "Hold fn, speak, and the words land at your cursor."
        case .clicky: return "Hold ⌃⌥ and ask about what’s on screen."
        case .island: return "What lives around the notch."
        case .account: return "Your own keys come first. An account is optional."
        case .privacy: return "What OpenClicky can see, and what it keeps."
        case .general: return "Startup, appearance and updates."
        case .orb: return "The small narrator that shows while you dictate."
        case .calendars: return "Which calendars the island shows."
        }
    }

    /// The sidebar icon, white on `tint`.
    var symbol: String {
        switch self {
        case .dictation: return "mic.fill"
        case .clicky: return "sparkle"
        case .island: return "rectangle.topthird.inset.filled"
        case .account: return "key.fill"
        case .privacy: return "hand.raised.fill"
        case .general: return "gearshape.fill"
        case .orb: return "capsule.fill"
        case .calendars: return "calendar"
        }
    }

    var tint: Color {
        switch self {
        case .dictation, .orb: return Color(red: 0.039, green: 0.518, blue: 1.0)
        case .clicky: return Color(red: 0.686, green: 0.322, blue: 0.871)
        case .island, .calendars: return Color(red: 0.11, green: 0.11, blue: 0.118)
        case .account: return Color(red: 0.204, green: 0.78, blue: 0.349)
        case .privacy: return Color(red: 0.369, green: 0.361, blue: 0.902)
        case .general: return Color(red: 0.557, green: 0.557, blue: 0.576)
        }
    }
}

/// One page (or the search results) as a grouped form: a section per group, its footer under it.
/// A page's folded group ("More") is a disclosure row that opens in place.
struct SettingsFormView: View {
    let groups: [SettingsSectionGroup]
    /// The page's header card above its first group; none for search results.
    var page: DictationSettingsPage?
    /// The Settings tour asks for a row to be scrolled into view before it points at it.
    @ObservedObject private var tourTargets = SettingsTourTargets.shared
    /// The folded groups opened on this page; they close again when the page is left.
    @State private var openAdvancedGroupIDs: Set<String> = []

    var body: some View {
        ScrollViewReader { scrollProxy in
        Form {
            if let page {
                Section { SettingsPageHeader(page: page) }
            }
            ForEach(groups) { group in
                if group.isAdvanced {
                    let isOpen = openAdvancedGroupIDs.contains(group.id)
                    Section {
                        // The whole row opens the group: a DisclosureGroup only answers on its
                        // small arrow, which people missed.
                        Button(action: {
                            withAnimation(.easeInOut(duration: 0.2)) {
                                if isOpen { openAdvancedGroupIDs.remove(group.id) } else { openAdvancedGroupIDs.insert(group.id) }
                            }
                        }) {
                            HStack(spacing: 8) {
                                Image(systemName: "chevron.right")
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(.secondary)
                                    .rotationEffect(.degrees(isOpen ? 90 : 0))
                                Text(group.title).font(SettingsLook.rowTitle)
                                Spacer()
                                if !isOpen {
                                    Text("\(group.items.count) settings").font(SettingsLook.caption).foregroundStyle(.secondary)
                                }
                            }
                            .padding(.vertical, SettingsLook.Metric.rowVertical)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .pointerCursor()
                        .accessibilityLabel(group.title)
                        .accessibilityValue(isOpen ? "Open" : "Closed")
                        if isOpen {
                            rows(of: group)
                            ForEach(group.footers) { footer in footer.view }
                        }
                    }
                } else {
                    Section {
                        rows(of: group)
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
        }
        .formStyle(.grouped)
        .onChange(of: tourTargets.scrollRequest) { requestedRowID in
            guard let requestedRowID else { return }
            withAnimation(.easeInOut(duration: 0.3)) { scrollProxy.scrollTo(requestedRowID, anchor: .center) }
            tourTargets.scrollRequest = nil
        }
        }
    }

    private func rows(of group: SettingsSectionGroup) -> some View {
        ForEach(group.items) { item in
            item.view
                .settingsTourTarget(item.id)
                .id(item.id)
        }
    }
}

private struct SettingsNavigateKey: EnvironmentKey {
    static let defaultValue: (DictationDestination) -> Void = { _ in }
}

extension EnvironmentValues {
    /// Opens a page from a row on another (The orb from Dictation), so the toolbar's back button
    /// returns. Set by the window's root view.
    var settingsNavigate: (DictationDestination) -> Void {
        get { self[SettingsNavigateKey.self] }
        set { self[SettingsNavigateKey.self] = newValue }
    }
}

/// A row that opens another page: its name, a line on what is there, and a chevron. The whole
/// row is the button, like System Settings' own.
struct SettingsNavigationRow: View {
    let title: String
    let detail: String
    let destination: DictationDestination
    @Environment(\.settingsNavigate) private var navigate

    var body: some View {
        Button(action: { navigate(destination) }) {
            SettingsRow(title: title, detail: detail) {
                Image(systemName: "chevron.right")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.tertiary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityAddTraits(.isButton)
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
