//
//  SettingsShortcutsPage.swift
//  OpenClicky
//
//  Settings → shortcuts: which keys do what. The "free the fn key" banner (only while macOS
//  still gives fn a job of its own), the dictation keys, the companion's keys with how visible
//  the pointer is, and pasting the last take again.
//

import Combine
import SwiftUI

extension SettingsCatalog {
    func shortcutsItems() -> [SettingsItem] {
        let key = settings.dictationKey.keycapLabel
        let fnKeyState = FnKeyState.shared
        let dictationKeyIsFn = settings.dictationKey == .fn
        return [
            SettingsItem(
                id: "shortcuts.fnBanner", page: .shortcuts, section: "",
                title: "fn also opens \(fnKeyState.usageNoun)", detail: "macOS uses the same key. let openclicky set it to “do nothing” — you can switch it back in keyboard settings.",
                keywords: ["fn", "emoji", "globe", "free the fn key", "keyboard settings"],
                chrome: .bare,
                isRelevant: { dictationKeyIsFn && !FnKeyState.shared.isFree },
                view: AnyView(FnKeyBanner(fnKeyState: fnKeyState))),
            SettingsItem(
                id: "shortcuts.dictationKey", page: .shortcuts, section: "dictation",
                title: "dictation key", detail: "hold to talk anywhere; tap to start a take, tap again to finish. currently \(key).",
                keywords: ["fn", "hotkey", "push to talk", "right option", "right command", "right control", "dictate"],
                view: AnyView(SettingsRow(title: "dictation key", detail: "hold to talk anywhere; tap to start a take, tap again to finish. currently \(key).") {
                    DictationKeyPicker(settings: settings)
                })),
            SettingsItem(
                id: "shortcuts.heyClicky", page: .shortcuts, section: "dictation",
                title: "“hey clicky” — edit selected text", detail: "hold \(key) with control and say the change: “make it formal”.",
                keywords: ["hey clicky", "edit", "rewrite", "control", "⌃", "selection"],
                view: AnyView(SettingsRow(title: "“hey clicky” — edit selected text", detail: "hold \(key) with control and say the change: “make it formal”.") {
                    Keycap(text: "\(key) + ⌃ hold")
                })),
            SettingsItem(
                id: "shortcuts.cancel", page: .shortcuts, section: "dictation",
                title: "cancel a take", detail: "esc, or double-tap \(key). throws the take away; nothing is pasted.",
                keywords: ["esc", "escape", "discard", "stop", "double-tap"],
                view: AnyView(SettingsRow(title: "cancel a take", detail: "esc, or double-tap \(key). throws the take away; nothing is pasted.") {
                    Keycap(text: "esc · \(key) ×2")
                })),
            SettingsItem(
                id: "shortcuts.fnFree", page: .shortcuts, section: "dictation",
                title: "fn is free for dictation", detail: "macOS’s own fn action is off (keyboard settings → “press 🌐 key to: do nothing”).",
                keywords: ["fn", "emoji", "globe", "give fn back"],
                isRelevant: { dictationKeyIsFn && FnKeyState.shared.isFree },
                view: AnyView(SettingsRow(title: "fn is free for dictation", detail: "macOS’s own fn action is off (keyboard settings → “press 🌐 key to: do nothing”).") {
                    Button("give fn back to macOS") { fnKeyState.giveBack() }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "shortcuts.talk", page: .shortcuts, section: "the companion",
                title: "talk to clicky", detail: "hold and ask about anything on screen; it answers out loud and points.",
                keywords: ["⌃", "⌥", "control option", "voice", "ask", "companion"],
                view: AnyView(SettingsRow(title: "talk to clicky", detail: "hold and ask about anything on screen; it answers out loud and points.") {
                    Keycap(text: "⌃ + ⌥ hold")
                })),
            SettingsItem(
                id: "shortcuts.type", page: .shortcuts, section: "the companion",
                title: "type to clicky", detail: "a one-line composer opens in the notch.",
                keywords: ["⌃", "composer", "notch", "text", "control twice"],
                view: AnyView(SettingsRow(title: "type to clicky", detail: "a one-line composer opens in the notch.") {
                    Keycap(text: "⌃ ×2")
                })),
            SettingsItem(
                id: "shortcuts.handsFree", page: .shortcuts, section: "the companion",
                title: "hands-free", detail: "tap \(key) + ⌃ twice for always-on listening with realtime voice. same keys to stop.",
                keywords: ["always on", "realtime", "listening", "hands free"],
                view: AnyView(SettingsRow(title: "hands-free", detail: "tap \(key) + ⌃ twice for always-on listening with realtime voice. same keys to stop.") {
                    Keycap(text: "\(key) + ⌃ ×2")
                })),
            SettingsItem(
                id: "shortcuts.pointerPresence", page: .shortcuts, section: "the companion",
                title: "the pointer shows", detail: "how visible the orange pointer is while you work.",
                keywords: ["pointer", "cursor", "buddy", "always", "while the mouse moves", "after a shake", "shake", "hide"],
                view: AnyView(PointerPresenceRadio(companionManager: companionManager))),
            SettingsItem(
                id: "shortcuts.pasteLast", page: .shortcuts, section: "text",
                title: "paste last take", detail: "put your last take at the cursor again.",
                keywords: ["paste", "again", "re-insert", "last"],
                view: AnyView(SettingsRow(title: "paste last take", detail: "put your last take at the cursor again.") {
                    Button("paste now") { companionManager.dictationTakeController.pasteLast() }.buttonStyle(PaperPillButtonStyle())
                })),
        ]
    }
}

/// What macOS does with fn, readable and refreshable from the views that offer to change it.
@MainActor
final class FnKeyState: ObservableObject {
    static let shared = FnKeyState()

    @Published private(set) var usage: FnKeyGuard.Usage? = FnKeyGuard.currentUsage()

    var isFree: Bool { usage == .doNothing }

    /// "the emoji picker", or what else fn does.
    var usageNoun: String {
        switch usage {
        case .emojiAndSymbols, .none: return "the emoji picker"
        case .changeInputSource: return "the input source switcher"
        case .startDictation: return "apple dictation"
        case .doNothing: return "nothing"
        }
    }

    func refresh() { usage = FnKeyGuard.currentUsage() }

    func free() {
        FnKeyGuard.freeFn()
        refresh()
    }

    func giveBack() {
        FnKeyGuard.restoreFn()
        refresh()
    }
}

private struct FnKeyBanner: View {
    @ObservedObject var fnKeyState: FnKeyState
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 3) {
                Text(settingsHighlighted("fn also opens \(fnKeyState.usageNoun)", query: searchQuery)).font(Paper.rowTitle).foregroundStyle(Paper.ink)
                Text(settingsHighlighted("macOS uses the same key. let openclicky set it to “do nothing” — you can switch it back in keyboard settings.", query: searchQuery))
                    .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 16)
            Button("free the fn key") { fnKeyState.free() }.buttonStyle(PaperPillButtonStyle(prominent: true))
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical)
        .background(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).fill(Paper.selection.opacity(0.55)))
        .overlay(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).strokeBorder(Paper.accent.opacity(0.35)))
        .onAppear { fnKeyState.refresh() }
    }
}

private struct DictationKeyPicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.dictationKey) {
            ForEach(DictationHotkey.allCases) { key in Text(key.displayName).tag(key) }
        }
        .labelsHidden().frame(width: 150)
    }
}

/// always / while the mouse moves / after a shake. Follows `pointerPresence` on its own instead
/// of observing the whole companion manager, which republishes on every audio level.
private struct PointerPresenceRadio: View {
    let companionManager: CompanionManager
    @State private var presence: PointerPresence
    @Environment(\.settingsSearchQuery) private var searchQuery

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        _presence = State(initialValue: companionManager.pointerPresence)
    }

    private func detail(for option: PointerPresence) -> String {
        switch option {
        case .always: return "follows the mouse everywhere."
        case .whileMoving: return "fades a few seconds after the mouse rests."
        case .onShake: return "hidden until you shake. a shake also releases a docked pointer."
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(settingsHighlighted("the pointer shows", query: searchQuery)).font(Paper.rowTitle).foregroundStyle(Paper.ink)
                Text(settingsHighlighted("how visible the orange pointer is while you work.", query: searchQuery)).font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            }
            VStack(alignment: .leading, spacing: 6) {
                ForEach(PointerPresence.allCases) { option in
                    Button(action: { companionManager.setPointerPresence(option) }) {
                        HStack(alignment: .top, spacing: 10) {
                            Image(systemName: presence == option ? "largecircle.fill.circle" : "circle")
                                .font(.system(size: 13))
                                .foregroundStyle(presence == option ? Paper.accentFill : Paper.inkTertiary)
                                .padding(.top, 1)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(settingsHighlighted(option.label, query: searchQuery)).font(Paper.body(13, weight: presence == option ? .semibold : .regular)).foregroundStyle(Paper.ink)
                                Text(settingsHighlighted(detail(for: option), query: searchQuery)).font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                            }
                            Spacer()
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).pointerCursor()
                    .accessibilityLabel(option.label)
                    .accessibilityHint(detail(for: option))
                    .accessibilityAddTraits(presence == option ? .isSelected : [])
                }
            }
            .padding(.leading, 2)
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical)
        .frame(maxWidth: .infinity, alignment: .leading)
        .onReceive(companionManager.$pointerPresence) { presence = $0 }
    }
}
