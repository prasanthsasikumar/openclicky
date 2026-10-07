//
//  SettingsPrivacyPage.swift
//  OpenClicky
//
//  Settings → privacy (was permissions + privacy & data): what openclicky can see, and what it
//  keeps. The three permissions with their live state, reading nearby text, what is kept on this
//  mac and for how long, where each file lives, and a summary of what leaves the mac.
//

import AppKit
import AVFoundation
import Combine
import Speech
import SwiftUI

extension SettingsCatalog {
    func privacyItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "privacy.microphone", page: .privacy, section: "what it can use",
                title: "microphone", detail: "openclicky listens only when you start a take. with this mac as the engine, audio never leaves it.",
                keywords: ["permission", "mic", "audio", "grant"],
                view: AnyView(PermissionRow(permission: .microphone))),
            SettingsItem(
                id: "privacy.accessibility", page: .privacy, section: "what it can use",
                title: "accessibility", detail: "lets openclicky paste takes into other apps and read nearby text.",
                keywords: ["permission", "paste", "grant", "keys", "event tap"],
                view: AnyView(PermissionRow(permission: .accessibility))),
            SettingsItem(
                id: "privacy.speechRecognition", page: .privacy, section: "what it can use",
                title: "speech recognition", detail: "apple’s on-device recogniser, for this mac as the engine.",
                keywords: ["permission", "offline", "apple", "grant", "siri"],
                view: AnyView(PermissionRow(permission: .speechRecognition))),
            SettingsItem(
                id: "privacy.readNearbyText", page: .privacy, section: "what it can use",
                title: "read nearby text", detail: "helps spell names and terms on screen right. off stops nearby-text capture.",
                keywords: ["context", "screen", "spelling", "names", "accessibility"],
                view: AnyView(SettingsRow(title: "read nearby text", detail: "helps spell names and terms on screen right. off stops nearby-text capture.") {
                    SettingsToggle(settings: settings, keyPath: \.readNearbyText)
                })),
            SettingsItem(
                id: "privacy.memoryOnThisMac", page: .privacy, section: "what it keeps",
                title: "keep my memory on this mac only", detail: "history, dictionary and styles stay here. with this mac as the engine and “also polish offline takes” off, nothing leaves this mac at all.",
                keywords: ["local", "memory", "cloud", "sync", "data"],
                view: AnyView(SettingsRow(title: "keep my memory on this mac only", detail: "history, dictionary and styles stay here. with this mac as the engine and “also polish offline takes” off, nothing leaves this mac at all.") {
                    SettingsToggle(settings: settings, keyPath: \.keepMemoryOnThisMac)
                })),
            SettingsItem(
                id: "privacy.incognito", page: .privacy, section: "what it keeps",
                title: "incognito", detail: "takes still paste, but nothing is saved to history.",
                keywords: ["private", "history", "don't save"],
                view: AnyView(SettingsRow(title: "incognito", detail: "takes still paste, but nothing is saved to history.") {
                    SettingsToggle(settings: settings, keyPath: \.incognito)
                })),
            SettingsItem(
                id: "privacy.clipboardHistory", page: .privacy, section: "what it keeps",
                title: "clipboard history", detail: "capture what you copy, so it shows in history too.",
                keywords: ["clipboard", "copy", "pasteboard"],
                view: AnyView(SettingsRow(title: "clipboard history", detail: "capture what you copy, so it shows in history too.") {
                    SettingsToggle(settings: settings, keyPath: \.clipboardHistoryEnabled)
                })),
            SettingsItem(
                id: "privacy.retryFailedTakes", page: .privacy, section: "what it keeps",
                title: "retry failed takes", detail: "keeps the recording of a take a network engine couldn’t finish (up to 100 takes or 1 GB) until you retry or delete it. off removes them now.",
                keywords: ["retry", "audio", "recording", "failed", "network"],
                view: AnyView(RetryFailedTakesRow(settings: settings, companionManager: companionManager))),
            SettingsItem(
                id: "privacy.deleteTakes", page: .privacy, section: "what it keeps",
                title: "delete my takes", detail: "wipes every take from this mac now. your dictionary and shortcuts stay.",
                keywords: ["delete", "wipe", "history", "erase", "data"],
                view: AnyView(DeleteTakesRow(companionManager: companionManager))),
            SettingsItem(
                id: "privacy.whereHistory", page: .privacy, section: "where things live",
                title: "history", detail: TakeStore.defaultFileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"),
                keywords: ["sqlite", "database", "file", "folder", "takes"],
                view: AnyView(SettingsRow(title: "history", detail: TakeStore.defaultFileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                    Button("show") { NSWorkspace.shared.activateFileViewerSelecting([TakeStore.defaultFileURL]) }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "privacy.whereSpace", page: .privacy, section: "where things live",
                title: "styles, dictionary, shortcuts", detail: DictationSpaceStore.defaultDirectoryURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"),
                keywords: ["json", "file", "folder"],
                view: AnyView(SettingsRow(title: "styles, dictionary, shortcuts", detail: DictationSpaceStore.defaultDirectoryURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                    Button("show") { NSWorkspace.shared.open(DictationSpaceStore.defaultDirectoryURL) }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "privacy.whereKeys", page: .privacy, section: "where things live",
                title: "keys and account", detail: "~/.openclicky/shell.json",
                keywords: ["shell.json", "token", "file", "key"],
                view: AnyView(SettingsRow(title: "keys and account", detail: "~/.openclicky/shell.json") {
                    Button("show") { OpenClickyConfiguration.revealSettingsFile() }.buttonStyle(PaperPillButtonStyle())
                })),
            SettingsItem(
                id: "privacy.summary", page: .privacy, section: "where things live",
                title: "privacy summary", detail: "with this mac as the engine nothing leaves it unless you turn on “also polish offline takes”. the other engines send audio to the provider you chose, with your key or account.",
                keywords: ["policy", "privacy", "what leaves", "readme"],
                chrome: .bare,
                view: AnyView(PrivacySummaryNote())),
        ]
    }
}

// MARK: - permissions

private enum PrivacyPermission {
    case microphone, accessibility, speechRecognition

    var title: String {
        switch self {
        case .microphone: return "microphone"
        case .accessibility: return "accessibility"
        case .speechRecognition: return "speech recognition"
        }
    }

    var detail: String {
        switch self {
        case .microphone: return "openclicky listens only when you start a take. with this mac as the engine, audio never leaves it."
        case .accessibility: return "lets openclicky paste takes into other apps and read nearby text."
        case .speechRecognition: return "apple’s on-device recogniser, for this mac as the engine."
        }
    }

    var isGranted: Bool {
        switch self {
        case .microphone: return AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        case .accessibility: return AXIsProcessTrusted()
        case .speechRecognition: return SFSpeechRecognizer.authorizationStatus() == .authorized
        }
    }

    func request() {
        switch self {
        case .microphone:
            if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                AVCaptureDevice.requestAccess(for: .audio) { _ in }
            } else if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
                NSWorkspace.shared.open(url)
            }
        case .accessibility:
            WindowPositionManager.requestAccessibilityPermission()
        case .speechRecognition:
            if SFSpeechRecognizer.authorizationStatus() == .notDetermined {
                SFSpeechRecognizer.requestAuthorization { _ in }
            } else if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_SpeechRecognition") {
                NSWorkspace.shared.open(url)
            }
        }
    }
}

/// A permission's live state, polled like the island's permission cards. Accessibility takes
/// effect without a relaunch here (the event tap restarts on the next poll), so "granted" is
/// the true state as soon as macOS says so.
private struct PermissionRow: View {
    let permission: PrivacyPermission
    @State private var isGranted: Bool
    private let pollTimer = Timer.publish(every: 1.5, on: .main, in: .common).autoconnect()

    init(permission: PrivacyPermission) {
        self.permission = permission
        _isGranted = State(initialValue: permission.isGranted)
    }

    private var detail: String {
        if permission == .accessibility && !isGranted {
            return permission.detail + " can’t see openclicky in the list? the island’s permission card can drag it in for you, and reset a stale row."
        }
        return permission.detail
    }

    var body: some View {
        SettingsRow(title: permission.title, detail: detail) {
            if isGranted {
                HStack(spacing: 4) {
                    Image(systemName: "checkmark").font(.system(size: 10, weight: .bold))
                    Text("granted").font(Paper.body(12))
                }
                .foregroundStyle(Paper.success)
                .accessibilityElement(children: .combine)
            } else {
                Button("grant") { permission.request() }.buttonStyle(PaperPillButtonStyle(prominent: true))
            }
        }
        .onReceive(pollTimer) { _ in isGranted = permission.isGranted }
    }
}

// MARK: - what it keeps

private struct RetryFailedTakesRow: View {
    @ObservedObject var settings: DictationSettings
    let companionManager: CompanionManager

    var body: some View {
        SettingsRow(title: "retry failed takes", detail: "keeps the recording of a take a network engine couldn’t finish (up to 100 takes or 1 GB) until you retry or delete it. off removes them now.") {
            PaperToggle(isOn: Binding(get: { settings.retainFailedTakeAudio }, set: { isOn in
                settings.retainFailedTakeAudio = isOn
                if !isOn { companionManager.dictationTakeController.audioStore.discardAll() }
            }))
        }
    }
}

private struct DeleteTakesRow: View {
    let companionManager: CompanionManager
    @State private var isConfirmingDelete = false

    var body: some View {
        SettingsRow(title: "delete my takes", detail: "wipes every take from this mac now. your dictionary and shortcuts stay.") {
            Button("delete history…") { isConfirmingDelete = true }.buttonStyle(PaperPillButtonStyle(destructive: true))
        }
        .confirmationDialog("delete every take on this mac?", isPresented: $isConfirmingDelete) {
            Button("delete all history", role: .destructive) {
                try? companionManager.dictationTakeStore?.deleteAll()
                companionManager.dictationTakeController.audioStore.discardAll()
                companionManager.dictationTakeController.historyDidChange()
            }
        } message: { Text("this can't be undone.") }
    }
}

private struct PrivacySummaryNote: View {
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(settingsHighlighted("with this mac as the engine nothing leaves it unless you turn on “also polish offline takes”. the other engines send audio to the provider you chose, with your key or account.", query: searchQuery))
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 4) {
                Text("the full privacy policy is in the project’s")
                Button("README ↗") { openExternalLink("https://github.com/prasanthsasikumar/openclicky#readme") }
                    .buttonStyle(.plain).foregroundStyle(Paper.accentFill).pointerCursor()
            }
        }
        .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
        .padding(.top, 8).padding(.leading, 2)
    }
}
