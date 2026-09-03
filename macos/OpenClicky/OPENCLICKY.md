# OpenClicky macOS app — provenance and changes

This app is a **fork** of the original open-source Clicky app (MIT; copyright holder in `LICENSE`), imported at
upstream commit `a80fa80` and then renamed and rewired for OpenClicky. Upstream's `LICENSE` is kept.
Because the project, targets, scheme, directories, bundle identifier, and product strings were all
renamed (`leanring-buddy` → `OpenClicky`), upstream changes are no longer pulled with `git subtree`;
port them by hand with `git log`/`git diff` against the upstream Clicky repository.

Upstream is the "teacher next to your cursor" Clicky: Claude vision + `[POINT:x,y]` pointing,
push-to-talk, ScreenCaptureKit, AssemblyAI streaming STT, ElevenLabs TTS. OpenClicky keeps that
shell and routes everything through the OpenClicky backend and agent. Integration points are marked
`OpenClicky:` in comments.

## Rename map

| Upstream | OpenClicky |
|---|---|
| `leanring-buddy.xcodeproj`, scheme/target `leanring-buddy` | `OpenClicky.xcodeproj`, scheme/target `OpenClicky` |
| `leanring-buddy/` sources, `leanring_buddyApp.swift` | `OpenClicky/`, `OpenClickyApp.swift` |
| `com.yourcompany.leanring-buddy`, product `Clicky` | `org.openclicky.app`, product `OpenClicky` |
| `com.learningbuddy.*` dispatch queues | `org.openclicky.*` |
| "Clicky" in UI, prompts, permission strings | "OpenClicky" |
| `worker/` (Cloudflare proxy), `appcast.xml`, demo gif, per-user `xcuserdata` | removed — `backend/` replaces the Worker; Sparkle feed/key entries dropped from Info.plist (updater is not started) |

Also removed (personal media, third-party leftovers, or bypasses of the backend): the hosted intro
video (a Mux stream of the original author; onboarding now runs the pointing demo and the
"press control + option" prompt instead), `ff.mp3`/`eshop.mp3`/`enter.mp3` (copyrighted game music),
`steve.jpg`, the `codex-*`/`makesomething-*`/`git-tools-prompt` screenshots and Discord/Google logos
(unused, from an earlier project), `OpenAIAPI.swift` and `ElementLocationDetector.swift` (unused clients
that called OpenAI/Anthropic directly with a local key), `scripts/release.sh` + `dmg-background.png`
(pointed at another person's releases repo), and a stale inner `AGENTS.md` describing a different app.

Kept unchanged on purpose: the app icon and accent color, `DesignSystem.swift`,
and the upstream `AGENTS.md` apart from the rename. The upstream README, feedback link, and the original author's name were removed.

## Integration changes

| File | Change |
|---|---|
| `OpenClickyConfiguration.swift` (new) | Reads `~/.openclicky/shell.json` (+ env overrides): backend URL, token, CLI command, workspace, model, transcription provider, login-item opt-in. Adds the bearer token to requests; builds the CLI environment with provider keys stripped. |
| `OpenClickyAgentClient.swift` (new) | Runs `openclicky do --gate-only` (lane) and `openclicky run --events` (agent turn), parsing JSON Lines into text, artifacts, thread id, status. |
| `CompanionManager.swift` | Worker URL → backend URL. **Agent mode** (on by default when a token exists): after the screenshot, the gate classifies the utterance; "agent" runs a Codex thread (screenshot attached, thread resumed across turns) and speaks the final message; "ask" continues into the original teacher lane with pointing. Publishes `agentActivityText` / `lastAgentArtifacts`. Email onboarding no longer posts to the original developer's form or PostHog. Generic error utterance. |
| `CompanionPanelView.swift` | OPENCLICKY section: Agent mode toggle, backend status (click → open settings file), live agent activity, reveal-last-artifacts. |
| `ClaudeAPI.swift`, `ElevenLabsTTSClient.swift`, `AssemblyAIStreamingTranscriptionProvider.swift` | Send `Authorization: Bearer <token>`; token URL comes from the backend base URL. |
| `OpenAIAudioTranscriptionProvider.swift` | Uploads to the backend's `POST /agent/transcribe` (JSON base64) instead of api.openai.com with a local key. Display name "OpenClicky". |
| `BuddyTranscriptionProvider.swift` | `transcriptionProvider` from shell.json wins; defaults to the backend ("openai") when a token exists. |
| `ClickyAnalytics.swift` | PostHog only when `PostHogAPIKey` is in Info.plist (upstream hardcoded the original key). |
| `OpenClickyApp.swift` | Login-item registration is opt-in (`registerAsLoginItem`); the menu bar icon is off by default (Settings → MENU BAR; the onboarding/permissions panel then hangs from the top-right of the screen); `--openclicky-smoke-run "<text>"` runs gate + agent headlessly and exits; `--openclicky-smoke-talk <seconds>` opens a Realtime session, plays the greeting, listens for that long and exits. |
| `NotchHUD.swift` (new) | The notch HUD (HeyClicky's notch app), one window per attached screen (the hardware-notch screen is the primary; other displays get the same island as a pill under their menu bar): collapsed lip + handle under the notch, compact status strip while listening/thinking/speaking, and the full Home / Agents / Settings panel on hover or click. Window is sized per state and only the active layer is in the SwiftUI hierarchy (NSHostingView otherwise centers and clips oversized content). Hover is polled from the pointer position; SwiftUI's onHover is unreliable in a non-key panel. Lives on the screen with the hardware notch. |
| `NotchHUDPanels.swift` (new) | Home (skills, integrations, shortcuts, Dock Cursor), Agents (thread cards from `openclicky threads list --json`, Open Agent), Settings (backend, agent mode, voice, cursor, support), and the top-right agent result card with Copy + "Follow up with agent". |
| `RealtimeVoiceClient.swift` (new) | The in-app OpenAI Realtime loop: mints the ephemeral secret via `POST /agent/realtime/session`, WebSocket to `gpt-realtime`, push-to-talk (ctrl+option) or always-on server VAD, PCM16 24 kHz both ways, barge-in flushes playback, `send_to_agent` tool → `CompanionManager.performAgentTask`. Audio lives in `RealtimeAudioEngine` on its own dispatch queue: CoreAudio's first-time setup hops synchronously to the main queue, so configuring the engine from a main-actor task deadlocks. Voice processing (echo cancellation) is tried first and needs the mixer → output link made at the hardware format before the 24 kHz player is connected; plain capture is the fallback. The voice-processed input reports 5 identical channels and `AVAudioConverter` turns a 5 → 1 channel conversion into silence, so channel 0 is copied into a mono buffer before resampling. In push-to-talk the engine only runs from key-down until the reply has played (the system's orange mic indicator goes away in between; the graph is pre-built at connect so key-down opens the mic in ~70 ms); "Always listening" keeps it open. Every turn carries **screen context**: before the reply is requested, the cursor screen (1280 px JPEG, own windows excluded) plus a pointer-position caption is added as a `conversation.item.create` user message with `input_image`, so "what is this?" is answered from the screen. In always-on mode `create_response` is off and the client requests the response itself after attaching the screen on `input_audio_buffer.committed`. |
| `OverlayWindow.swift` | `flyingToDock` navigation mode: the buddy flies into the notch along the bezier arc, fades, and the HUD shows it as a badge; pointing while docked launches from the notch and returns there. Buddy color is red-orange (`DS.Colors.overlayCursorColor`). |

Backend routes for this shell: `POST /chat` (Claude, streamed), `POST /tts` (ElevenLabs or OpenAI
speech), `POST /transcribe-token` (AssemblyAI, optional). All require the user's token.

## Running it

1. Backend up (`npm run dev -w backend`), agent built (`npm run build`), a token minted.
2. `~/.openclicky/shell.json`:
   ```json
   { "cliCommand": ["node", "/path/to/openclicky/agent/dist/cli.js"],
     "backendUrl": "http://localhost:8787", "token": "<jwt or session token>",
     "workspace": "/Users/you/OpenClicky", "model": "", "transcriptionProvider": "openai" }
   ```
3. `open OpenClicky.xcodeproj`, set your signing team (the project still carries upstream's
   `DEVELOPMENT_TEAM` until you pick yours), run.
4. Hold ctrl+option, speak. Questions get the pointing teacher; "make/fix/create…" goes to the agent.

Only `/Applications/OpenClicky.app` should exist: Spotlight indexes build products too, so every
`release.sh` install ends with `scripts/clean-stray-builds.sh` (also `npm run clean:mac-builds`),
which deletes OpenClicky.app bundles in Xcode's DerivedData and the repo `build/` folder.

Headless compile check: `xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky build CODE_SIGNING_ALLOWED=NO`.
Headless agent check: `OpenClicky.app/Contents/MacOS/OpenClicky --openclicky-smoke-run "create a file called x.txt containing 'y'"`.
Headless voice checks (need a real OpenAI key behind the backend): `--openclicky-smoke-talk 12` opens an always-on session, greets and prints capture statistics; `--openclicky-smoke-talk-file utterance.wav` runs a real push-to-talk turn with a recorded utterance instead of the microphone (make one with `say -o utterance.wav --data-format=LEI16@24000 "what is two plus two"`), prints your transcript, the reply, and confirms the microphone was released afterwards.

Settings → VOICE: "Realtime voice" (default on) switches push-to-talk to the Realtime loop; "Always listening" keeps the mic open with server-side turn detection so you can just talk.
