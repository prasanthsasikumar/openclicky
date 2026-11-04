import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct SpeechQueueTests {
    private struct FetchFailed: Error {}

    @Test func piecesPlayInOrderAndTheFirstAudioIsReportedOnce() async throws {
        var played: [String] = []
        var firstAudioCount = 0
        let queue = SpeechQueue(
            fetchAudio: { text in
                // The second piece arrives first: order must still follow the text.
                if text == "one" { try await Task.sleep(nanoseconds: 30_000_000) }
                return Data(text.utf8)
            },
            playAudio: { data in played.append(String(decoding: data, as: UTF8.self)) }
        )
        let playback = Task { try await queue.run(onFirstAudio: { firstAudioCount += 1 }) }
        queue.enqueue("one")
        queue.enqueue("two")
        queue.finish()
        try await playback.value
        #expect(played == ["one", "two"])
        #expect(firstAudioCount == 1)
    }

    @Test func pieceHooksFireInOrderWithTheirTags() async throws {
        var events: [String] = []
        let queue = SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { data in events.append("play \(String(decoding: data, as: UTF8.self))") })
        queue.enqueue("a", tag: 10)
        queue.enqueue("b", tag: 11)
        queue.finish()
        try await queue.run(onFirstAudio: { events.append("first") },
                            onPieceStarted: { events.append("start \($0)") },
                            onPieceEnded: { events.append("end \($0)") })
        #expect(events == ["first", "start 10", "play a", "end 10", "start 11", "play b", "end 11"])
    }

    @Test func untaggedPiecesReportTheirIndex() async throws {
        var events: [String] = []
        let queue = SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { _ in })
        queue.enqueue("a")
        queue.enqueue("b")
        queue.finish()
        try await queue.run(onFirstAudio: {}, onPieceStarted: { events.append("start \($0)") }, onPieceEnded: { events.append("end \($0)") })
        #expect(events == ["start 0", "end 0", "start 1", "end 1"])
    }

    @Test func runWaitsForPiecesThatArriveLater() async throws {
        var played: [String] = []
        let queue = SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { played.append(String(decoding: $0, as: UTF8.self)) })
        let playback = Task { try await queue.run(onFirstAudio: {}) }
        try await Task.sleep(nanoseconds: 20_000_000)
        queue.enqueue("late")
        queue.finish()
        try await playback.value
        #expect(played == ["late"])
    }

    @Test func aFailedPieceReportsWhatWasNotSpoken() async {
        var played: [String] = []
        let queue = SpeechQueue(
            fetchAudio: { text in
                if text == "two" { throw FetchFailed() }
                return Data(text.utf8)
            },
            playAudio: { played.append(String(decoding: $0, as: UTF8.self)) }
        )
        let playback = Task { try await queue.run(onFirstAudio: {}) }
        queue.enqueue("one")
        queue.enqueue("two")
        queue.enqueue("three")
        queue.finish()
        do {
            try await playback.value
            Issue.record("expected the failure to surface")
        } catch let failure as SpeechQueue.Failure {
            #expect(failure.unspokenText == ["two", "three"])
        } catch {
            Issue.record("unexpected error \(error)")
        }
        #expect(played == ["one"])
    }

    @Test func anEmptyQueueFinishesWithoutAudio() async throws {
        var firstAudioCount = 0
        let queue = SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { _ in })
        let playback = Task { try await queue.run(onFirstAudio: { firstAudioCount += 1 }) }
        queue.finish()
        try await playback.value
        #expect(firstAudioCount == 0)
    }
    // F-10: an interrupted answer must not keep buying audio nobody will hear.
    @Test func cancellingPlaybackStopsTheRemainingFetches() async throws {
        var fetched: [String] = []
        let queue = SpeechQueue(
            fetchAudio: { text in
                try await Task.sleep(nanoseconds: 50_000_000)
                fetched.append(text)
                return Data(text.utf8)
            },
            playAudio: { _ in try await Task.sleep(nanoseconds: 300_000_000) })
        ["p1", "p2", "p3", "p4", "p5", "p6"].forEach(queue.enqueue)
        queue.finish()
        let playback = Task { try await queue.run(onFirstAudio: {}) }
        try await Task.sleep(nanoseconds: 150_000_000)
        playback.cancel()
        _ = try? await playback.value
        try await Task.sleep(nanoseconds: 1_000_000_000)
        #expect(fetched.count <= 3)
    }

    // F-11: a stop that ends the current piece normally (stopPlayback) must not move on to the next
    // piece once the queue was cancelled.
    @Test func aCancelledQueuePlaysNothingAfterTheCurrentPiece() async throws {
        var played: [String] = []
        var finishCurrentPiece: CheckedContinuation<Void, Never>?
        let queue = SpeechQueue(
            fetchAudio: { Data($0.utf8) },
            playAudio: { data in
                let pieceText = String(decoding: data, as: UTF8.self)
                played.append(pieceText)
                // Only the first piece waits to be stopped; a second one returns at once, so this
                // test fails instead of hanging when the queue moves on.
                if pieceText == "one" { await withCheckedContinuation { finishCurrentPiece = $0 } }
            })
        queue.enqueue("one")
        queue.enqueue("two")
        queue.finish()
        let playback = Task { try await queue.run(onFirstAudio: {}) }
        while finishCurrentPiece == nil { try await Task.sleep(nanoseconds: 5_000_000) }
        queue.cancel()
        finishCurrentPiece?.resume()
        _ = try? await playback.value
        #expect(played == ["one"])
    }

    @Test func aCancelledSpeakerNeverStartsTheVoiceOrPoints() async throws {
        var voiceStarts = 0
        var pointTags: [String] = []
        let speaker = StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(fetchAudio: { Data($0.utf8) }, playAudio: { _ in }),
            onVoiceStarts: { voiceStarts += 1 },
            onPointTag: { pointTags.append($0) })
        let playback = Task { try await speaker.play() }
        speaker.cancel()
        speaker.receive(accumulatedAnswer: "[POINT:10,20:x] that is the save button. more")
        speaker.finish(closingSentences: [])
        speaker.voiceFellBack()
        _ = try? await playback.value
        #expect(voiceStarts == 0)
        #expect(pointTags.isEmpty)
    }

    // The agent's result arrives whole. It used to go out as one /tts request (which the backend
    // cuts at 400 characters) and nothing could stop it; now it is cut into pieces like an answer.
    @Test func aWholeAgentReplyIsSpokenInPiecesInOrder() async throws {
        let firstSentence = "done, i made the folder on your desktop and put the three reports in it."
        let laterSentences = Array(repeating: "each report is named after the month it covers so they sort in order.", count: 4)
        let wholeReply = ([firstSentence] + laterSentences).joined(separator: " ") + " i saved 3 files."
        var fetchedPieces: [String] = []
        var playedPieces: [String] = []
        var voiceStarts = 0
        let speaker = StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(
                fetchAudio: { pieceText in fetchedPieces.append(pieceText); return Data(pieceText.utf8) },
                playAudio: { playedPieces.append(String(decoding: $0, as: UTF8.self)) }),
            onVoiceStarts: { voiceStarts += 1 },
            onPointTag: { _ in Issue.record("an agent reply has no point tag") })
        let playback = Task { try await speaker.play() }
        speaker.receiveWholeReply(wholeReply)
        try await playback.value
        #expect(playedPieces.count > 1)
        #expect(playedPieces.first == firstSentence)
        #expect(playedPieces == fetchedPieces)
        #expect(playedPieces.joined(separator: " ") == wholeReply)
        #expect(voiceStarts == 1)
    }

    @Test func aWholeReplyWhoseVoiceFailsHandsBackWhatWasNotSaid() async {
        let firstPiece = "the first part was said out loud."
        let wholeReply = firstPiece + " " + String(repeating: "the rest could not be fetched at all. ", count: 5)
        var playedPieces: [String] = []
        let speaker = StreamingAnswerSpeaker(
            speechQueue: SpeechQueue(
                fetchAudio: { pieceText in
                    if pieceText != firstPiece { throw FetchFailed() }
                    return Data(pieceText.utf8)
                },
                playAudio: { playedPieces.append(String(decoding: $0, as: UTF8.self)) }),
            onVoiceStarts: {},
            onPointTag: { _ in })
        let playback = Task { try await speaker.play() }
        speaker.receiveWholeReply(wholeReply)
        do {
            try await playback.value
            Issue.record("expected the failure to surface")
        } catch let failure as SpeechQueue.Failure {
            #expect(playedPieces == [firstPiece])
            let everythingSaidOrHandedBack = (playedPieces + failure.unspokenText).joined(separator: " ")
            #expect(everythingSaidOrHandedBack == wholeReply.trimmingCharacters(in: .whitespaces))
        } catch {
            Issue.record("unexpected error \(error)")
        }
    }
}
