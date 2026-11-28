//
//  ReplyLanguage.swift
//  OpenClicky
//
//  The language OpenClicky speaks, from `language` in ~/.openclicky/shell.json (Settings →
//  Clicky → Language). English until another is chosen. "auto" is optional: answers follow the
//  language of the latest clear sentence, and transcription is not pinned.
//
//  The Realtime prompt used to say "Speak English unless the user speaks another language", and
//  any such freedom gets used: in the sibling app Saathi, clean English audio was answered in
//  Italian, Japanese and Portuguese once the model had followed one misheard turn — each reply in
//  the new language made the next one likelier. A pinned language is still the default. Automatic
//  is the same freedom, chosen on purpose, and a misheard fragment is not allowed to switch it.
//

import Foundation

enum ReplyLanguage {

    struct Choice: Identifiable, Equatable {
        let code: String
        let name: String
        var id: String { code }
    }

    /// Stored in shell.json when Clicky should follow the language just spoken.
    static let automaticCode = "auto"

    /// What the Settings picker offers. Short on purpose: it is a picker in a small panel.
    static let choices: [Choice] = [
        Choice(code: automaticCode, name: "Automatic"),
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

    /// What is stored: "en", "hi", or `automaticCode`. Unset stays English.
    static var configuredCode: String {
        normalizedCode(OpenClickyConfiguration.settings.language)
    }

    /// Whether answers follow the language just spoken, instead of one pinned language.
    static var followsTheSpeaker: Bool { configuredCode == automaticCode }

    /// A real language for engines that need one. Automatic falls back to English for those.
    static var currentCode: String {
        engineCode(for: OpenClickyConfiguration.settings.language)
    }

    /// The code sent to a transcriber, or nil when Automatic so the model detects the language.
    static var transcriptionLanguageCode: String? {
        transcriptionLanguage(for: OpenClickyConfiguration.settings.language)
    }

    nonisolated static func normalizedCode(_ configured: String?) -> String {
        let trimmed = configured?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard let first = trimmed.split(whereSeparator: { $0 == "-" || $0 == "_" }).first, !first.isEmpty else {
            return "en"
        }
        return first.lowercased()
    }

    nonisolated static func isAutomatic(_ code: String) -> Bool { code == automaticCode }

    /// Nil for Automatic. Anything else is the bare code transcription models take ("en-US" → "en").
    nonisolated static func transcriptionLanguage(for configured: String?) -> String? {
        let code = normalizedCode(configured)
        return isAutomatic(code) ? nil : code
    }

    /// A language code an engine can load. Automatic uses English until a sentence arrives.
    nonisolated static func engineCode(for configured: String?) -> String {
        transcriptionLanguage(for: configured) ?? "en"
    }

    /// The language's name in English, for the prompt: "English", "Hindi".
    nonisolated static func englishName(for code: String) -> String {
        if isAutomatic(code) { return "the language they are speaking" }
        return Locale(identifier: "en").localizedString(forLanguageCode: code) ?? code
    }

    /// The one sentence that opens a hands-free session.
    nonisolated static func greetingInstructions(for code: String) -> String {
        if isAutomatic(code) {
            return "Greet the user in one short sentence as OpenClicky. Use English until they have spoken another language."
        }
        return "Greet the user in \(englishName(for: code)) in one short sentence as OpenClicky."
    }

    /// Added to every system prompt, Realtime and Claude alike.
    nonisolated static func instruction(for code: String) -> String {
        if isAutomatic(code) {
            return """
            Reply in the language of the user's latest clear sentence. One misheard word, noise, \
            or a fragment is not a language change: stay with the language of the last clear \
            sentence, and use English until there is one. If a turn is silent, only noise, or too \
            short to make out, do not invent what was said: say that you did not catch that and \
            ask them to say it again, in the language you are already using.
            """
        }
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
