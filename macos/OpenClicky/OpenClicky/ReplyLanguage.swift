//
//  ReplyLanguage.swift
//  OpenClicky
//
//  The one language OpenClicky speaks, from `language` in ~/.openclicky/shell.json (Settings →
//  Voice → Language), English until another is chosen.
//
//  The Realtime prompt used to say "Speak English unless the user speaks another language", and
//  any such freedom gets used: in the sibling app Saathi, clean English audio was answered in
//  Italian, Japanese and Portuguese once the model had followed one misheard turn — each reply in
//  the new language made the next one likelier. So there is one language, stated in every prompt,
//  and the transcription is pinned to it too: unpinned, English audio came back as "怎麼這樣?",
//  which the model then read as the user switching language.
//

import Foundation

enum ReplyLanguage {

    struct Choice: Identifiable, Equatable {
        let code: String
        let name: String
        var id: String { code }
    }

    /// What the Settings picker offers. Short on purpose: it is a picker in a small panel.
    static let choices: [Choice] = [
        Choice(code: "en", name: "English"),
        Choice(code: "hi", name: "हिन्दी"),
        Choice(code: "ta", name: "தமிழ்"),
        Choice(code: "te", name: "తెలుగు"),
        Choice(code: "bn", name: "বাংলা"),
        Choice(code: "mr", name: "मराठी"),
        Choice(code: "kn", name: "ಕನ್ನಡ"),
        Choice(code: "ml", name: "മലയാളം"),
        Choice(code: "es", name: "Español"),
        Choice(code: "fr", name: "Français"),
        Choice(code: "de", name: "Deutsch"),
        Choice(code: "ja", name: "日本語"),
        Choice(code: "ko", name: "한국어"),
        Choice(code: "zh", name: "中文"),
    ]

    /// The configured language as an ISO-639-1 code, which is what the transcription models take:
    /// "en-US" in shell.json is sent as "en".
    static var currentCode: String {
        normalizedCode(OpenClickyConfiguration.settings.language)
    }

    nonisolated static func normalizedCode(_ configured: String?) -> String {
        let trimmed = configured?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard let first = trimmed.split(whereSeparator: { $0 == "-" || $0 == "_" }).first, !first.isEmpty else {
            return "en"
        }
        return first.lowercased()
    }

    /// The language's name in English, for the prompt: "English", "Hindi".
    nonisolated static func englishName(for code: String) -> String {
        Locale(identifier: "en").localizedString(forLanguageCode: code) ?? code
    }

    /// Added to every system prompt, Realtime and Claude alike.
    nonisolated static func instruction(for code: String) -> String {
        let name = englishName(for: code)
        return """
        Always speak and write in \(name). Every reply is in \(name): whatever language the user \
        seems to use, whatever language the transcript shows, and whatever language any earlier \
        reply in this conversation was in. Only the user changes this, in OpenClicky's settings. \
        If a turn is silent, only noise, or too short to make out, do not invent what was said: \
        say in \(name) that you did not catch that and ask them to say it again.
        """
    }

    /// A locale for Apple's recognisers: the configured language in this Mac's region where that
    /// pairing exists ("en" on an Indian Mac is en-IN), else the language on its own.
    static var recognitionLocale: Locale {
        let code = currentCode
        if let region = Locale.autoupdatingCurrent.region?.identifier {
            return Locale(identifier: "\(code)-\(region)")
        }
        return Locale(identifier: code)
    }
}
