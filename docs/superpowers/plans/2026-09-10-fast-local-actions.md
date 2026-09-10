# Fast Local Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make "open Spotify" and "create a folder on my desktop" finish in about two seconds instead of twelve, by executing a fixed set of local actions natively on the Realtime voice session instead of routing them through the Codex agent.

**Architecture:** The `gpt-realtime` session already dispatches one native tool (`point_at`) inside `RealtimeVoiceClient.handleToolCall`. Simple local actions become more tools of the same shape: typed arguments, executed in Swift, returning one plain sentence the model speaks. `send_to_agent` is untouched and still handles everything else. A separate, independent change attaches the Computer Use MCP server that the agent's instructions already assume exists, which is what makes the agent lane's timing predictable.

**Tech Stack:** Swift 5 / SwiftUI / AppKit (macOS 14.2+), Swift Testing (`@Test`/`#expect`), TypeScript + vitest for the agent CLI, Codex CLI 0.152.1.

**Spec:** `docs/superpowers/specs/2026-09-10-fast-local-actions-design.md`

## Global Constraints

- Tools never accept a filesystem path. Locations come from the closed enum `desktop | downloads | documents | workspace | home`; an unknown value is an error, never a fallback.
- No shell, no `osascript`, no AppleScript in the fast lane except `set_volume`, whose only mechanism on macOS is `osascript -e "set volume output volume <level>"` with an integer clamped to 0–100 in Swift before it is interpolated.
- Every tool returns exactly one sentence, in the words given in Task 1's `spokenSentence`. That string is what the assistant says out loud; treat it as product copy, not as a log line.
- Naming: follow `macos/OpenClicky/AGENTS.md` — full words, no abbreviations, no single-character names. `element` not `el`, `applicationURL` not `appURL`.
- Do not fix the known non-blocking warnings (Swift 6 concurrency, deprecated `onChange`).
- Swift tests are Swift Testing (`import Testing`, `@Test`, `#expect`), not XCTest.
- Run Swift tests with `xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests` from `macos/OpenClicky`. Quit the running app first (`pkill -x OpenClicky`), or the test run fails to install.
- Agent tests: `npm test -w agent` from the repo root.

---

### Task 1: MacActions — locations, validation, and the spoken sentences

The pure core: no AppKit, no filesystem, no side effects. Everything here is directly testable, and Task 2 builds the executor on top of it.

**Files:**
- Create: `macos/OpenClicky/OpenClicky/MacActions.swift`
- Test: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum MacActionLocation: String, CaseIterable { case desktop, downloads, documents, workspace, home }`
  - `static func MacActionLocation.named(_ raw: String) -> MacActionLocation?`
  - `func MacActionLocation.directoryURL(homeDirectory: URL, workspaceDirectory: URL) -> URL`
  - `var MacActionLocation.spokenName: String`
  - `enum MacActionOutcome: Equatable` with `var spokenSentence: String`
  - `enum MacActionValidation { static func folderName(_ raw: String) -> String?; static func webURL(_ raw: String) -> URL? }`

- [ ] **Step 1: Write the failing test**

Create `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`:

```swift
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
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionsTests 2>&1 | tail -20
```

Expected: build failure, `cannot find 'MacActionLocation' in scope`.

- [ ] **Step 3: Write the implementation**

Create `macos/OpenClicky/OpenClicky/MacActions.swift`:

```swift
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
    case invalidName
    case unknownLocation(String)
    case invalidURL
    case permissionDenied(MacActionLocation)
    case failed(String)

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
        case .invalidName:
            return "That name has characters a folder can't have."
        case .unknownLocation(let raw):
            return "I don't know where \(raw) is."
        case .invalidURL:
            return "That doesn't look like a web address I can open."
        case .permissionDenied(let location):
            return "macOS hasn't granted access to \(location.spokenName) yet — I asked for it."
        case .failed(let reason):
            return "That didn't work: \(reason)."
        }
    }
}

/// Argument checks. Both return nil for anything the fast lane will not act on.
enum MacActionValidation {

    /// A folder name the model spoke, or nil when it could reach outside the location it was given.
    /// `/` and `:` are the two separators macOS honours; a leading dot would create something the
    /// user cannot see; 255 bytes is the filesystem's own limit.
    static func folderName(_ raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 255 else { return nil }
        guard !trimmed.contains("/"), !trimmed.contains(":") else { return nil }
        guard !trimmed.hasPrefix(".") else { return nil }
        return trimmed
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
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionsTests 2>&1 | grep -E "passed|failed|TEST (SUCCEEDED|FAILED)"
```

Expected: 6 tests passed, `** TEST SUCCEEDED **`.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/MacActions.swift macos/OpenClicky/OpenClickyTests/MacActionsTests.swift
git commit -m "feat(mac): locations, validation and spoken copy for the fast action lane"
```

---

### Task 2: MacActionRunner — performing the actions

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift` (append the runner)
- Modify: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift` (append a runner suite)

**Interfaces:**
- Consumes: `MacActionLocation`, `MacActionOutcome`, `MacActionValidation` from Task 1.
- Produces:
  - `enum MacAction: Equatable` with cases `openApp(name:)`, `openURL(raw:)`, `createFolder(name:location:)`, `revealInFinder(name:location:)`, `setVolume(level:)`, `mediaControl(action:)`
  - `@MainActor final class MacActionRunner` with `init(homeDirectory:workspaceDirectory:findApplication:launchApplication:openURL:setVolume:sendMediaKey:)` and `func perform(_ action: MacAction) async -> MacActionOutcome`
  - `static func MacActionRunner.live(workspaceDirectory: URL) -> MacActionRunner`

- [ ] **Step 1: Write the failing test**

Append to `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`:

```swift
@MainActor
struct MacActionRunnerTests {

    /// A runner pointed at a temporary directory, with the AppKit calls replaced by recorders.
    private final class Recorder {
        var launchedApplications: [URL] = []
        var openedURLs: [URL] = []
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
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionRunnerTests 2>&1 | tail -20
```

Expected: build failure, `cannot find 'MacActionRunner' in scope`.

- [ ] **Step 3: Write the implementation**

Append to `macos/OpenClicky/OpenClicky/MacActions.swift`:

```swift
import AppKit

/// One action the Realtime model asked for, already parsed and typed.
enum MacAction: Equatable {
    case openApp(name: String)
    case openURL(raw: String)
    case createFolder(name: String, location: MacActionLocation)
    case revealInFinder(name: String, location: MacActionLocation)
    case setVolume(level: Int)
    case mediaControl(action: String)
}

/// Performs the fast-lane actions. The AppKit calls are injected so the whole runner can be tested
/// against a temporary directory without launching apps or moving the user's volume.
@MainActor
final class MacActionRunner {

    private let homeDirectory: URL
    private let workspaceDirectory: URL
    private let findApplication: (String) -> URL?
    private let launchApplication: (URL) -> Void
    private let openURL: (URL) -> Void
    private let setVolume: (Int) -> Void
    private let sendMediaKey: (String) -> Void

    init(
        homeDirectory: URL,
        workspaceDirectory: URL,
        findApplication: @escaping (String) -> URL?,
        launchApplication: @escaping (URL) -> Void,
        openURL: @escaping (URL) -> Void,
        setVolume: @escaping (Int) -> Void,
        sendMediaKey: @escaping (String) -> Void
    ) {
        self.homeDirectory = homeDirectory
        self.workspaceDirectory = workspaceDirectory
        self.findApplication = findApplication
        self.launchApplication = launchApplication
        self.openURL = openURL
        self.setVolume = setVolume
        self.sendMediaKey = sendMediaKey
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
            NSWorkspace.shared.activateFileViewerSelecting([target])
            return .revealed(name: name, location: location)

        case .setVolume(let requestedLevel):
            let level = min(100, max(0, requestedLevel))
            setVolume(level)
            return .volumeSet(level)

        case .mediaControl(let mediaAction):
            guard ["playpause", "next", "previous"].contains(mediaAction) else {
                return .failed("I don't know how to \(mediaAction)")
            }
            sendMediaKey(mediaAction)
            return .mediaControlled(mediaAction)
        }
    }

    /// The runner the app uses: real LaunchServices, real Finder, real volume.
    static func live(workspaceDirectory: URL) -> MacActionRunner {
        MacActionRunner(
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
            setVolume: { level in
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
                process.arguments = ["-e", "set volume output volume \(level)"]
                try? process.run()
            },
            sendMediaKey: { action in Self.postMediaKey(action) }
        )
    }

    /// Looks for `<name>.app` in the standard application folders. LaunchServices has no display-name
    /// lookup that does not also need a bundle identifier, and asking Spotlight would be slower than
    /// the action itself.
    private static func applicationURL(named name: String) -> URL? {
        let searchDirectories = [
            URL(fileURLWithPath: "/Applications"),
            URL(fileURLWithPath: "/System/Applications"),
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
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionRunnerTests 2>&1 | grep -E "passed|failed|TEST (SUCCEEDED|FAILED)"
```

Expected: 6 tests passed, `** TEST SUCCEEDED **`.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/MacActions.swift macos/OpenClicky/OpenClickyTests/MacActionsTests.swift
git commit -m "feat(mac): run the fast local actions natively"
```

---

### Task 3: Parse the model's tool call into a MacAction

Keeping parsing separate from the socket is what makes it testable — `handleToolCall` itself can never be unit-tested, because it needs a live Realtime connection.

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift` (append the parser)
- Modify: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift` (append a parser suite)

**Interfaces:**
- Consumes: `MacAction`, `MacActionLocation` from Tasks 1–2.
- Produces: `enum MacActionParseResult: Equatable { case action(MacAction); case notAFastAction; case badArguments(MacActionOutcome) }` and `static func MacAction.parse(toolName: String, arguments: [String: Any]) -> MacActionParseResult`.

- [ ] **Step 1: Write the failing test**

Append to `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`:

```swift
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
    }

    @Test func othersToolsAreLeftAlone() {
        #expect(MacAction.parse(toolName: "send_to_agent", arguments: ["task": "x"]) == .notAFastAction)
        #expect(MacAction.parse(toolName: "point_at", arguments: [:]) == .notAFastAction)
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionParsingTests 2>&1 | tail -20
```

Expected: build failure, `type 'MacAction' has no member 'parse'`.

- [ ] **Step 3: Write the implementation**

Append to `macos/OpenClicky/OpenClicky/MacActions.swift`:

```swift
/// What the tool call turned out to be.
enum MacActionParseResult: Equatable {
    case action(MacAction)
    /// Not one of the fast-lane tools: the caller's existing handling applies.
    case notAFastAction
    /// A fast-lane tool with arguments that cannot be acted on; the outcome carries what to say.
    case badArguments(MacActionOutcome)
}

extension MacAction {

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
            return .action(.openApp(name: string("name")))

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

        default:
            return .notAFastAction
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionParsingTests 2>&1 | grep -E "passed|failed|TEST (SUCCEEDED|FAILED)"
```

Expected: 4 tests passed, `** TEST SUCCEEDED **`.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/MacActions.swift macos/OpenClicky/OpenClickyTests/MacActionsTests.swift
git commit -m "feat(mac): parse fast-action tool calls off the Realtime session"
```

---

### Task 4: Offer the tools on the Realtime session and dispatch them

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift` — tool definitions in the `session.update` payload (around line 254–282), the instructions string (line 129–142), the callback property (near line 79), and the dispatch in `handleToolCall` (around line 645–655)
- Modify: `macos/OpenClicky/OpenClicky/CompanionManager.swift:212` — supply the live runner
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyApp.swift:83` and `:146` — the two smoke-test stubs
- Test: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift` (append)

**Interfaces:**
- Consumes: `MacAction.parse`, `MacActionRunner.live`, `MacActionOutcome.spokenSentence`.
- Produces: `static func RealtimeVoiceClient.fastActionToolDefinitions() -> [[String: Any]]` and `var onMacAction: ((MacAction) async -> MacActionOutcome)?`.

- [ ] **Step 1: Write the failing test**

Append to `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`:

```swift
struct FastActionToolDefinitionTests {

    @Test func everyFastActionIsOfferedToTheModel() {
        let names = RealtimeVoiceClient.fastActionToolDefinitions().compactMap { $0["name"] as? String }
        #expect(names == ["open_app", "open_url", "create_folder", "reveal_in_finder", "set_volume", "media_control"])
    }

    @Test func createFolderDeclaresTheClosedLocationSet() throws {
        let definition = try #require(RealtimeVoiceClient.fastActionToolDefinitions().first { $0["name"] as? String == "create_folder" })
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
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd macos/OpenClicky && xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/FastActionToolDefinitionTests 2>&1 | tail -20
```

Expected: build failure, `type 'RealtimeVoiceClient' has no member 'fastActionToolDefinitions'`.

- [ ] **Step 3: Write the implementation**

In `RealtimeVoiceClient.swift`, add the callback next to `onAgentTask` (line 79):

```swift
    /// Performs a fast local action (open an app, make a folder…) and returns the sentence to say.
    /// Main-actor isolated: `MacActionRunner` touches AppKit.
    var onMacAction: (@MainActor (MacAction) async -> MacActionOutcome)?
```

Add the tool definitions as a static function on `RealtimeVoiceClient` (put it directly above the function that builds the `session.update` payload):

```swift
    /// The local actions OpenClicky performs itself. Typed arguments only: `location` is a closed
    /// set and there is no path or command anywhere in the schema, so a mishearing cannot widen
    /// what the fast lane is able to do. See MacActions.swift.
    static func fastActionToolDefinitions() -> [[String: Any]] {
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
                "description": "Play, pause, or skip whatever is playing.",
                "parameters": ["type": "object", "properties": ["action": ["type": "string", "enum": ["playpause", "next", "previous"]]], "required": ["action"]],
            ],
        ]
    }
```

Change the `tools` line in the same payload from:

```swift
                "tools": [tool, pointTool],
```

to:

```swift
                "tools": [tool, pointTool] + Self.fastActionToolDefinitions(),
```

In `defaultInstructions`, replace the sentence beginning "For anything that requires doing work on the computer" with:

```swift
    Do simple local things yourself with the fast tools — open_app, open_url, create_folder, \
    reveal_in_finder, set_volume, media_control — and then say the one sentence they give back. \
    For anything bigger — editing files or code, running commands, using integrations, research, \
    multi-step tasks — first say one short sentence acknowledging it, then call the send_to_agent \
    tool with a clear, self-contained task, and afterwards tell the user in one sentence what happened. \
    Never pretend work was done without the tool. Do not read file paths aloud character by character.
```

In `handleToolCall`, add the fast-action branch before the existing `switch name` (so the six tools never fall through to `default`):

```swift
        switch MacAction.parse(toolName: name, arguments: arguments) {
        case .action(let action):
            let startedAt = Date()
            let outcome = await onMacAction?(action) ?? .failed("OpenClicky is not available")
            log("mac action: \(name) \(outcome.spokenSentence) in \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s")
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": outcome.spokenSentence]])
            requestContinuation(after: event)
            return
        case .badArguments(let outcome):
            log("mac action: \(name) rejected — \(outcome.spokenSentence)")
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": outcome.spokenSentence]])
            requestContinuation(after: event)
            return
        case .notAFastAction:
            break
        }
```

The continuation logic at the end of `handleToolCall` (the `responseId` / `responseInProgress` / `response.create` block) is needed by both paths, so extract it verbatim into a private method and call it from both:

```swift
    /// Ask the model to continue after a tool output. Extracted so the fast-action path and the
    /// existing tools share exactly one implementation of the barge-in and ordering rules.
    private func requestContinuation(after event: [String: Any]) {
        let responseId = event["response_id"] as? String
        if let responseId, cancelledResponseIds.contains(responseId) {
            log("tool output for cancelled response \(responseId): no continuation")
        } else if responseInProgress {
            if responseId == nil || responseId == activeResponseId {
                needsContinuationAfterResponse = true
            } else {
                log("tool output for inactive response \(responseId ?? "?"): no continuation")
            }
        } else {
            try? send(["type": "response.create"])
        }
    }
```

Replace the original block at the end of `handleToolCall` with `requestContinuation(after: event)`.

In `CompanionManager.swift`, add the runner as a stored property next to `notchHUDManager` (around line 252):

```swift
    /// Performs the fast local actions the Realtime session asks for (see MacActions.swift).
    private let macActionRunner = MacActionRunner.live(
        workspaceDirectory: URL(fileURLWithPath: OpenClickyConfiguration.workspacePath)
    )
```

and after the `onAgentTask` assignment at line 212, add:

```swift
        realtimeVoiceClient.onMacAction = { [weak self] action in
            guard let self else { return .failed("OpenClicky is not available") }
            return await self.macActionRunner.perform(action)
        }
```

In `OpenClickyApp.swift`, next to each `client.onAgentTask = { task in "smoke agent would run: \(task)" }` (lines 83 and 146), add:

```swift
                client.onMacAction = { action in .failed("smoke run does not perform \(action)") }
```

- [ ] **Step 4: Run the full Swift suite**

```bash
cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests 2>&1 | grep -E "error:|\*\* TEST (SUCCEEDED|FAILED) \*\*"
```

Expected: `** TEST SUCCEEDED **`, and no test that passed before now fails.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift macos/OpenClicky/OpenClicky/CompanionManager.swift macos/OpenClicky/OpenClicky/OpenClickyApp.swift macos/OpenClicky/OpenClickyTests/MacActionsTests.swift
git commit -m "feat(mac): offer the fast local actions on the Realtime session"
```

---

### Task 5: Log how long each lane took

Without this the two-second claim cannot be checked. Task 4 already logs fast actions; this adds the agent lane's missing end line.

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift` — the `send_to_agent` case (around line 648)

**Interfaces:**
- Consumes: the existing `log(_:)` helper, which writes to `~/Library/Logs/OpenClicky/app.log`.
- Produces: an `agent task finished:` line carrying elapsed seconds.

- [ ] **Step 1: Write the implementation**

In `handleToolCall`, replace the `send_to_agent` case body with:

```swift
        case "send_to_agent":
            let task = arguments["task"] as? String ?? ""
            log("agent task: \(task)")
            let startedAt = Date()
            output = await onAgentTask?(task) ?? "The agent lane is not available in this session."
            log("agent task finished in \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s")
```

There is no unit test for this step: it is one log line on a path that needs a live Realtime connection and a running agent. Step 2 verifies it by reading the log.

- [ ] **Step 2: Verify by hand**

```bash
cd macos/OpenClicky && pkill -x OpenClicky && scripts/release.sh --no-notarize && open /Applications/OpenClicky.app
```

Hold the talk shortcut and say "summarise what's on my screen" (a request that must reach the agent lane), then:

```bash
grep "agent task" ~/Library/Logs/OpenClicky/app.log | tail -2
```

Expected: an `agent task:` line followed by an `agent task finished in N.NN s` line.

- [ ] **Step 3: Commit**

```bash
git add macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift
git commit -m "feat(mac): log how long each agent run took"
```

---

### Task 6: Attach Computer Use so the agent stops hunting for it

Independent of Tasks 1–5; it can be done before or after them.

**Files:**
- Modify: `agent/src/config.ts` — the `cuaDriverBin` line in `resolveConfig`
- Modify: `agent/test/codexHome.test.ts` — append a suite
- Modify: `skills/ModelInstructions.md` — two lines

**Interfaces:**
- Consumes: nothing.
- Produces: `resolveConfig(...).cuaDriverBin` defaulting to the installed driver.

- [ ] **Step 1: Write the failing test**

Append to `agent/test/codexHome.test.ts`:

```typescript
describe("computer-use driver discovery", () => {
  it("defaults to the installed cua-driver so the config renders the MCP server", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cua-"));
    const driver = path.join(home, ".local", "bin", "cua-driver");
    fs.mkdirSync(path.dirname(driver), { recursive: true });
    fs.writeFileSync(driver, "#!/bin/sh\n", { mode: 0o755 });

    const cfg = resolveConfig({}, { HOME: home } as NodeJS.ProcessEnv);
    expect(cfg.cuaDriverBin).toBe(driver);
    expect(renderMcpServers({ cuaDriverBin: cfg.cuaDriverBin })).toContain("[mcp_servers.computer-use]");
  });

  it("renders no server when no driver is installed", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-nocua-"));
    const cfg = resolveConfig({}, { HOME: home } as NodeJS.ProcessEnv);
    expect(cfg.cuaDriverBin).toBeUndefined();
  });

  it("lets an explicit setting win, and an empty string disable it", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cua2-"));
    const driver = path.join(home, ".local", "bin", "cua-driver");
    fs.mkdirSync(path.dirname(driver), { recursive: true });
    fs.writeFileSync(driver, "#!/bin/sh\n", { mode: 0o755 });

    expect(resolveConfig({}, { HOME: home, CUA_DRIVER_BIN: "/opt/other" } as NodeJS.ProcessEnv).cuaDriverBin).toBe("/opt/other");
    expect(resolveConfig({}, { HOME: home, CUA_DRIVER_BIN: "" } as NodeJS.ProcessEnv).cuaDriverBin).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd agent && npx vitest run test/codexHome.test.ts 2>&1 | tail -20
```

Expected: FAIL — `expected undefined to be '/…/cua-driver'`.

- [ ] **Step 3: Write the implementation**

In `agent/src/config.ts`, replace:

```typescript
    cuaDriverBin: flags.cuaDriverBin ?? env.CUA_DRIVER_BIN ?? undefined,
```

with:

```typescript
    cuaDriverBin: flags.cuaDriverBin ?? resolveCuaDriverBin(env.CUA_DRIVER_BIN, home),
```

and add above `resolveConfig`:

```typescript
/**
 * Where cua-driver is, so the `computer-use` MCP server is actually rendered. Without this the
 * agent's instructions advertise Computer Use while no such server is attached, and the model
 * spends turns enumerating MCP resources looking for it before falling back to the shell.
 * An explicit CUA_DRIVER_BIN wins; an explicit empty string disables it.
 */
function resolveCuaDriverBin(configured: string | undefined, home: string): string | undefined {
  if (configured !== undefined) return configured === "" ? undefined : configured;
  const candidates = [
    path.join(home, ".local", "bin", "cua-driver"),
    "/Applications/CuaDriver.app/Contents/MacOS/cua-driver",
  ];
  return candidates.find((candidate) => {
    try {
      fs.accessSync(candidate, fs.constants.X_OK);
      return true;
    } catch {
      return false;
    }
  });
}
```

Add `import fs from "node:fs";` at the top of `agent/src/config.ts` if it is not already imported.

In `skills/ModelInstructions.md`, in the "Workflow routing:" list, add as the first two bullets:

```markdown
- OpenClicky performs simple local actions itself before you are involved — opening an app, opening a URL, creating a single folder, revealing a file, volume and media keys. Those requests do not reach you; do not plan around them or offer to do them faster.
- do not enumerate MCP resources to find out whether Computer Use exists. The attached tools are already in your tool list: if the `computer-use` server is there, use it per the `cua-driver` contract; if it is not, use the shell directly for local file and app actions rather than searching for an alternative.
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd .. && npm test -w agent 2>&1 | tail -8
```

Expected: all test files pass, including the three new cases.

- [ ] **Step 5: Rebuild the CLI the app runs**

```bash
npm run build -w agent && node -e "
const {resolveConfig}=require('./agent/dist/config.js');
" 2>/dev/null; grep -n "resolveCuaDriverBin" agent/dist/config.js | head -2
```

Expected: `resolveCuaDriverBin` appears in the built output. (The app runs `agent/dist/cli.js`, so a source-only change has no effect on it.)

- [ ] **Step 6: Verify the server is attached**

```bash
export OPENCLICKY_TOKEN="$(python3 -c "import json,os;print(json.load(open(os.path.expanduser('~/.openclicky/shell.json')))['token'])")"
node agent/dist/cli.js run "say ready" --events --cwd ~/OpenClicky > /dev/null 2>&1
grep -A2 "mcp_servers.computer-use" ~/.openclicky/codex-home/config.toml
```

Expected: the `[mcp_servers.computer-use]` block with the driver's path.

- [ ] **Step 7: Commit**

```bash
git add agent/src/config.ts agent/test/codexHome.test.ts skills/ModelInstructions.md
git commit -m "fix(agent): attach the Computer Use server the instructions assume"
```

---

### Task 7: Measure it

**Files:**
- Create: `macos/OpenClicky/scripts/measure-actions.sh`
- Modify: `macos/OpenClicky/AGENTS.md` — the Key Files table and Build & Run section

**Interfaces:**
- Consumes: the `mac action:` and `agent task finished in` log lines from Tasks 4–5.
- Produces: a p50/p95 table per verb.

- [ ] **Step 1: Write the script**

Create `macos/OpenClicky/scripts/measure-actions.sh`:

```bash
#!/usr/bin/env bash
# Reports how long the fast lane actually takes, from the app's own log.
#
#   scripts/measure-actions.sh          # summarise every mac action in the log
#   scripts/measure-actions.sh open_app # one verb
#
# Populate the log first by using the app: hold the talk shortcut and say "open Spotify",
# "create a folder called Test on my desktop", and so on. The spec's acceptance number is
# p95 under 2 s for open_app and create_folder.
set -euo pipefail

LOG="$HOME/Library/Logs/OpenClicky/app.log"
VERB="${1:-}"
[[ -f "$LOG" ]] || { echo "no log at $LOG — run the app first"; exit 1; }

grep "mac action:" "$LOG" \
  | sed -E 's/.*mac action: ([a-z_]+) .* in ([0-9.]+) s.*/\1 \2/' \
  | { [[ -n "$VERB" ]] && grep "^$VERB " || cat; } \
  | awk '
      { times[$1] = times[$1] " " $2 }
      END {
        printf "%-18s %6s %8s %8s\n", "verb", "runs", "p50", "p95"
        for (verb in times) {
          n = split(times[verb], values, " ")
          for (i = 1; i <= n; i++) for (j = i + 1; j <= n; j++)
            if (values[i] + 0 > values[j] + 0) { t = values[i]; values[i] = values[j]; values[j] = t }
          p50 = values[int((n + 1) / 2)]
          p95 = values[int(n * 0.95) < 1 ? 1 : int(n * 0.95)]
          printf "%-18s %6d %7.2fs %7.2fs\n", verb, n, p50, p95
        }
      }'
```

```bash
chmod +x macos/OpenClicky/scripts/measure-actions.sh
```

- [ ] **Step 2: Install the build and exercise every verb**

```bash
cd macos/OpenClicky && pkill -x OpenClicky && scripts/release.sh --no-notarize && open /Applications/OpenClicky.app
```

Hold the talk shortcut and say each of these, waiting for the reply between them:

1. "open Spotify" — with Spotify closed
2. "open Spotify" — again, now that it is open
3. "open Ableton" (or any app that is not installed) — expect "I couldn't find an app called Ableton."
4. "create a folder called Test on my desktop"
5. "create a folder called Test on my desktop" — again, expect "Test is already on your Desktop."
6. "turn the volume down to thirty"
7. "summarise what's on my screen" — must still reach the agent lane

- [ ] **Step 3: Read the numbers**

```bash
scripts/measure-actions.sh
```

Expected: `open_app` and `create_folder` p95 under 2.00 s. If either is above, do not adjust the target — find where the time goes in `app.log` first and report it.

- [ ] **Step 4: Update the docs**

In `macos/OpenClicky/AGENTS.md`, add to the Key Files table after the `AppConnectPrompt.swift` row:

```markdown
| `MacActions.swift` | ~330 | The fast lane: the local actions OpenClicky performs itself instead of routing them to Codex — `open_app`, `open_url`, `create_folder`, `reveal_in_finder`, `set_volume`, `media_control`, offered to the Realtime session as tools next to `point_at`. Typed arguments only: `location` is a closed enum (desktop/downloads/documents/workspace/home), names are rejected if they could escape it, and only http(s) URLs open — a mishearing can misname a folder but cannot produce a command. Each action returns one sentence, which is what the assistant says. About two seconds against twelve through the agent lane; anything outside the set still goes to `send_to_agent`. Unit-tested. |
```

And in the Build & Run section, after the `release.sh` lines:

```markdown
`scripts/measure-actions.sh` reports p50/p95 per verb from `~/Library/Logs/OpenClicky/app.log`
(`mac action:` and `agent task finished in` lines). The fast lane's acceptance number is p95 under
2 s for `open_app` and `create_folder`.
```

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/scripts/measure-actions.sh macos/OpenClicky/AGENTS.md
git commit -m "test(mac): measure the fast lane from the app's own log"
```

---

## Not in this plan

§3 of the spec — one-shot mode for the agent lane, reclaiming the 3.56 s closing turn — is deliberately unscheduled. Tasks 1–4 remove most single-command requests from the agent lane, so the decision needs Task 7's measurements first: if single-command agent runs are still common afterwards, plan it then; if they are rare, the complexity is not worth it.
