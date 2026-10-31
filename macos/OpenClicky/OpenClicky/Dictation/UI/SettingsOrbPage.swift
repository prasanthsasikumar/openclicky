//
//  SettingsOrbPage.swift
//  OpenClicky
//
//  Settings → The Orb: how the pill narrates a take. Show the orb; its style, theme and size;
//  when it appears. Its sounds and haptics are dictation's, so they live on Dictation.
//

import SwiftUI

extension SettingsCatalog {
    func orbItems() -> [SettingsItem] {
        let dictationKeyName = settings.dictationKey.keycapLabel
        func toggleItem(_ id: String, title: String, detail: String, keywords: [String], keyPath: ReferenceWritableKeyPath<DictationSettings, Bool>) -> SettingsItem {
            SettingsItem(
                id: id, title: title, detail: detail, keywords: keywords,
                view: AnyView(SettingsRow(title: title, detail: detail) { SettingsToggle(settings: settings, keyPath: keyPath) }))
        }
        return [
            toggleItem("orb.visible", title: "Show the orb",
                       detail: "Off: \(dictationKeyName) still works everywhere, just without narration.",
                       keywords: ["orb", "pill", "hide", "visible"], keyPath: \.orbVisible),
            SettingsItem(
                id: "orb.look",
                title: "Style", detail: "Pill, classic or pixel.",
                keywords: ["pill", "classic", "pixel", "style", "shape", "look"],
                view: AnyView(OrbLookPicker(settings: settings))),
            SettingsItem(
                id: "orb.theme",
                title: "Theme", detail: "Black, coral or mist.",
                keywords: ["colour", "color", "black", "coral", "mist"],
                view: AnyView(OrbDependentRow(settings: settings) {
                    SettingsRow(title: "Theme", detail: nil) { OrbThemePicker(settings: settings) }
                })),
            SettingsItem(
                id: "orb.size",
                title: "Size", detail: "Full or mini.",
                keywords: ["full", "mini", "small", "big"],
                view: AnyView(OrbDependentRow(settings: settings) {
                    SettingsRow(title: "Size", detail: nil) { OrbSizePicker(settings: settings) }
                })),
            toggleItem("orb.hidesWhenIdle", title: "Hide when idle",
                       detail: "Appears when a take starts, leaves when it’s done.",
                       keywords: ["hide", "idle", "not in use"], keyPath: \.orbHidesWhenIdle),
            toggleItem("orb.restsExpanded", title: "Rest with the box open",
                       detail: "Keeps the transcript box showing between takes.",
                       keywords: ["box", "transcript", "expanded"], keyPath: \.orbRestsExpanded),
            toggleItem("orb.opensBoxWhenPasteUnverified", title: "Open the box if a paste can’t be confirmed",
                       detail: "So words never vanish when an app ignores the paste.",
                       keywords: ["paste", "visibility", "box", "unverified", "confirm"], keyPath: \.orbOpensBoxWhenPasteUnverified),
            SettingsItem(
                id: "orb.isDraggable",
                title: "Free to drag", detail: "Off pins the orb at the bottom centre.",
                keywords: ["drag", "move", "pin", "position", "center", "centre", "bottom"],
                view: AnyView(OrbDragRow(settings: settings))),
            toggleItem("orb.tooltips", title: "Tooltips",
                       detail: "A short hint under the orb.",
                       keywords: ["hint", "help"], keyPath: \.tooltips),
        ]
    }
}

// MARK: - look, theme, size

/// A row that only means something while the orb is shown: dimmed and inert while it is hidden.
private struct OrbDependentRow<Content: View>: View {
    @ObservedObject var settings: DictationSettings
    @ViewBuilder var content: Content

    var body: some View {
        content
            .disabled(!settings.orbVisible)
            .opacity(settings.orbVisible ? 1 : 0.45)
    }
}

private struct OrbLookPicker: View {
    @ObservedObject var settings: DictationSettings
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(settingsHighlighted("Style", query: searchQuery)).font(SettingsLook.rowTitle)
            }
            Spacer(minLength: 16)
            HStack(spacing: 12) {
                ForEach(OrbLook.allCases) { look in
                    let isSelected = settings.orbLook == look
                    Button(action: { settings.orbLook = look }) {
                        VStack(spacing: 6) {
                            OrbPreview(look: look, theme: settings.orbTheme, size: .mini)
                                .frame(width: 68, height: 46)
                                .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.primary.opacity(0.06)))
                                .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                                    .strokeBorder(isSelected ? SettingsLook.accent : Color(nsColor: .separatorColor), lineWidth: isSelected ? 2 : 0.5))
                            Text(look.rawValue.capitalized)
                                .font(.system(size: 11, weight: isSelected ? .semibold : .regular))
                                .foregroundStyle(isSelected ? .primary : .secondary)
                        }
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(look.rawValue) style")
                    .accessibilityAddTraits(isSelected ? .isSelected : [])
                    .pointerCursor()
                }
            }
        }
        .padding(.vertical, 4)
        .disabled(!settings.orbVisible)
        .opacity(settings.orbVisible ? 1 : 0.45)
    }
}

private struct OrbThemePicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.orbTheme) {
            ForEach(OrbTheme.allCases) { theme in Text(theme.rawValue.capitalized).tag(theme) }
        }
        .labelsHidden().fixedSize()
        .pointerCursor()
    }
}

private struct OrbSizePicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.orbSize) {
            Text("Full").tag(OrbSize.full)
            Text("Mini").tag(OrbSize.mini)
        }
        .pickerStyle(.segmented)
        .labelsHidden().fixedSize()
        .pointerCursor()
    }
}

/// Free to drag, and the way back to the bottom once it was dragged somewhere.
private struct OrbDragRow: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        SettingsRow(title: "Free to drag",
                    detail: settings.orbPosition == nil ? "Off pins the orb at the bottom centre." : "Off pins the orb at the bottom centre. It rests where you dragged it.") {
            HStack(spacing: 10) {
                if settings.orbPosition != nil {
                    Button("Move to Bottom") { settings.orbPosition = nil }
                        .pointerCursor()
                }
                SettingsToggle(settings: settings, keyPath: \.orbIsDraggable)
            }
        }
    }
}

/// A still orb, for the look picker.
struct OrbPreview: View {
    let look: OrbLook
    let theme: OrbTheme
    let size: OrbSize

    static func fill(for theme: OrbTheme) -> Color {
        switch theme {
        case .black: return Color(red: 0.16, green: 0.16, blue: 0.16)
        case .coral: return Color(red: 0.89, green: 0.33, blue: 0.21)
        case .mist: return Color(white: 0.93)
        }
    }

    private var fill: Color { Self.fill(for: theme) }
    private var ink: Color { theme == .mist ? Color(white: 0.2) : Color.white.opacity(0.92) }

    var body: some View {
        let pill = OrbMetrics.pillSize(size)
        Group {
            if look == .classic {
                ZStack {
                    Circle().fill(fill)
                    OrbMarkShape().fill(ink).frame(width: pill.height * 0.6, height: pill.height * 0.6)
                }
                .frame(width: pill.height * 1.4, height: pill.height * 1.4)
            } else if look == .pixel {
                ZStack {
                    Circle().fill(fill)
                    OrbMarkShape().fill(ink).frame(width: pill.height * 0.6, height: pill.height * 0.6)
                        .mask(OrbPixelGrid().fill(.black))
                }
                .frame(width: pill.height * 1.4, height: pill.height * 1.4)
            } else {
                ZStack {
                    Capsule().fill(fill)
                    HStack(spacing: 6) {
                        RoundedRectangle(cornerRadius: 2).fill(ink).frame(width: 9, height: 2.5)
                        RoundedRectangle(cornerRadius: 2).fill(ink).frame(width: 9, height: 2.5)
                    }
                }
                .frame(width: pill.width, height: pill.height)
            }
        }
        .shadow(color: .black.opacity(0.2), radius: 6, y: 2)
    }
}
