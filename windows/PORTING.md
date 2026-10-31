# OpenClicky for Windows — porting guide

The Windows app is a port of `macos/OpenClicky` (Swift/SwiftUI, ~35k lines) to **C# / .NET 8 / WPF**.
Goal: **the same features, in Windows idioms**. Every Swift file has a home below. When porting a file,
read the Swift source (and its tests in `macos/OpenClicky/OpenClickyTests/`) and keep its behaviour,
its rules and its numbers (timeouts, limits, thresholds, prompts, sentences shown to the person).
Prompts and user-facing sentences are copied verbatim unless they name a Mac thing (then say the
Windows thing: "control + windows" instead of "control + option", "the tray" instead of "the menu bar").

`agent/` and `backend/` (TypeScript) are unchanged and already run on Windows; the app drives the
`openclicky` CLI exactly as the Mac app does.

## Layout

```
windows/
  OpenClicky.Windows.sln
  Directory.Build.props            shared: nullable, implicit usings, LangVersion latest, warnings
  src/OpenClicky.Core/             net8.0 class library — all logic with no Win32/WPF in it
  src/OpenClicky.Windows/          net8.0-windows10.0.19041.0, WPF (+ WinForms only for NotifyIcon); the exe
  tests/OpenClicky.Core.Tests/     xUnit; ports of the Swift unit tests for Core
  installer/                       Velopack packaging + release script
```

Core may not reference WPF, WinForms, WinRT or P/Invoke. Anything that needs the OS is an interface
in `OpenClicky.Core/Platform/` implemented in `OpenClicky.Windows/Platform/`.

### Namespaces / folders (Core)

| Folder (`OpenClicky.Core.*`) | Swift sources it ports |
|---|---|
| `Configuration` | OpenClickyConfiguration, AppBundleConfiguration, ReplyLanguage, AppRegion, DeviceIdentity, AppLog (logic), Preferences (UserDefaults replacement) |
| `Providers` | ProviderRoute (ProviderRouter, DirectProviderRequests, ProviderCredentials), BackendSignIn, OpenClickyAuthSession, BillingStatus, AccountCapabilities, ClickyAnalytics |
| `Answers` | AnswerProvider, AnswerProviderFactory, AnswerSourceCheck, ClaudeAPI, OpenAIChatAnswerProvider, GeminiAnswerProvider, ClaudeCodeAnswerProvider, CodexAnswerProvider, PlanCLIs, PlanToolProtocol, GuidedSteps (rules), SpokenChunker, WriteInPlace (logic), Farewell |
| `Speech` | BuddyTranscriptionProvider (interfaces), AssemblyAI / OpenAI upload / Sarvam providers, SarvamSpeechClient, BuddyAudioConversionSupport (PCM16, WAV, silence gate), RealtimeVoiceClient (protocol + session logic), SpeechVoices (CloudVoiceRequests, AWSSignatureV4, SpeechVoiceChoice), SpeechQueue, AnswerVoicePlayer (logic), SpokenStretches, Sherpa/Kokoro/Parakeet (logic + model download) |
| `Dictation` | DictationSettings, DictationSpace, DictationHotkey (Windows keys), TakeFormatter, SpokenNumbers, TakePolishers, TakeStore (SQLite), TakeAudioStore, LiveTyping (pure parts), DictationTakeController, DictationEngineResolver, HeyClickyEditor, DictationCapturing seams |
| `Shortcuts` | CompanionShortcutRecognizer (pure state machine, Windows keys) |
| `Skills` | SkillFile, SkillLibraryStore, AppSkillMatcher, SkillPromptBuilder |
| `Agent` | OpenClickyAgentClient, AppConnectPrompt (logic) |
| `Pointing` | CursorFlightPlanner, ScreenTextLocator (ranking), ScreenElementGrounder, AccessibleElementLocator (ranking), BrowserTabLocator (ranking), SystemPromptGuard (rules), PointerPresence, HUDPlacement, NotchGeometry→IslandGeometry |
| `Actions` | MacActions → `DesktopActions` (open_app, open_url, create_folder, reveal_in_finder→reveal_in_explorer, set_volume, media_control) — schemas + validation; execution through `IDesktopActionExecutor` |
| `Companion` | CompanionManager (the orchestrator), CompanionVoiceState, FirstRunTour (state/logic), SettingsTour (steps), PermissionPrompt (logic) |
| `Platform` | the OS interfaces (below) |

### Folders (Windows exe, `OpenClicky.Windows.*`)

| Folder | What |
|---|---|
| `Platform` | implementations of Core/Platform: keyboard hook, audio capture/playback, screen capture, UI Automation, text insertion, clipboard, OCR, Windows speech recognition, system voice, frontmost app, browser tabs, login item, network, desktop actions, earcons |
| `Overlay` | OverlayWindow (the pointer buddy, caption, waveform, spinner, flights) — one click-through window per monitor |
| `Island` | NotchHUD + NotchHUDPanels + PermissionPrompt card + composer + agent result card + AppConnect card — a top-center island on each monitor |
| `Orb` | OrbPanel |
| `MainWindow` | DictationWindow, every Settings page, WordsPage, WritingPage, AccountSheet, EmailAccountForm, KeyboardSketch, SettingsTour targets |
| `Tour` | FirstRunTour cards + views |
| `Tray` | MenuBarPanelManager → NotifyIcon + context menu |
| `Theme` | DesignSystem, OpenClickyMark, SettingsLook, icons |
| root | `App.xaml(.cs)`, `Program.cs` (single instance, helper-process flags, smoke flags), composition root |

## Mac → Windows mapping

| Mac | Windows |
|---|---|
| `~/.openclicky/shell.json` (shared with the CLI) | `%USERPROFILE%\.openclicky\shell.json` — same file, same keys |
| `~/.openclicky/dictation`, `~/.openclicky/skills` | same under `%USERPROFILE%\.openclicky\` |
| `~/Library/Application Support/OpenClicky/` (dictation.sqlite, models) | `%LOCALAPPDATA%\OpenClicky\` |
| `~/Library/Logs/OpenClicky/app.log` | `%LOCALAPPDATA%\OpenClicky\Logs\app.log` (same 2 MB truncation) |
| UserDefaults | `Preferences` — JSON at `%LOCALAPPDATA%\OpenClicky\preferences.json`, same key names |
| Menu bar `NSStatusItem` + `NSMenu` | tray `NotifyIcon` + context menu (same items) |
| The notch island | a top-center island on each monitor; collapsed it is the Mac's "thin handle line on displays without a notch" |
| ⌃ control | Ctrl |
| ⌥ option | Win (the Windows key) |
| Talk: hold ⌃⌥ | hold **Right Ctrl + Win** (the dictation Ctrl; left Ctrl + Win works too) |
| Text: tap ⌃ twice | tap **Ctrl** twice |
| Hands-free: dictation key + ⌃ twice | dictation key + **Ctrl** twice |
| Dictation key: fn (default), right ⌥, right ⌘, right ⌃ | **Right Ctrl** (default), Right Alt, Right Shift, Caps Lock — fn is invisible to Windows. A right-hand key's own flag is taken out of the companion shortcuts exactly as on the Mac (left/right told apart by the hook's vk codes) |
| ⌘V paste | Ctrl+V via `SendInput` |
| CGEvent tap (listen-only) | `WH_KEYBOARD_LL` low-level hook on a dedicated thread with its own message loop; never swallows keys except where the Mac swallowed them; ignores events stamped with our `dwExtraInfo` marker (live typing) |
| `CGEventKeyboardSetUnicodeString` live typing | `SendInput` with `KEYEVENTF_UNICODE`, ≤ 20 UTF-16 units per batch, `dwExtraInfo` = marker |
| Accessibility (AX) focused element, value, selection, secure field | UI Automation (`UIAutomationClient` COM via `System.Windows.Automation` or `Interop.UIAutomationClient`): focused element, `ValuePattern`/`TextPattern`, `IsPassword`, ControlType Edit/Document/ComboBox |
| `IsSecureEventInputEnabled` | focused element `IsPassword` (no global equivalent) |
| ScreenCaptureKit | GDI `BitBlt` per monitor (per-monitor-v2 DPI aware) — real pixel size recorded, own windows excluded with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on overlay/island/orb windows |
| Vision OCR | `Windows.Media.Ocr` (upscaled 2× as on the Mac) |
| CGWindowList (z-order, other windows) | `EnumWindows` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` + cloaked check |
| SystemPromptGuard (SecurityAgent…) | UAC runs on the secure desktop (cannot be pointed at); guard `consent.exe`, `CredentialUIBroker.exe`, `LockApp.exe` and the Windows Security credential dialog windows |
| AppleScript browser tab list | UI Automation over Chrome/Edge/Brave/Firefox tab strips (TabItem names), URL from the address bar Edit's value |
| Bundle id (`com.apple.Safari`) for app skills | process image name (`chrome.exe`, `msedge.exe`, `Code.exe`…); `app-skills` frontmatter `apps:` gets Windows exe names added alongside the bundle ids (Core matcher accepts both) |
| `NSWorkspace` frontmost app | `GetForegroundWindow` → pid → process name/path, `WinEventHook(EVENT_SYSTEM_FOREGROUND)` |
| AVAudioEngine mic | NAudio `WasapiCapture` (shared mode) → resample to 16 kHz/24 kHz mono PCM16 as each consumer needs |
| AVAudioPlayer | NAudio `WaveOutEvent` / `WasapiOut` |
| Realtime echo cancellation (voice processing) | WASAPI communications capture (`AudioCategory_Communications`) when available, plain capture as fallback |
| Apple Speech (offline engine) | `Windows.Media.SpeechRecognition` (continuous dictation, needs the language pack) |
| Parakeet / Kokoro via sherpa-onnx dylib | sherpa-onnx's .NET package (`org.k2fsa.sherpa.onnx`), in the same helper-process design: the exe re-launched with `--openclicky-kokoro-helper` / `--openclicky-parakeet-helper` / `--openclicky-moonshine-helper`, one JSON line each way. Moonshine tiny (English) ships inside the app (`Assets\Models`, fetched by `scripts/fetch-speech-model.ps1`) and is the default dictation engine until Parakeet downloads |
| Apple Foundation Models polish | none on Windows: skip that rung of the polish ladder |
| AVSpeechSynthesizer ("this Mac" voice) | `Windows.Media.SpeechSynthesis.SpeechSynthesizer` (OneCore voices) rendered to a WAV stream → same SpeechQueue — called "this PC" |
| Sparkle updates | Velopack (`UpdateManager` against GitHub Releases) |
| Move to Applications from the dmg | Velopack Setup.exe installs per-user; DiskImageSelfInstaller is dropped |
| SMAppService login item | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value |
| FnKeyGuard (free fn from Emoji) | dropped — no equivalent |
| Permissions: Mic, Accessibility, Screen Recording, Screen Content | only **Microphone** exists on Windows (Settings → Privacy → Microphone; detected via `ConsentStore` registry + a failed capture). The permission cards show only that step; AccessibilityDragPanel and the relaunch flows are dropped |
| Haptics | none |
| `open -n` relaunch | `Process.Start(Environment.ProcessPath)` then exit |
| SF Symbols | Segoe Fluent Icons / Segoe MDL2 Assets glyphs |
| System fonts | Segoe UI Variable |

## Packages (pinned in the csproj files — do not add others without a reason in the PR)

- Core: `Microsoft.Data.Sqlite`, `CommunityToolkit.Mvvm` (ObservableObject for models the UI binds to)
- Windows: `NAudio`, `WPF-UI` (Fluent NavigationView / controls for the main window), `Velopack`, `org.k2fsa.sherpa.onnx`, `H.NotifyIcon.Wpf` not used — WinForms `NotifyIcon` instead
- Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`

## Conventions

- Names as the Swift code names them (PascalCase): `DictationTakeController`, `TakeFormatter.Format`, … so a reader can follow a Swift file and its C# twin side by side. Clear, long names; no single-letter variables (same rule as the Mac AGENTS.md).
- One C# file per Swift file where practical, same base name.
- `async`/`await` everywhere; UI state changes on the WPF dispatcher (Core models raise `PropertyChanged` and the Windows side marshals; Core gets an `IMainThread` to post to).
- Swift `@Published` state → `CommunityToolkit.Mvvm` `ObservableObject` + `[ObservableProperty]`.
- `HttpClient`: one shared instance per vendor (the Mac's shared-URLSession lesson applies to `ClientWebSocket` reconnection too).
- JSON: `System.Text.Json` with the exact property names the Mac app writes (camelCase), so shell.json and the dictation JSON files are interchangeable between platforms.
- Every Swift unit test that tests logic now in Core gets an xUnit twin in `tests/OpenClicky.Core.Tests`.
- Logs: `AppLog.Write("route: …")` with the same line prefixes as the Mac (`route:`, `point_at`, `voice:`, `write in place:`…). Never log key material or dictated words.

## Build

```powershell
cd windows
dotnet build OpenClicky.Windows.sln -c Debug
dotnet test tests/OpenClicky.Core.Tests
dotnet run --project src/OpenClicky.Windows
```

## Mac parity since the port (updated 2026-10-28)

What the Mac gained after the port (git log da71df7..HEAD -- macos), and where Windows stands.

| Mac feature | Windows |
|---|---|
| Island: hover peeks, a click opens the panel | Done |
| Island: components in the panel's slot, a capsule to flip them, Settings → Plugins (default, order) | Done (framework; `Core/Companion/IslandComponents.cs`) |
| Plugin: System (CPU, memory, disk, battery rings) | Done (`Core/Plugins/SystemStats`, `Windows/Plugins/SystemStats`) |
| Island: transcript and agent components (the moment picks them), "follow up" on an agent thread | Done (`CompanionManager.IslandContext.cs`) |
| Plugin: Shelf (drop on the island, drag out, copy) | Done (`Core/Plugins/Shelf`, `Windows/Plugins/Shelf`; the collapsed island catches drags with an alpha-1 rectangle only while the Shelf is on) |
| Plugin: Coding agents (Claude Code / Codex sessions, plan limits) | Done (`Core/Plugins/CodingAgents`, `Windows/Plugins/CodingAgents`): logs under `%USERPROFILE%\.claude\projects` and `.codex\sessions`; plan limits from Claude Code's `.credentials.json` (the Mac's Keychain item); the peek line names a live session |
| Plugin: Now playing (artwork, progress, controls) | Done: Windows' media sessions, so every player that reports to Windows (`Core/Plugins/Media`, `Windows/Plugins/Media`) |
| Plugin: Teacher (draws lessons on screen) | Done (`Core/Plugins/Teacher`, `Windows/Plugins/Teacher`; both lanes' tools, early lessons, written answers when they cannot hear; handwriting in Ink Free; `--openclicky-teacher-smoke`) |
| Island: glass under the open panel | Done as the three styles (`Core/Companion/IslandAppearance.cs`, `openclicky.island.style`, Liquid Glass by default): a layered window cannot blur, so glass and blend are dark see-through tints, not frosted |
| Island: home dashboard (PR #36): now playing, calendar, weather and System tiles side by side, the island growing to fit | Done (`Core/Companion/IslandHome.cs`, `Windows/Island/IslandHomeView.cs`); home is the default component and the moment while music plays; the capsule leaves out the tools and what home shows |
| Island: hover grows sideways (or downwards) | Done: sideways is a wide 22-high strip along the top edge (no camera to split around), `openclicky.island.peekDirection` |
| Island tools: Shelf, Clipboard, Notes, Timer as round buttons in the band | Done (`Core/Plugins/{ClipboardList,Notes,FocusTimer}`, `Windows/Plugins/…`); Notes borrows the keyboard like the composer; the clipboard list skips copies marked private (and the dictation paste) |
| Home: Weather tile (Open-Meteo) | Done (`Core/Plugins/Weather`): location from `Windows.Devices.Geolocation` (no prompt for an unpackaged app; refused opens the privacy page), or a typed city; the town is named only when the location provider gives a civic address |
| Home: Calendar column | Done, read-only (`Core/Plugins/Calendar`, `Windows/Plugins/Calendar/WindowsCalendarSource.cs`, the appointment store, checked unpackaged on Windows 11 2026-10-30); no quick add (Windows lets only packaged apps write appointments); a click brings the Calendar app forward (no link to one event); refused or unavailable says so |
| Island settings rows (style, direction, weather, calendar, timer) | Core objects ready (`IslandAppearanceSettings`, `WeatherSettings`, `CalendarSettings`, `TimerSettings`); the rows are wired with the six-page Settings rework |
| Island: docking the pointer removed (2bf8edb) | Done: no dock button, tray item or setting; a stored dock flag is cleared once (CursorFlightPlanner still knows ToDock, unused) |
| Ask lane: simple requests answered on the device (Apple's on-device model) | Mac only for now: no Windows equivalent chosen |
| Voice: minimize everything / bring it back (one app by name), web search, stop that song (pause/play on the player) | Done (`Core/Actions`, `Windows/Platform/DesktopActionExecutor.Extras.cs`: Win32 windows, Windows media sessions) |
| Voice: play a song in Spotify by name; own Spotify credentials in Keys & Plans | Done, unverified live: Windows opens the `spotify:track:` URI and confirms playback through Spotify's media session; unconfirmed, it says "press play" (no AppleScript `play track`) |
| Voice: check email, draft one (never sent); Settings → Voice → Email | Done for Gmail and Outlook on the web (Windows has no Apple Mail to script): the inbox opens and is read from a fresh screenshot |
| Voice: act, then look again; the model told more than the person (toolOutput / logDescription) | Done (`RealtimeVoiceClient.SettleDelay`, `RealtimeToolResult.ModelOutput`), OpenAI and Gemini Live alike |
| Prompts: a command is done, a question is taught, email is never sent (070fbd8) | Done (`AskLanePrompts.CommandOrQuestionInstruction`) |
| Ask lane: pause before sending, Esc stops it, mouse close-up, [UNSURE], window buttons | Done: Esc and the island's strip stop the ask lane (`AskLaneEscape`, `CompanionManager.StopAskLane`; order take → ask lane → fast voice); 1.2 s pause before the capture, Settings → Privacy "Pause before sending my screen" (`AskSendPause`); off-target click check in guides, in DIPs (`GuidedStepClickCheck`); `[UNSURE]` (`UnsureMarker`); pointer place + 480 px close-up + repeated question (`AskPointerContext.cs`, crop by `IScreenCapture.CropJpeg`); caption buttons top right from DWM or the window rect (`WindowControlLocator`, in the fast lane's point_at), a background browser matched on the person's words (`BrowserTabLocator.MatchTexts`). No colour words for the buttons; a guide's "what's next?" is never a repeated question. Needs a live check |
| Hands-free without a realtime key: the standard lane listens on this PC (066eefb) | Done (`Core/Speech/StandardHandsFree.cs`, `CompanionManager.StandardHandsFree.cs`); unverified on a live microphone |
| Hands-free ends on a goodbye | Done: the standard lane by fixed sign-offs (`HandsFreeSignOff`); fast voice (OpenAI and Gemini Live) offers `stop_listening` in hands-free only, and ends after the goodbye has played (`RealtimeVoiceClient`, `CompanionManager.EndHandsFreeBySignOff`); unverified live |
| Esc stops the fast voice lane: a reply, or an action still working on its look (a429390, b9dbcc8) | Done (`RealtimeVoiceClient.IsBusy` / turn generation, `CompanionManager.EscStopsFastVoice`), OpenAI and Gemini Live alike; a dictation take still keeps esc first |
| Rephrase in place | Done: the ask lane had it; fast voice now gets `write_in_place` and its turn quotes the highlight or text box (`RealtimeVoiceClient.ScreenCaption`), OpenAI and Gemini Live alike. A web page with nothing clicked (UIA Document whose text is read-only or mixed) is no longer a text box (`Core/Platform/FocusedFieldEditability.cs`); a terminal is never copied from. With nothing highlighted, an editable box that reports no text (the Mac's Apple Mail body; here a Document or generic element UIA gives no text for) is read by the model from the screenshot and selected and replaced only when it rewrites (`WritingSource.ScreenOnly`). Classic Outlook's and Chromium's boxes usually report their text, so this is the fallback. Unverified live |
| Voice: click, scroll, open folders, look for files and words on the page (009fe3d, issue #27) | Done (`Core/Actions/ScreenNavigation.cs`, `DesktopActions.cs`, `RealtimeVoiceClient` `click` with point_at's chain factored into `LocateElementAsync`; `Windows/Platform/DesktopActionExecutor.Navigation.cs`), OpenAI and Gemini Live alike. Windows: SendInput wheel notches (about 100 DIPs each) instead of pixel events; Windows Search (`SystemIndex` over ADO, no package) else a bounded walk of the home folder, AppData in place of ~/Library; Ctrl+F is never pressed in Outlook (it forwards); "not allowed" is an app running as administrator (UIPI); the own-window check is WindowFromPoint. Tabs from a8295bf (`Core/Actions/BrowserControl.cs`, `Windows/Platform/DesktopActionExecutor.Browser.cs`): a click on a web link in the browser in front is followed in its own tab (the Hyperlink among the element's first six UI Automation ancestors, its Value pattern the href, http(s) only; loaded by Ctrl+L, the address typed as live dictation types once the focus is in the address bar, Delete for an inline completion, Enter; anything else stays a mouse click); `new_tab` on `click` is Ctrl+Shift+click on a link (a new selected tab, with no typing and no blank tab left behind if a step fails) and Ctrl+click on anything else; `switch_tab` (named / next / previous / newest / first) reads the window's TabItems, selects through SelectionItem, else Ctrl+1…8 / Ctrl+9 / Ctrl+Tab while the browser is in front. Windows: no AppleScript, so only the selected tab's URL is known (the address bar) and a named tab also matches its title with the words run together ("you tube"); Chrome's hover-card suffixes (" - Memory usage - 244 MB") are cut from titles; a browser running as administrator is "not allowed". switch_tab live-checked with `--openclicky-action-smoke` in Chrome (next, previous, named, no match; selected through UI Automation); following a link and new_tab need a live check. Click fixes from 5602681: `accept` is not a risky word (cookie banners); text snapping prefers a line that is the words over a sentence mentioning them; a target Claude located counts as located and is clicked, as on the Mac; a label naming a video, thumbnail or picture (`NamesAPicture`) skips text snapping and UI Automation and takes Claude's point as-is; a spot outside the monitor's work area (the taskbar; the Mac's visible frame) is not clicked and the model is told to scroll a little and click again, except under a window that fills the monitor (full screen hides the taskbar). Scroll, open_folder and find_files checked with `--openclicky-action-smoke`; click and find_on_page need a live check |
| Voice: scroll down slowly, keep scrolling until stop (a6d4ba4, issue #29) | Done (`ScreenNavigation.GlideSteps`, `ContinuousUnitsDue`, `ContinuousScrollShouldStop`; `DesktopActions` `stop_scrolling`; `DesktopActionExecutor.Navigation.cs`; esc in `CompanionManager.Voice.cs`). Windows: a slow scroll is the page's wheel units in 45 smoothstep steps of less than a notch (Chrome and Edge move a fraction of a notch for a fraction of 120); a continuous scroll is 3 wheel units every 40 ms (about 62 DIPs a second in Chrome), timed by the clock on a background thread, so a list that only moves on whole notches moves three lines every second and a half; OpenClicky's own window coming forward does not end it (the Mac counts any other app). A click, a new scroll, esc, another app in front or the pointer moving 40 px ends it; ten minutes at most. Needs a live check |
| Voice: click on the ask lane (Windows only) | A free account has no fast voice, so `click` is also an ask-lane tool (`Core/Actions/AskLaneClick.cs`, `CompanionManager.ClickForAskLaneAsync`): Claude's point as for a [POINT] tag, the same risky-word refusal and `ClickAsync` (links in their own tab, the taskbar check); no look after the click |

## Status (2026-10-14)

### Ported

Every Swift file has its Windows home (tables above). The last pieces landed in the integration step:

- **CompanionManager** — `Core/Companion/CompanionManager*.cs`, split by concern: state, settings bridge
  (`ICompanionSettings`) and permissions (`CompanionManager.cs`), shortcuts / push-to-talk / hands-free /
  typed requests (`.Voice`), the ask lane with streamed speech, `[POINT]`, guided steps, write_in_place,
  quick actions and the notices (`.AskLane`), the agent lane and result card (`.AgentLane`), Realtime
  wiring with the point_at chain (`.Realtime`), pointing, the demos and the Settings tour's pointer
  (`.Pointing`), captions / pointer presence / quitting (`.Captions`), uses and model downloads
  (`.Onboarding`), skills / connect card (`.Skills`), the dictation take controller
  (`.Dictation`, plus `DictationHistoryActions`). Its OS needs are `CompanionServices` / `ICompanionHost`
  (`CompanionPlatform.cs`).
- **FirstRunTourController** (`Core/Companion/FirstRunTourController.cs`), the island's voice pill rules
  (`IslandVoiceDestination`), the orb adapter for the take controller (`OrbModelDictationAdapter`), the
  window's live services (`Core/Settings/LiveSettingsServices.cs`: Test buttons, voice picker and preview,
  Kokoro/Parakeet download rows, the recorded tour narration).
- **Windows glue** (`OpenClicky.Windows/Companion/`): `GlobalShortcutMonitor` (keyboard hook →
  `CompanionShortcutRecognizer`; Start / menu-bar suppression for Ctrl+Win and Right Alt; Caps Lock swallowed
  while it dictates), `GuidedStepInputSource` (WH_MOUSE_LL + the keyboard hook, own windows ignored),
  `WriteInPlaceHost` and `BrowserTabSource` adapters, `VelopackAppUpdater` (GitHub Releases).
- **App entry** — `Program.cs` (Velopack hooks → Sherpa helper flags → smoke/demo flags → single instance →
  WPF app with no main window), `AppComposition.cs` (the composition root: tray, updater, login item,
  low memory → `UnloadForMemoryPressure`, resume → `NoteSystemResumed`), `SmokeCommands.cs`.
- **Installer** — `installer/build-release.ps1` (publish win-x64 + win-arm64 self-contained, `vpk pack`,
  optional deltas, `-Publish` → `vpk upload github`).

### Verified live (ARM64, one 3024×1964 monitor at 200 %, no microphone or speakers)

Screenshots in `docs/screenshots/app-*.png` (taken with `--openclicky-capturable-windows`; the floating
windows are otherwise excluded from screen captures):

- Starts without exceptions (`launch: ready` in `%LOCALAPPDATA%\OpenClicky\Logs\app.log`); the island at the
  top, the pointer overlay following the mouse, the tray icon and its menu (right click).
- A second launch forwards to the first, which opens the window (`app-window-from-second-launch.png`).
- Ctrl tapped twice (SendInput) opens the composer; a typed question goes down the ask lane (screenshot →
  write-in-place probe → route) and, with no keys, the caption says what is missing (`app-ask-missing-key-caption.png`).
- Ctrl+Win held starts the talk lane; with no microphone the caption says so (`app-talk-no-microphone.png`);
  letting go of Win did not open Start.
- Quit from the tray: the goodbye types into the island, then the app and its Kokoro helper exit.
- `installer/build-release.ps1 -Runtimes win-arm64` produced `Releases\win-arm64\OpenClicky.Windows-win-arm64-Setup.exe`
  (+ full nupkg and portable zip). Setup.exe was not run here.
- `dotnet build` clean; `dotnet test` 1025 passing.

### Known gaps

- **Not exercised live** (no keys, microphone or speakers here): a real answer and its voice, pointing from an
  answer, Realtime, the agent lane, dictation takes, the welcome tour (skipped through preferences for the run),
  the Settings tour, an update through Velopack, installing from Setup.exe.
- Tray **left click** from the notification-area overflow shows only the tooltip; right click opens the menu.
- `OrbSettings` (Core/Companion/OrbModel.cs) still reads the orb's keys itself instead of binding
  `DictationSettings` (same preference keys, so they agree).
- The Mac's DEBUG `UITour.swift` is not ported (`--openclicky-ui-demo` and `--openclicky-window-demo --shots`
  cover it), nor `--openclicky-smoke-talk(-file)`.
- No speech-recognition, Accessibility, Screen Recording or Screen Content cards, no fn freeing: Windows has
  only the microphone card.
- Velopack installs into `%LOCALAPPDATA%\OpenClicky.Windows` (pack id `OpenClicky.Windows`) and deletes that
  folder on uninstall, so the pack id must not be `OpenClicky`, whose folder holds the app's data and models.
- No code signing yet (`vpk pack` warns; SmartScreen asks on first run).
