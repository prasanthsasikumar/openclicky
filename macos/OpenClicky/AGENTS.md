# OpenClicky - Agent Instructions

<!-- This is the single source of truth for all AI coding agents. CLAUDE.md is a symlink to this file. -->
<!-- AGENTS.md spec: https://github.com/agentsmd/agents.md — supported by Claude Code, Cursor, Copilot, Gemini CLI, and others. -->

## Overview

macOS menu bar companion app. Lives entirely in the macOS status bar (no dock icon, no main window). Clicking the menu bar icon opens a custom floating panel with companion voice controls. Uses push-to-talk (ctrl+option) to capture voice input, transcribes it through the OpenClicky backend, and either sends the transcript + a screenshot to Claude (teacher lane) or hands it to a Codex agent thread via the `openclicky` CLI (agent mode). Claude responds with text (streamed via SSE) and voice (ElevenLabs TTS). A blue cursor overlay can fly to and point at UI elements Claude references on any connected monitor.

All model calls go through the OpenClicky backend (`../../backend`), which holds the provider keys — nothing sensitive ships in the app. The user's token lives in `~/.openclicky/shell.json`.

## Architecture

- **App Type**: Menu bar-only (`LSUIElement=true`), no dock icon or main window
- **Framework**: SwiftUI (macOS native) with AppKit bridging for menu bar panel and cursor overlay
- **Pattern**: MVVM with `@StateObject` / `@Published` state management
- **AI Chat**: Claude (Sonnet 4.6 default, Opus 4.6 optional) via the OpenClicky backend with SSE streaming
- **Speech-to-Text**: AssemblyAI real-time streaming (`u3-rt-pro` model) via websocket, with OpenAI and Apple Speech as fallbacks
- **Text-to-Speech**: ElevenLabs (`eleven_flash_v2_5` model) via the OpenClicky backend
- **Screen Capture**: ScreenCaptureKit (macOS 14.2+), multi-monitor support
- **Voice Input**: Push-to-talk via `AVAudioEngine` + pluggable transcription-provider layer. System-wide keyboard shortcut via listen-only CGEvent tap.
- **Element Pointing**: Claude embeds `[POINT:x,y:label:screenN]` tags in responses. The overlay parses these, maps coordinates to the correct monitor, and animates the blue cursor along a bezier arc to the target.
- **Concurrency**: `@MainActor` isolation, async/await throughout
- **Analytics**: PostHog via `ClickyAnalytics.swift`

### API Proxy (OpenClicky backend)

The app never calls external APIs directly. Every request goes to the OpenClicky backend with the user's bearer token (see `OpenClickyConfiguration.swift`).

| Route | Purpose |
|-------|---------|
| `POST /chat` | Claude vision + streaming chat (teacher lane) |
| `POST /tts` | ElevenLabs or OpenAI speech → audio/mpeg |
| `POST /transcribe-token` | Short-lived AssemblyAI websocket token (optional provider) |
| `POST /agent/transcribe` | Upload-based speech-to-text (default provider) |
| `openclicky` CLI | Agent mode: `do --gate-only` (lane) and `run --events` (Codex thread) via `OpenClickyAgentClient.swift` |

### Key Architecture Decisions

**Menu Bar Panel Pattern**: The companion panel uses `NSStatusItem` for the menu bar icon and a custom borderless `NSPanel` for the floating control panel. This gives full control over appearance (dark, rounded corners, custom shadow) and avoids the standard macOS menu/popover chrome. The panel is non-activating so it doesn't steal focus. A global event monitor auto-dismisses it on outside clicks.

**Cursor Overlay**: A full-screen transparent `NSPanel` hosts the blue cursor companion. It's non-activating, joins all Spaces, and never steals focus. The cursor position, response text, waveform, and pointing animations all render in this overlay via SwiftUI through `NSHostingView`.

**Global Push-To-Talk Shortcut**: Background push-to-talk uses a listen-only `CGEvent` tap instead of an AppKit global monitor so modifier-based shortcuts like `ctrl + option` are detected more reliably while the app is running in the background. The same tap feeds `CompanionShortcutRecognizer` for HeyClicky's other shortcuts: ⌃ ×2 opens the notch text composer (`submitTypedRequest` → Realtime `sendTextTurn`, or the teacher lane), fn+⌃ held dictates into the app in front (classic transcription pipeline → `FrontAppTextInserter`), fn+⌃ ×2 toggles hands-free (Realtime always-on).

**Screenshot pixel size for pointing**: `CompanionScreenCapture` records the captured image's real pixel size, not the size requested from ScreenCaptureKit; a capture that comes back at another size would otherwise put every pointed-at element off by that ratio.

**Shared URLSession for AssemblyAI**: A single long-lived `URLSession` is shared across all AssemblyAI streaming sessions (owned by the provider, not the session). Creating and invalidating a URLSession per session corrupts the OS connection pool and causes "Socket is not connected" errors after a few rapid reconnections.

**Transient Cursor Mode**: When "Show OpenClicky" is off, pressing the hotkey fades in the cursor overlay for the duration of the interaction (recording → response → TTS → optional pointing), then fades it out automatically after 1 second of inactivity.

## Key Files

| File | Lines | Purpose |
|------|-------|---------|
| `OpenClickyApp.swift` | ~89 | Menu bar app entry point. Uses `@NSApplicationDelegateAdaptor` with `CompanionAppDelegate` which creates `MenuBarPanelManager` and starts `CompanionManager`. No main window — the app lives entirely in the status bar. |
| `CompanionManager.swift` | ~1026 | Central state machine. Owns dictation, shortcut monitoring, screen capture, Claude API, ElevenLabs TTS, and overlay management. Tracks voice state (idle/listening/processing/responding), conversation history, model selection, and cursor visibility. Coordinates the full push-to-talk → screenshot → Claude → TTS → pointing pipeline. |
| `MenuBarPanelManager.swift` | ~243 | NSStatusItem + custom NSPanel lifecycle. Creates the menu bar icon, manages the floating companion panel (show/hide/position), installs click-outside-to-dismiss monitor. |
| `CompanionPanelView.swift` | ~492 | SwiftUI panel content for the menu bar dropdown. Shows companion status, push-to-talk instructions, model picker (Sonnet/Opus), the OPENCLICKY section (agent mode, backend status, agent activity), and quit button. No permissions UI — the island's permission cards own that. Dark aesthetic using `DS` design system. |
| `OverlayWindow.swift` | ~1000 | Full-screen transparent overlay (one per screen) hosting the blue cursor, the typewriter caption (`cursorCaptionText`: onboarding prompt, (i) explanation, hands-free feedback; wraps at 300 pt, flips left near the edge), waveform, and spinner. Handles cursor animation, element pointing with bezier arcs, multi-monitor coordinate mapping, and the cross-display flight legs (`performFlightLeg`). |
| `CompanionResponseOverlay.swift` | ~217 | SwiftUI view for the response text bubble and waveform displayed next to the cursor in the overlay. |
| `CompanionScreenCaptureUtility.swift` | ~132 | Multi-monitor screenshot capture using ScreenCaptureKit. Returns labeled image data for each connected display. |
| `BuddyDictationManager.swift` | ~866 | Push-to-talk voice pipeline. Handles microphone capture via `AVAudioEngine`, provider-aware permission checks, keyboard/button dictation sessions, transcript finalization, shortcut parsing, contextual keyterms, and live audio-level reporting for waveform feedback. |
| `BuddyTranscriptionProvider.swift` | ~100 | Protocol surface and provider factory for voice transcription backends. Resolves provider based on `VoiceTranscriptionProvider` in Info.plist — AssemblyAI, OpenAI, or Apple Speech. |
| `AssemblyAIStreamingTranscriptionProvider.swift` | ~478 | Streaming transcription provider. Fetches temp tokens from the OpenClicky backend, opens an AssemblyAI v3 websocket, streams PCM16 audio, tracks turn-based transcripts, and delivers finalized text on key-up. Shares a single URLSession across all sessions. |
| `OpenAIAudioTranscriptionProvider.swift` | ~317 | Upload-based transcription provider. Buffers push-to-talk audio locally, uploads as WAV on release, returns finalized transcript. |
| `AppleSpeechTranscriptionProvider.swift` | ~147 | Local fallback transcription provider backed by Apple's Speech framework. |
| `BuddyAudioConversionSupport.swift` | ~108 | Audio conversion helpers. Converts live mic buffers to PCM16 mono audio and builds WAV payloads for upload-based providers. |
| `GlobalPushToTalkShortcutMonitor.swift` | ~150 | System-wide shortcut monitor. Owns the listen-only `CGEvent` tap, feeds it to `CompanionShortcutRecognizer`, publishes talk press/release plus the other shortcut events. |
| `CompanionShortcutRecognizer.swift` | ~140 | Pure state machine for HeyClicky's four shortcuts: Talk (hold ⌃⌥), Text (tap ⌃ twice → notch composer), Dictate (hold fn+⌃ → typed into the front app), Hands-free (tap fn+⌃ twice → always-on toggle). Tap window 350 ms, double-tap window 450 ms; a key press while the modifier is down (⌃C) is never a tap. Unit-tested. |
| `FrontAppTextInserter.swift` | ~70 | Types dictated text into the app in front: pasteboard + posted ⌘V (needs Accessibility), pasteboard restored 0.6 s later; without Accessibility the text stays on the pasteboard. |
| `CursorFlightPlanner.swift` | ~100 | Splits a buddy flight whose destination is on another display (dock into the notch from an external monitor, fly back to a mouse that moved screens) into a leg to this display's nearest edge and a leg across the destination display. Unit-tested against this Mac's layout. |
| `ScreenTextLocator.swift` | ~190 | `point_at` snapping. The Realtime model's pixel guesses are 30–100 px off, so the screenshot it saw is OCR'd (Vision, accurate, no language correction, upscaled 2× because 11 px captions come back as "Now"/"HNew" otherwise) and the guess snaps to the element's visible `text` nearest to it: whole hint → distinctive words (never "button"/"menu"…), exact matches only, within 30 % of the width. Near misses ("reditt" for a Reddit tab) are left to the Claude fallback, and so are ambiguous ones: when a second candidate sits within 10 % of the width of the winner ("Edit" on every row of a list), the model's own 30–100 px error is what separates them, so nothing is resolved and Claude picks with the user's request in hand. Unit-tested, including a rendered-caption OCR test. |
| `BrowserTabLocator.swift` | ~190 | `point_at` for browser tabs ("where is the Reddit tab"): a 16 px favicon and a truncated title are unreadable to every screenshot model (all went to a menu bar item spelled "reditt"). When the request says "tab", every running Chrome/Brave/Edge/Safari with a window on screen is tried (active one first; the browser need not be in front — a terminal over the lower half of Chrome still shows the tab strip): AppleScript lists its front window's tabs (title + URL; first use prompts to allow controlling the browser), request words are matched against the URL host (×3) and title (×1), the tab's AXTabButton frame (Accessibility, strip order, counts must agree) is mapped into the capture, and CGWindowList z-order rules out a tab that is off the captured display or under another app's window. Runs before OCR/Claude in the pointing chain. Unit-tested. |
| `AccessibleElementLocator.swift` | ~215 | `point_at` for a caption that appears more than once ("Edit" on every row). OCR only sees pixels, so identical captions are separated by distance alone and the model's 30-100 px error decides; Accessibility knows each control's role and the title of the row it sits in. The front app's focused window is walked (600 nodes, depth 12, 0.25 s messaging timeout), every captioned control is mapped into the capture, and candidates are ranked by request words matching their container titles (x3) plus an actionable-role bonus, distance only breaking ties. Two equal candidates closer than the ambiguity margin stay unresolved and go to Claude. Runs between OCR and Claude in the pointing chain. Pure ranking unit-tested; the tree walk is not. |
| `AppLog.swift` | ~50 | `~/Library/Logs/OpenClicky/app.log` (truncated past 2 MB at launch): every Realtime log line (`screen attached`, `point_at … snapped/located by Claude`) and hands-free toggles, since Finder-launched stdout goes nowhere. Read it when the buddy pointed at the wrong thing. |
| `ScreenElementGrounder.swift` | ~60 | `point_at` fallback when there is no caption to snap to (icons, symbols, OCR misses): Claude Haiku 4.5 through the backend `/chat` proxy locates the element on the same screenshot (label + text + the user's last utterance) and answers `[POINT:x,y]`; measured ≤ 8 px off, 2–4 s. `RealtimeVoiceClient` resolves `point_at` calls on a serial chain (`pointingChain`) off the receive loop so audio keeps flowing and steps fly in order. Unit-tested (parsing, prompt). |
| `ClaudeAPI.swift` | ~291 | Claude vision API client with streaming (SSE) and non-streaming modes. TLS warmup optimization, image MIME detection, conversation history support. |
| `ElevenLabsTTSClient.swift` | ~81 | ElevenLabs TTS client. Sends text to the OpenClicky backend, plays back audio via `AVAudioPlayer`. Exposes `isPlaying` for transient cursor scheduling. |
| `DesignSystem.swift` | ~880 | Design system tokens — colors, corner radii, shared styles. All UI references `DS.Colors`, `DS.CornerRadius`, etc. |
| `ClickyAnalytics.swift` | ~121 | PostHog analytics integration for usage tracking. |
| `WindowPositionManager.swift` | ~275 | Window placement logic, Screen Recording permission flow, accessibility permission helpers, and `relaunchApp()` (`open -n`, then terminate) for the permission that only takes effect on a fresh launch. |
| `AppBundleConfiguration.swift` | ~28 | Runtime configuration reader for keys stored in the app bundle Info.plist. |
| `OpenClickyConfiguration.swift` | ~190 | Reads `~/.openclicky/shell.json` + env overrides; bearer auth; CLI environment. Bring your own key: `openaiApiKey` / `anthropicApiKey` become `x-openclicky-*` headers on every backend request (`authorize`) and `OPENCLICKY_OPENAI_KEY` / `OPENCLICKY_ANTHROPIC_KEY` for the CLI, never `OPENAI_API_KEY`. |
| `BillingStatus.swift` | ~170 | Settings → Account: signed out, an email + password form (invite-only accounts created with `npm run admin -w backend -- invite`); signed in, the plan and credits this month from `GET /billing/me` plus Sign out; with an own key in shell.json, "Keys: your own (not metered)". No Stripe UI (the backend's Stripe routes stay dormant). |
| `OpenClickyAuthSession.swift` | ~140 | Sign-in and session refresh: `GET /auth/config` on the backend gives the Supabase URL + publishable key, the password grant returns access + refresh tokens, both stored in shell.json (`token`, `refreshToken`, `tokenExpiresAt`, `accountEmail`) and refreshed 10 min before expiry on a 5-min timer (started from `NotchHUDManager.show`). |
| `OpenClickyAgentClient.swift` | ~200 | Runs the `openclicky` CLI and parses `--events` JSON Lines for agent mode. |
| `NotchHUD.swift` | ~1040 | The notch island (HeyClicky look: solid black, flared top corners, 16 pt bottom corners, no border/shadow, pop-up-menu window level). States: collapsed (the notch itself; the docked buddy's triangle peeks under it; a thin handle line on displays without a notch — flush with the top edge on a display that shows no menu bar, whose band is 0 pt), compact (busy strip, or the (i) caption typing out while the buddy is docked), connect (app card), permission (permission card), composer (⌃ tapped twice: one-line field, ↩ sends / esc closes; the app activates for the keyboard and the previous app gets it back), full (Home/Agents/Settings, 512 × 232). One window per screen. |
| `NotchHUDPanels.swift` | ~810 | The full panel: tab bar in the top band, Home (skill tiles + "+" composer, the four shortcuts, integrations, Dock Cursor, (i) → `explainWhatOpenClickyDoes`), Agents, Settings, result card. |
| `PermissionPrompt.swift` | ~320 | HeyClicky's permission cards on the island, one permission at a time in order (Microphone → Accessibility → Screen Recording → Screen Content): progress dashes, headline, one line of copy, one blue button. The button takes the macOS route for that step (`WindowPositionManager`'s system-prompt-then-System-Settings rule; Screen Content is granted by actually capturing, so `CompanionManager` injects that one) and the card moves to its `waiting` stage — which for Accessibility opens the drag helper, and for Screen Recording offers Quit & Reopen. Fed by the same 1.5 s permission poll as the flags, so cards advance on their own; ✕ hides them until relaunch, the gear opens the menu bar panel. The waiting accessibility card also offers "Already switched on? Reset it and try again": `tccutil reset Accessibility <bundle id>` plus a relaunch, the only way out of a TCC row whose recorded signature no longer matches the running build (an update signed differently, or a dev build sharing the bundle id) — the list shows the app switched on while macOS keeps refusing it. Unit-tested. |
| `AccessibilityDragPanel.swift` | ~189 | The "I'm OpenClicky — drag me into the list above" panel: a floating pop-up-menu-level `NSPanel` whose icon row is a `public.file-url` drag source for the app bundle, for when macOS never adds OpenClicky to the Accessibility list by itself. Follows the System Settings window (found through `CGWindowListCopyWindowInfo`, which gives bounds without Screen Recording permission) on a 1 s timer, and — unlike HeyClicky's — stays up through the drag, closing only when `AXIsProcessTrusted()` flips. |
| `AppConnectPrompt.swift` | ~360 | "Connect \<app\> to OpenClicky" card: polls the frontmost app/site every 1.5 s off the main thread, opens once per app skill that has an `integration` (Composio toolkit), remembers Yes/No in UserDefaults (`appSkillConnectDecisions`); Yes runs `openclicky integrations status/login` (Codex MCP OAuth into Composio Connect, browser page) then hands the agent a Composio connection task (or shows a "set COMPOSIO_MCP_URL" notice), declined skills are left out of the talk prompts. Example chips come from the skill's "Pointing hints"/"Common tasks". |
| `MacActions.swift` | ~360 | The fast lane: the local actions OpenClicky performs itself instead of routing them to Codex — `open_app`, `open_url`, `create_folder`, `reveal_in_finder`, `set_volume`, `media_control`, offered to the Realtime session as tools next to `point_at`. Typed arguments only: `location` is a closed enum (desktop/downloads/documents/workspace/home), names are rejected if they could escape it, and only http(s) URLs open — a mishearing can misname a folder but cannot produce a command. Each action returns one sentence, which is what the assistant says. Targets about two seconds against twelve through the agent lane; `scripts/measure-actions.sh` is how that gets checked. Anything outside the set still goes to `send_to_agent`. Unit-tested. |

## Build & Run

```bash
# Open in Xcode
open OpenClicky.xcodeproj

# Select the OpenClicky scheme, set signing team, Cmd+R to build and run

# Known non-blocking warnings: Swift 6 concurrency warnings,
# deprecated onChange warning in OverlayWindow.swift. Do NOT attempt to fix these.

scripts/release.sh --no-notarize   # fast local install over /Applications, Developer ID signing kept
scripts/release.sh                 # the same, notarized (needed only for other Macs)
```

`scripts/measure-actions.sh` reports p50/p95 per verb from `~/Library/Logs/OpenClicky/app.log`
(`mac action:` and `agent task finished in` lines). The fast lane's acceptance number is p95 under
2 s for `open_app` and `create_folder`.

**Debug builds carry their own bundle id** (`org.openclicky.app.debug`, shown as "OpenClicky Debug"),
so they get their own TCC rows and can run beside the installed app without either invalidating the
other's Accessibility / Screen Recording grants. That separation is what makes `xcodebuild` safe here:
a Debug build no longer costs the installed app its permissions, and `clean-stray-builds.sh` leaves
Debug bundles alone for the same reason. Two rules still hold: **one bundle id must only ever be
signed by one identity** (mixing Apple Development and Developer ID under the same id is what makes
System Settings show the app switched on while macOS keeps refusing it — the accessibility card's
"Already switched on? Reset it and try again" is the way out), and a Debug build gets its own
UserDefaults domain, so its onboarding state and toggles are separate from the installed app's.

## OpenClicky backend

Run it from the repository root: `npm run dev -w backend` (see the root README for keys and the auth flow).

## Code Style & Conventions

### Variable and Method Naming

IMPORTANT: Follow these naming rules strictly. Clarity is the top priority.

- Be as clear and specific with variable and method names as possible
- **Optimize for clarity over concision.** A developer with zero context on the codebase should immediately understand what a variable or method does just from reading its name
- Use longer names when it improves clarity. Do NOT use single-character variable names
- Example: use `originalQuestionLastAnsweredDate` instead of `originalAnswered`
- When passing props or arguments to functions, keep the same names as the original variable. Do not shorten or abbreviate parameter names. If you have `currentCardData`, pass it as `currentCardData`, not `card` or `cardData`

### Code Clarity

- **Clear is better than clever.** Do not write functionality in fewer lines if it makes the code harder to understand
- Write more lines of code if additional lines improve readability and comprehension
- Make things so clear that someone with zero context would completely understand the variable names, method names, what things do, and why they exist
- When a variable or method name alone cannot fully explain something, add a comment explaining what is happening and why

### Swift/SwiftUI Conventions

- Use SwiftUI for all UI unless a feature is only supported in AppKit (e.g., `NSPanel` for floating windows)
- All UI state updates must be on `@MainActor`
- Use async/await for all asynchronous operations
- Comments should explain "why" not just "what", especially for non-obvious AppKit bridging
- AppKit `NSPanel`/`NSWindow` bridged into SwiftUI via `NSHostingView`
- All buttons must show a pointer cursor on hover
- For any interactive element, explicitly think through its hover behavior (cursor, visual feedback, and whether hover should communicate clickability)

### Do NOT

- Do not add features, refactor code, or make "improvements" beyond what was asked
- Do not add docstrings, comments, or type annotations to code you did not change
- Do not try to fix the known non-blocking warnings (Swift 6 concurrency, deprecated onChange)
- The project, targets, and scheme are named OpenClicky (renamed from upstream's "leanring-buddy")
- Do not run `xcodebuild` from the terminal — it invalidates TCC permissions

## Git Workflow

- Branch naming: `feature/description` or `fix/description`
- Commit messages: imperative mood, concise, explain the "why" not the "what"
- Do not force-push to main

## Self-Update Instructions

<!-- AI agents: follow these instructions to keep this file accurate. -->

When you make changes to this project that affect the information in this file, update this file to reflect those changes. Specifically:

1. **New files**: Add new source files to the "Key Files" table with their purpose and approximate line count
2. **Deleted files**: Remove entries for files that no longer exist
3. **Architecture changes**: Update the architecture section if you introduce new patterns, frameworks, or significant structural changes
4. **Build changes**: Update build commands if the build process changes
5. **New conventions**: If the user establishes a new coding convention during a session, add it to the appropriate conventions section
6. **Line count drift**: If a file's line count changes significantly (>50 lines), update the approximate count in the Key Files table

Do NOT update this file for minor edits, bug fixes, or changes that don't affect the documented architecture or conventions.
