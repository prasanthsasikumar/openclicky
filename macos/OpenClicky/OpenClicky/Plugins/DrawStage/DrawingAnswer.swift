//
//  DrawingAnswer.swift
//  OpenClicky
//
//  One answer drawn on the stage while it is spoken: the model's reply streams in, its program is
//  fed to the stage as it grows, each say() line goes to the voice the moment the stage has run it
//  (SpeechQueue fetches two lines ahead), and the voice's start and end of each line tell the
//  stage when to draw that beat.
//
//  The model may answer NONE (or prose) when a picture is not needed: the reply is then cut short
//  and the question comes back as not drawn while nothing has been said or shown, so the caller
//  answers it the ordinary way. The same happens when the stage never reports ready. A voice that
//  fails mid-answer comes back with what it did not say, and the stage draws the rest at once.
//
//  When the first line is slow to come after the reply has shown it is a program (a strong model
//  thinking, a detailed figure), a short hold line is said first so the wait doesn't feel broken.
//  Windows twin: DrawingAnswer.cs.
//

import Foundation

/// How a drawn answer went. `drew` false means nothing was drawn or said: the question is the caller's.
struct DrawingAnswerResult: Equatable {
    let drew: Bool
    /// Every say() line of the program, in order.
    let spokenLines: [String]
    /// The lines the voice could not say, when it failed partway.
    let unspokenAfterVoiceFailure: [String]?
    /// The program the reply held.
    let program: String
    /// A follow-up's program ran after the drawing on the stage, which stayed.
    let keptTheDrawing: Bool

    static func notDrawn() -> DrawingAnswerResult {
        DrawingAnswerResult(drew: false, spokenLines: [], unspokenAfterVoiceFailure: nil, program: "", keptTheDrawing: false)
    }
}

enum DrawingAnswer {
    /// The longest a drawing may take once its program is written, voice included.
    static let limitSeconds: TimeInterval = 180
    /// The hold line is said when the first line hasn't come this long after the program started.
    static let holdLineAfterSeconds: TimeInterval = 2
    /// A page that hasn't said `ready` this long after the first feed is given up on.
    static let readinessTimeoutSeconds: TimeInterval = 5

    /// The hold line's piece tag: not a stage line, so it never starts or ends a beat.
    private static let holdLineTag = -1

    /// Draws the answer `writeReply` streams (it calls its closure with the whole reply so far and
    /// returns the whole reply) on `displayFrame`'s screen, speaking its lines through `speech`.
    /// Cancelling the calling task stops the reply and the voice and clears the stage; it is
    /// rethrown as CancellationError. With `followUp` (the drawing on the stage), a program that
    /// starts with keep() is drawn after it.
    @MainActor
    static func run(
        stage: DrawStageController,
        displayFrame: CGRect,
        canvasWidth: Int,
        canvasHeight: Int,
        writeReply: @escaping (@escaping @MainActor (String) -> Void) async throws -> String,
        speech: SpeechQueue,
        onFirstAudio: @escaping () -> Void,
        log: @escaping (String) -> Void,
        holdLine: String? = nil,
        holdLineAfter: TimeInterval = holdLineAfterSeconds,
        readinessTimeout: TimeInterval = readinessTimeoutSeconds,
        followUp: DrawnAnswer? = nil
    ) async throws -> DrawingAnswerResult {
        let state = RunState()

        // The stage's events for this answer only.
        stage.lineAdded = { k, text in
            state.spokenLines.append(text)
            speech.enqueue(text, tag: k)
        }
        stage.programRan = { _ in speech.finish() }
        stage.finished = { state.drawingEnded.resume() }
        stage.logged = { line in
            log("page: \(line)")
            if line.hasPrefix("error: the stage did not start") {
                state.stageFailed = true
                state.drawingEnded.resume()
            }
        }
        defer {
            stage.lineAdded = nil
            stage.programRan = nil
            stage.finished = nil
            stage.logged = nil
            state.holdLineTimer?.cancel()
            state.readinessTimer?.cancel()
        }

        let playback = Task { @MainActor in
            try await speech.run(
                onFirstAudio: onFirstAudio,
                onPieceStarted: { tag in if tag >= 0 { stage.lineStarted(tag) } },
                onPieceEnded: { tag in if tag >= 0 { stage.lineEnded(tag) } }
            )
        }

        func notDrawn(_ reason: String) -> DrawingAnswerResult {
            log("\(reason); not drawn")
            speech.cancel()
            playback.cancel()
            if state.opened { stage.clear(reason: reason) }
            return .notDrawn()
        }

        /// The stage is opened the moment the reply is known to be a program (or, for a follow-up,
        /// once keep() or its absence is known), and the hold-line and readiness clocks start.
        func open(keep: Bool) {
            guard !state.opened else { return }
            state.opened = true
            if keep, followUp != nil {
                stage.continueDrawing()
            } else {
                stage.begin(displayFrame: displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
            }
            if let holdLine {
                state.holdLineTimer = Task { @MainActor in
                    try? await Task.sleep(nanoseconds: UInt64(holdLineAfter * 1_000_000_000))
                    guard !Task.isCancelled, state.spokenLines.isEmpty, !state.holdLineSaid else { return }
                    state.holdLineSaid = true
                    log("the first line is slow; saying the hold line")
                    speech.enqueue(holdLine, tag: holdLineTag)
                }
            }
            state.readinessTimer = Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(readinessTimeout * 1_000_000_000))
                guard !Task.isCancelled, !stage.isReady else { return }
                state.stageFailed = true
                state.drawingEnded.resume()
            }
        }

        /// Reads the reply as it grows: decides prose/NONE/program once, then feeds the program.
        func replyGrew(_ replySoFar: String) {
            if state.decision == nil {
                switch DrawStageLesson.decide(replySoFar: replySoFar, done: false) {
                case .undecided: return
                case .notADrawing:
                    state.decision = .notADrawing
                    state.replyTask?.cancel()
                    return
                case .program:
                    state.decision = .program
                }
            }
            guard state.decision == .program else { return }
            let program = DrawStageLesson.programIn(replySoFar)
            if followUp != nil, state.keeps == nil {
                guard let keep = DrawStageLesson.keepsTheDrawing(program, done: false) else { return }
                state.keeps = keep
                log(keep ? "the follow-up keeps the drawing" : "the follow-up draws afresh")
                open(keep: keep)
            } else if followUp == nil {
                open(keep: false)
            }
            if !program.isEmpty { stage.feed(program, done: false) }
        }

        let replyTask = Task { @MainActor in try await writeReply { replySoFar in replyGrew(replySoFar) } }
        state.replyTask = replyTask

        let reply: String
        do {
            reply = try await withTaskCancellationHandler {
                try await replyTask.value
            } onCancel: {
                replyTask.cancel()
            }
        } catch {
            if Task.isCancelled {
                _ = notDrawn("stopped before the program was written")
                throw CancellationError()
            }
            if state.decision == .notADrawing { return notDrawn("the model answered in words") }
            if state.spokenLines.isEmpty { return notDrawn("the program was not written (\(error))") }
            // The reply failed after lines were already spoken: draw what came.
            reply = ""
        }

        if state.decision == .notADrawing || DrawStageLesson.decide(replySoFar: reply, done: true) == .notADrawing {
            return notDrawn("the model answered in words")
        }
        let program = DrawStageLesson.programIn(reply)
        if state.stageFailed { return notDrawn("the stage did not start") }
        if program.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return notDrawn("the reply held no program") }
        if followUp != nil, state.keeps == nil {
            let keep = DrawStageLesson.keepsTheDrawing(program, done: true) ?? false
            state.keeps = keep
            open(keep: keep)
        }
        open(keep: false)
        log("program written (\(program.count) chars)")
        stage.feed(program, done: true)

        // The drawing ends with its last line; the voice failing or being stopped ends it sooner.
        // A first-to-finish race, not a task group: awaiting the playback task cannot be cancelled,
        // and a group would wait for it even after the stage gave up (a page that never started).
        let raceEnded = Signal()
        let drawingWatcher = Task { @MainActor in await state.drawingEnded.wait(); raceEnded.resume() }
        let playbackWatcher = Task { @MainActor in _ = try? await playback.value; raceEnded.resume() }
        let limitWatcher = Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64(limitSeconds * 1_000_000_000))
            if !Task.isCancelled { raceEnded.resume() }
        }
        await raceEnded.wait()
        drawingWatcher.cancel()
        limitWatcher.cancel()
        _ = playbackWatcher
        if Task.isCancelled {
            _ = notDrawn("stopped while drawing")
            throw CancellationError()
        }
        speech.finish()

        var unspoken: [String]?
        do {
            try await playback.value
        } catch let failure as SpeechQueue.Failure {
            log("voice failed (\(failure.underlyingError)); drawing the rest now")
            stage.finishNow()
            unspoken = failure.unspokenText.filter { $0 != holdLine }
        } catch {
            // Cancelled: handled below.
        }
        if Task.isCancelled {
            stage.clear(reason: "stopped")
            throw CancellationError()
        }
        if state.stageFailed, state.spokenLines.isEmpty { return notDrawn("the stage did not start") }
        return DrawingAnswerResult(drew: true, spokenLines: state.spokenLines, unspokenAfterVoiceFailure: unspoken,
                                   program: program, keptTheDrawing: state.keeps == true)
    }

    /// What one run accumulates; a class so the stage's closures and the run share it.
    @MainActor
    private final class RunState {
        var spokenLines: [String] = []
        var decision: DrawStageLesson.ReplyDecision?
        var keeps: Bool?
        var opened = false
        var stageFailed = false
        var holdLineSaid = false
        var holdLineTimer: Task<Void, Never>?
        var readinessTimer: Task<Void, Never>?
        var replyTask: Task<String, Error>?
        let drawingEnded = Signal()
    }

    /// Resumed once; waiting after that returns at once. A cancelled waiter stops waiting too, so a
    /// task group that gives up on the drawing never hangs on it.
    @MainActor
    final class Signal {
        private var resumed = false
        private var waiters: [UUID: CheckedContinuation<Void, Never>] = [:]

        func resume() {
            guard !resumed else { return }
            resumed = true
            let waiting = waiters.values
            waiters.removeAll()
            waiting.forEach { $0.resume() }
        }

        func wait() async {
            if resumed { return }
            let id = UUID()
            await withTaskCancellationHandler {
                await withCheckedContinuation { continuation in
                    if Task.isCancelled { continuation.resume() } else { waiters[id] = continuation }
                }
            } onCancel: {
                Task { @MainActor in self.waiters.removeValue(forKey: id)?.resume() }
            }
        }
    }
}
