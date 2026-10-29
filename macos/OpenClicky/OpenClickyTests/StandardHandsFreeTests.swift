import Foundation
import Testing
@testable import OpenClicky

/// Standard hands-free: cutting spoken turns out of the microphone, and the fixed sign-offs.
struct StandardHandsFreeTests {
    /// 20 ms of a tone (speech-loud) or of faint noise.
    private func frame(loud: Bool, phase: Int = 0) -> [Float] {
        (0..<UtteranceDetector.frameLength).map { index in
            let t = Float(phase * UtteranceDetector.frameLength + index) / Float(UtteranceDetector.sampleRate)
            return loud ? 0.2 * sinf(2 * .pi * 220 * t) : 0.001 * sinf(2 * .pi * 50 * t)
        }
    }

    private func run(_ detector: inout UtteranceDetector, quiet: Int, loud: Int, thenQuiet: Int) -> [UtteranceDetector.Event] {
        var events: [UtteranceDetector.Event] = []
        var phase = 0
        for _ in 0..<quiet { if let event = detector.process(frame: frame(loud: false, phase: phase)) { events.append(event) }; phase += 1 }
        for _ in 0..<loud { if let event = detector.process(frame: frame(loud: true, phase: phase)) { events.append(event) }; phase += 1 }
        for _ in 0..<thenQuiet { if let event = detector.process(frame: frame(loud: false, phase: phase)) { events.append(event) }; phase += 1 }
        return events
    }

    @Test func aSpokenTurnKeepsItsStartAndEndsAfterAPause() throws {
        var detector = UtteranceDetector()
        // A second of room, a second of speech, then a second of quiet.
        let events = run(&detector, quiet: 50, loud: 50, thenQuiet: 50)
        #expect(events.first == .started)
        guard case .finished(let samples) = events.last else {
            Issue.record("no finished turn: \(events)")
            return
        }
        // The half second before the voice was detected is kept, so the first word is whole, and
        // a little quiet after the last word.
        let seconds = Double(samples.count) / Double(UtteranceDetector.sampleRate)
        #expect(seconds > 1.2)
        #expect(seconds < 1.9)
    }

    @Test func aShortSoundIsNotATurn() {
        var detector = UtteranceDetector()
        let events = run(&detector, quiet: 50, loud: 10, thenQuiet: 50)
        #expect(events == [.started, .discarded])
    }

    @Test func aPauseShorterThanTheEndingKeepsTheTurnGoing() {
        var detector = UtteranceDetector()
        var events = run(&detector, quiet: 50, loud: 30, thenQuiet: 20)
        events += run(&detector, quiet: 0, loud: 30, thenQuiet: 50)
        #expect(events.filter { $0 == .started }.count == 1)
        #expect(events.count == 2)
    }

    @Test func roomNoiseAloneNeverStartsATurn() {
        var detector = UtteranceDetector()
        #expect(run(&detector, quiet: 500, loud: 0, thenQuiet: 0).isEmpty)
    }

    @Test func writesA16kHzWav() {
        let data = UtteranceDetector.wavData([0, 0.5, -0.5])
        #expect(data.count == 44 + 6)
        #expect(String(decoding: data.prefix(4), as: UTF8.self) == "RIFF")
    }

    @Test func signOffsAreWholeTurnsOnly() {
        #expect(HandsFreeSignOff.isSignOff("That's all, thanks."))
        #expect(HandsFreeSignOff.isSignOff("OK bye"))
        #expect(HandsFreeSignOff.isSignOff("Stop listening!"))
        #expect(HandsFreeSignOff.isSignOff("that’s it for now"))
        #expect(!HandsFreeSignOff.isSignOff("thank you"))
        #expect(!HandsFreeSignOff.isSignOff("stop the music"))
        #expect(!HandsFreeSignOff.isSignOff("how do I quit Photoshop"))
        #expect(!HandsFreeSignOff.isSignOff("that's all I needed to know about refraction, now explain lenses"))
    }

    @Test func listensOnlyWhenIdleAndQuietLongEnough() {
        #expect(CompanionManager.standardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: 1))
        #expect(!CompanionManager.standardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: 0.2))
        #expect(!CompanionManager.standardHandsFreeAccepts(isOn: true, isBusy: true, quietFor: 5))
        #expect(!CompanionManager.standardHandsFreeAccepts(isOn: false, isBusy: false, quietFor: 5))
        #expect(!CompanionManager.standardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: nil))
    }
}
