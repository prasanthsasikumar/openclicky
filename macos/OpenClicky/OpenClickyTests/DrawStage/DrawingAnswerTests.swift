//
//  DrawingAnswerTests.swift
//  OpenClickyTests
//
//  One answer drawn while it is spoken, end to end against a fake page and a fake voice: the
//  program streams in, each line is spoken in order with its beat drawn between start and end, and
//  every way it can fall through (NONE, prose, a page that never starts, a voice that fails, esc)
//  leaves the question for the ordinary lane with nothing said.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct DrawingAnswerTests {
    private let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
    private let twoBeats = "```js\nstep(1); say(\"a\")\nrect(1, 2, 3, 4)\nstep(2); say(\"b\")\ncircle(5, 6, 7)\n```"

    /// Streams `reply` in pieces of `pieceLength` characters, `pauseBeforeChunkSeconds` between them.
    private func streamed(_ reply: String, pieceLength: Int = 12, pauseBeforeChunkSeconds: [Int: Double] = [:])
        -> (@escaping @MainActor (String) -> Void) async throws -> String {
        { onChunk in
            var soFar = ""
            var index = 0
            while soFar.count < reply.count {
                if let pause = pauseBeforeChunkSeconds[index] { try await Task.sleep(nanoseconds: UInt64(pause * 1_000_000_000)) }
                soFar = String(reply.prefix(min(reply.count, soFar.count + pieceLength)))
                onChunk(soFar)
                index += 1
                try await Task.sleep(nanoseconds: 2_000_000)
            }
            return reply
        }
    }

    private func run(page: FakeDrawStagePage, reply: @escaping (@escaping @MainActor (String) -> Void) async throws -> String,
                     speech: SpeechQueue, holdLine: String? = nil, holdLineAfter: TimeInterval = 2, readinessTimeout: TimeInterval = 5,
                     followUp: DrawnAnswer? = nil, control: DrawingAnswerControl = DrawingAnswerControl(),
                     stopVoiceNow: @escaping () -> Void = {}, onInterrupted: ((String, [String]) -> Void)? = nil,
                     log: @escaping (String) -> Void = { _ in }) async throws -> DrawingAnswerResult {
        try await DrawingAnswer.run(stage: page.controller, displayFrame: screen, canvasWidth: 1280, canvasHeight: 800,
                                    writeReply: reply, speech: speech, onFirstAudio: {}, log: log,
                                    holdLine: holdLine, holdLineAfter: holdLineAfter, readinessTimeout: readinessTimeout, followUp: followUp,
                                    control: control, stopVoiceNow: stopVoiceNow, onInterrupted: onInterrupted)
    }

    /// Waits for a state instead of sleeping a fixed time, so the tests hold under the full suite's load.
    private func waitUntil(timeoutSeconds: Double = 4, _ condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeoutSeconds)
        while !condition() {
            if Date() > deadline { Issue.record("timed out waiting"); return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
    }

    // MARK: - Stopping: esc, a new question, a page that never starts

    @Test func stoppingByThePersonKeepsWhatWasDrawn() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        var voiceStops = 0
        let control = DrawingAnswerControl()
        // The reply streams slowly after the first line, so a stop lands mid-drawing.
        let slowReply = streamed(twoBeats, pieceLength: 30, pauseBeforeChunkSeconds: [1: 1.5, 2: 1.5])
        let running = Task { @MainActor in
            try await self.run(page: page, reply: slowReply, speech: FakeSpeech.make(played: { played.append($0) }),
                               control: control, stopVoiceNow: { voiceStops += 1 })
        }
        try await waitUntil { played.count == 1 }
        #expect(played == ["a"])
        control.stop()
        let result = try await running.value
        #expect(result.drew)
        #expect(result.spokenLines == ["a"])
        #expect(result.stoppedByThePerson)
        #expect(voiceStops == 1)
        #expect(page.surface.postedTypes.contains("finishNow"))
        #expect(!page.surface.postedTypes.contains("clear"))
        #expect(page.surface.isShowing)
    }

    @Test func stoppingBeforeAnythingWasDrawnClearsTheStageAndSaysNothing() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let control = DrawingAnswerControl()
        let neverEndingReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            onChunk("```js\n")
            try await Task.sleep(nanoseconds: 10_000_000_000)
            return ""
        }
        let running = Task { @MainActor in try await self.run(page: page, reply: neverEndingReply, speech: FakeSpeech.make(played: { played.append($0) }), control: control) }
        try await waitUntil { page.surface.postedTypes.contains("beginStream") }
        control.stop()
        let result = try await running.value
        #expect(!result.drew)
        #expect(result.stoppedByThePerson)
        #expect(played.isEmpty)
        #expect(page.surface.postedTypes.contains("clear"))
        #expect(!page.surface.isShowing)
    }

    @Test func cancellationAfterADrawnLineKeepsTheStageForAFollowUp() async throws {
        let page = FakeDrawStagePage()
        var interrupted: (program: String, lines: [String])?
        var played: [String] = []
        let slowReply = streamed(twoBeats, pieceLength: 30, pauseBeforeChunkSeconds: [1: 1.5, 2: 1.5])
        let running = Task { @MainActor in
            try await self.run(page: page, reply: slowReply, speech: FakeSpeech.make(played: { played.append($0) }),
                               onInterrupted: { program, lines in interrupted = (program, lines) })
        }
        try await waitUntil { played.count == 1 }
        running.cancel()
        do {
            _ = try await running.value
            Issue.record("expected a cancellation")
        } catch is CancellationError {
            // as expected
        }
        #expect(interrupted?.lines == ["a"])
        #expect(interrupted?.program.contains("say(\"a\")") == true)
        #expect(page.surface.postedTypes.contains("finishNow"))
        #expect(!page.surface.postedTypes.contains("clear"))
        #expect(page.surface.isShowing)
    }

    @Test func aReplyThatFailsAfterLinesWereSpokenStillCounts() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        struct VendorDown: Error {}
        let failingReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            onChunk("```js\nstep(1); say(\"a\")\nrect(1, 2, 3, 4)\n")
            try await Task.sleep(nanoseconds: 150_000_000)
            throw VendorDown()
        }
        let result = try await run(page: page, reply: failingReply, speech: FakeSpeech.make(played: { played.append($0) }))
        #expect(result.drew)
        #expect(result.spokenLines == ["a"])
        #expect(played == ["a"])
        #expect(!page.surface.postedTypes.contains("clear"))
    }

    @Test func aStageThatNeverStartsGivesUpAtOnceWithoutTheHoldLine() async throws {
        let page = FakeDrawStagePage(ready: false)
        var played: [String] = []
        var replyWasCancelled = false
        let slowReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            onChunk("```js\nstep(1); say(\"a\")\n")
            do { try await Task.sleep(nanoseconds: 3_000_000_000) } catch { replyWasCancelled = true; throw error }
            return twoBeats
        }
        let startedAt = Date()
        let result = try await run(page: page, reply: slowReply, speech: FakeSpeech.make(played: { played.append($0) }),
                                   holdLine: "One moment.", holdLineAfter: 0.05, readinessTimeout: 0.2)
        #expect(!result.drew)
        #expect(played.isEmpty)
        #expect(replyWasCancelled)
        #expect(Date().timeIntervalSince(startedAt) < 1.5)
    }

    @Test func noneLetsTheReplyEndOnItsOwn() async throws {
        let page = FakeDrawStagePage()
        var replyWasCancelled = false
        let noneReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            onChunk("NONE")
            do { try await Task.sleep(nanoseconds: 100_000_000) } catch { replyWasCancelled = true; throw error }
            return "NONE"
        }
        let result = try await run(page: page, reply: noneReply, speech: FakeSpeech.make(played: { _ in }))
        #expect(!result.drew)
        #expect(!replyWasCancelled)
    }

    @Test func aProgramIsDrawnAndSpokenLineByLine() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let result = try await run(page: page, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { played.append($0) }))
        #expect(result.drew)
        #expect(result.spokenLines == ["a", "b"])
        #expect(played == ["a", "b"])
        #expect(result.unspokenAfterVoiceFailure == nil)
        let types = page.surface.postedTypes
        #expect(types.first == "beginStream")
        #expect(types.filter { $0 == "beginStream" }.count == 1)
        let order = types.filter { $0.hasPrefix("line") }
        #expect(order == ["lineStarted", "lineEnded", "lineStarted", "lineEnded"])
        let feeds = page.surface.postedObjects(ofType: "feed")
        #expect(feeds.last?["done"] as? Bool == true)
        #expect(page.surface.isShowing)
    }

    @Test func noneFallsThroughBeforeAnythingIsSpoken() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let result = try await run(page: page, reply: streamed("NONE"), speech: FakeSpeech.make(played: { played.append($0) }))
        #expect(!result.drew)
        #expect(played.isEmpty)
        #expect(!page.surface.postedTypes.contains("beginStream"))
        #expect(!page.surface.isShowing)
    }

    @Test func proseFallsThroughToo() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let result = try await run(page: page, reply: streamed("It is 3 pm.\nNothing more.\n"), speech: FakeSpeech.make(played: { played.append($0) }))
        #expect(!result.drew)
        #expect(played.isEmpty)
        #expect(!page.surface.postedTypes.contains("beginStream"))
    }

    @Test func theHoldLineIsSaidOnlyAfterTheProgramStartsAndTheFirstLineIsSlow() async throws {
        let slowPage = FakeDrawStagePage()
        var slowPlayed: [String] = []
        // The fence arrives at once, the first say() only after 0.5 s: past a 0.2 s hold threshold.
        let slowReply = streamed(twoBeats, pieceLength: 4, pauseBeforeChunkSeconds: [1: 0.5])
        let slowResult = try await run(page: slowPage, reply: slowReply, speech: FakeSpeech.make(played: { slowPlayed.append($0) }),
                                       holdLine: "One moment.", holdLineAfter: 0.2)
        #expect(slowResult.drew)
        #expect(slowPlayed == ["One moment.", "a", "b"])
        #expect(slowResult.spokenLines == ["a", "b"])

        let quickPage = FakeDrawStagePage()
        var quickPlayed: [String] = []
        _ = try await run(page: quickPage, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { quickPlayed.append($0) }),
                          holdLine: "One moment.", holdLineAfter: 2)
        #expect(quickPlayed == ["a", "b"])
    }

    @Test func aStageThatNeverReportsReadyFallsThroughAfterTheTimeout() async throws {
        let page = FakeDrawStagePage(ready: false)
        var played: [String] = []
        let startedAt = Date()
        let result = try await run(page: page, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { played.append($0) }), readinessTimeout: 0.2)
        #expect(!result.drew)
        #expect(played.isEmpty)
        #expect(Date().timeIntervalSince(startedAt) < 1.5)
    }

    @Test func aVoiceFailureFinishesTheDrawingAndHandsBackTheRest() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let result = try await run(page: page, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { played.append($0) }, failOn: "b"))
        #expect(result.drew)
        #expect(played == ["a"])
        #expect(result.unspokenAfterVoiceFailure == ["b"])
        #expect(page.surface.postedTypes.contains("finishNow"))
    }

    @Test func aFollowUpThatKeepsContinuesTheDrawing() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let drawing = DrawnAnswer(questions: ["how does it work?"], program: "step(1); say(\"a\")", canvasWidth: 1280, canvasHeight: 800, spokenLines: ["a"])
        let result = try await run(page: page, reply: streamed("keep()\nstep(2); say(\"c\")\n"), speech: FakeSpeech.make(played: { played.append($0) }), followUp: drawing)
        #expect(result.drew)
        #expect(result.keptTheDrawing)
        #expect(page.surface.postedTypes.contains("continueStream"))
        #expect(!page.surface.postedTypes.contains("beginStream"))
        #expect(played == ["c"])
    }

    @Test func aFollowUpWithoutKeepDrawsAfresh() async throws {
        let page = FakeDrawStagePage()
        let drawing = DrawnAnswer(questions: ["how does it work?"], program: "step(1); say(\"a\")", canvasWidth: 1280, canvasHeight: 800, spokenLines: ["a"])
        let result = try await run(page: page, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { _ in }), followUp: drawing)
        #expect(result.drew)
        #expect(!result.keptTheDrawing)
        #expect(page.surface.postedTypes.contains("beginStream"))
    }

    @Test func aCutOffProgramStillFinishes() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let cutOff = "```js\nstep(1); say(\"a\")\nrect(1, 2, 3, 4)\nstep(2); say(\"b\")\nrect(10,"
        let result = try await run(page: page, reply: streamed(cutOff), speech: FakeSpeech.make(played: { played.append($0) }))
        #expect(result.drew)
        #expect(result.spokenLines == ["a", "b"])
        #expect(played == ["a", "b"])
        #expect(page.surface.postedObjects(ofType: "feed").last?["done"] as? Bool == true)
    }

    @Test func cancellingBeforeTheFirstLineClearsTheStage() async throws {
        let page = FakeDrawStagePage()
        var played: [String] = []
        let speech = FakeSpeech.make(played: { played.append($0) })
        let neverEndingReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            onChunk("```js\n")
            try await Task.sleep(nanoseconds: 10_000_000_000)
            return ""
        }
        let running = Task { @MainActor in try await self.run(page: page, reply: neverEndingReply, speech: speech) }
        try await Task.sleep(nanoseconds: 50_000_000)
        running.cancel()
        do {
            _ = try await running.value
            Issue.record("expected a cancellation")
        } catch is CancellationError {
            // as expected
        } catch {
            Issue.record("unexpected error \(error)")
        }
        #expect(played.isEmpty)
        #expect(page.surface.postedTypes.contains("clear"))
        #expect(!page.surface.isShowing)
    }

    @Test func aPageErrorIsLoggedNotFatal() async throws {
        let page = FakeDrawStagePage()
        page.errorAfterFirstLine = "x is not defined"
        var logged: [String] = []
        let result = try await run(page: page, reply: streamed(twoBeats), speech: FakeSpeech.make(played: { _ in }), log: { logged.append($0) })
        #expect(result.drew)
        #expect(result.spokenLines == ["a", "b"])
        #expect(logged.contains("page: error: x is not defined"))
    }
}
