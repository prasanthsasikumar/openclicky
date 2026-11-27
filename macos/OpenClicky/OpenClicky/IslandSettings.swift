//
//  IslandSettings.swift
//  OpenClicky
//
//  Settings as a drop-down from the island. The gear (and the menu bar's Settings…) unfolds the
//  island into the same six pages the window has, built from the same `SettingsCatalog` and
//  `SettingsLayout`, so there is one set of settings and two places to reach it. It stays open
//  while you use it and closes on its ✕, esc, the gear again, or a click anywhere else. Pages
//  that need room of their own (Your words, How it writes) open in the window.
//

import SwiftUI

enum IslandSettingsLayout {
    static let width: CGFloat = 760
    static let sidebarWidth: CGFloat = 184
    static let preferredHeight: CGFloat = 560
    static let headerHeight: CGFloat = 40

    /// As tall as it likes, but never past the screen: a margin is kept above the Dock.
    static func height(visibleHeight: CGFloat) -> CGFloat {
        min(preferredHeight, max(320, visibleHeight - 90))
    }
}

struct IslandSettingsView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @State private var searchText = ""
    /// The pages opened from rows on the way here (The orb from Dictation), for the back button.
    @State private var trail: [DictationSettingsPage] = []

    init(model: NotchHUDModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
    }

    private var page: DictationSettingsPage { model.settingsPage ?? .general }
    private var query: String { searchText.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        let relevantItems = SettingsCatalog(settings: settings, companionManager: companionManager).allItems.filter { $0.isRelevant() }
        VStack(spacing: 0) {
            header
            HStack(spacing: 0) {
                sidebar
                Rectangle().fill(Color.white.opacity(0.08)).frame(width: 1)
                content(relevantItems: relevantItems)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .padding(.top, model.cardTopInset)
        .environment(\.colorScheme, .dark)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("settings")
    }

    private var header: some View {
        HStack(spacing: 8) {
            if !trail.isEmpty && query.isEmpty {
                Button(action: goBack) { Image(systemName: "chevron.left") }
                    .buttonStyle(IslandIconButtonStyle(size: 11, frameScale: 2))
                    .help("back")
                    .accessibilityLabel("back")
            }
            Text(query.isEmpty ? page.title : "Search results")
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(DS.HUD.text)
            Spacer()
            Button(action: openInWindow) { Image(systemName: "macwindow") }
                .buttonStyle(IslandIconButtonStyle(size: 11, frameScale: 2))
                .help("open settings in the window")
                .accessibilityLabel("open settings in the window")
            Button(action: { model.dismiss() }) { Image(systemName: "xmark") }
                .buttonStyle(IslandIconButtonStyle(size: 10, frameScale: 2.2))
                .help("close settings")
                .accessibilityLabel("close settings")
                .keyboardShortcut(.cancelAction)
        }
        .padding(.leading, IslandSettingsLayout.sidebarWidth + 18)
        .padding(.trailing, 12)
        .frame(height: IslandSettingsLayout.headerHeight)
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.system(size: 10.5, weight: .semibold)).foregroundColor(DS.HUD.text3)
                TextField("Search", text: $searchText)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text)
            }
            .padding(.horizontal, 8)
            .frame(height: 26)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.white.opacity(0.09)))
            .padding(.bottom, 8)

            ForEach(DictationSettingsPage.sidebarPages(handheldIsOn: HandheldPlugin.shared.isEnabled)) { sidebarPage in
                let isSelected = query.isEmpty && page.sidebarPage == sidebarPage
                Button(action: { select(sidebarPage) }) {
                    HStack(spacing: 9) {
                        Image(systemName: sidebarPage.symbol)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundColor(isSelected ? DS.HUD.text : DS.HUD.text2)
                            .frame(width: 22, height: 22)
                            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.white.opacity(isSelected ? 0.2 : 0.09)))
                        Text(sidebarPage.title)
                            .font(.system(size: 12.5, weight: isSelected ? .semibold : .regular))
                            .foregroundColor(isSelected ? DS.HUD.text : DS.HUD.text2)
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 7)
                    .frame(height: 32)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white.opacity(isSelected ? 0.1 : 0)))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .accessibilityAddTraits(isSelected ? .isSelected : [])
            }
            Spacer(minLength: 0)
            Text("OpenClicky \(AppVersion.shortVersion)")
                .font(.system(size: 10.5))
                .foregroundColor(DS.HUD.text3)
                .padding(.leading, 7)
        }
        .padding(.horizontal, 10)
        .padding(.bottom, 12)
        .frame(width: IslandSettingsLayout.sidebarWidth)
        .frame(maxHeight: .infinity, alignment: .top)
        .animation(Motion.hover, value: page)
    }

    @ViewBuilder
    private func content(relevantItems: [SettingsItem]) -> some View {
        if !query.isEmpty {
            let matches = relevantItems.filter { $0.matches(query) }
            if matches.isEmpty {
                Text("No settings match “\(query)”.")
                    .font(.system(size: 12.5))
                    .foregroundColor(DS.HUD.text2)
            } else {
                form(SettingsFormView(groups: SettingsSectionGroup.searchResultGroups(matches, query: query)))
                    .environment(\.settingsSearchQuery, query)
            }
        } else {
            form(SettingsFormView(groups: SettingsSectionGroup.groups(relevantItems.filter { $0.page == page && $0.showsOnPage }, foldsAdvanced: true), page: page))
                .id(page)
                .transition(.opacity.animation(Motion.swap))
        }
    }

    /// The window's form, on the island: its own grouped rows over the island's black instead of
    /// a window background, and rows that open a page keep you here when the page is a settings page.
    private func form(_ view: SettingsFormView) -> some View {
        view
            .scrollContentBackground(.hidden)
            .environment(\.settingsNavigate) { destination in
                switch destination {
                case .settings(let nextPage):
                    trail.append(page)
                    searchText = ""
                    model.showSettings(nextPage)
                case .section(let section):
                    // Your words and How it writes are whole pages of their own: the window has them.
                    model.dismiss()
                    companionManager.showDictationWindow(section: section)
                }
            }
    }

    private func select(_ sidebarPage: DictationSettingsPage) {
        searchText = ""
        trail = []
        model.showSettings(sidebarPage)
    }

    private func goBack() {
        guard let previous = trail.popLast() else { return }
        model.showSettings(previous)
    }

    private func openInWindow() {
        let current = page
        model.dismiss()
        companionManager.showDictationWindow(settingsPage: current)
    }
}
