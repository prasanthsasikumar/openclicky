//
//  DictationHotkey.swift
//  OpenClicky
//
//  The one key that is held (or tapped) to dictate anywhere. A single modifier, because it has to
//  work on top of whatever app is in front without being a shortcut that app already has: fn by
//  default, with the right-hand modifiers as alternatives for keyboards without fn.
//

import AppKit
import Foundation

enum DictationHotkey: String, CaseIterable, Identifiable {
    case fn
    case rightOption
    case rightCommand
    case rightControl

    var id: String { rawValue }

    /// The key code a `flagsChanged` event carries when this physical key moves.
    var keyCode: UInt16 {
        switch self {
        case .fn: return 63
        case .rightOption: return 61
        case .rightCommand: return 54
        case .rightControl: return 62
        }
    }

    /// The modifier flag this key sets while it is down.
    var modifierFlag: NSEvent.ModifierFlags {
        switch self {
        case .fn: return .function
        case .rightOption: return .option
        case .rightCommand: return .command
        case .rightControl: return .control
        }
    }

    /// What the settings page and the hint pill call it.
    var keycapLabel: String {
        switch self {
        case .fn: return "fn"
        case .rightOption: return "right ⌥"
        case .rightCommand: return "right ⌘"
        case .rightControl: return "right ⌃"
        }
    }

    var displayName: String {
        switch self {
        case .fn: return "fn"
        case .rightOption: return "right option"
        case .rightCommand: return "right command"
        case .rightControl: return "right control"
        }
    }

    /// Whether a `flagsChanged` event with this key code and these flags means the key went down.
    func isDown(keyCode: UInt16, modifierFlags: NSEvent.ModifierFlags) -> Bool? {
        guard keyCode == self.keyCode else { return nil }
        return modifierFlags.contains(modifierFlag)
    }
}
