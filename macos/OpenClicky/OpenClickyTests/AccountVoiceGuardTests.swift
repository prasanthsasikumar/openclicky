//
//  AccountVoiceGuardTests.swift
//  OpenClickyTests
//
//  A late or failing free voice never makes the person wait: the piece and the rest of the answer
//  go to the local voice, a stop is never mistaken for trouble, and two troubled answers in a row
//  rest the free voice for ten minutes in Automatic only.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct AnswerVoiceSessionTests {
    private struct VoiceRefused: Error {}

    private static func audio(_ voice: String, _ text: String) -> Data { Data("\(voice):\(text)".utf8) }
    private static func spoken(_ data: Data) -> String { String(decoding: data, as: UTF8.self) }

    /// Records what the session reports, and makes the session. The deadlines are generous: on a
    /// busy CI runner the main actor can stall for seconds, and a piece that answers at once then
    /// missed a half-second deadline (every on-time test failed there, 2026-10-29). A late piece
    /// sleeps far past them instead.
    @MainActor
    private final class Harness {
        var troubles: [AnswerVoiceSession.AccountTrouble] = []
        var answersOnTime = 0
        var troubledAnswers = 0
        var firstSoundCount = 0

        func session(accountDelaySeconds: @escaping (String) -> TimeInterval = { _ in 0 },
                     accountFails: @escaping (String) -> Bool = { _ in false },
                     speaksWithAccountVoice: Bool = true,
                     firstDeadline: TimeInterval = 10, laterDeadline: TimeInterval = 20) -> AnswerVoiceSession {
            AnswerVoiceSession(
                speaksWithAccountVoice: speaksWithAccountVoice,
                fetchAccountAudio: { text in
                    try await Task.sleep(nanoseconds: UInt64(accountDelaySeconds(text) * 1_000_000_000))
                    if accountFails(text) { throw VoiceRefused() }
                    return AnswerVoiceSessionTests.audio("account", text)
                },
                fetchFallbackAudio: { text in AnswerVoiceSessionTests.audio("local", text) },
                deadlineSeconds: { pieceNumber in pieceNumber == 0 ? firstDeadline : laterDeadline },
                onFirstAccountAudio: { [weak self] _ in self?.firstSoundCount += 1 },
                onAccountTrouble: { [weak self] trouble in self?.troubles.append(trouble) },
                onAnswerOnTime: { [weak self] in self?.answersOnTime += 1 },
                onTroubledAnswer: { [weak self] in self?.troubledAnswers += 1 }
            )
        }
    }

    @Test func anAccountVoiceInTimeSpeaksEveryPiece() async throws {
        let harness = Harness()
        let session = harness.session()
        #expect(Self.spoken(try await session.audio(for: "one")) == "account:one")
        #expect(Self.spoken(try await session.audio(for: "two")) == "account:two")
        #expect(harness.answersOnTime == 1)
        #expect(harness.firstSoundCount == 1)
        #expect(harness.troubledAnswers == 0)
    }

    /// The first piece is the silence after "thinking…": past its deadline the local voice speaks it.
    @Test func aLateFirstPieceIsSpokenByTheLocalVoiceAndSoIsTheRest() async throws {
        let harness = Harness()
        let session = harness.session(accountDelaySeconds: { text in text == "one" ? 120 : 0 }, firstDeadline: 1)
        let startedAt = Date()
        #expect(Self.spoken(try await session.audio(for: "one")) == "local:one")
        #expect(Date().timeIntervalSince(startedAt) < 60)
        // The account would answer the second piece at once; the answer stays in one voice.
        #expect(Self.spoken(try await session.audio(for: "two")) == "local:two")
        #expect(harness.troubledAnswers == 1)
        #expect(harness.answersOnTime == 0)
        guard case .tooSlow(pieceNumber: 0, _) = harness.troubles.first else {
            Issue.record("expected the first piece to be too slow, got \(harness.troubles)")
            return
        }
    }

    /// Later pieces are fetched while earlier ones play, so they get the longer deadline.
    @Test func aLaterPieceHasMoreRoomThanTheFirst() async throws {
        let harness = Harness()
        let session = harness.session(accountDelaySeconds: { text in text == "two" ? 2 : 0 }, firstDeadline: 1)
        _ = try await session.audio(for: "one")
        #expect(Self.spoken(try await session.audio(for: "two")) == "account:two")
        #expect(harness.troubles.isEmpty)
    }

    @Test func aFailedPieceIsSpokenByTheLocalVoice() async throws {
        let harness = Harness()
        let session = harness.session(accountFails: { text in text == "two" })
        #expect(Self.spoken(try await session.audio(for: "one")) == "account:one")
        #expect(Self.spoken(try await session.audio(for: "two")) == "local:two")
        #expect(Self.spoken(try await session.audio(for: "three")) == "local:three")
        #expect(harness.troubledAnswers == 1)
        guard case .failed(pieceNumber: 1, _) = harness.troubles.first else {
            Issue.record("expected the second piece to fail, got \(harness.troubles)")
            return
        }
    }

    /// Pieces are fetched ahead: one still in flight on the account voice when an earlier one fell
    /// back is said by the local voice too, without waiting out its own deadline.
    @Test func aPieceInFlightWhenAnotherFallsBackFollowsIt() async throws {
        let harness = Harness()
        let session = harness.session(accountDelaySeconds: { text in text == "one" ? 120 : 60 }, firstDeadline: 1)
        async let first = session.audio(for: "one")
        async let second = session.audio(for: "two")
        let (firstAudio, secondAudio) = try await (first, second)
        #expect(Self.spoken(firstAudio) == "local:one")
        #expect(Self.spoken(secondAudio) == "local:two")
        #expect(harness.troubles.count == 1)
        #expect(harness.troubledAnswers == 1)
    }

    /// Esc or a new question cancels the fetch: that is an interruption, not a voice to give up on.
    @Test func aStoppedAnswerIsNotTrouble() async throws {
        let harness = Harness()
        let session = harness.session(accountDelaySeconds: { _ in 5 })
        let piece = Task { try await session.audio(for: "one") }
        try await Task.sleep(nanoseconds: 20_000_000)
        piece.cancel()
        await #expect(throws: CancellationError.self) { try await piece.value }
        #expect(harness.troubles.isEmpty)
        #expect(harness.troubledAnswers == 0)
    }

    @Test func anotherVoiceGoesStraightToItsOwnFetch() async throws {
        let harness = Harness()
        let session = harness.session(accountDelaySeconds: { _ in 5 }, speaksWithAccountVoice: false)
        #expect(Self.spoken(try await session.audio(for: "one")) == "local:one")
        #expect(harness.troubles.isEmpty)
        #expect(harness.answersOnTime == 0)
    }
}

struct AccountVoiceRestRuleTests {
    @Test func twoTroubledAnswersInARowRestTheVoiceForTenMinutes() {
        var rule = AccountVoiceRestRule()
        let now = Date()
        let firstStartedARest = rule.recordTroubledAnswer(now: now)
        #expect(!firstStartedARest)
        #expect(!rule.isResting(now: now))
        let secondStartedARest = rule.recordTroubledAnswer(now: now)
        #expect(secondStartedARest)
        #expect(rule.isResting(now: now.addingTimeInterval(9 * 60)))
        #expect(!rule.isResting(now: now.addingTimeInterval(10 * 60 + 1)))
    }

    @Test func anAnswerOnTimeInBetweenStartsTheCountAgain() {
        var rule = AccountVoiceRestRule()
        let now = Date()
        _ = rule.recordTroubledAnswer(now: now)
        rule.recordAnswerOnTime()
        let startedARest = rule.recordTroubledAnswer(now: now)
        #expect(!startedARest)
        #expect(!rule.isResting(now: now))
    }

    /// After a rest the next answer tries the free voice again, and it takes two more to rest again.
    @Test func afterARestTheCountStartsFromNothing() {
        var rule = AccountVoiceRestRule()
        let now = Date()
        _ = rule.recordTroubledAnswer(now: now)
        _ = rule.recordTroubledAnswer(now: now)
        let later = now.addingTimeInterval(11 * 60)
        let startedARest = rule.recordTroubledAnswer(now: later)
        #expect(!startedARest)
        #expect(!rule.isResting(now: later))
    }
}

struct AccountVoiceRestResolveTests {
    private func resolve(picked: String?, resting: Bool, kokoro: Bool = false) -> SpeechVoiceChoice {
        var settings = OpenClickyShellSettings()
        settings.speechVoice = picked
        return SpeechVoiceChoice.resolve(settings: settings, hasAccount: true, kokoroInstalled: kokoro,
                                         languageCode: "en", accountVoiceIsResting: resting)
    }

    @Test func automaticPassesARestingFreeVoiceBy() {
        #expect(resolve(picked: nil, resting: false, kokoro: true) == .account)
        #expect(resolve(picked: nil, resting: true, kokoro: true) == .kokoro)
        #expect(resolve(picked: nil, resting: true) == .mac)
    }

    /// Someone who picked the free voice by hand keeps it; only one answer at a time falls back.
    @Test func aFreeVoicePickedByHandIsNeverPassedBy() {
        #expect(resolve(picked: "account", resting: true) == .account)
    }

    @Test func theLocalVoiceIsKokoroWhenItIsDownloadedElseThisMac() {
        #expect(SpeechVoiceChoice.fastestLocalVoice(kokoroInstalled: true, languageCode: "en") == .kokoro)
        #expect(SpeechVoiceChoice.fastestLocalVoice(kokoroInstalled: false, languageCode: "en") == .mac)
    }
}
