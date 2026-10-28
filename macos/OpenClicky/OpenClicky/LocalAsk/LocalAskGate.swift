//
//  LocalAskGate.swift
//  OpenClicky
//
//  The cheap check in front of the on-device model (the local ask rung, see
//  docs/superpowers/specs/2026-10-27-on-device-ask-rung-design.md). A question or a how-to must
//  never pay for the model's latency, so the model is asked only when the request is short, in
//  English, and contains a word that commands (open, volume, pause…) or asks for a rewrite. The
//  gate is deliberately generous: its job is to keep the model off questions, not to classify.
//

import Foundation

enum LocalAskGateDecision: Equatable {
    /// Worth asking the model. `mentionsRewriting` means a writing target should be captured first.
    case tryModel(mentionsRewriting: Bool)
    case skip(reason: String)
}

enum LocalAskGate {

    static let maximumWords = 24

    /// Words that ask for one of the fast lane's actions. Matched as whole words or phrases.
    nonisolated static let commandPhrases: [String] = [
        "open", "launch", "start", "go to", "show me", "show", "reveal",
        "make a folder", "make a new folder", "create a folder", "create folder", "new folder", "folder",
        "volume", "louder", "quieter", "mute", "unmute", "turn it up", "turn it down", "turn up", "turn down",
        "play", "pause", "resume", "stop the music", "stop playing",
        "next song", "next track", "previous song", "previous track", "skip", "next", "previous", "back",
    ]

    /// Words that ask for the text in front of the person to be rewritten.
    nonisolated static let rewritePhrases: [String] = [
        "rephrase", "rewrite", "reword", "shorten", "shorter", "longer", "expand", "fix", "correct",
        "polish", "tidy", "proofread", "improve", "make it", "make this", "translate",
    ]

    /// Openers of a question or a how-to: these teach, they never act.
    nonisolated static let questionOpeners: [String] = [
        "how do i", "how do you", "how can i", "how to", "how would i", "how should i",
        "what", "why", "where is", "where are", "where do", "which", "who", "when",
        "can you show me how", "could you show me how", "show me how", "teach me", "explain", "tell me about",
    ]

    nonisolated static func decide(transcript: String, replyLanguageCode: String) -> LocalAskGateDecision {
        guard replyLanguageCode == "en" else { return .skip(reason: "not english") }
        let words = normalizedWords(of: transcript)
        guard !words.isEmpty, words.count <= maximumWords else { return .skip(reason: "too long") }
        let normalized = words.joined(separator: " ")
        for opener in questionOpeners where normalized == opener || normalized.hasPrefix(opener + " ") {
            return .skip(reason: "a question")
        }
        if containsAnyPhrase(normalized, phrases: rewritePhrases) { return .tryModel(mentionsRewriting: true) }
        if containsAnyPhrase(normalized, phrases: commandPhrases) { return .tryModel(mentionsRewriting: false) }
        return .skip(reason: "no command word")
    }

    /// Lowercased words with punctuation stripped, so "Open Safari, please!" reads as "open safari please".
    private nonisolated static func normalizedWords(of transcript: String) -> [String] {
        transcript.lowercased()
            .split(whereSeparator: { !$0.isLetter && !$0.isNumber && $0 != "'" })
            .map(String.init)
    }

    /// Whole-word matching: "open" matches "open safari", not "opening hours".
    private nonisolated static func containsAnyPhrase(_ normalized: String, phrases: [String]) -> Bool {
        let padded = " " + normalized + " "
        return phrases.contains { padded.contains(" " + $0 + " ") }
    }
}
