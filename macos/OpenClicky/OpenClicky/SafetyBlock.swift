//
//  SafetyBlock.swift
//  OpenClicky
//
//  A question, or its answer, that the provider's own safety filter stopped. Asking again gets the
//  same block, so it is not "something went wrong, please ask again": the ask lane says plainly that
//  a filter stopped it and offers to explain the ideas behind it (`safetyBlockSentence`). Mirrored on
//  Windows in OpenClicky.Core/Answers/SafetyBlock.cs.
//

import Foundation

/// The provider's safety filter stopped the question or its answer. `reason` is the provider's own
/// word for it ("Gemini SAFETY", "content_filter", "refusal"), for the log only.
nonisolated struct SafetyBlockError: Error, Equatable, Sendable {
    let reason: String
}

nonisolated enum SafetyBlock {
    /// Gemini's `finishReason`s that mean its filters ended the answer. RECITATION (it would have
    /// quoted a source word for word) and MAX_TOKENS are not safety, and neither is a plain STOP.
    private static let geminiFinishReasons: Set<String> = ["SAFETY", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "IMAGE_SAFETY"]

    /// Gemini's `finishReason` stopped the answer for safety.
    static func isGeminiSafetyFinish(_ finishReason: String) -> Bool { geminiFinishReasons.contains(finishReason) }

    /// Anthropic's `stop_reason` when its safety classifier stopped the answer.
    static let anthropicStopReason = "refusal"

    /// OpenAI's (and compatible services') `finish_reason` when its content filter stopped the answer.
    static let openAIFinishReason = "content_filter"

    /// What the person hears. It is also remembered as the reply, so a "why?" or a "yes" after it is
    /// answered knowing what happened.
    static let sentence = "the ai i use has a safety filter, and it stopped that answer. i can still explain the ideas behind it, if you'd like."
}
