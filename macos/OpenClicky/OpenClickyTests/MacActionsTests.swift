//
//  MacActionsTests.swift
//  OpenClickyTests
//
//  The fast lane's pure core: which folder a spoken location means, which names and URLs are
//  allowed through, and the one sentence the assistant says for every outcome.
//

import Foundation
import Testing
@testable import OpenClicky

struct MacActionsTests {

    @Test @MainActor func safariIsFoundWhereMacOS26KeepsIt() {
        // Since macOS 26 Safari lives in a cryptex (/System/Cryptexes/App/System/Applications), not in
        // /Applications: "open Safari" answered "I couldn't find an app called Safari" (2026-10-28).
        #expect(MacActionRunner.applicationURL(named: "Safari") != nil)
        #expect(MacActionRunner.applicationURL(named: "safari") != nil)
    }

    private let homeDirectory = URL(fileURLWithPath: "/Users/tester")
    private let workspaceDirectory = URL(fileURLWithPath: "/Users/tester/OpenClicky")

    @Test func locationsResolveToTheFoldersTheyName() {
        let resolve = { (location: MacActionLocation) in
            location.directoryURL(homeDirectory: self.homeDirectory, workspaceDirectory: self.workspaceDirectory).path
        }
        #expect(resolve(.desktop) == "/Users/tester/Desktop")
        #expect(resolve(.downloads) == "/Users/tester/Downloads")
        #expect(resolve(.documents) == "/Users/tester/Documents")
        #expect(resolve(.workspace) == "/Users/tester/OpenClicky")
        #expect(resolve(.home) == "/Users/tester")
    }

    @Test func anUnknownLocationIsRejectedRatherThanGuessed() {
        #expect(MacActionLocation.named("desktop") == .desktop)
        #expect(MacActionLocation.named("Desktop") == .desktop)
        #expect(MacActionLocation.named("  DOWNLOADS ") == .downloads)
        // The model invented a location: better to say so than to write somewhere the user didn't ask for.
        #expect(MacActionLocation.named("icloud") == nil)
        #expect(MacActionLocation.named("") == nil)
    }

    @Test func folderNamesThatCouldEscapeTheirLocationAreRefused() {
        #expect(MacActionValidation.folderName("  Test  ") == "Test")
        #expect(MacActionValidation.folderName("Tax 2026") == "Tax 2026")
        #expect(MacActionValidation.folderName("a/b") == nil)
        #expect(MacActionValidation.folderName("a:b") == nil)
        #expect(MacActionValidation.folderName("..") == nil)
        #expect(MacActionValidation.folderName(".hidden") == nil)
        #expect(MacActionValidation.folderName("") == nil)
        #expect(MacActionValidation.folderName("   ") == nil)
        #expect(MacActionValidation.folderName(String(repeating: "a", count: 256)) == nil)
        #expect(MacActionValidation.folderName(String(repeating: "a", count: 255)) != nil)
    }

    @Test func onlyWebURLsOpen() {
        #expect(MacActionValidation.webURL("https://example.com")?.absoluteString == "https://example.com")
        #expect(MacActionValidation.webURL(" http://example.com ")?.absoluteString == "http://example.com")
        // A file: or custom scheme would launch a handler of the model's choosing.
        #expect(MacActionValidation.webURL("file:///etc/passwd") == nil)
        #expect(MacActionValidation.webURL("x-apple.systempreferences:foo") == nil)
        #expect(MacActionValidation.webURL("not a url") == nil)
    }

    @Test func everyOutcomeHasOneSentenceToSay() {
        #expect(MacActionOutcome.openedApp("Spotify").spokenSentence == "Opened Spotify.")
        #expect(MacActionOutcome.appNotFound("Spotify").spokenSentence == "I couldn't find an app called Spotify.")
        #expect(MacActionOutcome.createdFolder(name: "Test", location: .desktop).spokenSentence == "Created Test on your Desktop.")
        #expect(MacActionOutcome.folderAlreadyExists(name: "Test", location: .desktop).spokenSentence == "Test is already on your Desktop.")
        #expect(MacActionOutcome.revealed(name: "Test", location: .desktop).spokenSentence == "Showing Test on your Desktop.")
        #expect(MacActionOutcome.nothingToReveal(name: "Test", location: .desktop).spokenSentence == "I couldn't find Test on your Desktop.")
        #expect(MacActionOutcome.openedURL("https://example.com").spokenSentence == "Opened it in your browser.")
        #expect(MacActionOutcome.volumeSet(40).spokenSentence == "Volume set to 40 percent.")
        #expect(MacActionOutcome.mediaControlled("playpause").spokenSentence == "Done.")
        #expect(MacActionOutcome.invalidName.spokenSentence == "That name has characters a folder can't have.")
        #expect(MacActionOutcome.unknownLocation("icloud").spokenSentence == "I don't know where icloud is.")
        #expect(MacActionOutcome.invalidURL.spokenSentence == "That doesn't look like a web address I can open.")
        #expect(MacActionOutcome.permissionDenied(.documents).spokenSentence == "macOS hasn't granted access to your Documents folder yet. I asked for it.")
        #expect(MacActionOutcome.failed("disk full").spokenSentence == "That didn't work: disk full.")
    }

    /// One of every outcome, for the checks that hold for all of them.
    static let sampleOutcomes: [MacActionOutcome] = [
        .openedApp("Spotify"), .appNotFound("Spotify"),
        .createdFolder(name: "Test", location: .desktop), .folderAlreadyExists(name: "Test", location: .desktop),
        .revealed(name: "Test", location: .desktop), .nothingToReveal(name: "Test", location: .desktop),
        .openedURL("https://example.com"), .volumeSet(40), .mediaControlled("playpause"),
        .invalidName, .invalidAppName, .unknownLocation("icloud"), .invalidURL, .permissionDenied(.documents), .failed("disk full"),
    ]

    @Test func everyOutcomeTellsTheModelSomethingAndLogsSomething() {
        for outcome in Self.sampleOutcomes {
            #expect(!outcome.toolOutput.isEmpty)
            #expect(!outcome.logDescription.isEmpty)
        }
    }

    @Test func onlyOpeningAPageLooksAtTheScreenAfterwardsForNow() {
        #expect(MacActionOutcome.openedURL("https://example.com").thenLooksAtScreen)
        #expect(!MacActionOutcome.openedApp("Spotify").thenLooksAtScreen)
        #expect(!MacActionOutcome.volumeSet(40).thenLooksAtScreen)
    }

    @Test func theSpokenLocationNameReadsNaturally() {
        #expect(MacActionLocation.desktop.spokenName == "your Desktop")
        #expect(MacActionLocation.workspace.spokenName == "your OpenClicky folder")
        #expect(MacActionLocation.home.spokenName == "your home folder")
    }
}

@MainActor
struct MacActionRunnerTests {

    /// A runner pointed at a temporary directory, with the AppKit calls replaced by recorders.
    private final class Recorder {
        var launchedApplications: [URL] = []
        var openedURLs: [URL] = []
        var revealedURLs: [URL] = []
        var volumeLevels: [Int] = []
        var mediaKeys: [String] = []
        var installedApplications: [String: URL] = ["spotify": URL(fileURLWithPath: "/Applications/Spotify.app")]
        var playerCommands: [MediaCommand] = []
        var playerTakesCommands = false
        var windowsToMinimize = 0
        var windowsToRestore = 0
        var emailApp: EmailApp = .mail
        var unread: Result<[UnreadMail], MailScriptError> = .success([])
        var mailDraftScripts: [String] = []
        var mailDraftError: MailScriptError? = nil
        var mailActivations = 0
        var tracks: [MusicTrack] = []
        var spotifyRunning = true
        var spotifyLaunches = true
        var playedURIs: [String] = []
        var playSucceeds = true
        var spotifySearches: [String] = []
        var spotifyInstalled = true
    }

    private func makeRunner(root: URL, recorder: Recorder) -> MacActionRunner {
        MacActionRunner(
            homeDirectory: root,
            workspaceDirectory: root.appendingPathComponent("OpenClicky"),
            findApplication: { name in recorder.installedApplications[name.lowercased()] },
            launchApplication: { url in recorder.launchedApplications.append(url) },
            openURL: { url in recorder.openedURLs.append(url) },
            revealInFinder: { url in recorder.revealedURLs.append(url) },
            setVolume: { level in recorder.volumeLevels.append(level) },
            sendMediaKey: { action in recorder.mediaKeys.append(action) },
            extras: MacActionRunner.Extras(
                controlPlayer: { command in
                    recorder.playerCommands.append(command)
                    return recorder.playerTakesCommands
                },
                minimizeAllWindows: { recorder.windowsToMinimize },
                restoreMinimizedWindows: { recorder.windowsToRestore },
                chooseEmailApp: { _ in recorder.emailApp },
                readUnreadMail: { recorder.unread },
                runMailDraft: { script in
                    recorder.mailDraftScripts.append(script)
                    return recorder.mailDraftError
                },
                activateMail: { recorder.mailActivations += 1 },
                lookUpTrack: { _ in recorder.tracks },
                spotifyIsInstalled: { recorder.spotifyInstalled },
                spotifyIsRunning: { recorder.spotifyRunning },
                launchSpotify: { recorder.spotifyLaunches },
                playTrack: { uri in
                    recorder.playedURIs.append(uri)
                    return recorder.playSucceeds
                },
                openSpotifySearch: { query in recorder.spotifySearches.append(query) }
            )
        )
    }

    private func makeTemporaryRoot() throws -> URL {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("mac-actions-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("Desktop"), withIntermediateDirectories: true)
        return root
    }

    @Test func openingAnInstalledAppLaunchesItAndSaysSo() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        let outcome = await runner.perform(.openApp(name: "Spotify"))
        #expect(outcome == .openedApp("Spotify"))
        #expect(recorder.launchedApplications == [URL(fileURLWithPath: "/Applications/Spotify.app")])
    }

    @Test func anAppThatIsNotInstalledIsReportedNotGuessedAt() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        let outcome = await runner.perform(.openApp(name: "Ableton"))
        #expect(outcome == .appNotFound("Ableton"))
        #expect(recorder.launchedApplications.isEmpty)
    }

    @Test func creatingAFolderWritesItAndTheSecondAttemptSaysItIsAlreadyThere() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let runner = makeRunner(root: root, recorder: Recorder())

        let first = await runner.perform(.createFolder(name: "Test", location: .desktop))
        #expect(first == .createdFolder(name: "Test", location: .desktop))
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("Desktop/Test").path))

        let second = await runner.perform(.createFolder(name: "Test", location: .desktop))
        #expect(second == .folderAlreadyExists(name: "Test", location: .desktop))
    }

    @Test func anUnsafeFolderNameIsRefusedBeforeAnythingIsWritten() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let runner = makeRunner(root: root, recorder: Recorder())

        #expect(await runner.perform(.createFolder(name: "../escape", location: .desktop)) == .invalidName)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("escape").path))
    }

    @Test func revealingSomethingThatIsNotThereSaysSo() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let runner = makeRunner(root: root, recorder: Recorder())

        #expect(await runner.perform(.revealInFinder(name: "Missing", location: .desktop)) == .nothingToReveal(name: "Missing", location: .desktop))
    }

    @Test func onlyWebURLsAreOpenedAndVolumeIsClamped() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        #expect(await runner.perform(.openURL(raw: "file:///etc/passwd")) == .invalidURL)
        #expect(recorder.openedURLs.isEmpty)

        #expect(await runner.perform(.openURL(raw: "https://example.com")) == .openedURL("https://example.com"))
        #expect(recorder.openedURLs.map(\.absoluteString) == ["https://example.com"])

        #expect(await runner.perform(.setVolume(level: 400)) == .volumeSet(100))
        #expect(await runner.perform(.setVolume(level: -5)) == .volumeSet(0))
        #expect(recorder.volumeLevels == [100, 0])
    }

    @Test func pauseGoesToTheRunningPlayerAndNeverToThePlayPauseKey() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        recorder.playerTakesCommands = true
        let runner = makeRunner(root: root, recorder: recorder)
        let outcome = await runner.perform(.mediaControl(action: "pause"))
        #expect(recorder.playerCommands == [.pause])
        #expect(recorder.mediaKeys.isEmpty)
        #expect(outcome == .playerControlled("pause"))
        #expect(outcome.spokenSentence == "Paused.")
        #expect(await runner.perform(.mediaControl(action: "play")) == .playerControlled("play"))
        #expect(recorder.playerCommands == [.pause, .play])
    }

    @Test func pauseWithNoPlayerRunningDoesNothingAndSaysSo() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        recorder.playerTakesCommands = false
        let runner = makeRunner(root: root, recorder: recorder)
        let outcome = await runner.perform(.mediaControl(action: "pause"))
        #expect(recorder.mediaKeys.isEmpty)
        #expect(outcome == .failed("nothing is playing"))
        // Resuming with no player is what the media key is for.
        #expect(await runner.perform(.mediaControl(action: "play")) == .mediaControlled("play"))
        #expect(recorder.mediaKeys == ["play"])
    }

    @Test func minimizeAllReportsTheCountAndRestoreBringsThemBack() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.windowsToMinimize = 3
        #expect(await runner.perform(.minimizeAll) == .minimizedWindows(3))
        #expect(MacActionOutcome.minimizedWindows(3).spokenSentence == "Minimized 3 windows.")
        recorder.windowsToMinimize = 0
        #expect(await runner.perform(.minimizeAll) == .minimizedWindows(0))
        #expect(MacActionOutcome.minimizedWindows(0).spokenSentence == "Nothing to minimize.")
        recorder.windowsToRestore = 3
        #expect(await runner.perform(.restoreWindows) == .restoredWindows(3))
        #expect(MacActionOutcome.restoredWindows(3).spokenSentence == "Brought 3 windows back.")
        #expect(MacActionOutcome.restoredWindows(0).spokenSentence == "I hadn't minimized anything.")
        #expect(!MacActionOutcome.minimizedWindows(3).thenLooksAtScreen)
    }

    @Test func checkingMailReadsTheListAndGmailOpensTheInboxThenLooks() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.emailApp = .mail
        recorder.unread = .success([UnreadMail(sender: "Ana", subject: "Hi")])
        #expect(await runner.perform(.checkEmail(app: nil)) == .unreadMail([UnreadMail(sender: "Ana", subject: "Hi")]))
        recorder.emailApp = .gmail
        let gmail = await runner.perform(.checkEmail(app: nil))
        #expect(gmail == .openedGmailInbox)
        #expect(recorder.openedURLs.last == GmailURLs.inbox)
        #expect(gmail.thenLooksAtScreen)
    }

    @Test func aRefusedMailScriptOpensMailAndSaysWhereToAllowIt() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.emailApp = .mail
        recorder.unread = .failure(.notAllowed)
        let outcome = await runner.perform(.checkEmail(app: nil))
        #expect(outcome == .mailUnavailable(.notAllowed))
        #expect(recorder.mailActivations == 1)
        #expect(outcome.spokenSentence.contains("Automation"))
    }

    @Test func draftingNeverSendsAndRoutesToTheChosenApp() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.emailApp = .gmail
        let outcome = await runner.perform(.draftEmail(to: "Ana", subject: "", body: "See you Friday", app: nil))
        #expect(outcome == .draftedEmail(app: .gmail, to: "Ana", bodyLength: 14))
        #expect(recorder.openedURLs.last == GmailURLs.compose(to: "Ana", subject: "", body: "See you Friday"))
        recorder.emailApp = .mail
        #expect(await runner.perform(.draftEmail(to: "ana@example.com", subject: "Hi", body: "x", app: "mail")) == .draftedEmail(app: .mail, to: "ana@example.com", bodyLength: 1))
        #expect(recorder.mailDraftScripts.count == 1)
        #expect(recorder.mailDraftScripts[0].contains("{address:\"ana@example.com\"}"))
        #expect(await runner.perform(.draftEmail(to: "<b>", subject: "", body: "x", app: nil)) == .invalidRecipient)
        recorder.mailDraftError = .notAllowed
        #expect(await runner.perform(.draftEmail(to: "ana@example.com", subject: "", body: "x", app: nil)) == .mailDraftFailed(.notAllowed))
    }

    @Test func playingASongLaunchesSpotifyLooksItUpAndPlaysByURI() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.spotifyRunning = false
        recorder.spotifyLaunches = true
        recorder.tracks = [MusicTrack(uri: "spotify:track:1", name: "Blinding Lights", artists: ["The Weeknd"])]
        let outcome = await runner.perform(.playMusic(query: "blinding lights"))
        #expect(outcome == .playingTrack(name: "Blinding Lights", artist: "The Weeknd"))
        #expect(outcome.spokenSentence == "Playing Blinding Lights by The Weeknd.")
        #expect(recorder.playedURIs == ["spotify:track:1"])
        recorder.playSucceeds = false
        #expect(await runner.perform(.playMusic(query: "blinding lights")) == .playFailed)
    }

    @Test func aFailedLookupOpensSpotifysSearchInstead() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.spotifyRunning = true
        recorder.tracks = []
        #expect(await runner.perform(.playMusic(query: "blinding lights")) == .openedSpotifySearch(query: "blinding lights"))
        #expect(recorder.spotifySearches == ["blinding lights"])
        #expect(recorder.playedURIs.isEmpty)
    }

    @Test func spotifyNotInstalledIsSaidInsteadOfStartingForever() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.spotifyInstalled = false
        recorder.spotifyRunning = false
        #expect(await runner.perform(.playMusic(query: "x")) == .spotifyNotInstalled)
        #expect(MacActionOutcome.spotifyNotInstalled.spokenSentence == "Spotify isn't installed on this Mac.")
    }

    @Test func onlyATrackURIIsEverPlayed() async throws {
        // The lookup's answer is not trusted: anything but a spotify:track: URI goes to Spotify's search.
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.tracks = [MusicTrack(uri: "https://evil.example/x", name: "x", artists: [])]
        #expect(await runner.perform(.playMusic(query: "x")) == .openedSpotifySearch(query: "x"))
        #expect(recorder.playedURIs.isEmpty)
    }

    @Test func pauseGoesToThePlayerThatIsPlayingNotJustTheFirstRunning() {
        #expect(MacActionRunner.playerForControl(.pause, playing: .music, running: [.spotify, .music]) == .music)
        #expect(MacActionRunner.playerForControl(.pause, playing: nil, running: [.spotify, .music]) == nil)
        #expect(MacActionRunner.playerForControl(.play, playing: nil, running: [.music, .spotify]) == .spotify)
        #expect(MacActionRunner.playerForControl(.play, playing: .music, running: [.spotify, .music]) == .music)
        #expect(MacActionRunner.playerForControl(.play, playing: nil, running: []) == nil)
    }

    @Test func spotifyNotAnsweringAfterLaunchIsSaidNotHidden() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        recorder.spotifyRunning = false
        recorder.spotifyLaunches = false
        #expect(await runner.perform(.playMusic(query: "x")) == .spotifyStarting)
        #expect(recorder.playedURIs.isEmpty)
    }

    @Test func aWebSearchOpensTheSearchPageAndLooksAfterwards() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)
        let outcome = await runner.perform(.webSearch(query: "cats", site: .web))
        #expect(recorder.openedURLs == [URL(string: "https://www.google.com/search?q=cats")!])
        #expect(outcome == .searched(query: "cats", site: .web))
        #expect(outcome.thenLooksAtScreen)
        #expect(outcome.spokenSentence == "Searching for cats.")
        #expect(outcome.toolOutput.contains("latest screenshot"))
    }

    @Test func revealingAnExistingFolderShowsItAndReportsSuccess() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        let desktopPath = root.appendingPathComponent("Desktop")
        try FileManager.default.createDirectory(at: desktopPath.appendingPathComponent("TestFolder"), withIntermediateDirectories: true)

        let outcome = await runner.perform(.revealInFinder(name: "TestFolder", location: .desktop))
        #expect(outcome == .revealed(name: "TestFolder", location: .desktop))
        #expect(recorder.revealedURLs == [desktopPath.appendingPathComponent("TestFolder")])
    }

    @Test func mediaControlWithAValidActionSendsTheKeyAndReportsSuccess() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        let outcome = await runner.perform(.mediaControl(action: "next"))
        #expect(outcome == .mediaControlled("next"))
        #expect(recorder.mediaKeys == ["next"])
    }

    @Test func mediaControlWithAnInvalidActionDoesNotSendAndReportsFailure() async throws {
        let root = try makeTemporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = Recorder()
        let runner = makeRunner(root: root, recorder: recorder)

        let outcome = await runner.perform(.mediaControl(action: "shuffle"))
        #expect((outcome as? MacActionOutcome) != nil)
        if case .failed = outcome {
            #expect(true)
        } else {
            #expect(false, "Expected .failed outcome")
        }
        #expect(recorder.mediaKeys.isEmpty)
    }
}

struct MacActionParsingTests {

    @Test func parsesEachToolIntoItsAction() {
        #expect(MacAction.parse(toolName: "open_app", arguments: ["name": "Spotify"]) == .action(.openApp(name: "Spotify")))
        #expect(MacAction.parse(toolName: "open_url", arguments: ["url": "https://example.com"]) == .action(.openURL(raw: "https://example.com")))
        #expect(MacAction.parse(toolName: "create_folder", arguments: ["name": "Test", "location": "desktop"])
                == .action(.createFolder(name: "Test", location: .desktop)))
        #expect(MacAction.parse(toolName: "reveal_in_finder", arguments: ["name": "Test", "location": "downloads"])
                == .action(.revealInFinder(name: "Test", location: .downloads)))
        #expect(MacAction.parse(toolName: "media_control", arguments: ["action": "next"]) == .action(.mediaControl(action: "next")))
    }

    @Test func minimizeAllAndRestoreParseWithNoArguments() {
        #expect(MacAction.parse(toolName: "minimize_all", arguments: [:]) == .action(.minimizeAll))
        #expect(MacAction.parse(toolName: "restore_windows", arguments: [:]) == .action(.restoreWindows))
    }

    @Test func emailToolsParseTheirArguments() {
        #expect(MacAction.parse(toolName: "check_email", arguments: [:]) == .action(.checkEmail(app: nil)))
        #expect(MacAction.parse(toolName: "check_email", arguments: ["app": "gmail"]) == .action(.checkEmail(app: "gmail")))
        #expect(MacAction.parse(toolName: "draft_email", arguments: ["to": "Ana", "body": "Hi"]) == .action(.draftEmail(to: "Ana", subject: "", body: "Hi", app: nil)))
        #expect(MacAction.parse(toolName: "draft_email", arguments: ["to": "<x>", "body": "Hi"]) == .badArguments(.invalidRecipient))
        #expect(MacAction.parse(toolName: "draft_email", arguments: ["to": "Ana", "body": ""]) == .badArguments(.failed("I need something to write")))
    }

    @Test func playMusicParsesItsQuery() {
        #expect(MacAction.parse(toolName: "play_music", arguments: ["query": "Blinding Lights"]) == .action(.playMusic(query: "Blinding Lights")))
        #expect(MacAction.parse(toolName: "play_music", arguments: ["query": ""]) == .badArguments(.invalidQuery))
    }

    @Test func webSearchParsesTheSiteAndDefaultsToTheWeb() {
        #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats"]) == .action(.webSearch(query: "cats", site: .web)))
        #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats", "site": "YouTube"]) == .action(.webSearch(query: "cats", site: .youtube)))
        #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats", "site": "bing"]) == .badArguments(.invalidQuery))
        #expect(MacAction.parse(toolName: "web_search", arguments: ["query": ""]) == .badArguments(.invalidQuery))
    }

    @Test func acceptsTheNumberOrTheNumericStringTheModelSends() {
        // The Realtime model sends integers, but occasionally quotes them.
        #expect(MacAction.parse(toolName: "set_volume", arguments: ["level": 40]) == .action(.setVolume(level: 40)))
        #expect(MacAction.parse(toolName: "set_volume", arguments: ["level": "40"]) == .action(.setVolume(level: 40)))
        #expect(MacAction.parse(toolName: "set_volume", arguments: ["level": "loud"]) == .badArguments(.failed("I need a volume between 0 and 100")))
    }

    @Test func aMissingOrUnknownLocationIsAnAnswerNotAGuess() {
        #expect(MacAction.parse(toolName: "create_folder", arguments: ["name": "Test", "location": "icloud"])
                == .badArguments(.unknownLocation("icloud")))
        // No location at all: the workspace is the one place that needs no permission prompt.
        #expect(MacAction.parse(toolName: "create_folder", arguments: ["name": "Test"])
                == .action(.createFolder(name: "Test", location: .workspace)))

        #expect(MacAction.parse(toolName: "reveal_in_finder", arguments: ["name": "Test", "location": "icloud"])
                == .badArguments(.unknownLocation("icloud")))
        // No location at all: the workspace is the one place that needs no permission prompt.
        #expect(MacAction.parse(toolName: "reveal_in_finder", arguments: ["name": "Test"])
                == .action(.revealInFinder(name: "Test", location: .workspace)))
    }

    @Test func openAppRejectsAnAppNameThatEscapesTheApplicationDirectories() {
        // A traversal name that could resolve outside /Applications, /System/Applications, ~/Applications.
        #expect(MacAction.parse(toolName: "open_app", arguments: ["name": "../System/Applications/Calculator"])
                == .badArguments(.invalidAppName))
        #expect(MacAction.parse(toolName: "open_app", arguments: ["name": "com.spotify.client:evil"])
                == .badArguments(.invalidAppName))
        #expect(MacAction.parse(toolName: "open_app", arguments: ["name": ".hidden"])
                == .badArguments(.invalidAppName))
    }

    @Test func othersToolsAreLeftAlone() {
        #expect(MacAction.parse(toolName: "send_to_agent", arguments: ["task": "x"]) == .notAFastAction)
        #expect(MacAction.parse(toolName: "point_at", arguments: [:]) == .notAFastAction)
    }
}

struct FastActionToolDefinitionTests {

    @Test func everyFastActionIsOfferedToTheModel() {
        let names = MacAction.realtimeToolDefinitions().compactMap { $0["name"] as? String }
        #expect(names == ["open_app", "open_url", "create_folder", "reveal_in_finder", "set_volume", "media_control", "web_search", "minimize_all", "check_email", "draft_email", "play_music", "restore_windows"])
    }

    @Test func createFolderDeclaresTheClosedLocationSet() throws {
        let definition = try #require(MacAction.realtimeToolDefinitions().first { $0["name"] as? String == "create_folder" })
        let parameters = try #require(definition["parameters"] as? [String: Any])
        let properties = try #require(parameters["properties"] as? [String: Any])
        let location = try #require(properties["location"] as? [String: Any])
        #expect(location["enum"] as? [String] == ["desktop", "downloads", "documents", "workspace", "home"])
        // Only the name is required; an unspecified location means the workspace.
        #expect(parameters["required"] as? [String] == ["name"])
    }

    @Test func theInstructionsPointSimpleActionsAtTheFastToolsAndRealWorkAtTheAgent() {
        let instructions = RealtimeVoiceClient.defaultInstructions
        #expect(instructions.contains("open_app"))
        #expect(instructions.contains("send_to_agent"))
    }

    /// A command is done, a question is taught, and email is never sent — in the Realtime prompt
    /// and in the ask lane's quick-actions line (the plans and the free account).
    @Test func theInstructionsDoCommandsAndTeachQuestions() {
        let instructions = RealtimeVoiceClient.defaultInstructions
        for tool in ["minimize_all", "web_search", "play_music", "check_email", "draft_email"] { #expect(instructions.contains(tool)) }
        #expect(instructions.contains(CompanionManager.commandOrQuestionInstruction))
        #expect(CompanionManager.commandOrQuestionInstruction.contains("Do not explain how they could do it themselves"))
        #expect(CompanionManager.commandOrQuestionInstruction.contains("never send email"))
        #expect(CompanionManager.accountToolsInstruction.contains(CompanionManager.commandOrQuestionInstruction))
        #expect(CompanionManager.accountToolsInstruction.contains("check your email"))
    }

    /// Nothing else keeps the schema's argument keys and `MacAction.parse`'s argument keys in
    /// agreement: a divergence there does not fail loudly, it just makes `parse` take its
    /// "argument absent" branch and hand the runner an empty name or URL. This walks every
    /// definition's own `properties` keys (never a hand-written list, so a future edit to a
    /// definition is caught here rather than needing a matching edit here), builds arguments from
    /// them, and asserts `parse` turns those exact values into the matching typed action.
    @Test func everyDeclaredArgumentKeyParsesIntoTheMatchingAction() throws {
        for definition in MacAction.realtimeToolDefinitions() {
            let toolName = try #require(definition["name"] as? String)
            let parameters = try #require(definition["parameters"] as? [String: Any])
            let properties = try #require(parameters["properties"] as? [String: Any])

            var arguments: [String: Any] = [:]
            for (key, rawPropertySchema) in properties {
                let propertySchema = try #require(rawPropertySchema as? [String: Any])
                if let enumValues = propertySchema["enum"] as? [String] {
                    arguments[key] = try #require(enumValues.first)
                } else if propertySchema["type"] as? String == "integer" {
                    arguments[key] = 55
                } else {
                    arguments[key] = "Test Value"
                }
            }

            guard case .action(let action) = MacAction.parse(toolName: toolName, arguments: arguments) else {
                Issue.record("\(toolName) did not parse its own schema's argument keys into an action")
                continue
            }

            switch action {
            case .openApp(let name):
                #expect(name == arguments["name"] as? String)
            case .openURL(let raw):
                #expect(raw == arguments["url"] as? String)
            case .createFolder(let name, let location):
                #expect(name == arguments["name"] as? String)
                #expect(location.rawValue == arguments["location"] as? String)
            case .revealInFinder(let name, let location):
                #expect(name == arguments["name"] as? String)
                #expect(location.rawValue == arguments["location"] as? String)
            case .setVolume(let level):
                #expect(level == arguments["level"] as? Int)
            case .mediaControl(let action):
                #expect(action == arguments["action"] as? String)
            case .webSearch(let query, let site):
                #expect(query == arguments["query"] as? String)
                #expect(site.rawValue == arguments["site"] as? String)
            case .minimizeAll, .restoreWindows:
                #expect(arguments.isEmpty)
            case .checkEmail(let app):
                #expect(app == arguments["app"] as? String)
            case .draftEmail(let to, let subject, let body, let app):
                #expect(to == arguments["to"] as? String)
                #expect(subject == arguments["subject"] as? String)
                #expect(body == arguments["body"] as? String)
                #expect(app == arguments["app"] as? String)
            case .playMusic(let query):
                #expect(query == arguments["query"] as? String)
            }
        }
    }
}
