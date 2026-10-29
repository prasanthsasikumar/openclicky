//
//  LocalAskIntentMapperTests.swift
//  OpenClickyTests
//
//  The on-device model's intent mapped onto the fast lane's tool calls, and the spoken app name
//  resolved against what is installed. Every guard that keeps a mishearing from doing the wrong
//  thing lives here.
//

import Testing
@testable import OpenClicky

/// No app is installed unless a test says so, the way a Mac with none of the named apps would answer.
private func map(_ intent: LocalAskIntent, transcript: String = "", hasWritingTarget: Bool = false, resolveApp: @escaping (String) -> String? = { _ in nil }) -> LocalAskMapping {
    LocalAskIntentMapper.map(intent, transcript: transcript, hasWritingTarget: hasWritingTarget, resolveApp: resolveApp)
}

struct LocalAskIntentMapperTests {

    @Test func openAppUsesTheResolvedName() {
        let mapping = map(LocalAskIntent(kind: .openApp, name: "chrome"), resolveApp: { $0 == "chrome" ? "Google Chrome" : nil })
        #expect(mapping == .quickAction(toolName: "open_app", arguments: ["name": "Google Chrome"]))
    }

    @Test(arguments: ["", "it", "that", "this one", "the app", "the thing i had open before lunch"])
    func openAppWithNothingUsableGoesToTheCloud(name: String) {
        #expect(map(LocalAskIntent(kind: .openApp, name: name)) == .cloud(reason: "no app named"))
    }

    @Test func openAppNobodyHasGoesToTheCloud() {
        #expect(map(LocalAskIntent(kind: .openApp, name: "photoshop"), resolveApp: { _ in nil }) == .cloud(reason: "no app match"))
    }

    @Test(arguments: [("youtube", "https://youtube.com"), ("bbc.co.uk", "https://bbc.co.uk"), ("www.gmail.com", "https://www.gmail.com"), ("https://apple.com", "https://apple.com")])
    func websitesBecomeHTTPSURLs(spoken: String, url: String) {
        #expect(map(LocalAskIntent(kind: .openWebsite, name: spoken)) == .quickAction(toolName: "open_url", arguments: ["url": url]))
    }

    @Test func aBareWebsiteWordThatIsAnInstalledAppOpensTheApp() {
        // "open safari" came back as openWebsite "safari", which would have opened safari.com (2026-10-28).
        let mapping = map(LocalAskIntent(kind: .openWebsite, name: "safari"), resolveApp: { $0 == "safari" ? "Safari" : nil })
        #expect(mapping == .quickAction(toolName: "open_app", arguments: ["name": "Safari"]))
        // A real host is a website even when an app shares its name.
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "spotify.com"), resolveApp: { _ in "Spotify" }) == .quickAction(toolName: "open_url", arguments: ["url": "https://spotify.com"]))
    }

    @Test func aBareWebsiteWordOnlyBecomesAnAppOnAnExactOrAliasMatch() {
        // "open google" must not open Google Chrome, nor "open amazon" Amazon Music: the resolver's
        // one-word rule is for openApp, where the person said they want an app (review, 2026-10-28).
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "google"), resolveApp: { _ in "Google Chrome" }) == .quickAction(toolName: "open_url", arguments: ["url": "https://google.com"]))
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "amazon"), resolveApp: { _ in "Amazon Music" }) == .quickAction(toolName: "open_url", arguments: ["url": "https://amazon.com"]))
        // An alias ("mail" → Mail) and an exact name still open the app.
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "mail"), resolveApp: { _ in "Mail" }) == .quickAction(toolName: "open_app", arguments: ["name": "Mail"]))
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "Spotify"), resolveApp: { _ in "Spotify" }) == .quickAction(toolName: "open_app", arguments: ["name": "Spotify"]))
    }

    @Test func aWebsiteThatIsNotAHostGoesToTheCloud() {
        #expect(map(LocalAskIntent(kind: .openWebsite, name: "the news")) == .cloud(reason: "not a website"))
    }

    @Test func createFolderCarriesNameAndLocation() {
        #expect(map(LocalAskIntent(kind: .createFolder, name: "Taxes", location: "desktop")) == .quickAction(toolName: "create_folder", arguments: ["name": "Taxes", "location": "desktop"]))
    }

    @Test func createFolderWithoutALocationLeavesItToTheFastLane() {
        #expect(map(LocalAskIntent(kind: .createFolder, name: "Taxes")) == .quickAction(toolName: "create_folder", arguments: ["name": "Taxes"]))
    }

    @Test func showFolderWithoutAnItemGoesToTheCloud() {
        #expect(map(LocalAskIntent(kind: .showFolder, location: "downloads")) == .cloud(reason: "no item named"))
    }

    @Test func volumeNeedsANumber() {
        #expect(map(LocalAskIntent(kind: .setVolume, volumeLevel: 30)) == .quickAction(toolName: "set_volume", arguments: ["level": "30"]))
        #expect(map(LocalAskIntent(kind: .setVolume, volumeLevel: -1)) == .cloud(reason: "no volume level"))
        #expect(map(LocalAskIntent(kind: .setVolume, volumeLevel: 140)) == .cloud(reason: "no volume level"))
    }

    @Test(arguments: [("thirty", 30), ("fifty percent", 50), ("mute", 0), ("full volume", 100), ("max", 100)])
    func aSpokenVolumeLevelIsReadWhenTheModelLeftItAsWords(spoken: String, level: Int) {
        // The model sometimes puts "thirty" in name and leaves volumeLevel at -1 (seen live 2026-10-28).
        #expect(map(LocalAskIntent(kind: .setVolume, name: spoken, volumeLevel: -1)) == .quickAction(toolName: "set_volume", arguments: ["level": String(level)]))
    }

    @Test(arguments: [("play", "play"), ("pause", "pause"), ("next", "next"), ("previous", "previous")])
    func playbackMapsOntoMediaControl(spoken: String, action: String) {
        // play and pause, never playpause: the play/pause key goes to whatever macOS last played.
        #expect(map(LocalAskIntent(kind: .playback, playback: spoken)) == .quickAction(toolName: "media_control", arguments: ["action": action]))
    }

    @Test(arguments: ["pause", "pause the music", "next song", "skip this track", "go back to the previous song", "resume playing"])
    func aBarePlaybackCommandStaysOnThisMac(transcript: String) {
        #expect(map(LocalAskIntent(kind: .playback, playback: "next"), transcript: transcript) == .quickAction(toolName: "media_control", arguments: ["action": "next"]))
    }

    @Test(arguments: [
        // Seen live 2026-10-29: the model said playback with nothing named, and the play key
        // resumed a paused YouTube video in Chrome instead of Spotify.
        "Open Spotify and play the first song in my favourite list",
        "play my liked songs",
        "play Blinding Lights by The Weeknd",
        "play something on youtube",
        "pause this and open mail",
    ])
    func playbackThatAsksForMoreGoesToTheCloud(transcript: String) {
        #expect(map(LocalAskIntent(kind: .playback, playback: "play"), transcript: transcript) == .cloud(reason: "playback asks for more"))
    }

    @Test func playbackNamingAnAppGoesToTheCloud() {
        #expect(map(LocalAskIntent(kind: .playback, name: "Spotify", playback: "play"), transcript: "play spotify") == .cloud(reason: "playback asks for more"))
    }

    @Test func rewriteNeedsAWritingTarget() {
        #expect(map(LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), hasWritingTarget: true) == .rewrite(instruction: "shorter"))
        #expect(map(LocalAskIntent(kind: .rewriteText, rewriteInstruction: "shorter"), hasWritingTarget: false) == .cloud(reason: "no text to rewrite"))
    }

    @Test func needsCloudIsCloud() {
        #expect(map(LocalAskIntent(kind: .needsCloud)) == .cloud(reason: "model declined"))
    }

    @Test func everyQuickActionMappingParsesInTheFastLane() {
        let mappings: [LocalAskMapping] = [
            map(LocalAskIntent(kind: .openApp, name: "Safari"), resolveApp: { $0 }),
            map(LocalAskIntent(kind: .openWebsite, name: "youtube")),
            map(LocalAskIntent(kind: .createFolder, name: "Taxes", location: "desktop")),
            map(LocalAskIntent(kind: .showFolder, name: "Taxes", location: "desktop")),
            map(LocalAskIntent(kind: .setVolume, volumeLevel: 30)),
            map(LocalAskIntent(kind: .playback, playback: "next")),
        ]
        for mapping in mappings {
            guard case .quickAction(let toolName, let arguments) = mapping else { Issue.record("expected a quick action, got \(mapping)"); continue }
            guard case .action = MacAction.parse(toolName: toolName, arguments: arguments) else { Issue.record("\(toolName) did not parse"); continue }
        }
    }
}

private let installed = ["Safari", "Google Chrome", "Microsoft Word", "Mail", "System Settings", "Visual Studio Code", "Final Cut Pro"]

struct InstalledAppResolverTests {

    @Test(arguments: [("safari", "Safari"), ("google chrome", "Google Chrome"), ("chrome", "Google Chrome"), ("word", "Microsoft Word"), ("final cut", "Final Cut Pro"), ("vs code", "Visual Studio Code"), ("email", "Mail"), ("my mail", "Mail"), ("settings", "System Settings"), ("the browser", "Google Chrome")])
    func spokenNamesResolveToInstalledApps(spoken: String, expected: String) {
        #expect(InstalledAppResolver.resolve(spoken: spoken, installedAppNames: installed, defaultBrowserName: "Google Chrome") == expected)
    }

    @Test(arguments: ["photoshop", "the thing", "pro"])
    func unknownNamesDoNotResolve(spoken: String) {
        #expect(InstalledAppResolver.resolve(spoken: spoken, installedAppNames: installed, defaultBrowserName: nil) == nil)
    }
}
