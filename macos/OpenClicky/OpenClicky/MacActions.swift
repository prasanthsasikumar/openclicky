//
//  MacActions.swift
//  OpenClicky
//
//  The fast lane: the local actions OpenClicky performs itself instead of handing them to the
//  Codex agent. Opening an app or making a folder is one round-trip on the Realtime session
//  (about two seconds) rather than two Codex model turns (about twelve).
//
//  Every action takes typed arguments — never a path, never a command. A voice model mishears; the
//  worst a mishearing can do here is create a wrongly *named* folder in a location the user asked
//  for. Anything outside this set still goes to `send_to_agent`, which has the sandbox, the routing
//  instructions, and the approval gate in front of it.
//
//  This file is the pure core: locations, validation, and the sentence said for each outcome.
//  `MacActionRunner` performs them.
//

import Foundation

/// The folders the fast lane will touch. A closed set, so no spoken phrase can widen it.
enum MacActionLocation: String, CaseIterable {
    case desktop
    case downloads
    case documents
    case workspace
    case home

    /// Parses the model's argument. Unknown values return nil so the caller can say so out loud
    /// rather than writing somewhere the user did not ask for.
    static func named(_ raw: String) -> MacActionLocation? {
        MacActionLocation(rawValue: raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased())
    }

    func directoryURL(homeDirectory: URL, workspaceDirectory: URL) -> URL {
        switch self {
        case .desktop: return homeDirectory.appendingPathComponent("Desktop")
        case .downloads: return homeDirectory.appendingPathComponent("Downloads")
        case .documents: return homeDirectory.appendingPathComponent("Documents")
        case .workspace: return workspaceDirectory
        case .home: return homeDirectory
        }
    }

    /// How the assistant refers to the folder when it speaks.
    var spokenName: String {
        switch self {
        case .desktop: return "your Desktop"
        case .downloads: return "your Downloads folder"
        case .documents: return "your Documents folder"
        case .workspace: return "your OpenClicky folder"
        case .home: return "your home folder"
        }
    }
}

/// What happened, and the single sentence the assistant says about it. The Realtime model speaks
/// this string back verbatim, so it is product copy: no jargon, no paths read out character by
/// character, no apologies.
enum MacActionOutcome: Equatable {
    case openedApp(String)
    case appNotFound(String)
    case createdFolder(name: String, location: MacActionLocation)
    case folderAlreadyExists(name: String, location: MacActionLocation)
    case revealed(name: String, location: MacActionLocation)
    case nothingToReveal(name: String, location: MacActionLocation)
    case openedURL(String)
    case volumeSet(Int)
    case mediaControlled(String)
    case invalidName
    case unknownLocation(String)
    case invalidURL
    case permissionDenied(MacActionLocation)
    case failed(String)

    var spokenSentence: String {
        switch self {
        case .openedApp(let applicationName):
            return "Opened \(applicationName)."
        case .appNotFound(let applicationName):
            return "I couldn't find an app called \(applicationName)."
        case .createdFolder(let name, let location):
            return "Created \(name) on \(location.spokenName)."
        case .folderAlreadyExists(let name, let location):
            return "\(name) is already on \(location.spokenName)."
        case .revealed(let name, let location):
            return "Showing \(name) on \(location.spokenName)."
        case .nothingToReveal(let name, let location):
            return "I couldn't find \(name) on \(location.spokenName)."
        case .openedURL:
            return "Opened it in your browser."
        case .volumeSet(let level):
            return "Volume set to \(level) percent."
        case .mediaControlled:
            return "Done."
        case .invalidName:
            return "That name has characters a folder can't have."
        case .unknownLocation(let raw):
            return "I don't know where \(raw) is."
        case .invalidURL:
            return "That doesn't look like a web address I can open."
        case .permissionDenied(let location):
            return "macOS hasn't granted access to \(location.spokenName) yet — I asked for it."
        case .failed(let reason):
            return "That didn't work: \(reason)."
        }
    }
}

/// Argument checks. Both return nil for anything the fast lane will not act on.
enum MacActionValidation {

    /// A folder name the model spoke, or nil when it could reach outside the location it was given.
    /// `/` and `:` are the two separators macOS honours; a leading dot would create something the
    /// user cannot see; 255 bytes is the filesystem's own limit.
    static func folderName(_ raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 255 else { return nil }
        guard !trimmed.contains("/"), !trimmed.contains(":") else { return nil }
        guard !trimmed.hasPrefix(".") else { return nil }
        return trimmed
    }

    /// An http(s) URL, or nil. Other schemes are refused: `file:` reads the disk and a custom scheme
    /// launches whichever app registered it, neither of which is "open this page for me".
    static func webURL(_ raw: String) -> URL? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), let scheme = url.scheme?.lowercased() else { return nil }
        guard scheme == "http" || scheme == "https" else { return nil }
        guard url.host?.isEmpty == false else { return nil }
        return url
    }
}
