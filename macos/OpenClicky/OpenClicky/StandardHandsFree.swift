//
//  StandardHandsFree.swift
//  OpenClicky
//
//  Hands-free without a realtime key: the microphone stays open, a spoken turn is cut out of it
//  on this Mac (loudness against the room's own noise, half a second kept from before the speech
//  so the first word is whole), transcribed by the ask lane's engine, and asked exactly as if
//  ⌃⌥ had been held for it. Slower than fast voice (the answer comes after the person stops, not
//  while they finish), but it works on any key, a plan, or a free account.
//
//  One side at a time: while Clicky thinks or speaks the microphone is not listened to, so it
//  never answers its own voice. Esc and the shortcut still stop it.
//

import AVFoundation
import Foundation
import os

/// Finds spoken turns in a stream of 20 ms frames (16 kHz mono). Pure, so it is unit-tested
/// with synthetic audio (StandardHandsFreeTests).
nonisolated struct UtteranceDetector {
    enum Event: Equatable {
        case started
        /// The turn's samples, from half a second before it started to its last frame of speech.
        case finished([Float])
        /// Too short to be speech (a cough, a click, a door).
        case discarded
    }

    static let sampleRate = 16_000
    static let frameLength = 320
    /// Frames of 20 ms.
    static let prerollFrames = 25
    static let framesToStart = 8
    static let startWindow = 10
    static let silenceFramesToEnd = 40
    static let minimumSpeechFrames = 15
    static let maximumFrames = 30 * 50
    /// Below this nothing counts as speech, however quiet the room.
    static let minimumLevel: Float = 0.006
    /// Speech is this many times louder than the room's noise.
    static let levelOverNoise: Float = 3
    /// The room's noise is read from the quieter end of the last three seconds (minimum
    /// statistics): speech has gaps between words, steady sound (a fan, music) does not, so this
    /// follows the room up and down without taking a voice for noise.
    static let noiseWindowFrames = 150
    /// No turn starts before a second of the room has been heard (a voice at once would otherwise
    /// set the bar, and in a loud room a turn would never end).
    static let warmUpFrames = 50

    private(set) var noiseFloor: Float = minimumLevel / levelOverNoise
    private var recentLevels: [Float] = []
    private(set) var isInTurn = false
    private var recentFrames: [[Float]] = []
    private var recentWasSpeech: [Bool] = []
    private var turn: [Float] = []
    private var turnFrames = 0
    private var speechFrames = 0
    private var silentRun = 0
    private var samplesUpToLastSpeech = 0

    static func level(_ frame: [Float]) -> Float {
        guard !frame.isEmpty else { return 0 }
        var sum: Float = 0
        for sample in frame { sum += sample * sample }
        return (sum / Float(frame.count)).squareRoot()
    }

    func isSpeech(level: Float) -> Bool {
        level > max(Self.minimumLevel, noiseFloor * Self.levelOverNoise)
    }

    /// The tenth-quietest percent of the window, so one dropout does not pull the floor down.
    private mutating func noteLevel(_ level: Float) {
        recentLevels.append(level)
        if recentLevels.count > Self.noiseWindowFrames { recentLevels.removeFirst() }
        let sorted = recentLevels.sorted()
        noiseFloor = sorted[min(sorted.count - 1, sorted.count / 10)]
    }

    /// Starts over, room included (hands-free paused, or its microphone reopened).
    mutating func reset() {
        recentLevels.removeAll()
        noiseFloor = Self.minimumLevel / Self.levelOverNoise
        endTurn()
    }

    private mutating func endTurn() {
        isInTurn = false
        recentFrames.removeAll()
        recentWasSpeech.removeAll()
        turn.removeAll()
        turnFrames = 0
        speechFrames = 0
        silentRun = 0
        samplesUpToLastSpeech = 0
    }

    mutating func process(frame: [Float]) -> Event? {
        let level = Self.level(frame)
        noteLevel(level)
        let speech = isSpeech(level: level)
        guard isInTurn else {
            recentFrames.append(frame)
            recentWasSpeech.append(speech)
            if recentFrames.count > Self.prerollFrames { recentFrames.removeFirst() }
            if recentWasSpeech.count > Self.startWindow { recentWasSpeech.removeFirst() }
            guard recentLevels.count >= Self.warmUpFrames, recentWasSpeech.filter({ $0 }).count >= Self.framesToStart else { return nil }
            isInTurn = true
            turn = recentFrames.flatMap { $0 }
            turnFrames = recentFrames.count
            speechFrames = recentWasSpeech.filter { $0 }.count
            silentRun = 0
            samplesUpToLastSpeech = turn.count
            recentFrames.removeAll()
            recentWasSpeech.removeAll()
            return .started
        }
        turn += frame
        turnFrames += 1
        if speech {
            speechFrames += 1
            silentRun = 0
            samplesUpToLastSpeech = turn.count
        } else {
            silentRun += 1
        }
        guard silentRun >= Self.silenceFramesToEnd || turnFrames >= Self.maximumFrames else { return nil }
        let enoughSpeech = speechFrames >= Self.minimumSpeechFrames
        // A little of the quiet after the last word stays, so its ending is not clipped.
        let tail = min(turn.count, samplesUpToLastSpeech + Self.frameLength * 10)
        let samples = Array(turn.prefix(tail))
        endTurn()
        return enoughSpeech ? .finished(samples) : .discarded
    }

    /// 16 kHz mono float samples as a WAV file (16-bit PCM), for the transcription engines.
    static func wavData(_ samples: [Float]) -> Data {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        let byteCount = UInt32(samples.count * 2)
        data.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36) + byteCount)
        data.append(contentsOf: Array("WAVE".utf8)); data.append(contentsOf: Array("fmt ".utf8))
        append(UInt32(16)); append(UInt16(1)); append(UInt16(1)); append(UInt32(sampleRate)); append(UInt32(sampleRate * 2))
        append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(byteCount)
        for sample in samples { append(Int16(max(-1, min(1, sample)) * Float(Int16.max))) }
        return data
    }
}

/// "That's all, thanks" ends hands-free on the standard lane. Fast voice lets its model decide
/// (stop_listening); here a model round trip would cost seconds, so the phrases are fixed and
/// narrow: a whole turn of sign-off, never "stop the music" or "how do I quit Photoshop".
nonisolated enum HandsFreeSignOff {
    private static let phrases: Set<String> = [
        "that's all", "thats all", "that is all", "that's all i need", "that's all thanks", "that's all thank you",
        "that's it", "that's it for now", "that's it thanks", "stop listening", "you can stop listening",
        "ok bye", "okay bye", "bye", "goodbye", "bye clicky", "goodbye clicky", "i'm done", "im done", "i'm done thanks",
        "we're done", "that'll be all", "that will be all", "thanks that's all", "thank you that's all",
    ]

    static func isSignOff(_ transcript: String) -> Bool {
        let words = transcript.lowercased()
            .replacingOccurrences(of: "’", with: "'")
            .components(separatedBy: CharacterSet.letters.union(CharacterSet(charactersIn: "'")).inverted)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        return phrases.contains(words)
    }
}

/// The microphone for standard hands-free, on its own engine and queue (CoreAudio's first set-up
/// hops to the main queue, so it never runs on the main actor). No voice processing: it would
/// duck other apps' audio and make another engine read silence (see RealtimeAudioEngine).
nonisolated final class StandardHandsFreeListener: @unchecked Sendable {
    private let queue = DispatchQueue(label: "org.openclicky.handsfree.audio")
    private var engine = AVAudioEngine()
    private var detector = UtteranceDetector()
    private var pending: [Float] = []
    private var isRunning = false
    /// Read on the render thread: whether frames are listened to (off while Clicky answers).
    private let accepting = OSAllocatedUnfairLock(initialState: false)
    private var onEvent: (@Sendable (UtteranceDetector.Event) -> Void)?

    /// `preferredMicrophoneUID` is Settings → microphone, the one the ⌃⌥ lane hears with.
    func start(preferredMicrophoneUID: String?, onEvent: @escaping @Sendable (UtteranceDetector.Event) -> Void) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async {
                do {
                    self.onEvent = onEvent
                    try self.startOnQueue(preferredMicrophoneUID: preferredMicrophoneUID)
                    continuation.resume()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    /// Listen to the microphone or ignore it; a turn half heard when ignoring starts is dropped.
    func setAccepting(_ isAccepting: Bool) {
        let changed = accepting.withLock { state -> Bool in
            defer { state = isAccepting }
            return state != isAccepting
        }
        guard changed else { return }
        queue.async {
            self.detector.reset()
            self.pending.removeAll()
        }
    }

    func stop() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async {
                self.stopOnQueue()
                continuation.resume()
            }
        }
    }

    private func startOnQueue(preferredMicrophoneUID: String?) throws {
        guard !isRunning else { return }
        MicrophoneDevices.apply(preferredUID: preferredMicrophoneUID, to: engine)
        let inputNode = engine.inputNode
        let hardwareFormat = inputNode.outputFormat(forBus: 0)
        guard hardwareFormat.sampleRate > 0, hardwareFormat.channelCount > 0,
              let monoFormat = AVAudioFormat(standardFormatWithSampleRate: hardwareFormat.sampleRate, channels: 1),
              let targetFormat = AVAudioFormat(standardFormatWithSampleRate: Double(UtteranceDetector.sampleRate), channels: 1),
              let converter = AVAudioConverter(from: monoFormat, to: targetFormat) else {
            throw RealtimeVoiceError.audio("no microphone input available")
        }
        inputNode.installTap(onBus: 0, bufferSize: 4096, format: hardwareFormat) { [weak self] buffer, _ in
            guard let self, self.accepting.withLock({ $0 }) else { return }
            guard let samples = Self.resample(buffer, monoFormat: monoFormat, converter: converter, targetFormat: targetFormat) else { return }
            self.queue.async { self.consume(samples) }
        }
        engine.prepare()
        try engine.start()
        isRunning = true
        AppLog.append("hands-free (standard): microphone open, \(Int(hardwareFormat.sampleRate)) Hz × \(hardwareFormat.channelCount) ch")
    }

    private func stopOnQueue() {
        guard isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        engine = AVAudioEngine()
        detector.reset()
        pending.removeAll()
        isRunning = false
        onEvent = nil
    }

    private func consume(_ samples: [Float]) {
        pending += samples
        while pending.count >= UtteranceDetector.frameLength {
            let frame = Array(pending.prefix(UtteranceDetector.frameLength))
            pending.removeFirst(UtteranceDetector.frameLength)
            if let event = detector.process(frame: frame) { onEvent?(event) }
        }
    }

    /// A heard turn through the ask lane's own engine (Apple's on a free account, else the one set
    /// up), as the ⌃⌥ lane would hear it; "" when nothing was heard or the engine failed (logged).
    @MainActor static func transcribe(_ samples: [Float]) async -> String {
        let fileURL = FileManager.default.temporaryDirectory.appendingPathComponent("openclicky-handsfree-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: fileURL) }
        do {
            try UtteranceDetector.wavData(samples).write(to: fileURL)
            let provider: any BuddyTranscriptionProvider = AccountCapabilities.current().hearsOnDevice
                ? AppleSpeechTranscriptionProvider()
                : BuddyTranscriptionProviderFactory.makeDefaultProvider()
            let text = try await TakeAudioStore.transcribe(fileURL: fileURL, provider: provider, keyterms: [])
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            AppLog.append("hands-free (standard): transcription failed: \(error.localizedDescription)")
            return ""
        }
    }

    /// A recording's first channel at 16 kHz (the smoke run feeds files through the detector).
    static func samples16k(fileURL: URL) -> [Float]? {
        guard let file = try? AVAudioFile(forReading: fileURL),
              let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
              (try? file.read(into: buffer)) != nil,
              let monoFormat = AVAudioFormat(standardFormatWithSampleRate: file.processingFormat.sampleRate, channels: 1),
              let targetFormat = AVAudioFormat(standardFormatWithSampleRate: Double(UtteranceDetector.sampleRate), channels: 1),
              let converter = AVAudioConverter(from: monoFormat, to: targetFormat) else { return nil }
        return resample(buffer, monoFormat: monoFormat, converter: converter, targetFormat: targetFormat)
    }

    /// Channel 0 of the hardware buffer at 16 kHz. (A multi-channel → mono AVAudioConverter
    /// returns silence, so the channel is copied into a mono buffer first.)
    private static func resample(_ buffer: AVAudioPCMBuffer, monoFormat: AVAudioFormat, converter: AVAudioConverter, targetFormat: AVAudioFormat) -> [Float]? {
        guard let channel = buffer.floatChannelData?[0], buffer.frameLength > 0,
              let mono = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: buffer.frameLength),
              let monoChannel = mono.floatChannelData?[0] else { return nil }
        monoChannel.update(from: channel, count: Int(buffer.frameLength))
        mono.frameLength = buffer.frameLength
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * targetFormat.sampleRate / monoFormat.sampleRate) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return nil }
        var handedOver = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if handedOver {
                status.pointee = .noDataNow
                return nil
            }
            handedOver = true
            status.pointee = .haveData
            return mono
        }
        guard error == nil, let outputChannel = output.floatChannelData?[0] else { return nil }
        return Array(UnsafeBufferPointer(start: outputChannel, count: Int(output.frameLength)))
    }
}
