//
//  AccountVoiceGuard.swift
//  OpenClicky
//
//  Keeps a slow or failing account voice (the backend's /tts, ElevenLabs) from making anyone wait.
//  Each answer gets an `AnswerVoiceSession`: a piece whose account audio is late or fails is spoken
//  by the fastest local voice (Kokoro if it is downloaded, else this Mac), and so is the rest of that
//  answer, so the voice changes at most once. `AccountVoiceRest` remembers troubled answers: two in
//  a row rest the account voice for ten minutes, during which Automatic resolves past it (a voice
//  picked by hand only ever falls back for one answer). The deadlines, the rest and the session are
//  unit-tested with injected fetches and clock.
//

import Foundation

/// How long an account piece may take before the local voice speaks instead.
enum AccountVoiceDeadline {
    /// The first piece is what the person waits for in silence after "thinking…".
    static let firstPieceSeconds: TimeInterval = 2.5
    /// Later pieces are fetched while earlier ones play, so they have room before anyone hears a gap.
    static let laterPieceSeconds: TimeInterval = 6

    static func seconds(forPieceNumber pieceNumber: Int) -> TimeInterval {
        pieceNumber == 0 ? firstPieceSeconds : laterPieceSeconds
    }
}

/// Troubled answers in a row, and the rest they earn the account voice.
struct AccountVoiceRestRule {
    static let troubledAnswersBeforeResting = 2
    static let restingSeconds: TimeInterval = 10 * 60

    private(set) var consecutiveTroubledAnswers = 0
    private(set) var restingUntil: Date?

    func isResting(now: Date) -> Bool {
        guard let restingUntil else { return false }
        return now < restingUntil
    }

    /// An answer whose first account piece arrived in time.
    mutating func recordAnswerOnTime() {
        consecutiveTroubledAnswers = 0
    }

    /// An answer that had to fall back. Returns true when this one starts a rest.
    mutating func recordTroubledAnswer(now: Date) -> Bool {
        consecutiveTroubledAnswers += 1
        guard consecutiveTroubledAnswers >= Self.troubledAnswersBeforeResting else { return false }
        consecutiveTroubledAnswers = 0
        restingUntil = now.addingTimeInterval(Self.restingSeconds)
        return true
    }
}

/// The app's one memory of how the account voice has been doing; `SpeechVoiceChoice.current()`
/// reads it so Automatic (and Kokoro's warm-up) skip the account voice while it rests.
@MainActor
enum AccountVoiceRest {
    private static var rule = AccountVoiceRestRule()

    static var isResting: Bool { rule.isResting(now: Date()) }

    static func recordAnswerOnTime() {
        rule.recordAnswerOnTime()
    }

    static func recordTroubledAnswer() {
        if rule.recordTroubledAnswer(now: Date()) {
            AppLog.append("voice: account resting \(Int(AccountVoiceRestRule.restingSeconds / 60)) min after \(AccountVoiceRestRule.troubledAnswersBeforeResting) troubled answers")
        }
    }
}

/// One answer's voice. `audio(for:)` is `SpeechQueue`'s fetch step; pieces are asked for in text order.
@MainActor
final class AnswerVoiceSession {
    /// Why the account voice was let go for this answer.
    enum AccountTrouble {
        case tooSlow(pieceNumber: Int, seconds: TimeInterval)
        case failed(pieceNumber: Int, error: Error)
    }

    private enum AccountPieceOutcome {
        case audio(Data)
        case tooSlow
        case failed(Error)
        /// Another piece fell back while this one waited: no need to wait out its own deadline.
        case abandoned
    }

    private let speaksWithAccountVoice: Bool
    private let fetchAccountAudio: (String) async throws -> Data
    private let fetchFallbackAudio: (String) async throws -> Data
    private let deadlineSeconds: (Int) -> TimeInterval
    private let onFirstAccountAudio: (TimeInterval) -> Void
    private let onAccountTrouble: (AccountTrouble) -> Void
    private let onAnswerOnTime: () -> Void
    private let onTroubledAnswer: () -> Void

    private var nextPieceNumber = 0
    /// Set once a piece fell back: every later piece, including ones already in flight on the
    /// account voice, is spoken by the fallback so the voice changes only once.
    private(set) var hasFallenBack = false

    /// `fetchAccountAudio` is only used when `speaksWithAccountVoice`; otherwise every piece goes to
    /// `fetchFallbackAudio`, which is then simply the voice that speaks.
    init(speaksWithAccountVoice: Bool,
         fetchAccountAudio: @escaping (String) async throws -> Data,
         fetchFallbackAudio: @escaping (String) async throws -> Data,
         deadlineSeconds: @escaping (Int) -> TimeInterval = AccountVoiceDeadline.seconds(forPieceNumber:),
         onFirstAccountAudio: @escaping (TimeInterval) -> Void = { _ in },
         onAccountTrouble: @escaping (AccountTrouble) -> Void = { _ in },
         onAnswerOnTime: @escaping () -> Void = {},
         onTroubledAnswer: @escaping () -> Void = {}) {
        self.speaksWithAccountVoice = speaksWithAccountVoice
        self.fetchAccountAudio = fetchAccountAudio
        self.fetchFallbackAudio = fetchFallbackAudio
        self.deadlineSeconds = deadlineSeconds
        self.onFirstAccountAudio = onFirstAccountAudio
        self.onAccountTrouble = onAccountTrouble
        self.onAnswerOnTime = onAnswerOnTime
        self.onTroubledAnswer = onTroubledAnswer
    }

    func audio(for pieceText: String) async throws -> Data {
        let pieceNumber = nextPieceNumber
        nextPieceNumber += 1
        guard speaksWithAccountVoice, !hasFallenBack else { return try await fetchFallbackAudio(pieceText) }

        let deadline = deadlineSeconds(pieceNumber)
        let startedAt = Date()
        let outcome = await accountAudioWithinDeadline(pieceText, deadlineSeconds: deadline)
        // A stopped answer (esc, a new question) is an interruption, never a reason to fall back.
        try Task.checkCancellation()

        switch outcome {
        case .audio(let audioData):
            // Another piece fell back while this one was in flight: keep the answer in one voice.
            if hasFallenBack { return try await fetchFallbackAudio(pieceText) }
            if pieceNumber == 0 {
                onFirstAccountAudio(Date().timeIntervalSince(startedAt))
                onAnswerOnTime()
            }
            return audioData
        case .tooSlow:
            fallBack(because: .tooSlow(pieceNumber: pieceNumber, seconds: deadline))
            return try await fetchFallbackAudio(pieceText)
        case .failed(let error):
            fallBack(because: .failed(pieceNumber: pieceNumber, error: error))
            return try await fetchFallbackAudio(pieceText)
        case .abandoned:
            return try await fetchFallbackAudio(pieceText)
        }
    }

    private func fallBack(because trouble: AccountTrouble) {
        guard !hasFallenBack else { return }
        hasFallenBack = true
        onAccountTrouble(trouble)
        onTroubledAnswer()
    }

    /// The account fetch raced against the deadline and against another piece falling back; the
    /// losers are cancelled.
    private func accountAudioWithinDeadline(_ pieceText: String, deadlineSeconds: TimeInterval) async -> AccountPieceOutcome {
        let fetchAccountAudio = self.fetchAccountAudio
        return await withTaskGroup(of: AccountPieceOutcome.self) { group in
            group.addTask {
                do {
                    return .audio(try await fetchAccountAudio(pieceText))
                } catch {
                    return .failed(error)
                }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(deadlineSeconds * 1_000_000_000))
                return .tooSlow
            }
            group.addTask { [weak self] in
                // Polled rather than awaited on a continuation, so cancelling this task always ends it.
                while !Task.isCancelled {
                    if await self?.hasFallenBack ?? true { return .abandoned }
                    try? await Task.sleep(nanoseconds: 100_000_000)
                }
                return .abandoned
            }
            let firstOutcome = await group.next() ?? .tooSlow
            group.cancelAll()
            return firstOutcome
        }
    }
}
