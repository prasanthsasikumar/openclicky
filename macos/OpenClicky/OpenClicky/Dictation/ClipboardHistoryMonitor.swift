//
//  ClipboardHistoryMonitor.swift
//  OpenClicky
//
//  Opt-in clipboard history: what you copy shows up in history beside what you dictated. The
//  pasteboard has no change notification, so its change count is polled; only plain text is
//  kept, never from password managers' transient types, and never while incognito is on.
//

import AppKit
import Combine
import Foundation

@MainActor
final class ClipboardHistoryMonitor {
    private let settings: DictationSettings
    private let takeStore: TakeStore?
    private let onRecorded: () -> Void
    private var timer: Timer?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private var cancellable: AnyCancellable?
    private static let maxCharacters = 20_000
    /// Password managers mark transient copies with these types; they are never recorded.
    private static let transientTypes: [NSPasteboard.PasteboardType] = [
        NSPasteboard.PasteboardType("org.nspasteboard.TransientType"),
        NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"),
        NSPasteboard.PasteboardType("com.agilebits.onepassword"),
    ]

    init(settings: DictationSettings, takeStore: TakeStore?, onRecorded: @escaping () -> Void) {
        self.settings = settings
        self.takeStore = takeStore
        self.onRecorded = onRecorded
    }

    func start() {
        cancellable = settings.$clipboardHistoryEnabled.sink { [weak self] enabled in
            enabled ? self?.startPolling() : self?.stopPolling()
        }
    }

    func stop() {
        stopPolling()
        cancellable = nil
    }

    private func startPolling() {
        guard timer == nil else { return }
        lastChangeCount = NSPasteboard.general.changeCount
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.poll() }
        }
    }

    private func stopPolling() {
        timer?.invalidate()
        timer = nil
    }

    private func poll() {
        let pasteboard = NSPasteboard.general
        guard pasteboard.changeCount != lastChangeCount else { return }
        lastChangeCount = pasteboard.changeCount
        guard !settings.incognito, let takeStore else { return }
        // A take's paste changes the pasteboard twice (the words, then the restore): neither is a copy.
        if Date().timeIntervalSince(FrontAppTextInserter.lastInsertedAt) < 1.5 { return }
        let types = pasteboard.types ?? []
        guard !types.contains(where: { Self.transientTypes.contains($0) }) else { return }
        guard let text = pasteboard.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return }
        // A take's own paste is already in history as the take.
        if let last = try? takeStore.recent(limit: 1).first, last.displayText == text { return }
        let record = TakeRecord(mode: .clipboard, rawText: "", formattedText: String(text.prefix(Self.maxCharacters)),
                                appBundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
                                appName: NSWorkspace.shared.frontmostApplication?.localizedName, engine: "clipboard")
        try? takeStore.insert(record)
        onRecorded()
    }
}
