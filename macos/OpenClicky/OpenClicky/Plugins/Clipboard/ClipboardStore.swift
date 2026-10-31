//
//  ClipboardStore.swift
//  OpenClicky
//
//  The Clipboard plugin's one live instance. The pasteboard has no change notification, so its
//  change count is polled twice a second, but only while the plugin is switched on; switching it
//  off stops the polling and forgets the list. Nothing here is ever written to disk.
//

import AppKit
import Combine
import Foundation

/// Turns what is on a pasteboard into a `ClipboardContent`, and puts one back.
enum ClipboardPasteboard {
    /// Longer copies are kept cut to this many characters.
    static let maxTextLength = 20_000

    /// The copy on `pasteboard`, or nil for nothing worth a row (empty text, an unknown type).
    /// Files first (Finder also puts their names as text), then images, then text, which becomes
    /// a link when it is one web or mail address and nothing else.
    static func read(_ pasteboard: NSPasteboard) -> ClipboardContent? {
        let types = pasteboard.types ?? []
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            return .files(urls)
        }
        for imageType in [NSPasteboard.PasteboardType.png, .tiff] where types.contains(imageType) {
            if let data = pasteboard.data(forType: imageType) {
                let size = NSBitmapImageRep(data: data).map { CGSize(width: $0.pixelsWide, height: $0.pixelsHigh) } ?? .zero
                return .image(data, type: imageType, pixelSize: size)
            }
        }
        if let string = pasteboard.string(forType: .string) {
            let trimmed = string.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return nil }
            if let url = link(in: trimmed) { return .link(url) }
            return .text(String(string.prefix(maxTextLength)))
        }
        if let url = pasteboard.readObjects(forClasses: [NSURL.self], options: nil)?.first as? URL {
            return .link(url)
        }
        return nil
    }

    /// The text as a link when it is a single http, https or mailto address.
    static func link(in text: String) -> URL? {
        guard !text.contains(where: \.isWhitespace), let url = URL(string: text), let scheme = url.scheme?.lowercased() else { return nil }
        switch scheme {
        case "http", "https": return url.host?.isEmpty == false ? url : nil
        case "mailto": return url
        default: return nil
        }
    }

    static func write(_ content: ClipboardContent, to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        switch content {
        case .text(let text):
            pasteboard.setString(text, forType: .string)
        case .link(let url):
            pasteboard.writeObjects([url as NSURL])
            pasteboard.setString(url.absoluteString, forType: .string)
        case .files(let urls):
            pasteboard.writeObjects(urls.map { $0 as NSURL })
        case .image(let data, let type, _):
            pasteboard.setData(data, forType: type)
        }
    }
}

@MainActor
final class ClipboardStore: ObservableObject {
    static let shared = ClipboardStore()

    @Published private(set) var history = ClipboardHistory()
    private(set) var isPolling = false

    private let pasteboard: NSPasteboard
    private let defaults: UserDefaults
    private var lastChangeCount: Int
    private var timer: Timer?
    private var settingsObserver: NSObjectProtocol?

    init(pasteboard: NSPasteboard = .general, defaults: UserDefaults = .standard) {
        self.pasteboard = pasteboard
        self.defaults = defaults
        self.lastChangeCount = pasteboard.changeCount
    }

    /// Starts following the plugin's switch in Settings (once; later calls do nothing). The island
    /// calls it when it builds its components, so copies are caught from launch, not only after the
    /// list was first opened.
    func followSettings() {
        guard settingsObserver == nil else { return }
        settingsObserver = NotificationCenter.default.addObserver(forName: UserDefaults.didChangeNotification, object: defaults, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.syncWithSwitch() }
        }
        syncWithSwitch()
    }

    private func syncWithSwitch() {
        setEnabled(IslandComponentSettings(defaults: defaults).enabledPlugins.contains(IslandPluginRegistry.clipboardID))
    }

    /// On: poll from the current pasteboard (what was copied before is not news). Off: stop and
    /// forget everything kept.
    func setEnabled(_ enabled: Bool) {
        guard enabled != isPolling else { return }
        isPolling = enabled
        if enabled {
            lastChangeCount = pasteboard.changeCount
            let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
                // Scheduled on the main run loop, so the callback already runs on the main actor.
                MainActor.assumeIsolated { self?.poll() }
            }
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
        } else {
            timer?.invalidate()
            timer = nil
            history.removeAll()
        }
    }

    /// Reads the pasteboard if it changed since the last look.
    func poll() {
        guard pasteboard.changeCount != lastChangeCount else { return }
        lastChangeCount = pasteboard.changeCount
        // A dictated take is pasted through the pasteboard (the words, then whatever was there
        // before): neither is something the user copied.
        if pasteboard == .general, Date().timeIntervalSince(FrontAppTextInserter.lastInsertedAt) < 1.5 { return }
        let types = pasteboard.types ?? []
        guard !ClipboardHistory.isTransient(types), let content = ClipboardPasteboard.read(pasteboard) else { return }
        history.add(content, pasteboardTypes: types)
    }

    /// Puts a kept copy back on the pasteboard. The next poll sees it and moves it to the top.
    func copyBack(_ entry: ClipboardEntry) {
        ClipboardPasteboard.write(entry.content, to: pasteboard)
    }

    func clear() {
        history.removeAll()
    }
}
