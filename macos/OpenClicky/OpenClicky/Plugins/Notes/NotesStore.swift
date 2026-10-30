//
//  NotesStore.swift
//  OpenClicky
//
//  The Notes plugin's text: one plain-text file on this Mac, read once and written a moment
//  after typing pauses, so a keystroke never waits on the disk.
//

import AppKit
import Combine
import Foundation

@MainActor
final class NotesStore: ObservableObject {
    static let shared = NotesStore(fileURL: NotesStore.defaultFileURL)

    /// ~/Library/Application Support/OpenClicky/Notes/notes.txt
    nonisolated static var defaultFileURL: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return support.appendingPathComponent("OpenClicky/Notes/notes.txt")
    }

    @Published private(set) var text: String

    let fileURL: URL
    private let saveDelay: Duration
    private var pendingSave: Task<Void, Never>?
    private var terminationObserver: NSObjectProtocol?
    /// Typed since the last write; nothing is written (no empty file is made) until something is.
    private var isDirty = false

    init(fileURL: URL, saveDelay: Duration = .milliseconds(600)) {
        self.fileURL = fileURL
        self.saveDelay = saveDelay
        self.text = (try? String(contentsOf: fileURL, encoding: .utf8)) ?? ""
        // The last few keystrokes before quitting are still waiting on the delay: write them now.
        terminationObserver = NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.saveNow() }
        }
    }

    /// The editor's text changed: keep it and write it once typing pauses.
    func update(_ newText: String) {
        guard newText != text else { return }
        text = newText
        isDirty = true
        pendingSave?.cancel()
        let delay = saveDelay
        pendingSave = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            self?.saveNow()
        }
    }

    /// Writes the text now (the delay's end, quitting, or the editor going away).
    func saveNow() {
        pendingSave?.cancel()
        pendingSave = nil
        guard isDirty else { return }
        isDirty = false
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: fileURL, atomically: true, encoding: .utf8)
        } catch {
            // Kept dirty so the next pause or quitting tries again.
            isDirty = true
            AppLog.append("notes not saved: \(error.localizedDescription)")
        }
    }

    /// True while a write is waiting for typing to pause.
    var hasPendingSave: Bool { pendingSave != nil }
}
