//
//  TakeFormatter.swift
//  OpenClicky
//
//  Raw transcript → the text that is pasted. Two passes: local rules that always run (spoken
//  shortcuts, dictionary terms, fillers, sentence case) and, when a model is configured and the
//  style asks for it, one chat completion with the style's rules. The local pass is pure and
//  tested; a model that fails or times out leaves the local result, marked degraded.
//

import Foundation

/// Everything the formatter knows about one take besides its words.
struct TakeFormattingContext: Equatable {
    var style: DictationStyle
    var dictionary: [DictionaryTerm]
    var shortcuts: [SpokenShortcut]
    var appName: String?
    var language: DictationLanguage
    var script: DictationScript
    /// Captions read near the cursor, for spelling.
    var nearbyTerms: [String] = []
}

struct FormattedTake: Equatable {
    var text: String
    /// A spoken shortcut matched: the text is its replacement, and the model pass is skipped.
    var expandedShortcut: SpokenShortcut?
    /// The model pass was wanted but did not happen (no model, an error, a timeout).
    var formattingDegraded: Bool
}

/// A model that can rewrite a take; Sarvam chat or the OpenClicky backend behind it.
protocol TakePolisher: Sendable {
    var displayName: String { get }
    func polish(system: String, user: String) async throws -> String
}

enum TakeFormatter {

    static let modelTimeoutSeconds: TimeInterval = 8

    /// Only the unmistakable ones: "ah", "er", "eh" and "mm" are words in romanised Indian languages.
    static let fillers: Set<String> = ["um", "umm", "uh", "uhh", "erm", "hmm"]

    // MARK: local pass

    /// The rules-only result. Deterministic; no network.
    static func formatLocally(_ raw: String, context: TakeFormattingContext) -> FormattedTake {
        let collapsed = collapseWhitespace(raw)
        guard !collapsed.isEmpty else { return FormattedTake(text: "", expandedShortcut: nil, formattingDegraded: false) }

        if let shortcut = matchingShortcut(for: collapsed, in: context.shortcuts) {
            return FormattedTake(text: shortcut.replacement, expandedShortcut: shortcut, formattingDegraded: false)
        }

        var text = collapsed
        if context.style.removeFillers { text = removeFillers(from: text) }
        text = applyDictionary(to: text, terms: context.dictionary)
        if context.style.sentenceCase { text = sentenceCased(text) }
        return FormattedTake(text: text, expandedShortcut: nil, formattingDegraded: false)
    }

    /// A take that *is* a trigger, ignoring case, surrounding punctuation and extra spaces.
    static func matchingShortcut(for text: String, in shortcuts: [SpokenShortcut]) -> SpokenShortcut? {
        let spoken = normalizedForMatching(text)
        guard !spoken.isEmpty else { return nil }
        return shortcuts.first { normalizedForMatching($0.trigger) == spoken }
    }

    /// Lowercase words only: "My sign-off." and "my sign off" are the same trigger.
    static func normalizedForMatching(_ text: String) -> String {
        text.lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    /// Replaces each heard alias (and the written form itself, case-insensitively) with the written
    /// form, whole words only, longest alias first so "aditya kshatriya" wins over "aditya".
    static func applyDictionary(to text: String, terms: [DictionaryTerm]) -> String {
        var result = text
        let replacements: [(alias: String, written: String)] = terms.flatMap { term in
            ([term.written] + term.heardAs).map { (alias: $0, written: term.written) }
        }
        .filter { !$0.alias.trimmingCharacters(in: .whitespaces).isEmpty }
        .sorted { $0.alias.count > $1.alias.count }
        for replacement in replacements {
            let pattern = "(?<![\\p{L}\\p{N}])" + NSRegularExpression.escapedPattern(for: replacement.alias) + "(?![\\p{L}\\p{N}])"
            guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { continue }
            let range = NSRange(result.startIndex..., in: result)
            result = regex.stringByReplacingMatches(in: result, range: range, withTemplate: NSRegularExpression.escapedTemplate(for: replacement.written))
        }
        return result
    }

    /// Drops filler words wherever they stand, and the comma that often follows one ("um, so" → "so").
    static func removeFillers(from text: String) -> String {
        let pattern = "(?<![\\p{L}\\p{N}])(" + fillers.sorted().map(NSRegularExpression.escapedPattern(for:)).joined(separator: "|") + ")(?![\\p{L}\\p{N}])[,]?\\s*"
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return text }
        let stripped = regex.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "")
        return collapseWhitespace(stripped)
            .replacingOccurrences(of: " ,", with: ",")
            .replacingOccurrences(of: " .", with: ".")
    }

    /// A capital letter after each sentence end. No punctuation is added: the engines punctuate,
    /// and "git status" must land in a terminal as spoken.
    static func sentenceCased(_ text: String) -> String {
        var characters = Array(text)
        var atSentenceStart = true
        for index in characters.indices {
            let character = characters[index]
            if atSentenceStart, character.isLetter {
                characters[index] = Character(String(character).uppercased())
                atSentenceStart = false
            } else if character.isLetter || character.isNumber {
                atSentenceStart = false
            }
            if ".!?".contains(character) || character == "\n" { atSentenceStart = true }
        }
        return String(characters)
    }

    static func collapseWhitespace(_ text: String) -> String {
        text.split(whereSeparator: { $0 == " " || $0 == "\t" }).joined(separator: " ")
            .replacingOccurrences(of: " \n", with: "\n")
            .replacingOccurrences(of: "\n ", with: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: model pass

    /// The local pass, then the model's rewrite when there is one to ask and the style wants it.
    static func format(_ raw: String, context: TakeFormattingContext, polisher: (any TakePolisher)?, wantsModel: Bool) async -> FormattedTake {
        var local = formatLocally(raw, context: context)
        guard local.expandedShortcut == nil, !local.text.isEmpty, wantsModel, context.style.polishWithModel else { return local }
        guard let polisher else {
            local.formattingDegraded = true
            return local
        }
        let system = systemPrompt(for: context)
        do {
            let polished = try await withTimeout(seconds: modelTimeoutSeconds) {
                try await polisher.polish(system: system, user: raw)
            }
            let cleaned = unwrapModelAnswer(polished)
            guard !cleaned.isEmpty, looksLikeTheSameTake(cleaned, raw: raw) else {
                local.formattingDegraded = true
                return local
            }
            return FormattedTake(text: cleaned, expandedShortcut: nil, formattingDegraded: false)
        } catch {
            AppLog.append("take formatter: \(polisher.displayName) failed: \(error.localizedDescription)")
            local.formattingDegraded = true
            return local
        }
    }

    static func systemPrompt(for context: TakeFormattingContext) -> String {
        var lines: [String] = []
        lines.append("You clean up dictated speech into written text. Return only the cleaned text, nothing else: no preamble, no quotes, no explanation, no markdown fences.")
        lines.append("Keep the speaker's meaning, words and order. Fix punctuation, capitalisation and obvious speech-to-text errors; remove fillers, false starts and self-corrections (keep the corrected version). Do not add content, do not answer the text, do not translate unless told to.")
        lines.append("Write numbers the way the context expects (\"3 kg\", \"3 PM\", \"2026\"). Spoken formatting words become formatting: \"new line\", \"new paragraph\", \"comma\", \"full stop\".")
        lines.append("Style — \(context.style.name): \(context.style.rules)")
        if let appName = context.appName { lines.append("The text is going into \(appName).") }
        if let code = context.language.bareCode {
            lines.append("The language is \(context.language.name) (\(code)). Write it in \(context.script == .roman ? "roman letters (latin script), the way people type it on a phone" : "its native script").")
        } else if context.script == .roman {
            lines.append("If the speech is in an Indian language, write it in roman letters (latin script).")
        }
        if !context.dictionary.isEmpty {
            let terms = context.dictionary.map { term in
                term.heardAs.isEmpty ? "\"\(term.written)\"" : "\"\(term.written)\" (heard as \(term.heardAs.map { "\"\($0)\"" }.joined(separator: ", ")))"
            }
            lines.append("Spell these exactly as written: " + terms.joined(separator: "; ") + ".")
        }
        if !context.nearbyTerms.isEmpty {
            lines.append("Names visible on screen, for spelling: " + context.nearbyTerms.prefix(40).joined(separator: ", ") + ".")
        }
        return lines.joined(separator: "\n")
    }

    /// Models wrap answers in quotes or fences now and then; the take never wanted them.
    static func unwrapModelAnswer(_ answer: String) -> String {
        var text = answer.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("```") {
            text = text.split(separator: "\n").dropFirst().joined(separator: "\n")
            if text.hasSuffix("```") { text = String(text.dropLast(3)) }
            text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if text.count >= 2, text.hasPrefix("\""), text.hasSuffix("\"") {
            text = String(text.dropFirst().dropLast())
        }
        return text
    }

    /// A rewrite that is wildly longer or shorter than the words spoken is an answer or a refusal,
    /// not a cleanup; the local result is safer then.
    static func looksLikeTheSameTake(_ polished: String, raw: String) -> Bool {
        let rawWords = max(1, raw.split(whereSeparator: { $0.isWhitespace }).count)
        let polishedWords = polished.split(whereSeparator: { $0.isWhitespace }).count
        if rawWords <= 3 { return polishedWords <= rawWords + 4 }
        let ratio = Double(polishedWords) / Double(rawWords)
        return ratio >= 0.4 && ratio <= 1.8
    }

    private struct TimeoutError: Error {}

    private static func withTimeout<T: Sendable>(seconds: TimeInterval, _ work: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await work() }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                throw TimeoutError()
            }
            guard let first = try await group.next() else { throw TimeoutError() }
            group.cancelAll()
            return first
        }
    }
}

/// Polishes through Sarvam's chat completions with the user's key.
struct SarvamTakePolisher: TakePolisher {
    let client: SarvamSpeechClient
    var displayName: String { "Sarvam" }
    func polish(system: String, user: String) async throws -> String {
        try await client.complete(system: system, user: user)
    }
}

/// Polishes through the OpenClicky backend's OpenAI-shaped chat route.
struct BackendTakePolisher: TakePolisher {
    var displayName: String { "OpenClicky" }
    func polish(system: String, user: String) async throws -> String {
        guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/v1/chat/completions") else {
            throw SarvamSpeechError.unreachable("backend URL is invalid")
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&request)
        let body: [String: Any] = [
            "model": "gpt-4o-mini",
            "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
            "temperature": 0.2,
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw SarvamSpeechError.refused(status: (response as? HTTPURLResponse)?.statusCode ?? 0, message: String(decoding: data.prefix(200), as: UTF8.self))
        }
        guard let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let choices = answer["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String else {
            throw SarvamSpeechError.unreadable("there was no message in it")
        }
        return content
    }
}
