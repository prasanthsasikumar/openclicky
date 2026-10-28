//
//  LocalAskRung.swift
//  OpenClicky
//
//  The local ask rung: before the ask lane sends a screenshot to a cloud model, the on-device
//  model gets one short chance to recognise a quick action or a rewrite. Gate → model → mapper,
//  under a time budget (the pre-send pause when it is on), with every error, timeout and unmapped
//  answer meaning "the cloud answers, as it always did". Design:
//  docs/superpowers/specs/2026-10-27-on-device-ask-rung-design.md.
//

import Foundation

enum LocalAskDecision: Equatable {
    /// Run this fast-lane tool with these arguments, then speak the outcome.
    case quickAction(toolName: String, arguments: [String: String])
    /// Write this over the writing target, then speak the outcome.
    case rewrite(newText: String)
    /// Asked to fix or polish text that came back word for word: nothing to paste, say so.
    case textAlreadyReadsWell
    /// The ordinary path, with its pause and screenshot; `reason` is for the log.
    case cloud(reason: String)
}

struct LocalAskRung {

    /// The model's budget. Measured on an M2 Pro (2026-10-28): a warmed classification takes about
    /// 0.9 s, a cold one 1.0–1.7 s; the pre-send pause is 1.2 s, so the budget is the longer of the
    /// two and a command-shaped question costs the cloud path at most 0.3 s more than the pause.
    static let defaultBudgetSeconds: TimeInterval = 1.5
    /// The longest text the on-device model rewrites: a long email, within its 4,096-token session.
    static let maximumRewriteCharacters = 2_500

    let model: any LocalAskModel
    /// The installed app for a spoken name (`InstalledAppResolver`), injected so tests need no disk.
    var resolveApp: @Sendable (String) -> String?
    var log: @Sendable (String) -> Void = { AppLog.append($0) }

    /// The keyword gate (`LocalAskGate`): nil-cost, run before anything else.
    nonisolated static func gate(transcript: String, replyLanguageCode: String) -> LocalAskGateDecision {
        LocalAskGate.decide(transcript: transcript, replyLanguageCode: replyLanguageCode)
    }

    /// The gate already passed. Returns within `budgetSeconds`; a model answer that arrives later is
    /// dropped, and a caller that was cancelled (esc, a new question) gets `.cloud("cancelled")`.
    func decide(transcript: String, writingTarget: WritingTarget?, budgetSeconds: TimeInterval) async -> LocalAskDecision {
        let startedAt = Date()
        guard !Task.isCancelled else { return .cloud(reason: "cancelled") }

        let decision = await withTaskGroup(of: LocalAskDecision.self) { group in
            group.addTask { await self.decideWithTheModel(transcript: transcript, writingTarget: writingTarget) }
            group.addTask {
                try? await Task.sleep(for: .seconds(budgetSeconds))
                return .cloud(reason: "timed out")
            }
            let first = await group.next() ?? .cloud(reason: "error")
            group.cancelAll()
            return first
        }
        guard !Task.isCancelled else { return .cloud(reason: "cancelled") }

        let seconds = String(format: "%.2f", Date().timeIntervalSince(startedAt))
        switch decision {
        case .quickAction(let toolName, _): log("local rung: \(toolName) in \(seconds) s")
        case .rewrite(let newText): log("local rung: rewrite \(writingTarget?.text.count ?? 0)→\(newText.count) chars in \(seconds) s")
        case .textAlreadyReadsWell: log("local rung: rewrite unchanged, already reads well in \(seconds) s")
        case .cloud(let reason): log("local rung: cloud (\(reason)) in \(seconds) s")
        }
        return decision
    }

    private func decideWithTheModel(transcript: String, writingTarget: WritingTarget?) async -> LocalAskDecision {
        do {
            let intent = try await model.classify(transcript: transcript)
            let mapping = LocalAskIntentMapper.map(intent, hasWritingTarget: writingTarget != nil, resolveApp: resolveApp)
            switch mapping {
            case .quickAction(let toolName, let arguments):
                return .quickAction(toolName: toolName, arguments: arguments)
            case .cloud(let reason):
                return .cloud(reason: reason)
            case .rewrite(let instruction):
                guard let writingTarget else { return .cloud(reason: "no text to rewrite") }
                // A box whose text cannot be read (Apple Mail's body) has nothing for a text-only model.
                guard writingTarget.source != .screenOnly else { return .cloud(reason: "no readable text") }
                guard writingTarget.text.count <= Self.maximumRewriteCharacters else { return .cloud(reason: "text too long") }
                let rewritten = try await model.rewrite(writingTarget.text, instruction: instruction)
                if let rejectionReason = LocalRewriteCheck.rejectionReason(original: writingTarget.text, rewritten: rewritten) {
                    // "Fix the grammar" on text with nothing to fix comes back unchanged, and that is
                    // the right answer; an unchanged "warmer" is the model not doing the job.
                    if rejectionReason == "unchanged", LocalRewriteCheck.isCorrectionInstruction(instruction) { return .textAlreadyReadsWell }
                    return .cloud(reason: "rewrite: \(rejectionReason)")
                }
                return .rewrite(newText: rewritten)
            }
        } catch {
            if !(error is CancellationError) { log("local rung: \(model.displayName) failed: \(error.localizedDescription)") }
            return .cloud(reason: "error")
        }
    }
}
