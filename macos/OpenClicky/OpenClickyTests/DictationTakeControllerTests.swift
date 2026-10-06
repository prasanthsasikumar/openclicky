//
//  DictationTakeControllerTests.swift
//  OpenClickyTests
//
//  The take state machine, driven through a fake capture side and a fake host: hold, tap, double
//  tap, esc, the hands-free gesture, a cancel while a model is at work, and where the words go.
//

import AVFoundation
import Combine
import Foundation
import Testing
@testable import OpenClicky

@MainActor
private final class FakeCapture: DictationCapturing {
    var isDictationInProgress = false
    var transcriptionProviderDisplayName = "fake"
    var preferredMicrophoneUID: String?
    var audioRetentionSink: (@Sendable (AVAudioPCMBuffer) -> Void)?
    let level = CurrentValueSubject<CGFloat, Never>(0)
    let errors = CurrentValueSubject<String?, Never>(nil)
    let activity = CurrentValueSubject<Bool, Never>(false)
    var audioPowerLevelPublisher: AnyPublisher<CGFloat, Never> { level.eraseToAnyPublisher() }
    var errorMessagePublisher: AnyPublisher<String?, Never> { errors.eraseToAnyPublisher() }
    var sessionActivityPublisher: AnyPublisher<Bool, Never> { activity.eraseToAnyPublisher() }

    private(set) var starts = 0
    private(set) var stops = 0
    private(set) var cancels = 0
    private var onPartial: ((String) -> Void)?
    private var onFinal: ((String) -> Void)?

    func startPushToTalkFromKeyboardShortcut(currentDraftText: String, updateDraftText: @escaping (String) -> Void, submitDraftText: @escaping (String) -> Void) async {
        starts += 1
        isDictationInProgress = true
        activity.send(true)
        onPartial = updateDraftText
        onFinal = submitDraftText
    }

    func stopPushToTalkFromKeyboardShortcut() { stops += 1 }

    func cancelCurrentDictation(preserveDraftText: Bool) {
        cancels += 1
        isDictationInProgress = false
        onFinal = nil
        activity.send(false)
    }

    func replaceTranscriptionProvider(_ provider: any BuddyTranscriptionProvider) {}

    /// The engine's words arrive: the session ends, then the final text is delivered (as the real
    /// manager does: state first, callback second).
    func deliverFinal(_ text: String) {
        let final = onFinal
        isDictationInProgress = false
        onFinal = nil
        activity.send(false)
        final?(text)
    }

    func deliverPartial(_ text: String) { onPartial?(text) }
}

private struct SlowPolisher: TakePolisher {
    let seconds: Double
    let answer: String
    var displayName: String { "slow" }
    func polish(system: String, user: String) async throws -> String {
        try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        return answer
    }
}

@MainActor
private struct Harness {
    let settings: DictationSettings
    let capture = FakeCapture()
    let store: TakeStore
    let orb = OrbModel()
    let controller: DictationTakeController
    let pasted: Pasted

    final class Pasted { var texts: [String] = []; var frontApp: String? = "com.apple.Notes" }

    init(polisher: (any TakePolisher)? = nil, field: FocusedFieldSnapshot? = nil) throws {
        let suite = "openclicky-tests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        settings = DictationSettings(defaults: defaults)
        settings.sounds = false
        settings.haptics = false
        settings.engine = .offline
        settings.readNearbyText = false
        settings.polishWithModel = true
        settings.polishOfflineTakes = polisher != nil
        store = try TakeStore(fileURL: FileManager.default.temporaryDirectory.appendingPathComponent("takes-\(UUID().uuidString).sqlite"))
        let pasted = Pasted()
        self.pasted = pasted
        let snapshot = field ?? FocusedFieldSnapshot(appBundleID: "com.apple.Notes", appName: "Notes", role: "AXTextArea", isEditable: true, isSecure: false, value: "", selectedText: nil)
        var host = DictationHost()
        host.readFocusedField = { snapshot }
        host.readNearbyTerms = { [] }
        host.frontAppBundleID = { pasted.frontApp }
        host.isSecureInputOn = { false }
        host.isAccessibilityTrusted = { true }
        host.paste = { text in pasted.texts.append(text); return .typed }
        host.verifyPaste = { _, _ in .verified }
        host.makePolisher = { polisher }
        host.engineUnavailableReason = { _ in nil }
        let spaceStore = DictationSpaceStore(directoryURL: FileManager.default.temporaryDirectory.appendingPathComponent("space-\(UUID().uuidString)"))
        let audioStore = TakeAudioStore(directoryURL: FileManager.default.temporaryDirectory.appendingPathComponent("audio-\(UUID().uuidString)"))
        controller = DictationTakeController(settings: settings, spaceStore: spaceStore, takeStore: store, orb: orb, capture: capture, host: host, audioStore: audioStore)
    }

    /// Lets the controller's start task and main-queue hops run.
    func settle(_ seconds: Double = 0.05) async {
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }
}

struct DictationTakeControllerTests {

    @Test @MainActor func holdingTheKeyPastesTheWordsAndRecordsTheTake() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        #expect(h.controller.state == .starting)
        await h.settle()
        #expect(h.controller.state == .listening)
        #expect(h.capture.starts == 1)
        h.controller.handle(.dictationReleased(wasTap: false))
        #expect(h.controller.state == .finishing)
        #expect(h.capture.stops == 1)
        h.capture.deliverFinal("hello there")
        await h.settle(0.3)
        #expect(h.pasted.texts == ["Hello there"])
        #expect(h.controller.state == .idle)
        let rows = try h.store.recent()
        #expect(rows.count == 1)
        #expect(rows.first?.formattedText == "Hello there")
        #expect(rows.first?.pasteOutcome == .verified)
        #expect(rows.first?.appBundleID == "com.apple.Notes")
    }

    @Test @MainActor func aTapKeepsListeningUntilTheNextPress() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: true))
        #expect(h.controller.state == .listening)
        #expect(h.capture.stops == 0)
        h.controller.handle(.dictationPressed)
        #expect(h.controller.state == .finishing)
        #expect(h.capture.stops == 1)
        h.controller.handle(.dictationReleased(wasTap: true))
        #expect(h.controller.state == .finishing)
        #expect(h.capture.stops == 1)
    }

    @Test @MainActor func twoQuickTapsCancelWithoutPasting() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: true))
        h.controller.handle(.dictationPressed)
        h.controller.handle(.dictationReleased(wasTap: true))
        h.controller.handle(.dictationDoubleTapped)
        #expect(h.controller.state == .idle)
        #expect(h.capture.cancels == 1)
        await h.settle(0.3)
        #expect(h.pasted.texts.isEmpty)
        #expect(try h.store.recent().isEmpty)
    }

    @Test @MainActor func escapeCancelsAListeningTake() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        #expect(h.controller.handle(.escapePressed))
        #expect(h.controller.state == .idle)
        #expect(h.capture.cancels == 1)
        #expect(!h.controller.handle(.escapePressed))
    }

    @Test @MainActor func aCancelledTakeNeverPastesIntoTheNextOne() async throws {
        let h = try Harness(polisher: SlowPolisher(seconds: 0.4, answer: "Old words, polished."))
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        h.capture.deliverFinal("old words")
        await h.settle()
        // esc while the model is at work, then a new take at once.
        h.controller.handle(.escapePressed)
        #expect(h.controller.state == .idle)
        h.controller.handle(.dictationPressed)
        await h.settle()
        #expect(h.controller.state == .listening)
        #expect(h.capture.starts == 2)
        await h.settle(0.6)
        // The old polish has finished by now; nothing was pasted and the new take still listens.
        #expect(h.pasted.texts.isEmpty)
        #expect(h.controller.state == .listening)
        h.controller.handle(.dictationReleased(wasTap: false))
        h.capture.deliverFinal("new words")
        await h.settle(0.7)
        #expect(h.pasted.texts == ["New words, polished.".replacingOccurrences(of: "New words, polished.", with: "Old words, polished.")])
    }

    @Test @MainActor func aQuickControlTapNeverOpensAnEditTake() async throws {
        let h = try Harness()
        // fn + ⌃ tapped twice, as the recogniser reports it: no take, hands-free goes to the companion.
        h.controller.handle(.dictationEditPressed)
        h.controller.handle(.dictationReleased(wasTap: true))
        h.controller.handle(.dictationEditPressed)
        h.controller.handle(.dictationReleased(wasTap: true))
        #expect(!h.controller.handle(.handsFreeToggleRequested))
        await h.settle(0.5)
        #expect(h.controller.state == .idle)
        #expect(h.capture.starts == 0)
    }

    @Test @MainActor func controlHeldPastTheTapWindowIsAnEditTake() async throws {
        let h = try Harness()
        h.controller.handle(.dictationEditPressed)
        #expect(h.controller.state == .idle)
        await h.settle(0.5)
        #expect(h.controller.state == .listening)
        #expect(h.orb.phase == .editListening)
    }

    @Test @MainActor func controlJoiningAHeldKeyTurnsTheTakeIntoAnEdit() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationEditModifierJoined)
        #expect(h.orb.phase == .editListening)
        #expect(h.capture.starts == 1)
    }

    @Test @MainActor func nothingHeardIsNotRecorded() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        h.capture.deliverFinal("   ")
        await h.settle(0.3)
        #expect(h.controller.state == .idle)
        #expect(h.pasted.texts.isEmpty)
        #expect(try h.store.recent().isEmpty)
        #expect(h.orb.phase == .failed("didn't catch that"))
    }

    @Test @MainActor func aSessionThatEndsWithoutWordsFailsTheTake() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        // The engine gave up without a transcript: the manager's session flag drops.
        h.capture.cancelCurrentDictation(preserveDraftText: false)
        await h.settle(0.4)
        #expect(h.controller.state == .idle)
        #expect(h.orb.phase == .failed("didn't catch that"))
    }

    @Test @MainActor func theWordsStayInTheOrbWhenTheAppChanged() async throws {
        let h = try Harness()
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        h.pasted.frontApp = "com.apple.Safari"
        h.capture.deliverFinal("hello")
        await h.settle(0.3)
        #expect(h.pasted.texts.isEmpty)
        #expect(h.orb.isBoxOpen)
        #expect(h.orb.boxText == "Hello")
        #expect(try h.store.recent().first?.pasteOutcome == .leftInOrb)
    }

    @Test @MainActor func aSecureFieldIsNeverPastedInto() async throws {
        let secure = FocusedFieldSnapshot(appBundleID: "com.apple.Safari", appName: "Safari", role: "AXTextField", isEditable: false, isSecure: true, value: nil, selectedText: nil)
        let h = try Harness(field: secure)
        h.pasted.frontApp = "com.apple.Safari"
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        h.capture.deliverFinal("my password")
        await h.settle(0.3)
        #expect(h.pasted.texts.isEmpty)
        #expect(h.orb.phase == .done("not pasting into a password field"))
    }

    @Test @MainActor func incognitoPastesButRecordsNothing() async throws {
        let h = try Harness()
        h.settings.incognito = true
        h.controller.handle(.dictationPressed)
        await h.settle()
        h.controller.handle(.dictationReleased(wasTap: false))
        h.capture.deliverFinal("secret plans")
        await h.settle(0.3)
        #expect(h.pasted.texts == ["Secret plans"])
        #expect(try h.store.recent().isEmpty)
    }
}
