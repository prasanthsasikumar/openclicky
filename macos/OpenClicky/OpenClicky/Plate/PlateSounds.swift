//
//  PlateSounds.swift
//  OpenClicky
//
//  The small sounds of the cards: one landing, a stamp, paper crumpling, the ball dropping in
//  the bin, the last card handled. Synthesised here from sines and noise, like the dictation
//  tones, so nothing is bundled. They follow the same "sounds" switch and stay quiet.
//

import AVFoundation
import Foundation

enum PlateSound: CaseIterable {
    case place, stamp, crumple, binDrop, allDone
}

enum PlateSoundSynth {
    /// Mono samples in -1…1. `seed` makes the noise the same every time (and testable).
    static func samples(for sound: PlateSound, sampleRate: Double = 44_100, seed: UInt64 = 7) -> [Double] {
        var state = seed &* 6364136223846793005 &+ 1442695040888963407
        func noise() -> Double {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Double((state >> 33) & 0xFFFF) / 32768.0 - 1
        }
        func tone(_ frequency: Double, _ index: Int) -> Double { sin(2 * .pi * frequency * Double(index) / sampleRate) }
        let seconds: Double
        switch sound {
        case .place: seconds = 0.07
        case .stamp: seconds = 0.16
        case .crumple: seconds = 0.34
        case .binDrop: seconds = 0.2
        case .allDone: seconds = 0.42
        }
        let count = Int(seconds * sampleRate)
        var output = [Double](repeating: 0, count: count)
        var smoothed = 0.0
        for index in 0..<count {
            let t = Double(index) / sampleRate
            let progress = Double(index) / Double(count)
            switch sound {
            case .place:
                // A soft card tap: low-passed noise that dies in a few milliseconds over a faint knock.
                smoothed += (noise() - smoothed) * 0.25
                output[index] = smoothed * exp(-t * 90) * 0.9 + tone(190, index) * exp(-t * 60) * 0.3
            case .stamp:
                // A thump falling in pitch with a short click on top.
                let frequency = 150 - 70 * progress
                output[index] = sin(2 * .pi * frequency * t) * exp(-t * 28) * 0.9 + noise() * exp(-t * 220) * 0.4
            case .crumple:
                // Bursts of bright noise, irregular, fading.
                smoothed += (noise() - smoothed) * 0.7
                let burst = pow(abs(sin(2 * .pi * (13 + 9 * progress) * t + sin(41 * t) * 2)), 6)
                output[index] = smoothed * burst * (1 - progress) * 0.8
            case .binDrop:
                let frequency = 220 - 120 * progress
                output[index] = sin(2 * .pi * frequency * t) * exp(-t * 22) * 0.8 + noise() * exp(-t * 160) * 0.25
            case .allDone:
                // Two notes a fifth apart, the second a moment later.
                let first = tone(784, index) * exp(-t * 9)
                let second = t > 0.11 ? tone(1175, index) * exp(-(t - 0.11) * 8) : 0
                output[index] = (first + second) * 0.45 * min(1, t / 0.006)
            }
        }
        return output
    }

    static func wav(for sound: PlateSound, sampleRate: Double = 44_100) -> Data {
        var pcm = Data()
        for value in samples(for: sound, sampleRate: sampleRate) {
            var sample = Int16(max(-1, min(1, value)) * Double(Int16.max)).littleEndian
            withUnsafeBytes(of: &sample) { pcm.append(contentsOf: $0) }
        }
        return BuddyWAVFileBuilder.buildWAVData(fromPCM16MonoAudio: pcm, sampleRate: Int(sampleRate))
    }
}

@MainActor
final class PlateSoundPlayer {
    /// Several players per sound: six cards land within half a second.
    private var players: [PlateSound: [AVAudioPlayer]] = [:]
    var isOn: () -> Bool = { true }

    func prepare() {
        guard isOn() else { return }
        for sound in PlateSound.allCases where players[sound] == nil {
            let data = PlateSoundSynth.wav(for: sound)
            players[sound] = (0..<(sound == .place ? 3 : 1)).compactMap { _ in
                let player = try? AVAudioPlayer(data: data)
                player?.volume = sound == .place ? 0.22 : 0.3
                player?.prepareToPlay()
                return player
            }
        }
    }

    func play(_ sound: PlateSound, after delay: TimeInterval = 0) {
        guard isOn() else { return }
        prepare()
        guard let pool = players[sound], let player = pool.first(where: { !$0.isPlaying }) ?? pool.first else { return }
        player.currentTime = 0
        if delay > 0 { player.play(atTime: player.deviceCurrentTime + delay) } else { player.play() }
    }
}
