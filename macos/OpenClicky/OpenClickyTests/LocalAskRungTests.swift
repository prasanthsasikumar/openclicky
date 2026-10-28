//
//  LocalAskRungTests.swift
//  OpenClickyTests
//
//  The rung's decisions with a fake model: a mapped quick action, a decline, a slow model that
//  loses to the budget, a failing model, rewrites that pass or fail the check, and a cancelled
//  caller. CI has no on-device model; nothing here needs one.
//

import Foundation
import Testing
@testable import OpenClicky

private struct FakeModel: LocalAskModel {
    var displayName: String { "fake" }
    var intent: LocalAskIntent = LocalAskIntent(kind: .needsCloud)
    var rewritten: String = ""
    var delaySeconds: TimeInterval = 0
    var error: Error?
    func classify(transcript: String) async throws -> LocalAskIntent {
        if delaySeconds > 0 { try await Task.sleep(for: .seconds(delaySeconds)) }
        if let error { throw error }
        return intent
    }
    func rewrite(_ text: String, instruction: String) async throws -> String {
        if let error { throw error }
        return rewritten
    }
    func prewarm() {}
}

private struct Boom: Error {}

private func rung(_ model: FakeModel) -> LocalAskRung {
    LocalAskRung(model: model, resolveApp: { $0 }, log: { _ in })
}

private let target = WritingTarget(scope: .selection, text: "Hi John, I wanted to check whether you could send the report by tomorrow afternoon.", appBundleID: nil, appName: "Mail")

struct LocalAskRungTests {

    @Test func aQuickActionComesBackMapped() async {
        let decision = await rung(FakeModel(intent: LocalAskIntent(kind: .openApp, name: "Safari"))).decide(transcript: "open safari", writingTarget: nil, budgetSeconds: 1)
        #expect(decision == .quickAction(toolName: "open_app", arguments: ["name": "Safari"]))
    }

    @Test func theModelDecliningIsCloud() async {
        #expect(await rung(FakeModel()).decide(transcript: "open it", writingTarget: nil, budgetSeconds: 1) == .cloud(reason: "model declined"))
    }

    @Test func aSlowModelIsCloudWithinTheBudget() async {
        let started = Date()
        let decision = await rung(FakeModel(intent: LocalAskIntent(kind: .openApp, name: "Safari"), delaySeconds: 5)).decide(transcript: "open safari", writingTarget: nil, budgetSeconds: 0.2)
        #expect(decision == .cloud(reason: "timed out"))
        #expect(Date().timeIntervalSince(started) < 1.0)
    }

    @Test func aFailingModelIsCloud() async {
        #expect(await rung(FakeModel(error: Boom())).decide(transcript: "open safari", writingTarget: nil, budgetSeconds: 1) == .cloud(reason: "error"))
    }

    @Test func aRewriteIsReturnedWhenItPassesTheCheck() async {
        let model = FakeModel(intent: LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), rewritten: "Hi John, could you send the report by tomorrow afternoon?")
        #expect(await rung(model).decide(transcript: "make this shorter", writingTarget: target, budgetSeconds: 2) == .rewrite(newText: "Hi John, could you send the report by tomorrow afternoon?"))
    }

    @Test func aRewriteThatAnsweredIsCloud() async {
        let model = FakeModel(intent: LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), rewritten: "Sure! Here is a shorter version: Hi John, send it tomorrow.")
        #expect(await rung(model).decide(transcript: "make this shorter", writingTarget: target, budgetSeconds: 2) == .cloud(reason: "rewrite: answered instead of rewrote"))
    }

    @Test func aScreenOnlyTargetIsCloud() async {
        var screenOnly = target
        screenOnly.source = .screenOnly
        let model = FakeModel(intent: LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), rewritten: "x")
        #expect(await rung(model).decide(transcript: "make this shorter", writingTarget: screenOnly, budgetSeconds: 2) == .cloud(reason: "no readable text"))
    }

    @Test func aLongTargetIsCloud() async {
        let long = WritingTarget(scope: .wholeField, text: String(repeating: "word ", count: 600), appBundleID: nil, appName: nil)
        let model = FakeModel(intent: LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), rewritten: "x")
        #expect(await rung(model).decide(transcript: "make this shorter", writingTarget: long, budgetSeconds: 2) == .cloud(reason: "text too long"))
    }

    @Test func aCancelledCallerGetsCloud() async {
        let task = Task {
            await rung(FakeModel(intent: LocalAskIntent(kind: .openApp, name: "Safari"), delaySeconds: 0.5)).decide(transcript: "open safari", writingTarget: nil, budgetSeconds: 2)
        }
        task.cancel()
        #expect(await task.value == .cloud(reason: "cancelled"))
    }

    @Test func theGateIsTheKeywordGate() {
        #expect(LocalAskRung.gate(transcript: "open safari", replyLanguageCode: "en") == .tryModel(mentionsRewriting: false))
        #expect(LocalAskRung.gate(transcript: "how do i open safari", replyLanguageCode: "en") == .skip(reason: "a question"))
    }
}
