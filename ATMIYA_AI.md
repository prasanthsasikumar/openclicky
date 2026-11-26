# ATMIYA AI

Personal macOS AI agent. Forked from **OpenClicky** (MIT-licensed; see `LICENSE` and
`macos/OpenClicky/LICENSE`, and `macos/OpenClicky/OPENCLICKY.md` for provenance). The application is
renamed **ATMIYA AI** (`PRODUCT_NAME`, `CFBundleDisplayName`, bundle id `org.atmiya.app`); all
original copyright and license notices are preserved.

## What it does

- **Screen awareness**: ScreenCaptureKit capture; the model sees your screen.
- **Cursor assistant**: a cursor "buddy" flies to and points at UI elements (`[POINT:x,y]`, `point_at`).
- **Voice recognition**: hold ⌃⌥ to talk; Apple on-device speech recognition (offline); optional
  Gemini realtime (Gemini Live); hold **fn** for dictation with per-app styles, dictionary, history.
- **Dictation**: offline Apple recognizer by default; optional Gemini polish; pasted into the app in front.
- **Native macOS interface**: notch/island HUD, menu bar, SwiftUI settings, Keychain-backed secrets.
- **Agent lane ("do work")**: hands tasks to **OpenCode** (replaces OpenAI Codex). Real files/commands;
  free `opencode/*` models, Google Gemini, or local Ollama.

## No paid services required

- **Free Google Gemini API** support. Create a **free** key at https://aistudio.google.com/apikey
  (no paid tier needed) and add it in Settings, or set `GEMINI_API_KEY`. Nothing paid is purchased.
- **Optional local Ollama**. Install Ollama and pull a local model (a download you approve, e.g.
  `ollama pull llama3.2-vision`); enable "Ollama (local)". No key, no cloud, no cost.
- **Offline modes**. Dictation + Apple on-device recognition + this Mac's voice work with no key.
- Paid OpenAI / Anthropic keys are **optional**, never mandatory. The local backend
  (`npm run dev -w backend`, http://localhost:8787) is self-hosted; no paid account.

## API keys are stored securely

- App API keys are stored in the **macOS Keychain** (`org.atmiya.app.secrets`,
  `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`, not iCloud-synced). They are **not** written to
  plaintext `~/.openclicky/shell.json`; existing plaintext keys are migrated and removed. Keys are
  never logged. `shell.json`/`~/.openclicky` are mode `0600`/`0700`.
- Agent provider keys are **not** forwarded to agent-spawned shells (explicit env allowlist).
- Backend keys live in git-ignored `backend/.dev.vars` (mode `0600`); `SESSION_TOKEN_SECRET` is local.

## Data is local

App data + settings in `~/.openclicky`; app data in `~/Library/Application Support`; history in
local SQLite; the backend is self-hosted. Nothing is sent anywhere except the model vendor you choose.

## Build & run (you approve system changes)

```bash
scripts/setup-atmiya.sh                       # npm install + build + tests + local .dev.vars
npm run dev -w backend                        # http://localhost:8787  (no paid keys)
# native app needs Xcode (not just Command Line Tools):
cd macos/OpenClicky && open OpenClicky.xcodeproj   # scheme OpenClicky; ad-hoc sign; Run
```

Then grant **Microphone, Speech Recognition, Screen Recording, Accessibility** when macOS asks.
`shell.json` (`~/.openclicky/shell.json`) e.g.:

```json
{ "cliCommand": ["node", "/ABS/PATH/agent/dist/cli.js"],
  "backendUrl": "http://localhost:8787",
  "answerSource": "gemini",
  "ollamaEnabled": false, "ollamaBaseUrl": "http://localhost:11434", "ollamaModel": "llama3.2-vision",
  "workspace": "~/ATMIYA" }
```

API keys (e.g. `geminiApiKey`) placed in shell.json are migrated into the Keychain on launch; prefer
adding them in Settings.

## Notes / known limitations

- The full SwiftUI app builds with **Xcode**; Command Line Tools alone can `swift build` only
  `macos/OpenClickyShell`. (Asks for Xcode install — a system change you approve.)
- The optional `openclicky talk` CLI lane uses a realtime provider; in-app voice uses Gemini Live /
  Apple on-device + local TTS. No paid realtime is required for the app.
- `ffmpeg` is only needed for the standalone `openclicky voice` CLI lane; the native app uses CoreAudio.
