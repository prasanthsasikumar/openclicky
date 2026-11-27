//
//  HandheldBindings.swift
//  OpenClicky
//
//  What each gesture of each control of a handheld does: the actions a gesture can be given, one
//  device's bindings, and where a person's own choices are kept (UserDefaults, per device).
//

import CoreGraphics
import Foundation

/// One key with its modifiers, posted to the app in front (Enter, Delete, ⌘Z).
struct HandheldKeyCombination: Codable, Equatable {
    let keyCode: UInt16
    /// `CGEventFlags` raw value of the modifiers held with the key.
    let modifierFlags: UInt64
    /// How the combination is shown in the bindings page ("⌘Z").
    let displayText: String

    static let commandFlag: UInt64 = CGEventFlags.maskCommand.rawValue
}

/// What a gesture can be bound to.
enum HandheldAction: Codable, Equatable {
    /// A take for as long as the control is held, like the dictation key held.
    case dictateHold
    /// A press starts a take and the next press ends it, like the dictation key tapped.
    case dictateToggle
    /// A question for as long as the control is held, like ⌃⌥ held.
    case askHold
    /// A press starts a question and the next press sends it. For controls that cannot be held.
    case askToggle
    case handsFreeToggle
    /// Opens the island's composer.
    case typeQuestion
    /// What esc does: stops a take, a question or an answer.
    case stop
    case goToWaitingTerminal
    case openIsland
    case keyCombination(HandheldKeyCombination)
    case nothing

    var title: String {
        switch self {
        case .dictateHold: return "Dictate (hold)"
        case .dictateToggle: return "Dictate (press to start, press to stop)"
        case .askHold: return "Ask Clicky (hold)"
        case .askToggle: return "Ask Clicky (press to start, press to stop)"
        case .handsFreeToggle: return "Hands-free on / off"
        case .typeQuestion: return "Type a question"
        case .stop: return "Stop"
        case .goToWaitingTerminal: return "Go to the waiting terminal"
        case .openIsland: return "Open the island"
        case .keyCombination(let keyCombination): return keyCombination.displayText
        case .nothing: return "Nothing"
        }
    }

    /// True for the actions that last as long as the control is held, so they need a control that
    /// reports both the start and the end of a long press.
    var needsHold: Bool {
        switch self {
        case .dictateHold, .askHold: return true
        default: return false
        }
    }

    /// The fixed actions a picker offers, in its order. A recorded key combination is added by the
    /// picker itself.
    private static let allFixedActions: [HandheldAction] = [
        .dictateHold, .dictateToggle, .askHold, .askToggle, .handsFreeToggle, .typeQuestion, .stop,
        .goToWaitingTerminal, .openIsland, .nothing,
    ]

    /// The actions offered for one gesture of one control. The hold actions are offered only for
    /// a long press on a control that also reports the long press ending: without the end, a take
    /// or a question started by holding would never stop.
    static func choices(for gesture: HandheldGesture, on control: HandheldControl) -> [HandheldAction] {
        let controlReportsWholeHold = control.gestures.contains(.longStart) && control.gestures.contains(.longEnd)
        let holdActionsAreOffered = gesture == .longStart && controlReportsWholeHold
        return allFixedActions.filter { holdActionsAreOffered || !$0.needsHold }
    }
}

struct HandheldBindings: Codable, Equatable {
    /// Control id → gesture raw value → action. A gesture with no entry does nothing.
    private var actionsByControlID: [String: [String: HandheldAction]] = [:]

    init() {}

    init(_ entries: [(controlID: String, gesture: HandheldGesture, action: HandheldAction)]) {
        for entry in entries {
            set(entry.action, controlID: entry.controlID, gesture: entry.gesture)
        }
    }

    func action(controlID: String, gesture: HandheldGesture) -> HandheldAction {
        actionsByControlID[controlID]?[gesture.rawValue] ?? .nothing
    }

    mutating func set(_ action: HandheldAction, controlID: String, gesture: HandheldGesture) {
        actionsByControlID[controlID, default: [:]][gesture.rawValue] = action
    }

    /// The gestures a person chooses an action for, in the page's order. Going down and a long
    /// press ending are never bound: the first comes before the device knows the gesture, the
    /// second ends whatever the long press started.
    static func bindableGestures(of control: HandheldControl) -> [HandheldGesture] {
        [HandheldGesture.single, .double, .longStart].filter { control.gestures.contains($0) }
    }
}

/// A person's bindings per device, in UserDefaults; a device with nothing stored, or with stored
/// data that no longer reads, has its own defaults.
final class HandheldBindingsStore {
    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    private func storageKey(for device: HandheldDevice) -> String {
        "openclicky.handheld.bindings.\(device.id)"
    }

    func bindings(for device: HandheldDevice) -> HandheldBindings {
        guard let storedData = defaults.data(forKey: storageKey(for: device)),
              let storedBindings = try? JSONDecoder().decode(HandheldBindings.self, from: storedData) else {
            return device.defaultBindings
        }
        return storedBindings
    }

    func save(_ bindings: HandheldBindings, for device: HandheldDevice) {
        guard let encodedBindings = try? JSONEncoder().encode(bindings) else { return }
        defaults.set(encodedBindings, forKey: storageKey(for: device))
    }

    func reset(_ device: HandheldDevice) {
        defaults.removeObject(forKey: storageKey(for: device))
    }
}
