//
//  LocalAskIntent.swift
//  OpenClicky
//
//  What the on-device model understood, as plain values, and how that becomes one of the fast
//  lane's tool calls. The model fills in a `LocalAskIntent` (through guided generation in
//  `AppleLocalAskModel`); `LocalAskIntentMapper` is where the safety sits: anything not named, a
//  pronoun, a website that isn't one, a volume outside 0–100, goes to the cloud instead of being
//  guessed at, and every quick action is run through `MacAction.parse` so the fast lane's own
//  validation applies before anything is performed.
//

import Foundation

/// The model's reading of the request. Built by hand in tests; the @Generable type converts to it.
struct LocalAskIntent: Equatable {
    enum Kind: String, CaseIterable {
        case openApp, openWebsite, createFolder, showFolder, setVolume, playback, rewriteText, needsCloud
    }
    var kind: Kind
    /// The app, folder or website named, as said; empty when none.
    var name: String = ""
    /// desktop, downloads, documents or home when a folder's place was said; else empty.
    var location: String = ""
    /// 0…100 when a level was said; -1 otherwise.
    var volumeLevel: Int = -1
    /// play, pause, next or previous; else empty.
    var playback: String = ""
    /// For rewriteText: the instruction in a few words ("shorter", "warmer", "fix the grammar").
    var rewriteInstruction: String = ""
}

enum LocalAskMapping: Equatable {
    case quickAction(toolName: String, arguments: [String: String])
    case rewrite(instruction: String)
    case cloud(reason: String)
}

enum LocalAskIntentMapper {

    /// Words that name nothing: "open it" must never open an app called "it".
    static let pronouns: Set<String> = ["it", "that", "this", "them", "the app", "that one", "this one", "the thing"]

    /// The most words an app name may have; anything longer is a description, not a name.
    static let maximumAppNameWords = 4

    /// The most words a bare playback command has ("go back to the previous song"). Anything longer
    /// asks for more than play, pause or skip: "open Spotify and play the first song in my favourite
    /// list" came back as playback with nothing named, and the play key resumed a paused YouTube
    /// video in Chrome instead (2026-10-29).
    static let maximumPlaybackWords = 6

    /// Words that mean the request asks for something besides the player's own buttons: an app to
    /// open, a second request, a song or list to find.
    static let wordsBeyondPlayback: Set<String> = ["open", "launch", "and", "then", "spotify", "youtube", "list", "playlist", "album", "artist", "favourite", "favorite", "liked", "by"]

    nonisolated static func map(_ intent: LocalAskIntent, transcript: String, hasWritingTarget: Bool, resolveApp: (String) -> String?) -> LocalAskMapping {
        let mapping = unvalidatedMapping(intent, transcript: transcript, hasWritingTarget: hasWritingTarget, resolveApp: resolveApp)
        // The fast lane's own validation is the last word: a mapping it would refuse goes to the
        // cloud rather than being spoken as a failure.
        if case .quickAction(let toolName, let arguments) = mapping {
            guard case .action = MacAction.parse(toolName: toolName, arguments: arguments) else {
                return .cloud(reason: "fast lane refused")
            }
        }
        return mapping
    }

    private nonisolated static func unvalidatedMapping(_ intent: LocalAskIntent, transcript: String, hasWritingTarget: Bool, resolveApp: (String) -> String?) -> LocalAskMapping {
        let name = intent.name.trimmingCharacters(in: .whitespacesAndNewlines)
        switch intent.kind {
        case .openApp:
            let lowered = name.lowercased()
            guard !name.isEmpty, !pronouns.contains(lowered), lowered.split(separator: " ").count <= maximumAppNameWords else {
                return .cloud(reason: "no app named")
            }
            guard let installedName = resolveApp(name) else { return .cloud(reason: "no app match") }
            return .quickAction(toolName: "open_app", arguments: ["name": installedName])

        case .openWebsite:
            // A bare word that *is* an installed app's name (or one of its aliases) is the app:
            // "open safari" came back as a website and would have opened safari.com. Only an exact
            // match counts here: "open google" must not open Google Chrome by the resolver's
            // one-word rule, which is for openApp, where the person said they want an app. A real
            // host (a dot, a scheme) stays a website.
            let looksLikeAHost = name.contains(".") || name.lowercased().hasPrefix("http")
            if !looksLikeAHost, let installedName = resolveApp(name),
               installedName.caseInsensitiveCompare(name) == .orderedSame || InstalledAppResolver.isAlias(name) {
                return .quickAction(toolName: "open_app", arguments: ["name": installedName])
            }
            guard let url = websiteURL(spoken: name) else { return .cloud(reason: "not a website") }
            return .quickAction(toolName: "open_url", arguments: ["url": url])

        case .createFolder, .showFolder:
            guard let itemName = MacActionValidation.folderName(name) else { return .cloud(reason: "no item named") }
            var arguments = ["name": itemName]
            let spokenLocation = intent.location.trimmingCharacters(in: .whitespacesAndNewlines)
            if !spokenLocation.isEmpty, let location = MacActionLocation.named(spokenLocation) {
                arguments["location"] = location.rawValue
            }
            return .quickAction(toolName: intent.kind == .createFolder ? "create_folder" : "reveal_in_finder", arguments: arguments)

        case .setVolume:
            guard let level = volumeLevel(in: intent) else { return .cloud(reason: "no volume level") }
            return .quickAction(toolName: "set_volume", arguments: ["level": String(level)])

        case .playback:
            // A named app, song or list is the cloud's: play_music, or open_app and then play.
            guard name.isEmpty, isBarePlaybackCommand(transcript) else { return .cloud(reason: "playback asks for more") }
            // play and pause go to the player with the track; the play/pause key goes to whatever
            // macOS last played, a paused browser video included, and with nothing playing it starts it.
            switch intent.playback.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
            case "play": return .quickAction(toolName: "media_control", arguments: ["action": "play"])
            case "pause": return .quickAction(toolName: "media_control", arguments: ["action": "pause"])
            case "next": return .quickAction(toolName: "media_control", arguments: ["action": "next"])
            case "previous": return .quickAction(toolName: "media_control", arguments: ["action": "previous"])
            default: return .cloud(reason: "no playback action")
            }

        case .rewriteText:
            guard hasWritingTarget else { return .cloud(reason: "no text to rewrite") }
            return .rewrite(instruction: intent.rewriteInstruction.trimmingCharacters(in: .whitespacesAndNewlines))

        case .needsCloud:
            return .cloud(reason: "model declined")
        }
    }

    /// "pause", "next song", "skip this track": short, and naming nothing to open or find.
    nonisolated static func isBarePlaybackCommand(_ transcript: String) -> Bool {
        let words = transcript.lowercased().split(whereSeparator: { !$0.isLetter && $0 != "'" }).map(String.init)
        guard words.count <= maximumPlaybackWords else { return false }
        return words.allSatisfy { !wordsBeyondPlayback.contains($0) }
    }

    /// The level the model filled in, or the one it left in words: it put "thirty" in `name` and -1
    /// in `volumeLevel` (seen live 2026-10-28), so the spoken number is read the dictation way.
    nonisolated static func volumeLevel(in intent: LocalAskIntent) -> Int? {
        if (0...100).contains(intent.volumeLevel) { return intent.volumeLevel }
        let spoken = intent.name.lowercased()
        guard !spoken.isEmpty else { return nil }
        if spoken.contains("mute") || spoken.contains("silen") { return 0 }
        if spoken.contains("full") || spoken.contains("max") { return 100 }
        for token in SpokenNumbers.digitize(spoken).split(whereSeparator: { !$0.isNumber }) {
            if let level = Int(token), (0...100).contains(level) { return level }
        }
        return nil
    }

    /// "youtube" → https://youtube.com, "bbc.co.uk" → https://bbc.co.uk, a full http(s) URL as it
    /// is; nil for anything with spaces or that is not shaped like a host ("the news").
    nonisolated static func websiteURL(spoken: String) -> String? {
        let trimmed = spoken.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !trimmed.isEmpty, !trimmed.contains(" ") else { return nil }
        let candidate: String
        if trimmed.hasPrefix("http://") || trimmed.hasPrefix("https://") {
            candidate = trimmed
        } else if trimmed.contains(".") {
            // A host with a dot: letters or digits on both sides of every dot.
            let parts = trimmed.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
            let host = String(parts[0])
            let labels = host.split(separator: ".", omittingEmptySubsequences: false)
            guard labels.count >= 2, labels.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" } }) else { return nil }
            candidate = "https://" + trimmed
        } else {
            // One bare word: a well-known site said by name ("youtube", "gmail").
            guard (2...40).contains(trimmed.count), trimmed.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "-" }) else { return nil }
            candidate = "https://" + trimmed + ".com"
        }
        guard let url = MacActionValidation.webURL(candidate) else { return nil }
        return url.absoluteString
    }
}
