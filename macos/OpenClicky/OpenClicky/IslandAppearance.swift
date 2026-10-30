//
//  IslandAppearance.swift
//  OpenClicky
//
//  The island's style (Settings → Appearance): blend, black melting into glass; liquid glass, the
//  whole open island glass under a light tint; or solid black like the notch. The closed island is
//  always the notch's own black, whatever the style.
//

import AppKit
import SwiftUI

enum IslandLook: String, CaseIterable, Identifiable {
    case blend
    case glass
    case black

    static let defaultsKey = "openclicky.island.style"
    static let defaultLook: IslandLook = .glass
    /// The old glass slider (0 all black, above 0 a melt), read once for someone who set it by hand.
    static let legacyGlassLevelKey = "openclicky.island.glassLevel"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .blend: return "Blend"
        case .glass: return "Liquid Glass"
        case .black: return "Black"
        }
    }

    /// The line under the picker for the chosen style.
    var footnote: String {
        switch self {
        case .blend: return "Black at the top, fading into frosted glass towards the bottom."
        case .glass: return "Clear glass all over, with a light tint so the text stays readable."
        case .black: return "Solid black all over, just like the notch."
        }
    }

    /// True when the open island shows glass at all.
    var hasGlass: Bool { self != .black }

    /// The stored style; else the old glass slider's meaning (a melt when it was above 0, black at
    /// 0); else the default.
    static func current(in defaults: UserDefaults = .standard) -> IslandLook {
        if let raw = defaults.string(forKey: defaultsKey), let look = IslandLook(rawValue: raw) { return look }
        if let level = defaults.object(forKey: legacyGlassLevelKey) as? Double { return level > 0 ? .blend : .black }
        return defaultLook
    }

    /// Blend's black: solid down to a third, then thinning into the glass.
    static let blendStops: [Gradient.Stop] = [
        .init(color: .black, location: 0),
        .init(color: .black, location: 0.35),
        .init(color: .black.opacity(0.55), location: 0.7),
        .init(color: .black.opacity(0.15), location: 1),
    ]
    /// Liquid glass's flat tint, and the short shade under the menu bar's edge.
    static let glassTint: Double = 0.22
    static let glassTopShadeHeight: CGFloat = 8

    /// The open island's drop shadow: lighter on glass, which casts less.
    func shadowOpacity(isOpen: Bool) -> Double {
        guard isOpen else { return 0 }
        return self == .glass ? 0.25 : 0.45
    }
}

/// Which way the island grows when hovered (and while it is busy): out to the sides inside the
/// menu bar, so nothing below is covered, or a line down under the notch.
enum IslandPeekDirection: String, CaseIterable, Identifiable {
    case sideways
    case down

    static let defaultsKey = "openclicky.island.peekDirection"
    var id: String { rawValue }

    var title: String {
        switch self {
        case .sideways: return "Sideways"
        case .down: return "Downwards"
        }
    }

    static func current(in defaults: UserDefaults = .standard) -> IslandPeekDirection {
        defaults.string(forKey: defaultsKey).flatMap(IslandPeekDirection.init(rawValue:)) ?? .sideways
    }
}

extension SettingsCatalog {
    func islandAppearanceItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "appearance.peekDirection", page: .appearance, section: "",
                title: "When you hover the notch",
                detail: "Sideways grows inside the menu bar, so tabs and toolbars stay uncovered; downwards shows a line under the notch. Clicking still opens the island downwards.",
                keywords: ["hover", "peek", "sideways", "horizontal", "down", "downwards", "expand", "grow", "tabs", "notch"],
                view: AnyView(IslandPeekDirectionRow())),
            SettingsItem(
                id: "appearance.style", page: .appearance, section: "",
                title: "Style", detail: "",
                keywords: ["island", "style", "glass", "liquid glass", "blend", "black", "look", "notch", "transparent"],
                view: AnyView(IslandLookRow())),
        ]
    }
}

/// The style as a segmented control, the chosen style's line under it.
private struct IslandLookRow: View {
    @State private var look = IslandLook.current()

    var body: some View {
        VStack(alignment: .trailing, spacing: 8) {
            HStack {
                Text("Style").font(SettingsLook.rowTitle)
                Spacer(minLength: 16)
                Picker("", selection: Binding(get: { look }, set: { newValue in
                    look = newValue
                    UserDefaults.standard.set(newValue.rawValue, forKey: IslandLook.defaultsKey)
                })) {
                    ForEach(IslandLook.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .pointerCursor()
            }
            Text(look.footnote).font(SettingsLook.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, SettingsLook.Metric.rowVertical)
    }
}

private struct IslandPeekDirectionRow: View {
    @State private var direction = IslandPeekDirection.current()

    var body: some View {
        SettingsRow(title: "When you hover the notch", detail: "Sideways grows inside the menu bar, so tabs and toolbars stay uncovered; downwards shows a line under the notch. Clicking still opens the island downwards.") {
            Picker("", selection: Binding(get: { direction }, set: { newValue in
                direction = newValue
                UserDefaults.standard.set(newValue.rawValue, forKey: IslandPeekDirection.defaultsKey)
            })) {
                ForEach(IslandPeekDirection.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .pointerCursor()
        }
    }
}
