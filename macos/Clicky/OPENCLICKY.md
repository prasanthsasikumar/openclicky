# OpenClicky changes to the vendored Clicky app

This directory is `farzaa/clicky` (MIT) imported with `git subtree` at upstream commit `a80fa80`.
Pull upstream later with:

```bash
git subtree pull --prefix=macos/Clicky https://github.com/farzaa/clicky.git main --squash
```

Upstream is the older "teacher next to your cursor" Clicky (Claude vision + pointing, AssemblyAI,
ElevenLabs). OpenClicky keeps that shell and routes it through the OpenClicky backend and agent.
Changes are kept small and marked `OpenClicky:` in comments so upstream merges stay easy.

## What changed

| File | Change |
|---|---|
| `OpenClickyConfiguration.swift` (new) | Reads `~/.openclicky/shell.json` (+ env overrides): backend URL, token, CLI command, workspace, model, transcription provider, login-item opt-in. Adds the bearer token to requests; builds the CLI environment with provider keys stripped. |
| `OpenClickyAgentClient.swift` (new) | Runs `openclicky do --gate-only` (lane) and `openclicky run --events` (agent turn), parsing JSON Lines into text, artifacts, thread id, status. |
| `CompanionManager.swift` | Worker URL → backend URL. **Agent mode** (on by default when a token exists): after the screenshot, the gate classifies the utterance; "agent" runs a Codex thread (screenshot attached, thread resumed across turns) and speaks the final message; "ask" continues into the original teacher lane with pointing. Publishes `agentActivityText` / `lastAgentArtifacts`. Email onboarding no longer posts to the original developer's form or PostHog. Generic error utterance. |
| `CompanionPanelView.swift` | New OPENCLICKY section: Agent mode toggle, backend status (click → open settings file), live agent activity, reveal-last-artifacts. |
| `ClaudeAPI.swift`, `ElevenLabsTTSClient.swift`, `AssemblyAIStreamingTranscriptionProvider.swift` | Send `Authorization: Bearer <token>`; token URL comes from the backend base URL. |
| `OpenAIAudioTranscriptionProvider.swift` | Uploads to the backend's `POST /agent/transcribe` (JSON base64) instead of api.openai.com with a local key. Display name "OpenClicky". |
| `BuddyTranscriptionProvider.swift` | `transcriptionProvider` from shell.json wins; defaults to the backend ("openai") when a token exists. |
| `ClickyAnalytics.swift` | PostHog only when `PostHogAPIKey` is in Info.plist (upstream hardcoded the original key). |
| `leanring_buddyApp.swift` | Login-item registration is opt-in (`registerAsLoginItem`). |

Backend routes added for this shell: `POST /chat` (Claude, streamed), `POST /tts` (ElevenLabs or
OpenAI speech), `POST /transcribe-token` (AssemblyAI, optional). All require the user's token.

## Running it

1. Backend up (`npm run dev -w backend`), agent built (`npm run build`), a token minted.
2. `~/.openclicky/shell.json`:
   ```json
   { "cliCommand": ["node", "/path/to/openclicky/agent/dist/cli.js"],
     "backendUrl": "http://localhost:8787", "token": "<jwt or session token>",
     "workspace": "/Users/you/OpenClicky", "model": "", "transcriptionProvider": "openai" }
   ```
3. `open leanring-buddy.xcodeproj`, set your signing team, run. (Upstream's advice stands: build
   from Xcode for day-to-day work; `xcodebuild` from a terminal can reset TCC permissions.)
4. Hold ctrl+option, speak. Questions get the pointing teacher; "make/fix/create…" goes to the agent.

Headless compile check used in CI-like runs: `xcodebuild -project leanring-buddy.xcodeproj -scheme leanring-buddy build CODE_SIGNING_ALLOWED=NO`.
