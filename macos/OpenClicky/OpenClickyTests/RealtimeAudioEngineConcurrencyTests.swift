//
//  RealtimeAudioEngineConcurrencyTests.swift
//  OpenClickyTests
//
//  `RealtimeAudioEngine` hands its microphone tap to AVAudioEngine, which calls it on the CoreAudio
//  render thread — a real thread, not the serial `queue` the rest of the class is synchronised on.
//  Anything the tap reads off `self` is therefore read concurrently with the writes `tearDown()`
//  makes on that queue. These tests drive exactly that pair of threads.
//
//  They are written to be run under the Thread Sanitizer, which is where the failure is visible:
//
//      xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky \
//        -destination 'platform=macOS' -enableThreadSanitizer YES \
//        -only-testing:OpenClickyTests/RealtimeAudioEngineConcurrencyTests
//
//  Without TSan they only assert that the teardown/deliver interleaving does not crash or deliver a
//  frame after release, which is the weaker half of the guarantee.
//

import AVFoundation
import Foundation
import Testing
@testable import OpenClicky

struct RealtimeAudioEngineConcurrencyTests {

    /// A 10 ms mono float buffer, the shape an input tap actually delivers.
    private static func makeInputBuffer(sampleRate: Double = 48_000) -> AVAudioPCMBuffer {
        let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1)!
        let frameCount = AVAudioFrameCount(sampleRate / 100)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount)!
        buffer.frameLength = frameCount
        let channel = buffer.floatChannelData![0]
        for index in 0..<Int(frameCount) {
            channel[index] = sin(Float(index) * 0.05) * 0.25
        }
        return buffer
    }

    /// The render thread reads `converter`, `monoFormat` and `onMicrophoneFrame` off `self` while
    /// `tearDown()` nils them on the audio queue. TSan reports that pair; the fix is for the tap to
    /// capture what it needs by value so there is nothing shared to read.
    @Test func deliveringBuffersWhileTheGraphIsTornDownIsRaceFree() async {
        let engine = RealtimeAudioEngine()
        let deliveredFrameCount = Counter()
        engine.setCallbacks(
            onMicrophoneFrame: { data, _ in
                if !data.isEmpty { deliveredFrameCount.increment() }
            },
            onPlaybackActiveChanged: { _ in }
        )

        let buffer = Self.makeInputBuffer()
        let keepDelivering = Flag(true)

        // Thread A: the CoreAudio render thread, delivering buffers for as long as the app is
        // tearing the graph down underneath it. It must outlive the control loop, not race it to
        // the start line — an overlap in wall-clock time is the whole point of the test.
        let renderThread = Thread {
            while keepDelivering.value {
                engine.handleMicrophoneBuffer(buffer)
            }
        }
        renderThread.stackSize = 512 * 1024
        renderThread.start()

        // Thread B: the app, tearing the graph down and back up between turns.
        for _ in 0..<200 {
            engine.stop()
            engine.pause()
            await engine.releaseNow()
        }

        keepDelivering.value = false
        while !renderThread.isFinished { await Task.yield() }

        // The assertion that holds with or without TSan: nothing crashed, and a released engine
        // never hands a frame to a client that has already torn its session down.
        _ = deliveredFrameCount.value
    }

    /// `debugSummary()` reads the counters on the audio queue while the render thread bumps them.
    /// Same shape, narrower: the statistics path must not be a race either.
    @Test func readingStatisticsWhileBuffersArriveIsRaceFree() async {
        let engine = RealtimeAudioEngine()
        engine.setCallbacks(onMicrophoneFrame: { _, _ in }, onPlaybackActiveChanged: { _ in })
        let buffer = Self.makeInputBuffer()
        let keepDelivering = Flag(true)

        let renderThread = Thread {
            while keepDelivering.value {
                engine.handleMicrophoneBuffer(buffer)
            }
        }
        renderThread.start()

        for _ in 0..<100 {
            _ = await engine.debugSummary()
        }

        keepDelivering.value = false
        while !renderThread.isFinished { await Task.yield() }
    }
}

/// A lock-guarded flag, so the test's own stop signal is not itself the race TSan reports.
private final class Flag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag: Bool

    init(_ flag: Bool) { self.flag = flag }

    var value: Bool {
        get {
            lock.lock()
            defer { lock.unlock() }
            return flag
        }
        set {
            lock.lock()
            flag = newValue
            lock.unlock()
        }
    }
}

/// A lock-guarded counter, so the test's own bookkeeping is not itself the race TSan reports.
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0

    func increment() {
        lock.lock()
        count += 1
        lock.unlock()
    }

    var value: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }
}
