//
//  SettingsOrbPage.swift
//  OpenClicky
//
//  Settings → the orb: how the pill narrates a take. Show the orb; its look, theme and size;
//  when it appears; feedback. The live preview draws the real orb view (`OrbRootView`) with a
//  model of its own, so every change shows the way the orb will look on screen.
//

import SwiftUI

extension SettingsCatalog {
    func orbItems() -> [SettingsItem] {
        let dictationKeyName = settings.dictationKey.keycapLabel
        func toggleItem(_ id: String, section: String, title: String, detail: String, keywords: [String], keyPath: ReferenceWritableKeyPath<DictationSettings, Bool>) -> SettingsItem {
            SettingsItem(
                id: id, page: .orb, section: section, title: title, detail: detail, keywords: keywords,
                view: AnyView(SettingsRow(title: title, detail: detail) { SettingsToggle(settings: settings, keyPath: keyPath) }))
        }
        return [
            toggleItem("orb.visible", section: "", title: "show the orb",
                       detail: "off: \(dictationKeyName) still works everywhere, just without narration.",
                       keywords: ["orb", "pill", "hide", "visible"], keyPath: \.orbVisible),
            SettingsItem(
                id: "orb.look", page: .orb, section: "look",
                title: "look", detail: "pill, classic or pixel.",
                keywords: ["pill", "classic", "pixel", "style", "shape"],
                view: AnyView(OrbLookPicker(settings: settings))),
            SettingsItem(
                id: "orb.theme", page: .orb, section: "look",
                title: "theme", detail: "black, coral or mist.",
                keywords: ["colour", "color", "black", "coral", "mist"],
                view: AnyView(SettingsRow(title: "theme", detail: nil) { OrbThemePicker(settings: settings) })),
            SettingsItem(
                id: "orb.size", page: .orb, section: "look",
                title: "size", detail: "full or mini.",
                keywords: ["full", "mini", "small", "big"],
                view: AnyView(SettingsRow(title: "size", detail: nil) { OrbSizePicker(settings: settings) })),
            toggleItem("orb.hidesWhenIdle", section: "when it appears", title: "hide when idle",
                       detail: "appears when a take starts, leaves when it’s done.",
                       keywords: ["hide", "idle", "not in use"], keyPath: \.orbHidesWhenIdle),
            toggleItem("orb.restsExpanded", section: "when it appears", title: "rest with the box open",
                       detail: "keeps the transcript box showing between takes.",
                       keywords: ["box", "transcript", "expanded"], keyPath: \.orbRestsExpanded),
            toggleItem("orb.opensBoxWhenPasteUnverified", section: "when it appears", title: "open the box if a paste can’t be confirmed",
                       detail: "so words never vanish when an app ignores the paste.",
                       keywords: ["paste", "visibility", "box", "unverified", "confirm"], keyPath: \.orbOpensBoxWhenPasteUnverified),
            toggleItem("orb.isDraggable", section: "when it appears", title: "free to drag",
                       detail: "off pins the orb at the bottom centre.",
                       keywords: ["drag", "move", "pin", "position", "center"], keyPath: \.orbIsDraggable),
            toggleItem("orb.tooltips", section: "feedback", title: "tooltips",
                       detail: "a short hint under the orb.",
                       keywords: ["hint", "help"], keyPath: \.tooltips),
            toggleItem("orb.sounds", section: "feedback", title: "sounds",
                       detail: "tones when a take starts, stops, or finishes.",
                       keywords: ["sound", "tone", "earcon", "audio", "beep"], keyPath: \.sounds),
            toggleItem("orb.haptics", section: "feedback", title: "haptics",
                       detail: "a trackpad tap on start, stop, and result.",
                       keywords: ["trackpad", "vibration", "tap"], keyPath: \.haptics),
        ]
    }
}

// MARK: - look, theme, size

private struct OrbLookPicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        HStack(spacing: 12) {
            ForEach(OrbLook.allCases) { look in
                Button(action: { settings.orbLook = look }) {
                    VStack(spacing: 8) {
                        OrbPreview(look: look, theme: settings.orbTheme, size: .full)
                            .frame(height: 44)
                        Text(look.rawValue).font(Paper.body(12, weight: settings.orbLook == look ? .semibold : .regular)).foregroundStyle(Paper.ink)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 14)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Paper.cardRaised))
                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(settings.orbLook == look ? Paper.accentFill : Paper.hairline, lineWidth: settings.orbLook == look ? 2 : 1))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain).pointerCursor()
                .accessibilityLabel("\(look.rawValue) look")
                .accessibilityAddTraits(settings.orbLook == look ? .isSelected : [])
            }
        }
        .padding(Paper.Metric.rowHorizontal)
    }
}

private struct OrbThemePicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        HStack(spacing: 6) {
            ForEach(OrbTheme.allCases) { theme in
                Button(action: { settings.orbTheme = theme }) {
                    HStack(spacing: 6) {
                        Circle().fill(OrbPreview.fill(for: theme)).frame(width: 10, height: 10)
                            .overlay(Circle().strokeBorder(Paper.hairline))
                        Text(theme.rawValue).font(Paper.body(12, weight: settings.orbTheme == theme ? .semibold : .regular)).foregroundStyle(Paper.ink)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(RoundedRectangle(cornerRadius: Paper.Metric.controlRadius, style: .continuous).fill(settings.orbTheme == theme ? Paper.selection : Paper.cardRaised))
                    .overlay(RoundedRectangle(cornerRadius: Paper.Metric.controlRadius, style: .continuous).strokeBorder(settings.orbTheme == theme ? Paper.accentFill : Paper.hairline))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain).pointerCursor()
                .accessibilityAddTraits(settings.orbTheme == theme ? .isSelected : [])
            }
        }
    }
}

private struct OrbSizePicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        PaperSegments(options: [(OrbSize.full, "full"), (OrbSize.mini, "mini")], selection: $settings.orbSize)
    }
}

// MARK: - live preview

/// The orb as it will look, in a pretend app, in four moments of a take.
struct OrbLivePreviewPanel: View {
    @ObservedObject var settings: DictationSettings
    @StateObject private var previewModel = OrbModel()
    @State private var moment: PreviewMoment = .resting

    enum PreviewMoment: String, CaseIterable, Identifiable {
        case resting, listening
        case movingWords = "moving words"
        case noPlaceToPaste = "no place to paste"
        var id: String { rawValue }
    }

    private let sampleTake = "see you at seven, i’ll bring the charger."

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("live preview · updates as you change things").font(Paper.micro).foregroundStyle(Paper.inkTertiary)
            ZStack(alignment: .bottom) {
                RoundedRectangle(cornerRadius: 10)
                    .fill(LinearGradient(colors: [Color(red: 0.85, green: 0.9, blue: 0.95), Color(red: 0.95, green: 0.88, blue: 0.85)], startPoint: .top, endPoint: .bottom))
                    .overlay(alignment: .top) {
                        HStack {
                            Text("Messages").font(.system(size: 9, weight: .semibold))
                            Spacer()
                            Text("9:41").font(.system(size: 9))
                        }
                        .foregroundStyle(Color.black.opacity(0.7))
                        .padding(.horizontal, 10).frame(height: 18)
                        .background(Color.white.opacity(0.55))
                        .clipShape(UnevenRoundedRectangle(topLeadingRadius: 10, topTrailingRadius: 10))
                    }
                if settings.orbVisible {
                    OrbRootView(model: previewModel, settings: settings)
                        .scaleEffect(0.7, anchor: .bottom)
                        .allowsHitTesting(false)
                } else {
                    Text("the orb is hidden — \(settings.dictationKey.keycapLabel) still works.")
                        .font(Paper.caption).foregroundStyle(Color.black.opacity(0.6))
                        .padding(.bottom, 16)
                }
            }
            .frame(height: 220)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("orb preview, \(moment.rawValue)")

            FlowingChips(moments: PreviewMoment.allCases, selected: moment) { moment = $0 }

            HStack(spacing: 6) {
                Text(settings.orbPosition == nil ? "rests at the bottom of your screen" : "resting where you dragged it")
                    .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                if settings.orbPosition != nil {
                    Button("back to the bottom") { settings.orbPosition = nil }
                        .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
                }
            }
        }
        .padding(Paper.Metric.rowHorizontal)
        .background(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).fill(Paper.card))
        .overlay(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).strokeBorder(Paper.hairline))
        .onAppear(perform: applyMoment)
        .onChange(of: moment) { _, _ in applyMoment() }
        .onChange(of: settings.orbRestsExpanded) { _, _ in applyMoment() }
    }

    private func applyMoment() {
        previewModel.quickRewrites = []
        previewModel.isRewriting = false
        previewModel.audioLevel = 0
        previewModel.liveTranscript = ""
        switch moment {
        case .resting:
            previewModel.phase = .idle
            previewModel.boxText = sampleTake
            previewModel.boxReason = "your last take"
            previewModel.isBoxOpen = settings.orbRestsExpanded
            previewModel.hint = "tap or hold \(settings.dictationKey.keycapLabel) to talk"
        case .listening:
            previewModel.isBoxOpen = false
            previewModel.phase = .listening
            previewModel.liveTranscript = "see you at seven, i’ll"
            previewModel.audioLevel = 0.55
            previewModel.hint = nil
        case .movingWords:
            previewModel.isBoxOpen = false
            previewModel.phase = .working("pasting…")
            previewModel.hint = nil
        case .noPlaceToPaste:
            previewModel.phase = .idle
            previewModel.boxText = sampleTake
            previewModel.boxReason = "moved to text box"
            previewModel.quickRewrites = ["shorter"]
            previewModel.isBoxOpen = true
            previewModel.hint = nil
        }
    }
}

private struct FlowingChips: View {
    let moments: [OrbLivePreviewPanel.PreviewMoment]
    let selected: OrbLivePreviewPanel.PreviewMoment
    let onSelect: (OrbLivePreviewPanel.PreviewMoment) -> Void

    var body: some View {
        // Two rows of two so the chips fit the 300 pt panel.
        VStack(alignment: .leading, spacing: 6) {
            ForEach(0..<((moments.count + 1) / 2), id: \.self) { rowIndex in
                HStack(spacing: 6) {
                    ForEach(moments[(rowIndex * 2)..<min(rowIndex * 2 + 2, moments.count)]) { moment in
                        Button(action: { onSelect(moment) }) {
                            Text(moment.rawValue)
                                .font(Paper.body(11, weight: selected == moment ? .semibold : .regular))
                                .foregroundStyle(selected == moment ? Color.white : Paper.ink)
                                .padding(.horizontal, 10).padding(.vertical, 4)
                                .background(Capsule().fill(selected == moment ? Paper.accentFill : Paper.cardRaised))
                                .overlay(Capsule().strokeBorder(selected == moment ? Color.clear : Paper.hairline))
                        }
                        .buttonStyle(.plain).pointerCursor()
                        .accessibilityAddTraits(selected == moment ? .isSelected : [])
                    }
                }
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
