//
//  CompanionManager+DrawStage.swift
//  OpenClicky
//
//  Draw to explain, in the ask lane: a question a picture answers ("how does a jet engine
//  work?") goes to the person's answer model with the drawing prompt, and the reply is drawn on
//  the stage while its lines are spoken (DrawingAnswer). The model answers NONE when words do
//  better, and the question then takes the ordinary path with nothing said or shown. When the
//  drawing and the voice are done it shrinks into a bubble beside the island; a click brings it
//  back, and a question asked while it is open can build on it (a follow-up).
//
//  `DrawStageSession` holds what the stage is doing (an extension cannot store state): the
//  drawing that is up, its state for DrawStageTurn, what was there before an attempt (so an
//  attempt that draws nothing gives it back), and the handle esc stops a playing drawing through.
//  Design: docs/superpowers/specs/2026-11-04-draw-and-explain-mac-design.md.
//

import AppKit
import Foundation

@MainActor
final class DrawStageSession {
    static let shared = DrawStageSession(stage: .shared, bubble: DrawStageBubble.shared, stopVoiceNow: {})

    private(set) var state: DrawStageTurn.StageState = .none
    /// The drawing on the stage or in its bubble, for a follow-up to build on.
    private(set) var current: DrawnAnswer?
    /// The screen the drawing was made on, where its bubble goes.
    private(set) var displayFrame: CGRect?
    /// What was on the stage before the attempt under way, given back if it draws nothing.
    private var before: (state: DrawStageTurn.StageState, drawing: DrawnAnswer?)?
    /// The bubble's picture and place, so it can come back after a NONE.
    private var lastBubble: (thumbnail: NSImage, islandFrame: CGRect?, placement: HUDPlacement, screen: NSScreen)?
    /// The drawing being played: esc stops it through this and keeps what was drawn.
    private var playingControl: DrawingAnswerControl?
    /// Cuts the audio that is playing (the answer voice player's stop).
    var stopVoiceNow: () -> Void
    private weak var companionManager: CompanionManager?

    let stage: DrawStageController
    let bubble: DrawStageBubbling

    init(stage: DrawStageController, bubble: DrawStageBubbling, stopVoiceNow: @escaping () -> Void) {
        self.stage = stage
        self.bubble = bubble
        self.stopVoiceNow = stopVoiceNow
        stage.displayGone = { [weak self] in self?.displayGone() }
    }

    /// Wires the stage window, the photo finder, clicked parts, the bubble and the voice's stop to the companion, once.
    func attach(to companionManager: CompanionManager) {
        self.companionManager = companionManager
        stopVoiceNow = { [weak companionManager] in companionManager?.answerVoicePlayer.stopPlayback() }
        stage.surface = DrawStageWindow.shared
        stage.findPhoto = { query, caption in
            guard let picture = await TeacherPictureFinder.find(query: query, caption: caption) else { return nil }
            return DrawStagePhoto(picture: picture)
        }
        stage.figurePartClicked = { [weak companionManager] part in
            companionManager?.submitTypedRequest("what does the \(part) do?")
        }
        if let bubble = bubble as? DrawStageBubble {
            bubble.onRestore = { [weak self] in self?.restoreFromBubble() }
            bubble.onDiscard = { [weak self] in self?.discard(reason: "the bubble's ✕") }
        }
    }

    /// Whether a line of the playing drawing has been heard (esc then keeps it; before that, esc is a plain stop).
    var hasSpokenAnything: Bool { playingControl != nil && linesHeardSoFar > 0 }
    private var linesHeardSoFar = 0

    // MARK: - State changes

    /// An attempt starts: what is there now is remembered, the bubble (if any) steps aside.
    func drawingStarted(on displayFrame: CGRect, speech: SpeechQueue, control: DrawingAnswerControl) {
        before = (state, current)
        self.displayFrame = displayFrame
        playingControl = control
        linesHeardSoFar = 0
        state = .playing
        bubble.dismiss()
    }

    func noteLineHeard() { linesHeardSoFar += 1 }

    /// The attempt ended. Drawn: it is the drawing now. Not drawn (NONE, an error, stopped before
    /// anything was drawn): whatever was there before comes back, bubble included.
    func drawingEnded(drew: Bool, drawing: DrawnAnswer?) {
        playingControl = nil
        let previous = before
        before = nil
        if drew, let drawing {
            current = drawing
            state = .open
            return
        }
        switch previous?.state {
        case .bubble?:
            current = previous?.drawing
            state = .bubble
            if let lastBubble { bubble.show(thumbnail: lastBubble.thumbnail, islandFrame: lastBubble.islandFrame, placement: lastBubble.placement, screen: lastBubble.screen) }
        case .open?:
            // The follow-up's request left the open drawing on the stage; the companion sends it to its bubble.
            current = previous?.drawing
            state = .open
        default:
            current = nil
            state = .none
        }
    }

    /// A new question cancelled the drawing after a line was drawn: it stays open, with what it has, for a follow-up.
    func interrupted(question: String, programSoFar: String, linesHeard: [String], canvasWidth: Int, canvasHeight: Int) {
        playingControl = nil
        before = nil
        current = DrawnAnswer(questions: [question], program: programSoFar, canvasWidth: canvasWidth, canvasHeight: canvasHeight, spokenLines: linesHeard)
        state = .open
    }

    /// Esc or the stop button while a drawing plays: the reply and the voice stop now, the stage
    /// draws the rest at once, and the run returns with what was heard so the drawing goes to its bubble.
    func stopPlaying(reason: String) {
        guard state == .playing else { return }
        AppLog.append("draw: stopped while playing (\(reason))")
        stopVoiceNow()
        playingControl?.stop()
        // The run sends finishNow itself once it is stopped; said again it is harmless, and a
        // stage with no run attached (the smoke) still finishes.
        stage.finishNow()
    }

    func minimised(thumbnail: NSImage, islandFrame: CGRect?, placement: HUDPlacement, screen: NSScreen) {
        guard state == .open || state == .playing else { return }
        lastBubble = (thumbnail, islandFrame, placement, screen)
        bubble.show(thumbnail: thumbnail, islandFrame: islandFrame, placement: placement, screen: screen)
        state = .bubble
    }

    func restoreFromBubble() {
        guard state == .bubble else { return }
        AppLog.append("draw: brought back from the bubble")
        bubble.dismiss()
        stage.show()
        state = .open
    }

    func discard(reason: String) {
        guard state != .none else { return }
        bubble.dismiss()
        stage.clear(reason: reason)
        current = nil
        lastBubble = nil
        before = nil
        state = .none
    }

    /// The drawing's display went away: a playing drawing is stopped like esc (its voice too), a
    /// finished one is thrown away with its bubble.
    func displayGone() {
        switch state {
        case .playing:
            stopPlaying(reason: "the display went away")
        case .open, .bubble:
            // The controller has already cleared a stage whose display went; saying so again is harmless.
            discard(reason: "the display went away")
        case .none:
            break
        }
    }

    var screen: NSScreen? {
        guard let displayFrame else { return NSScreen.main }
        return NSScreen.screens.first { $0.frame == displayFrame } ?? NSScreen.main
    }
}

extension CompanionManager {
    /// Answers `transcript` by drawing on the stage, speaking as it draws. True when it did (and
    /// the voice has finished, the bubble is up); false when the caller must answer it the
    /// ordinary way, with nothing said or shown. Cancellation (a new question) is rethrown; esc
    /// stops the drawing early and still returns true with what was drawn in its bubble.
    /// `pointer` and `pointerCloseUp` are where the mouse was and what is under it (the ordinary
    /// lane's own); they go with the request only when the question is about the screen ("how does
    /// this work?"), so the drawing can be tied to what the person is working on. A question about
    /// the world at large ("how does an electron microscope work?") sends the screen as the canvas
    /// and nothing more, and the model draws whether or not the thing is on screen.
    func tryDrawingAnswer(transcript: String, cursorCapture: CompanionScreenCapture, decision: DrawStageTurn.Decision,
                          pointer: PointerInCapture? = nil, pointerCloseUp: (data: Data, label: String)? = nil) async throws -> Bool {
        let session = DrawStageSession.shared
        let canvasWidth = cursorCapture.screenshotWidthInPixels
        let canvasHeight = cursorCapture.screenshotHeightInPixels
        guard let systemPrompt = DrawStageLesson.systemPrompt(canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return false }

        // A follow-up builds on the drawing that is open (or the one a new question interrupted,
        // which stays open with what it had).
        let followUp = (decision == .followUp || decision == .interruptThenFollowUp) && session.state == .open ? session.current : nil
        var userPrompt: String?
        if let followUp {
            userPrompt = DrawStageLesson.followUpPrompt(question: transcript, drawing: followUp)
        } else {
            userPrompt = DrawStageLesson.questionPrompt(transcript)
        }
        let aboutTheScreen = DrawStageLesson.refersToTheScreen(transcript)
        if aboutTheScreen, let pointer, userPrompt != nil {
            userPrompt! += "\n[\(pointer.promptLine)]"
        }
        guard let userPrompt else { return false }
        AppLog.append("draw: \(followUp == nil ? "asking for a drawing" : "asking about the drawing") (\(canvasWidth)×\(canvasHeight)\(aboutTheScreen && pointerCloseUp != nil ? ", with the pointer's close-up" : ""))")

        let answerVoiceSession = answerVoicePlayer.makeAnswerSession()
        let speech = SpeechQueue(
            fetchAudio: { pieceText in try await answerVoiceSession.audio(for: pieceText) },
            playAudio: { [answerVoicePlayer] audioData in try await answerVoicePlayer.playToCompletion(audioData) }
        )
        var images = [Self.imageLabeledWithItsPixelSize(cursorCapture)]
        if aboutTheScreen, let pointerCloseUp { images.append(pointerCloseUp) }
        let answers = claudeAPI
        let options = AnswerRequestOptions(maxTokens: DrawStageLesson.outputTokens, purpose: .drawing, thinkingOff: true, timeoutSeconds: 180)
        let writeReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            let result = try await answers.analyzeImageStreaming(
                images: images, systemPrompt: Self.withReplyLanguage(systemPrompt), userPrompt: userPrompt, tools: [], options: options,
                onTextChunk: { onChunk($0) })
            return result.text
        }

        let control = DrawingAnswerControl()
        session.drawingStarted(on: cursorCapture.displayFrame, speech: speech, control: control)
        let askStartedAt = Date()
        let result: DrawingAnswerResult
        do {
            result = try await DrawingAnswer.run(
                stage: session.stage, displayFrame: cursorCapture.displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight,
                writeReply: writeReply, speech: speech,
                onFirstAudio: { [weak self] in
                    self?.voiceState = .responding
                    self?.dismissCursorCaption()
                    DrawStageSession.shared.noteLineHeard()
                    AppLog.append("draw: voice started \(String(format: "%.1f", Date().timeIntervalSince(askStartedAt))) s after the question")
                },
                log: { AppLog.append("draw: \($0)") },
                holdLine: DrawStageLesson.holdLine,
                followUp: followUp,
                control: control,
                stopVoiceNow: { [weak self] in self?.answerVoicePlayer.stopPlayback() },
                onInterrupted: { programSoFar, linesHeard in
                    DrawStageSession.shared.interrupted(question: transcript, programSoFar: programSoFar, linesHeard: linesHeard,
                                                        canvasWidth: canvasWidth, canvasHeight: canvasHeight)
                }
            )
        } catch is CancellationError {
            if session.state == .playing { session.drawingEnded(drew: false, drawing: nil) }
            throw CancellationError()
        } catch {
            // The account's voice allowance or answer budget, a vendor down: the ordinary lane says so.
            AppLog.append("draw: the drawing request failed (\(error)); answering in words")
            session.drawingEnded(drew: false, drawing: nil)
            if session.state == .open { await minimiseDrawingToBubble(reason: "the follow-up was not drawn") }
            return false
        }
        guard result.drew else {
            session.drawingEnded(drew: false, drawing: nil)
            // A follow-up the model answered NONE to: the open drawing goes to its bubble and the
            // question is answered the ordinary way; a NONE on a fresh question gives the bubble back.
            if session.state == .open { await minimiseDrawingToBubble(reason: "a question about something else") }
            return result.stoppedByThePerson
        }

        let program = result.keptTheDrawing && followUp != nil ? (followUp!.program + "\n" + result.program) : result.program
        let questions = (followUp?.questions ?? []) + [transcript]
        let spokenLines = (result.keptTheDrawing ? (followUp?.spokenLines ?? []) : []) + result.spokenLines
        session.drawingEnded(drew: true, drawing: DrawnAnswer(questions: questions, program: program, canvasWidth: canvasWidth,
                                                              canvasHeight: canvasHeight, spokenLines: spokenLines))
        AppLog.append("draw: drew \(result.spokenLines.count) line(s) in \(String(format: "%.1f", Date().timeIntervalSince(askStartedAt))) s\(result.stoppedByThePerson ? ", stopped early" : "")")
        if !result.spokenLines.isEmpty {
            rememberExchange(userTranscript: transcript, assistantResponse: result.spokenLines.joined(separator: " "))
        }
        if let unspoken = result.unspokenAfterVoiceFailure, !unspoken.isEmpty, !result.stoppedByThePerson {
            dismissCursorCaption()
            speakWithSystemVoice(unspoken.joined(separator: " "))
        }
        if !Task.isCancelled {
            await minimiseDrawingToBubble(reason: result.stoppedByThePerson ? "stopped" : "the drawing is done")
        }
        return true
    }

    /// Draw to explain on the fast-voice (Realtime) lane. Called with each spoken turn's words;
    /// true when the question is taken over by a drawing (the fast voice's own reply is cut
    /// and the drawing is narrated by the answer voice), false when the fast voice keeps it.
    /// Afterwards the fast voice is told what was drawn and said, so a follow-up builds on it;
    /// a question the model would not draw for goes back to the fast voice as a typed turn.
    @discardableResult
    func tryRealtimeDrawing(transcript: String) -> Bool {
        let session = DrawStageSession.shared
        let decision = DrawStageTurn.decide(question: transcript, stageState: session.state,
                                            drawToExplain: dictationSettings.drawToExplain, isGuidedStepContinuation: false)
        AppLog.append("draw: fast voice gate \(decision) for a \(transcript.count)-char question (stage \(session.state))")
        guard decision != .notADrawingQuestion else { return false }
        if decision == .toBubbleThenOrdinary {
            Task { @MainActor in await minimiseDrawingToBubble(reason: "a question about something else") }
            return false
        }
        guard let capture = realtimeVoiceClient.lastScreenCapture else {
            AppLog.append("draw: fast voice has no screen yet; answering in words")
            return false
        }
        // The fast voice steps aside: its reply (which may have begun) is cut, and in always-on
        // mode the microphone stops feeding it while the answer voice narrates, so the speakers
        // cannot start a turn of their own. ⌃⌥ and esc still stop the drawing.
        realtimeVoiceClient.stopSpeaking()
        let wasAlwaysOn = realtimeVoiceClient.isAlwaysOn
        if wasAlwaysOn { realtimeVoiceClient.stopListening() }
        TeacherPlugin.clearBoard(reason: "a drawing")
        voiceState = .processing
        isAskInFlight = true
        currentResponseTask = Task { @MainActor in
            defer {
                if wasAlwaysOn { realtimeVoiceClient.startListeningContinuously() }
            }
            do {
                let drew = try await tryDrawingAnswer(transcript: transcript, cursorCapture: capture, decision: decision)
                if drew {
                    let lines = session.current?.spokenLines ?? []
                    realtimeVoiceClient.addAssistantNote(DrawStageLesson.fastVoiceNote(question: transcript, spokenLines: lines))
                } else {
                    // Not a drawing after all: the fast voice answers it, in its own voice.
                    AppLog.append("draw: handing the question back to the fast voice")
                    await realtimeVoiceClient.sendTextTurn(transcript)
                }
            } catch {
                // Cancelled by a new press or esc: the stage and session already know.
            }
            if !Task.isCancelled { await finishAskTurn() }
        }
        return true
    }

    /// The finished drawing shrinks into its bubble beside the island; the stage hides and sleeps.
    func minimiseDrawingToBubble(reason: String) async {
        let session = DrawStageSession.shared
        guard session.state == .open || session.state == .playing else { return }
        if session.state == .playing { session.stopPlaying(reason: reason) }
        let screen = session.screen ?? NSScreen.main
        var thumbnail: NSImage?
        if let bounds = await session.stage.measure(), !bounds.isEmpty {
            thumbnail = await session.stage.surface?.snapshot(of: bounds)
        }
        if let screen {
            let island = notchHUDManager.islandFrame(on: screen)
            session.minimised(thumbnail: thumbnail ?? NSImage(size: NSSize(width: 96, height: 64)),
                              islandFrame: island?.frame, placement: island?.placement ?? notchHUDManager.placement, screen: screen)
        }
        session.stage.hide(reason: reason)
        AppLog.append("draw: minimised to the bubble (\(reason))")
    }

    func discardDrawing(reason: String) {
        DrawStageSession.shared.discard(reason: reason)
    }

    /// Esc (or the island's stop button) with a drawing: one playing with a line already heard
    /// stops its voice and goes to its bubble; one whose first line hasn't come yet is a plain
    /// stop of the ask lane (nothing is said or shown); a finished drawing that is open (brought
    /// back from its bubble, or just done) goes back to its bubble; esc on the bubble alone clears it.
    func escapeStopsTheDrawing() {
        let session = DrawStageSession.shared
        switch session.state {
        case .playing:
            if session.hasSpokenAnything {
                session.stopPlaying(reason: "esc")
                showAskCaption("stopped", holdSeconds: 1.5)
            } else {
                session.stopPlaying(reason: "esc before anything was drawn")
                _ = stopAskLane(from: "esc")
            }
        case .open:
            Task { @MainActor in await minimiseDrawingToBubble(reason: "esc") }
        case .bubble:
            discardDrawing(reason: "esc")
        case .none:
            break
        }
    }
}
