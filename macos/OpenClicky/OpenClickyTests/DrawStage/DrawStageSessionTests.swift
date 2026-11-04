//
//  DrawStageSessionTests.swift
//  OpenClickyTests
//
//  The stage's state across questions: an attempt that draws nothing gives back what was there
//  (the bubble, or the open drawing), an interrupted drawing stays open for a follow-up, and the
//  display going away stops or discards the drawing instead of leaving a voice and a bubble behind.
//

import AppKit
import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
final class FakeDrawStageBubble: DrawStageBubbling {
    private(set) var showCount = 0
    private(set) var dismissCount = 0
    private(set) var isShowing = false

    func show(thumbnail: NSImage, islandFrame: CGRect?, placement: HUDPlacement, screen: NSScreen) {
        showCount += 1
        isShowing = true
    }

    func dismiss() {
        dismissCount += 1
        isShowing = false
    }
}

@MainActor
struct DrawStageSessionTests {
    private let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
    private let firstDrawing = DrawnAnswer(questions: ["how does a heart work?"], program: "step(1); say(\"a\")", canvasWidth: 1280, canvasHeight: 800, spokenLines: ["a"])

    private func makeSession() -> (DrawStageSession, FakeDrawStagePage, FakeDrawStageBubble, Counter) {
        let page = FakeDrawStagePage()
        let bubble = FakeDrawStageBubble()
        let counter = Counter()
        let session = DrawStageSession(stage: page.controller, bubble: bubble, stopVoiceNow: { counter.voiceStops += 1 })
        return (session, page, bubble, counter)
    }

    final class Counter { var voiceStops = 0 }

    private func parkedInBubble(_ session: DrawStageSession, _ bubble: FakeDrawStageBubble) {
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        session.drawingEnded(drew: true, drawing: firstDrawing)
        session.minimised(thumbnail: NSImage(size: NSSize(width: 96, height: 64)), islandFrame: nil, placement: .hidden, screen: NSScreen.screens[0])
        #expect(session.state == .bubble)
        #expect(bubble.isShowing)
    }

    @Test func aNewAttemptThatDrawsNothingBringsThePreviousBubbleBack() {
        let (session, _, bubble, _) = makeSession()
        parkedInBubble(session, bubble)
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        #expect(!bubble.isShowing)
        session.drawingEnded(drew: false, drawing: nil)
        #expect(session.state == .bubble)
        #expect(session.current == firstDrawing)
        #expect(bubble.isShowing)
        #expect(bubble.showCount == 2)
    }

    @Test func aNewDrawingReplacesTheBubble() {
        let (session, _, bubble, _) = makeSession()
        parkedInBubble(session, bubble)
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        let second = DrawnAnswer(questions: ["how does a lung work?"], program: "step(1); say(\"b\")", canvasWidth: 1280, canvasHeight: 800, spokenLines: ["b"])
        session.drawingEnded(drew: true, drawing: second)
        #expect(session.state == .open)
        #expect(session.current == second)
        #expect(!bubble.isShowing)
    }

    @Test func aFollowUpThatDrawsNothingLeavesTheDrawingOpen() {
        let (session, _, _, _) = makeSession()
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        session.drawingEnded(drew: true, drawing: firstDrawing)
        #expect(session.state == .open)
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        session.drawingEnded(drew: false, drawing: nil)
        #expect(session.state == .open)
        #expect(session.current == firstDrawing)
    }

    @Test func anInterruptedDrawingStaysOpenWithWhatWasDrawn() {
        let (session, _, _, _) = makeSession()
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: DrawingAnswerControl())
        session.interrupted(question: "how does a heart work?", programSoFar: "step(1); say(\"a\")", linesHeard: ["a"], canvasWidth: 1280, canvasHeight: 800)
        #expect(session.state == .open)
        #expect(session.current?.program == "step(1); say(\"a\")")
        #expect(session.current?.questions == ["how does a heart work?"])
    }

    @Test func stoppingAPlayingDrawingStopsTheVoiceAndTheRun() {
        let (session, page, _, counter) = makeSession()
        let control = DrawingAnswerControl()
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: control)
        session.stopPlaying(reason: "esc")
        #expect(control.wasStopped)
        #expect(counter.voiceStops == 1)
        #expect(page.surface.postedTypes.contains("finishNow"))
    }

    @Test func theDisplayGoingAwayWhilePlayingStopsTheDrawing() {
        let (session, _, _, counter) = makeSession()
        let control = DrawingAnswerControl()
        session.drawingStarted(on: screen, speech: FakeSpeech.make(played: { _ in }), control: control)
        session.displayGone()
        #expect(control.wasStopped)
        #expect(counter.voiceStops == 1)
    }

    @Test func theDisplayGoingAwayInTheBubbleDiscardsIt() {
        let (session, page, bubble, _) = makeSession()
        parkedInBubble(session, bubble)
        session.displayGone()
        #expect(session.state == .none)
        #expect(session.current == nil)
        #expect(!bubble.isShowing)
        #expect(page.surface.postedTypes.contains("clear"))
    }
}
