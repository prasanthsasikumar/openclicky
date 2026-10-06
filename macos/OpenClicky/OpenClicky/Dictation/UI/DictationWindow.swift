//
//  DictationWindow.swift
//  OpenClicky
//
//  The main window: a rail on the left (record, history; your space: dictionary, shortcuts,
//  styles; account, incognito and settings at the foot) and a page on the right. One window,
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

enum DictationSettingsPage: String, CaseIterable, Identifiable {
    case general, shortcuts, orb, microphone, permissions, engine, privacy, plan, account, about
    var id: String { rawValue }

    var title: String {
        switch self {
        case .orb: return "the orb"
        case .privacy: return "privacy & data"
        case .plan: return "plan & usage"
        default: return rawValue
        }
    }

    var symbol: String {
        switch self {
        case .general: return "gearshape"
        case .shortcuts: return "keyboard"
        case .orb: return "circle"
        case .microphone: return "mic"
        case .permissions: return "checkmark.shield"
        case .engine: return "waveform"
        case .privacy: return "lock"
        case .plan: return "creditcard"
        case .account: return "person.circle"
        case .about: return "info.circle"
        }
    }

    /// "openclicky" pages, then "you" pages, then about.
    static let appPages: [DictationSettingsPage] = [.general, .shortcuts, .orb, .microphone, .permissions, .engine]
    static let youPages: [DictationSettingsPage] = [.privacy, .plan, .account]
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
            if !model.isRailCollapsed {
                DictationRailView(model: model, companionManager: companionManager)
                    .frame(width: 250)
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
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                OpenClickyWordmark()
                Spacer()
                Button(action: { model.isRailCollapsed.toggle() }) {
                    Image(systemName: "sidebar.left").font(.system(size: 13)).foregroundStyle(Paper.inkSecondary)
                }
                .buttonStyle(.plain).pointerCursor().help("Toggle Sidebar")
            }
            .padding(.top, 44)
            .padding(.horizontal, 22)
            .padding(.bottom, 14)
            Rectangle().fill(Paper.hairline).frame(height: 1).padding(.horizontal, 22)

            VStack(alignment: .leading, spacing: 2) {
                railButton(.record)
                railButton(.history)
                Text("your space")
                    .font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary)
                    .padding(.top, 18).padding(.bottom, 8).padding(.leading, 22)
                    .background(alignment: .bottomLeading) {
                        RoundedRectangle(cornerRadius: 2).fill(Paper.highlighter).frame(width: 34, height: 5).padding(.leading, 22).padding(.bottom, 6)
                    }
                railButton(.dictionary)
                railButton(.shortcuts)
                railButton(.styles)
            }
            .padding(.top, 14)
            .padding(.horizontal, 10)

            Spacer()

            Rectangle().fill(Paper.hairline).frame(height: 1).padding(.horizontal, 22)
            HStack(spacing: 8) {
                Button(action: { model.section = .settings; model.settingsPage = .account }) {
                    HStack(spacing: 8) {
                        ZStack {
                            Circle().fill(Paper.accentSoft)
                            Text(accountInitial).font(Paper.body(12, weight: .semibold)).foregroundStyle(Paper.ink)
                        }
                        .frame(width: 30, height: 30)
                        .overlay(Circle().strokeBorder(Paper.accent.opacity(0.4)))
                        Text(accountName).font(Paper.body(12, weight: .medium)).foregroundStyle(Paper.ink).lineLimit(1)
                    }
                }
                .buttonStyle(.plain).pointerCursor()
                Spacer()
                Button(action: { settings.incognito.toggle() }) {
                    Image(systemName: settings.incognito ? "eye.slash" : "eye")
                        .font(.system(size: 13)).foregroundStyle(settings.incognito ? Paper.accent : Paper.inkSecondary)
                }
                .buttonStyle(.plain).pointerCursor()
                .help(settings.incognito ? "incognito: on — takes paste but are not saved" : "incognito: off")
                Button(action: { model.section = .settings }) {
                    Image(systemName: "gearshape").font(.system(size: 13)).foregroundStyle(model.section == .settings ? Paper.accent : Paper.inkSecondary)
                }
                .buttonStyle(.plain).pointerCursor().help("settings")
            }
            .padding(.horizontal, 22)
            .padding(.vertical, 18)
        }
        .frame(maxHeight: .infinity)
        .background(Paper.rail)
    }

    private var accountName: String {
        authSession.accountEmail.map { String($0.split(separator: "@").first ?? "") } ?? NSFullUserName()
    }

    private var accountInitial: String {
        String(accountName.prefix(1)).lowercased()
    }

    private func railButton(_ section: DictationSection) -> some View {
        let selected = model.section == section
        return Button(action: { model.section = section }) {
            HStack(spacing: 10) {
                Image(systemName: section.symbol).font(.system(size: 12, weight: .medium)).frame(width: 16)
                Text(section.title).font(Paper.body(15, weight: selected ? .semibold : .regular))
                Spacer()
            }
            .foregroundStyle(selected ? Paper.ink : Paper.inkSecondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(selected ? Paper.selection : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
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
                .padding(.top, 44)
                content
            }
            .padding(.horizontal, 44)
            .padding(.bottom, 40)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
