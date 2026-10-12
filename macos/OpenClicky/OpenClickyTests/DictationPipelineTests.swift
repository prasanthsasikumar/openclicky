//
//  DictationPipelineTests.swift
//  OpenClickyTests
//
//  The dictation pipeline's shared rules: which engines keep the voice on this Mac, the stored
//  language read off the main actor, what the island says about an engine that can't run, a
//  session's callbacks going quiet once a newer session took over, and errors that name the model
//  that really refused.
//

import AVFoundation
import Foundation
import Testing
@testable import OpenClicky

/// A provider that never opens anything: the manager's session-identity tests need one to exist.
private struct UnusedTranscriptionProvider: BuddyTranscriptionProvider {
    let displayName = "unused"
    let requiresSpeechRecognitionPermission = false
    let isConfigured = true
    let unavailableExplanation: String? = nil

    func startStreamingSession(
        keyterms: [String],
        onTranscriptUpdate: @escaping (String) -> Void,
        onFinalTranscriptReady: @escaping (String) -> Void,
        onError: @escaping (Error) -> Void
    ) async throws -> any BuddyStreamingTranscriptionSession {
        throw CancellationError()
    }
}

private struct EngineRefusal: LocalizedError {
    var errorDescription: String? { "the engine refused" }
}

struct DictationPipelineTests {

    // MARK: engines on this Mac

    @Test func onlyTheOfflineEnginesKeepTheVoiceOnThisMac() {
        let onDeviceEngines = DictationEngineChoice.allCases.filter(\.isOnDevice)
        #expect(Set(onDeviceEngines) == [.offline, .parakeet])
        for engine in DictationEngineChoice.allCases {
            #expect((engine.audioDestinationTag == "Private · offline") == engine.isOnDevice)
        }
    }

    @Test @MainActor func theStoredLanguageIsTheOneTheSettingsWrote() {
        let defaults = ScratchDefaults.make()
        #expect(DictationSettings.storedLanguage(defaults: defaults) == .auto)
        let settings = DictationSettings(defaults: defaults)
        settings.languageCode = "hi-IN"
        #expect(DictationSettings.storedLanguage(defaults: defaults).bareCode == "hi")
    }

    // MARK: the island's engine warning

    @Test func theWarningSaysTheEnginesRealReasonAndNamesThePageItOpens() throws {
        let download = try #require(NotchEngineWarning(engine: .parakeet, unavailableReason: "needs its 465 MB download"))
        #expect(download.message.hasPrefix("parakeet needs its 465 MB download"))
        #expect(download.buttonTitle == "open dictation settings")
        #expect(!download.message.contains("backend"))

        let language = try #require(NotchEngineWarning(engine: .parakeet, unavailableReason: DictationEngineResolver.parakeetLanguageNotHeardReason))
        #expect(language.message.hasPrefix(DictationEngineResolver.parakeetLanguageNotHeardReason))
        #expect(language.buttonTitle == "open language settings")

        let sarvam = try #require(NotchEngineWarning(engine: .sarvam, unavailableReason: "needs your sarvam key"))
        #expect(sarvam.buttonTitle == "add a key")

        #expect(NotchEngineWarning(engine: .openclicky, unavailableReason: nil) == nil)
        #expect(!(NotchEngineWarning(engine: .assemblyai, unavailableReason: "needs your own key")?.buttonTitle.contains("shell.json") ?? true))
    }

    @Test func anEngineOnThisMacIsNeverSentToTheAccountPage() {
        #expect(NotchVoiceDestination.setupDestination(for: .parakeet, unavailableReason: "needs its 465 MB download") == .dictationSettings)
        #expect(NotchVoiceDestination.setupDestination(for: .openclicky, unavailableReason: "needs your own key") == .keySettings)
        #expect(NotchVoiceDestination.setupDestination(for: .assemblyai, unavailableReason: "needs an openclicky account: assemblyai runs on openclicky's key") == .accountSettings)
        #expect(NotchVoiceDestination.setupDestination(for: .sarvam, unavailableReason: nil) == .dictationSettings)
    }

    // MARK: one session's callbacks never reach the next

    @Test @MainActor func aCallbackFromAnOlderSessionIsIgnored() async {
        let manager = BuddyDictationManager(transcriptionProvider: UnusedTranscriptionProvider())
        let olderSessionCallbacks = manager.makeTranscriptionCallbacks(forSessionIdentity: manager.beginNewSessionIdentity())
        let currentSessionCallbacks = manager.makeTranscriptionCallbacks(forSessionIdentity: manager.beginNewSessionIdentity())

        olderSessionCallbacks.onError(EngineRefusal())
        try? await Task.sleep(nanoseconds: 100_000_000)
        #expect(manager.lastErrorMessage == nil)

        currentSessionCallbacks.onError(EngineRefusal())
        try? await Task.sleep(nanoseconds: 100_000_000)
        #expect(manager.lastErrorMessage == "the engine refused")
    }

    // MARK: errors name the model that refused

    @Test func anOwnAnthropicKeyRefusalIsAnthropicsNotOpenClickys() async {
        let polisher = AnthropicTakePolisher(anthropicKey: "ak-user", send: { request in
            (Data(#"{"error":{"message":"invalid x-api-key"}}"#.utf8),
             HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!)
        })
        do {
            _ = try await polisher.polish(system: "tidy", user: "hello")
            Issue.record("a 401 must throw")
        } catch let error as TakePolishError {
            guard case .refused(let vendor, let status, _) = error else { Issue.record("expected a refusal, got \(error)"); return }
            #expect(vendor == "anthropic")
            #expect(status == 401)
            #expect(error.localizedDescription.hasPrefix("anthropic refused"))
            #expect(PolishFailureNotice.sentence(for: error) == PolishFailureNotice.ownKeyRefusedSentence)
        } catch {
            Issue.record("expected TakePolishError, got \(error)")
        }
    }

    @Test func anEmptyHeyClickyEditNamesTheModelThatMadeIt() async {
        struct EmptyPolisher: TakePolisher {
            var displayName: String { "Apple Intelligence (on this Mac)" }
            func polish(system: String, user: String) async throws -> String { "   " }
        }
        let context = TakeFormattingContext(style: DictationSpace.empty.style(forAppBundleID: nil), dictionary: [], shortcuts: [],
                                            appName: nil, language: .auto, script: .native)
        do {
            _ = try await HeyClickyEditor.edit(selection: "hello", instruction: "shorter", context: context, polisher: EmptyPolisher())
            Issue.record("an empty edit must throw")
        } catch {
            #expect(error as? TakePolishError == .emptyAnswer(vendor: "Apple Intelligence (on this Mac)"))
            #expect(!error.localizedDescription.contains("sarvam"))
        }
    }
}
