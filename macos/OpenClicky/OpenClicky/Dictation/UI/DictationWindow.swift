//
//  DictationWindow.swift
//  OpenClicky
//
//  OpenClicky's one window. A native sidebar lists the pages, with no heading, in this order: General,
//  Dictation, Clicky, Voice, Your words (what you dictated: try it, this week, every take), How it
//  writes (dictionary, snippets, styles), The Orb, Shortcuts, Keys & Plans, Account, Privacy &
//  Security, About. Every
//  page is drawn the same native way, a search field filters every setting, and incognito and the
//  account sit at the sidebar's foot. Every way in (⌘,, the menu
//  bar, the island, the orb's menu, the Dock) opens this window at the page it asks for. It is
//  reopened rather than recreated, and remembers its frame.
//

import AppKit
import Combine
import SwiftUI

/// The window's own pages, listed first under Settings and drawn like the settings pages.
enum DictationSection: String, CaseIterable, Identifiable {
    /// What you dictated: try it here, this week, and every take (record and history, merged).
    case words
    /// Dictionary, snippets and styles on one page.
    case writing
    var id: String { rawValue }

    var sidebarTitle: String {
        switch self {
        case .words: return "Your words"
        case .writing: return "How it writes"
        }
    }

    var symbol: String {
        switch self {
        case .words: return "text.quote"
        case .writing: return "character.book.closed.fill"
        }
    }

    var tint: Color {
        switch self {
        case .words: return Color(red: 0.824, green: 0.306, blue: 0.165)
        case .writing: return Color(red: 0.686, green: 0.322, blue: 0.871)
        }
    }
}

/// What the window shows: one of your pages or one settings page.
enum DictationDestination: Hashable {
    case section(DictationSection)
    case settings(DictationSettingsPage)
}

@MainActor
final class DictationWindowModel: ObservableObject {
    @Published var destination: DictationDestination = .section(.words)
    @Published var pendingHistorySearch: String?
    /// While this holds words the window shows matching settings from every page.
    @Published var searchText = ""

    var trimmedSearchText: String { searchText.trimmingCharacters(in: .whitespacesAndNewlines) }
    var isSearching: Bool { !trimmedSearchText.isEmpty }

    /// A click in the sidebar. Clears any search so the chosen page is what shows.
    func select(_ chosenDestination: DictationDestination) {
        searchText = ""
        destination = chosenDestination
    }

    func select(_ section: DictationSection) { select(.section(section)) }

    /// Every way into the window comes through here. A settings page wins over a section; with
    /// neither, the window just comes forward as it was (a leftover search included).
    func open(section requestedSection: DictationSection? = nil, settingsPage requestedPage: DictationSettingsPage? = nil) {
        if let requestedPage {
            select(.settings(requestedPage))
        } else if let requestedSection {
            select(.section(requestedSection))
        }
    }
}

@MainActor
final class DictationWindowController: NSWindowController, NSWindowDelegate {
    let model = DictationWindowModel()
    private let companionManager: CompanionManager
    private var appearanceSubscription: AnyCancellable?

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: Self.startingSize),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false)
        window.title = "OpenClicky"
        window.setFrameAutosaveName("OpenClickyWindow")
        window.isReleasedWhenClosed = false
        // A toolbar lets the split view put the page title and the sidebar button in the title bar.
        window.toolbar = NSToolbar(identifier: "OpenClickyMainToolbar")
        window.toolbarStyle = .unified
        super.init(window: window)
        window.delegate = self
        let hostingController = NSHostingController(rootView: DictationRootView(model: model, companionManager: companionManager))
        // The window keeps its own size; the view fills it (SwiftUI would otherwise size the window to fit).
        hostingController.sizingOptions = []
        window.contentViewController = hostingController
        // Set after the content view controller, which replaces the window's minimum with its own.
        window.contentMinSize = Self.minimumSize
        window.setContentSize(Self.startingSize)
        window.setFrameUsingName("OpenClickyWindow")
        appearanceSubscription = window.followAppearanceSetting(of: companionManager.dictationSettings)
        if window.frame.origin == .zero { window.center() }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    /// The sidebar (240 pt) plus 540 pt for a page. Every page is a grouped form that reflows, so
    /// the widest fixed row decides: Keys & Plans' "Add a key" (provider menu, 130 pt key field, Add)
    /// still leaves its title a readable column at 540 pt and squeezes it to one letter a line by
    /// 480 pt. Narrower windows also let the split view push the sidebar off its edge.
    static let minimumSize = NSSize(width: 780, height: 560)

    /// Tall enough that the whole sidebar shows without scrolling, and no wider than a settings
    /// page needs; shorter on a screen that has less room.
    static var startingSize: NSSize {
        let room = NSScreen.main?.visibleFrame.size ?? NSSize(width: 1440, height: 900)
        return NSSize(width: min(840, room.width - 40), height: min(880, room.height - 40))
    }

    func show(section: DictationSection? = nil, settingsPage: DictationSettingsPage? = nil) {
        model.open(section: section, settingsPage: settingsPage)
        // A menu-bar-only app is not active when the window opens from the status item.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        // No field starts with the keyboard: a key field taking it would scroll a settings page.
        window?.makeFirstResponder(nil)
    }

    func windowWillClose(_ notification: Notification) {
        companionManager.stopSettingsTour()
        // Back to a menu-bar app: no Dock icon while the window is closed.
        DispatchQueue.main.async {
            if !(NSApp.windows.contains { $0.isVisible && !($0 is NSPanel) }) {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }
}

extension NSWindow {
    /// Keeps this window light, dark or matching the system as the "appearance" setting says: set
    /// now, and again on every change. Every window of the app goes through here (the onboarding
    /// window once forgot to and stayed in the system look). Keep the returned subscription.
    func followAppearanceSetting(of settings: DictationSettings) -> AnyCancellable {
        appearance = settings.appearance.nsAppearance
        // `$appearance` fires before the property changes, so the new value is read from the
        // publisher; the change is applied on the next turn, outside the SwiftUI update that made it.
        return settings.$appearance
            .dropFirst()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] newAppearance in self?.appearance = newAppearance.nsAppearance }
    }
}

// MARK: - Root

struct DictationRootView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var spaceStore: DictationSpaceStore
    // Observed so rows whose relevance depends on them (the fn banner, the sign-in rows) refresh.
    @ObservedObject private var fnKeyState = FnKeyState.shared
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @State private var historyCount = 0

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.spaceStore = companionManager.dictationSpaceStore
    }

    private var catalog: SettingsCatalog { SettingsCatalog(settings: settings, companionManager: companionManager) }

    var body: some View {
        let relevantItems = catalog.allItems.filter { $0.isRelevant() }
        let query = model.trimmedSearchText
        let matches = query.isEmpty ? [] : relevantItems.filter { $0.matches(query) }
        NavigationSplitView {
            VStack(spacing: 0) {
            List(selection: Binding(
                get: { model.isSearching ? nil : model.destination },
                set: { chosen in if let chosen { model.select(chosen) } }
            )) {
                // General, Dictation, Clicky, Voice, then your two pages, then the rest.
                ForEach(DictationSettingsPage.pagesBeforeYourPages) { page in pageRow(page, matches: matches) }
                sectionRow(.words, count: historyCount, matches: matches)
                sectionRow(.writing, matches: matches)
                ForEach(DictationSettingsPage.pagesAfterYourPages) { page in pageRow(page, matches: matches) }
            }
            .listStyle(.sidebar)
            .searchable(text: $model.searchText, placement: .sidebar, prompt: "Search settings")
            SidebarFooter(settings: settings, companionManager: companionManager, model: model)
            }
            .navigationSplitViewColumnWidth(240)
        } detail: {
            detail(query: query, matches: matches, relevantItems: relevantItems)
        }
        .frame(minWidth: DictationWindowController.minimumSize.width, minHeight: DictationWindowController.minimumSize.height)
        .onAppear {
            fnKeyState.refresh()
            reloadHistoryCount()
        }
        .onReceive(companionManager.dictationTakeController.$historyVersion) { _ in reloadHistoryCount() }
        // ⌘, opens General. It sits on the root so it works wherever the focus is; AppKit offers a
        // key equivalent to the key window before the menu bar, whose Settings item would open the
        // app's empty settings scene.
        .background(
            Button("Settings") { model.select(.settings(.general)) }
                .keyboardShortcut(",", modifiers: .command)
                .hidden()
                .accessibilityHidden(true)
        )
    }

    @ViewBuilder
    private func detail(query: String, matches: [SettingsItem], relevantItems: [SettingsItem]) -> some View {
        if !query.isEmpty {
            Group {
                if matches.isEmpty {
                    Text("No settings match “\(query)”.")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    SettingsFormView(groups: SettingsSectionGroup.searchResultGroups(matches, query: query))
                        .environment(\.settingsSearchQuery, query)
                }
            }
            .navigationTitle("Search Results")
        } else {
            switch model.destination {
            case .settings(let page):
                SettingsFormView(groups: SettingsSectionGroup.groups(relevantItems.filter { $0.page == page && $0.searchOnlyGroup == nil }), page: page)
                    .id(page)
                    .navigationTitle(page.title)
            case .section(let section):
                sectionPage(section)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .navigationTitle(section.sidebarTitle)
            }
        }
    }

    @ViewBuilder
    private func sectionPage(_ section: DictationSection) -> some View {
        switch section {
        case .words: WordsPageView(model: model, companionManager: companionManager)
        case .writing: WritingPageView(companionManager: companionManager)
        }
    }

    private func pageRow(_ page: DictationSettingsPage, matches: [SettingsItem]) -> some View {
        SidebarIconLabel(title: page.title, symbol: page.symbol, tint: page.tint)
            .settingsTourTarget(SettingsTourTargets.sidebarTargetID(for: .settings(page)))
            .opacity(model.isSearching && !matches.contains { $0.page == page && $0.searchOnlyGroup == nil } ? 0.35 : 1)
            .tag(DictationDestination.settings(page))
    }

    private func sectionRow(_ section: DictationSection, count: Int? = nil, matches: [SettingsItem]) -> some View {
        SidebarIconLabel(title: section.sidebarTitle, symbol: section.symbol, tint: section.tint)
            .settingsTourTarget(SettingsTourTargets.sidebarTargetID(for: .section(section)))
            .badge(count ?? 0)
            .opacity(model.isSearching && !matches.contains { $0.searchOnlyGroup == section.sidebarTitle } ? 0.35 : 1)
            .tag(DictationDestination.section(section))
    }

    private func reloadHistoryCount() {
        historyCount = (try? companionManager.dictationTakeStore?.stats().allTimeTakes) ?? 0
    }
}

/// A sidebar row like System Settings': a white icon in a coloured rounded square, and the name.
struct SidebarIconLabel: View {
    let title: String
    let symbol: String
    let tint: Color

    var body: some View {
        Label {
            Text(title)
        } icon: {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 20, height: 20)
                .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(tint.gradient))
        }
    }
}

/// The sidebar's foot: incognito, and who is signed in (a click opens Account).
private struct SidebarFooter: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager
    @ObservedObject var model: DictationWindowModel
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            HStack {
                Text("Incognito").font(.system(size: 13))
                Spacer()
                SettingsSwitch(isOn: $settings.incognito)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .help(settings.incognito ? "Incognito is on: takes paste but are not saved." : "Incognito is off: takes are saved to history on this Mac.")
            Divider()
            Button(action: { model.select(.settings(.account)) }) {
                HStack(spacing: 10) {
                    ZStack {
                        Circle().fill(authSession.accountEmail == nil ? Color(nsColor: .tertiaryLabelColor) : SettingsLook.accent)
                        Text(accountInitial).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white)
                    }
                    .frame(width: 28, height: 28)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(accountName).font(.system(size: 13, weight: .medium)).lineLimit(1).truncationMode(.tail)
                        Text(authSession.accountEmail != nil ? "Signed in" : (authSession.isGuestWithoutEmail ? "Trying it free" : "Personal · no account"))
                            .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Account")
            .pointerCursor()
        }
    }

    /// "Prasanth S." — the first name and the initial of the last, or the signed-in address's name.
    private var accountName: String {
        if let email = authSession.accountEmail { return String(email.split(separator: "@").first ?? "") }
        let nameParts = NSFullUserName().split(separator: " ")
        guard let firstName = nameParts.first else { return "You" }
        if nameParts.count > 1, let lastInitial = nameParts.last?.first { return "\(firstName) \(lastInitial)." }
        return String(firstName)
    }

    private var accountInitial: String { String(accountName.prefix(1)).uppercased() }
}
