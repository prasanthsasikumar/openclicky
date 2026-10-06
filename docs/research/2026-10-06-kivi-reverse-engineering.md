# Kivi (Sarvam AI) reverse-engineering report

Source: `Kivi.app` build `2026.09.22.0850` (Desktop copy) and the installed `2026.09.25.1212` running on
this Mac on 2026-10-06. Method: `strings`/`nm` + `swift-demangle` on `Kivi` and `KiviKit.framework`,
`assetutil` + CoreUI extraction of `Assets.car`, the app's own UserDefaults and Core Data store
(`~/Library/Application Support/Kivi.store`), and the live UI walked over Accessibility with
`screencapture` of every page. No network traffic was captured; the server protocol below is what the
client encodes, not what the server replies beyond the decoded `FinalPayload`.

Kivi is a **dictation app**: hold one key anywhere, speak, release; the words are transcribed by
Sarvam's service, cleaned up ("formatted") in a style chosen per app, and pasted into the app in front.
Everything else in the app serves that loop.

## 1. Bundle

| Fact | Value |
|---|---|
| Bundle id / scheme | `ai.sarvam.Kivi`, URL scheme `kivi://` (used for the auth callback only) |
| Min macOS | 14.0, universal binary (x86_64 + arm64), SwiftUI + AppKit, SwiftData |
| Frameworks | `KiviKit.framework` (the engine: 41 MB), `Sparkle 2.9.5` (updates), PostHog (analytics + crash reporter) |
| Auth | Ory Kratos at `https://login.sarvam.ai/identity`, org service `https://auth.sarvam.ai`, Supabase project `bjutljpmfhogrbdofplf` (anon key empty in this build); Google sign-in through `ASWebAuthenticationSession`; email + password + OTP fallback |
| Dictation transport | `wss://kivi.sarvam.ai/v1/dictate/stream` (also `aws-qa`, `aws-staging`, and `ws://127.0.0.1:8788` for dev) |
| Updates | `SUFeedURL` `https://storage.googleapis.com/kivi-app-updates/kivi/stable/appcast.xml`, EdDSA public key in Info.plist, daily check, automatic install |
| Telemetry | PostHog EU, feature flags, session replay buffer, latency traces (`DictationLatencyTraceUpload`), evidence outbox |
| Install | `DiskImageSelfInstaller`: launched from a `.dmg` it copies itself to `/Applications/Kivi.app` ("Kivi couldn't install itself", "A previous Kivi installation file is already at…", "live install kept previous Kivi at…"), relaunches the installed copy |
| Login item | `kivi.launchAtLogin` → `SMAppService` ("open at restart") |
| Permissions | Microphone, Accessibility (hotkeys, paste, selection, nearby text). No Screen Recording. |
| fn key | `kiviSystemFnKeyActionGuard.*` defaults: the app sets `com.apple.HIToolbox AppleFnUsageType = 0` (fn does nothing system-wide) and remembers whether it changed it; this Mac has it at 0 |

Resources: `AppIcon.icns`, `Assets.car` (AppIcon, `MenuBarLogo` 256², `kivi-wordmark` vector, seven
`bird-panel-*` pixel-art scenes 1147×2752 for the record page, three `folio-art-*` 1216×1520 for
onboarding, `header-footprints`, `header-jute`), earcons `start/stop/complete/error/blocked/notify/soften.wav`,
fonts Matter + MatterMono (commercial), SeasonMix (commercial), Petrona + Plus Jakarta Sans + Space Grotesk
(OFL), `mermaid.min.js` + `renderer.html` (Hey Kivi renders Mermaid diagrams in a WKWebView),
`default.metallib` (the orb's shader).

## 2. The dictation loop ("a take")

Types: `KiviKit.DictationCoordinator`, `LiveDictationService`, `AudioCapture`, `VoiceProcessingAudioSource`,
`PreConnectBuffer`, `TakeLedger`, `TakeAudioStore`, `Paster`, `PasteTransaction`, `PasteLandingVerifier`,
`HostTextInserter`, `FocusedFieldGate`, `TextBoxMonitor`, `SelectionSnapshotter`, `ScreenContextCapture`,
`ScreenTermExtractor`.

1. **Press** the kivi key (default `fn`, `FnHotkeyPolicy`; configurable to a single modifier,
   `HotkeyDescriptor`/`ShortcutScheme`). A listen-only + consuming `CGEvent` tap (`HotkeyTap`) with
   survival checks ("tap disabled by system", "rearm tap survived 3s", "table change: releasing hooks
   precautionarily"). The mic is pre-armed at press time ("warm capture re-armed (press-time start, zero
   device cost)"); `kiviAppleEchoCancellationEnabled` selects the voice-processing input.
2. **Stream** PCM16 frames over the websocket as they are captured (`audio_frames_sent`,
   `audio_protocol`, `chunk_ordinal`, `socket_generation`), buffering before the socket opens
   (`PreConnectBuffer`) and after a link loss (`linkLostBufferCapFrames`, `liveReconnectCatchUpBatchFrames`).
   Budgets (`DictationBudgets`): `freeTakeMaxMs`, `paidTakeMaxMs` ("up to 1 hour each"), `pingIntervalMs`,
   `ackTimeoutMs`, `finalTimeoutMs`, `openConnectBudgetMs`, `offlineEscalateMs`.
3. **Release** → `EndOfSpeechMessage` (`eos`): `trace_id`, `general_app_style_preset`, `language_hint`,
   `screen_terms` (names read off the screen through Accessibility, so they are spelled right),
   `screen_summary`, `focused_field` (role, subrole, signature, editable, before/after context),
   `screen_nodes` (≤ `maxScreenNodes`), `surface_contexts`, `cursor_context`, `evidence_capture_enabled`,
   `audio_manifest` (frame count, PCM byte count, sample rate, channels, bits, SHA-256 — the server
   acknowledges and can ask for a repair/resend of missing ranges: `audio_repair_*`).
   Other client messages: `ContextMessage` (app context + style presets + custom rules +
   format prefs), `screenContext(requestID:nodes:)`, `authRefresh(token:)`, `ping`, `{"type":"cancel"}`.
4. **Server** replies with progress (`formatting_progress`, `partial`) and a `FinalPayload`:
   `request_id`, `formatted_text`, `raw_transcript`, `detected_language(s)`, `route`,
   `resolved_persona`, `resolved_preset`, `content_kind`, `insertion_replace_before`, `latency`,
   `usage` (`monthly_audio_seconds_limit`, `audio_seconds_used`), `runtime_pack`, `style_context`,
   `output_suspect`, `server_durable`, `formatting_degraded`. Taps `esc` or double-tap fn cancel
   ("discard an in-progress take").
5. **Paste**: `DictationInsertionPlanner` → `PasteTransaction` → pasteboard write + ⌘V
   (`PasteKeyCodes`), then `PasteLandingVerifier` reads the focused field back
   (`paste.verifiedPasted` / `paste.postedUnverified` counters) and restores the user's pasteboard
   ("Restored user pasteboard snapshot after paste", "Skipped clipboard restore because pasteboard
   changed after paste"). Refusals: "Refusing paste because focused app is not …", "…focused field no
   longer matches expected target", "…target app changed before posting". Rich paste for Notion /
   Slack / Google Sheets (`NotionMarkdownPasteBuilder`, `SpreadsheetPastePayloadBuilder`,
   `SheetsFormulaGuard`, `SlackSelectedEditPolicy`), code-editor guard (`CodeEditorEditPolicy`:
   "code editor edits disabled / requires exact"), secure-input detection (`SecureInputOwner`: no
   paste into password fields). No text box → the transcript opens in the orb's box: "no text box
   found, copy from here".
6. **Record**: every take is a `Capture` (SwiftData `ZCAPTURE`: id, mode `dictate|clipboard|edit`,
   status `complete|failed`, raw text, formatted text, app bundle id, language hint, duration,
   pinned, archived, revision count, user/org/workspace, remote id, pending sync) with
   `CaptureRevision`s (previous/new text, editor source). History syncs to the server
   (`HistorySyncState`, cursors) unless "keep my memory on this device only" / incognito.
   Failed takes keep their audio locally for retry (`kivi.retainFailedTakeAudio`, "up to 1 GB or 100
   takes", `TakeAudioStore`, "retrying your recording", `beginManualRetry(takeID:)`).

Sounds (`BundledEarconPlayer`): start / stop / complete / error / blocked / notify / soften; haptics
(`MacHapticPlayer`) on start, stop, results. Orb phases (`FlowPhase`/`TxStage`): idle, listening
(waveform), "moving your words", "finishing your recording", "moved to text box", "didn't catch that",
"no speech detected", "couldn't finish your transcript. try again".

## 3. Formatting, styles, memory

- **Styles / personas** (`StyleCatalog`, `TransformPresetRecord`, `PersonasStore`): five seeded
  personas by app group — `developer` ("terse, and your code stays code"), `work messaging` ("clear,
  quick, and work-ready"), `personal messaging`, `email`, `other apps`; each app is assigned to one
  (`PersonaAppUsage`, `user_app_assignments`), with per-app overrides and cosmetic styles. Presets have
  `rules_raw` → `rules_compiled`, examples, `when_to_use`, `when_not_to_use`, a marketplace source.
  Example presets seen: casual ("lowercase, shorthand, zero fuss"), formal ("highest formality, full
  forms"), "clean sentences, shorthand kept", "everything spelled out, properly", "the fewest words
  that still say it", "the same note, with warmth". Casual/formal also have hotkeys
  (`kiviCasualHotkey`, `kiviFormalHotkey`, `kiviExpandHotkey`).
- **Language & script**: `kiviLanguageHint` = `auto` or one of `en-IN hi-IN bn-IN gu-IN kn-IN ml-IN
  mr-IN or-IN pa-IN ta-IN te-IN`; script `native` ("नमस्ते, आप कैसे हैं?") or `roman` ("namaste, aap
  kaise hain?").
- **Dictionary** (`MemoryEntity`, `MemoryHeardAliasRecord`, `SharedTerm`, "teach kivi a term"): "you
  say *aditya shatriya* → kivi writes **Aaditya Kshatriya**"; personal and organisation scopes;
  Wispr Flow import (`WisprFlowDictionaryReader` reads Wispr's SQLite). Memory objects are cached
  locally (`ZLOCALMEMORYOBJECT`, AES-encrypted `MemoryForestCache`).
- **Shortcuts** (`SpokenShortcutRecord`, "teach kivi a shortcut"): "you say *my sign-off* → kivi
  writes your saved sign-off, the same text every time"; "shortcuts expand only when you say the exact
  trigger. if the term is also in your dictionary, kivi can replace it inside a longer sentence."
- **Hey Kivi** (fn + ⌃ held, `HeyKivi*`, `KiviSession*`): an edit/author mode — "say your edit, then
  tap"; operates on the selection, the last take, or the orb's text ("make it formal", "make it crisper",
  "make this smaller", "write an acknowledgement email"); the server answers with steps
  (`author`, `clarify` with `waiting_for: target|details|choice`, `decline`), Markdown documents with
  Mermaid diagrams, a conversation log with turns, placement receipts ("moved to text box"), and
  "use this version" / "copy what kivi wrote". `kiviAutoEditAfterDictation` auto-edits a take.
- **Clipboard history** (`ClipboardHistoryService`, `kiviClipboardHistoryEnabled`, copy/paste
  hotkeys): system clipboard captures appear in history as `clipboard` mode captures.
- **History search**: local `KiviSearch.sqlite` (`LocalSearchIndex`, `QuickSearchEngine`) plus a
  server `HistorySearchRESTClient` that answers questions ("ask your history", "press enter to ask")
  with citations.
- **Analytics** (`UsageAnalytics`, `AnalyticsSnapshot`, `LeaderboardViewModel`): words today
  ("142 — he can't fly. your words can."), takes today and "mostly google chrome", weekly words
  (`268 / 1,00,000 words this week`), dictations "up to 1 hour each", leaderboard by period.

## 4. The UI (as observed)

Paper-cream window (`NSWindow Frame main` 1180×760), serif lowercase headings with a green
highlighter sweep (`KiviHighlightSweep`), sans body, a left rail: **record**, **history**, "your space":
**dictionary**, **shortcuts**, **styles**; footer: avatar + name, incognito eye, settings gear.

- **record**: time-of-day greeting ("midday. words flowing?", "morning! ready when you are.",
  "still up? he's listening."), a "pro mode is active — free for your 1st month" banner, the main box
  ("press fn anywhere to talk"), recent takes (copy), "all takes →", and a pixel-art bird panel with
  today's word count.
- **history**: search field ("search your words — try: the offsite flights", "press enter to ask"),
  filter, day groups with counts ("Today 5", "Yesterday 13"), rows = app icon + text + time + copy;
  failed rows say "couldn't finish"; edit conversations say "open hey kivi conversation".
- **dictionary**: hero example, "teach kivi a term", import (Wispr Flow), scopes (personal / org).
- **shortcuts**: hero example, "teach kivi a shortcut", empty state with a dotted bird.
- **styles** ("how you sound, app by app"): language auto-detect or chosen, script native/roman,
  "your styles" cards per persona with assigned app icons, "your apps".
- **settings** (a second rail: kivi → general, shortcuts, the orb, microphone, permissions; you →
  privacy & data, plan & billing, account; about):
  - general: appearance light/dark/system ("the orb and its transcript box follow this too"), open at
    restart, inactivity timeout (3 min, "end a session after this long with no speech"), reduce
    animation, welcome demo replay.
  - shortcuts: kivi key `fn` ("hold to dictate or talk anywhere"), "hey kivi" mode `fn + left ⌃`,
    cancel take `esc` or double-tap fn, paste last content (click to set).
  - the orb: position preview ("drag the orb"), look classic / pixel / pill, theme black / forest /
    mist, size full / mini, show the orb, hide when not in use, rest with the box open, paste
    visibility · orb box, free to drag, tooltips, sounds, haptics.
  - microphone: input device (system default + list), speak to test.
  - permissions: microphone, accessibility ("active this session"), read nearby text.
  - privacy & data: keep my memory on this device only, incognito, retry failed dictations, delete my
    dictation data (7-day scheduled wipe), org policy (no training, zero data retention), privacy policy.
  - plan & billing: words this week, limits, team "soon".
  - account: name, email, personal workspace, sign out ("history stays safe on this Mac").
  - about: logo + wordmark, software updates (check now), follow (X, Instagram), privacy policy.
- **the orb**: a floating always-on-top panel at the bottom centre (`FloatingBarPanel`, 1480×720
  transparent, `flowBarPosition=bottom`), resting as a small black pill with two dashes; a transcript
  box can open above it; draggable (`flowMovable`), docks (`double-click to dock`); hint pill
  ("tap / hold to talk", "tap / release to transcribe", "say it, then tap").
- **onboarding** (`EditorialOnboardingExperience`, `Edo*`, `FolioArtwork`): a folio with chapters
  (skip-able), permissions step (mic → accessibility with a repair walkthrough), key choice ("the keys
  you'll press a hundred times a day. choose wisely."), persona style survey, a live demo ("press fn
  anywhere and your first take will land here").
- **menu bar**: `MenuBarController` with a `MenuBarLogo` template image and a `MenuBarPanel`.

Copy voice: all lowercase, short, warm ("you talk, kivi gets it right.", "your words in flight",
"yapped all day? welcome home.").

## 5. What OpenClicky already has, and what the port adds

Already in OpenClicky: a global CGEvent-tap shortcut recogniser, a push-to-talk audio pipeline with
pluggable transcription providers (AssemblyAI streaming, OpenAI upload via the backend, Apple on-device
`SpeechAnalyzer`/`SFSpeechRecognizer`), pasteboard + ⌘V insertion, a permissions flow with the
Accessibility drag helper and TCC reset, Sparkle as a dependency (not started), a signed/notarized
release script that builds a zip and dmg, PostHog, a backend with accounts and metered keys, and
Saathi's Sarvam client (Saaras REST transcription, Bulbul speech) to copy from.

The port (see the design spec) adds the take loop with a key held anywhere, the orb, the formatting
layer with styles / dictionary / shortcuts, local history with search, a Kivi-shaped main window and
settings, onboarding, the DMG self-installer, a working Sparkle feed, an offline engine as a first-class
choice, and Sarvam (REST and streaming) as the cloud engine.
