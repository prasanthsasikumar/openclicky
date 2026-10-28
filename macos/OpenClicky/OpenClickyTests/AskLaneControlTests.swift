//
//  AskLaneControlTests.swift
//  OpenClickyTests
//
//  Esc stops the ask lane at any time, except that a dictation take in progress keeps esc to itself.
//  A question pauses before its screen is sent, unless the pause is off or a guide asks for itself.
//  An [UNSURE] reply is never spoken with its marker and never points, even with a tag.
//

import Foundation
import Testing
@testable import OpenClicky

struct AskLaneControlTests {
    @Test func aDictationTakeKeepsEscToItself() {
        #expect(AskLaneEscape.target(dictationTakeInProgress: true, askLaneCanBeStopped: true) == .dictationTake)
        #expect(AskLaneEscape.target(dictationTakeInProgress: true, askLaneCanBeStopped: false) == .dictationTake)
    }

    @Test func escStopsTheAskLaneWhenItHasSomethingUnderWay() {
        #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: true) == .askLane)
        #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: false) == .nothing)
    }

    /// Hands-free (Realtime) answering, speaking or still working on an action: esc stops that
    /// too. Pressing esc while Gmail loaded did nothing, and Clicky read the inbox (2026-10-28).
    @Test func escStopsTheRealtimeLaneWhenItIsBusy() {
        #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: false, realtimeIsBusy: true) == .realtime)
        #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: true, realtimeIsBusy: true) == .askLane)
        #expect(AskLaneEscape.target(dictationTakeInProgress: true, askLaneCanBeStopped: false, realtimeIsBusy: true) == .dictationTake)
        #expect(AskLaneEscape.target(dictationTakeInProgress: false, askLaneCanBeStopped: false, realtimeIsBusy: false) == .nothing)
    }

    @Test func aPressStillListeningOrTranscribingCanBeStopped() {
        #expect(AskLaneEscape.canStop(pressIsSettled: false, isAskInFlight: false, guideStep: nil))
    }

    @Test func aQuestionOnItsWayCanBeStoppedBeforeAnyGuideStepIsKnown() {
        // The first step of a guide: no step number yet, but the question is in flight.
        #expect(AskLaneEscape.canStop(pressIsSettled: true, isAskInFlight: true, guideStep: nil))
    }

    @Test func aGuideWaitingForAClickCanBeStopped() {
        #expect(AskLaneEscape.canStop(pressIsSettled: true, isAskInFlight: false, guideStep: 2))
    }

    @Test func nothingUnderWayMeansEscIsLeftAlone() {
        #expect(!AskLaneEscape.canStop(pressIsSettled: true, isAskInFlight: false, guideStep: nil))
    }

    @Test func aQuestionPausesBeforeItsScreenIsSent() {
        #expect(AskSendPause.seconds(isEnabled: true, isGuidedStepContinuation: false) == AskSendPause.defaultSeconds)
        #expect(AskSendPause.defaultSeconds == 1.2)
    }

    @Test func noPauseWhenTheSettingIsOff() {
        #expect(AskSendPause.seconds(isEnabled: false, isGuidedStepContinuation: false) == nil)
    }

    /// The person already let the task go ahead: a pause after every click would only slow it down.
    @Test func aGuidesOwnNextStepNeverPauses() {
        #expect(AskSendPause.seconds(isEnabled: true, isGuidedStepContinuation: true) == nil)
    }

    @Test func thePauseSaysEscCancels() {
        #expect(AskSendPause.caption.contains("esc to cancel"))
        #expect(!AskSendPause.caption.contains("—"))
    }

    @Test func aPauseThatRunsOutLetsTheQuestionGo() async {
        #expect(await AskSendPause.waitOut(seconds: 0.01))
    }

    @Test func aCancelledPauseSendsNothing() async {
        let pause = Task { await AskSendPause.waitOut(seconds: 5) }
        pause.cancel()
        #expect(await pause.value == false)
    }

    /// On by default, and kept once turned off, like its neighbours on the privacy page.
    @Test @MainActor func pauseBeforeSendingIsOnByDefaultAndRemembered() throws {
        let defaults = try #require(UserDefaults(suiteName: "openclicky-tests-\(UUID().uuidString)"))
        #expect(DictationSettings(defaults: defaults).pauseBeforeSendingScreen)
        DictationSettings(defaults: defaults).pauseBeforeSendingScreen = false
        #expect(!DictationSettings(defaults: defaults).pauseBeforeSendingScreen)
    }

    // MARK: [UNSURE]

    @Test func theUnsureMarkerIsFoundHoweverItIsWritten() {
        #expect(UnsureMarker.containsMarker("[UNSURE] look for a gear icon."))
        #expect(UnsureMarker.containsMarker("[ unsure ] look for a gear icon."))
        #expect(!UnsureMarker.containsMarker("i'm not sure that's the one."))
        #expect(UnsureMarker.removingMarkers(from: "[UNSURE] look for a gear icon.") == "look for a gear icon.")
        #expect(!UnsureMarker.islandStatus.contains("—"))
    }

    @Test func theUnsureMarkerIsNeverSpokenAndNothingPoints() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[UNSURE] i can't spot it. look for a gear icon in your dock.") == ["i can't spot it."])
        #expect(chunker.finish() == ["look for a gear icon in your dock."])
        #expect(chunker.isUnsure)
        #expect(chunker.pointTag == nil)
    }

    /// A model that sends a tag with [UNSURE] anyway: the tag is never spoken, and the answer is
    /// still marked unsure so the speaker ignores it.
    @Test func aPointTagAfterTheUnsureMarkerIsNotSpoken() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[UNSURE] [POINT:400,300:gear] it might be the gear icon. ") == ["it might be the gear icon."])
        #expect(chunker.isUnsure)
        #expect(chunker.pointTag == "[POINT:400,300:gear]")
    }

    @Test func aStreamThatCouldStillBeTheUnsureMarkerWaits() {
        var chunker = SpokenChunker()
        #expect(chunker.feed(accumulated: "[UNS") == [])
        #expect(chunker.feed(accumulated: "[UNSURE]") == [])
        #expect(chunker.feed(accumulated: "[UNSURE] [POINT:4") == [])
        #expect(chunker.feed(accumulated: "[UNSURE] [POINT:400,300:gear] tell me what you see. ") == ["tell me what you see."])
    }

    @Test func anAnswerWithoutTheMarkerIsNotUnsure() {
        var chunker = SpokenChunker()
        _ = chunker.feed(accumulated: "[POINT:400,300:gear] click the gear. ")
        #expect(!chunker.isUnsure)
        #expect(chunker.pointTag == "[POINT:400,300:gear]")
    }

    @Test @MainActor func anUnsureAnswerNeverMovesThePointer() async {
        var pointedTags: [String] = []
        let speaker = StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(fetchAudio: { _ in Data() }, playAudio: { _ in }),
            onVoiceStarts: {},
            onPointTag: { pointedTags.append($0) }
        )
        speaker.receive(accumulatedAnswer: "[UNSURE] [POINT:400,300:gear] it might be the gear icon.")
        speaker.finish(closingSentences: [])
        try? await speaker.play()
        #expect(pointedTags.isEmpty)

        var pointedBySure: [String] = []
        let sureSpeaker = StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(fetchAudio: { _ in Data() }, playAudio: { _ in }),
            onVoiceStarts: {},
            onPointTag: { pointedBySure.append($0) }
        )
        sureSpeaker.receive(accumulatedAnswer: "[POINT:400,300:gear] click the gear icon.")
        sureSpeaker.finish(closingSentences: [])
        try? await sureSpeaker.play()
        #expect(pointedBySure == ["[POINT:400,300:gear]"])
    }
}
