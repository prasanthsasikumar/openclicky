//
//  MediaPluginTests.swift
//  OpenClickyTests
//
//  The Now playing plugin: the script reply, the running position, the clock, the store and
//  which player is asked.
//

import AppKit
import Foundation
import Testing
@testable import OpenClicky

struct MediaPluginTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func line(_ fields: [String]) -> String { fields.joined(separator: MediaSnapshot.fieldSeparator) }

    @Test func theScriptsOneLineReplyBecomesASnapshot() throws {
        let reply = line(["playing", "Blue Hero", "The Band", "Album", "83.5", "245", "ABC123", ""])
        let snapshot = try #require(MediaSnapshot.parse(reply, player: .music, sampledAt: now))
        #expect(snapshot.isPlaying && snapshot.title == "Blue Hero" && snapshot.artist == "The Band")
        #expect(snapshot.position == 83.5 && snapshot.duration == 245 && snapshot.artworkKey == "ABC123" && snapshot.artworkURL == nil)
        let spotify = try #require(MediaSnapshot.parse(line(["paused", "T", "A", "", "10", "200", "spotify:track:x", "https://i.scdn.co/image/abc"]), player: .spotify, sampledAt: now))
        #expect(!spotify.isPlaying && spotify.artworkURL == URL(string: "https://i.scdn.co/image/abc"))
        #expect(MediaSnapshot.parse(line(["stopped", "", "", "", "0", "0", "", ""]), player: .music, sampledAt: now) == nil)
        #expect(MediaSnapshot.parse("garbage", player: .music, sampledAt: now) == nil)
    }

    @Test func positionRunsOnWhilePlayingAndFreezesWhenPaused() throws {
        var snapshot = try #require(MediaSnapshot.parse(line(["playing", "T", "A", "", "100", "120", "k", ""]), player: .music, sampledAt: now))
        #expect(snapshot.position(at: now.addingTimeInterval(15)) == 115)
        #expect(snapshot.position(at: now.addingTimeInterval(50)) == 120)   // clamped to the duration
        snapshot.isPlaying = false
        #expect(snapshot.position(at: now.addingTimeInterval(15)) == 100)
    }

    @Test func theClockReadsLikeAPlayer() {
        #expect(MediaFormatter.clock(5) == "0:05")
        #expect(MediaFormatter.clock(83.7) == "1:23")
        #expect(MediaFormatter.clock(3723) == "1:02:03")
    }

    @MainActor @Test func theStoreFollowsTheSourceFetchesArtworkOncePerTrackAndForwardsCommands() async throws {
        final class Source: MediaSourcing, @unchecked Sendable {
            var reply: MediaSnapshot?
            var commands: [MediaCommand] = []
            var artworkLoads = 0
            func readNowPlaying() async -> MediaReading { reply.map { .track($0) } ?? .nothing }
            func perform(_ command: MediaCommand, on player: MediaPlayer) async { commands.append(command) }
            func loadArtwork(for snapshot: MediaSnapshot) async -> NSImage? { artworkLoads += 1; return NSImage(size: NSSize(width: 1, height: 1)) }
        }
        let source = Source()
        let now = self.now
        let store = MediaStore(source: source, now: { now })
        store.setEnabled(true)
        #expect(store.isPolling && store.pollInterval == 5)
        store.beginWatching()
        #expect(store.pollInterval == 2)
        await store.refreshNow()
        #expect(store.snapshot == nil && store.artwork == nil && store.nowPlayingLine == nil)
        source.reply = try #require(MediaSnapshot.parse(line(["playing", "Blue Hero", "The Band", "", "1", "200", "k1", ""]), player: .spotify, sampledAt: now))
        await store.refreshNow()
        await store.refreshNow()
        #expect(store.isPlaying && store.nowPlayingLine == "♪ Blue Hero · The Band" && store.artwork != nil && source.artworkLoads == 1)
        source.reply?.isPlaying = false
        await store.refreshNow()
        #expect(!store.isPlaying && store.nowPlayingLine == nil && source.artworkLoads == 1)
        await store.playPause()
        await store.seek(to: 0.5)
        await store.next()
        #expect(source.commands == [.playPause, .seek(100), .next])
        source.reply = nil
        await store.refreshNow()
        #expect(store.snapshot == nil && store.artwork == nil)
        store.endWatching()
        #expect(store.pollInterval == 5)
        store.setEnabled(false)
        #expect(!store.isPolling && store.snapshot == nil)
    }

    @Test func thePlayingPlayerWinsAndSpotifyBreaksATie() {
        #expect(AppleScriptMediaSource.pick(running: [.music, .spotify], playing: [.music]) == .music)
        #expect(AppleScriptMediaSource.pick(running: [.music, .spotify], playing: [.music, .spotify]) == .spotify)
        #expect(AppleScriptMediaSource.pick(running: [.music], playing: []) == .music)
        #expect(AppleScriptMediaSource.pick(running: [], playing: []) == nil)
    }

    @Test func theScriptsAskEachPlayerInItsOwnWords() {
        #expect(AppleScriptMediaSource.nowPlayingScript(for: .spotify).contains("tell application \"Spotify\""))
        #expect(AppleScriptMediaSource.nowPlayingScript(for: .spotify).contains("/ 1000"))       // milliseconds → seconds
        #expect(AppleScriptMediaSource.nowPlayingScript(for: .music).contains("persistent ID"))
        #expect(AppleScriptMediaSource.commandScript(.seek(42), for: .music) == "tell application \"Music\" to set player position to 42")
        #expect(AppleScriptMediaSource.commandScript(.next, for: .spotify) == "tell application \"Spotify\" to next track")
        #expect(AppleScriptMediaSource.commandScript(.pause, for: .spotify) == "tell application \"Spotify\" to pause")
        #expect(AppleScriptMediaSource.commandScript(.play, for: .music) == "tell application \"Music\" to play")
    }

    @Test func thePeekLineNamesTheTrackAfterALiveSession() {
        #expect(NotchPeekLine.peekText(status: "s", agentLine: nil, mediaLine: "♪ T · A") == "♪ T · A")
        #expect(NotchPeekLine.peekText(status: "s", agentLine: "claude · thinking", mediaLine: "♪ T · A") == "claude · thinking")
        #expect(NotchPeekLine.peekText(status: "s", agentLine: nil, mediaLine: nil) == "s")
    }

    @MainActor @Test func overlappingRefreshesCoalesceAndKeepTheNewTracksArtwork() async throws {
        final class SlowSource: MediaSourcing, @unchecked Sendable {
            var reply: MediaSnapshot?
            var reads = 0
            func readNowPlaying() async -> MediaReading {
                reads += 1
                try? await Task.sleep(nanoseconds: 40_000_000)
                return reply.map { .track($0) } ?? .nothing
            }
            func perform(_ command: MediaCommand, on player: MediaPlayer) async {}
            func loadArtwork(for snapshot: MediaSnapshot) async -> NSImage? {
                try? await Task.sleep(nanoseconds: 40_000_000)
                return NSImage(size: NSSize(width: 2, height: 2))
            }
        }
        let source = SlowSource()
        let now = self.now
        source.reply = try #require(MediaSnapshot.parse(line(["playing", "T", "A", "", "1", "200", "k1", ""]), player: .spotify, sampledAt: now))
        let store = MediaStore(source: source, now: { now })
        store.setEnabled(true)                     // its own first read runs in the background
        for _ in 0..<100 where store.artwork == nil {   // wait for that read (and its artwork) to finish
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        #expect(store.artwork != nil)
        source.reply = try #require(MediaSnapshot.parse(line(["playing", "T2", "A", "", "1", "200", "k2", ""]), player: .spotify, sampledAt: now))
        source.reads = 0
        async let first: Void = store.refreshNow()
        async let second: Void = store.refreshNow()
        async let third: Void = store.refreshNow()
        _ = await (first, second, third)
        #expect(source.reads == 2)                 // one in flight, one more after it, never three
        #expect(store.artwork != nil)              // the artwork load was not thrown away
        store.setEnabled(false)
    }

    @MainActor @Test func aRefusedPlayerIsNamedSoTheUserCanAllowIt() async {
        struct Refusing: MediaSourcing {
            func readNowPlaying() async -> MediaReading { .notAllowed(.spotify) }
            func perform(_ command: MediaCommand, on player: MediaPlayer) async {}
            func loadArtwork(for snapshot: MediaSnapshot) async -> NSImage? { nil }
        }
        let store = MediaStore(source: Refusing(), now: { self.now })
        store.setEnabled(true)
        await store.refreshNow()
        #expect(store.snapshot == nil && store.refusedPlayer == .spotify)
        #expect(MediaIslandView.hint(refused: .spotify) == "allow openclicky to control spotify in system settings → privacy & security → automation")
    }
}
