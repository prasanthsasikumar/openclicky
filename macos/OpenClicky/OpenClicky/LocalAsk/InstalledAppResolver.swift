//
//  InstalledAppResolver.swift
//  OpenClicky
//
//  The app a person named, found among the apps installed on this Mac. The fast lane's finder
//  needs the exact "<name>.app", so "open chrome" would otherwise fail out loud with "I couldn't
//  find an app called chrome" (the builder check on the on-device design, 2026-10-27). Matching is
//  deterministic and short, never the model's guess at a product name: an alias table for the
//  things people call apps ("email"), the exact name, the spoken words as the start of the name
//  ("final cut"), initials ("vs code"), and one distinctive word of the name ("word", "chrome").
//

import AppKit
import Foundation

enum InstalledAppResolver {

    /// What people call an app, mapped to its name. "browser" is the default browser's name.
    static let aliases: [String: String] = [
        "email": "Mail", "mail": "Mail", "my email": "Mail",
        "settings": "System Settings", "system preferences": "System Settings", "preferences": "System Settings",
        "calculator": "Calculator", "notes": "Notes", "photos": "Photos", "messages": "Messages",
        "music": "Music", "calendar": "Calendar", "finder": "Finder", "terminal": "Terminal",
    ]

    static let browserAlias = "browser"

    /// A word of three letters or fewer never resolves by itself ("pro" would pick Final Cut Pro).
    static let minimumDistinctiveWordLength = 4

    /// Pure: the installed app's name for what was spoken, or nil. `installedAppNames` are the apps'
    /// display names without ".app".
    nonisolated static func resolve(spoken: String, installedAppNames: [String], defaultBrowserName: String?) -> String? {
        let spokenWords = normalizedWords(spoken)
        guard !spokenWords.isEmpty else { return nil }
        let spokenPhrase = spokenWords.joined(separator: " ")

        if spokenPhrase == browserAlias || spokenPhrase == "web browser" { return defaultBrowserName }
        if let aliased = aliases[spokenPhrase] {
            return installedAppNames.first { $0.caseInsensitiveCompare(aliased) == .orderedSame } ?? aliased
        }
        if let exact = installedAppNames.first(where: { $0.lowercased() == spokenPhrase }) { return exact }

        // The spoken words as the start of the name, each a prefix of the next word of the name or the
        // initials of a run of its words: "final cut" → Final Cut Pro, "vs code" → Visual Studio Code.
        let startMatches = installedAppNames.filter { wordsOpen(nameWords: normalizedWords($0), spokenWords: spokenWords) }
        if startMatches.count == 1 { return startMatches[0] }
        if startMatches.count > 1 { return startMatches.min(by: { $0.count < $1.count }) }

        // One distinctive word of exactly one app's name: "chrome", "word".
        if spokenWords.count == 1, spokenWords[0].count >= minimumDistinctiveWordLength {
            let containing = installedAppNames.filter { normalizedWords($0).contains(spokenWords[0]) }
            if containing.count == 1 { return containing[0] }
        }
        return nil
    }

    /// Lowercased words, with the articles and verbs people wrap a name in removed ("the", "my", "app").
    private nonisolated static func normalizedWords(_ text: String) -> [String] {
        var words = text.lowercased()
            .split(whereSeparator: { !$0.isLetter && !$0.isNumber })
            .map(String.init)
        while let first = words.first, ["the", "my", "open", "launch", "start", "up"].contains(first) { words.removeFirst() }
        while let last = words.last, ["app", "application", "please"].contains(last) { words.removeLast() }
        return words
    }

    /// Whether `spokenWords` match the beginning of `nameWords`, word by word, each spoken word being
    /// a prefix of the name's next word or the initials of a run of them.
    private nonisolated static func wordsOpen(nameWords: [String], spokenWords: [String]) -> Bool {
        func matches(spokenIndex: Int, nameIndex: Int) -> Bool {
            if spokenIndex == spokenWords.count { return true }
            if nameIndex == nameWords.count { return false }
            let spokenWord = spokenWords[spokenIndex]
            if nameWords[nameIndex].hasPrefix(spokenWord), matches(spokenIndex: spokenIndex + 1, nameIndex: nameIndex + 1) { return true }
            // Initials: "vs" → "visual studio".
            if spokenWord.count >= 2, nameIndex + spokenWord.count <= nameWords.count {
                let initials = String(nameWords[nameIndex..<(nameIndex + spokenWord.count)].compactMap(\.first))
                if initials == spokenWord, matches(spokenIndex: spokenIndex + 1, nameIndex: nameIndex + spokenWord.count) { return true }
            }
            return false
        }
        return matches(spokenIndex: 0, nameIndex: 0)
    }

    // MARK: - This Mac

    private static var cachedInstalledAppNames: [String]?

    /// The installed apps' names, read once per launch: /Applications, the system's, its utilities
    /// and the person's own folder.
    @MainActor static func installedAppNamesOnThisMac() -> [String] {
        if let cachedInstalledAppNames { return cachedInstalledAppNames }
        let fileManager = FileManager.default
        let folders = [
            URL(fileURLWithPath: "/Applications"),
            URL(fileURLWithPath: "/System/Applications"),
            URL(fileURLWithPath: "/System/Applications/Utilities"),
            fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Applications"),
        ]
        var names: [String] = []
        for folder in folders {
            guard let entries = try? fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else { continue }
            for entry in entries where entry.pathExtension == "app" {
                names.append(displayName(ofApplicationAt: entry))
            }
        }
        cachedInstalledAppNames = names
        return names
    }

    /// The name the fast lane's finder needs: the bundle folder's own name, which is what
    /// `<name>.app` resolves against (a display name can differ from the folder name).
    private static func displayName(ofApplicationAt url: URL) -> String {
        url.deletingPathExtension().lastPathComponent
    }

    @MainActor static func defaultBrowserNameOnThisMac() -> String? {
        guard let exampleURL = URL(string: "https://example.com"),
              let browserURL = NSWorkspace.shared.urlForApplication(toOpen: exampleURL) else { return nil }
        return displayName(ofApplicationAt: browserURL)
    }

    /// Live: what was spoken, resolved against this Mac's apps.
    @MainActor static func resolveOnThisMac(spoken: String) -> String? {
        resolve(spoken: spoken, installedAppNames: installedAppNamesOnThisMac(), defaultBrowserName: defaultBrowserNameOnThisMac())
    }
}
