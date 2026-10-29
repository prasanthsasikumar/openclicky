//
//  SettingsKeyboardPage.swift
//  OpenClicky
//
//  Settings → Shortcuts (the `keyboard` page): which keys do what, at a glance. Dictating,
//  editing and cancelling a take, and Clicky's keys. The dictation key itself (with the "fn also
//  opens…" warning while macOS still gives fn a job of its own) is changed on Dictation; its
//  items are built here because they share the fn state.
//

import Combine
import SwiftUI

extension SettingsCatalog {
    /// The dictation key and what macOS does with fn: on the Dictation page, where someone
    /// changing how they dictate looks for it. Shortcuts shows the key and links here.
    func dictationKeyItems() -> [SettingsItem] {
        let fnKeyState = FnKeyState.shared
        let dictationKeyIsFn = settings.dictationKey == .fn
        // A right-hand key shares its flag with the left one: the left one keeps the companion's
        // jobs, and a key typed while the right one is held makes the hold a shortcut, not a take.
        let rightHandKeyNote: String = {
            switch settings.dictationKey {
            case .fn: return ""
            case .rightOption: return " Talk uses the left ⌥ with ⌃. A key typed with right ⌥ held (é, €) cancels the take."
            case .rightCommand: return " A shortcut typed with right ⌘ held (⌘C) cancels the take."
            case .rightControl: return " Edits, hands-free and ⌃ twice use the left ⌃."
            }
        }()
        let dictationKeyDetail = "Hold to talk anywhere. Tap to start a take, tap again to finish." + rightHandKeyNote
        return [
            SettingsItem(
                id: "keyboard.dictationKey", page: .dictation, section: "Key",
                title: "Dictation key", detail: dictationKeyDetail,
                keywords: ["fn", "hotkey", "push to talk", "right option", "right command", "right control", "dictate", "shortcut"],
                view: AnyView(SettingsRow(title: "Dictation key", detail: dictationKeyDetail) {
                    DictationKeyPicker(settings: settings)
                })),
            SettingsItem(
                id: "keyboard.where", page: .dictation, section: "Key",
                title: "Where the key is", detail: "A sketch of a MacBook keyboard with the dictation key held.",
                keywords: ["where", "keyboard", "right option", "right command", "right control", "left", "macbook"],
                view: AnyView(KeyboardSketchRow(settings: settings))),
            SettingsItem(
                id: "keyboard.fnWarning", page: .dictation, section: "Key",
                title: "fn also opens \(fnKeyState.usageNoun)", detail: "macOS uses the same key. You can switch it back in the system’s Keyboard settings.",
                keywords: ["fn", "emoji", "globe", "free the fn key", "keyboard settings"],
                isRelevant: { dictationKeyIsFn && !FnKeyState.shared.isFree },
                view: AnyView(FnKeyWarningRow(fnKeyState: fnKeyState))),
            SettingsItem(
                id: "keyboard.fnFree", page: .dictation, section: "Key",
                title: "fn is free for dictation", detail: "macOS’s own fn action is off (Keyboard settings → Press 🌐 key to: Do Nothing).",
                keywords: ["fn", "emoji", "globe", "give fn back"],
                isRelevant: { dictationKeyIsFn && FnKeyState.shared.isFree },
                view: AnyView(SettingsRow(title: "fn is free for dictation", detail: "macOS’s own fn action is off (Keyboard settings → Press 🌐 key to: Do Nothing).") {
                    Button("Give fn Back to macOS") { fnKeyState.giveBack() }
                        .pointerCursor()
                })),
        ]
    }

    /// Settings → Shortcuts: every key OpenClicky listens for, at a glance.
    func keyboardItems() -> [SettingsItem] {
        let key = settings.dictationKey.keycapLabel
        let usesRealtime = AccountCapabilities.current().usesRealtime
        return [
            SettingsItem(
                id: "keyboard.dictate", page: .keyboard, section: "Dictation",
                title: "Dictate", detail: "Hold, or tap to start and tap to finish. Change the key on the Dictation page.",
                keywords: ["fn", "hotkey", "push to talk", "dictate", "change key"],
                view: AnyView(SettingsRow(title: "Dictate", detail: "Hold, or tap to start and tap to finish. Change the key on the Dictation page.") {
                    HStack(spacing: 8) {
                        SettingsKeys(keys: [key], gesture: "hold")
                        Button("Change…") { companionManager.showSettings(.dictation) }
                            .pointerCursor()
                    }
                })),
            SettingsItem(
                id: "keyboard.edit", page: .keyboard, section: "Dictation",
                title: "Edit selected text", detail: "Hold and say the change, like “make it formal”.",
                keywords: ["hey clicky", "edit", "rewrite", "control", "⌃", "selection"],
                view: AnyView(SettingsRow(title: "Edit selected text", detail: "Hold and say the change, like “make it formal”.") {
                    SettingsKeys(keys: [key, "⌃"], gesture: "hold")
                })),
            SettingsItem(
                id: "keyboard.cancel", page: .keyboard, section: "Dictation",
                title: "Cancel a take", detail: "Nothing is pasted.",
                keywords: ["esc", "escape", "discard", "stop", "double-tap"],
                view: AnyView(SettingsRow(title: "Cancel a take", detail: "Nothing is pasted.") {
                    SettingsKeys(keys: ["esc"], gesture: "or \(key) twice")
                })),
            SettingsItem(
                id: "keyboard.talk", page: .keyboard, section: "Clicky",
                title: "Talk to Clicky", detail: "Ask about anything on screen. It answers out loud and points.",
                keywords: ["⌃", "⌥", "control option", "voice", "ask", "companion"],
                view: AnyView(SettingsRow(title: "Talk to Clicky", detail: "Ask about anything on screen. It answers out loud and points.") {
                    SettingsKeys(keys: ["⌃", "⌥"], gesture: "hold")
                })),
            SettingsItem(
                id: "keyboard.type", page: .keyboard, section: "Clicky",
                title: "Type to Clicky", detail: "A one-line composer opens in the island.",
                keywords: ["⌃", "composer", "notch", "island", "text", "control twice"],
                view: AnyView(SettingsRow(title: "Type to Clicky", detail: "A one-line composer opens in the island.") {
                    SettingsKeys(keys: ["⌃"], gesture: "tap twice")
                })),
            // Hands-free runs on OpenAI Realtime or Gemini Live, which need an own key: without one the key
            // would do nothing, so it isn't listed (Voice says how to get it).
            SettingsItem(
                id: "keyboard.handsFree", page: .keyboard, section: "Clicky",
                title: "Hands-free", detail: "Always-on listening. Same keys to stop.",
                keywords: ["always on", "realtime", "listening", "hands free"],
                isRelevant: { usesRealtime },
                view: AnyView(SettingsRow(title: "Hands-free", detail: "Always-on listening. Same keys to stop.") {
                    SettingsKeys(keys: [key, "⌃"], gesture: "tap twice")
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
        case .emojiAndSymbols, .none: return "the Emoji picker"
        case .changeInputSource: return "the input source switcher"
        case .startDictation: return "Apple Dictation"
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

/// macOS still gives fn a job of its own: a warning row with the fix.
private struct FnKeyWarningRow: View {
    @ObservedObject var fnKeyState: FnKeyState
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "exclamationmark.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(.white, SettingsLook.warning)
            VStack(alignment: .leading, spacing: 2) {
                Text(settingsHighlighted("fn also opens \(fnKeyState.usageNoun)", query: searchQuery)).font(SettingsLook.rowTitle)
                Text(settingsHighlighted("macOS uses the same key. You can switch it back in the system’s Keyboard settings.", query: searchQuery))
                    .font(SettingsLook.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 12)
            Button("Set fn to Do Nothing") { fnKeyState.free() }
                .pointerCursor()
        }
        .padding(.vertical, 4)
        .onAppear { fnKeyState.refresh() }
    }
}

private struct KeyboardSketchRow: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        KeyboardSketch(key: settings.dictationKey)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 8)
    }
}

private struct DictationKeyPicker: View {
    @ObservedObject var settings: DictationSettings

    var body: some View {
        Picker("", selection: $settings.dictationKey) {
            ForEach(DictationHotkey.allCases) { key in Text(key.settingsTitle).tag(key) }
        }
        .labelsHidden().fixedSize()
        .pointerCursor()
    }
}

extension DictationHotkey {
    /// The pop-up's label ("Right Option ⌥").
    var settingsTitle: String {
        switch self {
        case .fn: return "fn"
        case .rightOption: return "Right Option ⌥"
        case .rightCommand: return "Right Command ⌘"
        case .rightControl: return "Right Control ⌃"
        }
    }
}
