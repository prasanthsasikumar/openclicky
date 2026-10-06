//
//  DictationCapturing.swift
//  OpenClicky
//
//  The microphone-and-engine side of a take, as the take controller sees it: start, stop, cancel,
//  and three streams (level, errors, whether a session is open). `BuddyDictationManager` is the
//  real one; the controller's tests drive a fake, since a real session needs a microphone.
//

import AppKit
import ApplicationServices
import AVFoundation
import Carbon.HIToolbox
import Combine
import Foundation

@MainActor
protocol DictationCapturing: AnyObject {
    var isDictationInProgress: Bool { get }
    var transcriptionProviderDisplayName: String { get }
    var preferredMicrophoneUID: String? { get set }
    var audioRetentionSink: (@Sendable (AVAudioPCMBuffer) -> Void)? { get set }

    var audioPowerLevelPublisher: AnyPublisher<CGFloat, Never> { get }
    var errorMessagePublisher: AnyPublisher<String?, Never> { get }
    /// True from the moment a keyboard session opens until its transcript was delivered or it ended.
    var sessionActivityPublisher: AnyPublisher<Bool, Never> { get }

    func startPushToTalkFromKeyboardShortcut(
        currentDraftText: String,
        updateDraftText: @escaping (String) -> Void,
        submitDraftText: @escaping (String) -> Void
    ) async
    func stopPushToTalkFromKeyboardShortcut()
    func cancelCurrentDictation(preserveDraftText: Bool)
    func replaceTranscriptionProvider(_ provider: any BuddyTranscriptionProvider)
}

extension BuddyDictationManager: DictationCapturing {
    var audioPowerLevelPublisher: AnyPublisher<CGFloat, Never> { $currentAudioPowerLevel.eraseToAnyPublisher() }
    var errorMessagePublisher: AnyPublisher<String?, Never> { $lastErrorMessage.eraseToAnyPublisher() }
    var sessionActivityPublisher: AnyPublisher<Bool, Never> { $isKeyboardShortcutSessionActiveOrFinalizing.eraseToAnyPublisher() }
}

/// What the controller needs from the Mac around a take; injectable so tests paste nowhere.
struct DictationHost {
    var readFocusedField: () -> FocusedFieldSnapshot = { FocusedFieldReader.snapshot() }
    var readNearbyTerms: () -> [String] = { FocusedFieldReader.nearbyTerms() }
    var frontAppBundleID: () -> String? = { NSWorkspace.shared.frontmostApplication?.bundleIdentifier }
    var isSecureInputOn: () -> Bool = { IsSecureEventInputEnabled() }
    var isAccessibilityTrusted: () -> Bool = { AXIsProcessTrusted() }
    var paste: @MainActor (String) -> FrontAppTextInserter.Outcome = { FrontAppTextInserter.insert($0) }
    var verifyPaste: (String, FocusedFieldSnapshot) async -> PasteLanding = { await PasteLanding.verify(text: $0, before: $1) }
    var makePolisher: () -> (any TakePolisher)? = { DictationEngineResolver.makePolisher() }
    var engineUnavailableReason: (DictationEngineChoice) -> String? = { DictationEngineResolver.unavailableReason(for: $0) }
}
