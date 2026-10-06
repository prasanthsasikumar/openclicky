//
//  SarvamTranscriptionProviders.swift
//  OpenClicky
//
//  Sarvam as a transcription engine behind the shared provider protocol. Streaming first: the
//  socket hears 16 kHz audio as it is captured and answers with transcript pieces, so words show
//  while the key is held and the final text is a flush away. Upload second: a whole take as one
//  WAV after release, the shape Saathi has verified against a real key, used when the socket
//  cannot be opened.
//

import AVFoundation
import Foundation
import os

final class SarvamTranscriptionProvider: BuddyTranscriptionProvider {
    let displayName = "Sarvam"
    let requiresSpeechRecognitionPermission = false

    /// The key from shell.json; the streaming socket needs the language too.
    private let key: () -> String?
    private let language: () -> DictationLanguage
    private let preferStreaming: Bool

    init(key: @escaping () -> String? = { OpenClickyConfiguration.settings.sarvamKey },
         language: @escaping () -> DictationLanguage,
         preferStreaming: Bool = true) {
        self.key = key
        self.language = language
        self.preferStreaming = preferStreaming
    }

    var isConfigured: Bool { !(key()?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "").isEmpty }

    var unavailableExplanation: String? {
        isConfigured ? nil : "sarvam needs your key: paste it under settings → engine."
    }

    func startStreamingSession(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> any BuddyStreamingTranscriptionSession {
        guard let key = key()?.trimmingCharacters(in: .whitespacesAndNewlines), !key.isEmpty else {
            throw SarvamSpeechError.noKey
        }
        let language = language()
        if preferStreaming {
            // The realtime socket gives words while the key is held; the chunked socket gives them
            // at the end; the upload is the last resort. Each is tried in turn.
            for model in [SarvamSpeechClient.RealtimeModel.v4, .v3] {
                do {
                    return try await SarvamStreamingSession.open(
                        key: key, language: language, keyterms: keyterms, flavour: .realtime, realtimeModel: model,
                        onTranscriptUpdate: onTranscriptUpdate, onFinalTranscriptReady: onFinalTranscriptReady, onError: onError)
                } catch {
                    AppLog.append("sarvam realtime socket (\(model.rawValue)) could not open (\(error.localizedDescription)); trying the next")
                }
            }
            do {
                return try await SarvamStreamingSession.open(
                    key: key, language: language, keyterms: keyterms, flavour: .chunked,
                    onTranscriptUpdate: onTranscriptUpdate, onFinalTranscriptReady: onFinalTranscriptReady, onError: onError)
            } catch {
                AppLog.append("sarvam streaming could not open (\(error.localizedDescription)); uploading the take instead")
            }
        }
        return SarvamUploadSession(
            client: SarvamSpeechClient(key: key), language: language, keyterms: keyterms,
            onTranscriptUpdate: onTranscriptUpdate, onFinalTranscriptReady: onFinalTranscriptReady, onError: onError)
    }
}

/// The socket session. Audio arrives on the render thread; the converter and the pending-chunk
/// buffer sit behind a lock, and every send happens on one serial queue so frames keep their order.
final class SarvamStreamingSession: NSObject, BuddyStreamingTranscriptionSession, URLSessionWebSocketDelegate, @unchecked Sendable {
    let finalTranscriptFallbackDelaySeconds: TimeInterval = 6

    /// Which of Sarvam's sockets this is: the realtime one (partials, manual endpointing, raw PCM)
    /// or the chunked one (a transcript per chunk, WAV).
    enum Flavour { case realtime, chunked }
    private let flavour: Flavour

    /// Frames are batched into chunks this long before they go out (a frame per message is too chatty).
    static let chunkDurationSeconds = 0.25
    private static var chunkByteCount: Int { Int(Double(SarvamSpeechClient.sampleRate) * 2 * chunkDurationSeconds) }

    private struct State {
        var pending = Data()
        var pieces: [String] = []
        /// Realtime: the words of the utterance in progress, replaced by each partial.
        var partial = ""
        var hasRequestedFinal = false
        var hasDeliveredFinal = false
        var isClosed = false
        var sentBytes = 0
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let converter = BuddyPCM16AudioConverter(targetSampleRate: Double(SarvamSpeechClient.sampleRate))
    private let sendQueue = DispatchQueue(label: "org.openclicky.sarvam.stream")
    private let onTranscriptUpdate: (String) -> Void
    private let onFinalTranscriptReady: (String) -> Void
    private let onError: (Error) -> Void
    private var urlSession: URLSession!
    private var socket: URLSessionWebSocketTask!
    /// The continuation `open` waits on, resumed once, from whichever delegate call comes first.
    private let opened = OSAllocatedUnfairLock<CheckedContinuation<Void, Error>?>(initialState: nil)
    private let finalDeliveryWork = OSAllocatedUnfairLock<DispatchWorkItem?>(initialState: nil)

    private init(flavour: Flavour, onTranscriptUpdate: @escaping (String) -> Void, onFinalTranscriptReady: @escaping (String) -> Void, onError: @escaping (Error) -> Void) {
        self.flavour = flavour
        self.onTranscriptUpdate = onTranscriptUpdate
        self.onFinalTranscriptReady = onFinalTranscriptReady
        self.onError = onError
        super.init()
    }

    static func open(
        key: String, language: DictationLanguage, keyterms: [String], flavour: Flavour = .chunked,
        realtimeModel: SarvamSpeechClient.RealtimeModel = .v4,
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> SarvamStreamingSession {
        let session = SarvamStreamingSession(flavour: flavour, onTranscriptUpdate: onTranscriptUpdate, onFinalTranscriptReady: onFinalTranscriptReady, onError: onError)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 15
        session.urlSession = URLSession(configuration: configuration, delegate: session, delegateQueue: nil)
        let request = flavour == .realtime
            ? SarvamSpeechClient.realtimeRequest(language: language, keyterms: keyterms, key: key, model: realtimeModel)
            : SarvamSpeechClient.streamingRequest(language: language, keyterms: keyterms, key: key)
        session.socket = session.urlSession.webSocketTask(with: request)
        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                        session.opened.withLock { $0 = continuation }
                        session.socket.resume()
                    }
                }
                group.addTask {
                    try await Task.sleep(nanoseconds: 6_000_000_000)
                    throw SarvamSpeechError.unreachable("the streaming socket did not open in 6 s")
                }
                try await group.next()
                group.cancelAll()
            }
        } catch {
            // The socket may still open later; it is torn down so a late delegate call goes nowhere.
            session.cancel()
            throw error
        }
        session.receiveLoop()
        if flavour == .realtime { session.send(SarvamSpeechClient.realtimeEventMessage("speech_start")) }
        return session
    }

    // MARK: URLSessionWebSocketDelegate

    private func takeOpenContinuation() -> CheckedContinuation<Void, Error>? {
        opened.withLock { box in defer { box = nil }; return box }
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        takeOpenContinuation()?.resume()
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        let wasClosed = state.withLock { box -> Bool in defer { box.isClosed = true }; return box.isClosed }
        let reasonText = reason.map { String(decoding: $0, as: UTF8.self) } ?? ""
        if let continuation = takeOpenContinuation() {
            continuation.resume(throwing: SarvamSpeechError.refused(status: closeCode.rawValue, message: reasonText))
            return
        }
        guard !wasClosed else { return }
        AppLog.append("sarvam stream closed (\(closeCode.rawValue)) \(reasonText)")
        deliverFinalIfRequested()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error else { return }
        let wasClosed = state.withLock { box -> Bool in defer { box.isClosed = true }; return box.isClosed }
        if let continuation = takeOpenContinuation() {
            continuation.resume(throwing: SarvamSpeechError.unreachable(error.localizedDescription))
            return
        }
        // After cancel() every callback is the teardown's own; nothing is reported.
        guard !wasClosed else { return }
        let hasText = state.withLock { !$0.pieces.isEmpty }
        if hasText { deliverFinalIfRequested() } else { onError(SarvamSpeechError.unreachable(error.localizedDescription)) }
    }

    private func receiveLoop() {
        socket.receive { [weak self] result in
            guard let self else { return }
            switch result {
            case .success(let message):
                if case .string(let text) = message { self.handle(serverText: text) }
                if case .data(let data) = message { self.handle(serverText: String(decoding: data, as: UTF8.self)) }
                let closed = self.state.withLock { $0.isClosed }
                if !closed { self.receiveLoop() }
            case .failure(let error):
                let (hadText, closed) = self.state.withLock { box -> (Bool, Bool) in
                    defer { box.isClosed = true }
                    return (!box.pieces.isEmpty, box.isClosed)
                }
                if !closed {
                    if hadText { self.deliverFinalIfRequested() } else { self.onError(SarvamSpeechError.unreachable(error.localizedDescription)) }
                }
            }
        }
    }

    private func handle(serverText: String) {
        if flavour == .realtime {
            handleRealtime(serverText)
            return
        }
        switch SarvamSpeechClient.decodeStreamingMessage(serverText) {
        case .transcript(let piece):
            let trimmed = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return }
            let text = state.withLock { box -> String in
                box.pieces.append(trimmed)
                return box.pieces.joined(separator: " ")
            }
            onTranscriptUpdate(text)
            // Every piece after the flush could be the last; wait briefly for a straggler.
            let requested = state.withLock { $0.hasRequestedFinal }
            if requested { scheduleFinalDelivery(after: 0.7) }
        case .error(let message):
            AppLog.append("sarvam stream error: \(message)")
            onError(SarvamSpeechError.refused(status: 0, message: message))
        case .other:
            break
        }
    }

    private func handleRealtime(_ serverText: String) {
        switch SarvamSpeechClient.decodeRealtimeMessage(serverText) {
        case .partial(let words):
            let text = state.withLock { box -> String in
                box.partial = words
                return (box.pieces + [words]).joined(separator: " ")
            }
            onTranscriptUpdate(text.trimmingCharacters(in: .whitespaces))
        case .final(let words):
            let trimmed = words.trimmingCharacters(in: .whitespacesAndNewlines)
            let (text, requested) = state.withLock { box -> (String, Bool) in
                if !trimmed.isEmpty { box.pieces.append(trimmed) }
                box.partial = ""
                return (box.pieces.joined(separator: " "), box.hasRequestedFinal)
            }
            onTranscriptUpdate(text)
            // The final after the flush is the last word; the session is ended right after.
            if requested { scheduleFinalDelivery(after: 0.3) }
        case .sessionEnd:
            state.withLock { $0.isClosed = true }
            deliverFinalIfRequested()
        case .error(let message, let fatal):
            AppLog.append("sarvam realtime error: \(message) (fatal: \(fatal))")
            if fatal { onError(SarvamSpeechError.refused(status: 0, message: message)) }
        case .sessionBegin, .other:
            break
        }
    }

    // MARK: BuddyStreamingTranscriptionSession

    func appendAudioBuffer(_ audioBuffer: AVAudioPCMBuffer) {
        guard let pcm = converter.convertToPCM16Data(from: audioBuffer) else { return }
        let chunk: Data? = state.withLock { box in
            guard !box.hasRequestedFinal, !box.isClosed else { return nil }
            box.pending.append(pcm)
            guard box.pending.count >= Self.chunkByteCount else { return nil }
            let out = box.pending
            box.pending = Data()
            box.sentBytes += out.count
            return out
        }
        if let chunk { send(audioMessage(pcm16: chunk)) }
    }

    private func audioMessage(pcm16: Data) -> String {
        flavour == .realtime ? SarvamSpeechClient.realtimeAudioMessage(pcm16: pcm16) : SarvamSpeechClient.streamingAudioMessage(pcm16: pcm16)
    }

    func requestFinalTranscript() {
        let remainder: Data? = state.withLock { box in
            guard !box.hasRequestedFinal else { return nil }
            box.hasRequestedFinal = true
            let out = box.pending
            box.pending = Data()
            return out
        }
        guard let remainder else { return }
        if !remainder.isEmpty { send(audioMessage(pcm16: remainder)) }
        if flavour == .realtime {
            send(SarvamSpeechClient.realtimeEventMessage("speech_end"))
            send(SarvamSpeechClient.realtimeEventMessage("flush"))
        } else {
            send(SarvamSpeechClient.streamingFlushMessage)
        }
        // The flushed pieces normally arrive within a second; the manager's own fallback covers more.
        scheduleFinalDelivery(after: 2.5)
    }

    func cancel() {
        state.withLock { $0.isClosed = true; $0.hasDeliveredFinal = true }
        finalDeliveryWork.withLock { $0?.cancel(); $0 = nil }
        takeOpenContinuation()?.resume(throwing: SarvamSpeechError.unreachable("cancelled"))
        socket.cancel(with: .normalClosure, reason: nil)
        urlSession.invalidateAndCancel()
    }

    private func send(_ text: String) {
        sendQueue.async { [socket] in
            socket?.send(.string(text)) { error in
                if let error { AppLog.append("sarvam stream send failed: \(error.localizedDescription)") }
            }
        }
    }

    private func scheduleFinalDelivery(after seconds: TimeInterval) {
        let work = DispatchWorkItem { [weak self] in self?.deliverFinalIfRequested() }
        finalDeliveryWork.withLock { $0?.cancel(); $0 = work }
        sendQueue.asyncAfter(deadline: .now() + seconds, execute: work)
    }

    private func deliverFinalIfRequested() {
        let text: String? = state.withLock { box in
            guard box.hasRequestedFinal, !box.hasDeliveredFinal else { return nil }
            box.hasDeliveredFinal = true
            // A partial that never got its final still counts: those words were heard.
            return (box.pieces + (box.partial.isEmpty ? [] : [box.partial])).joined(separator: " ")
        }
        guard let text else { return }
        if flavour == .realtime { send(SarvamSpeechClient.realtimeEventMessage("end")) }
        socket.cancel(with: .normalClosure, reason: nil)
        urlSession.finishTasksAndInvalidate()
        onFinalTranscriptReady(text.trimmingCharacters(in: .whitespacesAndNewlines))
    }
}

/// The whole take, uploaded once after release; nothing shows while the key is held.
final class SarvamUploadSession: BuddyStreamingTranscriptionSession, @unchecked Sendable {
    let finalTranscriptFallbackDelaySeconds: TimeInterval = 20

    /// Saaras takes about thirty seconds a request; longer takes go up in pieces, in order.
    static let pieceSeconds = 28.0
    private static let silencePeak: Int16 = 400

    private struct State {
        var pcm = Data()
        var peak: Int16 = 0
        var hasRequestedFinal = false
        var isCancelled = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let converter = BuddyPCM16AudioConverter(targetSampleRate: Double(SarvamSpeechClient.sampleRate))
    private let client: SarvamSpeechClient
    private let language: DictationLanguage
    private let keyterms: [String]
    private let onTranscriptUpdate: (String) -> Void
    private let onFinalTranscriptReady: (String) -> Void
    private let onError: (Error) -> Void
    private var uploadTask: Task<Void, Never>?

    init(client: SarvamSpeechClient, language: DictationLanguage, keyterms: [String],
         onTranscriptUpdate: @escaping (String) -> Void, onFinalTranscriptReady: @escaping (String) -> Void, onError: @escaping (Error) -> Void) {
        self.client = client
        self.language = language
        self.keyterms = keyterms
        self.onTranscriptUpdate = onTranscriptUpdate
        self.onFinalTranscriptReady = onFinalTranscriptReady
        self.onError = onError
    }

    func appendAudioBuffer(_ audioBuffer: AVAudioPCMBuffer) {
        guard let pcm = converter.convertToPCM16Data(from: audioBuffer) else { return }
        let peak = Self.peak(of: pcm)
        state.withLock { box in
            guard !box.hasRequestedFinal else { return }
            box.pcm.append(pcm)
            box.peak = max(box.peak, peak)
        }
    }

    func requestFinalTranscript() {
        let captured: (pcm: Data, peak: Int16)? = state.withLock { box in
            guard !box.hasRequestedFinal else { return nil }
            box.hasRequestedFinal = true
            return (box.pcm, box.peak)
        }
        guard let captured else { return }
        let pcm = captured.pcm
        guard captured.peak >= Self.silencePeak, !pcm.isEmpty else {
            onFinalTranscriptReady("")
            return
        }
        let pieces = Self.split(pcm16: pcm, sampleRate: SarvamSpeechClient.sampleRate, pieceSeconds: Self.pieceSeconds)
        uploadTask = Task { [client, language, keyterms] in
            var texts: [String] = []
            for piece in pieces {
                if Task.isCancelled { return }
                do {
                    let wav = BuddyWAVFileBuilder.buildWAVData(fromPCM16MonoAudio: piece, sampleRate: SarvamSpeechClient.sampleRate)
                    let text = try await client.transcribe(wav: wav, language: language, keyterms: keyterms)
                    if !text.isEmpty { texts.append(text) }
                    self.onTranscriptUpdate(texts.joined(separator: " "))
                } catch {
                    guard !self.state.withLock({ $0.isCancelled }) else { return }
                    self.onError(error)
                    return
                }
            }
            guard !self.state.withLock({ $0.isCancelled }) else { return }
            self.onFinalTranscriptReady(texts.joined(separator: " "))
        }
    }

    func cancel() {
        state.withLock { $0.isCancelled = true }
        uploadTask?.cancel()
    }

    /// Whole pieces of `pieceSeconds`, the last one shorter. Pure, for the test.
    static func split(pcm16: Data, sampleRate: Int, pieceSeconds: Double) -> [Data] {
        let pieceBytes = max(2, Int(Double(sampleRate) * 2 * pieceSeconds))
        var pieces: [Data] = []
        var offset = 0
        while offset < pcm16.count {
            let end = min(offset + pieceBytes, pcm16.count)
            pieces.append(pcm16.subdata(in: offset..<end))
            offset = end
        }
        return pieces
    }

    static func peak(of pcm16: Data) -> Int16 {
        var peak: Int16 = 0
        pcm16.withUnsafeBytes { raw in
            for sample in raw.bindMemory(to: Int16.self) {
                let magnitude = sample == Int16.min ? Int16.max : abs(sample)
                if magnitude > peak { peak = magnitude }
            }
        }
        return peak
    }
}
