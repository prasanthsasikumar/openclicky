//
//  SettingsPrivacyPage.swift
//  OpenClicky
//
//  Settings → Privacy & Security: what OpenClicky can use, and what it keeps. The three
//  permissions with their live state, reading nearby text, what is kept on this Mac and for how
//  long, where each file lives, and (under them) what leaves the Mac.
//

import AppKit
import AVFoundation
import Combine
import Speech
import SwiftUI

extension SettingsCatalog {
    func privacyItems() -> [SettingsItem] {
        func path(_ url: URL) -> String { url.path.replacingOccurrences(of: NSHomeDirectory(), with: "~") }
        return [
            SettingsItem(
                id: "privacy.microphone",
                title: "Microphone", detail: PrivacyPermission.microphone.detail,
                keywords: ["permission", "mic", "audio", "grant"],
                view: AnyView(PermissionRow(permission: .microphone))),
            SettingsItem(
                id: "privacy.accessibility",
                title: "Accessibility", detail: PrivacyPermission.accessibility.detail,
                keywords: ["permission", "paste", "grant", "keys", "event tap", "terminal", "live typing"],
                view: AnyView(PermissionRow(permission: .accessibility))),
            SettingsItem(
                id: "privacy.speechRecognition",
                title: "Speech recognition", detail: PrivacyPermission.speechRecognition.detail,
                keywords: ["permission", "offline", "apple", "grant", "siri"],
                view: AnyView(PermissionRow(permission: .speechRecognition))),
            SettingsItem(
                id: "privacy.readNearbyText",
                title: "Read nearby text", detail: "Helps spell names and terms on screen right.",
                keywords: ["context", "screen", "spelling", "names", "accessibility"],
                view: AnyView(SettingsRow(title: "Read nearby text", detail: "Helps spell names and terms on screen right.") {
                    SettingsToggle(settings: settings, keyPath: \.readNearbyText)
                })),
            SettingsItem(
                id: "privacy.pauseBeforeSendingScreen",
                title: "Pause before sending my screen", detail: "A moment to press esc before Clicky sends your screen.",
                keywords: ["screenshot", "screen", "cancel", "esc", "send", "wait", "consent"],
                view: AnyView(SettingsRow(title: "Pause before sending my screen", detail: "A moment to press esc before Clicky sends your screen.") {
                    SettingsToggle(settings: settings, keyPath: \.pauseBeforeSendingScreen)
                })),
            SettingsItem(
                id: "privacy.memoryOnThisMac",
                title: "Keep my memory on this Mac only", detail: "History, dictionary and styles stay here. Questions to Clicky still go online.",
                keywords: ["local", "memory", "cloud", "sync", "data"],
                view: AnyView(SettingsRow(title: "Keep my memory on this Mac only", detail: "History, dictionary and styles stay here. Questions to Clicky still go online.") {
                    SettingsToggle(settings: settings, keyPath: \.keepMemoryOnThisMac)
                })),
            SettingsItem(
                id: "privacy.incognito",
                title: "Incognito", detail: "Takes still paste, but nothing is saved to history.",
                keywords: ["private", "history", "don't save"],
                view: AnyView(SettingsRow(title: "Incognito", detail: "Takes still paste, but nothing is saved to history.") {
                    SettingsToggle(settings: settings, keyPath: \.incognito)
                })),
            SettingsItem(
                id: "privacy.clipboardHistory",
                title: "Clipboard history", detail: "Capture what you copy, so it shows in history too.",
                keywords: ["clipboard", "copy", "pasteboard"],
                view: AnyView(SettingsRow(title: "Clipboard history", detail: "Capture what you copy, so it shows in history too.") {
                    SettingsToggle(settings: settings, keyPath: \.clipboardHistoryEnabled)
                })),
            SettingsItem(
                id: "privacy.retryFailedTakes",
                title: "Keep failed takes to retry", detail: "Up to 100 takes or 1 GB. Off removes them now.",
                keywords: ["retry", "audio", "recording", "failed", "network"],
                view: AnyView(RetryFailedTakesRow(settings: settings, companionManager: companionManager))),
            SettingsItem(
                id: "privacy.deleteTakes",
                title: "Delete all takes", detail: "Your dictionary and snippets stay.",
                keywords: ["delete", "wipe", "history", "erase", "data"],
                view: AnyView(DeleteTakesRow(companionManager: companionManager))),
            SettingsItem(
                id: "privacy.whereHistory",
                title: "History", detail: path(TakeStore.defaultFileURL),
                keywords: ["sqlite", "database", "file", "folder", "takes"],
                view: AnyView(SettingsRow(title: "History", detail: path(TakeStore.defaultFileURL)) {
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([TakeStore.defaultFileURL]) }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "privacy.whereSpace",
                title: "Styles, dictionary, snippets", detail: path(DictationSpaceStore.defaultDirectoryURL),
                keywords: ["json", "file", "folder", "shortcuts"],
                view: AnyView(SettingsRow(title: "Styles, dictionary, snippets", detail: path(DictationSpaceStore.defaultDirectoryURL)) {
                    Button("Show in Finder") { NSWorkspace.shared.open(DictationSpaceStore.defaultDirectoryURL) }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "privacy.whereKeys",
                title: "Keys and account", detail: "~/.openclicky/shell.json",
                keywords: ["shell.json", "token", "file", "key"],
                view: AnyView(SettingsRow(title: "Keys and account", detail: "~/.openclicky/shell.json") {
                    Button("Show in Finder") { OpenClickyConfiguration.revealSettingsFile() }
                        .pointerCursor()
                })),
            SettingsItem(
                id: "privacy.summary",
                title: "Privacy summary", detail: PrivacySummaryNote.text,
                keywords: ["policy", "privacy", "what leaves", "readme"],
                chrome: .footer,
                view: AnyView(PrivacySummaryNote())),
        ]
    }
}

// MARK: - permissions

private enum PrivacyPermission {
    case microphone, accessibility, speechRecognition

    var title: String {
        switch self {
        case .microphone: return "Microphone"
        case .accessibility: return "Accessibility"
        case .speechRecognition: return "Speech recognition"
        }
    }

    var detail: String {
        switch self {
        case .microphone: return "Listens only during a take. With This Mac as the engine, audio never leaves it."
        case .accessibility: return "Pastes into other apps and reads nearby text. Terminals get the words when you let go."
        case .speechRecognition: return "Apple’s on-device recogniser, for This Mac."
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
/// effect without a relaunch here (the event tap restarts on the next poll), so "Allowed" is
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
            return permission.detail + " Can’t see OpenClicky in the list? The island’s permission card can drag it in for you, and reset a stale row."
        }
        return permission.detail
    }

    var body: some View {
        SettingsRow(title: permission.title, detail: detail) {
            if isGranted {
                HStack(spacing: 6) {
                    Circle().fill(SettingsLook.success).frame(width: 8, height: 8)
                    Text("Allowed").font(SettingsLook.rowTitle).foregroundStyle(.secondary)
                }
                .accessibilityElement(children: .combine)
            } else {
                Button("Allow…") { permission.request() }
                    .buttonStyle(.borderedProminent)
                    .tint(SettingsLook.accent)
                    .pointerCursor()
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
        SettingsRow(title: "Keep failed takes to retry", detail: "Up to 100 takes or 1 GB. Off removes them now.") {
            SettingsSwitch(isOn: Binding(get: { settings.retainFailedTakeAudio }, set: { isOn in
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
        SettingsRow(title: "Delete all takes", detail: "Your dictionary and snippets stay.") {
            Button("Delete History…", role: .destructive) { isConfirmingDelete = true }
                .foregroundStyle(SettingsLook.danger)
                .pointerCursor()
        }
        .confirmationDialog("Delete every take on this Mac?", isPresented: $isConfirmingDelete) {
            Button("Delete All History", role: .destructive) {
                try? companionManager.dictationTakeStore?.deleteAll()
                companionManager.dictationTakeController.audioStore.discardAll()
                companionManager.dictationTakeController.historyDidChange()
            }
        } message: { Text("This can’t be undone.") }
    }
}

private struct PrivacySummaryNote: View {
    static let text = "With This Mac as the engine, what you dictate stays on this Mac unless AI clean-up is on (edits need it too). The other engines send audio to the provider you chose. Questions to Clicky always go online with a picture of your screen, and fast voice and hands-free send your voice to OpenAI."
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(settingsHighlighted(Self.text, query: searchQuery))
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 3) {
                Text("The full privacy policy is in the project’s")
                Button("README") { openExternalLink("https://github.com/prasanthsasikumar/openclicky#readme") }
                    .buttonStyle(.link)
                    .pointerCursor()
                Text(".")
            }
        }
        .font(SettingsLook.caption).foregroundStyle(.secondary)
    }
}
