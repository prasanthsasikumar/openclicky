//
//  LocalRewriteCheck.swift
//  OpenClicky
//
//  Whether a rewrite from the on-device model may be pasted over the person's text. A small model
//  sometimes answers the text instead of rewriting it, returns it unchanged, or drops most of it;
//  any of those goes to the cloud instead, silently, rather than landing in an email.
//

import Foundation

enum LocalRewriteCheck {

    /// How a model begins when it is talking to you rather than rewriting. "i can" alone is left
    /// out: "I can send the report tomorrow" is a perfectly good rewrite.
    static let answeringOpeners = [
        "sure", "here is", "here's", "here is a", "certainly", "of course", "i can help", "i can do that",
        "i cannot", "i can't help", "i'm sorry", "i am sorry", "as an ai", "okay,", "okay!", "ok,", "ok!",
    ]

    /// Below this many characters the original is short enough that "make it longer" may grow it
    /// well past three times its length.
    static let shortOriginalCharacters = 40
    static let shortOriginalAllowance = 200

    /// Instructions that ask for corrections, where unchanged text means there was nothing to correct.
    static let correctionWords = ["fix", "correct", "grammar", "spelling", "polish", "proofread", "tidy", "typo", "typos", "punctuation"]

    nonisolated static func isCorrectionInstruction(_ instruction: String) -> Bool {
        let words = instruction.lowercased().split(whereSeparator: { !$0.isLetter }).map(String.init)
        return words.contains { correctionWords.contains($0) }
    }

    /// nil when the rewrite may be pasted; otherwise why not.
    nonisolated static func rejectionReason(original: String, rewritten: String) -> String? {
        let trimmedOriginal = original.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedRewrite = rewritten.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedRewrite.isEmpty else { return "empty" }
        let loweredRewrite = trimmedRewrite.lowercased()
        if answeringOpeners.contains(where: { loweredRewrite.hasPrefix($0) }) { return "answered instead of rewrote" }
        guard trimmedRewrite != trimmedOriginal else { return "unchanged" }
        let originalCount = trimmedOriginal.count
        let rewriteCount = trimmedRewrite.count
        if rewriteCount * 3 < originalCount { return "too short" }
        let longestAllowed = originalCount < shortOriginalCharacters ? max(shortOriginalAllowance, originalCount * 3) : originalCount * 3
        if rewriteCount > longestAllowed { return "too long" }
        return nil
    }
}
