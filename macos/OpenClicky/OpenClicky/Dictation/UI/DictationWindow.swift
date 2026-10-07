//
//  DictationWindow.swift
//  OpenClicky
//
//  The main window: a rail on the left (record, history; how it writes: dictionary, shortcuts,
//  styles; incognito, settings and the account at the foot) and a page on the right. One window,
//  reopened from the menu bar, the orb's menu or the Dock, remembering its frame.
//

import AppKit
import Combine
import SwiftUI

enum DictationSection: String, CaseIterable, Identifiable {
    case record, history, dictionary, shortcuts, styles, settings
    var id: String { rawValue }

    var title: String { rawValue }

    var symbol: String {
        switch self {
        case .record: return "mic"
        case .history: return "clock"
        case .dictionary: return "sparkles"
        case .shortcuts: return "bolt"
        case .styles: return "pencil.line"
        case .settings: return "gearshape"
        }
    }
}

/// Six pages, each answering one question. `voice` was engine + microphone; `privacy` took in
/// permissions; `account` took in plan & usage; `general` took in about.
enum DictationSettingsPage: String, CaseIterable, Identifiable {
    case general, shortcuts, orb, voice, privacy, account
    var id: String { rawValue }

    var title: String {
        switch self {
        case .orb: return "the orb"
        default: return rawValue
        }
    }

    var symbol: String {
        switch self {
        case .general: return "gearshape"
        case .shortcuts: return "keyboard"
        case .orb: return "circle"
        case .voice: return "waveform"
        case .privacy: return "lock"
        case .account: return "person.circle"
        }
    }
}

@MainActor
final class DictationWindowModel: ObservableObject {
    @Published var section: DictationSection = .record
    @Published var settingsPage: DictationSettingsPage = .general
    @Published var pendingHistorySearch: String?
    @Published var isRailCollapsed = false
}

@MainActor
final class DictationWindowController: NSWindowController, NSWindowDelegate {
    let model = DictationWindowModel()
    private let companionManager: CompanionManager
    private var appearanceCancellable: AnyCancellable?

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1180, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered, defer: false)
        window.title = "openclicky"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.minSize = NSSize(width: 900, height: 600)
        window.setFrameAutosaveName("OpenClickyDictationWindow")
        window.isReleasedWhenClosed = false
        window.backgroundColor = NSColor(Paper.background)
        super.init(window: window)
        window.delegate = self
        let hosting = NSHostingView(rootView: DictationRootView(model: model, companionManager: companionManager))
        // The window keeps its own size; the view fills it (SwiftUI would otherwise grow the window to its ideal height).
        hosting.sizingOptions = []
        window.contentView = hosting
        applyAppearance()
        appearanceCancellable = companionManager.dictationSettings.$appearance.sink { [weak self] _ in
            DispatchQueue.main.async { self?.applyAppearance() }
        }
        if window.frame.origin == .zero { window.center() }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    private func applyAppearance() {
        window?.appearance = companionManager.dictationSettings.appearance.nsAppearance
    }

    func show(section: DictationSection? = nil, settingsPage: DictationSettingsPage? = nil) {
        if let section { model.section = section }
        if let settingsPage { model.section = .settings; model.settingsPage = settingsPage }
        // A menu-bar-only app is not active when the window opens from the status item.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        // Back to a menu-bar app: no Dock icon while the window is closed.
        DispatchQueue.main.async {
            if !(NSApp.windows.contains { $0.isVisible && !($0 is NSPanel) }) {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }
}

// MARK: - Root

struct DictationRootView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager

    var body: some View {
        HStack(spacing: 0) {
            if !model.isRailCollapsed && model.section != .settings {
                DictationRailView(model: model, companionManager: companionManager)
                    .frame(width: Paper.Metric.mainRail)
                Rectangle().fill(Paper.hairline).frame(width: 1)
            }
            Group {
                switch model.section {
                case .record: RecordPageView(model: model, companionManager: companionManager)
                case .history: HistoryPageView(model: model, companionManager: companionManager)
                case .dictionary: DictionaryPageView(spaceStore: companionManager.dictationSpaceStore)
                case .shortcuts: ShortcutsPageView(spaceStore: companionManager.dictationSpaceStore)
                case .styles: StylesPageView(companionManager: companionManager)
                case .settings: DictationSettingsView(model: model, companionManager: companionManager)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Paper.background)
        .ignoresSafeArea()
    }
}

struct DictationRailView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var spaceStore: DictationSpaceStore
    @ObservedObject private var authSession = OpenClickyAuthSession.shared
    @State private var historyCount = 0

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.spaceStore = companionManager.dictationSpaceStore
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                OpenClickyWordmark()
                Spacer()
                Button(action: { model.isRailCollapsed.toggle() }) {
                    Image(systemName: "sidebar.left").font(.system(size: 13)).foregroundStyle(Paper.inkSecondary)
                }
                .buttonStyle(.plain).pointerCursor().help("Toggle Sidebar")
            }
            .padding(.top, 44)
            .padding(.horizontal, 10)
            .padding(.bottom, 18)

            railButton(.record) { Keycap(text: settings.dictationKey.keycapLabel) }
            railButton(.history) { railCount(historyCount) }
            Text("how it writes")
                .font(Paper.micro).foregroundStyle(Paper.inkTertiary)
                .padding(.top, 16).padding(.bottom, 4).padding(.leading, 10)
                .accessibilityAddTraits(.isHeader)
            railButton(.dictionary) { railCount(spaceStore.space.dictionary.count) }
            railButton(.shortcuts) { railCount(spaceStore.space.shortcuts.count) }
            railButton(.styles) { railCount(spaceStore.space.styles.count) }

            Spacer()

            Toggle(isOn: $settings.incognito) {
                Text("incognito").font(Paper.body(13)).foregroundStyle(Paper.ink)
            }
            .toggleStyle(RailSwitchStyle())
            .help(settings.incognito ? "incognito is on — takes paste but are not saved" : "incognito is off — takes are saved to history on this mac")

            // ⌘, reaches this button while the window is key: AppKit offers a key equivalent to the
            // key window before the menu bar, whose Settings item would open the app's empty scene.
            Button(action: { model.section = .settings }) {
                HStack {
                    Text("settings").font(Paper.body(13)).foregroundStyle(Paper.ink)
                    Spacer()
                    Text("⌘ ,").font(Paper.micro).foregroundStyle(Paper.inkTertiary)
                }
                .padding(.horizontal, 10)
                .frame(height: Paper.Metric.railItemHeight)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()
            .keyboardShortcut(",", modifiers: .command)

            Button(action: { model.section = .settings; model.settingsPage = .account }) {
                HStack(spacing: 10) {
                    ZStack {
                        Circle().fill(Paper.accentSoft)
                        Text(accountInitial).font(Paper.body(13, weight: .semibold)).foregroundStyle(Paper.ink)
                    }
                    .frame(width: 30, height: 30)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(accountName).font(Paper.rowTitle).foregroundStyle(Paper.ink).lineLimit(1).truncationMode(.tail)
                        Text(accountState).font(Paper.micro).foregroundStyle(Paper.inkTertiary).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                .padding(10)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()
            .help("account")
            .overlay(alignment: .top) { Rectangle().fill(Paper.hairline).frame(height: 1) }
            .padding(.top, 8)
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 12)
        .frame(maxHeight: .infinity)
        .background(Paper.rail)
        .onAppear(perform: reloadHistoryCount)
        .onReceive(companionManager.dictationTakeController.$historyVersion) { _ in reloadHistoryCount() }
    }

    /// "Prasanth S." — the first name and the initial of the last, or the signed-in address's name.
    private var accountName: String {
        if let email = authSession.accountEmail { return String(email.split(separator: "@").first ?? "") }
        let nameParts = NSFullUserName().split(separator: " ")
        guard let firstName = nameParts.first else { return "you" }
        if nameParts.count > 1, let lastInitial = nameParts.last?.first { return "\(firstName) \(lastInitial)." }
        return String(firstName)
    }

    private var accountState: String {
        authSession.accountEmail == nil ? "personal · no account" : "signed in · openclicky account"
    }

    private var accountInitial: String {
        String(accountName.prefix(1)).lowercased()
    }

    private func reloadHistoryCount() {
        historyCount = (try? companionManager.dictationTakeStore?.stats().allTimeTakes) ?? 0
    }

    private func railCount(_ count: Int) -> some View {
        Text("\(count)").font(Paper.caption).foregroundStyle(Paper.inkTertiary).monospacedDigit()
    }

    private func railButton<Trailing: View>(_ section: DictationSection, @ViewBuilder trailing: () -> Trailing) -> some View {
        let selected = model.section == section
        return Button(action: { model.section = section }) {
            HStack(spacing: 8) {
                Text(section.title).font(Paper.body(13, weight: selected ? .semibold : .regular)).foregroundStyle(Paper.ink)
                Spacer()
                trailing()
            }
            .padding(.horizontal, 10)
            .frame(height: Paper.Metric.railItemHeight)
            .background(RoundedRectangle(cornerRadius: Paper.Metric.railItemRadius, style: .continuous).fill(selected ? Paper.selection : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// A labelled rail row with a small switch on the right ("incognito").
private struct RailSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button(action: { configuration.isOn.toggle() }) {
            HStack {
                configuration.label
                Spacer()
                Capsule()
                    .fill(configuration.isOn ? Paper.success : Paper.hairline)
                    .frame(width: 28, height: 16)
                    .overlay(alignment: configuration.isOn ? .trailing : .leading) {
                        Circle().fill(Color.white).frame(width: 12, height: 12).padding(2)
                    }
                    .animation(.easeOut(duration: 0.15), value: configuration.isOn)
            }
            .padding(.horizontal, 10)
            .frame(height: Paper.Metric.railItemHeight)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityValue(configuration.isOn ? "on" : "off")
    }
}

/// A page's scrolling body with the standard margins.
struct PageScaffold<Content: View>: View {
    let title: String
    var trailing: AnyView?
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView(showsIndicators: true) {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .center) {
                    PaperHeading(text: title)
                    Spacer()
                    if let trailing { trailing }
                }
                .padding(.top, Paper.Metric.windowTop)
                content
            }
            .padding(.horizontal, Paper.Metric.windowSides)
            .padding(.bottom, 40)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
