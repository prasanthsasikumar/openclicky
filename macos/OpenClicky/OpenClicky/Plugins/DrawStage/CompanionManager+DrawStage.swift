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
//  drawing that is up, its state for DrawStageTurn, and the voice of a drawing being played so
//  esc can stop it and keep what was drawn. Design:
//  docs/superpowers/specs/2026-11-04-draw-and-explain-mac-design.md.
//

import AppKit
import Foundation

@MainActor
final class DrawStageSession {
    static let shared = DrawStageSession()

    private(set) var state: DrawStageTurn.StageState = .none
    /// The drawing on the stage or in its bubble, for a follow-up to build on.
    private(set) var current: DrawnAnswer?
    /// The screen the drawing was made on, where its bubble goes.
    private var displayFrame: CGRect?
    /// The voice of the drawing being played: esc stops it and the drawing finishes at once.
    private var playingSpeech: SpeechQueue?
    private weak var companionManager: CompanionManager?

    let stage = DrawStageController.shared
    let bubble = DrawStageBubble.shared

    /// Wires the stage window, the photo finder, clicked parts and the bubble to the companion, once.
    func attach(to companionManager: CompanionManager) {
        self.companionManager = companionManager
        stage.surface = DrawStageWindow.shared
        stage.findPhoto = { query, caption in
            guard let picture = await TeacherPictureFinder.find(query: query, caption: caption) else { return nil }
            return DrawStagePhoto(picture: picture)
        }
        stage.figurePartClicked = { [weak companionManager] part in
            companionManager?.submitTypedRequest("what does the \(part) do?")
        }
        bubble.onRestore = { [weak self] in self?.restoreFromBubble() }
        bubble.onDiscard = { [weak self] in self?.discard(reason: "the bubble's ✕") }
    }

    // MARK: - State changes

    func drawingStarted(on displayFrame: CGRect, speech: SpeechQueue) {
        self.displayFrame = displayFrame
        playingSpeech = speech
        state = .playing
        bubble.dismiss()
    }

    func drawingEnded(drew: Bool, drawing: DrawnAnswer?) {
        playingSpeech = nil
        if drew, let drawing {
            current = drawing
            state = .open
        } else if state == .playing {
            state = .none
            current = nil
        }
    }

    /// Esc while a drawing plays: the voice stops, the stage draws the rest at once, and the
    /// run returns with what was said so the drawing goes to its bubble (not thrown away).
    func stopPlaying(reason: String) {
        guard state == .playing else { return }
        AppLog.append("draw: stopped while playing (\(reason))")
        playingSpeech?.cancel()
        stage.finishNow()
    }

    func minimised() {
        guard state == .open || state == .playing else { return }
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
        state = .none
    }

    var screen: NSScreen? {
        guard let displayFrame else { return NSScreen.main }
        return NSScreen.screens.first { $0.frame == displayFrame } ?? NSScreen.main
    }
}

extension CompanionManager {
    /// Answers `transcript` by drawing on the stage, speaking as it draws. True when it did (and
    /// the voice has finished, the bubble is up); false when the caller must answer it the
    /// ordinary way, with nothing said or shown. Cancellation (esc, a new question) is rethrown.
    func tryDrawingAnswer(transcript: String, cursorCapture: CompanionScreenCapture, decision: DrawStageTurn.Decision) async throws -> Bool {
        let session = DrawStageSession.shared
        let canvasWidth = cursorCapture.screenshotWidthInPixels
        let canvasHeight = cursorCapture.screenshotHeightInPixels
        guard let systemPrompt = DrawStageLesson.systemPrompt(canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return false }

        // A follow-up builds on the drawing that is open; a question while one was playing finds
        // it already stopped and cleared by the interruption, so it starts afresh.
        let followUp = decision == .followUp ? session.current : nil
        let userPrompt: String?
        if let followUp {
            userPrompt = DrawStageLesson.followUpPrompt(question: transcript, drawing: followUp)
        } else {
            userPrompt = DrawStageLesson.questionPrompt(transcript)
        }
        guard let userPrompt else { return false }
        AppLog.append("draw: \(followUp == nil ? "asking for a drawing" : "asking about the drawing") (\(canvasWidth)×\(canvasHeight))")

        let answerVoiceSession = answerVoicePlayer.makeAnswerSession()
        let speech = SpeechQueue(
            fetchAudio: { pieceText in try await answerVoiceSession.audio(for: pieceText) },
            playAudio: { [answerVoicePlayer] audioData in try await answerVoicePlayer.playToCompletion(audioData) }
        )
        let images = [Self.imageLabeledWithItsPixelSize(cursorCapture)]
        let answers = claudeAPI
        let options = AnswerRequestOptions(maxTokens: DrawStageLesson.outputTokens, purpose: .drawing, thinkingOff: true, timeoutSeconds: 180)
        let writeReply: (@escaping @MainActor (String) -> Void) async throws -> String = { onChunk in
            let result = try await answers.analyzeImageStreaming(
                images: images, systemPrompt: Self.withReplyLanguage(systemPrompt), userPrompt: userPrompt, tools: [], options: options,
                onTextChunk: { onChunk($0) })
            return result.text
        }

        session.drawingStarted(on: cursorCapture.displayFrame, speech: speech)
        let askStartedAt = Date()
        let result: DrawingAnswerResult
        do {
            result = try await DrawingAnswer.run(
                stage: session.stage, displayFrame: cursorCapture.displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight,
                writeReply: writeReply, speech: speech,
                onFirstAudio: { [weak self] in
                    self?.voiceState = .responding
                    self?.dismissCursorCaption()
                    AppLog.append("draw: voice started \(String(format: "%.1f", Date().timeIntervalSince(askStartedAt))) s after the question")
                },
                log: { AppLog.append("draw: \($0)") },
                holdLine: DrawStageLesson.holdLine,
                followUp: followUp
            )
        } catch is CancellationError {
            session.drawingEnded(drew: false, drawing: nil)
            throw CancellationError()
        } catch {
            // The account's voice allowance or answer budget, a vendor down: the ordinary lane says so.
            AppLog.append("draw: the drawing request failed (\(error)); answering in words")
            session.drawingEnded(drew: false, drawing: nil)
            return false
        }
        guard result.drew else {
            session.drawingEnded(drew: false, drawing: nil)
            return false
        }

        let program = result.keptTheDrawing && followUp != nil ? (followUp!.program + "\n" + result.program) : result.program
        let questions = (followUp?.questions ?? []) + [transcript]
        let spokenLines = (result.keptTheDrawing ? (followUp?.spokenLines ?? []) : []) + result.spokenLines
        session.drawingEnded(drew: true, drawing: DrawnAnswer(questions: questions, program: program, canvasWidth: canvasWidth,
                                                              canvasHeight: canvasHeight, spokenLines: spokenLines))
        AppLog.append("draw: drew \(result.spokenLines.count) lines in \(String(format: "%.1f", Date().timeIntervalSince(askStartedAt))) s")
        rememberExchange(userTranscript: transcript, assistantResponse: result.spokenLines.joined(separator: " "))
        if let unspoken = result.unspokenAfterVoiceFailure, !unspoken.isEmpty {
            dismissCursorCaption()
            speakWithSystemVoice(unspoken.joined(separator: " "))
        }
        if !Task.isCancelled {
            await minimiseDrawingToBubble(reason: "the drawing is done")
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
        session.minimised()
        if let screen {
            let island = notchHUDManager.islandFrame(on: screen)
            session.bubble.show(thumbnail: thumbnail ?? NSImage(size: NSSize(width: 96, height: 64)),
                                islandFrame: island?.frame, placement: island?.placement ?? notchHUDManager.placement, screen: screen)
        }
        session.stage.hide(reason: reason)
        AppLog.append("draw: minimised to the bubble (\(reason))")
    }

    func discardDrawing(reason: String) {
        DrawStageSession.shared.discard(reason: reason)
    }

    /// Esc with a drawing: one playing stops its voice and goes to its bubble (the ask lane's
    /// turn then ends on its own); a finished one, open or in its bubble, is cleared.
    func escapeStopsTheDrawing() {
        let session = DrawStageSession.shared
        switch session.state {
        case .playing:
            session.stopPlaying(reason: "esc")
            showAskCaption("stopped", holdSeconds: 1.5)
        case .open, .bubble:
            discardDrawing(reason: "esc")
        case .none:
            break
        }
    }
}
