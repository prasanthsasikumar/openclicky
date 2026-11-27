# Handheld Device Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An off-by-default Mac plugin that lets the owner of a supported handheld (first: the WhisPrompt handheld) drive OpenClicky from its buttons with configurable bindings, plus island notices for coding agents in a terminal.

**Architecture:** `Plugins/Handheld/` holds a device catalog (data + pure decoder per device), a listener over `IOHIDManager` that exists only while the plugin's switch is on, a bindings store, and a runner that turns bound actions into the `CompanionShortcutEvent`s and talk transitions the keyboard already produces. The main program gains one entry point (`CompanionManager.performExternalShortcut`). Terminal notices are a separate switch inside the Coding agents plugin, fed by the existing `AgentSessionWatcher` and shown through `IslandNoticeCenter`.

**Tech Stack:** Swift 5 mode, SwiftUI + AppKit, IOKit HID (`IOHIDManager`), Swift Testing (`import Testing`, suites as structs).

**Spec:** `docs/superpowers/specs/2026-11-27-handheld-device-plugin-design.md`

## Global Constraints

- Mac only. All new code under `macos/OpenClicky/OpenClicky/Plugins/Handheld/` except the touches each task names. The Xcode project uses synchronized folders: a new file in the tree is in the target.
- With `openclicky.handheld.enabled` false (the default): no `IOHIDManager` is created, nothing polls, no prompt, no row beyond the one switch in Settings → Island › More.
- Read only: never write an output or feature report to the device, never open it with `kIOHIDOptionsTypeSeizeDevice`.
- Device match: vendor `0x303a`, product `0x8001`. Maker's app bundle id: `ai.whisprompt.desktop`.
- No artwork, strings or prompts from the maker's app. The device's name appears only as the supported device's display name.
- Naming rules of `macos/OpenClicky/CLAUDE.md`: long clear names, no single-letter variables, comments say why. Every button `.pointerCursor()`.
- Tests: Swift Testing suite structs (free functions are not matched by `-only-testing`); never read `~/Documents`; default DerivedData, normal signing. Quit the installed OpenClicky before `xcodebuild test`.
- Test command (from `macos/OpenClicky`): `xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/<SuiteName>`; a run counts only if the xcresult shows the suite's cases ran.
- Work on branch `feature/handheld-plugin` in a worktree (`../openclicky-handheld`); commits end with the session's Co-Authored-By line. Do not push, release, or touch the site.
- A paste or keystroke check cannot be done from a Debug build launched from the shell: those are Prasanth's, on a build from `scripts/release.sh --no-notarize` (in a worktree, symlink `scripts/release.env` from the main checkout first).

## Review Focus

1. **Unplugged or switched off mid-hold:** `longStart` arrives and `longEnd` never does (cable pulled, plugin switched off, Mac sleeps). The take or question must end as a release, never leave the microphone open. → Task 4.
2. **Notices on switch-on and in bursts:** sessions that were already finished when the switch goes on, or five sessions finishing in one poll, must not flood the island. First poll is a baseline; at most one notice per session per state. → Task 8.
3. **Recorded shortcut that is one of OpenClicky's own** (a binding of ⌃⌥, fn, or esc): the posted keys must not start a talk or a take through the event tap. → Task 4.
4. **Both apps running:** every press acts twice. The person is told on the page; nothing else changes. → Task 6.
5. **Stale or repeated events:** the same sequence number delivered twice, or a `single` arriving after the device reconnects with its counter restarted at a low number, runs the action once and is never ignored for good. → Task 3.

---

### Task 1: Device contract and the WhisPrompt decoder

**Files:**
- Create: `OpenClicky/Plugins/Handheld/HandheldDevice.swift`
- Create: `OpenClicky/Plugins/Handheld/Devices/WhisPromptHandheld.swift`
- Test: `OpenClickyTests/HandheldDeviceTests.swift`

**Interfaces:**
- Produces:
  ```swift
  enum HandheldGesture: String, Codable, CaseIterable { case down, single, double, longStart, longEnd }
  struct HandheldControl: Equatable, Identifiable { let id: String; let name: String; let gestures: [HandheldGesture] }
  struct HandheldEvent: Equatable { let controlID: String; let gesture: HandheldGesture; let sequence: UInt32 }
  struct HandheldDevice: Identifiable {
      let id: String; let displayName: String
      let vendorID: Int; let productID: Int
      let microphoneName: String?            // CoreAudio device name, nil if none
      let controls: [HandheldControl]
      let defaultBindings: HandheldBindings  // Task 2
      let decode: (_ reportID: UInt32, _ bytes: [UInt8]) -> HandheldEvent?   // bytes include the report id byte
  }
  enum HandheldDeviceCatalog { static let all: [HandheldDevice]; static func device(vendorID: Int, productID: Int) -> HandheldDevice? }
  ```
- WhisPrompt entry: id `whisprompt`, display name "WhisPrompt handheld", microphone "WhisPrompt Mic Stream", controls `keyA` (byte `02`; gestures down, single, double, longStart, longEnd), `keyB` (byte `01`; single, double), `keyC` (byte `00`; single, double). `defaultBindings` is filled in Task 2; until then pass an empty value.

- [ ] **Step 1: Write the failing tests** in `struct HandheldDeviceTests`, using the recorded reports:

```swift
let whisprompt = HandheldDeviceCatalog.device(vendorID: 0x303a, productID: 0x8001)!
func bytes(_ hex: String) -> [UInt8] { hex.split(separator: " ").map { UInt8($0, radix: 16)! } }

@Test func keyADownSingleDoubleLong() {
    #expect(whisprompt.decode(4, bytes("04 02 02 05 21 00 00 00 10 15 43 00 00")) == HandheldEvent(controlID: "keyA", gesture: .down, sequence: 0x21))
    #expect(whisprompt.decode(4, bytes("04 02 02 01 22 00 00 00 07 17 43 00 00"))?.gesture == .single)
    #expect(whisprompt.decode(4, bytes("04 02 02 02 24 00 00 00 8c 1b 43 00 00"))?.gesture == .double)
    #expect(whisprompt.decode(4, bytes("04 02 02 03 26 00 00 00 e0 21 43 00 00"))?.gesture == .longStart)
    #expect(whisprompt.decode(4, bytes("04 02 02 04 27 00 00 00 c0 2d 43 00 00"))?.gesture == .longEnd)
}
@Test func keysBAndC() {
    #expect(whisprompt.decode(4, bytes("04 02 01 01 28 00 00 00 d3 31 43 00 00")) == HandheldEvent(controlID: "keyB", gesture: .single, sequence: 0x28))
    #expect(whisprompt.decode(4, bytes("04 02 00 02 2b 00 00 00 f8 40 43 00 00")) == HandheldEvent(controlID: "keyC", gesture: .double, sequence: 0x2b))
}
@Test func everythingElseIsNoEvent() {
    #expect(whisprompt.decode(3, bytes("03 02 08 05 01 00 00 00 09 00 03 64 01 2f 24 90 fa 0b 00")) == nil)   // status
    #expect(whisprompt.decode(2, bytes("02 00 00 00 00 00 fb 00")) == nil)                                     // wheel
    #expect(whisprompt.decode(4, bytes("04 02 07 01 28 00 00 00")) == nil)                                     // unknown key
    #expect(whisprompt.decode(4, bytes("04 02 02 09 28 00 00 00")) == nil)                                     // unknown gesture
    #expect(whisprompt.decode(4, bytes("04 02")) == nil)                                                       // short
    #expect(HandheldDeviceCatalog.device(vendorID: 0x303a, productID: 0x1234) == nil)
}
```

- [ ] **Step 2: Run** `-only-testing:OpenClickyTests/HandheldDeviceTests`. Expected: build fails, types not found.
- [ ] **Step 3: Implement** the types and the decoder. Sequence is the four bytes at offsets 4–7, little endian. A gesture a control does not list decodes to nil.
- [ ] **Step 4: Run** the suite. Expected: 3 cases pass.
- [ ] **Step 5: Commit** `Handheld: device contract and the WhisPrompt handheld's key events`.

### Task 2: Actions and bindings

**Files:**
- Create: `OpenClicky/Plugins/Handheld/HandheldBindings.swift`
- Modify: `OpenClicky/Plugins/Handheld/Devices/WhisPromptHandheld.swift` (defaults)
- Test: `OpenClickyTests/HandheldBindingsTests.swift`

**Interfaces:**
- Produces:
  ```swift
  struct HandheldKeyCombination: Codable, Equatable { let keyCode: UInt16; let modifierFlags: UInt64; let displayText: String }
  enum HandheldAction: Codable, Equatable {
      case dictateHold, dictateToggle, askHold, askToggle, handsFreeToggle, typeQuestion, stop,
           goToWaitingTerminal, openIsland, keyCombination(HandheldKeyCombination), nothing
      var title: String            // "Dictate (hold)", "Dictate (press to start, press to stop)", "Ask Clicky (hold)",
                                   // "Ask Clicky (press to start, press to stop)", "Hands-free on / off", "Type a question",
                                   // "Stop", "Go to the waiting terminal", "Open the island", the combination's displayText, "Nothing"
      var needsHold: Bool          // true for dictateHold and askHold
      static func choices(for gesture: HandheldGesture, on control: HandheldControl) -> [HandheldAction]
  }
  struct HandheldBindings: Codable, Equatable {
      func action(controlID: String, gesture: HandheldGesture) -> HandheldAction     // .nothing when unset
      mutating func set(_ action: HandheldAction, controlID: String, gesture: HandheldGesture)
  }
  final class HandheldBindingsStore {
      init(defaults: UserDefaults)
      func bindings(for device: HandheldDevice) -> HandheldBindings                  // stored, else device.defaultBindings
      func save(_ bindings: HandheldBindings, for device: HandheldDevice)            // key "openclicky.handheld.bindings.<device id>"
      func reset(_ device: HandheldDevice)
  }
  ```
- Bindable gestures on the page are `single`, `double` and `longStart` ("hold"); `down` and `longEnd` are never bound by the person. Hold actions (`needsHold`) are offered only for `longStart`, and only on a control that lists both `longStart` and `longEnd`; `longStart` offers every action.
- WhisPrompt defaults: keyA single `dictateToggle`, double `typeQuestion`, longStart `dictateHold`; keyB single Return (key code 36, "Enter"), double `goToWaitingTerminal`; keyC single Delete (key code 51, "Delete"), double ⌘Z (key code 6, command flag, "⌘Z").

- [ ] **Step 1: Write the failing tests** in `struct HandheldBindingsTests` (a `UserDefaults(suiteName:)` made and removed per test):
  - `defaultsAreTheDevices`: each of the seven defaults above; `keyB` longStart is `.nothing`.
  - `choicesOfferHoldOnlyWhereItCanEnd`: `choices(for: .single, on: keyA)` and `choices(for: .longStart, on: keyB-like control without longEnd)` contain neither `.dictateHold` nor `.askHold`; `choices(for: .longStart, on: keyA)` contains both.
  - `savedBindingsSurviveAndResetReturnsDefaults`: set keyC single to `.askToggle`, save, a new store reads it; `reset` then reads Delete.
  - `corruptStoredDataFallsBackToDefaults`: write `Data([0x00])` under the key; `bindings(for:)` equals the defaults.
- [ ] **Step 2: Run**; expected: build fails.
- [ ] **Step 3: Implement.** JSON in UserDefaults.
- [ ] **Step 4: Run**; expected: 4 cases pass.
- [ ] **Step 5: Commit** `Handheld: bindable actions, per-device bindings and their defaults`.

### Task 3: The listener

**Files:**
- Create: `OpenClicky/Plugins/Handheld/HandheldListener.swift`
- Test: `OpenClickyTests/HandheldListenerTests.swift`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  ```swift
  protocol HandheldReportSource: AnyObject {      // the IOKit side, faked in tests
      var onDeviceConnected: ((_ vendorID: Int, _ productID: Int) -> Void)? { get set }
      var onDeviceDisconnected: ((_ vendorID: Int, _ productID: Int) -> Void)? { get set }
      var onInputReport: ((_ vendorID: Int, _ productID: Int, _ reportID: UInt32, _ bytes: [UInt8]) -> Void)? { get set }
      func start(matching devices: [(vendorID: Int, productID: Int)])
      func stop()
  }
  final class IOKitHandheldReportSource: HandheldReportSource   // IOHIDManager, kIOHIDOptionsTypeNone, main run loop, created in start(), released in stop()
  @MainActor final class HandheldListener: ObservableObject {
      init(source: HandheldReportSource, catalog: [HandheldDevice] = HandheldDeviceCatalog.all)
      @Published private(set) var connectedDevice: HandheldDevice?
      var onEvent: ((HandheldDevice, HandheldEvent) -> Void)?
      var onDisconnect: ((HandheldDevice) -> Void)?
      func start(); func stop()          // stop() also reports a disconnect of the connected device
      var isRunning: Bool { get }
  }
  ```
- Duplicate rule (Review Focus 5): an event is dropped only when its sequence equals the last accepted one from this connection; the remembered sequence is cleared on every connect and disconnect.
- `IOKitHandheldReportSource` logs `handheld: listening` / `handheld: stopped` / `handheld: <name> connected|disconnected` through `AppLog.append`, and an open failure with its hex code.

- [ ] **Step 1: Write the failing tests** in `struct HandheldListenerTests` with a `FakeHandheldReportSource`:
  - `decodesAndForwards`: connect, feed the keyA single report → one `onEvent` with `.single`; `connectedDevice?.id == "whisprompt"`.
  - `dropsARepeatedSequenceOnce`: the same report twice → one event; the next sequence → a second event.
  - `aReconnectForgetsTheLastSequence`: event with sequence 0x22, disconnect, connect, event with sequence 0x22 again → two events.
  - `stopReportsADisconnectAndStopsTheSource`: connected, `stop()` → `onDisconnect` called, `connectedDevice == nil`, fake's `stopCount == 1`, `isRunning == false`.
  - `anUnknownDeviceIsIgnored`: connect with product `0x1234` → `connectedDevice == nil`.
- [ ] **Step 2: Run**; expected: build fails.
- [ ] **Step 3: Implement.** In the IOKit source the input-report buffer is allocated per device and freed on removal; the C callbacks hop to the main actor before calling the closures.
- [ ] **Step 4: Run**; expected: 5 cases pass.
- [ ] **Step 5: Commit** `Handheld: listen to a supported device's reports, only while asked to`.

### Task 4: The entry point and the action runner

**Files:**
- Modify: `OpenClicky/CompanionManager.swift` (beside `handleCompanionShortcut`, about line 1460)
- Create: `OpenClicky/Plugins/Handheld/HandheldActionRunner.swift`
- Test: `OpenClickyTests/HandheldActionRunnerTests.swift`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces in `CompanionManager`:
  ```swift
  /// A shortcut from something other than the keyboard (a handheld's button). Takes the keyboard's own path.
  func performExternalShortcut(_ shortcutEvent: CompanionShortcutEvent)
  ```
  `.talkPressed` / `.talkReleased` go to `handleShortcutTransition(.pressed / .released)`; every other event to `handleCompanionShortcut`. Logs `external shortcut: <event>`.
- Produces:
  ```swift
  @MainActor protocol HandheldActionHost: AnyObject {
      func performExternalShortcut(_ shortcutEvent: CompanionShortcutEvent)
      func postKeyCombination(_ combination: HandheldKeyCombination)
      func openIsland()
      func goToWaitingTerminal()        // Task 8 fills it; a no-op that logs until then
  }
  @MainActor final class HandheldActionRunner {
      static let askToggleLimitSeconds: TimeInterval = 60
      init(host: HandheldActionHost, schedule: @escaping (TimeInterval, @escaping () -> Void) -> HandheldScheduledWork)
      func handle(_ event: HandheldEvent, bindings: HandheldBindings)
      func releaseEverythingHeld(reason: String)     // called on disconnect and when the plugin stops
  }
  protocol HandheldScheduledWork { func cancel() }
  ```
- Mapping: `dictateHold` on `longStart` → `.dictationPressed`, on `longEnd` → `.dictationReleased(wasTap: false)`; `dictateToggle` → `.dictationPressed` then `.dictationReleased(wasTap: true)`; `askHold` on `longStart` → `.talkPressed`, `longEnd` → `.talkReleased`; `askToggle` → first press `.talkPressed`, second `.talkReleased`, released by the runner after 60 s; `handsFreeToggle` → `.handsFreeToggleRequested`; `typeQuestion` → `.textComposerRequested`; `stop` → `.escapePressed`. A `longEnd` runs the release of whatever its control's `longStart` started, whatever the bindings say by then. `down` runs nothing.
- `CompanionManager` conforms to `HandheldActionHost` in an extension inside `Plugins/Handheld/`: `postKeyCombination` posts key down and up from a private event source stamped with `FrontAppTextInserter.liveTypingEventMarker` (the monitor already ignores those: Review Focus 3); `openIsland` uses the island's existing open call.

- [ ] **Step 1: Write the failing tests** in `struct HandheldActionRunnerTests` with a recording fake host and a manual scheduler:
  - `holdDictationIsPressThenRelease`: longStart, longEnd on keyA (defaults) → `[.dictationPressed, .dictationReleased(wasTap: false)]`.
  - `toggleDictationIsATap`: keyA single → `[.dictationPressed, .dictationReleased(wasTap: true)]`.
  - `askToggleAlternatesAndReleasesItselfAfterSixtySeconds`: bind keyB single to `.askToggle`; one press → `[.talkPressed]`; firing the scheduled work → `.talkReleased`; a press, then a second press → pressed, released, and the scheduled work was cancelled.
  - `aHoldEndsWhenTheDeviceGoes` (Review Focus 1): longStart then `releaseEverythingHeld` → `.dictationReleased(wasTap: false)`; the same for `askHold` and for an open `askToggle`; a second call sends nothing.
  - `longEndReleasesWhatLongStartBegan`: longStart under `dictateHold`, bindings changed to `.nothing`, longEnd → still released.
  - `keyCombinationGoesToTheHostNotTheShortcuts` (Review Focus 3): keyB single → host's `postedCombinations == [Enter]`, no shortcut events.
  - `downAndNothingDoNothing`.
- [ ] **Step 2: Run**; expected: build fails.
- [ ] **Step 3: Implement** the runner, the method and the conformance.
- [ ] **Step 4: Run** this suite and `CompanionShortcutRecognizerTests`; expected: 7 new cases pass, the old suite unchanged.
- [ ] **Step 5: Commit** `Handheld: bound actions run through the keyboard's own shortcut path`.

### Task 5: The plugin, its switch, and the checks with the device

**Files:**
- Create: `OpenClicky/Plugins/Handheld/HandheldPlugin.swift`
- Modify: `OpenClicky/CompanionManager.swift` (`start()`: one line, `HandheldPlugin.shared.attach(to: self)`)
- Test: `OpenClickyTests/HandheldPluginTests.swift`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:
  ```swift
  @MainActor final class HandheldPlugin: ObservableObject {
      static let enabledKey = "openclicky.handheld.enabled"
      static let shared: HandheldPlugin
      init(defaults: UserDefaults, makeSource: @escaping () -> HandheldReportSource, makerAppIsRunning: @escaping () -> Bool)
      @Published private(set) var isEnabled: Bool
      @Published private(set) var connectedDevice: HandheldDevice?
      @Published private(set) var lastSeenDevice: HandheldDevice?      // id kept under "openclicky.handheld.lastDevice"
      @Published var bindings: HandheldBindings                        // of connectedDevice ?? lastSeenDevice; saved on set
      var makerAppIsAlsoRunning: Bool { get }                          // NSRunningApplication, bundle id ai.whisprompt.desktop
      func attach(to host: HandheldActionHost)                         // starts only if isEnabled
      func setEnabled(_ enabled: Bool)
      func resetBindings()
  }
  ```
- The report source is created by `makeSource` only inside the enabled path and dropped when disabled.

- [ ] **Step 1: Write the failing tests** in `struct HandheldPluginTests`:
  - `offMeansNoSourceIsEverMade`: attach with the switch off → `makeSource` call count 0.
  - `switchingOnStartsAndOffStopsAndReleases`: on → 1 source, started; feed longStart; off → host got the release, source stopped.
  - `aPressRunsItsBinding`: on, connect, keyA single → host got the tap pair.
  - `bindingsEditedArePersistedForTheDevice`.
- [ ] **Step 2: Run**; expected: build fails. **Step 3: Implement. Step 4: Run**; expected: 4 cases pass.
- [ ] **Step 5: Checkpoint with Prasanth and the device** (Debug build, `defaults write org.openclicky.app.debug openclicky.handheld.enabled -bool true`, maker's app quit, read `~/Library/Logs/OpenClicky/app.log`):
  1. Which physical key is `keyA`, `keyB`, `keyC`? Fix the names in `WhisPromptHandheld.swift` and its tests if the guess was wrong.
  2. Does the Debug app raise an Input Monitoring prompt when the listener starts? Record the answer in the spec.
  3. Status report 3, kind `08`, twelfth byte: does it fall as the battery drains or change when the cable is pulled? If confirmed, add `batteryPercent` decoding and a test; if not, leave battery out.
  4. Hold to dictate from `longStart`: say a sentence starting the instant the key goes down. If the first word is lost, switch `dictateHold` to start at `down` and cancel silently (`.dictationDoubleTapped` is the keyboard's cancel) when `single` or `double` follows; add the tests.
- [ ] **Step 6: Commit** `Handheld: the plugin, off unless switched on` (plus the checkpoint's fixes).

### Task 6: Settings

**Files:**
- Modify: `OpenClicky/Dictation/UI/SettingsPages.swift` (new case `.handheld`: sidebar page `.island`, title "Handheld", subtitle "What each button on your handheld does.", symbol `"dot.radiowaves.left.and.right"`, the island's tint)
- Modify: `OpenClicky/Dictation/UI/SettingsCatalog.swift`, `OpenClicky/Dictation/UI/SettingsLayout.swift`
- Create: `OpenClicky/Plugins/Handheld/HandheldSettingsRows.swift`
- Test: `OpenClickyTests/HandheldSettingsTests.swift`

**Interfaces:**
- Consumes: `HandheldPlugin`, `HandheldAction.choices`.
- Rows (ids): `handheld.enabled` — switch "Handheld devices", detail "Use a supported push-to-talk handheld with OpenClicky.", keywords handheld, device, button, whisprompt, push to talk; placed in the Island page's advanced ("More") group. `island.openHandheld` — navigation row "Handheld" to `.settings(.handheld)`, `isRelevant` only while the plugin is on, placed directly after the switch. On the page, in order: `handheld.status` (device name and "connected" / "No supported handheld is connected", with a "Reset all" button), one `handheld.binding.<controlID>.<gesture>` picker row per control and bindable gesture (labels "press", "twice", "hold"), `handheld.mouseNote` footer "The wheel and touch pad work as a mouse.", `handheld.microphone` (Task 7), `handheld.makerAppNote` footer "The WhisPrompt app is also running, so each press will do two things." relevant only while true (Review Focus 4).
- "A key or shortcut…" in a picker opens a small recorder sheet: the next key down with its modifiers becomes the combination; esc cancels.

- [ ] **Step 1: Write the failing tests** in `struct HandheldSettingsTests`:
  - `everyRowHasAPlace`: with the plugin on and the WhisPrompt device last seen, every row id above is placed by `SettingsLayout` (the existing "rows with no place" check returns none).
  - `offShowsOnlyTheSwitch`: plugin off → of all `handheld.*` and `island.openHandheld` items only `handheld.enabled` is relevant, and it is in an `isAdvanced` group.
  - `aControlGetsOnePickerPerBindableGesture`: keyA → press, twice, hold; keyB → press, twice.
  - `theMakerAppNoteFollowsTheApp`.
- [ ] **Step 2: Run**; expected: fails. **Step 3: Implement**, following how `island.openCalendars` and the `.calendars` page are built. **Step 4: Run** this suite plus the existing settings layout and search suites; expected: all pass.
- [ ] **Step 5: Look at it.** `--openclicky-open-settings handheld` on the Debug build; screenshot the page connected and not connected, and Island › More with the plugin off. Fix what looks wrong.
- [ ] **Step 6: Commit** `Handheld: its switch under Island › More and a page of bindings`.

### Task 7: The device's microphone

**Files:**
- Create: `OpenClicky/Plugins/Handheld/HandheldMicrophone.swift`
- Modify: `OpenClicky/CompanionManager.swift` (`startDictation`, the three `preferredMicrophoneUID` assignments, about lines 1448–1455)
- Modify: `OpenClicky/Plugins/Handheld/HandheldSettingsRows.swift` (the `handheld.microphone` switch: "Use its microphone while it is connected", detail "For dictation and ⌃⌥ questions. Hands-free keeps your own microphone.")
- Test: `OpenClickyTests/HandheldMicrophoneTests.swift`

**Interfaces:**
- Produces:
  ```swift
  enum HandheldMicrophone {
      static let useDeviceMicrophoneKey = "openclicky.handheld.useDeviceMicrophone"   // false by default
      /// The microphone a lane records from: the handheld's while the plugin is on, the option is on, the device is
      /// connected and its microphone is present; otherwise the person's own choice, untouched.
      static func effectiveUID(personsChoiceUID: String?, pluginIsOn: Bool, optionIsOn: Bool,
                               connectedDeviceMicrophoneName: String?, availableMicrophones: [MicrophoneDevice]) -> String?
  }
  ```
- `CompanionManager` applies `effectiveUID` to `dictationTakeController.dictationManager` and `buddyDictationManager` only; `realtimeVoiceClient` keeps the person's choice. It is re-applied when the plugin's `connectedDevice`, the option, or the person's choice changes. `DictationSettings.preferredMicrophoneUID` is never written by this.

- [ ] **Step 1: Write the failing tests** in `struct HandheldMicrophoneTests`: device microphone chosen when all four conditions hold; the person's choice returned when the plugin is off, the option is off, no device is connected, or the named microphone is not in `availableMicrophones`; a nil choice stays nil in those cases.
- [ ] **Step 2: Run**; fails. **Step 3: Implement. Step 4: Run** this suite and the existing microphone suites; pass.
- [ ] **Step 5: By hand with Prasanth:** option on, dictate (log shows the handheld's microphone), pull the cable between takes (next take uses his own microphone), pull it during a take (the take ends, nothing crashes; check for a new `.ips` in `~/Library/Logs/DiagnosticReports`).
- [ ] **Step 6: Commit** `Handheld: its microphone for takes and questions while it is connected`.

### Task 8: Terminal notices (Coding agents plugin)

**Files:**
- Modify: `OpenClicky/Plugins/CodingAgents/AgentSessions.swift` (new activity, Claude scratch filter, background interval)
- Create: `OpenClicky/Plugins/CodingAgents/AgentSessionNotifier.swift`
- Create: `OpenClicky/Plugins/CodingAgents/TerminalWindowFinder.swift`
- Modify: `OpenClicky/NotchHUD.swift` (`syncOptionalComponents`, about line 866: start or stop the notifier), `OpenClicky/Dictation/UI/SettingsCatalog.swift` + `SettingsLayout.swift` (the switch), `OpenClicky/Plugins/Handheld/HandheldPlugin.swift` (`goToWaitingTerminal`)
- Test: `OpenClickyTests/AgentSessionNotifierTests.swift`, additions to the existing agent session parser tests

**Interfaces:**
- `AgentActivity` gains `case awaitingToolResult(since: Date?)`: the tail's last record is an assistant tool request with no result after it (Claude: a `tool_use` block with no later `tool_result`; Codex: a function call item with no output). Its `line` stays what `.tool` shows. The parser takes the record's own timestamp for `since`.
- `AgentSessionParser.isOwnScratchRollout(cwd:)` is applied to Claude sessions too.
- `AgentSessionWatcher.backgroundInterval` becomes a property: 5 s while notices are on (`var noticesAreOn: Bool`), else 20 s.
- Produces:
  ```swift
  @MainActor final class AgentSessionNotifier {
      static let enabledKey = "openclicky.agents.notices"            // false by default
      static let waitingAfterSeconds: TimeInterval = 10
      init(post: @escaping (IslandNotice, (() -> Void)?) -> Void, focusTerminal: @escaping (AgentSession) -> Void, now: @escaping () -> Date)
      func observe(_ sessions: [AgentSession], liveIDs: Set<String>)  // called with every publish of the watcher
      private(set) var newestSessionNeedingAttention: AgentSession?
      func reset()
  }
  enum TerminalWindowFinder {
      /// Pure: the window to raise for a project among the on-screen windows of terminal and editor apps.
      static func bestWindow(forProject project: String, among windows: [(ownerName: String, ownerPID: pid_t, title: String)]) -> pid_t?
      static func bringForward(_ session: AgentSession)    // bestWindow, else the terminal app most recently in front; AX raise + activate
  }
  ```
- Notices: finished → `IslandNotice(id: "agent-done-<session id>", tone: .done, title: "<Agent name> finished", detail: project, actionTitle: "Go to it", seconds: 10)` when a session that was live and working is now `.idle`. Waiting → `id: "agent-waiting-<session id>-<since>"`, `tone: .plain`, title "<Agent name> may be waiting for you", `seconds: 14`, when `awaitingToolResult(since:)` is 10 s or more old.
- Rules (Review Focus 2): the first `observe` after start or `reset` only records states; one notice per notice id ever; when more than two notices would be posted from one `observe`, post one: title "3 sessions finished" (the count), detail the project names joined by ", ", no action.
- Settings: `plugins.agents.notices` switch "Tell me when a session finishes or needs me", detail "Needs Coding agents on. A waiting session is a guess from its log.", relevant only while the Coding agents plugin is on, placed under that plugin's row. Sound: `DictationEarcons`' existing done tone when the dictation sounds switch is on.

- [ ] **Step 1: Write the failing tests.** Parser fixtures: a Claude tail ending in `tool_use` → `.awaitingToolResult`; the same followed by `tool_result` → not; a Codex equivalent; a Claude session whose cwd is the scratch root is skipped. `struct AgentSessionNotifierTests`: `theFirstLookIsABaseline`; `finishingPostsOnceWithTheProject`; `waitingPostsOnlyAfterTenSeconds` (9 s → none, 10 s → one, a later observe → no second); `aNewWaitInTheSameSessionPostsAgain` (different `since`); `aBurstBecomesOneNotice` (three finishing at once → one notice titled "3 sessions finished"); `resetMakesTheNextLookABaselineAgain`. `TerminalWindowFinder.bestWindow`: a Terminal window titled "… — openclicky — …" wins over an unrelated one; no match → nil; a non-terminal app's window with the project in its title is ignored.
- [ ] **Step 2: Run**; fails. **Step 3: Implement.** The notifier is created in `NotchHUDManager` only while both switches are on and subscribes to the watcher's `$sessions`. Terminal and editor apps for the finder: Terminal, iTerm2, Warp, Ghostty, WezTerm, kitty, Alacritty, Visual Studio Code, Cursor, Zed (by bundle id).
- [ ] **Step 4: Run** the new suite and the existing agent session suites; pass.
- [ ] **Step 5: By hand:** with the switch on, run a Claude Code session in a terminal: let it finish (notice, "Go to it" raises that terminal), make it ask permission for a command (the "may be waiting" notice within about 15 s); count false "waiting" notices over a normal half hour of use. More than one false notice: remove the waiting notice, keep "finished", and say so in the spec and the switch's detail.
- [ ] **Step 6: Commit** `Coding agents: say when a terminal session finishes or may be waiting`.

### Task 9: Whole-feature check and the written record

**Files:**
- Modify: `macos/OpenClicky/CLAUDE.md` (Key Files rows for `Plugins/Handheld/` and the two new Coding agents files; the plugin registry row's note that Handheld is not an island component), `CHANGELOG.md` (Unreleased, under the Mac heading), the spec (answers from Task 5's checkpoint and Task 8's by-hand count)
- Create: `docs/handheld-hand-test.md`

- [ ] **Step 1: Run the whole Mac test suite**; expected: every suite passes, with the new suites' case counts in the xcresult. A timing flake in a known flaky test is rerun once and named.
- [ ] **Step 2: Off means off.** Fresh Debug profile, plugin never switched on: `app.log` has no `handheld:` line, no HID manager in a sample of the process (`sample` output has no `IOHIDManager` frames), Island page shows nothing but the folded switch.
- [ ] **Step 3: Write `docs/handheld-hand-test.md`:** the checks only Prasanth can do on a `release.sh --no-notarize` build: each default binding in a scratch TextEdit document (focus checked before each press), Enter / Delete / ⌘Z landing, a recorded shortcut, both apps running, the Input Monitoring behaviour of the Developer ID build, sleep and wake with the device plugged in, the microphone option.
- [ ] **Step 4: Commit** `Handheld: notes for agents, changelog and the hand test`.
- [ ] **Step 5: Stop.** Report what was verified by test, what was seen on screen, and what waits on the hand test. No merge, push or release without being asked.
