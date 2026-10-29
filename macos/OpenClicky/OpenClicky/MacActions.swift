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
    case playerControlled(String)
    case searched(query: String, site: WebSearchSite)
    case invalidQuery
    case minimizedWindows(count: Int, keptApp: String?)
    case restoredWindows(count: Int, app: String?)
    case windowAppNotRunning(String)
    case unreadMail([UnreadMail])
    case openedGmailInbox
    case mailUnavailable(MailScriptError)
    case draftedEmail(app: EmailApp, to: String, bodyLength: Int)
    case invalidRecipient
    case mailDraftFailed(MailScriptError)
    case playingTrack(name: String, artist: String)
    case openedSpotifySearch(query: String)
    case spotifyStarting
    case spotifyNotInstalled
    case playFailed
    case invalidName
    case invalidAppName
    case unknownLocation(String)
    case invalidURL
    case permissionDenied(MacActionLocation)
    case failed(String)

    /// True when the action did what was asked (the tour counts it as a task handed over).
    var didSomething: Bool {
        switch self {
        case .openedApp, .createdFolder, .folderAlreadyExists, .revealed, .openedURL, .volumeSet, .mediaControlled, .playerControlled, .searched, .minimizedWindows, .restoredWindows, .unreadMail, .openedGmailInbox, .draftedEmail, .playingTrack, .openedSpotifySearch: return true
        default: return false
        }
    }

    /// What the Realtime model is told in the tool's output. The same as the sentence unless an
    /// outcome has more to say to the model than to the person (a list to read out, a screenshot
    /// to look at).
    var toolOutput: String {
        switch self {
        case .searched(let query, _):
            return "The results for \(query) are on screen in the latest screenshot. If they asked for a fact (the weather, a score, a price), say that fact from the page; otherwise say what the first few results are. One sentence."
        case .unreadMail(let unread):
            guard !unread.isEmpty else { return spokenSentence }
            let lines = unread.map { "\($0.sender) — \($0.subject)" }.joined(separator: "\n")
            return "Unread in Mail:\n\(lines)\nRead out sender and subject briefly, in this order, at most five. Never invent what the emails say."
        case .openedGmailInbox:
            return "Gmail's inbox is open and attached as the latest screenshot. Unread rows are in bold. Read out sender and subject of the unread ones briefly, newest first, at most five, then stop."
        case .mailUnavailable:
            return spokenSentence + " Mail is on screen in the latest screenshot; read the unread rows if you can see them."
        case .draftedEmail:
            return spokenSentence + " If they ask you to send it, say you never send email and that it is open in front of them."
        case .windowAppNotRunning:
            return spokenSentence + " Call open_app if they want it opened."
        default:
            return spokenSentence
        }
    }

    /// What goes in app.log. The same as the sentence unless it would carry something private
    /// (an email body or recipient).
    var logDescription: String {
        switch self {
        case .unreadMail(let unread):
            return "check_email: \(unread.count) unread in Mail"
        case .draftedEmail(let app, let recipient, let bodyLength):
            return "draft_email: \(app.rawValue), \(bodyLength)-character body, \(recipient.contains("@") ? "address" : "name") given"
        // An AppleScript error message can quote the expression that failed, body and all.
        case .mailDraftFailed:
            return "draft_email: mail script failed"
        case .mailUnavailable:
            return "check_email: mail script failed"
        default:
            return spokenSentence
        }
    }

    /// Whether the model should see a fresh screenshot before it speaks: true for an action that
    /// changed what is on screen in a way worth describing (a page opened), false for the rest.
    var thenLooksAtScreen: Bool {
        switch self {
        case .openedURL, .searched, .openedGmailInbox, .mailUnavailable: return true
        case .draftedEmail(let app, _, _): return app == .gmail
        default: return false
        }
    }

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
        case .playerControlled(let command):
            return command == "pause" ? "Paused." : "Playing."
        case .searched(let query, _):
            return "Searching for \(query)."
        case .invalidQuery:
            return "I didn't catch what to search for."
        case .minimizedWindows(let count, nil):
            return count == 0 ? "Nothing to minimize." : "Minimized \(count) window\(count == 1 ? "" : "s")."
        case .minimizedWindows(let count, let keptApp?):
            return count == 0 ? "Nothing else to minimize; \(keptApp) stays up." : "Minimized \(count) window\(count == 1 ? "" : "s"); \(keptApp) stays up."
        case .restoredWindows(let count, nil):
            return count == 0 ? "I hadn't minimized anything." : "Brought \(count) window\(count == 1 ? "" : "s") back."
        case .restoredWindows(let count, let app?):
            // Zero is not a failure: the app had nothing in the Dock and is now in front.
            return count == 0 ? "\(app) is in front now." : "Brought \(app) back."
        case .windowAppNotRunning(let app):
            return "\(app) isn't open."
        case .unreadMail(let unread):
            guard !unread.isEmpty else { return "No unread email in Mail." }
            let named = unread.prefix(3).map { "\($0.spokenSender) about \($0.subject)" }
            let more = unread.count - named.count
            let list = more > 0 ? named.joined(separator: ", ") + ", and \(more) more" : named.joined(separator: ", ")
            return "\(unread.count) unread: \(list)."
        case .openedGmailInbox:
            return "Here's your inbox."
        case .mailUnavailable(let error):
            return error == .notAllowed
                ? "macOS didn't let me control Mail. Allow OpenClicky under System Settings, Privacy & Security, Automation. I opened Mail instead."
                : "I couldn't read Mail in time, so I opened it instead."
        case .draftedEmail(_, let recipient, _):
            return "Drafted an email to \(recipient); it's open for you to check. I don't send email — you do."
        case .invalidRecipient:
            return "I didn't catch who it's for."
        case .playingTrack(let name, let artist):
            return artist.isEmpty ? "Playing \(name)." : "Playing \(name) by \(artist)."
        case .openedSpotifySearch(let query):
            return "I couldn't look that up, so I opened Spotify's search for \(query); pick one."
        case .spotifyStarting:
            return "Spotify is starting; give it a moment and ask again."
        case .spotifyNotInstalled:
            return "Spotify isn't installed on this Mac."
        case .playFailed:
            return "Spotify didn't play it."
        case .mailDraftFailed(let error):
            switch error {
            case .failed(let reason): return "Mail couldn't make the draft: \(reason)."
            case .notAllowed: return "macOS didn't let me control Mail. Allow OpenClicky under System Settings, Privacy & Security, Automation."
            case .timedOut: return "Mail didn't answer in time."
            case .recipientNotFilled: return "The draft is open, but I couldn't fill in the recipient; add it yourself."
            }
        case .invalidName:
            return "That name has characters a folder can't have."
        case .invalidAppName:
            return "That app name has characters an app name can't have."
        case .unknownLocation(let raw):
            return "I don't know where \(raw) is."
        case .invalidURL:
            return "That doesn't look like a web address I can open."
        case .permissionDenied(let location):
            return "macOS hasn't granted access to \(location.spokenName) yet. I asked for it."
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

    /// Something to search for, or nil: trimmed, at most 200 characters, and nothing a control
    /// character (a newline, a NUL) could do to the URL or the page.
    static func searchQuery(_ raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 200 else { return nil }
        guard trimmed.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) && !CharacterSet.newlines.contains($0) }) else { return nil }
        return trimmed
    }

    /// Who an email is for: an address or a name as said, at most 80 characters, never an angle
    /// bracket (so it can only ever land in a To field). Nil when there is nothing usable.
    static func recipient(_ raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 80, !trimmed.contains("<"), !trimmed.contains(">") else { return nil }
        return trimmed
    }

    static func emailSubject(_ raw: String) -> String {
        String(raw.trimmingCharacters(in: .whitespacesAndNewlines).prefix(200))
    }

    /// Gmail's compose URL carries about this much; Mail takes the same cap so both behave alike.
    static func emailBody(_ raw: String) -> String {
        String(raw.trimmingCharacters(in: .whitespacesAndNewlines).prefix(2_000))
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
    case webSearch(query: String, site: WebSearchSite)
    /// Every window to the Dock, except the named app's when there is one.
    case minimizeAll(keepApp: String?)
    /// What minimizeAll minimized, or only the named app's windows when there is one.
    case restoreWindows(app: String?)
    case checkEmail(app: String?)
    case draftEmail(to: String, subject: String, body: String, app: String?)
    case playMusic(query: String)
}

/// Performs the fast-lane actions. The AppKit calls are injected so the whole runner can be tested
/// against a temporary directory without launching apps or moving the user's volume.
@MainActor
final class MacActionRunner {

    /// The system calls the newer actions need, each defaulted to "nothing happened" so a test
    /// passes only the ones it drives. `live` fills every one of them.
    struct Extras {
        /// Sends pause / play to a running music player; false when no player is running.
        var controlPlayer: (MediaCommand) async -> Bool
        /// Minimizes every ordinary window on screen except the named app's; returns how many,
        /// or nil when the named app is not running.
        var minimizeAllWindows: (String?) -> Int?
        /// Brings back what the last minimizeAllWindows minimized, or only the named app's windows;
        /// returns how many, or nil when the named app is not running.
        var restoreMinimizedWindows: (String?) -> Int?
        /// Which email app a check or draft goes to, given the app the person named (or nil).
        var chooseEmailApp: (String?) -> EmailApp
        /// Mail's unread inbox messages, newest first, or why they could not be read.
        var readUnreadMail: () async -> Result<[UnreadMail], MailScriptError>
        /// Runs a draft script in Mail; nil when it worked.
        var runMailDraft: (String) async -> MailScriptError?
        /// Brings Mail to the front (when it could not be read, so the screen shows it instead).
        var activateMail: () -> Void
        /// The catalogue lookup for play_music; empty when nothing answered.
        var lookUpTrack: (String) async -> [MusicTrack]
        var spotifyIsInstalled: () -> Bool
        var spotifyIsRunning: () -> Bool
        /// Opens Spotify and waits for it to answer; false when it did not in time.
        var launchSpotify: () async -> Bool
        /// Plays a track by URI; false when Spotify refused.
        var playTrack: (String) async -> Bool
        /// Opens Spotify's own search page for the words.
        var openSpotifySearch: (String) -> Void

        init(
            controlPlayer: @escaping (MediaCommand) async -> Bool = { _ in false },
            minimizeAllWindows: @escaping (String?) -> Int? = { _ in 0 },
            restoreMinimizedWindows: @escaping (String?) -> Int? = { _ in 0 },
            chooseEmailApp: @escaping (String?) -> EmailApp = { _ in .gmail },
            readUnreadMail: @escaping () async -> Result<[UnreadMail], MailScriptError> = { .success([]) },
            runMailDraft: @escaping (String) async -> MailScriptError? = { _ in nil },
            activateMail: @escaping () -> Void = {},
            lookUpTrack: @escaping (String) async -> [MusicTrack] = { _ in [] },
            spotifyIsInstalled: @escaping () -> Bool = { false },
            spotifyIsRunning: @escaping () -> Bool = { false },
            launchSpotify: @escaping () async -> Bool = { false },
            playTrack: @escaping (String) async -> Bool = { _ in false },
            openSpotifySearch: @escaping (String) -> Void = { _ in }
        ) {
            self.controlPlayer = controlPlayer
            self.minimizeAllWindows = minimizeAllWindows
            self.restoreMinimizedWindows = restoreMinimizedWindows
            self.chooseEmailApp = chooseEmailApp
            self.readUnreadMail = readUnreadMail
            self.runMailDraft = runMailDraft
            self.activateMail = activateMail
            self.lookUpTrack = lookUpTrack
            self.spotifyIsInstalled = spotifyIsInstalled
            self.spotifyIsRunning = spotifyIsRunning
            self.launchSpotify = launchSpotify
            self.playTrack = playTrack
            self.openSpotifySearch = openSpotifySearch
        }

        static let inert = Extras()
    }

    private let homeDirectory: URL
    private let workspaceDirectory: URL
    private let findApplication: (String) -> URL?
    private let launchApplication: (URL) -> Void
    private let openURL: (URL) -> Void
    private let revealInFinder: (URL) -> Void
    private let setVolume: (Int) -> Void
    private let sendMediaKey: (String) -> Void
    private let extras: Extras

    init(
        homeDirectory: URL,
        workspaceDirectory: URL,
        findApplication: @escaping (String) -> URL?,
        launchApplication: @escaping (URL) -> Void,
        openURL: @escaping (URL) -> Void,
        revealInFinder: @escaping (URL) -> Void,
        setVolume: @escaping (Int) -> Void,
        sendMediaKey: @escaping (String) -> Void,
        extras: Extras = .inert
    ) {
        self.homeDirectory = homeDirectory
        self.workspaceDirectory = workspaceDirectory
        self.findApplication = findApplication
        self.launchApplication = launchApplication
        self.openURL = openURL
        self.revealInFinder = revealInFinder
        self.setVolume = setVolume
        self.sendMediaKey = sendMediaKey
        self.extras = extras
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
            // "Stop that song" goes to the player itself: the play/pause key toggles, so with
            // nothing playing it would start something.
            if mediaAction == "pause" || mediaAction == "play" {
                let playerTookIt = await extras.controlPlayer(mediaAction == "pause" ? .pause : .play)
                if playerTookIt { return .playerControlled(mediaAction) }
                if mediaAction == "pause" { return .failed("nothing is playing") }
            }
            guard ["playpause", "next", "previous", "play"].contains(mediaAction) else {
                return .failed("I don't know how to \(mediaAction)")
            }
            sendMediaKey(mediaAction)
            return .mediaControlled(mediaAction)

        case .webSearch(let query, let site):
            openURL(WebSearchSite.url(for: site, query: query))
            return .searched(query: query, site: site)

        case .minimizeAll(let keptApp):
            guard let count = extras.minimizeAllWindows(keptApp) else { return .windowAppNotRunning(keptApp ?? "") }
            return .minimizedWindows(count: count, keptApp: keptApp)

        case .restoreWindows(let app):
            guard let count = extras.restoreMinimizedWindows(app) else { return .windowAppNotRunning(app ?? "") }
            return .restoredWindows(count: count, app: app)

        case .checkEmail(let requestedApp):
            switch extras.chooseEmailApp(requestedApp) {
            case .gmail:
                openURL(GmailURLs.inbox)
                return .openedGmailInbox
            case .mail:
                switch await extras.readUnreadMail() {
                case .success(let unread):
                    return .unreadMail(unread)
                case .failure(let error):
                    // The screen shows Mail instead, and the model reads what it can.
                    extras.activateMail()
                    return .mailUnavailable(error)
                }
            }

        case .draftEmail(let rawRecipient, let rawSubject, let rawBody, let requestedApp):
            guard let recipient = MacActionValidation.recipient(rawRecipient) else { return .invalidRecipient }
            let subject = MacActionValidation.emailSubject(rawSubject)
            let body = MacActionValidation.emailBody(rawBody)
            guard !body.isEmpty else { return .failed("I need something to write") }
            switch extras.chooseEmailApp(requestedApp) {
            case .gmail:
                openURL(GmailURLs.compose(to: recipient, subject: subject, body: body))
                return .draftedEmail(app: .gmail, to: recipient, bodyLength: body.count)
            case .mail:
                if let error = await extras.runMailDraft(MailScripts.draftSource(to: recipient, subject: subject, body: body)) {
                    return .mailDraftFailed(error)
                }
                return .draftedEmail(app: .mail, to: recipient, bodyLength: body.count)
            }

        case .playMusic(let query):
            guard extras.spotifyIsInstalled() else { return .spotifyNotInstalled }
            if !extras.spotifyIsRunning() {
                guard await extras.launchSpotify() else { return .spotifyStarting }
            }
            let tracks = await extras.lookUpTrack(query)
            // No lookup answered, or it answered with something that is not a track (the backend
            // is not trusted with what Spotify is told to play): Spotify's own search leaves the
            // person one click away.
            guard let track = MusicTrackPicker.pick(tracks, for: query), track.uri.hasPrefix("spotify:track:") else {
                extras.openSpotifySearch(query)
                return .openedSpotifySearch(query: query)
            }
            guard await extras.playTrack(track.uri) else { return .playFailed }
            return .playingTrack(name: track.name, artist: track.artists.first ?? "")
        }
    }

    /// The music lookup's route is logged when it changes (own credentials added, say), not per call.
    private static var lastLoggedMusicRoute: String?

    /// Which player pause / play goes to: the one with the track (Music playing while Spotify sits
    /// idle gets Music, not Spotify); pause with nothing playing goes nowhere; play with nothing up
    /// goes to Spotify if it runs, else the first running player.
    nonisolated static func playerForControl(_ command: MediaCommand, playing: MediaPlayer?, running: [MediaPlayer]) -> MediaPlayer? {
        if let playing { return playing }
        guard command == .play else { return nil }
        return running.contains(.spotify) ? .spotify : running.first
    }

    /// The runner the app uses: real LaunchServices, real Finder, real volume.
    static func live(workspaceDirectory: URL) -> MacActionRunner {
        let minimizedWindows = MinimizedWindowsMemory()
        let musicLookup = { () -> MusicLookup in
            let route = MusicLookupRoute.route(credentials: OpenClickyConfiguration.settings.spotifyCredentials, backendURL: URL(string: OpenClickyConfiguration.settings.backendUrl))
            if route.logName != lastLoggedMusicRoute {
                lastLoggedMusicRoute = route.logName
                AppLog.append("route: music lookup → \(route.logName)")
            }
            return MusicLookup(route: route)
        }
        return MacActionRunner(
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
            sendMediaKey: { action in Self.postMediaKey(action) },
            extras: Extras(
                controlPlayer: { command in
                    let running = AppleScriptMediaSource.runningPlayers()
                    guard !running.isEmpty else { return false }
                    let source = AppleScriptMediaSource()
                    // The player with the track: the one playing for pause, the one paused too for play.
                    var playerWithTrack: MediaPlayer?
                    if case .track(let snapshot) = await source.readNowPlaying(), command == .play || snapshot.isPlaying {
                        playerWithTrack = snapshot.player
                    }
                    guard let player = Self.playerForControl(command, playing: playerWithTrack, running: running) else { return false }
                    await source.perform(command, on: player)
                    return true
                },
                minimizeAllWindows: { keptApp in minimizedWindows.minimizeEverything(keepingAppNamed: keptApp) },
                restoreMinimizedWindows: { app in minimizedWindows.restore(appNamed: app) },
                chooseEmailApp: { requested in
                    EmailApp.choose(requested: requested, setting: OpenClickyConfiguration.settings.emailApp, mailHasAccounts: EmailApp.mailHasAccounts())
                },
                readUnreadMail: {
                    await MailAppleScriptRunner.run(MailScripts.unreadInboxSource()).map(MailScripts.parseUnread)
                },
                runMailDraft: { script in
                    switch await MailAppleScriptRunner.run(script) {
                    case .failure(let error): return error
                    case .success(let reply): return reply == "no-recipient" ? .recipientNotFilled : nil
                    }
                },
                activateMail: {
                    guard let mailURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.mail") else { return }
                    let configuration = NSWorkspace.OpenConfiguration()
                    configuration.activates = true
                    NSWorkspace.shared.openApplication(at: mailURL, configuration: configuration, completionHandler: nil)
                },
                lookUpTrack: { query in await musicLookup().search(query) },
                spotifyIsInstalled: { SpotifyPlayer.isInstalled() },
                spotifyIsRunning: { SpotifyPlayer.isRunning() },
                launchSpotify: { await SpotifyPlayer.launchAndWait() },
                playTrack: { uri in await SpotifyPlayer.play(uri: uri) },
                openSpotifySearch: { query in SpotifyPlayer.openSearch(for: query) }
            )
        )
    }

    /// Looks for `<name>.app` in the standard application folders. LaunchServices has no display-name
    /// lookup that does not also need a bundle identifier, and asking Spotlight would be slower than
    /// the action itself.
    static func applicationURL(named name: String) -> URL? {
        let searchDirectories = [
            URL(fileURLWithPath: "/Applications"),
            URL(fileURLWithPath: "/System/Applications"),
            // Safari (and only Safari) lives here since macOS 26, not in /Applications.
            URL(fileURLWithPath: "/System/Cryptexes/App/System/Applications"),
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

    /// The local actions OpenClicky performs itself. Typed arguments only: `location` is a closed
    /// set and there is no path or command anywhere in the schema, so a mishearing cannot widen
    /// what the fast lane is able to do. `parse` below reads the calls these produce.
    // nonisolated so the test target can build the definitions without a main-actor hop; it touches no state.
    nonisolated static func realtimeToolDefinitions() -> [[String: Any]] {
        let locations = MacActionLocation.allCases.map(\.rawValue)
        let locationProperty: [String: Any] = [
            "type": "string",
            "enum": locations,
            "description": "Which folder. Leave it out for OpenClicky's own workspace folder.",
        ]
        return [
            [
                "type": "function",
                "name": "open_app",
                "description": "Open or switch to a Mac app by its name, e.g. 'Spotify'. Use this instead of the agent for simply opening an app.",
                "parameters": ["type": "object", "properties": ["name": ["type": "string", "description": "The app's name as the user said it."]], "required": ["name"]],
            ],
            [
                "type": "function",
                "name": "open_url",
                "description": "Open a web page in the user's browser. http and https only.",
                "parameters": ["type": "object", "properties": ["url": ["type": "string", "description": "The full https URL."]], "required": ["url"]],
            ],
            [
                "type": "function",
                "name": "create_folder",
                "description": "Make a new folder. Use this instead of the agent for a single folder.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "name": ["type": "string", "description": "The folder's name, without any slashes."],
                        "location": locationProperty,
                    ],
                    "required": ["name"],
                ],
            ],
            [
                "type": "function",
                "name": "reveal_in_finder",
                "description": "Show an existing file or folder in Finder.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "name": ["type": "string", "description": "The file or folder's name."],
                        "location": locationProperty,
                    ],
                    "required": ["name"],
                ],
            ],
            [
                "type": "function",
                "name": "set_volume",
                "description": "Set the Mac's output volume.",
                "parameters": ["type": "object", "properties": ["level": ["type": "integer", "description": "0 to 100."]], "required": ["level"]],
            ],
            [
                "type": "function",
                "name": "media_control",
                "description": "Pause, resume, or skip whatever is playing. Use pause for 'stop', 'pause', 'be quiet'; play to resume; playpause only when they say play/pause without saying which.",
                "parameters": ["type": "object", "properties": ["action": ["type": "string", "enum": ["playpause", "next", "previous", "pause", "play"]]], "required": ["action"]],
            ],
            [
                "type": "function",
                "name": "web_search",
                "description": "Search the web (or YouTube, Wikipedia, Google Maps, Amazon) for what the user said and show the results in their browser. Use for 'search for…', 'look up…', 'google…', and for live facts you cannot know: the weather, news, scores, prices, opening hours.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "query": ["type": "string", "description": "What to search for, as the user said it."],
                        "site": ["type": "string", "enum": WebSearchSite.allCases.map(\.rawValue), "description": "Where to search. Leave it out for the web."],
                    ],
                    "required": ["query"],
                ],
            ],
            [
                "type": "function",
                "name": "minimize_all",
                "description": "Minimize every open window so the desktop is clear. For 'minimize everything', 'clear my screen', 'hide all windows'; with keep for 'everything except Terminal', 'leave just Safari'.",
                "parameters": [
                    "type": "object",
                    "properties": ["keep": ["type": "string", "description": "Only when they named an app to leave on screen: its name, e.g. 'Terminal'."]],
                    "required": [String](),
                ],
            ],
            [
                "type": "function",
                "name": "check_email",
                "description": "Show the user's unread email. Use when they say check my email, any new mail, what's in my inbox.",
                "parameters": ["type": "object", "properties": ["app": ["type": "string", "enum": ["mail", "gmail"], "description": "Only when they named one: 'mail' for Apple Mail, 'gmail' for Gmail."]], "required": [String]()],
            ],
            [
                "type": "function",
                "name": "draft_email",
                "description": "Start a new email with the recipient and a short body written from what the user said, and leave it open for them to read and send themselves. Never sends.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "to": ["type": "string", "description": "The recipient's email address, or their name exactly as the user said it."],
                        "subject": ["type": "string", "description": "A short subject. Leave it out if nothing fits."],
                        "body": ["type": "string", "description": "The email's text, short and in the user's voice, saying what they asked you to say. No invented facts."],
                        "app": ["type": "string", "enum": ["mail", "gmail"], "description": "Only when they named one."],
                    ],
                    "required": ["to", "body"],
                ],
            ],
            [
                "type": "function",
                "name": "play_music",
                "description": "Play a specific song, album or artist in Spotify. Pass what they said, e.g. 'Blinding Lights by The Weeknd'. For pause, resume or skip use media_control.",
                "parameters": ["type": "object", "properties": ["query": ["type": "string", "description": "The song (and artist, if said), as the user said it."]], "required": ["query"]],
            ],
            [
                "type": "function",
                "name": "restore_windows",
                "description": "Bring windows back from the Dock. Without app: every window minimize_all minimized ('bring them back', 'restore my windows'). With app: that app's windows only, nothing else ('show just the terminal', 'bring back Safari').",
                "parameters": [
                    "type": "object",
                    "properties": ["app": ["type": "string", "description": "Only when they named one app: its name, e.g. 'Terminal'. Never leave it out when they named one."]],
                    "required": [String](),
                ],
            ],
        ]
    }

    /// The same quick actions as Anthropic tool definitions, for the ask lane wherever the agent
    /// lane does not run (and, through PlanToolProtocol, the plans).
    nonisolated static func anthropicToolDefinitions() -> [[String: Any]] {
        realtimeToolDefinitions().map { definition in
            [
                "name": definition["name"] as? String ?? "",
                "description": definition["description"] as? String ?? "",
                "input_schema": definition["parameters"] as? [String: Any] ?? ["type": "object", "properties": [String: Any]()],
            ]
        }
    }

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

        case "web_search":
            guard let query = MacActionValidation.searchQuery(string("query")) else { return .badArguments(.invalidQuery) }
            let rawSite = string("site").lowercased()
            let site: WebSearchSite? = rawSite.isEmpty ? .web : WebSearchSite(rawValue: rawSite)
            guard let site else { return .badArguments(.invalidQuery) }
            return .action(.webSearch(query: query, site: site))

        case "minimize_all", "restore_windows":
            // An app name is only matched against running apps, never resolved to a path, but it
            // gets open_app's checks all the same.
            let rawAppName = string(toolName == "minimize_all" ? "keep" : "app")
            var appName: String?
            if !rawAppName.isEmpty {
                guard let validName = MacActionValidation.folderName(rawAppName) else { return .badArguments(.invalidAppName) }
                appName = validName
            }
            return .action(toolName == "minimize_all" ? .minimizeAll(keepApp: appName) : .restoreWindows(app: appName))

        case "play_music":
            guard let query = MacActionValidation.searchQuery(string("query")) else { return .badArguments(.invalidQuery) }
            return .action(.playMusic(query: query))

        case "check_email":
            let app = string("app")
            return .action(.checkEmail(app: app.isEmpty ? nil : app))

        case "draft_email":
            guard let recipient = MacActionValidation.recipient(string("to")) else { return .badArguments(.invalidRecipient) }
            let body = MacActionValidation.emailBody(string("body"))
            guard !body.isEmpty else { return .badArguments(.failed("I need something to write")) }
            let app = string("app")
            return .action(.draftEmail(to: recipient, subject: string("subject"), body: body, app: app.isEmpty ? nil : app))

        default:
            return .notAFastAction
        }
    }
}
