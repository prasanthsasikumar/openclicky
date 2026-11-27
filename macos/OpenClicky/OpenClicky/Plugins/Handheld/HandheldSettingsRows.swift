//
//  HandheldSettingsRows.swift
//  OpenClicky
//
//  The handheld plugin in Settings. Everyone gets one switch, folded under Island › More. Someone
//  who switches it on gets a row that opens the Handheld page: which device is connected, and for
//  every button and every gesture it reports, a picker of what that does. `SettingsLayout` places
//  the rows; this file builds them.
//

import AppKit
import Combine
import SwiftUI

extension SettingsCatalog {
    func handheldSettingsItems() -> [SettingsItem] {
        // The rows shown depend on the plugin's switch and on which device was seen; an open
        // Settings page is drawn again when either changes.
        HandheldPlugin.shared.onSettingsShapeChanged = { [weak settings] in settings?.objectWillChange.send() }
        return HandheldSettings.items(plugin: .shared)
    }
}

@MainActor
enum HandheldSettings {
    static let switchTitle = "Handheld devices"
    static let switchDetail = "Use a supported push-to-talk handheld with OpenClicky."
    static let mouseNote = "The wheel and touch pad work as a mouse."
    static let makerAppNote = "The WhisPrompt app is also running, so each press will do two things."

    /// The word a gesture goes by on the page.
    static func label(for gesture: HandheldGesture) -> String {
        switch gesture {
        case .single: return "press"
        case .double: return "twice"
        case .longStart: return "hold"
        case .down: return "down"
        case .longEnd: return "release"
        }
    }

    static func items(plugin: HandheldPlugin) -> [SettingsItem] {
        var items: [SettingsItem] = [
            SettingsItem(
                id: "handheld.enabled",
                title: switchTitle, detail: switchDetail,
                keywords: ["handheld", "device", "button", "hardware", "whisprompt", "push to talk", "plugin"],
                view: AnyView(HandheldSwitchRow(plugin: plugin))),
            SettingsItem(
                id: "island.openHandheld",
                title: "Handheld", detail: DictationSettingsPage.handheld.subtitle,
                keywords: ["handheld", "device", "button", "bindings", "keys"],
                isRelevant: { plugin.isEnabled },
                view: AnyView(SettingsNavigationRow(title: "Handheld", detail: DictationSettingsPage.handheld.subtitle, destination: .settings(.handheld)))),
            SettingsItem(
                id: "handheld.status",
                title: "Handheld", detail: "Which handheld is connected.",
                keywords: ["handheld", "connected", "reset"],
                isRelevant: { plugin.isEnabled },
                view: AnyView(HandheldStatusRow(plugin: plugin))),
            SettingsItem(
                id: "handheld.makerAppNote",
                title: makerAppNote, chrome: .footer,
                isRelevant: { plugin.isEnabled && plugin.makerAppIsAlsoRunning },
                view: AnyView(SettingsFootnote(text: makerAppNote))),
        ]

        // One picker per button and gesture, for every supported device; only the rows of the
        // device the page is about are shown.
        for device in HandheldDeviceCatalog.all {
            for control in device.controls {
                for gesture in HandheldBindings.bindableGestures(of: control) {
                    let rowTitle = "\(control.name), \(label(for: gesture))"
                    items.append(SettingsItem(
                        id: "handheld.binding.\(device.id).\(control.id).\(gesture.rawValue)",
                        title: rowTitle, detail: "",
                        keywords: ["handheld", "button", "binding", control.name.lowercased()],
                        isRelevant: { plugin.isEnabled && plugin.deviceShown?.id == device.id },
                        view: AnyView(HandheldBindingRow(plugin: plugin, control: control, gesture: gesture, title: rowTitle))))
                }
            }
        }

        items.append(SettingsItem(
            id: "handheld.microphone",
            title: HandheldMicrophone.rowTitle, detail: HandheldMicrophone.rowDetail,
            keywords: ["handheld", "microphone", "mic", "input"],
            isRelevant: { plugin.isEnabled && plugin.deviceShown?.microphoneName != nil },
            view: AnyView(HandheldMicrophoneRow(plugin: plugin))))
        items.append(SettingsItem(
            id: "handheld.mouseNote",
            title: mouseNote, chrome: .footer,
            isRelevant: { plugin.isEnabled && plugin.deviceShown != nil },
            view: AnyView(SettingsFootnote(text: mouseNote))))
        return items
    }
}

// MARK: - The switch

struct HandheldSwitchRow: View {
    @ObservedObject var plugin: HandheldPlugin

    var body: some View {
        SettingsRow(title: HandheldSettings.switchTitle, detail: HandheldSettings.switchDetail) {
            SettingsSwitch(isOn: Binding(get: { plugin.isEnabled }, set: { plugin.setEnabled($0) }))
                .accessibilityLabel(HandheldSettings.switchTitle)
        }
    }
}

// MARK: - Its microphone

struct HandheldMicrophoneRow: View {
    @ObservedObject var plugin: HandheldPlugin

    var body: some View {
        SettingsRow(title: HandheldMicrophone.rowTitle, detail: HandheldMicrophone.rowDetail) {
            SettingsSwitch(isOn: Binding(get: { plugin.usesDeviceMicrophone }, set: { plugin.setUsesDeviceMicrophone($0) }))
                .accessibilityLabel(HandheldMicrophone.rowTitle)
        }
    }
}

// MARK: - Which device, and reset

struct HandheldStatusRow: View {
    @ObservedObject var plugin: HandheldPlugin

    private var title: String {
        plugin.deviceShown?.displayName ?? "No supported handheld is connected"
    }

    private var detail: String {
        if plugin.connectedDevice != nil { return "Connected." }
        let supportedNames = HandheldDeviceCatalog.all.map(\.displayName).joined(separator: ", ")
        if plugin.deviceShown != nil { return "Not connected. Its buttons can still be set up." }
        return "Plug one in to set up its buttons. Supported: \(supportedNames)."
    }

    var body: some View {
        SettingsRow(title: title, detail: detail) {
            if plugin.deviceShown != nil {
                Button("Reset all") { plugin.resetBindings() }
                    .controlSize(.small)
                    .pointerCursor()
                    .help("Put every button back to what it did at first")
            }
        }
    }
}

// MARK: - One button, one gesture

struct HandheldBindingRow: View {
    @ObservedObject var plugin: HandheldPlugin
    let control: HandheldControl
    let gesture: HandheldGesture
    let title: String
    @State private var isRecordingKeys = false

    /// The picker's entries are told apart by these tags; an action carries no identity of its own.
    private static let currentCombinationTag = "combination"
    private static let recordTag = "record"

    private var boundAction: HandheldAction {
        plugin.bindings.action(controlID: control.id, gesture: gesture)
    }

    private var fixedChoices: [HandheldAction] {
        HandheldAction.choices(for: gesture, on: control)
    }

    private func tag(for action: HandheldAction) -> String {
        if case .keyCombination = action { return Self.currentCombinationTag }
        return action.title
    }

    var body: some View {
        SettingsRow(title: title) {
            Picker("", selection: Binding(
                get: { tag(for: boundAction) },
                set: { pickedTag in
                    if pickedTag == Self.recordTag {
                        isRecordingKeys = true
                    } else if let pickedAction = fixedChoices.first(where: { tag(for: $0) == pickedTag }) {
                        plugin.setAction(pickedAction, controlID: control.id, gesture: gesture)
                    }
                }
            )) {
                ForEach(fixedChoices, id: \.title) { action in
                    Text(action.title).tag(tag(for: action))
                }
                Divider()
                if case .keyCombination(let combination) = boundAction {
                    Text(combination.displayText).tag(Self.currentCombinationTag)
                }
                Text("A key or shortcut…").tag(Self.recordTag)
            }
            .labelsHidden().fixedSize()
            .pointerCursor()
            .accessibilityLabel(title)
            .popover(isPresented: $isRecordingKeys, arrowEdge: .bottom) {
                HandheldKeyRecorder { recordedCombination in
                    if let recordedCombination {
                        plugin.setAction(.keyCombination(recordedCombination), controlID: control.id, gesture: gesture)
                    }
                    isRecordingKeys = false
                }
            }
        }
    }
}

// MARK: - Recording a key

/// "Press the key or shortcut": the next key pressed, with its modifiers, becomes the binding.
/// Esc closes it without changing anything.
struct HandheldKeyRecorder: View {
    /// Called once with the combination, or nil when it was called off.
    let onFinish: (HandheldKeyCombination?) -> Void
    @State private var keyMonitor: Any?

    private static let escapeKeyCode: UInt16 = 53

    var body: some View {
        VStack(spacing: 6) {
            Text("Press the key or shortcut").font(SettingsLook.rowTitle)
            Text("Esc to cancel").font(SettingsLook.caption).foregroundStyle(.secondary)
        }
        .padding(16)
        .onAppear {
            keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { keyEvent in
                let pressedModifiers = keyEvent.modifierFlags.intersection([.command, .option, .control, .shift])
                if keyEvent.keyCode == Self.escapeKeyCode && pressedModifiers.isEmpty {
                    onFinish(nil)
                } else {
                    onFinish(HandheldKeyCombination.recorded(
                        keyCode: keyEvent.keyCode, modifiers: pressedModifiers,
                        characters: keyEvent.charactersIgnoringModifiers ?? ""))
                }
                // The key is taken here, never typed into the window behind.
                return nil
            }
        }
        .onDisappear {
            if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
            keyMonitor = nil
        }
    }
}

extension HandheldKeyCombination {
    /// The keys that have no letter to show.
    private static let namesOfKeysWithoutALetter: [UInt16: String] = [
        36: "Enter", 76: "Enter", 51: "Delete", 117: "Forward Delete", 53: "Esc", 48: "Tab", 49: "Space",
        123: "←", 124: "→", 125: "↓", 126: "↑", 115: "Home", 119: "End", 116: "Page Up", 121: "Page Down",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10", 103: "F11", 111: "F12",
    ]

    /// A combination as it was pressed in the recorder: the Quartz flags to post it with, and its
    /// name the way a menu shows a shortcut (⌃⌥⇧⌘ then the key).
    static func recorded(keyCode: UInt16, modifiers: NSEvent.ModifierFlags, characters: String) -> HandheldKeyCombination {
        var quartzFlags: CGEventFlags = []
        var modifierSymbols = ""
        if modifiers.contains(.control) { quartzFlags.insert(.maskControl); modifierSymbols += "⌃" }
        if modifiers.contains(.option) { quartzFlags.insert(.maskAlternate); modifierSymbols += "⌥" }
        if modifiers.contains(.shift) { quartzFlags.insert(.maskShift); modifierSymbols += "⇧" }
        if modifiers.contains(.command) { quartzFlags.insert(.maskCommand); modifierSymbols += "⌘" }
        let keyName = namesOfKeysWithoutALetter[keyCode] ?? (characters.isEmpty ? "Key \(keyCode)" : characters.uppercased())
        return HandheldKeyCombination(keyCode: keyCode, modifierFlags: quartzFlags.rawValue, displayText: modifierSymbols + keyName)
    }
}
