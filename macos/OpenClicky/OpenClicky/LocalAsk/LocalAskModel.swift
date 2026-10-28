//
//  LocalAskModel.swift
//  OpenClicky
//
//  The small model the local ask rung talks to, behind a protocol so the rung's decisions are
//  unit-tested with a fake (CI has no model) and so another on-device model could take the
//  place of Apple's. The one real implementation is `AppleLocalAskModel` (Foundation Models,
//  macOS 26 with Apple Intelligence on).
//

import Foundation

protocol LocalAskModel: Sendable {
    var displayName: String { get }
    /// Sorts the request into a `LocalAskIntent`. Throws when the model refuses, is throttled or
    /// is gone; the rung treats every error as "the cloud answers".
    func classify(transcript: String) async throws -> LocalAskIntent
    /// Rewrites `text` as `instruction` asks; returns only the new text.
    func rewrite(_ text: String, instruction: String) async throws -> String
    /// Loads what the next call needs. Never throws, never blocks the caller.
    func prewarm()
}

enum LocalAskModelAvailability {

    /// nil when Apple's model can answer in English on this Mac; otherwise the polisher's sentence
    /// ("apple intelligence is off", "needs macos 26", …), which Settings shows.
    static func unavailableReason() -> String? {
        if AppleModelTakePolisherAvailability.isAvailable(languageCode: "en") { return nil }
        return AppleModelTakePolisherAvailability.unavailableReason() ?? "apple's model is unavailable"
    }

    /// Apple's model on macOS 26 when it is available; nil on every other Mac.
    static func makeModel() -> (any LocalAskModel)? {
        #if canImport(FoundationModels)
        if #available(macOS 26, *), unavailableReason() == nil {
            return AppleLocalAskModel()
        }
        #endif
        return nil
    }
}
