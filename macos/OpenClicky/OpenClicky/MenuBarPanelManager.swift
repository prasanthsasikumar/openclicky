//
//  MenuBarPanelManager.swift
//  OpenClicky
//
//  Manages the NSStatusItem (menu bar icon) and its menu: what OpenClicky is doing, agent mode,
//  the window, the welcome tour, and quit. A plain menu with checkmarks, like
//  any Mac menu bar app; there is no custom panel any more.
//

import AppKit
import Combine
import SwiftUI

@MainActor
final class MenuBarPanelManager: NSObject {
    private var statusItem: NSStatusItem?
    private var iconVisibilityCancellable: AnyCancellable?

    private let companionManager: CompanionManager

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        super.init()
        if companionManager.isMenuBarIconVisible { createStatusItem() }
        iconVisibilityCancellable = companionManager.$isMenuBarIconVisible
            .dropFirst()
            .removeDuplicates()
            .sink { [weak self] visible in
                if visible { self?.createStatusItem() } else { self?.removeStatusItem() }
            }

    }

    // MARK: - Status Item

    private func removeStatusItem() {
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
        statusItem = nil
    }

    private func createStatusItem() {
        guard statusItem == nil else { return }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)

        guard let button = statusItem?.button else { return }

        button.image = makeClickyMenuBarIcon()
        button.image?.isTemplate = true
        button.action = #selector(statusItemClicked)
        button.target = self
        // Either click opens the menu.
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }

    /// The status item's menu, for either click: what OpenClicky is doing, agent mode, the
    /// windows, and quit. A plain menu with checkmarks, like any Mac menu bar app.
    private func showStatusMenu() {
        guard let statusItem else { return }
        let menu = NSMenu()
        let header = NSMenuItem()
        let headerView = NSHostingView(rootView: StatusMenuHeader(statusText: statusText, dictationKey: companionManager.dictationSettings.dictationKey.keycapLabel))
        headerView.frame = NSRect(x: 0, y: 0, width: 280, height: 46)
        header.view = headerView
        menu.addItem(header)
        menu.addItem(.separator())
        addItem(to: menu, title: "Agent Mode", action: #selector(toggleAgentMode), isOn: companionManager.isAgentModeEnabled)
        menu.addItem(.separator())
        addItem(to: menu, title: "Open OpenClicky", action: #selector(openWindow))
        addItem(to: menu, title: "Open the Island", action: #selector(openIsland))
        addItem(to: menu, title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        addItem(to: menu, title: "Replay Welcome…", action: #selector(replayWelcome))
        menu.addItem(.separator())
        addItem(to: menu, title: "Quit OpenClicky", action: #selector(quit), keyEquivalent: "q")
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        // The menu is attached only for the click, or AppKit would keep it attached for good.
        DispatchQueue.main.async { statusItem.menu = nil }
    }

    private func addItem(to menu: NSMenu, title: String, action: Selector, keyEquivalent: String = "", isOn: Bool? = nil) {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
        item.target = self
        if let isOn { item.state = isOn ? .on : .off }
        menu.addItem(item)
    }

    /// "Ready", or what OpenClicky is busy with.
    private var statusText: String {
        switch companionManager.voiceState {
        case .idle: return companionManager.agentActivityText == nil ? "Ready" : "Agent working"
        case .listening: return "Listening"
        case .processing: return "Thinking"
        case .responding: return "Speaking"
        }
    }

    @objc private func toggleAgentMode() { companionManager.setAgentModeEnabled(!companionManager.isAgentModeEnabled) }
    @objc private func openWindow() { companionManager.showDictationWindow(section: .words) }
    @objc private func openIsland() { companionManager.notchHUDManager.openPanel() }
    @objc private func openSettings() { companionManager.showSettings() }
    @objc private func replayWelcome() { companionManager.firstRunTour.replay() }
    @objc private func quit() { companionManager.quitWithFarewell() }

    /// The mark, as a template image: the ring and its pointer, matching the app icon.
    private func makeClickyMenuBarIcon() -> NSImage {
        let iconSize: CGFloat = 18
        let image = NSImage(size: NSSize(width: iconSize, height: iconSize), flipped: true) { rect in
            let path = OpenClickyMark.bezierPath(in: rect.insetBy(dx: 1.5, dy: 1.5))
            NSColor.black.setFill()
            path.fill()
            return true
        }
        image.isTemplate = true
        return image
    }

    @objc private func statusItemClicked() {
        showStatusMenu()
    }
}

/// The menu's first row: a status dot, the name, what it is doing, and the two keys to remember.
private struct StatusMenuHeader: View {
    let statusText: String
    let dictationKey: String

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 8) {
                Circle().fill(statusText == "Ready" ? Color(nsColor: .systemGreen) : Color(nsColor: .systemOrange)).frame(width: 8, height: 8)
                Text("OpenClicky").font(.system(size: 13, weight: .bold))
                Spacer()
                Text(statusText).font(.system(size: 13)).foregroundStyle(.secondary)
            }
            Text("Hold ⌃⌥ to talk · hold \(dictationKey) to dictate").font(.system(size: 12)).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 6)
        .frame(width: 280, alignment: .leading)
    }
}
