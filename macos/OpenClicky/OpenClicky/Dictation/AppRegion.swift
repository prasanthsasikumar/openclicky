//
//  AppRegion.swift
//  OpenClicky
//
//  India or the rest of the world: which options the settings offer. Sarvam (engine, voice, key),
//  the ten Indian dictation languages, seven Indian Clicky languages and the native-or-roman
//  script are India's. Since 2026-10-30 OpenClicky is for the world and the Region choice is gone:
//  `DictationSettings.region` is always `.world`. It never changes what is set: an option already
//  in use stays offered.
//

import Foundation

enum AppRegion: String, CaseIterable, Identifiable {
    case india
    case world
    var id: String { rawValue }

    var title: String {
        switch self {
        case .india: return "India"
        case .world: return "Rest of the world"
        }
    }

    /// Whether the settings offer what only Indian users need.
    var offersIndianOptions: Bool { self == .india }

    /// The Clicky languages from `ReplyLanguage.choices` that only India needs.
    static let indianReplyLanguageCodes: Set<String> = ["hi", "ta", "te", "bn", "mr", "kn", "ml"]
}

extension DictationLanguage {
    /// Whether this is one of the Indian languages (English counts as India's only in its en-IN form).
    var isIndian: Bool { code.hasSuffix("-IN") && code != "en-IN" }

    /// The pinnable languages the region offers, in picker order, always keeping `current`.
    static func pinnableChoices(region: AppRegion, current: DictationLanguage) -> [DictationLanguage] {
        let offered = region == .india ? indianChoices : worldChoices
        if current == .auto || offered.contains(current) { return offered }
        return offered + [current]
    }

    /// What "Pin one" starts on when nothing was pinned before.
    static func defaultPinned(region: AppRegion) -> DictationLanguage {
        region == .india ? named("en-IN") : named("en-US")
    }

    private static var indianChoices: [DictationLanguage] { choices.filter { $0 != .auto && $0.code.hasSuffix("-IN") } }
    private static var worldChoices: [DictationLanguage] { choices.filter { $0 != .auto && !$0.code.hasSuffix("-IN") } }
}

extension ReplyLanguage {
    /// The Clicky languages the region offers, always keeping `currentCode`.
    static func choices(region: AppRegion, currentCode: String) -> [Choice] {
        choices.filter { choice in
            region.offersIndianOptions || !AppRegion.indianReplyLanguageCodes.contains(choice.code) || choice.code == currentCode
        }
    }
}
