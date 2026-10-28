//
//  AppleScriptMediaSource.swift
//  OpenClicky
//
//  Apple Music and Spotify through their scripting interfaces: one script per player returns the
//  track as one line, commands are one-liners, and a player is only ever asked while it is
//  running (never launched). macOS asks once per player whether OpenClicky may control it.
//

import AppKit
import Foundation

struct AppleScriptMediaSource: MediaSourcing {
    /// Scripts run here, off the main thread: a player can take a moment to answer.
    private static let queue = DispatchQueue(label: "openclicky.media.applescript", qos: .utility)
    /// Players whose script failed once (no permission, no dictionary): logged once, not every poll.
    nonisolated(unsafe) private static var loggedFailures: Set<MediaPlayer> = []

    init() {}

    /// A playing player first; Spotify before Music on a tie; else the first running one.
    static func pick(running: [MediaPlayer], playing: Set<MediaPlayer>) -> MediaPlayer? {
        if playing.contains(.spotify) && running.contains(.spotify) { return .spotify }
        if let first = running.first(where: { playing.contains($0) }) { return first }
        return running.first
    }

    static func runningPlayers() -> [MediaPlayer] {
        MediaPlayer.allCases.filter { !NSRunningApplication.runningApplications(withBundleIdentifier: $0.bundleIdentifier).isEmpty }
    }

    /// The eight fields joined by ASCII 30; "stopped" when nothing is up. Every field is bound to
    /// a variable first: a list of bare object references cannot be coerced to text.
    static func nowPlayingScript(for player: MediaPlayer) -> String {
        switch player {
        case .music:
            return """
            tell application "Music"
                if player state is stopped then return "stopped"
                try
                    set t to current track
                on error
                    return "stopped"
                end try
                set s to (player state as text)
                set n to (name of t as text)
                set a to (artist of t as text)
                set al to (album of t as text)
                set p to (player position as text)
                set d to (duration of t as text)
                set k to (persistent ID of t as text)
                set AppleScript's text item delimiters to (ASCII character 30)
                return {s, n, a, al, p, d, k, ""} as text
            end tell
            """
        case .spotify:
            return """
            tell application "Spotify"
                if player state is stopped then return "stopped"
                try
                    set t to current track
                on error
                    return "stopped"
                end try
                set s to (player state as text)
                set n to (name of t as text)
                set a to (artist of t as text)
                set al to (album of t as text)
                set p to (player position as text)
                set d to (((duration of t) / 1000) as text)
                set k to (id of t as text)
                set u to (artwork url of t as text)
                set AppleScript's text item delimiters to (ASCII character 30)
                return {s, n, a, al, p, d, k, u} as text
            end tell
            """
        }
    }

    static func commandScript(_ command: MediaCommand, for player: MediaPlayer) -> String {
        let app = player == .music ? "Music" : "Spotify"
        switch command {
        case .playPause: return "tell application \"\(app)\" to playpause"
        case .pause: return "tell application \"\(app)\" to pause"
        case .play: return "tell application \"\(app)\" to play"
        case .next: return "tell application \"\(app)\" to next track"
        case .previous: return "tell application \"\(app)\" to previous track"
        case .seek(let seconds): return "tell application \"\(app)\" to set player position to \(Int(seconds.rounded()))"
        }
    }

    // MARK: MediaSourcing

    /// Apple's "not authorized to send Apple events" error: the user said no (or has not been asked).
    static let notPermittedError = -1743

    func readNowPlaying() async -> MediaReading {
        let running = Self.runningPlayers()
        guard !running.isEmpty else { return .nothing }
        var snapshots: [MediaPlayer: MediaSnapshot] = [:]
        var refused: MediaPlayer?
        for player in running {
            let result = await Self.run(Self.nowPlayingScript(for: player), player: player)
            if result.errorNumber == Self.notPermittedError { refused = refused ?? player }
            if let reply = result.descriptor?.stringValue, let snapshot = MediaSnapshot.parse(reply, player: player, sampledAt: Date()) {
                snapshots[player] = snapshot
            }
        }
        let playing = Set(snapshots.filter { $0.value.isPlaying }.map(\.key))
        if let picked = Self.pick(running: running.filter { snapshots[$0] != nil }, playing: playing), let snapshot = snapshots[picked] {
            return .track(snapshot)
        }
        if let refused { return .notAllowed(refused) }
        return .nothing
    }

    /// Only while the player still runs: a `tell` to a player that just quit would relaunch it.
    func perform(_ command: MediaCommand, on player: MediaPlayer) async {
        guard Self.runningPlayers().contains(player) else { return }
        _ = await Self.run(Self.commandScript(command, for: player), player: player)
    }

    /// Music hands the artwork over as bytes; Spotify gives a URL.
    func loadArtwork(for snapshot: MediaSnapshot) async -> NSImage? {
        switch snapshot.player {
        case .music:
            guard Self.runningPlayers().contains(.music) else { return nil }
            let script = "tell application \"Music\" to return raw data of artwork 1 of current track"
            guard let data = await Self.run(script, player: .music).descriptor?.data else { return nil }
            return NSImage(data: data)
        case .spotify:
            guard let url = snapshot.artworkURL, let (data, _) = try? await URLSession.shared.data(from: url) else { return nil }
            return NSImage(data: data)
        }
    }

    struct ScriptResult {
        let descriptor: NSAppleEventDescriptor?
        let errorNumber: Int?
    }

    /// Every script waits at most three seconds for the player.
    static func withTimeout(_ source: String) -> String {
        "with timeout of 3 seconds\n\(source)\nend timeout"
    }

    /// Runs a script on the utility queue (one at a time; `loggedFailures` is only touched here);
    /// a failure is logged once per player and yields no descriptor.
    private static func run(_ source: String, player: MediaPlayer) async -> ScriptResult {
        await withCheckedContinuation { continuation in
            queue.async {
                var errorInfo: NSDictionary?
                let result = NSAppleScript(source: withTimeout(source))?.executeAndReturnError(&errorInfo)
                let errorNumber = (errorInfo?[NSAppleScript.errorNumber] as? NSNumber)?.intValue
                if result == nil, !loggedFailures.contains(player) {
                    loggedFailures.insert(player)
                    AppLog.append("now playing: \(player.displayName) script failed (\(errorNumber ?? 0)) \(errorInfo?[NSAppleScript.errorMessage] ?? "unknown")")
                }
                continuation.resume(returning: ScriptResult(descriptor: result, errorNumber: errorNumber))
            }
        }
    }
}
