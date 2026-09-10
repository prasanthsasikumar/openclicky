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
import AppKit

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
    case invalidAppName
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
        case .invalidAppName:
            return "That app name has characters an app name can't have."
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
    /// user cannot see; 255 characters is the filesystem's own limit. Also used to validate
    /// `open_app`'s name, since the same rules keep it from resolving to a path outside the
    /// application search directories (`../System/Applications/Calculator`, say).
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

/// One action the Realtime model asked for, already parsed and typed.
enum MacAction: Equatable {
    case openApp(name: String)
    case openURL(raw: String)
    case createFolder(name: String, location: MacActionLocation)
    case revealInFinder(name: String, location: MacActionLocation)
    case setVolume(level: Int)
    case mediaControl(action: String)
}

/// Performs the fast-lane actions. The AppKit calls are injected so the whole runner can be tested
/// against a temporary directory without launching apps or moving the user's volume.
@MainActor
final class MacActionRunner {

    private let homeDirectory: URL
    private let workspaceDirectory: URL
    private let findApplication: (String) -> URL?
    private let launchApplication: (URL) -> Void
    private let openURL: (URL) -> Void
    private let revealInFinder: (URL) -> Void
    private let setVolume: (Int) -> Void
    private let sendMediaKey: (String) -> Void

    init(
        homeDirectory: URL,
        workspaceDirectory: URL,
        findApplication: @escaping (String) -> URL?,
        launchApplication: @escaping (URL) -> Void,
        openURL: @escaping (URL) -> Void,
        revealInFinder: @escaping (URL) -> Void,
        setVolume: @escaping (Int) -> Void,
        sendMediaKey: @escaping (String) -> Void
    ) {
        self.homeDirectory = homeDirectory
        self.workspaceDirectory = workspaceDirectory
        self.findApplication = findApplication
        self.launchApplication = launchApplication
        self.openURL = openURL
        self.revealInFinder = revealInFinder
        self.setVolume = setVolume
        self.sendMediaKey = sendMediaKey
    }

    func perform(_ action: MacAction) async -> MacActionOutcome {
        switch action {
        case .openApp(let name):
            guard let applicationURL = findApplication(name) else { return .appNotFound(name) }
            launchApplication(applicationURL)
            return .openedApp(name)

        case .openURL(let raw):
            guard let url = MacActionValidation.webURL(raw) else { return .invalidURL }
            openURL(url)
            return .openedURL(url.absoluteString)

        case .createFolder(let rawName, let location):
            guard let name = MacActionValidation.folderName(rawName) else { return .invalidName }
            let directory = location.directoryURL(homeDirectory: homeDirectory, workspaceDirectory: workspaceDirectory)
            let target = directory.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: target.path) {
                return .folderAlreadyExists(name: name, location: location)
            }
            do {
                try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
                return .createdFolder(name: name, location: location)
            } catch let error as NSError {
                // macOS answers a TCC-protected folder with EPERM until the user approves the prompt
                // it has just shown them.
                if error.domain == NSCocoaErrorDomain && error.code == NSFileWriteNoPermissionError {
                    return .permissionDenied(location)
                }
                return .failed(error.localizedDescription)
            }

        case .revealInFinder(let rawName, let location):
            guard let name = MacActionValidation.folderName(rawName) else { return .invalidName }
            let directory = location.directoryURL(homeDirectory: homeDirectory, workspaceDirectory: workspaceDirectory)
            let target = directory.appendingPathComponent(name)
            guard FileManager.default.fileExists(atPath: target.path) else {
                return .nothingToReveal(name: name, location: location)
            }
            revealInFinder(target)
            return .revealed(name: name, location: location)

        case .setVolume(let requestedLevel):
            let level = min(100, max(0, requestedLevel))
            setVolume(level)
            return .volumeSet(level)

        case .mediaControl(let mediaAction):
            guard ["playpause", "next", "previous"].contains(mediaAction) else {
                return .failed("I don't know how to \(mediaAction)")
            }
            sendMediaKey(mediaAction)
            return .mediaControlled(mediaAction)
        }
    }

    /// The runner the app uses: real LaunchServices, real Finder, real volume.
    static func live(workspaceDirectory: URL) -> MacActionRunner {
        MacActionRunner(
            homeDirectory: FileManager.default.homeDirectoryForCurrentUser,
            workspaceDirectory: workspaceDirectory,
            findApplication: { name in
                // A bundle identifier if the model said one ("com.spotify.client"), otherwise the
                // display name the user actually spoke.
                NSWorkspace.shared.urlForApplication(withBundleIdentifier: name) ?? Self.applicationURL(named: name)
            },
            launchApplication: { applicationURL in
                let configuration = NSWorkspace.OpenConfiguration()
                configuration.activates = true
                NSWorkspace.shared.openApplication(at: applicationURL, configuration: configuration, completionHandler: nil)
            },
            openURL: { url in NSWorkspace.shared.open(url) },
            revealInFinder: { url in NSWorkspace.shared.activateFileViewerSelecting([url]) },
            setVolume: { level in
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
                process.arguments = ["-e", "set volume output volume \(level)"]
                try? process.run()
            },
            sendMediaKey: { action in Self.postMediaKey(action) }
        )
    }

    /// Looks for `<name>.app` in the standard application folders. LaunchServices has no display-name
    /// lookup that does not also need a bundle identifier, and asking Spotlight would be slower than
    /// the action itself.
    private static func applicationURL(named name: String) -> URL? {
        let searchDirectories = [
            URL(fileURLWithPath: "/Applications"),
            URL(fileURLWithPath: "/System/Applications"),
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications"),
        ]
        let wanted = name.lowercased().hasSuffix(".app") ? name.lowercased() : "\(name.lowercased()).app"
        for directory in searchDirectories {
            let candidate = directory.appendingPathComponent(wanted)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            // Fall back to a case-insensitive scan of that one folder: "vs code" never matches, but
            // "spotify" finding "Spotify.app" must not depend on the user's capitalisation.
            if let entries = try? FileManager.default.contentsOfDirectory(atPath: directory.path),
               let match = entries.first(where: { $0.lowercased() == wanted }) {
                return directory.appendingPathComponent(match)
            }
        }
        return nil
    }

    /// The media keys are NX system-defined events, not ordinary key codes.
    private static func postMediaKey(_ action: String) {
        let keyCode: Int32
        switch action {
        case "next": keyCode = 17        // NX_KEYTYPE_FAST
        case "previous": keyCode = 18    // NX_KEYTYPE_REWIND
        default: keyCode = 16            // NX_KEYTYPE_PLAY
        }
        for isKeyDown in [true, false] {
            let flags = NSEvent.ModifierFlags(rawValue: isKeyDown ? 0xA00 : 0xB00)
            let data1 = Int((keyCode << 16) | ((isKeyDown ? 0xA : 0xB) << 8))
            guard let event = NSEvent.otherEvent(
                with: .systemDefined, location: .zero, modifierFlags: flags, timestamp: 0,
                windowNumber: 0, context: nil, subtype: 8, data1: data1, data2: -1
            ) else { continue }
            event.cgEvent?.post(tap: .cghidEventTap)
        }
    }
}

/// What the tool call turned out to be.
enum MacActionParseResult: Equatable {
    case action(MacAction)
    /// Not one of the fast-lane tools: the caller's existing handling applies.
    case notAFastAction
    /// A fast-lane tool with arguments that cannot be acted on; the outcome carries what to say.
    case badArguments(MacActionOutcome)
}

extension MacAction {

    /// Turns the Realtime tool call into a typed action. The model controls these values: nothing
    /// here trusts them beyond the enum and the validation in `MacActionValidation`.
    static func parse(toolName: String, arguments: [String: Any]) -> MacActionParseResult {
        let string = { (key: String) -> String in
            (arguments[key] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        // Locations are optional: the workspace is the only one that never triggers a macOS
        // permission prompt, so an unspecified location goes there rather than to the Desktop.
        let location = { () -> MacActionLocation? in
            let raw = string("location")
            if raw.isEmpty { return .workspace }
            return MacActionLocation.named(raw)
        }

        switch toolName {
        case "open_app":
            guard let name = MacActionValidation.folderName(string("name")) else { return .badArguments(.invalidAppName) }
            return .action(.openApp(name: name))

        case "open_url":
            return .action(.openURL(raw: string("url")))

        case "create_folder":
            guard let location = location() else { return .badArguments(.unknownLocation(string("location"))) }
            return .action(.createFolder(name: string("name"), location: location))

        case "reveal_in_finder":
            guard let location = location() else { return .badArguments(.unknownLocation(string("location"))) }
            return .action(.revealInFinder(name: string("name"), location: location))

        case "set_volume":
            let raw = arguments["level"]
            if let number = raw as? NSNumber { return .action(.setVolume(level: number.intValue)) }
            if let text = raw as? String, let number = Int(text.trimmingCharacters(in: .whitespaces)) {
                return .action(.setVolume(level: number))
            }
            return .badArguments(.failed("I need a volume between 0 and 100"))

        case "media_control":
            return .action(.mediaControl(action: string("action").lowercased()))

        default:
            return .notAFastAction
        }
    }
}
