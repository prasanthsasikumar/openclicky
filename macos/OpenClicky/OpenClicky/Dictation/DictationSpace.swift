//
//  DictationSpace.swift
//  OpenClicky
//
//  "Your space": the styles each app is written in, the dictionary of names spelled your way, and
//  the spoken shortcuts that expand into saved text. Three JSON files under ~/.openclicky/dictation,
//  readable and editable by hand, watched for changes made outside the app.
//

import Combine
import Foundation

/// How a take is written for a group of apps.
struct DictationStyle: Codable, Identifiable, Equatable {
    var id: String
    var name: String
    var tagline: String
    /// Free-text rules for the model ("terse. keep code as code.").
    var rules: String
    /// Start sentences with a capital letter and end them with a full stop (the local pass).
    var sentenceCase: Bool
    /// Drop "um", "uh" and friends (the local pass).
    var removeFillers: Bool
    /// Let a configured model rewrite the take with the rules above.
    var polishWithModel: Bool
    /// Bundle ids of the apps written in this style.
    var appBundleIDs: [String]

    static func seeded() -> [DictationStyle] {
        [
            DictationStyle(
                id: "developer", name: "developer", tagline: "terse, and your code stays code",
                rules: "Terse. Keep identifiers, file names, commands and code exactly as spoken; never expand or translate them. No pleasantries. Sentence case.",
                sentenceCase: true, removeFillers: true, polishWithModel: true,
                appBundleIDs: [
                    "com.apple.dt.Xcode", "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92", "com.apple.Terminal",
                    "com.googlecode.iterm2", "com.anthropic.claudefordesktop", "com.openai.codex", "com.github.GitHubClient",
                    "com.google.android.studio", "dev.zed.Zed", "dev.warp.Warp-Stable", "com.jetbrains.intellij",
                ]),
            DictationStyle(
                id: "work-messaging", name: "work messaging", tagline: "clear, quick, and work-ready",
                rules: "Clear and quick, as a colleague writes in a chat: short sentences, friendly, no filler, no sign-off. Keep it one message.",
                sentenceCase: true, removeFillers: true, polishWithModel: true,
                appBundleIDs: ["com.tinyspeck.slackmacgap", "com.microsoft.teams2", "com.microsoft.teams", "com.hnc.Discord", "us.zoom.xos", "com.linear", "notion.id"]),
            DictationStyle(
                id: "personal-messaging", name: "personal messaging", tagline: "lowercase, shorthand, zero fuss",
                rules: "Casual, lowercase, the way people text: shorthand kept (idk, lemme, ngl), light punctuation, warmth kept, nothing added.",
                sentenceCase: false, removeFillers: true, polishWithModel: true,
                appBundleIDs: ["com.apple.MobileSMS", "net.whatsapp.WhatsApp", "org.telegram.desktop", "ru.keepcoder.Telegram", "com.facebook.archon"]),
            DictationStyle(
                id: "email", name: "email", tagline: "composed, complete sentences",
                rules: "Composed, complete sentences with full punctuation and grammar; contractions are fine. Paragraphs where the thought changes. Never invent a greeting or a sign-off that was not spoken.",
                sentenceCase: true, removeFillers: true, polishWithModel: true,
                appBundleIDs: ["com.apple.mail", "com.microsoft.Outlook", "com.google.Chrome", "com.apple.Safari", "company.thebrowser.Browser", "com.readdle.smartemail-Mac"]),
            DictationStyle(
                id: "other", name: "other apps", tagline: "clean sentences, your voice kept",
                rules: "Light cleanup only: punctuation, capitalisation, fillers removed, your wording kept.",
                sentenceCase: true, removeFillers: true, polishWithModel: true,
                appBundleIDs: []),
        ]
    }
}

/// "you say X → OpenClicky writes Y": a name or term spelled the way you want it.
struct DictionaryTerm: Codable, Identifiable, Equatable {
    var id: UUID
    /// What it should be written as.
    var written: String
    /// Ways it tends to be heard ("aditya shatriya"). Empty = match the written form itself.
    var heardAs: [String]
    var createdAt: Date

    init(id: UUID = UUID(), written: String, heardAs: [String] = [], createdAt: Date = Date()) {
        self.id = id
        self.written = written
        self.heardAs = heardAs
        self.createdAt = createdAt
    }
}

/// "you say my sign-off → OpenClicky writes the saved text": expands only when the whole take is
/// the trigger.
struct SpokenShortcut: Codable, Identifiable, Equatable {
    var id: UUID
    var trigger: String
    var replacement: String
    var createdAt: Date

    init(id: UUID = UUID(), trigger: String, replacement: String, createdAt: Date = Date()) {
        self.id = id
        self.trigger = trigger
        self.replacement = replacement
        self.createdAt = createdAt
    }
}

/// The three files, loaded together.
struct DictationSpace: Equatable {
    var styles: [DictationStyle]
    var dictionary: [DictionaryTerm]
    var shortcuts: [SpokenShortcut]

    static let empty = DictationSpace(styles: DictationStyle.seeded(), dictionary: [], shortcuts: [])

    /// The style for an app, by bundle id; the one with no apps (the "other apps" style) when none claims it.
    func style(forAppBundleID bundleID: String?) -> DictationStyle {
        if let bundleID, let claimed = styles.first(where: { $0.appBundleIDs.contains(bundleID) }) { return claimed }
        return styles.first { $0.appBundleIDs.isEmpty } ?? styles.first ?? DictationStyle.seeded()[4]
    }

    /// Moves an app to a style, removing it from whichever style had it.
    mutating func assign(appBundleID: String, toStyleID styleID: String) {
        for index in styles.indices {
            styles[index].appBundleIDs.removeAll { $0 == appBundleID }
            if styles[index].id == styleID { styles[index].appBundleIDs.append(appBundleID) }
        }
    }
}

/// Reads and writes the space under ~/.openclicky/dictation, and reloads when the files change.
@MainActor
final class DictationSpaceStore: ObservableObject {
    @Published private(set) var space: DictationSpace

    let directoryURL: URL
    private var directoryWatcher: DispatchSourceFileSystemObject?
    private var pendingReload: DispatchWorkItem?
    private var lastWriteFingerprint = ""

    static let defaultDirectoryURL = URL(fileURLWithPath: NSString(string: "~/.openclicky/dictation").expandingTildeInPath)

    init(directoryURL: URL = DictationSpaceStore.defaultDirectoryURL) {
        self.directoryURL = directoryURL
        self.space = Self.load(from: directoryURL)
    }

    private var stylesURL: URL { directoryURL.appendingPathComponent("styles.json") }
    private var dictionaryURL: URL { directoryURL.appendingPathComponent("dictionary.json") }
    private var shortcutsURL: URL { directoryURL.appendingPathComponent("shortcuts.json") }

    private static func load(from directoryURL: URL) -> DictationSpace {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        func read<T: Decodable>(_ name: String, as type: T.Type) -> T? {
            guard let data = try? Data(contentsOf: directoryURL.appendingPathComponent(name)) else { return nil }
            return try? decoder.decode(type, from: data)
        }
        var styles = read("styles.json", as: [DictationStyle].self) ?? DictationStyle.seeded()
        if styles.isEmpty { styles = DictationStyle.seeded() }
        return DictationSpace(
            styles: styles,
            dictionary: read("dictionary.json", as: [DictionaryTerm].self) ?? [],
            shortcuts: read("shortcuts.json", as: [SpokenShortcut].self) ?? [])
    }

    func reload() {
        space = Self.load(from: directoryURL)
    }

    /// Changes the space and writes every file (small files, written whole).
    func update(_ change: (inout DictationSpace) -> Void) {
        var changed = space
        change(&changed)
        guard changed != space else { return }
        space = changed
        save()
    }

    func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .secondsSince1970
        do {
            try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try encoder.encode(space.styles).write(to: stylesURL, options: .atomic)
            try encoder.encode(space.dictionary).write(to: dictionaryURL, options: .atomic)
            try encoder.encode(space.shortcuts).write(to: shortcutsURL, options: .atomic)
            lastWriteFingerprint = Self.fingerprint(of: space)
        } catch {
            AppLog.append("dictation space: could not write \(directoryURL.path): \(error.localizedDescription)")
        }
    }

    /// Picks up edits made by hand while the app runs. The directory is watched, not the files: an
    /// editor's atomic save replaces a file, and a watch on the old one goes quiet.
    func startWatching() {
        guard directoryWatcher == nil else { return }
        try? FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let descriptor = open(directoryURL.path, O_EVTONLY)
        guard descriptor >= 0 else { return }
        let watcher = DispatchSource.makeFileSystemObjectSource(fileDescriptor: descriptor, eventMask: [.write, .rename, .delete], queue: .main)
        watcher.setEventHandler { [weak self] in
            self?.pendingReload?.cancel()
            let work = DispatchWorkItem { [weak self] in
                guard let self else { return }
                let reloaded = Self.load(from: self.directoryURL)
                // The app's own write arrives here too; only an outside change replaces the model.
                guard Self.fingerprint(of: reloaded) != self.lastWriteFingerprint || reloaded != self.space else { return }
                self.space = reloaded
            }
            self?.pendingReload = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
        }
        watcher.setCancelHandler { close(descriptor) }
        watcher.resume()
        directoryWatcher = watcher
    }

    private static func fingerprint(of space: DictationSpace) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .secondsSince1970
        let parts = [try? encoder.encode(space.styles), try? encoder.encode(space.dictionary), try? encoder.encode(space.shortcuts)]
        return parts.map { $0.map { String(decoding: $0, as: UTF8.self) } ?? "" }.joined(separator: "|")
    }
}
