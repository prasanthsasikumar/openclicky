//
//  ParakeetDictationTests.swift
//  OpenClickyTests
//
//  The offline Parakeet engine: which languages it takes, when it replaces Apple's recogniser,
//  and the session's live and final words (the helper replaced by an injected transcriber).
//

import Foundation
@testable import OpenClicky
import Testing

struct ParakeetDictationTests {
    @Test func hearsAutoDetectAndEuropeanLanguagesButNotIndianOnes() {
        #expect(ParakeetDictation.hears(bareLanguageCode: nil))
        #expect(ParakeetDictation.hears(bareLanguageCode: "en"))
        #expect(ParakeetDictation.hears(bareLanguageCode: "de"))
        #expect(!ParakeetDictation.hears(bareLanguageCode: "hi"))
        #expect(!ParakeetDictation.hears(bareLanguageCode: "ml"))
    }

    @Test func anAutomaticDownloadReplacesOnlyTheOldOfflineEngine() {
        #expect(ParakeetDictation.engineAfterAutomaticDownload(current: .offline) == .parakeet)
        #expect(ParakeetDictation.engineAfterAutomaticDownload(current: .sarvam) == .sarvam)
        #expect(ParakeetDictation.engineAfterAutomaticDownload(current: .openclicky) == .openclicky)
    }

    @Test func theEngineSaysWhatItNeeds() {
        func reason(installed: Bool, language: String?) -> String? {
            DictationEngineResolver.unavailableReason(for: .parakeet, kind: .signedOut, isConfigured: false, signedIn: false, sarvamKey: nil,
                                                      parakeetInstalled: installed, bareLanguageCode: language)
        }
        #expect(reason(installed: true, language: nil) == nil)
        #expect(reason(installed: false, language: "en")?.contains("download") == true)
        #expect(reason(installed: true, language: "hi")?.contains("language") == true)
    }

    @Test func pcm16BecomesFloatSamples() {
        var pcm = Data()
        for sample: Int16 in [0, 16384, -32768] { withUnsafeBytes(of: sample.littleEndian) { pcm.append(contentsOf: $0) } }
        #expect(ParakeetEngine.floatSamples(fromPCM16: pcm) == [0, 0.5, -1])
    }

    @Test func theFinalWordsReadTheWholeTake() async {
        let heard = HeardLog()
        let session = ParakeetTranscriptionSession(
            onTranscriptUpdate: { _ in }, onFinalTranscriptReady: { heard.final($0) }, onError: { _ in },
            transcribe: { audio in "\(audio.count) bytes" })
        session.appendPCM16(Data(count: 3200))
        session.appendPCM16(Data(count: 3200))
        session.requestFinalTranscript()
        #expect(await heard.waitForFinal() == "6400 bytes")
    }

    @Test func aCancelledTakeSaysNothing() async {
        let heard = HeardLog()
        let session = ParakeetTranscriptionSession(
            onTranscriptUpdate: { _ in }, onFinalTranscriptReady: { heard.final($0) }, onError: { _ in },
            transcribe: { _ in try await Task.sleep(nanoseconds: 200_000_000); return "words" })
        session.appendPCM16(Data(count: 3200))
        session.requestFinalTranscript()
        session.cancel()
        try? await Task.sleep(nanoseconds: 500_000_000)
        #expect(heard.finals.isEmpty)
    }

    @Test func liveWordsArriveWhileTheKeyIsHeld() async {
        let heard = HeardLog()
        let session = ParakeetTranscriptionSession(
            onTranscriptUpdate: { heard.partial($0) }, onFinalTranscriptReady: { heard.final($0) }, onError: { _ in },
            transcribe: { _ in "so far" })
        session.appendPCM16(Data(count: 32_000))
        // The live words come once a second; a busy CI runner can be late, so wait up to 5 s.
        #expect(await heard.waitForPartial("so far"))
        session.cancel()
    }

    /// Every live read re-reads the whole take, so past 30 s of audio the live words stop; the
    /// final words still read all of it.
    @Test func liveWordsStopPastThirtySecondsButTheFinalReadsEverything() async {
        let heard = HeardLog()
        let session = ParakeetTranscriptionSession(
            onTranscriptUpdate: { heard.partial($0) }, onFinalTranscriptReady: { heard.final($0) }, onError: { _ in },
            transcribe: { audio in "\(audio.count) bytes" })
        let longTakeBytes = ParakeetTranscriptionSession.liveWordsAudioLimitBytes + 32_000
        session.appendPCM16(Data(count: longTakeBytes))
        try? await Task.sleep(nanoseconds: 1_400_000_000)
        #expect(heard.partials.isEmpty)
        session.requestFinalTranscript()
        #expect(await heard.waitForFinal() == "\(longTakeBytes) bytes")
    }

    /// The live-words loop holds the session only weakly: a session nobody keeps goes away.
    @Test func theLiveWordsLoopDoesNotKeepTheSessionAlive() {
        weak var releasedSession: ParakeetTranscriptionSession?
        do {
            let session = ParakeetTranscriptionSession(
                onTranscriptUpdate: { _ in }, onFinalTranscriptReady: { _ in }, onError: { _ in },
                transcribe: { _ in "words" })
            releasedSession = session
        }
        #expect(releasedSession == nil)
    }
}

/// What a session reported, from whichever thread it reported on.
private final class HeardLog: @unchecked Sendable {
    private let lock = NSLock()
    private var storedFinals: [String] = []
    private var storedPartials: [String] = []

    var finals: [String] { lock.withLock { storedFinals } }
    var partials: [String] { lock.withLock { storedPartials } }
    func final(_ text: String) { lock.withLock { storedFinals.append(text) } }
    func partial(_ text: String) { lock.withLock { storedPartials.append(text) } }

    func waitForPartial(_ words: String) async -> Bool {
        for _ in 0..<250 {
            if partials.contains(words) { return true }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        return false
    }

    func waitForFinal() async -> String? {
        for _ in 0..<50 {
            if let first = finals.first { return first }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        return nil
    }
}
