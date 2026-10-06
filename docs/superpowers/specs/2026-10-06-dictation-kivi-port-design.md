# OpenClicky dictation: the Kivi port — design

Date: 2026-10-06. Status: implementing on `feat/dictation-kivi-port`.

## Intent (from the request)

Reverse-engineer Kivi (Sarvam's macOS dictation app) and port all of its functionality into OpenClicky:
the same installation experience, a clean logo, the same product shape — and offline transcription,
which Kivi does not have. Saathi already talks to Sarvam, so Sarvam is the cloud engine. The research is
in `docs/research/2026-10-06-kivi-reverse-engineering.md`.

Assumptions made because the request could not be clarified mid-task:

- The app keeps its name (OpenClicky) and gets an original mark, not Kivi's bird. "Clean logo" = a
  simple, flat, single-idea icon that reads at 16 px.
- The companion features OpenClicky already has (talk with ⌃⌥, pointing, agent lane, notch HUD) stay
  and keep working; dictation becomes the app's first job and its main window.
- No server features are reimplemented where OpenClicky has no server for them: leaderboard, org
  policies, team spaces, "ask your history" over a server index. History search is local. Hey-Clicky
  editing and formatting use whichever model is configured (Sarvam key, OpenClicky account, or none).
- Kivi's fonts and art are not copied. Headings use the system serif; body uses the system sans.

## Shape

Three layers, all inside the existing `macos/OpenClicky` target (no new packages):

1. **Dictation engine** (`Dictation/`): one take at a time — key down → capture → engine → format →
   paste → record. Pure parts are unit-tested.
2. **Surfaces** (`Dictation/UI`): the orb (floating pill), the main window (rail + pages), settings,
   onboarding, menu bar item.
3. **Installation**: DMG self-install on first launch, Sparkle updates with a GitHub-hosted appcast,
   login item, the fn-key system guard.

### Engines (speech → raw transcript)

`DictationEngine` is the existing `BuddyTranscriptionProvider` protocol (streaming session:
`appendAudioBuffer`, `requestFinalTranscript`, callbacks). Engines, chosen in Settings → Engine:

| Engine | Where audio goes | Partials | Notes |
|---|---|---|---|
| `offline` (default when nothing is configured) | nowhere | yes | Apple `SpeechAnalyzer` (macOS 26) / `SFSpeechRecognizer` on-device; `ReplyLanguage` locale; models downloaded on first use. Already present. |
| `sarvam` | api.sarvam.ai | yes | New. Streaming `wss://api.sarvam.ai/speech-to-text/ws` (Saaras v4, `language-code`, `keyterms`, 16 kHz PCM16 as base64 WAV chunks, `flush` at release) with a REST fallback (`POST /speech-to-text`, multipart WAV, 30 s chunks) — the REST shape Saathi has already verified against a real key. Key: `sarvamKey` in shell.json. |
| `openclicky` | OpenClicky backend | no | Existing upload provider (`/agent/transcribe`). |
| `assemblyai` | AssemblyAI via backend token | yes | Existing. |

`DictationEngineChoice` resolves the engine from settings and reports availability reasons ("no Sarvam
key", "no account") so the UI can say why a choice is greyed out. Nothing silently falls back to a
network engine when offline was chosen.

### Formatting (raw transcript → what is pasted)

`TakeFormatter` runs after the engine's final text:

1. **Local rules** (always, offline-safe, tested): trim, collapse whitespace; **spoken shortcuts** — a
   take that *is* the trigger (case-insensitive, punctuation-insensitive) becomes its replacement;
   **dictionary** — "you say X → write Y": replace whole-word matches of each alias inside the text;
   strip leading fillers ("um", "uh", "umm", "so uh") only at the start of sentences; capitalise the
   first letter when the style asks for sentences.
2. **Model polish** (when a model is configured and the style wants it): one chat completion with the
   style's rules, the dictionary terms, the app name, the language and script, and the raw text; the
   output replaces the text. Providers: Sarvam chat (`/v1/chat/completions`, `sarvam-105b`, BYO key),
   the OpenClicky backend (`/v1/chat/completions`), or nothing. Timeout 8 s; on failure the local
   result is pasted and the take is marked `formattingDegraded`.

### Styles, dictionary, shortcuts (the "space")

Stored as JSON under `~/.openclicky/dictation/` (`styles.json`, `dictionary.json`, `shortcuts.json`),
watched like shell.json. Seeded styles mirror Kivi's five personas with OpenClicky names and the same
app-group assignments (developer: Xcode, VS Code, Cursor, Terminal, iTerm, Claude, Codex, GitHub
Desktop, Android Studio; work messaging: Slack, Teams, Discord, Zoom; personal messaging: Messages,
WhatsApp; email: Mail, Outlook; other apps: everything else). Each style: id, name, tagline, rules
(free text for the model), `sentenceCase`, `fillerRemoval`, `polishWithModel`, `appBundleIDs`.

### Takes and history

`TakeStore`: SQLite (system `sqlite3`, no dependency) at
`~/Library/Application Support/OpenClicky/dictation.sqlite`, table `takes` (id, created_at, mode
`dictate|edit|clipboard`, status `complete|failed|cancelled`, raw_text, formatted_text, app_bundle_id,
app_name, language, engine, duration_seconds, paste_outcome, pinned) and `take_revisions`. Day
groups, counts for the record page ("N today · mostly <app>"), words today / this week, full-text
search (`LIKE`, local). Incognito skips the store. Clipboard history (opt-in) records `clipboard` rows
from a pasteboard poll.

### The take controller

`DictationTakeController` (main actor) replaces the fn+⌃ dictation path in `CompanionManager`:

- `DictationHotkey` (fn by default; right ⌥ / right ⌘ / ⌃⌥ alternatives) is recognised by
  `CompanionShortcutRecognizer`, which gains: `dictateKeyPressed/Released(wasTap)` for the chosen key,
  `editModePressed/Released` for key + ⌃ (Hey Clicky), `cancelRequested` for `esc` while a take is open
  and for a double tap of the key. The companion shortcuts keep their bindings.
- Press: play `start`, haptic, orb → listening, start the engine session on the shared audio engine
  (`BuddyDictationManager` is reused as the capture + session owner).
- Release: play `stop`, orb → "moving your words", finalise, format, paste through
  `FrontAppTextInserter` (extended with a landing check: focused AX element value contains the text →
  `verified`; no editable focus → the text stays in the orb's box with Copy), record, play `complete`,
  orb → "moved to text box" then idle. Inactivity timeout ends a take after N minutes of silence.
- Hey Clicky: key + ⌃ held → "say your edit, then let go"; the instruction plus the selection (or the
  last take) go to the model; the answer replaces the selection or is pasted; recorded as an `edit` take.

### Surfaces

- **Orb** (`OrbPanel`): a non-activating floating panel, bottom centre of the main display by default,
  draggable (position remembered), looks `pill` (two dashes) and `classic` (round mark); themes
  black / coral / mist; sizes full / mini; states idle, listening (live level bars), working, result
  (text box with Copy), error; hint pill under it when tooltips are on; hides when not in use (option).
  Reduce-animation honoured.
- **Main window** (`DictationWindow`): 1180×760, paper palette, rail (record, history, "your space":
  dictionary, shortcuts, styles; footer: account, incognito, settings), pages built from the
  Kivi observations. Settings is a second rail with the same nine pages, adapted: *engine* joins
  general; *plan & billing* shows the OpenClicky account/credits; *about* has Sparkle "check now".
- **Onboarding** (`OnboardingWindow`): welcome → permissions (mic, accessibility) → choose the key →
  choose the engine (offline / Sarvam key / sign in) → first take ("press fn anywhere and your first
  take will land here" — the window has a text box that receives it). Replayable from settings.
- **Menu bar**: a template mark; menu: open OpenClicky, dictation on/off, settings, check for
  updates, quit. On by default now (Kivi's shape), with the HUD unchanged.

### Installation and updates

- `DiskImageSelfInstaller`: when the bundle path is under `/Volumes/` and `/Applications/OpenClicky.app`
  is not this build, offer to copy to /Applications (replacing an older copy, keeping a `.previous`
  until the new one launches), relaunch, eject. Silent when already installed.
- Sparkle: `SUFeedURL` → `https://github.com/prasanthsasikumar/openclicky/releases/latest/download/appcast.xml`,
  `SUPublicEDKey` generated with Sparkle's `generate_keys`; the release script signs the zip with
  `sign_update`, writes `appcast.xml` with `generate_appcast`, and uploads both with the release.
  Updater started at launch; daily checks; "check now" in About.
- fn-key guard: Settings → shortcuts shows when macOS still assigns fn to Emoji & Symbols
  (`AppleFnUsageType != 0`) and offers to set it to "do nothing" (and back).
- Login item through `SMAppService` ("open at restart").

### Testing

Unit (Swift Testing, headless): shortcut recogniser additions, `TakeFormatter` rules, shortcut/dictionary
matching, `TakeStore` round trips in a temp directory, Sarvam request/message encoding and
response decoding, WAV wrapping, engine resolution, self-installer path decisions, appcast
generation inputs, fn-guard parsing. Real-provider checks: a Sarvam transcription of a bundled WAV
with the key from shell.json (`--openclicky-smoke-transcribe <wav>`), run by hand. Build +
`xcodebuild test` must stay green in CI.

## Out of scope for this pass

Leaderboard, organisations/teams, server history sync, Wispr Flow import, rich paste builders for
Notion/Sheets/Slack, Mermaid rendering in Hey Clicky answers, session replay telemetry.
