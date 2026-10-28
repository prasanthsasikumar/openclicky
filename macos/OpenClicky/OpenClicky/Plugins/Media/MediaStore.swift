//
//  MediaStore.swift
//  OpenClicky
//
//  One per app: asks the player what is playing, every 2 s while a Now playing row is on screen,
//  every 5 s while the plugin is merely on (so the island can switch to it and the peek line can
//  name the track), never while off. Artwork is fetched once per track. Commands go straight to
//  the player and the next read follows at once.
//

import AppKit
import Combine
import Foundation

enum MediaCommand: Equatable, Sendable {
    case playPause
    /// Pause and play as themselves, for a voice "stop that song" that must never start playback.
    case pause
    case play
    case next
    case previous
    case seek(TimeInterval)
}

/// What a read found: a track, nothing (no player, nothing up), or a player that macOS will not
/// let OpenClicky talk to.
enum MediaReading: Equatable, Sendable {
    case nothing
    case track(MediaSnapshot)
    case notAllowed(MediaPlayer)
}

/// Where the track comes from: the players' scripting interfaces, or a test double.
protocol MediaSourcing: Sendable {
    func readNowPlaying() async -> MediaReading
    func perform(_ command: MediaCommand, on player: MediaPlayer) async
    func loadArtwork(for snapshot: MediaSnapshot) async -> NSImage?
}

@MainActor
final class MediaStore: ObservableObject {
    static let watchedInterval: TimeInterval = 2
    static let backgroundInterval: TimeInterval = 5

    @Published private(set) var snapshot: MediaSnapshot?
    @Published private(set) var artwork: NSImage?
    /// A player the user has not allowed OpenClicky to control (the empty state says how).
    @Published private(set) var refusedPlayer: MediaPlayer?

    private let source: any MediaSourcing
    private let now: () -> Date
    private var isEnabled = false
    private var watchers = 0
    private var timer: Timer?
    private var generation = 0
    private var artworkKey: String?
    /// One read at a time: a tick that lands mid-read asks for one more after it, never a pile.
    private var isRefreshing = false
    private var refreshAgain = false

    init(source: any MediaSourcing, now: @escaping () -> Date = Date.init) {
        self.source = source
        self.now = now
    }

    var isPlaying: Bool { snapshot?.isPlaying ?? false }

    /// "♪ title · artist" while playing, for the island's peek line.
    var nowPlayingLine: String? {
        guard let snapshot, snapshot.isPlaying else { return nil }
        return snapshot.artist.isEmpty ? "♪ \(snapshot.title)" : "♪ \(snapshot.title) · \(snapshot.artist)"
    }

    var isPolling: Bool { timer != nil }
    var pollInterval: TimeInterval { watchers > 0 ? Self.watchedInterval : Self.backgroundInterval }

    /// The plugin's switch: off forgets the track and stops asking.
    func setEnabled(_ enabled: Bool) {
        guard isEnabled != enabled else { return }
        isEnabled = enabled
        if enabled {
            Task { await refreshNow() }
        } else {
            generation += 1
            snapshot = nil
            artwork = nil
            artworkKey = nil
            refusedPlayer = nil
        }
        rebuildTimer()
    }

    func beginWatching() {
        watchers += 1
        if watchers == 1 {
            if isEnabled { Task { await refreshNow() } }
            rebuildTimer()
        }
    }

    func endWatching() {
        watchers = max(0, watchers - 1)
        if watchers == 0 { rebuildTimer() }
    }

    /// One read, serialised: a call that lands mid-read returns at once and the running read goes
    /// round once more. A read that outlives the switch going off is dropped.
    func refreshNow() async {
        guard isEnabled else { return }
        #if DEBUG
        if let debugSnapshot {
            if snapshot != debugSnapshot { snapshot = debugSnapshot }
            return
        }
        #endif
        if isRefreshing {
            refreshAgain = true
            return
        }
        isRefreshing = true
        defer { isRefreshing = false }
        repeat {
            refreshAgain = false
            await readOnce()
        } while refreshAgain && isEnabled
    }

    private func readOnce() async {
        let mine = generation
        let reading = await source.readNowPlaying()
        guard mine == generation, isEnabled else { return }
        let next: MediaSnapshot?
        switch reading {
        case .track(let snapshot):
            next = snapshot
            if refusedPlayer != nil { refusedPlayer = nil }
        case .nothing:
            next = nil
            if refusedPlayer != nil { refusedPlayer = nil }
        case .notAllowed(let player):
            next = nil
            if refusedPlayer != player { refusedPlayer = player }
        }
        if next != snapshot { snapshot = next }
        guard let next else {
            artwork = nil
            artworkKey = nil
            return
        }
        guard next.artworkKey != artworkKey else { return }
        // A new track: the old cover goes at once, the new one lands when it arrives, and a load
        // that outlives another track change is dropped (that track's own load follows).
        artworkKey = next.artworkKey
        artwork = nil
        let image = await source.loadArtwork(for: next)
        guard mine == generation, isEnabled, artworkKey == next.artworkKey else { return }
        artwork = image
    }

    func playPause() async { await perform(.playPause) }
    func next() async { await perform(.next) }
    func previous() async { await perform(.previous) }

    /// Seeks to a fraction of the track.
    func seek(to fraction: Double) async {
        guard let snapshot, snapshot.duration > 0 else { return }
        await perform(.seek(min(max(fraction, 0), 1) * snapshot.duration))
    }

    private func perform(_ command: MediaCommand) async {
        guard let player = snapshot?.player else { return }
        await source.perform(command, on: player)
        await refreshNow()
    }

    private func rebuildTimer() {
        timer?.invalidate()
        timer = nil
        guard isEnabled else { return }
        let timer = Timer(timeInterval: pollInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refreshNow() }
        }
        timer.tolerance = 0.5
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    #if DEBUG
    /// The UI tour's track, shown in place of the player's until cleared with nil.
    private var debugSnapshot: MediaSnapshot?
    func debugSetSnapshot(_ snapshot: MediaSnapshot?, artwork: NSImage?) {
        generation += 1
        debugSnapshot = snapshot
        self.snapshot = snapshot
        self.artwork = artwork
        artworkKey = snapshot?.artworkKey
    }
    #endif
}
