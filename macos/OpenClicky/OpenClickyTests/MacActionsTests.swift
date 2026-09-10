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
        #expect(MacActionOutcome.permissionDenied(.documents).spokenSentence == "macOS hasn't granted access to your Documents folder yet — I asked for it.")
        #expect(MacActionOutcome.failed("disk full").spokenSentence == "That didn't work: disk full.")
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
            sendMediaKey: { action in recorder.mediaKeys.append(action) }
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

    @Test func othersToolsAreLeftAlone() {
        #expect(MacAction.parse(toolName: "send_to_agent", arguments: ["task": "x"]) == .notAFastAction)
        #expect(MacAction.parse(toolName: "point_at", arguments: [:]) == .notAFastAction)
    }
}
