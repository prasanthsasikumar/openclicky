//
//  DictationEarcons.swift
//  OpenClicky
//
//  The short tones and trackpad taps around a take: start, stop, done, blocked, error. The tones
//  are synthesised here (a sine with a soft envelope, two notes for the chords) so nothing has to
//  be bundled or licensed; they are quiet and short, meant to be felt more than heard.
//

import AppKit
import AVFoundation
import Foundation

enum DictationEarcon: CaseIterable {
    case start, stop, complete, blocked, error, notify

    /// Notes in Hz with their durations; played one after the other.
    fileprivate var notes: [(frequency: Double, seconds: Double)] {
        switch self {
        case .start: return [(660, 0.06), (880, 0.09)]
        case .stop: return [(880, 0.06), (660, 0.09)]
        case .complete: return [(784, 0.07), (1047, 0.11)]
        case .blocked: return [(330, 0.12)]
        case .error: return [(392, 0.1), (294, 0.14)]
        case .notify: return [(988, 0.09)]
        }
    }
}

@MainActor
final class DictationEarconPlayer {
    private var players: [DictationEarcon: AVAudioPlayer] = [:]
    private let sampleRate = 44_100.0
    private weak var settings: DictationSettings?

    init(settings: DictationSettings) {
        self.settings = settings
    }

    func play(_ earcon: DictationEarcon) {
        guard settings?.sounds ?? true else { return }
        let player: AVAudioPlayer
        if let existing = players[earcon] {
            player = existing
        } else {
            guard let made = try? AVAudioPlayer(data: Self.wav(for: earcon, sampleRate: sampleRate)) else { return }
            made.volume = 0.35
            made.prepareToPlay()
            players[earcon] = made
            player = made
        }
        player.currentTime = 0
        player.play()
    }

    func tap() {
        guard settings?.haptics ?? true else { return }
        NSHapticFeedbackManager.defaultPerformer.perform(.generic, performanceTime: .now)
    }

    /// A WAV with the earcon's notes, each with a 8 ms attack and a release to silence.
    static func wav(for earcon: DictationEarcon, sampleRate: Double) -> Data {
        var samples = Data()
        for note in earcon.notes {
            let count = Int(note.seconds * sampleRate)
            let attack = Int(0.008 * sampleRate)
            for index in 0..<count {
                let envelope: Double
                if index < attack { envelope = Double(index) / Double(attack) } else { envelope = 1 - Double(index - attack) / Double(max(1, count - attack)) }
                let value = sin(2 * .pi * note.frequency * Double(index) / sampleRate) * envelope * 0.8
                var sample = Int16(max(-1, min(1, value)) * Double(Int16.max)).littleEndian
                withUnsafeBytes(of: &sample) { samples.append(contentsOf: $0) }
            }
        }
        return BuddyWAVFileBuilder.buildWAVData(fromPCM16MonoAudio: samples, sampleRate: Int(sampleRate))
    }
}
