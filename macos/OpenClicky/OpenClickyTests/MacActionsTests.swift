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
