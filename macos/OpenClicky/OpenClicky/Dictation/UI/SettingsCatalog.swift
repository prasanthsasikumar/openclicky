//
//  SettingsCatalog.swift
//  OpenClicky
//
//  Every setting is a `SettingsItem` in `SettingsCatalog`: title, copy, page, section and the
//  control itself, so a page and a search result in the window (SettingsPages.swift)
//  draw the very same row, and a control found by search can be changed right there. The pages'
//  items live in the Settings*Page.swift files beside this one; the rows here are drawn with
//  the system's own controls and type (`SettingsLook`).
//

import AppKit
import Combine
import SwiftUI

/// The version ("0.8.1") and build number ("285"), from the bundle.
enum AppVersion {
    static var shortVersion: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "" }
    static var buildNumber: String { Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "" }
}

// MARK: - The catalog

/// One setting: what it is called, what it says, where it lives, and the row that changes it.
struct SettingsItem: Identifiable {
    enum Chrome {
        /// A row inside the section's group, divided from its neighbours.
        case card
        /// A note under the section's group (the privacy summary, "language moved to styles").
        case footer
    }

    let id: String
    /// Where the row is drawn. Set by `SettingsLayout`, which places every row; a builder leaves it.
    var page: DictationSettingsPage = .general
    /// The label above the group ("Microphone"); empty for a page's unlabelled first group. Set by
    /// `SettingsLayout`, except for rows whose builder names their own group (one per calendar account).
    var section: String = ""
    let title: String
    var detail: String = ""
    /// Other words someone might search for ("startup" for open at login).
    var keywords: [String] = []
    var chrome: Chrome = .card
    /// False hides the item from its page and from search (the fn banner once fn is free).
    var isRelevant: @MainActor () -> Bool = { true }
    /// Set for an item that only search shows: a way to one of your own pages (Your words, How it
    /// writes), which aren't drawn from the catalog. It names the page in the search results.
    var searchOnlyGroup: String?
    /// True for rows in a page's folded group at the bottom ("More"): rarely changed, hidden until
    /// the group is opened. Search ignores the fold.
    var isAdvanced = false
    /// False for a row only search finds (incognito, whose switch lives in the sidebar's foot).
    var showsOnPage = true
    let view: AnyView

    /// The heading it is listed under in search results.
    var searchGroupTitle: String { searchOnlyGroup ?? page.searchTitle }

    func matches(_ query: String) -> Bool {
        let searchableText = ([title, detail, section, searchGroupTitle] + keywords).joined(separator: " ")
        return query
            .lowercased()
            .split(separator: " ")
            .allSatisfy { searchableText.localizedCaseInsensitiveContains($0) }
    }
}

/// Every setting on every page, built against the live settings so the rows are operable.
@MainActor
struct SettingsCatalog {
    let settings: DictationSettings
    let companionManager: CompanionManager

    /// Every row, placed and ordered by `SettingsLayout`, then the search-only ways to your own pages.
    var allItems: [SettingsItem] {
        SettingsLayout.arrange(unplacedItems()) + yourPagesSearchItems()
    }

    /// Every row as its builder makes it, before the layout gives it a page and a place.
    private func unplacedItems() -> [SettingsItem] {
        dictationItems() + keyboardItems() + orbItems()
            + clickyItems() + voiceItems()
            + islandAppearanceItems() + islandPluginItems() + calendarSettingsItems() + weatherSettingsItems()
            + clipboardSettingsItems() + notesSettingsItems() + timerSettingsItems()
            + accountItems() + keysItems() + privacyItems() + generalItems() + aboutItems()
            + navigationItems()
    }

    /// The rows that open a page of their own: Your words, How it writes and The orb from
    /// Dictation, Calendars from Island.
    func navigationItems() -> [SettingsItem] {
        func item(_ id: String, title: String, detail: String, keywords: [String], destination: DictationDestination) -> SettingsItem {
            SettingsItem(
                id: id, title: title, detail: detail, keywords: keywords,
                view: AnyView(SettingsNavigationRow(title: title, detail: detail, destination: destination)))
        }
        return [
            item("dictation.openWords", title: "Your words", detail: "Every take, with search. Try dictation here.",
                 keywords: ["history", "takes", "try"], destination: .section(.words)),
            item("dictation.openWriting", title: "How it writes", detail: "Dictionary, snippets and per-app styles.",
                 keywords: ["dictionary", "snippets", "styles"], destination: .section(.writing)),
            item("dictation.openOrb", title: "The orb", detail: "Style, theme, size and when it appears.",
                 keywords: ["orb", "pill", "look", "theme", "size"], destination: .settings(.orb)),
            item("island.openCalendars", title: "Calendars", detail: "Which calendars the island shows.",
                 keywords: ["calendar", "calendars", "hide", "show", "account"], destination: .settings(.calendars)),
        ]
    }

    /// What lives on Your words and How it writes, findable by search: each a row that opens its page.
    func yourPagesSearchItems() -> [SettingsItem] {
        func item(_ id: String, section: DictationSection, title: String, detail: String, keywords: [String]) -> SettingsItem {
            SettingsItem(
                id: id, page: .dictation, section: section.sidebarTitle, title: title, detail: detail, keywords: keywords,
                searchOnlyGroup: section.sidebarTitle, showsOnPage: false,
                view: AnyView(SettingsRow(title: title, detail: detail) {
                    Button("Open \(section.sidebarTitle)") { companionManager.showDictationWindow(section: section) }
                        .pointerCursor()
                }))
        }
        return [
            item("words.takes", section: .words, title: "Your takes", detail: "Every take you dictated, with search and filters.",
                 keywords: ["history", "takes", "transcript", "past", "record", "recording", "retry", "dictated"]),
            item("words.tryIt", section: .words, title: "Try it here", detail: "A box to dictate into and see what OpenClicky writes.",
                 keywords: ["test", "practice", "try", "scratch"]),
            item("writing.dictionary", section: .writing, title: "Dictionary", detail: "Names and terms written exactly as you expect.",
                 keywords: ["dictionary", "terms", "names", "spelling", "vocabulary", "words", "misheard"]),
            item("writing.snippets", section: .writing, title: "Snippets", detail: "Say a short phrase, get a longer text.",
                 keywords: ["snippet", "shortcut", "text expansion", "template", "signature", "address", "email"]),
            item("writing.styles", section: .writing, title: "Styles", detail: "How you sound, app by app: the rules the AI clean-up reads.",
                 keywords: ["style", "tone", "formal", "casual", "per app", "rules", "apps"]),
        ]
    }

}

/// One section of a page: its rows, and the notes under it.
struct SettingsSectionGroup: Identifiable {
    let id: String
    let title: String
    let items: [SettingsItem]
    var footers: [SettingsItem] = []
    /// The page's folded group ("More"), drawn closed until opened.
    var isAdvanced = false

    /// Consecutive items with the same section form one group; a footer item joins the group
    /// before it. `foldsAdvanced` marks a page's folded group; search results never fold.
    static func groups(_ items: [SettingsItem], foldsAdvanced: Bool = false, title: (SettingsItem) -> String = { $0.section }) -> [SettingsSectionGroup] {
        var groups: [SettingsSectionGroup] = []
        for item in items {
            let groupTitle = title(item)
            if let last = groups.last, last.title == groupTitle {
                var merged = last
                if item.chrome == .footer {
                    merged.footers.append(item)
                } else {
                    merged = SettingsSectionGroup(id: last.id, title: last.title, items: last.items + [item], footers: last.footers, isAdvanced: last.isAdvanced)
                }
                groups[groups.count - 1] = merged
            } else if item.chrome == .footer {
                groups.append(SettingsSectionGroup(id: "\(groups.count)-\(groupTitle)", title: groupTitle, items: [], footers: [item],
                                                   isAdvanced: foldsAdvanced && item.isAdvanced))
            } else {
                groups.append(SettingsSectionGroup(id: "\(groups.count)-\(groupTitle)", title: groupTitle, items: [item],
                                                   isAdvanced: foldsAdvanced && item.isAdvanced))
            }
        }
        return groups
    }
}

extension SettingsSectionGroup {
    /// Search results by page, the pages with a row whose title holds the query first ("dictionary"
    /// shows the Dictionary before the privacy rows that mention it); otherwise in sidebar order.
    static func searchResultGroups(_ matches: [SettingsItem], query: String) -> [SettingsSectionGroup] {
        let groups = groups(matches) { $0.searchGroupTitle }
        func namesTheQuery(_ group: SettingsSectionGroup) -> Bool {
            group.items.contains { $0.title.localizedCaseInsensitiveContains(query) }
        }
        return groups.filter(namesTheQuery) + groups.filter { !namesTheQuery($0) }
    }
}

private struct SettingsSearchQueryKey: EnvironmentKey {
    static let defaultValue = ""
}

extension EnvironmentValues {
    /// The search the row is shown for; its title and copy mark the matched words.
    var settingsSearchQuery: String {
        get { self[SettingsSearchQueryKey.self] }
        set { self[SettingsSearchQueryKey.self] = newValue }
    }
}

/// `text` with every word of `query` marked with the highlighter.
func settingsHighlighted(_ text: String, query: String) -> AttributedString {
    var attributed = AttributedString(text)
    for word in query.split(separator: " ") where !word.isEmpty {
        var searchStart = attributed.startIndex
        while searchStart < attributed.endIndex,
              let range = attributed[searchStart...].range(of: String(word), options: [.caseInsensitive, .diacriticInsensitive]) {
            attributed[range].backgroundColor = SettingsLook.highlighter
            searchStart = range.upperBound
        }
    }
    return attributed
}

// MARK: - Rows and small controls

/// A settings row: title, a line of copy, and the control on the right.
struct SettingsRow<Trailing: View>: View {
    let title: String
    var detail: String?
    /// A feature's coloured icon on the left (the island's pages); most rows have none.
    var icon: SettingsFeatureTile?
    @ViewBuilder var trailing: Trailing
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            if let icon { icon.padding(.trailing, -6) }
            VStack(alignment: .leading, spacing: 2) {
                Text(settingsHighlighted(title, query: searchQuery)).font(SettingsLook.rowTitle)
                if let detail, !detail.isEmpty {
                    Text(settingsHighlighted(detail, query: searchQuery))
                        .font(SettingsLook.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 16)
            trailing
        }
        .padding(.vertical, SettingsLook.Metric.rowVertical)
        .accessibilityElement(children: .contain)
        .accessibilityHint(detail ?? "")
    }
}

/// A switch bound to one of the dictation settings, observing them itself so it stays live
/// wherever it is drawn (a page or a search result).
struct SettingsToggle: View {
    @ObservedObject var settings: DictationSettings
    let keyPath: ReferenceWritableKeyPath<DictationSettings, Bool>

    var body: some View {
        SettingsSwitch(isOn: Binding(get: { settings[keyPath: keyPath] }, set: { settings[keyPath: keyPath] = $0 }))
    }
}

/// A small tag beside a title ("Private · offline", "Audio goes to Sarvam").
struct SettingsTag: View {
    let text: String
    var isPositive = false

    var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(isPositive ? Color(nsColor: .systemGreen) : .secondary)
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(RoundedRectangle(cornerRadius: 4, style: .continuous)
                .fill(isPositive ? Color(nsColor: .systemGreen).opacity(0.15) : Color.primary.opacity(0.07)))
    }
}

/// A note under a section ("Keys are sent to the OpenClicky backend…").
struct SettingsFootnote: View {
    let text: String
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        Text(settingsHighlighted(text, query: searchQuery))
            .font(SettingsLook.caption).foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

func openExternalLink(_ address: String) {
    if let url = URL(string: address) { NSWorkspace.shared.open(url) }
}

/// Bumps whenever shell.json changes — from these pages or edited by hand — so rows that read
/// it (the sarvam key, the provider badges, the account's backend and token) redraw.
@MainActor
final class ShellSettingsRevision: ObservableObject {
    static let shared = ShellSettingsRevision()

    @Published private(set) var revision = 0
    private var credentialsObserver: NSObjectProtocol?

    private init() {
        credentialsObserver = NotificationCenter.default.addObserver(
            forName: OpenClickyConfiguration.credentialsChangedNotification, object: nil, queue: .main
        ) { _ in
            Task { @MainActor in ShellSettingsRevision.shared.noteChanged() }
        }
    }

    /// Call after writing shell.json through `OpenClickyConfiguration.update`.
    func noteChanged() { revision += 1 }
}
