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

    /// The classification's budget with the pre-send pause on. Measured on an M2 Pro (2026-10-28):
    /// a warmed classification takes about 0.9 s, a cold one 1.0–1.5 s, the first of a session up
    /// to 1.7 s; the pause is 1.2 s. Two seconds lets a cold request through, and a command-shaped
    /// question that the model declines reaches the cloud at most 0.8 s after the pause would have
    /// ended (the person's call, recorded in the plan's ledger).
    static let defaultBudgetSeconds: TimeInterval = 2.0
    /// With the pause off the person chose speed: the model gets the pause's length and no more,
    /// so a cloud question is never more than 1.2 s later than today.
    static let budgetWithoutPauseSeconds: TimeInterval = AskSendPause.defaultSeconds
    /// A rewrite the model agreed to is a second call (0.5–1.4 s measured) and gets this much more;
    /// the cloud's own rewrite takes three to six seconds, so the wait is still the shorter one.
    static let rewriteBudgetSeconds: TimeInterval = 2.5
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

    /// The gate already passed. The classification returns within `budgetSeconds` and a rewrite
    /// within `rewriteBudgetSeconds` more; a model answer that arrives later is dropped, and a
    /// caller that was cancelled (esc, a new question) gets `.cloud("cancelled")`.
    func decide(transcript: String, writingTarget: WritingTarget?, budgetSeconds: TimeInterval,
                rewriteBudgetSeconds: TimeInterval = LocalAskRung.rewriteBudgetSeconds) async -> LocalAskDecision {
        let startedAt = Date()
        guard !Task.isCancelled else { return .cloud(reason: "cancelled") }

        let step = await raced(budgetSeconds: budgetSeconds, whenTimedOut: .decided(.cloud(reason: "timed out"))) {
            await self.classifyAndMap(transcript: transcript, writingTarget: writingTarget)
        }
        var decision: LocalAskDecision
        switch step {
        case .decided(let decided):
            decision = decided
        case .rewrite(let instruction, let writingTarget):
            guard !Task.isCancelled else { return .cloud(reason: "cancelled") }
            decision = await raced(budgetSeconds: rewriteBudgetSeconds, whenTimedOut: .cloud(reason: "timed out")) {
                await self.rewriteLeg(instruction: instruction, writingTarget: writingTarget)
            }
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

    /// What the classification settled, or the rewrite still to be made.
    private enum Step {
        case decided(LocalAskDecision)
        case rewrite(instruction: String, writingTarget: WritingTarget)
    }

    /// `work` against a timer: whichever finishes first wins. The loser is cancelled but never
    /// waited for: a task group would hold the result until every child returned, and a framework
    /// call that ignores cancellation (the model throttled in the background on battery) would then
    /// hold the cloud path hostage. The late result is dropped.
    private func raced<Result: Sendable>(budgetSeconds: TimeInterval, whenTimedOut: Result, _ work: @escaping @Sendable () async -> Result) async -> Result {
        let workTask = Task { await work() }
        let timerTask = Task { () -> Result in
            try? await Task.sleep(for: .seconds(budgetSeconds))
            return whenTimedOut
        }
        let finishLine = RaceFinishLine<Result>()
        let winner = await withCheckedContinuation { (continuation: CheckedContinuation<Result, Never>) in
            Task { await finishLine.offer(await workTask.value, to: continuation) }
            Task { await finishLine.offer(await timerTask.value, to: continuation) }
        }
        workTask.cancel()
        timerTask.cancel()
        return winner
    }

    private func classifyAndMap(transcript: String, writingTarget: WritingTarget?) async -> Step {
        do {
            let intent = try await model.classify(transcript: transcript)
            let mapping = LocalAskIntentMapper.map(intent, transcript: transcript, hasWritingTarget: writingTarget != nil, resolveApp: resolveApp)
            switch mapping {
            case .quickAction(let toolName, let arguments):
                return .decided(.quickAction(toolName: toolName, arguments: arguments))
            case .cloud(let reason):
                return .decided(.cloud(reason: reason))
            case .rewrite(let instruction):
                guard let writingTarget else { return .decided(.cloud(reason: "no text to rewrite")) }
                // A box whose text cannot be read (Apple Mail's body) has nothing for a text-only model.
                guard writingTarget.source != .screenOnly else { return .decided(.cloud(reason: "no readable text")) }
                guard writingTarget.text.count <= Self.maximumRewriteCharacters else { return .decided(.cloud(reason: "text too long")) }
                return .rewrite(instruction: instruction, writingTarget: writingTarget)
            }
        } catch {
            if !(error is CancellationError) { log("local rung: \(model.displayName) failed: \(error.localizedDescription)") }
            return .decided(.cloud(reason: "error"))
        }
    }

    private func rewriteLeg(instruction: String, writingTarget: WritingTarget) async -> LocalAskDecision {
        do {
            let rewritten = try await model.rewrite(writingTarget.text, instruction: instruction)
            if let rejectionReason = LocalRewriteCheck.rejectionReason(original: writingTarget.text, rewritten: rewritten) {
                // "Fix the grammar" on text with nothing to fix comes back unchanged, and that is
                // the right answer; an unchanged "warmer" is the model not doing the job.
                if rejectionReason == "unchanged", LocalRewriteCheck.isCorrectionInstruction(instruction) { return .textAlreadyReadsWell }
                return .cloud(reason: "rewrite: \(rejectionReason)")
            }
            return .rewrite(newText: rewritten)
        } catch {
            if !(error is CancellationError) { log("local rung: \(model.displayName) failed: \(error.localizedDescription)") }
            return .cloud(reason: "error")
        }
    }
}

/// Resumes a race's continuation exactly once, with whichever result arrives first.
private actor RaceFinishLine<Result: Sendable> {
    private var hasFinished = false

    func offer(_ result: Result, to continuation: CheckedContinuation<Result, Never>) {
        guard !hasFinished else { return }
        hasFinished = true
        continuation.resume(returning: result)
    }
}
