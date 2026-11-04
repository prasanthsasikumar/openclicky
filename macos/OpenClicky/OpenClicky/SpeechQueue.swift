//
//  SpeechQueue.swift
//  OpenClicky
//
//  Speaks an answer piece by piece while the rest is still being written. A piece's audio is
//  requested up to two pieces ahead of the one playing, so the next piece is usually ready by the
//  time the current one finishes, and an interrupted answer has bought at most two pieces nobody
//  hears; playback always follows the order of the text. The fetch and play steps
//  are injected, so the ordering, the first-audio signal and the failure hand-off are tested
//  without the network or a speaker.
//

import Foundation

@MainActor
final class SpeechQueue {
    /// A piece's audio could not be fetched or played. `unspokenText` is that piece and every one
    /// after it (including pieces enqueued later), so the caller can say them another way.
    struct Failure: Error {
        let unspokenText: [String]
        let underlyingError: Error
    }

    private let fetchAudio: (String) async throws -> Data
    private let playAudio: (Data) async throws -> Void

    private var pieceTexts: [String] = []
    /// What the caller named each piece (the drawing stage's line number); its index when it gave none.
    private var pieceTags: [Int] = []
    /// The fetches started so far, in piece order (a prefix of `pieceTexts`).
    private var pieceFetches: [Task<Data, Error>] = []
    /// The piece `run` is waiting for or playing; fetches reach `piecesFetchedAhead` beyond it.
    private var currentPieceIndex = 0
    private static let piecesFetchedAhead = 2
    private var isFinished = false
    /// Set by `cancel()`: nothing more is played, even when the current piece ends normally
    /// (a stopped player finishes its piece as if it had played to the end).
    private var isCancelled = false
    /// After a failure, later pieces are only collected (for the caller's fallback), never fetched.
    private var hasFailed = false
    /// Resumed when a piece is enqueued or the queue is finished, so `run` can stop waiting.
    private var waitingForMorePieces: CheckedContinuation<Void, Never>?

    init(fetchAudio: @escaping (String) async throws -> Data, playAudio: @escaping (Data) async throws -> Void) {
        self.fetchAudio = fetchAudio
        self.playAudio = playAudio
    }

    func enqueue(_ pieceText: String) {
        enqueue(pieceText, tag: nil)
    }

    /// `tag` names the piece to the start/end hooks of `run` (the drawing stage passes its line
    /// number); without one the piece is known by its index.
    func enqueue(_ pieceText: String, tag: Int?) {
        guard !isFinished else { return }
        pieceTags.append(tag ?? pieceTexts.count)
        pieceTexts.append(pieceText)
        startFetchesWithinReach()
        wakeRunLoop()
    }

    /// No more pieces will come: `run` returns once the last one has played.
    func finish() {
        isFinished = true
        wakeRunLoop()
    }

    /// Plays every piece in order until `finish()` and the last piece are done. `onFirstAudio` runs
    /// once, right before the first piece starts playing. `onPieceStarted` and `onPieceEnded` get
    /// each piece's tag just before its audio starts and right after it ends normally (the drawing
    /// stage draws a line's beat between the two).
    func run(onFirstAudio: () -> Void, onPieceStarted: ((Int) -> Void)? = nil, onPieceEnded: ((Int) -> Void)? = nil) async throws {
        // However playback ends (done, failed, interrupted), no fetch is left running.
        defer { cancelPendingFetches() }
        var nextPieceIndex = 0
        while true {
            try checkNotCancelled()
            if nextPieceIndex < pieceTexts.count {
                currentPieceIndex = nextPieceIndex
                startFetchesWithinReach()
                do {
                    let audioData = try await pieceFetches[nextPieceIndex].value
                    try checkNotCancelled()
                    if nextPieceIndex == 0 { onFirstAudio() }
                    let pieceTag = pieceTags[nextPieceIndex]
                    onPieceStarted?(pieceTag)
                    try await playAudio(audioData)
                    onPieceEnded?(pieceTag)
                } catch is CancellationError {
                    throw CancellationError()
                } catch where isCancelled || Task.isCancelled {
                    // A fetch cancelled by `cancel()` fails with URLError.cancelled, not a
                    // CancellationError: that is an interruption, not a voice failure to fall back from.
                    throw CancellationError()
                } catch {
                    // Wait for the rest of the answer, then hand back everything not yet spoken.
                    hasFailed = true
                    cancelPendingFetches()
                    while !isFinished {
                        await withCheckedContinuation { waitingForMorePieces = $0 }
                    }
                    cancelPendingFetches()
                    throw Failure(unspokenText: Array(pieceTexts[nextPieceIndex...]), underlyingError: error)
                }
                nextPieceIndex += 1
                continue
            }
            if isFinished { return }
            await withCheckedContinuation { waitingForMorePieces = $0 }
        }
    }

    /// Stops fetching and playing: used when the person speaks again or the answer is abandoned.
    func cancel() {
        isFinished = true
        isCancelled = true
        cancelPendingFetches()
        wakeRunLoop()
    }

    private func checkNotCancelled() throws {
        if isCancelled { throw CancellationError() }
        try Task.checkCancellation()
    }

    /// Starts the fetches for every enqueued piece up to `piecesFetchedAhead` beyond the current one.
    private func startFetchesWithinReach() {
        guard !hasFailed, !isCancelled else { return }
        let lastPieceIndexWithinReach = min(pieceTexts.count - 1, currentPieceIndex + Self.piecesFetchedAhead)
        while pieceFetches.count <= lastPieceIndexWithinReach {
            let pieceText = pieceTexts[pieceFetches.count]
            let fetchAudio = self.fetchAudio
            // At most two requests at once (ElevenLabs caps concurrent requests on smaller plans): a
            // piece waits for the fetch two places ahead of it before starting its own.
            let fetchTwoPlacesAhead = pieceFetches.count >= 2 ? pieceFetches[pieceFetches.count - 2] : nil
            pieceFetches.append(Task {
                _ = try? await fetchTwoPlacesAhead?.value
                try Task.checkCancellation()
                return try await fetchAudio(pieceText)
            })
        }
    }

    private func cancelPendingFetches() {
        pieceFetches.forEach { $0.cancel() }
    }

    private func wakeRunLoop() {
        let continuation = waitingForMorePieces
        waitingForMorePieces = nil
        continuation?.resume()
    }
}

/// One ask-lane answer being spoken as it streams: the chunker cuts the text, the queue fetches and
/// plays each piece, and the pointer is moved when the voice starts (or, when the tag only shows up
/// at the end, as soon as it is known).
@MainActor
final class StreamingAnswerSpeaker {
    private var chunker = SpokenChunker()
    private let speechQueue: SpeechQueue
    private let onVoiceStarts: () -> Void
    private let onPointTag: (String) -> Void
    private var hasVoiceStarted = false
    private var hasHandledPointTag = false
    private var enqueuedPieceCount = 0
    /// A new question interrupted this answer: its voice must not start and its pointer must not fly.
    private(set) var isCancelled = false

    init(speechQueue: SpeechQueue, onVoiceStarts: @escaping () -> Void, onPointTag: @escaping (String) -> Void) {
        self.speechQueue = speechQueue
        self.onVoiceStarts = onVoiceStarts
        self.onPointTag = onPointTag
    }

    /// Plays the pieces as they come; throws `SpeechQueue.Failure` with what was left unsaid.
    func play() async throws {
        try await speechQueue.run(onFirstAudio: { [weak self] in self?.voiceDidStart() })
    }

    /// The whole answer so far, from the stream.
    func receive(accumulatedAnswer: String) {
        chunker.feed(accumulated: accumulatedAnswer).forEach(enqueue)
    }

    /// The stream is over. `closingSentences` (what a quick action did) are spoken after the answer.
    func finish(closingSentences: [String]) {
        chunker.finish().forEach(enqueue)
        closingSentences.filter { !$0.isEmpty }.forEach(enqueue)
        speechQueue.finish()
        // A trailing tag is only known now; with nothing to say there is no voice to wait for.
        if hasVoiceStarted || enqueuedPieceCount == 0 { handlePointTagIfKnown() }
    }

    /// A reply that is already whole (the agent's result): handed over in one go and closed, so the
    /// chunker cuts it into pieces exactly as it would a streamed answer and the queue fetches each
    /// piece while the one before it plays, instead of one long request for the whole reply.
    func receiveWholeReply(_ replyText: String) {
        receive(accumulatedAnswer: replyText)
        finish(closingSentences: [])
    }

    /// The voice could not be used: the caller speaks another way and points now.
    func voiceFellBack() {
        handlePointTagIfKnown()
    }

    func cancel() {
        isCancelled = true
        speechQueue.cancel()
    }

    private func enqueue(_ pieceText: String) {
        enqueuedPieceCount += 1
        speechQueue.enqueue(pieceText)
    }

    private func voiceDidStart() {
        guard !isCancelled else { return }
        hasVoiceStarted = true
        onVoiceStarts()
        handlePointTagIfKnown()
    }

    private func handlePointTagIfKnown() {
        // An [UNSURE] answer never points, even when the model sent a tag with it.
        guard !isCancelled, !hasHandledPointTag, !chunker.isUnsure, let pointTag = chunker.pointTag else { return }
        hasHandledPointTag = true
        onPointTag(pointTag)
    }
}
