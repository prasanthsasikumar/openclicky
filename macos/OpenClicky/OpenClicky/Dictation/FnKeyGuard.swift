//
//  FnKeyGuard.swift
//  OpenClicky
//
//  macOS gives the fn key a job of its own — Emoji & Symbols, Dictation, or switching input
//  sources (System Settings → Keyboard → "Press 🌐 key to"). While it has one, every tap of the
//  dictation key also opens that. This reads the setting, offers to set it to "Do Nothing", and
//  remembers the old value so it can be put back. The setting lives in the HIToolbox domain as
//  `AppleFnUsageType`: 0 do nothing, 1 change input source, 2 emoji & symbols, 3 start dictation.
//

import Foundation

enum FnKeyGuard {
    static let domain = "com.apple.HIToolbox"
    static let key = "AppleFnUsageType"
    private static let rememberedKey = "dictation.fnGuard.previousUsageType"

    enum Usage: Int {
        case doNothing = 0, changeInputSource = 1, emojiAndSymbols = 2, startDictation = 3

        var description: String {
            switch self {
            case .doNothing: return "do nothing"
            case .changeInputSource: return "change input source"
            case .emojiAndSymbols: return "show emoji & symbols"
            case .startDictation: return "start apple dictation"
            }
        }
    }

    /// What macOS does with fn right now; nil when unreadable.
    static func currentUsage() -> Usage? {
        guard let value = CFPreferencesCopyAppValue(key as CFString, domain as CFString) as? Int else {
            // Unset means the default, which is Emoji & Symbols on current macOS.
            return .emojiAndSymbols
        }
        return Usage(rawValue: value) ?? .doNothing
    }

    /// True when fn is free for dictation.
    static var isFnFree: Bool { currentUsage() == .doNothing }

    /// Sets fn to do nothing, remembering what it did before. Takes effect for new key presses
    /// after the HIToolbox notices, which is immediate for the preference itself.
    static func freeFn() {
        if let current = currentUsage(), current != .doNothing {
            UserDefaults.standard.set(current.rawValue, forKey: rememberedKey)
        }
        write(.doNothing)
    }

    /// Puts the previous job back (Settings → shortcuts → "give fn back to macOS").
    static func restoreFn() {
        let previous = Usage(rawValue: UserDefaults.standard.integer(forKey: rememberedKey)) ?? .emojiAndSymbols
        write(previous == .doNothing ? .emojiAndSymbols : previous)
        UserDefaults.standard.removeObject(forKey: rememberedKey)
    }

    private static func write(_ usage: Usage) {
        CFPreferencesSetAppValue(key as CFString, usage.rawValue as CFNumber, domain as CFString)
        CFPreferencesAppSynchronize(domain as CFString)
        // The HIToolbox reads the value on a distributed notification; nudge it the way System Settings does.
        DistributedNotificationCenter.default().post(name: Notification.Name("com.apple.HIToolbox.fnUsageTypeDidChange"), object: nil)
        AppLog.append("fn key guard: AppleFnUsageType = \(usage.rawValue) (\(usage.description))")
    }
}
