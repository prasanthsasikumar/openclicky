# Do Things For Me Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Six new voice actions (minimize everything, restore, web search, play a song in Spotify, check email, draft an email), pause/play that never starts playback by mistake, and "act, then look" so the model reads the screen it just changed.

**Architecture:** Every action is a typed `MacAction` case in the existing fast lane (`MacActions.swift`): a tool definition, a `parse` branch, a runner branch over injected closures, an outcome with a spoken sentence and a separate note for the model. The slow parts (AppleScript, Spotify launch, page settle, screenshot) run on one serial chain in `RealtimeVoiceClient` so the receive loop never waits. The Spotify catalogue lookup goes straight to Spotify on the person's own credentials, else to a new public backend route.

**Tech Stack:** Swift 5 mode / SwiftUI / AppKit, Swift Testing (`@Test`), Accessibility (`AXUIElement`), `NSAppleScript`, `CGWindowListCopyWindowInfo`; backend Hono on Node + Workers, vitest.

**Spec:** `docs/superpowers/specs/2026-10-27-do-things-for-me-design.md`

## Global Constraints

- Own keys first: a person's own Spotify credentials go straight to Spotify; the backend route carries only the search words; nothing of the person's goes to the backend otherwise.
- Tool calls must not wait on the Realtime receive loop: anything slow runs on `actionChain` (Task 9).
- Fast-lane acceptance: p95 under 2 s per action (`macos/OpenClicky/scripts/measure-actions.sh`), except `check_email` in Apple Mail (AppleScript capped at 5 s) and any action that ends with a look at the screen (+ about 2 s, 4 s cap).
- Every argument typed and capped: query 1–200 characters, no control characters; `to` ≤ 80 characters, no `<` or `>`; `body` truncated at 2,000 characters; `subject` ≤ 200.
- No tool sends email, deletes, runs a command or pays. There is no send tool.
- Log lines never carry an email body or recipient, only sizes.
- `MacActionRunner` stays testable without AppKit: every system call is an injected closure.
- Naming: clarity over concision, no single-character names (`macos/OpenClicky/AGENTS.md` conventions). Comments say why.
- Apple Events entitlement and `NSAppleEventsUsageDescription` already exist; no Contacts permission is ever requested.
- Debug builds carry their own bundle id; `xcodebuild test` is safe. Quit the installed app before running tests (`pkill -x OpenClicky`); reinstall for live checks with `scripts/release.sh --no-notarize`.
- Windows and Linux clients untouched.

## Review Focus

1. "Stop that song" with nothing playing and no player running: must not start playback (media key `playpause` would). Pinned in Task 3.
2. "Minimize everything" while OpenClicky's island or orb is up, or with a window on another Space: OpenClicky's own windows and other Spaces are never touched. Pinned in Task 4.
3. A recipient like `ana+work@example.com` or a body with `&`, `#`, a newline: the Gmail compose URL must carry them intact (`+` must be `%2B`, newline `%0A`). Pinned in Task 5.
4. A `draft_email` with `to` set to a name ("Ana") rather than an address: Mail gets `{name:…}`, Gmail gets the name in `to`, nothing is looked up. Pinned in Task 5.
5. A sign-off or barge-in while an action waits (page settling, Mail script running): the chain's late output must not send `response.create` after a cancel. Pinned in Task 9.

---

### Task 1: Outcomes grow a note for the model, a log line and "then look"

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift` (`MacActionOutcome`, ~line 60)
- Test: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift` (`MacActionsTests`)

**Interfaces:**
- Produces: on `MacActionOutcome`: `var toolOutput: String` (what the Realtime model is told; defaults to `spokenSentence`), `var logDescription: String` (what `app.log` gets; defaults to `spokenSentence`), `var thenLooksAtScreen: Bool` (false for every existing case except `.openedURL`, which is true).
- Produces: `RealtimeVoiceClient.handleToolCall` logs `outcome.logDescription` and sends `outcome.toolOutput` as the `function_call_output` (today both are `spokenSentence`, `RealtimeVoiceClient.swift:~1050`).

- [ ] **Step 1: Write the failing tests**

```swift
@Test func everyOutcomeTellsTheModelSomethingAndLogsSomething() {
    for outcome in MacActionsTests.sampleOutcomes {   // extend the existing list used by everyOutcomeHasOneSentenceToSay
        #expect(!outcome.toolOutput.isEmpty)
        #expect(!outcome.logDescription.isEmpty)
    }
}

@Test func onlyOpeningAPageLooksAtTheScreenAfterwardsForNow() {
    #expect(MacActionOutcome.openedURL("https://example.com").thenLooksAtScreen)
    #expect(!MacActionOutcome.openedApp("Spotify").thenLooksAtScreen)
    #expect(!MacActionOutcome.volumeSet(40).thenLooksAtScreen)
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/MacActionsTests 2>&1 | grep -E "error:|failed|TEST"`
Expected: compile error, `toolOutput` not found.

- [ ] **Step 3: Add the three properties to `MacActionOutcome`**

`toolOutput` and `logDescription` are computed, returning `spokenSentence` for every current case (later tasks add cases that differ). `thenLooksAtScreen` is a `switch` with `.openedURL` true, default false. Then change the `.action` branch of `handleToolCall` to log `outcome.logDescription` and send `outcome.toolOutput`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: `TEST SUCCEEDED`.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/MacActions.swift macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift macos/OpenClicky/OpenClickyTests/MacActionsTests.swift
git commit -m "Fast-lane outcomes say one thing to the person and another to the model"
```

---

### Task 2: `web_search`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/WebSearchURL.swift`
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift` (`MacAction`, `MacActionOutcome`, `MacActionValidation`, `realtimeToolDefinitions`, `parse`, `MacActionRunner.perform`)
- Test: `macos/OpenClicky/OpenClickyTests/WebSearchURLTests.swift` (new), `MacActionsTests.swift`

**Interfaces:**
- Produces: `enum WebSearchSite: String, CaseIterable { case web, youtube, wikipedia, maps, amazon }` with `static func url(for site: WebSearchSite, query: String) -> URL` (Google `https://www.google.com/search?q=`, YouTube `/results?search_query=`, Wikipedia `https://en.wikipedia.org/w/index.php?search=`, Maps `https://www.google.com/maps/search/`, Amazon `https://www.amazon.com/s?k=`).
- Produces: `MacActionValidation.searchQuery(_ raw: String) -> String?` (trimmed, 1–200 characters, nil on control characters).
- Produces: `MacAction.webSearch(query: String, site: WebSearchSite)`, `MacActionOutcome.searched(query: String, site: WebSearchSite)` (spoken "Searching for <query>."; `toolOutput` "The results for <query> are on screen in the latest screenshot. Say what the first few are in one sentence."; `thenLooksAtScreen` true), `MacActionOutcome.invalidQuery` ("I didn't catch what to search for.").
- Tool: `web_search`, properties `query` (string, required), `site` (string enum of the five raw values, optional; absent = `web`).
- Runner: uses the existing `openURL` closure.

- [ ] **Step 1: Write the failing tests**

```swift
// WebSearchURLTests.swift
@Test func eachSiteBuildsItsOwnSearchURLWithTheQueryEncoded() {
    #expect(WebSearchSite.url(for: .web, query: "rotary engine & co").absoluteString == "https://www.google.com/search?q=rotary%20engine%20%26%20co")
    #expect(WebSearchSite.url(for: .youtube, query: "lo-fi").absoluteString == "https://www.youtube.com/results?search_query=lo-fi")
    #expect(WebSearchSite.url(for: .amazon, query: "usb c hub").absoluteString.hasPrefix("https://www.amazon.com/s?k="))
}
@Test func queriesAreTrimmedCappedAndRefuseControlCharacters() {
    #expect(MacActionValidation.searchQuery("  cats  ") == "cats")
    #expect(MacActionValidation.searchQuery("") == nil)
    #expect(MacActionValidation.searchQuery("a\u{0}b") == nil)
    #expect(MacActionValidation.searchQuery(String(repeating: "x", count: 201)) == nil)
}
// MacActionsTests.swift, MacActionParsingTests
@Test func webSearchParsesTheSiteAndDefaultsToTheWeb() {
    #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats"]) == .action(.webSearch(query: "cats", site: .web)))
    #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats", "site": "youtube"]) == .action(.webSearch(query: "cats", site: .youtube)))
    #expect(MacAction.parse(toolName: "web_search", arguments: ["query": "cats", "site": "bing"]) == .badArguments(.invalidQuery))
    #expect(MacAction.parse(toolName: "web_search", arguments: ["query": ""]) == .badArguments(.invalidQuery))
}
// MacActionRunnerTests
@Test func aWebSearchOpensTheSearchPageAndLooksAfterwards() async throws {
    let outcome = await runner.perform(.webSearch(query: "cats", site: .web))
    #expect(recorder.openedURLs == [URL(string: "https://www.google.com/search?q=cats")!])
    #expect(outcome == .searched(query: "cats", site: .web))
    #expect(outcome.thenLooksAtScreen)
}
```

Also update `everyFastActionIsOfferedToTheModel` to the new name list (`web_search` after `media_control`).

- [ ] **Step 2: Run the tests to verify they fail** (Step 2 command of Task 1, plus `-only-testing:OpenClickyTests/WebSearchURLTests`). Expected: compile errors.

- [ ] **Step 3: Implement `WebSearchURL.swift` and the `MacActions.swift` additions**

`url(for:query:)` builds with `URLComponents` and sets `percentEncodedQuery` so `+` and `&` in the query are encoded (`URLComponents.queryItems` leaves `+` bare). `parse` reads `site` with `WebSearchSite(rawValue:)` on the lowercased string; an unknown site is `.badArguments(.invalidQuery)`.

- [ ] **Step 4: Run the tests to verify they pass.** Expected: `TEST SUCCEEDED`, and `everyDeclaredArgumentKeyParsesIntoTheMatchingAction` still passes (it walks every definition).

- [ ] **Step 5: Commit** — `git commit -m "web_search: open a search for what was said, on the web or a named site"`

---

### Task 3: `media_control` gains `pause` and `play` that go to the player itself

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/Plugins/Media/MediaStore.swift` (`MediaCommand`: add `case pause`, `case play`)
- Modify: `macos/OpenClicky/OpenClicky/Plugins/Media/AppleScriptMediaSource.swift` (`commandScript`: `pause` → `tell application "<app>" to pause`, `play` → `… to play`)
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift` (tool enum, runner)
- Test: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift`, `MediaSnapshotTests.swift` or wherever `commandScript` is tested (`grep -rn commandScript OpenClickyTests`)

**Interfaces:**
- Produces: `MacActionRunner.Extras` — a struct holding every closure the new actions need, with `static let inert: Extras` (each closure a no-op returning the "nothing happened" value) for tests; `MacActionRunner.init` gains `extras: Extras` (callers in tests pass `.inert`, `live` builds the real one). This task adds the first member: `controlPlayer: (MediaCommand) async -> Bool` (true when a running player took the command).
- Produces: `MacActionOutcome.playerControlled(String)` — "Paused." / "Playing." (the command's word).
- `media_control` enum becomes `["playpause", "next", "previous", "pause", "play"]`; description: "Pause, resume, or skip whatever is playing. Use pause for 'stop', 'pause', 'be quiet'; play to resume; playpause only when they say 'play/pause' without saying which."
- Runner: `pause`/`play` → `extras.controlPlayer(.pause / .play)`; if it returns false (no player running) → `.failed("nothing is playing")` for pause and the media key for play. Other actions unchanged.

- [ ] **Step 1: Write the failing tests**

```swift
@Test func pauseGoesToTheRunningPlayerAndNeverToThePlayPauseKey() async throws {   // Review Focus 1
    recorder.playerTakesCommands = true
    let outcome = await runner.perform(.mediaControl(action: "pause"))
    #expect(recorder.playerCommands == [.pause])
    #expect(recorder.mediaKeys.isEmpty)
    #expect(outcome == .playerControlled("pause"))
}
@Test func pauseWithNoPlayerRunningDoesNothingAndSaysSo() async throws {
    recorder.playerTakesCommands = false
    let outcome = await runner.perform(.mediaControl(action: "pause"))
    #expect(recorder.mediaKeys.isEmpty)
    #expect(outcome == .failed("nothing is playing"))
}
@Test func pauseAndPlayScriptsTellThePlayerDirectly() {
    #expect(AppleScriptMediaSource.commandScript(.pause, for: .spotify) == "tell application \"Spotify\" to pause")
    #expect(AppleScriptMediaSource.commandScript(.play, for: .music) == "tell application \"Music\" to play")
}
```

The `Recorder` gains `playerCommands: [MediaCommand]` and `playerTakesCommands: Bool`; `makeRunner` passes `extras: Extras(controlPlayer: { command in recorder.playerCommands.append(command); return recorder.playerTakesCommands })` (later tasks add members with `.inert` defaults in the memberwise call).

- [ ] **Step 2: Run the tests to verify they fail.** Expected: compile errors (`Extras`, `.pause`).

- [ ] **Step 3: Implement**

`live`'s `controlPlayer`: `let players = AppleScriptMediaSource.runningPlayers(); guard let player = players.first else { return false }; await AppleScriptMediaSource().perform(command, on: player); return true` (Spotify first if both run — `runningPlayers()`' order; builder checks and pins the preference to Spotify when both run).

- [ ] **Step 4: Run the tests to verify they pass.** Also the Media plugin tests (`-only-testing:OpenClickyTests/MediaStoreTests` and the snapshot tests) still pass with the new enum cases.

- [ ] **Step 5: Commit** — `git commit -m "Stop that song: pause and play go to the player, never the play/pause key"`

---

### Task 4: `minimize_all` and `restore_windows`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/OnScreenWindows.swift` — `OnScreenWindow` (moved from `BrowserTabLocator.swift`, gaining `let layer: Int`) and `enum OnScreenWindows { static func current() -> [OnScreenWindow] }` (the body of `BrowserTabLocator.onScreenWindows()`, which then calls this)
- Create: `macos/OpenClicky/OpenClicky/WindowMinimizer.swift`
- Modify: `macos/OpenClicky/OpenClicky/BrowserTabLocator.swift` (use `OnScreenWindows.current()`), `MacActions.swift`
- Test: `macos/OpenClicky/OpenClickyTests/WindowMinimizerTests.swift` (new), `MacActionsTests.swift`, `BrowserTabLocatorTests.swift` (compiles with the moved type)

**Interfaces:**
- Produces: `struct MinimizableWindow: Equatable { let ownerPID: pid_t; let bounds: CGRect }`.
- Produces: `enum WindowMinimizer`:
  - `static func windowsToMinimize(onScreen: [OnScreenWindow], regularAppPIDs: Set<pid_t>, ownPID: pid_t) -> [MinimizableWindow]` — pure: layer 0 only, owner in `regularAppPIDs`, owner ≠ `ownPID`, bounds at least 50×50 (drops tooltips and menus).
  - `@MainActor static func minimize(_ windows: [MinimizableWindow]) -> Int` — for each owner pid, `AXUIElementCreateApplication`, `kAXWindowsAttribute`, match each AX window's frame to a `bounds` within 2 pt, set `kAXMinimizedAttribute` to `true`; returns how many it set. Messaging timeout 0.25 s per app.
  - `@MainActor static func restore(_ windows: [MinimizableWindow]) -> Int` — same matching, sets `false`.
- Produces: `MacActionRunner.Extras` members `minimizeAllWindows: () -> Int` and `restoreMinimizedWindows: () -> Int` (the runner keeps no state; `live` keeps a stack `[[MinimizableWindow]]` plus the previously front app's pid in a small `@MainActor final class MinimizedWindowsMemory` in `WindowMinimizer.swift`, newest set restored first, and re-activates that app on restore).
- Produces: `MacAction.minimizeAll`, `MacAction.restoreWindows`; outcomes `.minimizedWindows(Int)` ("Minimized N windows." / 0 → "Nothing to minimize."), `.restoredWindows(Int)` ("Brought N windows back." / 0 → "I hadn't minimized anything.").
- Tools: `minimize_all` (no properties; description "Minimize every open window so the desktop is clear. For 'minimize everything', 'clear my screen', 'hide all windows'."), `restore_windows` ("Bring back the windows minimize_all minimized.").

- [ ] **Step 1: Write the failing tests**

```swift
// WindowMinimizerTests.swift
@Test func onlyOrdinaryWindowsOfRegularAppsAreMinimizedNeverOpenClickysOwn() {   // Review Focus 2
    let onScreen = [
        OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800), layer: 0),
        OnScreenWindow(ownerPID: 99, bounds: CGRect(x: 0, y: 0, width: 1710, height: 1112), layer: 0),      // OpenClicky overlay
        OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 300, y: 300, width: 40, height: 20), layer: 0),     // a tooltip
        OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 0, width: 400, height: 300), layer: 25),      // a menu
        OnScreenWindow(ownerPID: 20, bounds: CGRect(x: 0, y: 40, width: 800, height: 600), layer: 0),      // a background-only app
    ]
    let picked = WindowMinimizer.windowsToMinimize(onScreen: onScreen, regularAppPIDs: [10, 99], ownPID: 99)
    #expect(picked == [MinimizableWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 40, width: 1200, height: 800))])
}
// MacActionRunnerTests
@Test func minimizeAllReportsTheCountAndRestoreBringsThemBack() async throws {
    recorder.windowsToMinimize = 3
    #expect(await runner.perform(.minimizeAll) == .minimizedWindows(3))
    recorder.windowsToMinimize = 0
    #expect(await runner.perform(.minimizeAll) == .minimizedWindows(0))
    #expect(MacActionOutcome.minimizedWindows(0).spokenSentence == "Nothing to minimize.")
    recorder.windowsToRestore = 3
    #expect(await runner.perform(.restoreWindows) == .restoredWindows(3))
    #expect(MacActionOutcome.restoredWindows(0).spokenSentence == "I hadn't minimized anything.")
}
@Test func minimizeAllAndRestoreParseWithNoArguments() {
    #expect(MacAction.parse(toolName: "minimize_all", arguments: [:]) == .action(.minimizeAll))
    #expect(MacAction.parse(toolName: "restore_windows", arguments: [:]) == .action(.restoreWindows))
}
```

- [ ] **Step 2: Run the tests to verify they fail.** Expected: compile errors.

- [ ] **Step 3: Implement `OnScreenWindows.swift`, `WindowMinimizer.swift`, the `MacActions.swift` additions, and switch `BrowserTabLocator` to `OnScreenWindows.current()`**

`current()` reads `kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements`, keeps `kCGWindowAlpha > 0`, and records `kCGWindowLayer`. `regularAppPIDs` in `live` is `NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }`; `ownPID` is `ProcessInfo.processInfo.processIdentifier`. Windows on other Spaces are never listed by `OnScreenOnly`, which is the spec's rule.

- [ ] **Step 4: Run the tests to verify they pass** (add `-only-testing:OpenClickyTests/WindowMinimizerTests -only-testing:OpenClickyTests/BrowserTabLocatorTests`).

- [ ] **Step 5: Live check on this Mac** — build with `scripts/release.sh --no-notarize`, open the installed app, say "minimize everything" with hands-free on and two apps open: both go to the Dock, the island stays, `app.log` has `mac action: minimize_all Minimized 2 windows.`; "bring them back" restores them. Record the p95 from `scripts/measure-actions.sh` in the commit message.

- [ ] **Step 6: Commit** — `git commit -m "Minimize everything, and bring it back: every on-screen window, never OpenClicky's own"`

---

### Task 5: Email: which app, Mail scripts, Gmail URLs, the two actions

**Files:**
- Create: `macos/OpenClicky/OpenClicky/EmailActions.swift`
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyConfiguration.swift` (`OpenClickyShellSettings`: `var emailApp: String? = nil`)
- Modify: `macos/OpenClicky/OpenClicky/MacActions.swift`
- Test: `macos/OpenClicky/OpenClickyTests/EmailActionsTests.swift` (new), `MacActionsTests.swift`

**Interfaces:**
- Produces: `enum EmailApp: String { case mail, gmail }` with `static func choose(requested: String?, setting: String?, mailHasAccounts: Bool) -> EmailApp` (requested wins when it parses; else setting when it parses; else `mailHasAccounts ? .mail : .gmail`) and `static func mailHasAccounts(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> Bool` (any `~/Library/Mail/V*/MailData/Accounts.plist` exists).
- Produces: `struct UnreadMail: Equatable { let sender: String; let subject: String }`.
- Produces: `enum MailScripts`:
  - `static func unreadInboxSource(limit: Int = 10) -> String` — one AppleScript returning sender and subject of the newest `limit` unread inbox messages, fields joined by ASCII 30, records by ASCII 29, wrapped by the Media plugin's style of timeout but at 5 s (`with timeout of 5 seconds`).
  - `static func parseUnread(_ reply: String) -> [UnreadMail]`.
  - `static func draftSource(to recipient: String, subject: String, body: String) -> String` — `make new outgoing message with properties {subject:…, content:…, visible:true}`, then `make new to recipient at end of to recipients with properties {address:…}` when `recipient` contains `@`, `{name:…}` otherwise, then `activate`. `static func escaped(_ text: String) -> String` doubles backslashes and escapes double quotes.
- Produces: `enum GmailURLs`: `static let inbox = URL(string: "https://mail.google.com/mail/u/0/#inbox")!`, `static func compose(to: String, subject: String, body: String) -> URL` (`https://mail.google.com/mail/?view=cm&fs=1&to=…&su=…&body=…`, every value percent-encoded with `+`, `&`, `#`, newline encoded).
- Produces: `MacActionValidation.recipient(_:) -> String?` (trimmed, 1–80, no `<`/`>`), `.emailSubject(_:) -> String` (trimmed, truncated to 200), `.emailBody(_:) -> String` (trimmed, truncated to 2,000).
- Produces: `MacActionRunner.Extras` members: `chooseEmailApp: (String?) -> EmailApp`, `readUnreadMail: () async -> Result<[UnreadMail], MailScriptError>` where `enum MailScriptError: Equatable { case notAllowed, timedOut, failed(String) }`, `runMailDraft: (String) async -> MailScriptError?` (nil on success), `activateMail: () -> Void`.
- Produces: `MacAction.checkEmail(app: String?)`, `MacAction.draftEmail(to: String, subject: String, body: String, app: String?)`.
- Outcomes:
  - `.unreadMail([UnreadMail])` — spoken: empty → "No unread email in Mail."; else "N unread: <sender> about <subject>, …" for the first three, "and N more" if more. `toolOutput`: the full list (≤10) as lines "sender — subject" followed by "Read out sender and subject briefly, newest first, at most five. Never invent what the emails say." `logDescription`: "check_email: N unread in Mail".
  - `.openedGmailInbox` — spoken "Here's your inbox."; `toolOutput` "Gmail's inbox is open and attached as the latest screenshot. Unread rows are in bold. Read out sender and subject of the unread ones briefly, newest first, at most five, then stop."; `thenLooksAtScreen` true.
  - `.mailUnavailable(MailScriptError)` — spoken: `.notAllowed` → "macOS didn't let me control Mail. Allow OpenClicky under System Settings, Privacy & Security, Automation. I opened Mail instead."; others → "I couldn't read Mail in time, so I opened it instead."; `thenLooksAtScreen` true.
  - `.draftedEmail(app: EmailApp, to: String)` — spoken "Drafted an email to <to>; it's open for you to check. I don't send email — you do."; `toolOutput` the same plus "If they ask you to send it, say you never send email and that it is open in front of them."; `logDescription` "draft_email: <app>, <body length>-character body, <address|name> given"; `thenLooksAtScreen` true only for `.gmail`.
  - `.invalidRecipient` ("I didn't catch who it's for."), `.mailDraftFailed(MailScriptError)` (the Automation sentence for `.notAllowed`, "Mail couldn't make the draft: …" otherwise).
- Tools: `check_email` (`app` optional enum `["mail", "gmail"]`; description "Show the user's unread email. Use when they say check my email, any new mail, what's in my inbox."), `draft_email` (`to` required, `subject` optional, `body` required, `app` optional; description "Start a new email with the recipient and a short body written from what the user said, and leave it open for them to read and send themselves. Never sends.").

- [ ] **Step 1: Write the failing tests**

```swift
// EmailActionsTests.swift
@Test func theNamedAppWinsThenTheSettingThenWhetherMailIsSetUp() {
    #expect(EmailApp.choose(requested: "gmail", setting: "mail", mailHasAccounts: true) == .gmail)
    #expect(EmailApp.choose(requested: nil, setting: "gmail", mailHasAccounts: true) == .gmail)
    #expect(EmailApp.choose(requested: nil, setting: nil, mailHasAccounts: true) == .mail)
    #expect(EmailApp.choose(requested: nil, setting: nil, mailHasAccounts: false) == .gmail)
    #expect(EmailApp.choose(requested: "outlook", setting: nil, mailHasAccounts: false) == .gmail)
}
@Test func theDraftScriptEscapesQuotesAndUsesAnAddressOrAName() {   // Review Focus 4
    let withAddress = MailScripts.draftSource(to: "ana@example.com", subject: "Hi \"there\"", body: "back\\slash")
    #expect(withAddress.contains("subject:\"Hi \\\"there\\\"\""))
    #expect(withAddress.contains("content:\"back\\\\slash\""))
    #expect(withAddress.contains("{address:\"ana@example.com\"}"))
    #expect(!withAddress.contains("send"))
    let withName = MailScripts.draftSource(to: "Ana", subject: "", body: "x")
    #expect(withName.contains("{name:\"Ana\"}"))
}
@Test func theUnreadReplyParsesIntoSenderAndSubject() {
    let reply = "Ana <ana@x.com>\u{1E}Invoice\u{1D}GitHub\u{1E}[repo] PR #4"
    #expect(MailScripts.parseUnread(reply) == [UnreadMail(sender: "Ana <ana@x.com>", subject: "Invoice"), UnreadMail(sender: "GitHub", subject: "[repo] PR #4")])
    #expect(MailScripts.parseUnread("") == [])
}
@Test func gmailComposeEncodesEverythingGmailWouldOtherwiseMisread() {   // Review Focus 3
    let url = GmailURLs.compose(to: "ana+work@example.com", subject: "Q&A #2", body: "line one\nline two")
    #expect(url.absoluteString == "https://mail.google.com/mail/?view=cm&fs=1&to=ana%2Bwork%40example.com&su=Q%26A%20%232&body=line%20one%0Aline%20two")
}
@Test func recipientsAndBodiesAreCapped() {
    #expect(MacActionValidation.recipient("<script>") == nil)
    #expect(MacActionValidation.recipient(" Ana ") == "Ana")
    #expect(MacActionValidation.emailBody(String(repeating: "a", count: 2_500)).count == 2_000)
}
@Test func theUnreadSentenceNamesAtMostThree() {
    let four = (1...4).map { UnreadMail(sender: "S\($0)", subject: "T\($0)") }
    #expect(MacActionOutcome.unreadMail(four).spokenSentence == "4 unread: S1 about T1, S2 about T2, S3 about T3, and 1 more.")
    #expect(MacActionOutcome.unreadMail([]).spokenSentence == "No unread email in Mail.")
    #expect(MacActionOutcome.unreadMail(four).toolOutput.contains("S4 — T4"))
    #expect(!MacActionOutcome.draftedEmail(app: .mail, to: "ana@example.com").logDescription.contains("ana@"))
}
// MacActionRunnerTests
@Test func checkingMailReadsTheListAndGmailOpensTheInboxThenLooks() async throws {
    recorder.emailApp = .mail; recorder.unread = .success([UnreadMail(sender: "Ana", subject: "Hi")])
    #expect(await runner.perform(.checkEmail(app: nil)) == .unreadMail([UnreadMail(sender: "Ana", subject: "Hi")]))
    recorder.emailApp = .gmail
    let gmail = await runner.perform(.checkEmail(app: nil))
    #expect(gmail == .openedGmailInbox)
    #expect(recorder.openedURLs.last == GmailURLs.inbox)
    #expect(gmail.thenLooksAtScreen)
}
@Test func aRefusedMailScriptOpensMailAndSaysWhereToAllowIt() async throws {
    recorder.emailApp = .mail; recorder.unread = .failure(.notAllowed)
    let outcome = await runner.perform(.checkEmail(app: nil))
    #expect(outcome == .mailUnavailable(.notAllowed))
    #expect(recorder.mailActivations == 1)
    #expect(outcome.spokenSentence.contains("Automation"))
}
@Test func draftingNeverSendsAndRoutesToTheChosenApp() async throws {
    recorder.emailApp = .gmail
    let outcome = await runner.perform(.draftEmail(to: "Ana", subject: "", body: "See you Friday", app: nil))
    #expect(outcome == .draftedEmail(app: .gmail, to: "Ana"))
    #expect(recorder.openedURLs.last == GmailURLs.compose(to: "Ana", subject: "", body: "See you Friday"))
    recorder.emailApp = .mail
    #expect(await runner.perform(.draftEmail(to: "ana@example.com", subject: "Hi", body: "x", app: "mail")) == .draftedEmail(app: .mail, to: "ana@example.com"))
    #expect(recorder.mailDraftScripts.count == 1)
    #expect(await runner.perform(.draftEmail(to: "<b>", subject: "", body: "x", app: nil)) == .invalidRecipient)
}
```

- [ ] **Step 2: Run the tests to verify they fail.** Expected: compile errors.

- [ ] **Step 3: Implement `EmailActions.swift`, the settings field, the `MacActions.swift` additions, and `live`'s closures**

`live`: `chooseEmailApp` reads `OpenClickyConfiguration.settings.emailApp` and `EmailApp.mailHasAccounts()`; `readUnreadMail` and `runMailDraft` run `NSAppleScript` on a utility queue the way `AppleScriptMediaSource.run` does (copy that pattern into `MailAppleScriptRunner` in `EmailActions.swift`; map error -1743 to `.notAllowed`, -1712 (timeout) to `.timedOut`); `activateMail` opens Mail by bundle id `com.apple.mail` with `activates = true`. The compose URL is built with `URLComponents.percentEncodedQuery` from a custom allowed set (`urlQueryAllowed` minus `+&#=`).

- [ ] **Step 4: Run the tests to verify they pass** (`-only-testing:OpenClickyTests/EmailActionsTests` too).

- [ ] **Step 5: Live check** — install; with hands-free on: "check my email" (Mail has accounts on this Mac → the list, the first time macOS asks to allow controlling Mail); "check Gmail" opens the inbox (reading it is Task 9's concern); "draft an email to <a real address> saying I'll be late" → a Mail window with the body, unsent; "send it" → the decline sentence (the prompt line lands in Task 10; note what the model said now). Confirm `app.log` has no address or body.

- [ ] **Step 6: Commit** — `git commit -m "Check my email and draft one by voice, in Mail or Gmail, never sent"`

---

### Task 6: Backend `GET /music/search`

**Files:**
- Create: `backend/src/music.ts`
- Modify: `backend/src/env.ts` (`SPOTIFY_CLIENT_ID?`, `SPOTIFY_CLIENT_SECRET?`, `SPOTIFY_BASE_URL?` default `https://api.spotify.com`, `SPOTIFY_ACCOUNTS_BASE_URL?` default `https://accounts.spotify.com`), `backend/src/app.ts` (register `app.get("/music/search", …)` with the public routes, before the auth middleware), `backend/README.md` (the route and the two env values)
- Test: `backend/test/music.test.ts`

**Interfaces:**
- Produces: `export type MusicTrack = { uri: string; name: string; artists: string[]; album: string; durationMs: number }`.
- Produces: `export async function searchTracks(env: Env, query: string, now = Date.now()): Promise<MusicTrack[]>` — client-credentials token (POST `${accounts}/api/token`, Basic auth, `grant_type=client_credentials`), cached until 60 s before expiry; GET `${base}/v1/search?q=…&type=track&limit=5&market=<env.SPOTIFY_MARKET ?? "US">`; results cached 1 h per normalised query (lowercased, whitespace collapsed), at most 500 entries.
- Produces: `export function resetMusicCaches()` for tests; `export function musicSearchHandler(c: Context): Promise<Response>` — 400 when `q` is missing, empty or over 200 characters; 503 `{ error: "music search is not configured" }` without the credentials; 429 after 30 requests a minute from one IP (`cf-connecting-ip`, else `x-forwarded-for`'s first value, else `"local"`); otherwise `{ tracks: MusicTrack[] }`.
- The route is public: no `requireAuth`, no billing.

- [ ] **Step 1: Write the failing tests**

```ts
// test/music.test.ts — a fake Spotify on a local http server, the shape of test/app.test.ts
it("searches on a client-credentials token and returns at most five tracks", async () => { /* token endpoint called once; /v1/search called with q, type=track, limit=5, market=US; response {tracks:[…]} */ });
it("caches the token until it is about to expire and the query for an hour", async () => { /* two searches, one token call; same query twice, one search call; resetMusicCaches between describes */ });
it("answers 503 without credentials and 400 for a missing or oversize q", async () => { /* over: {} → 503; q="" → 400; q of 201 chars → 400 */ });
it("rate-limits a single address at thirty a minute", async () => { /* 31 calls with x-forwarded-for: 10.0.0.1 → the 31st is 429; a different address still passes */ });
it("needs no bearer token", async () => { /* no Authorization header → 200 */ });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && npx vitest run test/music.test.ts`
Expected: FAIL, module `../src/music.js` not found.

- [ ] **Step 3: Implement `music.ts`, the env fields, the route, the README lines**

Module-level `Map`s for the token and the query cache (the Workers isolate and the Node process both keep them per instance, which is fine). The limiter is a `Map<string, number[]>` of timestamps pruned on each call.

- [ ] **Step 4: Run the tests and the type check**

Run: `cd backend && npx vitest run && npm run typecheck`
Expected: all pass.

- [ ] **Step 5: Commit** — `git commit -m "Backend: /music/search looks a song up on Spotify's catalogue for play_music"`

Prasanth's step (not the builder's): register the Spotify app, put `SPOTIFY_CLIENT_ID`/`SPOTIFY_CLIENT_SECRET` in `backend/.dev.vars` and on the VPS (per `flowsxr-hq/infra` runbooks), restart the local backend service (`launchctl` per `openclicky-local-setup` memory).

---

### Task 7: Spotify: lookup routes, track picking, playing

**Files:**
- Create: `macos/OpenClicky/OpenClicky/MusicLookup.swift`
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyConfiguration.swift` (`var spotifyCredentials: String? = nil`, "clientId:clientSecret"), `macos/OpenClicky/OpenClicky/ProviderRoute.swift` (a `musicLookup` capability so the route is logged `route: music lookup → spotify|backend`), `MacActions.swift`
- Test: `macos/OpenClicky/OpenClickyTests/MusicLookupTests.swift` (new), `MacActionsTests.swift`, `ProviderRouteTests.swift`

**Interfaces:**
- Produces: `struct MusicTrack: Equatable, Decodable { let uri: String; let name: String; let artists: [String] }` (decoded from both the backend's `{tracks:[…]}` and Spotify's `{tracks:{items:[{uri,name,artists:[{name}]}]}}` through two small decoders in `MusicLookup.swift`).
- Produces: `enum MusicLookupRoute: Equatable { case spotify(clientId: String, clientSecret: String), backend(URL), none }` and `static func route(credentials: String?, backendURL: URL?) -> MusicLookupRoute`.
- Produces: `enum MusicTrackPicker { static func pick(_ tracks: [MusicTrack], for query: String) -> MusicTrack? }` — the first track whose name or an artist shares a word (≥ 3 letters, lowercased) with the query, else the first.
- Produces: `struct MusicLookup { init(route: MusicLookupRoute, session: URLSession = .shared); func search(_ query: String) async -> [MusicTrack] }` — the Spotify branch mints the client-credentials token (cached in the instance until 60 s before expiry) and searches with `market` from `Locale.current.region?.identifier ?? "US"`; the backend branch GETs `/music/search?q=`; every request under a 4 s timeout; errors → `[]`.
- Produces: `enum SpotifyPlayer` (AppleScript, utility queue, the Media plugin's pattern): `static func isRunning() -> Bool` (bundle id `com.spotify.client`), `static func launchAndWait(seconds: TimeInterval = 6) async -> Bool` (open by bundle id, poll `player state` every 0.5 s), `static func play(uri: String) async -> Bool` (`tell application "Spotify" to play track "<uri>"`), `static func openSearch(query: String)` (opens `spotify:search:<percent-encoded query>`).
- Produces: `MacActionRunner.Extras` members: `lookUpTrack: (String) async -> [MusicTrack]`, `spotifyIsRunning: () -> Bool`, `launchSpotify: () async -> Bool`, `playTrack: (String) async -> Bool`, `openSpotifySearch: (String) -> Void`.
- Produces: `MacAction.playMusic(query: String)`; outcomes `.playingTrack(name: String, artist: String)` ("Playing <name> by <artist>."), `.openedSpotifySearch(query: String)` ("I couldn't look that up, so I opened Spotify's search for <query>; pick one."), `.spotifyStarting` ("Spotify is starting; give it a moment and ask again."), `.playFailed` ("Spotify didn't play it.").
- Tool: `play_music` (`query` required; description "Play a specific song, album or artist in Spotify. Pass what they said, e.g. 'Blinding Lights by The Weeknd'. For pause/resume/skip use media_control.").
- Runner order: validate query → if not running, `launchSpotify()`; false → `.spotifyStarting` → `lookUpTrack` → empty → `openSpotifySearch` + `.openedSpotifySearch` → `MusicTrackPicker.pick` → `playTrack(uri)` → `.playingTrack` or `.playFailed`.

- [ ] **Step 1: Write the failing tests**

```swift
// MusicLookupTests.swift
@Test func ownCredentialsGoToSpotifyAndOtherwiseTheBackend() {
    #expect(MusicLookupRoute.route(credentials: "id:secret", backendURL: URL(string: "https://api.example")) == .spotify(clientId: "id", clientSecret: "secret"))
    #expect(MusicLookupRoute.route(credentials: " ", backendURL: URL(string: "https://api.example")) == .backend(URL(string: "https://api.example")!))
    #expect(MusicLookupRoute.route(credentials: nil, backendURL: nil) == .none)
}
@Test func theTrackSharingAWordWithTheRequestWinsElseTheFirst() {
    let tracks = [MusicTrack(uri: "a", name: "Blinding Lights (Remix)", artists: ["Someone"]), MusicTrack(uri: "b", name: "Blinding Lights", artists: ["The Weeknd"])]
    #expect(MusicTrackPicker.pick(tracks, for: "blinding lights by the weeknd")?.uri == "b")
    #expect(MusicTrackPicker.pick(tracks, for: "that song")?.uri == "a")
    #expect(MusicTrackPicker.pick([], for: "x") == nil)
}
@Test func bothResultShapesDecode() throws { /* backend {tracks:[…]} and Spotify {tracks:{items:[…]}} → the same [MusicTrack] */ }
// MacActionRunnerTests
@Test func playingASongLaunchesSpotifyLooksItUpAndPlaysByURI() async throws {
    recorder.spotifyRunning = false; recorder.spotifyLaunches = true
    recorder.tracks = [MusicTrack(uri: "spotify:track:1", name: "Blinding Lights", artists: ["The Weeknd"])]
    #expect(await runner.perform(.playMusic(query: "blinding lights")) == .playingTrack(name: "Blinding Lights", artist: "The Weeknd"))
    #expect(recorder.playedURIs == ["spotify:track:1"])
}
@Test func aFailedLookupOpensSpotifysSearchInstead() async throws {
    recorder.spotifyRunning = true; recorder.tracks = []
    #expect(await runner.perform(.playMusic(query: "blinding lights")) == .openedSpotifySearch(query: "blinding lights"))
    #expect(recorder.spotifySearches == ["blinding lights"])
    #expect(recorder.playedURIs.isEmpty)
}
@Test func spotifyNotAnsweringAfterLaunchIsSaidNotHidden() async throws {
    recorder.spotifyRunning = false; recorder.spotifyLaunches = false
    #expect(await runner.perform(.playMusic(query: "x")) == .spotifyStarting)
}
```

Plus a `ProviderRouteTests` case asserting the `musicLookup` capability logs `route: music lookup → backend` when no credentials are set and `→ spotify` when they are (follow the file's existing pattern).

- [ ] **Step 2: Run the tests to verify they fail.** Expected: compile errors.

- [ ] **Step 3: Implement `MusicLookup.swift`, the settings field, the route logging, the `MacActions.swift` additions, `live`'s closures**

- [ ] **Step 4: Run the tests to verify they pass** (`-only-testing:OpenClickyTests/MusicLookupTests -only-testing:OpenClickyTests/ProviderRouteTests`).

- [ ] **Step 5: Live check** — with the backend credentials in place (Task 6's note) or own credentials in shell.json: "play Blinding Lights" with Spotify closed → Spotify opens and plays it, "Playing Blinding Lights by The Weeknd." within ~3 s; "stop that song" pauses (Task 3); with the backend stopped, "play xyz" opens Spotify's search. `scripts/measure-actions.sh` p95 for `play_music` in the commit message.

- [ ] **Step 6: Commit** — `git commit -m "Play a song in Spotify by name: own credentials, else the backend lookup, else Spotify's search"`

---

### Task 8: Settings: Email on Voice, Spotify on Keys & Plans

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/Dictation/UI/SettingsClickyPage.swift` (the Voice page items, after the "Fast voice" section)
- Modify: `macos/OpenClicky/OpenClicky/Dictation/UI/SettingsAccountPage.swift` (the key provider enum: `case spotify`)
- Modify: `macos/OpenClicky/OpenClicky/AnswerSourceCheck.swift` or the key row's `test(provider)` path so the Spotify Test runs a lookup
- Test: `macos/OpenClicky/OpenClickyTests/SettingsCatalogTests.swift` (if present; else the catalog test file `grep -rln "SettingsCatalog" OpenClickyTests`)

**Interfaces:**
- Voice page, section "Doing things": one `SettingsItem` id `clicky.emailApp`, title "Email", detail "Which inbox 'check my email' and 'draft an email' use.", a `Picker` with Automatic / Apple Mail / Gmail writing `emailApp` (nil / "mail" / "gmail") through `OpenClickyConfiguration.update`; keywords `["email", "mail", "gmail", "inbox", "draft"]`; footer text "Nothing is ever sent by voice." as the section's `footer` (the way other sections carry one — follow `SettingsFormView`).
- Keys & Plans: provider `.spotify` — title "Spotify", detail "Finds the song you asked for. Paste client id:client secret from developer.spotify.com; without it OpenClicky's own lookup is used.", placeholder "id:secret", keyPath `\.spotifyCredentials`, `answerSource` nil, `speechVoice` nil; its Test button runs `MusicLookup(route: .spotify(…)).search("test")` and reports "✓ Works. Found <name>." or "✗ Spotify refused the credentials."

- [ ] **Step 1: Write the failing test** — the catalog test asserting an item with id `clicky.emailApp` exists on `.voice` in section "Doing things" and that the keys page lists "Spotify" (follow the file's existing assertions).

- [ ] **Step 2: Run it to verify it fails.**

- [ ] **Step 3: Implement the rows and the Test path.**

- [ ] **Step 4: Run the test, then open the installed app's Settings and look at both rows** (search "gmail" finds the Email row; the Spotify row has Test, Remove).

- [ ] **Step 5: Commit** — `git commit -m "Settings: which inbox to check, and own Spotify credentials"`

---

### Task 9: Act, then look — the action chain in `RealtimeVoiceClient`

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift` (`handleToolCall` `.action`/`.badArguments` branches ~line 1050; new `actionChain: Task<Void, Never>?`; `closeConnection` cancels it)
- Test: `macos/OpenClicky/OpenClickyTests/RealtimeVoiceClientTests.swift` (`RealtimeSessionHandshakeOrderTests`, the fake socket)

**Interfaces:**
- Produces: `var frontAppProvider: (() -> pid_t?)?` on `RealtimeVoiceClient` (the companion sets it to `NSWorkspace.shared.frontmostApplication?.processIdentifier`), `static func settleDelay(frontChanged: Bool, elapsed: TimeInterval) -> TimeInterval?` — pure: nil (stop waiting) when `frontChanged && elapsed >= 1.0`, or `elapsed >= 4.0`; else 0.25.
- Behaviour: the `.action` branch no longer awaits `onMacAction` inline. It appends to `actionChain` (awaiting the previous link, like `pointingChain`): perform → if `outcome.thenLooksAtScreen`, wait per `settleDelay` (front pid sampled at the start) → `await attachScreenContext()` → send `function_call_output` with `outcome.toolOutput` → `requestContinuation(after: event)`. Log `mac action: <logDescription> in <s> s` as today. `.badArguments` stays inline (nothing slow).
- `requestContinuation` already handles an output arriving after `response.done` and after a cancel (Review Focus 5); the test pins both.

- [ ] **Step 1: Write the failing tests**

```swift
@Test func anActionThatLooksAfterwardsAttachesTheScreenBeforeItsOutput() async throws {
    let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
    client.screenContextProvider = { RealtimeScreenContext.testFixture }   // a 1×1 JPEG + caption; add a static fixture if none exists
    client.onMacAction = { _ in .openedURL("https://example.com") }
    await Self.waitUntil { socket.isWaitingToReceive }
    socket.open(firstEvent: #"{"type":"response.function_call_arguments.done","name":"open_url","call_id":"c1","response_id":"r1","arguments":"{\"url\":\"https://example.com\"}"}"#)
    await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
    let screenIndex = try #require(socket.sentTexts.firstIndex { $0.contains("[Screen context") })
    let outputIndex = try #require(socket.sentTexts.firstIndex { $0.contains("function_call_output") })
    #expect(screenIndex < outputIndex)
    client.disconnect(reason: nil)
}
@Test func aLateOutputAfterACancelledResponseNeverAsksForAContinuation() async throws {   // Review Focus 5
    // response r1 cancelled (barge-in) while the action waits; the output goes out, no response.create follows.
}
@Test func theSettleWaitStopsOnceTheFrontAppChangedAndAlwaysByFourSeconds() {
    #expect(RealtimeVoiceClient.settleDelay(frontChanged: false, elapsed: 0.5) == 0.25)
    #expect(RealtimeVoiceClient.settleDelay(frontChanged: true, elapsed: 0.5) == 0.25)
    #expect(RealtimeVoiceClient.settleDelay(frontChanged: true, elapsed: 1.0) == nil)
    #expect(RealtimeVoiceClient.settleDelay(frontChanged: false, elapsed: 4.0) == nil)
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

- [ ] **Step 3: Implement the chain, the settle wait, the provider, the cancel in `closeConnection`**

- [ ] **Step 4: Run the tests to verify they pass**, plus `VoiceReliabilityTests` and `RealtimeWriteInPlaceTests`.

- [ ] **Step 5: The Gmail readability spike (live, by Prasanth with the builder watching the log)** — install; hands-free on; Gmail signed in in the default browser; "check Gmail" five times. Count how many of the five read sender + subject of the unread rows right. Write the count and the settle times (`mac action: … in N s`) into `AGENTS.md`'s `MacActions.swift` row. **If fewer than four of five are right**, add Task 9b.

- [ ] **Step 6: Commit** — `git commit -m "Act, then look: an action that changes the screen attaches a fresh screenshot before the model speaks"`

#### Task 9b (only if the spike fails): a full-resolution crop of the front window

**Files:** `RealtimeVoiceClient.swift` (`attachScreenContext(cropFrontWindow: Bool)`), `CompanionScreenCaptureUtility.swift` (a crop of the front window's CG bounds at native scale, JPEG, long side ≤ 1,600 px, as a second `input_image` labelled "close-up of the front window, not for pointing"), the tool outputs of `.openedGmailInbox` and `.searched` also carry `lastScreenText`'s lines for the window. Test: the second image is present only when the outcome asks for it. Repeat the spike.

---

### Task 10: Command or question, in both prompts

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift` (`defaultInstructions`, ~line 206: the fast-tools sentence lists the new tools; add the command/question block from the spec §5)
- Modify: `macos/OpenClicky/OpenClicky/CompanionManager.swift` (`companionVoiceResponseSystemPrompt` ~line 1649, or the `toolsLine` at ~1830: the same block where the quick actions are offered)
- Test: `macos/OpenClicky/OpenClickyTests/MacActionsTests.swift` (`FastActionToolDefinitionTests.theInstructionsPointSimpleActionsAtTheFastToolsAndRealWorkAtTheAgent`)

- [ ] **Step 1: Extend the failing test**

```swift
@Test func theInstructionsDoCommandsAndTeachQuestions() {
    let instructions = RealtimeVoiceClient.defaultInstructions
    for tool in ["minimize_all", "web_search", "play_music", "check_email", "draft_email"] { #expect(instructions.contains(tool)) }
    #expect(instructions.contains("Do not explain how they could do it themselves"))
    #expect(instructions.contains("never send email"))
    #expect(CompanionManager.companionVoiceResponseSystemPrompt.contains("Do not explain how they could do it themselves"))
}
```

- [ ] **Step 2: Run it to verify it fails.**

- [ ] **Step 3: Add the block** — the spec §5 paragraph verbatim, plus one sentence: "You never send email: a draft is left open for the user to read and send." Keep the Teacher and write_in_place sentences as they are.

- [ ] **Step 4: Run the test to verify it passes.**

- [ ] **Step 5: Live check** — "how do I minimize this?" teaches and points (nothing minimized); "minimize everything" minimizes without a lesson; after a draft, "send it" is declined.

- [ ] **Step 6: Commit** — `git commit -m "Prompts: a command is done, a question is taught, and email is never sent"`

---

### Task 11: Docs, changelog, the live checklist

**Files:**
- Modify: `macos/OpenClicky/AGENTS.md` (the `MacActions.swift` row: the new actions, `Extras`, act-then-look with the measured settle times, the Spotify routes; new rows for `WebSearchURL.swift`, `WindowMinimizer.swift`/`OnScreenWindows.swift`, `EmailActions.swift`, `MusicLookup.swift`; the Settings rows), `CHANGELOG.md` (a `## 0.10.0` section above 0.9.4, one bold-gist bullet per thing the person can now say; Prasanth may rename the version), `backend/README.md` (done in Task 6; check), `macos/OpenClicky/VERSION` untouched (the release bumps it).
- Test: none; `npm run check:skills` is unaffected.

- [ ] **Step 1: Write the rows and the changelog section.**

- [ ] **Step 2: Run the whole macOS unit suite once** — `xcodebuild test … -only-testing:OpenClickyTests` → `TEST SUCCEEDED`; `cd backend && npx vitest run && npm run typecheck`.

- [ ] **Step 3: The live checklist, with Prasanth, hands-free on, each ticked in the PR description:**
  - "minimize everything" / "bring them back"
  - "open a webpage and search for rotary engines" / "search YouTube for lo-fi"
  - "open Facebook"
  - "check my email" (Mail) / "check Gmail"
  - "draft a new email to <address> saying …" / "send it" (declined)
  - "open Spotify and play <song>" / "stop that song" / "stop that song" again with nothing playing (nothing starts)
  - "how do I minimize this?" (teaches, nothing minimized)
  - "minimize everything" with a window on another Space (stays)
  - `scripts/measure-actions.sh`: p95 under 2 s for `minimize_all`, `web_search`, `play_music` (lookup included), `draft_email`

- [ ] **Step 4: Commit** — `git commit -m "Docs and changelog for do-things-for-me"`

---

## Self-review notes

- Spec coverage: §1 actions → Tasks 2, 3, 4, 5, 7; §2 act-then-look and the Gmail spike → Task 9 (+9b); §3 Spotify → Tasks 6, 7; §4 email → Task 5 (+ Task 8 setting); §5 prompts → Task 10; §6 backend → Task 6; §7 settings → Task 8; §8 safety → the validations in 2/5, the log lines in 1/5, Review Focus 5 in 9; §9 tests → each task; §10 docs → Task 11.
- Left to the builder (from the spec): the settle numbers are measured in Task 9 Step 5; no search-engine setting now (YAGNI; `web_search` is Google for `web`); the limiter and cache shapes are in Task 6; the Voice page placement is in Task 8; the version is 0.10.0 unless Prasanth says otherwise.
- Types used across tasks: `MacActionRunner.Extras` (Task 3 creates it; Tasks 4, 5, 7 add members), `MacActionOutcome.toolOutput/logDescription/thenLooksAtScreen` (Task 1), `OnScreenWindow` gains `layer` (Task 4; `BrowserTabLocatorTests` constructs it — update those initialisers), `EmailApp`/`UnreadMail`/`MailScriptError` (Task 5), `MusicTrack`/`MusicLookupRoute` (Task 7; the backend's `MusicTrack` in Task 6 is the same shape over the wire).
