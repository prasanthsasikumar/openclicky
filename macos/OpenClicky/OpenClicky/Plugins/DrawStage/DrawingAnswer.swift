//
//  DrawingAnswer.swift
//  OpenClicky
//
//  One answer drawn on the stage while it is spoken: the model's reply streams in, its program is
//  fed to the stage as it grows, each say() line goes to the voice the moment the stage has run it
//  (SpeechQueue fetches two lines ahead), and the voice's start and end of each line tell the
//  stage when to draw that beat.
//
//  The model may answer NONE (or prose) when a picture is not needed: the question then comes back
//  as not drawn while nothing has been said or shown, so the caller answers it the ordinary way.
//  The same happens when the stage never reports ready (the reply is cut short at once, so the
//  person is not left waiting). A voice that fails mid-answer comes back with what it did not say,
//  and the stage draws the rest at once.
//
//  Stopping: esc (`DrawingAnswerControl.stop`) cuts the reply and the voice now and keeps what was
//  drawn, so the drawing can go to its bubble; a new question cancels the task, and when a line
//  was already drawn the stage is kept (`onInterrupted`) so the new question can build on it.
//
//  When the first line is slow to come after the reply has shown it is a program (a strong model
//  thinking, a detailed figure), a short hold line is said first so the wait doesn't feel broken.
//  Windows twin: DrawingAnswer.cs.
//

import Foundation

/// How a drawn answer went. `drew` false means nothing was drawn or said: the question is the caller's.
struct DrawingAnswerResult: Equatable {
    let drew: Bool
    /// The say() lines the person heard start, in order.
    let spokenLines: [String]
    /// The lines the voice could not say, when it failed partway.
    let unspokenAfterVoiceFailure: [String]?
    /// The program the reply held (as far as it came).
    let program: String
    /// A follow-up's program ran after the drawing on the stage, which stayed.
    let keptTheDrawing: Bool
    /// Esc (or the stop button) ended it early; what was drawn stays.
    var stoppedByThePerson: Bool = false

    static func notDrawn(stoppedByThePerson: Bool = false) -> DrawingAnswerResult {
        DrawingAnswerResult(drew: false, spokenLines: [], unspokenAfterVoiceFailure: nil, program: "", keptTheDrawing: false,
                            stoppedByThePerson: stoppedByThePerson)
    }
}

/// The handle the person's stop (esc, the island's button) reaches a running drawing through.
@MainActor
final class DrawingAnswerControl {
    private(set) var wasStopped = false
    fileprivate var stopAction: (() -> Void)?

    /// Nonisolated so it can be a default argument of `DrawingAnswer.run`.
    nonisolated init() {}

    /// Stops the reply and the voice now; the run returns with what was drawn so far.
    func stop() {
        guard !wasStopped else { return }
        wasStopped = true
        stopAction?()
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
    /// `stopVoiceNow` cuts the audio that is playing (the player's stop); `control.stop()` ends the
    /// run early keeping what was drawn; cancelling the calling task (a new question) rethrows
    /// CancellationError after `onInterrupted(programSoFar, linesHeard)` when a line was drawn, or
    /// after clearing the stage when nothing was. With `followUp` (the drawing on the stage), a
    /// program that starts with keep() is drawn after it.
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
        followUp: DrawnAnswer? = nil,
        control: DrawingAnswerControl = DrawingAnswerControl(),
        stopVoiceNow: @escaping () -> Void = {},
        onInterrupted: ((String, [String]) -> Void)? = nil
    ) async throws -> DrawingAnswerResult {
        let state = RunState()

        // The stage's events for this answer only.
        stage.lineAdded = { lineIndex, text in
            state.lines.append(text)
            speech.enqueue(text, tag: lineIndex)
        }
        stage.programRan = { _ in speech.finish() }
        stage.finished = { state.drawingEnded.resume() }
        stage.logged = { line in
            log("page: \(line)")
            if line.hasPrefix("error: the stage did not start") { state.stageGaveUp(speech: speech) }
        }
        defer {
            stage.lineAdded = nil
            stage.programRan = nil
            stage.finished = nil
            stage.logged = nil
            control.stopAction = nil
            state.holdLineTimer?.cancel()
            state.readinessTimer?.cancel()
        }

        let playback = Task { @MainActor in
            try await speech.run(
                onFirstAudio: onFirstAudio,
                onPieceStarted: { tag in
                    guard tag >= 0 else { return }
                    if tag < state.lines.count { state.linesHeard.append(state.lines[tag]) }
                    stage.lineStarted(tag)
                },
                onPieceEnded: { tag in if tag >= 0 { stage.lineEnded(tag) } }
            )
        }

        func notDrawn(_ reason: String, stoppedByThePerson: Bool = false) -> DrawingAnswerResult {
            log("\(reason); not drawn")
            speech.cancel()
            playback.cancel()
            if state.opened { stage.clear(reason: reason) }
            return .notDrawn(stoppedByThePerson: stoppedByThePerson)
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
                    // Only once the page is known to work: a stage that never starts must not promise a drawing.
                    guard !Task.isCancelled, stage.isReady, state.lines.isEmpty, !state.holdLineSaid, !state.stopped else { return }
                    state.holdLineSaid = true
                    log("the first line is slow; saying the hold line")
                    speech.enqueue(holdLine, tag: holdLineTag)
                }
            }
            state.readinessTimer = Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(readinessTimeout * 1_000_000_000))
                guard !Task.isCancelled, !stage.isReady else { return }
                log("the stage did not report ready in \(readinessTimeout) s")
                state.stageGaveUp(speech: speech)
            }
        }

        /// Reads the reply as it grows: decides prose/NONE/program once, then feeds the program.
        func replyGrew(_ replySoFar: String) {
            state.latestReply = replySoFar
            if state.decision == nil {
                switch DrawStageLesson.decide(replySoFar: replySoFar, done: false) {
                case .undecided: return
                case .notADrawing:
                    state.decision = .notADrawing
                    // A NONE reply is over in a moment: let it end, so an account is not charged
                    // a cancelled stream's whole hold. Prose could go on for a while: cut it.
                    if !DrawStageLesson.isNone(replySoFar) { state.replyTask?.cancel() }
                    return
                case .program:
                    state.decision = .program
                }
            }
            guard state.decision == .program, !state.stopped else { return }
            let program = DrawStageLesson.programIn(replySoFar)
            if followUp != nil, state.keeps == nil {
                guard let keep = DrawStageLesson.keepsTheDrawing(program, done: false) else { return }
                state.keeps = keep
                log(keep ? "the follow-up keeps the drawing" : "the follow-up draws afresh")
                open(keep: keep)
            } else if followUp == nil {
                open(keep: false)
            }
            if !program.isEmpty {
                state.fedProgram = program
                stage.feed(program, done: false)
            }
        }

        let replyTask = Task { @MainActor in try await writeReply { replySoFar in replyGrew(replySoFar) } }
        state.replyTask = replyTask

        // Esc or the stop button: the reply and the voice stop now; what was drawn stays.
        control.stopAction = {
            guard !state.stopped else { return }
            state.stopped = true
            log("stopped by the person")
            replyTask.cancel()
            speech.cancel()
            stopVoiceNow()
            if state.opened { stage.finishNow() }
            state.drawingEnded.resume()
        }

        var reply: String
        do {
            reply = try await withTaskCancellationHandler {
                try await replyTask.value
            } onCancel: {
                replyTask.cancel()
            }
        } catch {
            if Task.isCancelled {
                return try interrupted(state: state, stage: stage, speech: speech, playback: playback, log: log, onInterrupted: onInterrupted)
            }
            if state.stageFailed { return notDrawn("the stage did not start") }
            if state.decision == .notADrawing { return notDrawn("the model answered in words: \(replyHead(state.latestReply))") }
            if state.stopped, state.linesHeard.isEmpty, state.fedProgram.isEmpty {
                return notDrawn("stopped before anything was drawn", stoppedByThePerson: true)
            }
            if !state.stopped, state.lines.isEmpty {
                return notDrawn("the program was not written (\(error))")
            }
            // Stopped, or the reply failed after lines were already drawn: finish with what came.
            log(state.stopped ? "finishing with what was drawn" : "the reply failed after lines were drawn (\(error)); finishing with what came")
            reply = state.latestReply
        }

        if state.decision == .notADrawing || DrawStageLesson.decide(replySoFar: reply, done: true) == .notADrawing {
            return notDrawn("the model answered in words: \(replyHead(reply.isEmpty ? state.latestReply : reply))")
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
        if !state.stopped {
            log("program written (\(program.count) chars)")
            state.fedProgram = program
            stage.feed(program, done: true)
        }

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
            return try interrupted(state: state, stage: stage, speech: speech, playback: playback, log: log, onInterrupted: onInterrupted)
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
            // Cancelled: by the person (handled below) or by the task (handled above).
        }
        if Task.isCancelled {
            return try interrupted(state: state, stage: stage, speech: speech, playback: playback, log: log, onInterrupted: onInterrupted)
        }
        if state.stageFailed, state.linesHeard.isEmpty { return notDrawn("the stage did not start") }
        if state.stopped, state.linesHeard.isEmpty, state.fedProgram.isEmpty {
            return notDrawn("stopped before anything was drawn", stoppedByThePerson: true)
        }
        return DrawingAnswerResult(drew: true, spokenLines: state.linesHeard, unspokenAfterVoiceFailure: unspoken,
                                   program: program, keptTheDrawing: state.keeps == true, stoppedByThePerson: state.stopped)
    }

    /// The first words of a reply, one line, for the log when the model declined to draw.
    private static func replyHead(_ reply: String) -> String {
        let oneLine = reply.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        return oneLine.count > 120 ? String(oneLine.prefix(120)) + "…" : oneLine
    }

    /// The task was cancelled (a new question): a drawing with a line already drawn stays on the
    /// stage for a follow-up, finished at once; one with nothing drawn is cleared. Always rethrows.
    @MainActor
    private static func interrupted(state: RunState, stage: DrawStageController, speech: SpeechQueue, playback: Task<Void, Error>,
                                    log: @escaping (String) -> Void, onInterrupted: ((String, [String]) -> Void)?) throws -> DrawingAnswerResult {
        speech.cancel()
        playback.cancel()
        if state.opened, !state.fedProgram.isEmpty, !state.lines.isEmpty {
            log("interrupted after \(state.linesHeard.count) line(s); the drawing stays for a follow-up")
            stage.finishNow()
            onInterrupted?(DrawStageLesson.programIn(state.latestReply), state.linesHeard)
        } else {
            log("interrupted before anything was drawn; not drawn")
            if state.opened { stage.clear(reason: "interrupted") }
        }
        throw CancellationError()
    }

    /// What one run accumulates; a class so the stage's closures and the run share it.
    @MainActor
    private final class RunState {
        /// Every say() line the page ran, in order (the drawing's lines).
        var lines: [String] = []
        /// The lines whose audio started.
        var linesHeard: [String] = []
        var latestReply = ""
        var fedProgram = ""
        var decision: DrawStageLesson.ReplyDecision?
        var keeps: Bool?
        var opened = false
        var stageFailed = false
        var stopped = false
        var holdLineSaid = false
        var holdLineTimer: Task<Void, Never>?
        var readinessTimer: Task<Void, Never>?
        var replyTask: Task<String, Error>?
        let drawingEnded = Signal()

        /// The page never started: the reply is cut at once and nothing more is said.
        func stageGaveUp(speech: SpeechQueue) {
            guard !stageFailed else { return }
            stageFailed = true
            replyTask?.cancel()
            speech.cancel()
            drawingEnded.resume()
        }
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
