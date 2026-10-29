//
//  KokoroVoice.swift
//  OpenClicky
//
//  Kokoro: an open 82M-parameter voice model that runs on this Mac, free and offline, through
//  sherpa-onnx's C library. The engine ships inside the app (Contents/Frameworks, signed with the
//  app's identity by scripts/embed-kokoro-engine.sh, so it loads with library validation on); only
//  the model (350 MB) is downloaded, when Kokoro is picked in Settings, checked against a pinned
//  SHA-256 and unpacked into Application Support.
//
//  The loaded model takes about 400 MB of memory, so it lives in a helper process
//  (KokoroHelper.swift) that runs only around a conversation: `prepareForAnswer` starts and warms
//  it the moment the ask shortcut is pressed (well inside the listening and thinking that follow),
//  and it is ended after three minutes without an answer, or at once when macOS reports memory
//  pressure. A loaded model speaks a sentence in
//  about 0.3× its length on an M2 Pro and 0.55× under Rosetta (the closest stand-in measured for
//  an Intel Mac). The int8 model is smaller but was slower on both (0.86× / 1.3×).
//

import Combine
import Foundation

@MainActor
final class KokoroVoice: ObservableObject {
    static let shared = KokoroVoice()

    typealias State = ModelInstallState

    @Published private(set) var state: State

    /// The embedded sherpa-onnx version (scripts/embed-kokoro-engine.sh); must match sherpa-onnx-c-api.h.
    nonisolated static let runtimeVersion = "1.13.8"
    /// Kokoro v1.0, multilingual, full precision. sherpa-onnx re-packed this archive on 2026-10-26
    /// (same files inside, new SHA-256), and every download against the old pin failed its check.
    nonisolated static let modelDownload = ModelDownload(
        url: URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2")!,
        sha256: "60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4", bytes: 349_886_476)
    /// What Settings says before the download, rounded.
    nonisolated static let downloadSizeText = "350 MB"

    nonisolated static var folder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenClicky/Kokoro", isDirectory: true)
    }
    /// The engine, inside the app.
    nonisolated static var libraryFolder: URL { SherpaLibrary.folder }
    nonisolated static var modelFolder: URL { folder.appendingPathComponent("kokoro-multi-lang-v1_0", isDirectory: true) }

    nonisolated static var isInstalled: Bool {
        let fileManager = FileManager.default
        return SherpaLibrary.isEmbedded() && [modelFolder.appendingPathComponent("model.onnx"),
                modelFolder.appendingPathComponent("voices.bin"),
                modelFolder.appendingPathComponent("tokens.txt"),
                modelFolder.appendingPathComponent("espeak-ng-data")].allSatisfy { fileManager.fileExists(atPath: $0.path) }
    }

    private var downloadTask: Task<Void, Never>?
    private let engine = SherpaHelperClient(flag: KokoroHelper.flag, logName: "kokoro")
    /// How long the model stays in memory after the last answer it spoke.
    nonisolated static let idleUnloadSeconds: UInt64 = 180
    private var idleUnloadTask: Task<Void, Never>?
    private var memoryPressureSource: DispatchSourceMemoryPressure?

    private init() {
        state = Self.isInstalled ? .installed : .notInstalled
        // macOS is short of memory: give the 400 MB back now; the next question loads it again.
        let source = DispatchSource.makeMemoryPressureSource(eventMask: [.warning, .critical], queue: .main)
        source.setEventHandler { [weak self] in
            Task { @MainActor in self?.unload(reason: "memory pressure") }
        }
        source.resume()
        memoryPressureSource = source
    }

    // MARK: Memory

    /// Called when the ask shortcut is pressed or a typed question is sent: loads and warms the
    /// model while the question is still being heard, if Kokoro is the voice.
    func prepareForAnswer() {
        warmUpIfSpeaking()
    }

    /// Restarts the idle clock; when it runs out the model leaves memory.
    private func scheduleIdleUnload() {
        idleUnloadTask?.cancel()
        idleUnloadTask = Task { [weak self] in
            var seconds = Self.idleUnloadSeconds
            #if DEBUG
            // Lets the voice smoke see the unload without waiting three minutes.
            if let override = ProcessInfo.processInfo.environment["OPENCLICKY_KOKORO_IDLE_SECONDS"].flatMap(UInt64.init) { seconds = override }
            #endif
            try? await Task.sleep(nanoseconds: seconds * 1_000_000_000)
            guard !Task.isCancelled else { return }
            self?.unload(reason: "idle")
        }
    }

    private func unload(reason: String) {
        idleUnloadTask?.cancel()
        idleUnloadTask = nil
        engine.unload { wasLoaded in
            if wasLoaded { AppLog.append("kokoro: unloaded (\(reason))") }
        }
    }

    // MARK: Speakers

    struct Speaker: Identifiable, Equatable, Sendable {
        /// The speaker's index in the model's voice table.
        let id: Int
        let name: String
        let languageCode: String
        let accent: String
        var label: String { "\(name), \(accent)" }
    }

    /// The speakers offered in Settings: the model has 54, these are the clearest per language.
    nonisolated static let speakers: [Speaker] = [
        Speaker(id: 3, name: "Heart", languageCode: "en", accent: "American"),
        Speaker(id: 2, name: "Bella", languageCode: "en", accent: "American"),
        Speaker(id: 6, name: "Nicole", languageCode: "en", accent: "American"),
        Speaker(id: 9, name: "Sarah", languageCode: "en", accent: "American"),
        Speaker(id: 16, name: "Michael", languageCode: "en", accent: "American"),
        Speaker(id: 11, name: "Adam", languageCode: "en", accent: "American"),
        Speaker(id: 21, name: "Emma", languageCode: "en", accent: "British"),
        Speaker(id: 26, name: "George", languageCode: "en", accent: "British"),
        Speaker(id: 31, name: "Alpha", languageCode: "hi", accent: "Hindi"),
        Speaker(id: 33, name: "Omega", languageCode: "hi", accent: "Hindi"),
        Speaker(id: 28, name: "Dora", languageCode: "es", accent: "Spanish"),
        Speaker(id: 30, name: "Siwis", languageCode: "fr", accent: "French"),
        Speaker(id: 35, name: "Sara", languageCode: "it", accent: "Italian"),
        Speaker(id: 37, name: "Alpha", languageCode: "ja", accent: "Japanese"),
        Speaker(id: 42, name: "Dora", languageCode: "pt", accent: "Portuguese"),
        Speaker(id: 45, name: "Xiaobei", languageCode: "zh", accent: "Mandarin"),
    ]

    /// The speaker for the reply language: the one picked if it speaks it, else the language's
    /// first; nil when Kokoro doesn't speak the language.
    nonisolated static func speaker(forLanguage languageCode: String, picked: Int?) -> Speaker? {
        let forLanguage = speakers.filter { $0.languageCode == languageCode }
        return forLanguage.first(where: { $0.id == picked }) ?? forLanguage.first
    }

    /// Kokoro's language hint for espeak (sherpa-onnx's `lang`); English and Mandarin use the
    /// model's own lexicons instead.
    nonisolated static func languageHint(for languageCode: String) -> String {
        switch languageCode {
        case "en": return "en-us"
        case "zh": return "zh"
        case "pt": return "pt-br"
        default: return languageCode
        }
    }

    // MARK: Speaking

    /// `text` in Kokoro's voice, as WAV bytes. The first call loads the model (about 3 s).
    func audio(for text: String, languageCode: String = ReplyLanguage.currentCode) async throws -> Data {
        guard Self.isInstalled else { throw KokoroError.notInstalled }
        guard let speaker = Self.speaker(forLanguage: languageCode, picked: OpenClickyConfiguration.settings.kokoroSpeaker) else {
            throw KokoroError.languageNotSpoken
        }
        let request = KokoroEngine.Request(text: text, speakerID: speaker.id, languageHint: Self.languageHint(for: languageCode),
                                           libraryFolder: Self.libraryFolder, modelFolder: Self.modelFolder)
        defer { scheduleIdleUnload() }
        guard let wav = Data(base64Encoded: try await engine.send(request, as: KokoroHelper.Reply.self).wav) else { throw KokoroError.nothingGenerated }
        return wav
    }


    /// Loads the model and speaks one throwaway word, so the first real answer starts at once.
    func warmUpIfSpeaking() {
        guard Self.isInstalled, SpeechVoiceChoice.current() == .kokoro else { return }
        Task {
            let startedAt = Date()
            _ = try? await audio(for: "ok")
            AppLog.append(String(format: "kokoro: warmed up in %.1f s", Date().timeIntervalSince(startedAt)))
        }
    }

    // MARK: Download

    /// Downloads, checks and unpacks Kokoro; does nothing while a download runs or once installed.
    /// Only picking Kokoro in Settings calls this: account answers speak with ElevenLabs, so Kokoro
    /// is never fetched on its own.
    func download() {
        guard downloadTask == nil, !Self.isInstalled else {
            if Self.isInstalled { state = .installed }
            return
        }
        state = .downloading(fraction: 0)
        downloadTask = Task { [weak self] in
            do {
                try await Self.modelDownload.install(into: Self.folder) { fraction in
                    Task { @MainActor in
                        // A late progress report must not undo the installed state.
                        guard let self, self.state.isBusy else { return }
                        self.state = fraction >= 1 ? .unpacking : .downloading(fraction: fraction)
                    }
                }
                guard Self.isInstalled else { throw ModelDownloadError.unpackFailed("the files weren't where they should be") }
                AppLog.append("kokoro: installed")
                self?.state = .installed
            } catch where error is CancellationError || (error as? URLError)?.code == .cancelled {
                self?.state = .notInstalled
            } catch {
                AppLog.append("kokoro: download failed: \(error.localizedDescription)")
                self?.state = .failed(error.localizedDescription)
            }
            self?.downloadTask = nil
        }
    }

    func cancelDownload() {
        downloadTask?.cancel()
    }

    /// Deletes the download and unloads the model.
    func remove() {
        downloadTask?.cancel()
        engine.unload()
        try? FileManager.default.removeItem(at: Self.folder)
        state = .notInstalled
    }
}

enum KokoroError: Error, LocalizedError, Equatable {
    case notInstalled
    case languageNotSpoken
    case modelNotLoaded
    case nothingGenerated

    var errorDescription: String? {
        switch self {
        case .notInstalled: return "Kokoro isn't downloaded yet."
        case .languageNotSpoken: return "Kokoro doesn't speak this language."
        case .modelNotLoaded: return "Kokoro's model didn't load."
        case .nothingGenerated: return "Kokoro said nothing."
        }
    }
}
