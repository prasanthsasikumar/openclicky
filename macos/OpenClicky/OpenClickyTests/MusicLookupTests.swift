//
//  MusicLookupTests.swift
//  OpenClickyTests
//
//  "Play Blinding Lights": where the song lookup goes, which result is played, and how the two
//  result shapes (the backend's and Spotify's own) decode. The network and Spotify itself are not
//  tested here.
//

import Foundation
import Testing
@testable import OpenClicky

struct MusicLookupTests {
    @Test func ownCredentialsGoToSpotifyAndOtherwiseTheBackend() {
        let backend = URL(string: "https://api.example")!
        #expect(MusicLookupRoute.route(credentials: "id:secret", backendURL: backend) == .spotify(clientId: "id", clientSecret: "secret"))
        #expect(MusicLookupRoute.route(credentials: " ", backendURL: backend) == .backend(backend))
        #expect(MusicLookupRoute.route(credentials: "no-colon", backendURL: backend) == .backend(backend))
        #expect(MusicLookupRoute.route(credentials: nil, backendURL: nil) == .none)
        #expect(MusicLookupRoute.route(credentials: "id:secret", backendURL: nil).logName == "spotify")
        #expect(MusicLookupRoute.backend(backend).logName == "backend")
    }

    @Test func theTrackSharingAWordWithTheRequestWinsElseTheFirst() {
        let tracks = [
            MusicTrack(uri: "a", name: "Blinding Lights (Remix)", artists: ["Someone"]),
            MusicTrack(uri: "b", name: "Blinding Lights", artists: ["The Weeknd"]),
        ]
        #expect(MusicTrackPicker.pick(tracks, for: "blinding lights by the weeknd")?.uri == "b")
        #expect(MusicTrackPicker.pick(tracks, for: "that song")?.uri == "a")
        #expect(MusicTrackPicker.pick([], for: "x") == nil)
    }

    @Test func bothResultShapesDecode() throws {
        let backend = Data(#"{"tracks":[{"uri":"spotify:track:1","name":"One","artists":["A"],"album":"X","durationMs":1}]}"#.utf8)
        let spotify = Data(#"{"tracks":{"items":[{"uri":"spotify:track:1","name":"One","artists":[{"name":"A"}],"album":{"name":"X"}}]}}"#.utf8)
        let expected = [MusicTrack(uri: "spotify:track:1", name: "One", artists: ["A"])]
        #expect(try MusicLookup.decodeBackendResults(backend) == expected)
        #expect(try MusicLookup.decodeSpotifyResults(spotify) == expected)
    }

    @Test func theTokenCacheIsPerClientIdSoNewCredentialsAreReallyTested() {
        let cache = MusicLookup.TokenCache()
        cache.store("tok-a", clientId: "a", expiresIn: 3600)
        #expect(cache.current(clientId: "a") == "tok-a")
        #expect(cache.current(clientId: "b") == nil)
        cache.store("tok-short", clientId: "c", expiresIn: 30)
        #expect(cache.current(clientId: "c") == nil)
    }

    @Test func theSpotifySearchURIIsEncoded() {
        #expect(SpotifyPlayer.searchURL(for: "c++ & more").absoluteString == "spotify:search:c%2B%2B%20%26%20more")
    }
}
