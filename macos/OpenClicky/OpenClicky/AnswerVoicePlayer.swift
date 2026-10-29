//
//  AnswerVoicePlayer.swift
//  OpenClicky
//
//  Fetches the audio for one piece of a spoken answer and plays it. The voice is the one picked in
//  Settings → Clicky → Voice (`SpeechVoiceChoice`): a key's voice straight from its vendor, Kokoro
//  or this Mac through `CloudVoiceSpeaker`, and only the account's free voice from the OpenClicky
//  backend's /tts (ElevenLabs). Each piece is fetched whole, not streamed; `SpeechQueue` overlaps
//  fetching the next piece with playing the current one, which is what makes the voice start early.
//

import AVFoundation
import Foundation

@MainActor
final class AnswerVoicePlayer {
    /// The /tts URL, read for every request so a backend changed in shell.json while the app runs
    /// is used by the next answer (it was fixed when the client was built).
    private let currentProxyURL: () -> String
    var proxyURL: URL { Self.resolvedProxyURL(fromProxyURL: currentProxyURL()) }
    private let session: URLSession

    /// The audio player for the current TTS playback. Kept alive so the
    /// audio finishes playing even if the caller doesn't hold a reference.
    private var audioPlayer: AVAudioPlayer?
    /// The voice last logged, so app.log says when it changes rather than for every sentence.
    private var lastLoggedVoice: SpeechVoiceChoice?

    /// `proxyURL` is built from the user-configurable backend URL (shell.json or an environment
    /// override), not a compile-time literal, so a malformed value falls back to the hosted
    /// backend's own /tts endpoint instead of crashing the app. That fallback string is a literal
    /// built from a known-good constant, so force-unwrapping it is safe. Pulled out as a pure static
    /// function so the fallback behavior can be unit tested without constructing a full client.
    nonisolated static func resolvedProxyURL(fromProxyURL proxyURL: String) -> URL {
        URL(string: proxyURL) ?? URL(string: "\(OpenClickyConfiguration.hostedBackendURL)/tts")!
    }

    init(currentProxyURL: @escaping () -> String) {
        self.currentProxyURL = currentProxyURL

        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 60
        self.session = URLSession(configuration: configuration)
    }

    /// The voice for one answer, in the voice `SpeechVoiceChoice.current()` names when the answer
    /// starts. The account's free voice comes from the backend's /tts behind `AnswerVoiceSession`'s
    /// deadlines: a late or failed piece, and the rest of that answer, are spoken by the fastest local
    /// voice. Its `audio(for:)` is `SpeechQueue`'s fetch step.
    func makeAnswerSession() -> AnswerVoiceSession {
        // Settings → Clicky → Voice: a key's voice straight from its vendor, Kokoro or this Mac;
        // only the free voice goes through the backend.
        let voice = SpeechVoiceChoice.current()
        if voice != lastLoggedVoice {
            AppLog.append("voice: \(voice.rawValue)")
            lastLoggedVoice = voice
        }
        let speaksWithAccountVoice = voice == .account
        let fallbackVoice = speaksWithAccountVoice
            ? SpeechVoiceChoice.fastestLocalVoice(kokoroInstalled: KokoroVoice.isInstalled, languageCode: ReplyLanguage.currentCode)
            : voice
        return AnswerVoiceSession(
            speaksWithAccountVoice: speaksWithAccountVoice,
            fetchAccountAudio: { [weak self] pieceText in
                guard let self else { throw CancellationError() }
                return try await self.fetchAccountAudio(for: pieceText)
            },
            fetchFallbackAudio: { [session] pieceText in
                try await CloudVoiceSpeaker.audio(for: pieceText, voice: fallbackVoice, session: session)
            },
            onFirstAccountAudio: { seconds in
                AppLog.append(String(format: "voice: account first sound in %.1f s", seconds))
            },
            onAccountTrouble: { trouble in
                switch trouble {
                case .tooSlow(let pieceNumber, let seconds):
                    AppLog.append(String(format: "voice: account piece %d not back in %.1f s; %@ speaks the rest",
                                         pieceNumber + 1, seconds, fallbackVoice.rawValue))
                case .failed(let pieceNumber, let error):
                    // 402 is the account's voice allowance (tts_budget): the plan, not a failure.
                    let voiceRefusal = error as? BackendHTTPError
                    let isVoiceAllowanceSpent = voiceRefusal?.service == .voice && voiceRefusal?.status == 402
                    if !isVoiceAllowanceSpent {
                        ClickyAnalytics.trackTTSError(error: error.localizedDescription)
                    }
                    AppLog.append("voice: account piece \(pieceNumber + 1) failed (\((error as NSError).code)); \(fallbackVoice.rawValue) speaks the rest")
                }
            },
            onAnswerOnTime: { AccountVoiceRest.recordAnswerOnTime() },
            onTroubledAnswer: { AccountVoiceRest.recordTroubledAnswer() }
        )
    }

    /// The audio for `text` as a one-piece answer, without playing it.
    func fetchAudio(for text: String) async throws -> Data {
        try await makeAnswerSession().audio(for: text)
    }

    /// One piece from the backend's /tts, the account's free ElevenLabs voice.
    private func fetchAccountAudio(for text: String) async throws -> Data {
        var request = URLRequest(url: proxyURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("audio/mpeg", forHTTPHeaderField: "Accept")
        OpenClickyConfiguration.authorize(&request)
        // The backend relays this body to ElevenLabs as it is, the same body an own ElevenLabs key sends.
        request.httpBody = try CloudVoiceRequests.elevenLabsSpeechBody(text: text)

        let (data, response) = try await BackendSignIn.send(request) { [session] in try await session.data(for: $0) }
        return try Self.audio(from: data, response: response)
    }

    /// The audio in a speech answer, or the error that says why there is none.
    private static func audio(from data: Data, response: URLResponse) throws -> Data {
        guard let httpResponse = response as? HTTPURLResponse else {
            throw NSError(domain: "ElevenLabsTTS", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "Invalid response"])
        }

        guard (200...299).contains(httpResponse.statusCode) else {
            throw BackendHTTPError(service: .voice, status: httpResponse.statusCode, body: data)
        }
        return data
    }

    /// Plays `data` and returns when it has finished (or was stopped). Cancelling the calling task
    /// stops the audio.
    func playToCompletion(_ data: Data) async throws {
        try Task.checkCancellation()
        let player = try AVAudioPlayer(data: data)
        let finishObserver = AudioPlayerFinishObserver()
        player.delegate = finishObserver
        self.audioPlayer = player
        await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                finishObserver.onFinish = { continuation.resume() }
                if !player.play() { finishObserver.finishOnce() }
            }
        } onCancel: {
            Task { @MainActor in
                player.stop()
                finishObserver.finishOnce()
            }
        }
        if audioPlayer === player { audioPlayer = nil }
        try Task.checkCancellation()
    }

    /// Whether TTS audio is currently playing back.
    var isPlaying: Bool {
        audioPlayer?.isPlaying ?? false
    }

    /// Stops any in-progress playback immediately.
    func stopPlayback() {
        if let audioPlayer, let finishObserver = audioPlayer.delegate as? AudioPlayerFinishObserver {
            audioPlayer.stop()
            finishObserver.finishOnce()
        } else {
            audioPlayer?.stop()
        }
        audioPlayer = nil
    }
}

/// Tells `playToCompletion` (and RecordedVoice) when an AVAudioPlayer has finished, exactly once
/// (natural end, decode error, or a stop).
final class AudioPlayerFinishObserver: NSObject, AVAudioPlayerDelegate {
    var onFinish: (() -> Void)?

    func finishOnce() {
        let finish = onFinish
        onFinish = nil
        finish?()
    }

    func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        DispatchQueue.main.async { self.finishOnce() }
    }

    func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) {
        DispatchQueue.main.async { self.finishOnce() }
    }
}
