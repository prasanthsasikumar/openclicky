//
//  MicrophoneEchoGateTests.swift
//  OpenClickyTests
//
//  Hands-free on a microphone without echo cancellation (the lid closed, a chosen microphone):
//  the microphone is held while Clicky speaks and for a moment after, so its own voice is not
//  taken for the person and the answer is not cut off (2026-11-08).
//

import Foundation
import Testing
@testable import OpenClicky

struct MicrophoneEchoGateTests {
    @Test func echoCancellationNeverHoldsTheMicrophone() {
        #expect(!MicrophoneEchoGate.holdsMicrophone(echoCancelled: true, isSpeaking: true, playbackEndedAt: nil))
        #expect(!MicrophoneEchoGate.holdsMicrophone(echoCancelled: true, isSpeaking: false, playbackEndedAt: Date()))
    }

    @Test func withoutEchoCancellationTheMicrophoneWaitsForTheVoiceAndItsTail() {
        let now = Date()
        #expect(MicrophoneEchoGate.holdsMicrophone(echoCancelled: false, isSpeaking: true, playbackEndedAt: nil, now: now))
        #expect(MicrophoneEchoGate.holdsMicrophone(echoCancelled: false, isSpeaking: false,
                                                   playbackEndedAt: now.addingTimeInterval(-0.2), now: now))
        #expect(!MicrophoneEchoGate.holdsMicrophone(echoCancelled: false, isSpeaking: false,
                                                    playbackEndedAt: now.addingTimeInterval(-(MicrophoneEchoGate.tail + 0.1)), now: now))
        #expect(!MicrophoneEchoGate.holdsMicrophone(echoCancelled: false, isSpeaking: false, playbackEndedAt: nil, now: now))
    }
}
