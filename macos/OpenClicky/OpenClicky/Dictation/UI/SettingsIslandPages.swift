//
//  SettingsIslandPages.swift
//  OpenClicky
//
//  The island's rows on Settings → Island: each plugin's switch, and how the island picks
//  between the components it has: what it shows first and the capsule's order. Each plugin
//  folder adds its own rows; `SettingsLayout` places them all.
//

import AppKit
import Combine
import SwiftUI

extension SettingsCatalog {
    /// One plugin's switch row; `SettingsLayout` puts it on Island (or Teacher on Clicky).
    func pluginItem(_ id: String, keywords: [String] = []) -> SettingsItem? {
        guard let plugin = IslandPluginRegistry.all.first(where: { $0.id == id }) else { return nil }
        return SettingsItem(
            id: "plugins.\(plugin.id)",
            title: plugin.title, detail: plugin.settingsDetail ?? "",
            keywords: ["plugin", "island", plugin.title.lowercased(), plugin.id] + keywords,
            view: AnyView(IslandPluginRow(plugin: plugin)))
    }

    /// The plugins without a folder of settings of their own, and how the island picks between
    /// its components: the default and the capsule's order. Clipboard, Notes, Timer, Weather and
    /// Calendar add their own rows from their folders.
    func islandPluginItems() -> [SettingsItem] {
        [
            pluginItem(IslandPluginRegistry.mediaID, keywords: ["music", "spotify", "now playing", "song"]),
            pluginItem(IslandPluginRegistry.systemID, keywords: ["cpu", "memory", "widget"]),
            pluginItem(IslandPluginRegistry.shelfID, keywords: ["file tray", "files", "drop"]),
            pluginItem(IslandPluginRegistry.agentsID),
            pluginItem(IslandPluginRegistry.teacherID),
        ].compactMap { $0 } + [
            SettingsItem(
                id: "plugins.islandDefault",
                title: "Shows first", detail: "What the open island shows when nothing else is going on.",
                keywords: ["island", "component", "panel", "shortcuts", "transcript", "agent", "default", "slot", "dashboard"],
                view: AnyView(IslandDefaultComponentRow())),
            SettingsItem(
                id: "plugins.islandOrder",
                title: "Component order", detail: "The order of the island's capsule when there is more than one to flip to.",
                keywords: ["island", "component", "order", "capsule", "arrange", "reorder"],
                view: AnyView(IslandComponentOrderRow())),
        ]
    }
}

// MARK: - one plugin

/// A plugin's switch, with its name and what it does on the left like every other row.
struct IslandPluginRow: View {
    let plugin: any IslandComponent
    @State private var isOn = false

    /// Each plugin's tile colour on its settings row.
    static func tint(for id: String) -> Color {
        switch id {
        case IslandPluginRegistry.mediaID: return Color(red: 0.98, green: 0.18, blue: 0.33)
        case IslandPluginRegistry.systemID, IslandPluginRegistry.shelfID: return Color(red: 0.0, green: 0.478, blue: 1.0)
        case "notes": return Color(red: 1.0, green: 0.8, blue: 0.0)
        case "timer": return Color(red: 1.0, green: 0.584, blue: 0.0)
        case IslandPluginRegistry.teacherID: return Color(red: 0.204, green: 0.78, blue: 0.349)
        case IslandPluginRegistry.agentsID: return Color(red: 0.11, green: 0.11, blue: 0.118)
        default: return Color(red: 0.557, green: 0.557, blue: 0.576)
        }
    }

    var body: some View {
        SettingsRow(title: plugin.title, detail: plugin.settingsDetail ?? "", icon: SettingsFeatureTile(symbol: plugin.symbolName, tint: Self.tint(for: plugin.id))) {
            SettingsSwitch(isOn: Binding(
                get: { isOn },
                set: { newValue in
                    isOn = newValue
                    var settings = IslandComponentSettings(defaults: .standard)
                    var enabled = settings.enabledPlugins.filter { $0 != plugin.id }
                    if newValue { enabled.append(plugin.id) }
                    settings.enabledPlugins = enabled
                }
            ))
            .accessibilityLabel(plugin.title)
        }
        .onAppear { isOn = IslandComponentSettings(defaults: .standard).enabledPlugins.contains(plugin.id) }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: DispatchQueue.main)) { _ in
            let next = IslandComponentSettings(defaults: .standard).enabledPlugins.contains(plugin.id)
            if next != isOn { isOn = next }
        }
    }
}

// MARK: - the island's pick

struct IslandDefaultComponentRow: View {
    @State private var defaultID = IslandComponentSettings(defaults: .standard).resolvedDefaultID(in: IslandComponentRegistry.all)
    @State private var choices = IslandDefaultComponentRow.choices()

    /// The built-ins and the extras that are on.
    @MainActor private static func choices() -> [any IslandComponent] {
        let settings = IslandComponentSettings(defaults: .standard)
        return IslandComponentRegistry.all.filter { settings.isEnabled($0.id, in: IslandComponentRegistry.all) }
    }

    var body: some View {
        SettingsRow(title: "Shows first", detail: "What the open island shows when nothing else is going on.") {
            Picker("", selection: Binding(get: { defaultID }, set: { newValue in
                defaultID = newValue
                var settings = IslandComponentSettings(defaults: .standard)
                settings.defaultID = newValue
            })) {
                ForEach(choices, id: \.id) { component in Text(component.title).tag(component.id) }
            }
            .labelsHidden()
            .fixedSize()
            .pointerCursor()
        }
        // A switch flipped in the row below changes what can be the default: follow it.
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: DispatchQueue.main)) { _ in
            let resolved = IslandComponentSettings(defaults: .standard).resolvedDefaultID(in: IslandComponentRegistry.all)
            if resolved != defaultID { defaultID = resolved }
            let next = Self.choices()
            if next.map(\.id) != choices.map(\.id) { choices = next }
        }
    }
}

/// The capsule's order: one row per component with up and down arrows.

struct IslandComponentOrderRow: View {
    @State private var order: [String] = IslandComponentOrderRow.resolvedOrder()

    /// The stored order, with unknown ids dropped and missing ones appended in registration order.
    @MainActor private static func resolvedOrder() -> [String] {
        let known = IslandComponentRegistry.all.map(\.id)
        let stored = IslandComponentSettings(defaults: .standard).order.filter { known.contains($0) }
        return stored + known.filter { !stored.contains($0) }
    }

    @State private var enabledPlugins = IslandComponentSettings(defaults: .standard).enabledPlugins

    var body: some View {
        SettingsRow(title: "Component order", detail: "The order of the island's capsule when there is more than one to flip to.") {
            VStack(alignment: .trailing, spacing: 4) {
                ForEach(Array(order.enumerated()), id: \.element) { index, id in
                    HStack(spacing: 6) {
                        Text(IslandComponentRegistry.all.first { $0.id == id }?.title ?? id)
                            .font(SettingsLook.caption)
                            .opacity(IslandComponentRegistry.all.first { $0.id == id }?.isPlugin == true && !enabledPlugins.contains(id) ? 0.5 : 1)
                        Button(action: { move(index, by: -1) }) { Image(systemName: "chevron.up") }
                            .disabled(index == 0)
                        Button(action: { move(index, by: 1) }) { Image(systemName: "chevron.down") }
                            .disabled(index == order.count - 1)
                    }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: DispatchQueue.main)) { _ in
            let next = IslandComponentSettings(defaults: .standard).enabledPlugins
            if next != enabledPlugins { enabledPlugins = next }
        }
    }

    private func move(_ index: Int, by offset: Int) {
        let target = index + offset
        guard order.indices.contains(target) else { return }
        order.swapAt(index, target)
        var settings = IslandComponentSettings(defaults: .standard)
        settings.order = order
    }
}
