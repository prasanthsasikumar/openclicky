//
//  SkillLibraryStore.swift
//  OpenClicky
//
//  The user's skill library as the notch HUD and the talk lanes see it. Reads and writes the same
//  files as the CLI (`agent/src/skillsLibrary.ts`):
//
//    <userSkillsDirectory>/library/<id>/SKILL.md   one folder per skill (created here or dropped in by hand)
//    <userSkillsDirectory>/activations.json        { "active": [ids], "updatedAt": ISO-8601 }
//    <userSkillsDirectory>/active/<id> → library/<id>   symlinks for activated skills only; Codex loads this dir
//
//  App-teaching skills are read-only here and matched by frontmost app: the ones inside the app
//  (`Contents/Resources/app-skills`), plus a local folder (a checkout's `app-skills/` or
//  `~/.openclicky/app-skills`) whose skills add to them and win over a bundled one with the same id.
//

import Combine
import Foundation

@MainActor
final class SkillLibraryStore: ObservableObject {
    @Published private(set) var librarySkills: [SkillFile] = []
    @Published private(set) var activeIds: Set<String> = []
    @Published private(set) var appSkills: [SkillFile] = []
    @Published var lastError: String?

    let userSkillsDirectory: URL
    let appSkillsDirectory: URL
    let bundledAppSkillsDirectory: URL?

    /// Watches `library/` (skills added or removed) and the root (activations.json rewritten by the CLI).
    private var watchers: [DispatchSourceFileSystemObject] = []
    private var reloadDebounce: DispatchWorkItem?

    var libraryDirectory: URL { userSkillsDirectory.appendingPathComponent("library", isDirectory: true) }
    var activeDirectory: URL { userSkillsDirectory.appendingPathComponent("active", isDirectory: true) }
    var activationsURL: URL { userSkillsDirectory.appendingPathComponent("activations.json") }

    /// Activated skills that apply to the voice / teacher prompts.
    var activeTalkSkills: [SkillFile] {
        librarySkills.filter { activeIds.contains($0.id) && $0.isForTalk }
    }

    convenience init() {
        self.init(userSkillsDirectory: OpenClickyConfiguration.userSkillsDirectory,
                  appSkillsDirectory: OpenClickyConfiguration.appSkillsDirectory,
                  bundledAppSkillsDirectory: OpenClickyConfiguration.bundledAppSkillsDirectory)
    }

    init(userSkillsDirectory: URL, appSkillsDirectory: URL, bundledAppSkillsDirectory: URL? = nil, watch: Bool = true) {
        self.userSkillsDirectory = userSkillsDirectory
        self.appSkillsDirectory = appSkillsDirectory
        self.bundledAppSkillsDirectory = bundledAppSkillsDirectory
        ensureDirectories()
        reload()
        if watch { startWatching() }
    }

    deinit {
        watchers.forEach { $0.cancel() }
    }

    // MARK: - Reading

    /// Re-reads the library, the activations, and the app skills; drops stale `active/` links.
    /// `clearingErrors`: an explicit reload (launch, user action) drops a stale `lastError` from an
    /// earlier failure; a background reload (directory watcher, the reload after a failed save) keeps
    /// it so the HUD still shows what went wrong. `syncActiveDirectory` sets it again if linking fails.
    func reload(clearingErrors: Bool = true) {
        ensureDirectories()
        librarySkills = SkillFile.load(directory: libraryDirectory)
        let known = Set(librarySkills.map(\.id))
        activeIds = Set(readActivations().filter { known.contains($0) })
        appSkills = Self.loadAppSkills(bundledDirectory: bundledAppSkillsDirectory, localDirectory: appSkillsDirectory)
        if clearingErrors { lastError = nil }
        syncActiveDirectory()
    }

    private func readActivations() -> [String] {
        guard let data = try? Data(contentsOf: activationsURL),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let active = object["active"] as? [Any] else { return [] }
        return active.compactMap { $0 as? String }
    }

    /// The bundled skills with the local folder's laid over them: a local skill replaces the bundled
    /// one with the same folder id, so an edited copy in a checkout wins. Sorted by id, as `SkillFile.load` is,
    /// because `AppSkillMatcher` takes the first skill whose bundle id matches.
    static func loadAppSkills(bundledDirectory: URL?, localDirectory: URL) -> [SkillFile] {
        // A checkout's app-skills/ also holds "_template" for contributors: never a skill to match.
        let localSkills = SkillFile.load(directory: localDirectory).filter { !$0.id.hasPrefix("_") }
        guard let bundledDirectory, bundledDirectory.standardizedFileURL != localDirectory.standardizedFileURL else {
            return localSkills
        }
        let localSkillIds = Set(localSkills.map(\.id))
        let bundledSkillsNotOverridden = SkillFile.load(directory: bundledDirectory).filter { !localSkillIds.contains($0.id) }
        return (bundledSkillsNotOverridden + localSkills).sorted { $0.id < $1.id }
    }

    // MARK: - Activation

    func setActive(_ id: String, _ on: Bool) {
        var ids = readActivations().filter { $0 != id }
        if on { ids.append(id) }
        do {
            try writeActivations(ids)
            reload()
        } catch {
            lastError = "Could not save activations: \(error.localizedDescription)"
            reload(clearingErrors: false)
        }
    }

    private func writeActivations(_ ids: [String]) throws {
        var seen = Set<String>()
        let unique = ids.filter { seen.insert($0).inserted }
        let payload: [String: Any] = ["active": unique, "updatedAt": ISO8601DateFormatter().string(from: Date())]
        let data = try JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted, .sortedKeys])
        try (String(decoding: data, as: UTF8.self) + "\n").write(to: activationsURL, atomically: true, encoding: .utf8)
    }

    /// `active/` holds exactly one symlink per activated existing skill (mirrors `syncActiveDir` in the CLI).
    private func syncActiveDirectory() {
        let fileManager = FileManager.default
        let wanted = librarySkills.map(\.id).filter { activeIds.contains($0) }
        let existing = (try? fileManager.contentsOfDirectory(atPath: activeDirectory.path)) ?? []
        for entry in existing where !wanted.contains(entry) {
            try? fileManager.removeItem(at: activeDirectory.appendingPathComponent(entry))
        }
        for id in wanted {
            let link = activeDirectory.appendingPathComponent(id)
            let target = libraryDirectory.appendingPathComponent(id, isDirectory: true)
            if let current = try? fileManager.destinationOfSymbolicLink(atPath: link.path), current == target.path { continue }
            try? fileManager.removeItem(at: link)
            do {
                try fileManager.createSymbolicLink(atPath: link.path, withDestinationPath: target.path)
            } catch let error as NSError where Self.isAlreadyExists(error) {
                continue // another writer (the CLI) linked it first
            } catch {
                lastError = "Could not link \(id) into active/: \(error.localizedDescription)"
            }
        }
    }

    // MARK: - Removing

    /// Deletes a library skill: its activation, its `active/` link and its folder.
    func removeSkill(_ id: String) {
        let ids = readActivations().filter { $0 != id }
        do {
            try writeActivations(ids)
            try? FileManager.default.removeItem(at: activeDirectory.appendingPathComponent(id))
            let folder = libraryDirectory.appendingPathComponent(id, isDirectory: true)
            if FileManager.default.fileExists(atPath: folder.path) {
                try FileManager.default.removeItem(at: folder)
            }
            reload()
        } catch {
            lastError = "Could not remove \(id): \(error.localizedDescription)"
            reload(clearingErrors: false)
        }
    }

    // MARK: - Creating

    /// Writes a SKILL.md into the library under a unique id derived from its name and activates it.
    @discardableResult
    func importSkill(markdown: String) throws -> SkillFile {
        guard let parsed = SkillFile.parse(markdown, id: "pending") else {
            throw SkillLibraryError.invalidMarkdown
        }
        ensureDirectories()
        let base = Self.slugify(parsed.name)
        // Claim the id by creating its directory non-recursively: EEXIST means another writer (the CLI, a
        // hand-dropped folder) owns it, so try the next suffix. Never write SKILL.md into a folder we did not create.
        var id = base
        var suffix = 2
        var folder = libraryDirectory.appendingPathComponent(id, isDirectory: true)
        while true {
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
                break
            } catch let error as NSError where Self.isAlreadyExists(error) {
                id = "\(base)-\(suffix)"
                suffix += 1
                folder = libraryDirectory.appendingPathComponent(id, isDirectory: true)
            }
        }
        let text = markdown.hasSuffix("\n") ? markdown : markdown + "\n"
        try text.write(to: folder.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
        setActive(id, true)
        guard let skill = librarySkills.first(where: { $0.id == id }) else { throw SkillLibraryError.invalidMarkdown }
        return skill
    }

    // MARK: - Helpers

    /// `NSFileWriteFileExistsError` (Foundation) or `EEXIST` (POSIX) — the path is already taken.
    static func isAlreadyExists(_ error: NSError) -> Bool {
        if error.domain == NSCocoaErrorDomain, error.code == NSFileWriteFileExistsError { return true }
        if error.domain == NSPOSIXErrorDomain, error.code == Int(EEXIST) { return true }
        if let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError { return isAlreadyExists(underlying) }
        return false
    }

    private func ensureDirectories() {
        let fileManager = FileManager.default
        try? fileManager.createDirectory(at: libraryDirectory, withIntermediateDirectories: true)
        try? fileManager.createDirectory(at: activeDirectory, withIntermediateDirectories: true)
    }

    /// Mirrors `slugify` in agent/src/skillMarkdown.ts: lowercase, non-alphanumerics → "-", trimmed, never empty.
    static func slugify(_ name: String) -> String {
        var out = ""
        var pendingDash = false
        for scalar in name.lowercased().unicodeScalars {
            let isAlnum = (scalar >= "a" && scalar <= "z") || (scalar >= "0" && scalar <= "9")
            if isAlnum {
                if pendingDash, !out.isEmpty { out.append("-") }
                pendingDash = false
                out.unicodeScalars.append(scalar)
            } else {
                pendingDash = true
            }
        }
        return out.isEmpty ? "skill" : out
    }

    private func startWatching() {
        // library/ for skills added or removed; the root for activations.json rewritten by the CLI
        // (`openclicky skills activate|deactivate`) so the HUD toggles follow.
        for directory in [libraryDirectory, userSkillsDirectory] {
            let descriptor = open(directory.path, O_EVTONLY)
            guard descriptor >= 0 else { continue }
            // Events are delivered on the main queue because reload() mutates @Published state on the main actor.
            let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: descriptor, eventMask: [.write, .rename, .delete], queue: .main)
            source.setEventHandler { [weak self] in self?.scheduleReload() }
            source.setCancelHandler { close(descriptor) }
            source.resume()
            watchers.append(source)
        }
    }

    /// One reload per burst of file events (both watchers share the 300 ms debounce).
    private func scheduleReload() {
        reloadDebounce?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.reload(clearingErrors: false) }
        reloadDebounce = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
    }
}

enum SkillLibraryError: LocalizedError {
    case invalidMarkdown

    var errorDescription: String? {
        switch self {
        case .invalidMarkdown: return "The skill file needs frontmatter with a name and a description."
        }
    }
}
