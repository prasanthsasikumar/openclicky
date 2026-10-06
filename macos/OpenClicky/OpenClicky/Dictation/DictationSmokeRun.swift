//
//  DictationSmokeRun.swift
//  OpenClicky
//
//  `--openclicky-smoke-transcribe <wav> [engine]`: a recording is fed to an engine as if it were
//  the microphone, then formatted the way a take is. The real-provider check for Sarvam (a key is
//  needed) and the offline engine (speech recognition permission is needed), run by hand.
//

import AVFoundation
import Foundation

@MainActor
enum DictationSmokeRun {
    static func transcribe(filePath: String, engineName: String) async -> Int32 {
        guard let engine = DictationEngineChoice(rawValue: engineName) else {
            print("smoke: unknown engine \(engineName); one of \(DictationEngineChoice.allCases.map(\.rawValue).joined(separator: ", "))")
            return 2
        }
        let settings = DictationSettings.shared
        let provider: any BuddyTranscriptionProvider
        if engine == .sarvam, let key = ProcessInfo.processInfo.environment["OPENCLICKY_SARVAM_KEY"], !key.isEmpty {
            // The environment's key is for this run only; nothing is written to shell.json.
            provider = SarvamTranscriptionProvider(key: { key }, language: { settings.language })
        } else {
            if let reason = DictationEngineResolver.unavailableReason(for: engine) {
                print("smoke: \(engine.rawValue) unavailable: \(reason)")
                return 2
            }
            provider = DictationEngineResolver.makeProvider(for: engine, settings: settings)
        }
        print("smoke: engine \(provider.displayName), language \(settings.language.code), file \(filePath)")

        let file: AVAudioFile
        do { file = try AVAudioFile(forReading: URL(fileURLWithPath: filePath)) } catch {
            print("smoke: cannot read \(filePath): \(error.localizedDescription)")
            return 2
        }
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else { return 2 }
        do { try file.read(into: buffer) } catch {
            print("smoke: cannot decode \(filePath): \(error.localizedDescription)")
            return 2
        }

        let started = Date()
        var finalText: String?
        var failure: String?
        let session: any BuddyStreamingTranscriptionSession
        do {
            session = try await provider.startStreamingSession(
                keyterms: [],
                onTranscriptUpdate: { partial in print("smoke: … \(partial)") },
                onFinalTranscriptReady: { text in finalText = text },
                onError: { error in failure = error.localizedDescription })
        } catch {
            print("smoke: could not open the engine: \(error.localizedDescription)")
            return 1
        }
        // Feed the file in 100 ms slices, paced like a microphone so streaming engines behave.
        let sliceFrames = AVAudioFrameCount(file.processingFormat.sampleRate / 10)
        var offset: AVAudioFrameCount = 0
        while offset < buffer.frameLength {
            let count = min(sliceFrames, buffer.frameLength - offset)
            guard let slice = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: count) else { break }
            for channel in 0..<Int(buffer.format.channelCount) {
                if let from = buffer.floatChannelData?[channel], let to = slice.floatChannelData?[channel] {
                    to.update(from: from + Int(offset), count: Int(count))
                }
            }
            slice.frameLength = count
            session.appendAudioBuffer(slice)
            offset += count
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        session.requestFinalTranscript()
        var waited = 0.0
        while finalText == nil, failure == nil, waited < session.finalTranscriptFallbackDelaySeconds + 15 {
            try? await Task.sleep(nanoseconds: 200_000_000)
            waited += 0.2
        }
        if let failure {
            print("smoke: engine failed: \(failure)")
            return 1
        }
        guard let raw = finalText else {
            print("smoke: no final transcript after \(Int(waited)) s")
            return 1
        }
        print("smoke: heard in \(Int(Date().timeIntervalSince(started) * 1000)) ms: \(raw)")
        let space = DictationSpaceStore().space
        let context = TakeFormattingContext(style: space.style(forAppBundleID: nil), dictionary: space.dictionary, shortcuts: space.shortcuts, appName: "smoke", language: settings.language, script: settings.script)
        let polisher: (any TakePolisher)? = {
            if let key = ProcessInfo.processInfo.environment["OPENCLICKY_SARVAM_KEY"], !key.isEmpty { return SarvamTakePolisher(client: SarvamSpeechClient(key: key)) }
            return DictationEngineResolver.makePolisher()
        }()
        let formatted = await TakeFormatter.format(raw, context: context, polisher: polisher, wantsModel: engine == .offline ? settings.polishOfflineTakes : settings.polishWithModel)
        print("smoke: formatted (\(formatted.formattingDegraded ? "local rules" : "model")): \(formatted.text)")
        return raw.isEmpty ? 1 : 0
    }
}
