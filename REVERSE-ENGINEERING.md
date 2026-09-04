# OpenClicky — Reverse-Engineering Reference & Build Spec

> Source of truth for building **OpenClicky**, an open-source alternative to **HeyClicky** (v1.0.48).
> Everything below was recovered from the shipped `HeyClicky.app` bundle on 2026-09-02.

---

## 0. TL;DR — What HeyClicky actually is

A **native macOS AI voice assistant** (SwiftUI, not Electron) with:

- A floating "notch"/HUD UI + floating session button + push-to-talk and always-on voice
- Screen capture for visual context
- **OpenAI Codex CLI** (bundled, v0.132.0) as the autonomous agent engine
- **Claude Haiku** as a cheap "gate" model that classifies/paywalls each request before the expensive agent runs
- **GPT-5.6-luna** as the main agent/"higher" model
- **OpenAI Realtime** (`gpt-realtime-2.1`) for voice, **Deepgram** for STT fallback
- **Composio MCP** for external integrations (Gmail, Notion, GitHub, Sheets, Calendar, Drive, Docs, Linear, Slack…)
- **cua-driver** (trycua.com) for Computer Use / real macOS GUI control
- **Supabase** for auth + a **Cloudflare Worker** (`api.heyclicky.com`) that proxies all model calls and holds the provider keys
- **PostHog** (analytics), **Sentry** (crash + session replay), **Sparkle** (auto-update)

Identity: `com.humansongs.clicky`, Developer: the original vendor (see the MIT LICENSE in macos/OpenClicky). Internal codename `leanring-buddy`, closed repo `clicky-closed`, workspace `louisville-v1`.

---

## 1. Product surface to replicate

From Info.plist + binary strings + bundled assets, the feature set is:

1. **Onboarding** — "What should Clicky be good at?" skill picker (powered by vendored Hermes skills, see §9). Paywall (`paywall-intro-v2.mp4`).
2. **Voice assistant** — push-to-talk + always-on listening, wake-word style barge-in, spoken task summaries.
3. **Notch HUD** — a menu-bar/"notch" anchored floating window: agent activity timeline, running agent cards, elapsed-time badges, docked cursor badge.
4. **Floating session button** — top-right `NSPanel`, gradient circle, hover scale+glow, shows when a session runs and the main window is unfocused.
5. **Screenshot context** — captures the screen (excluding its own floating UI) and attaches it to agent turns.
6. **Document reading** — reads the full text of the foreground document (not just visible screen) via Desktop/Documents/Downloads access.
7. **Agent mode** — explicit "do work" lane that spawns Codex agents on threads; multiple background threads alive at once.
8. **Realtime "higher model" lane** — lightweight voice/visual answers without a full agent spawn (`send_to_higher_model`).
9. **Integrations** — Settings → Integrations UI; Gmail, Google Sheets/Calendar/Drive/Docs, Notion, GitHub, Linear, Slack, Spotify, Obsidian, and more via Composio.
10. **Computer Use** — real macOS app + browser GUI control via cua-driver, backgrounded (no focus stealing).
11. **Permissions pages** — Accessibility, Screen Recording, Microphone, Speech Recognition, Desktop/Documents/Downloads.
12. **Account** — email auth (Supabase), account deletion, subscription/paywall gating.
13. **Remote Tasks / cloud crons** — *not shipped* in this build (explicitly gated out in the model instructions).

### Permission usage descriptions (reuse these strings, they're good)

- Screen Recording: "HeyClicky needs screen recording access to see your screen and help you."
- Microphone: "HeyClicky uses your microphone so you can talk to it"
- Speech Recognition: "HeyClicky uses speech recognition to transcribe your voice when you talk to it"
- Desktop/Documents/Downloads: "HeyClicky reads the document you're viewing so it can answer about the whole file, not just what's visible on screen."

---

## 2. Architecture blueprint (4 layers)

```
┌─────────────────────────────────────────────────────────────┐
│  SwiftUI shell  (46MB Mach-O, native)                        │
│  NotchRootView · CodexHUDWindow · FloatingSessionButton      │
│  RealtimeVoiceClient · RealtimeMicrophoneCapture             │
│  ScreenshotManager · ActiveDocumentReader · Paywall          │
└───────┬───────────────────────┬──────────────────┬───────────┘
        │ JSON-RPC over stdio    │                   │
        ▼                       ▼                   ▼
┌───────────────┐   ┌─────────────────────┐   ┌──────────────────┐
│ Codex CLI     │   │ cua-driver daemon   │   │ Composio MCP     │
│ 0.132.0       │   │ (embedded, --socket)│   │ (integrations)   │
│ + MCP servers │   │ computer-use MCP    │   │                  │
│ + skills      │   │ (Computer Use)      │   │                  │
└───────┬───────┘   └─────────────────────┘   └──────────────────┘
        │ model calls (proxied)
        ▼
┌─────────────────────────────────────────────────────────────┐
│  Cloudflare Worker  api.heyclicky.com                        │
│  /agent/* · /skills/* · /auth/* · /codex-thread-launch       │
│  holds real OpenAI/Anthropic/Deepgram keys                   │
└───────┬─────────────────────────────────────────────────────┘
        │ auth
        ▼
┌─────────────────────────────────────────────────────────────┐
│  Supabase  mrpvynsdsnimuisyhkow.supabase.co                  │
│  email auth · user data · skill activations · programs        │
└─────────────────────────────────────────────────────────────┘
```

The client never holds provider keys. It authenticates to Supabase, gets a JWT, and presents that JWT to the Worker, which authorizes Anthropic/OpenAI/Realtime/Deepgram calls server-side. Binary string that proves this: `"Anthropic chat auth failed, retrying with refreshed Supabase token"`.

---

## 3. Model stack & routing (the two-tier trick)

| Role | Model | Transport |
|---|---|---|
| Main agent / "higher" model | `gpt-5.6-luna` | via Worker → OpenAI |
| Gate model (cheap pre-flight) | Claude **Haiku** | via Worker → Anthropic (`api.anthropic.com/v1/messages`) |
| Realtime voice | `gpt-realtime-2.1` | `wss://api.openai.com/v1/realtime?model=` |
| STT fallback | Deepgram | `wss://api.deepgram.com/v1/listen` |
| Optional vision/provider | Gemini | `GEMINI_API_KEY` env var referenced |

**Two-tier routing pattern** (the core product idea to copy):

1. Every task launch is gated by a cheap **Claude Haiku** call first ("Haiku launch-label gate") — it decides task classification / paywall / whether to spawn a full agent. Binary strings: `[launchTask] awaiting Haiku gate before submit`, `Haiku launch-label gate cleared/denied`.
2. Lightweight voice or "what's on screen" questions go through a separate **higher-model** lane (`send_to_higher_model`) instead of spawning a Codex agent — cheap, fast, no full agent lifecycle.
3. Real "do work" requests spawn a Codex agent thread (`sessions_spawn`).

Also present: a **Claude proxy chat** mode (`ClaudeProxyChatRequest`, `ClaudeSSEFormat`) — "I use Claude in the background" — for ChatGPT-style chat, streaming via SSE.

---

## 4. Voice stack details

Recovered Realtime subsystem (Swift files: `RealtimeVoiceClient`, `RealtimeMicrophoneCapture`, `RealtimeDuplexAudioEngine`, `RealtimePlayer`):

- **Push-to-talk** and **always-on** listening modes.
- **Duplex AEC (acoustic echo cancellation)** engine — "AEC topology verdict", "duplex AEC engine PROMOTED".
- **Barge-in / interrupt gates** — always-on `speech_start` must pass a barge-in gate before interrupting a response.
- **Session health polling** — after opening a WebSocket session it polls until "healthy" before committing audio.
- **400ms tail commit** — keeps the mic open 400ms after PTT release, then commits.
- **Realtime tool calls** — the voice session itself can invoke tools (`[Realtime tool] call name=`, `play_on_spotify`, `shell_tool`, calendar events).
- **"Acknowledgement-only" commits** — `commitAudioAndRequestAcknowledgementOnly` (model just acks, words are forwarded to a Codex thread).
- **Clipboard handoff** — higher-model responses can be injected to clipboard.
- Voice session endpoints on the Worker: `/agent/realtime/session`, `/agent/realtime/turn`, `/agent/realtime/warmup`, plus `/agent/realtime/spotify/search`, `/agent/realtime/google-calendar/events(+/create)`.
- OpenAI Realtime event names used: `conversation.item.input_audio_transcription.completed`.

**Voice TTS previews** (16 bundled `voice-preview-*.mp3` + `realtime-voice-preview-*.mp3` files) suggest ~11 distinct voices (alloy, ash, ballad, cedar, coral, echo, marin, sage, shimmer, verse, fun, techy, expert, kid, original, bubbly, cheerful, gentle, polished, smooth, bright, hope, original). Sound effects: `clicky-question.wav`, `clicky-text-send/receive/open/close.wav`, `skill-up/down.wav`, `hatching.wav`, `enter.mp3`, `eshop.mp3`, `connection-question.wav`, `agent-launch.m4a`, `agent-done.m4a`, `agent-close.m4a`.

---

## 5. Agent engine — Codex CLI integration

Bundled under `Contents/Resources/CodexRuntime/`:

```
CodexRuntime/
  .clicky-codex-version        # "0.132.0:cua-0.21.0-<sha>"
  bin/codex                    # tiny POSIX sh shim → picks arch triple
  vendor/aarch64-apple-darwin/codex/codex   # 185MB arm64 binary
  vendor/aarch64-apple-darwin/path/rg       # 3.9MB ripgrep
  vendor/x86_64-apple-darwin/...            # same for Intel
```

The `bin/codex` shim (verbatim logic):

```sh
#!/bin/sh
set -eu
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
CURRENT_MACHINE_ARCHITECTURE="$(uname -m)"
case "${CURRENT_MACHINE_ARCHITECTURE}" in
  arm64)  TARGET_TRIPLE="aarch64-apple-darwin" ;;
  x86_64) TARGET_TRIPLE="x86_64-apple-darwin" ;;
  *)      echo "unsupported arch"; exit 1 ;;
esac
TARGET_PATH_DIRECTORY="${SCRIPT_DIR}/../vendor/${TARGET_TRIPLE}/path"
[ -d "$TARGET_PATH_DIRECTORY" ] && export PATH="${TARGET_PATH_DIRECTORY}:${PATH:-}"
exec "${SCRIPT_DIR}/../vendor/${TARGET_TRIPLE}/codex/codex" "$@"
```

**How the Swift app drives Codex:**

- Spawns `codex` as a subprocess, talks over **JSON-RPC stdio** (`CodexProtocolClient.swift`: `Sending initialize...`, `Initialize response`, `stdin write failed`, `stdout EOF`).
- Uses an **isolated `CODEX_HOME`** per run: `[Codex Process] Started app-server with isolated CODEX_HOME at ...`.
- Maintains **threads** (`threadID`), persists per-thread state in UserDefaults under keys like `clicky.codex.agentsTabLastViewedAtByThreadID.v1`, `clicky.codex.fileDiffsByThreadID.v1`, `clicky.codex.finalSummariesByThreadID.v1`, `clicky.codex.hiddenThreadIDs.v1`.
- Calls `sessions_spawn` to launch agents, `send_to_higher_model` for the lightweight lane.
- **Orphan sweep**: on shutdown it lists processes (`/bin/ps -axo pid=,ppid=,comm=`) and kills orphaned `app-server` / `node` / `codex` children.
- Screenshots are attached as `localImage` to Codex turns (`[Codex] Attached screenshot as localImage`).
- "Lease" polling for task lifecycle: `Failed to complete Codex lease`, `Failed to poll Codex lease status`.

### Recovered Codex config TOML

```toml
js_repl = true
multi_agent = true

[mcp_servers.openaiDeveloperDocs]
url = "https://developers.openai.com/mcp"

[[skills.config]]
path = ""
enabled = true

[mcp_servers.clicky-crons]
command = ""
startup_timeout_sec = 10.0

[mcp_servers.computer-use]
args = "--socket"
env = { CUA_DRIVER_EMBEDDED = "1", CUA_DRIVER_RS_TELEMETRY_ENABLED = "false", CUA_DRIVER_RS_UPDATE_CHECK = "false" }
startup_timeout_sec = 20.0
enabled_tools = ""

[mcp_servers.composio]
url = ""
bearer_token_env_var = "OPENAI_API_KEY"
tool_timeout_sec = 120.0

[projects.<workspace>]
trust_level = "trusted"
model_instructions_file = "<ClickyModelInstructions.md>"
```

---

## 6. Computer Use — cua-driver

`Contents/Helpers/cua-driver` is a 62MB Mach-O (v0.21.0) by **Cua (trycua.com)**.

- The app **embeds it as a daemon spawned inside Clicky.app** so macOS attributes Accessibility + Screen Recording to HeyClicky's own bundle identity (`com.trycua.driver` is only the daemon's TCC identity when standalone).
- Exposed to Codex as the **`computer-use` MCP server** over a socket (`--socket`), via a thin stdio proxy.
- Tools (recovered): `launch_app`, `list_apps`, `get_window_state`, `elements`, `click`, `double_click`, `middle_click`, `type_text`, `set_value`, `press_key`, `scroll`, `drag`, `move_cursor`, `capture`/screenshot, `page` (CDP browser DOM driving), `get_browser_state`.
- Delivery modes: `background` (default, no focus steal) vs `foreground`.
- Snapshot-Act-Verify contract: `elements` → act by `element_token` → verify via `effect` field + re-snapshot.
- CDP browser support (Chrome via `--remote-debugging-port`), `insert_text` vs `keystrokes` typing modes.
- **Multi-agent compat surface** (`--compat` flag): `claude, codex, cursor, hermes, antigravity, openclaw, opencode, pi, prime-agent, qwen, droid, zcode`.
- Self-serve CLI: `serve`, `doctor`, `status`, `permissions status|grant`, `config`, `telemetry`, `update`, `install-service`, `recording start|stop|render` (encrypted Computer History).
- Browser-driving guidance baked in: "Chrome refuses to open `--remote-debugging-port` on its default data directory… pass `--user-data-dir=<other path>`".

The `cua-driver` bundled skill pack ships `SKILL.md`, `README.md`, `RECORDING.md`, `TESTS.md`, `WEB_APPS.md`.

---

## 7. Integrations — Composio MCP

External apps go through **Composio**, exposed to Codex as the `composio` MCP server. Config: `bearer_token_env_var = "OPENAI_API_KEY"`, `tool_timeout_sec = 120.0`.

- **Google Workspace is split** into separate toolkits: `gmail`, `googlesheets`, `googlecalendar`, `googledrive`, `googledocs`.
- Toolkit schema tool: `COMPOSIO_GET_TOOL_SCHEMAS` (Worker caches schema ~10 min).
- Notion file uploads via `NOTION_CREATE_FILE_UPLOAD` → `NOTION_SEND_FILE_UPLOAD` → attach `file_upload` ID; `NOTION_APPEND_MEDIA_BLOCKS` only for public HTTPS URLs.
- The **approval gate** doctrine (worth copying verbatim):
  - "The user's instruction IS the approval." Perform the write directly, don't draft-then-ask.
  - Confirm first ONLY for: deleting/archiving data, overwriting content the user didn't ask to touch, sending email, spending money.
  - Gmail sends are draft-first + explicit approval (recipients/subject/body).

---

## 8. Worker API surface (api.heyclicky.com)

Endpoints recovered from the binary:

```
/auth/v1/authorize
/auth/v1/token?grant_type=refresh_token
/auth/v1/user
/auth-callback

/agent/session-token
/agent/realtime/session
/agent/realtime/turn
/agent/realtime/warmup
/agent/realtime/spotify/search
/agent/realtime/google-calendar/events
/agent/realtime/google-calendar/events/create
/agent/composio/session

/codex-thread-launch
/proactive-agents
/projects
/programs

/skills/library
/skills/create
/skills/creations
/skills/activations/sync
```

Supabase instance: `https://mrpvynsdsnimuisyhkow.supabase.co`. Anon key + Sentry DSN in the shipped Info.plist are **redacted placeholders** (`eyJhbG...tTAY`, `76fcb9...aa93`) — real values are not in this binary; they're fetched at runtime or stripped from this `internal-testing` build.

---

## 9. Skills system — Hermes-format

Two sets:

**(A) Vendored Hermes Agent skills** (unmodified, MIT, Nous Research) — used for the onboarding "What should Clicky be good at?" picker. `ATTRIBUTION.md` says verbatim: "vendored unmodified from NousResearch/hermes-agent … Backend wiring … intentionally not included yet." Full list:

`apple-notes`, `apple-reminders`, `findmy`, `imessage`, `claude-code`, `codex`, `excalidraw`, `github-auth`, `github-code-review`, `github-issues`, `github-pr-workflow`, `github-repo-management`, `google-workspace` (author: Nous Research), `linear`, `maps` (Mibayy), `notion`, `ocr-and-documents`, `polymarket`, `spotify`, `youtube-content`, `powerpoint`, `airtable`, `blender-toolkit`, `claude-design` (BadTechBandit), `obsidian`.

**(B) Clicky's own bundled skills** (in `ClickyBundledSkills/`) — these define the routing surface:

| Skill | Purpose |
|---|---|
| `clicky-artifacts` | Open/reveal/find/export/rename generated files |
| `clicky-build-preview` | Build/preview/iterate websites & web apps |
| `clicky-creative-studio` | Route broad creative work to available capabilities |
| `clicky-dev-setup-doctor` | Debug dev env / MCP / API keys / localhost |
| `clicky-email-assistant` | Draft/rewrite/triage/reply email |
| `clicky-google-workspace` | Gmail/Calendar/Drive/Docs/Sheets via Composio |
| `clicky-repo-operator` | Git/GitHub: branches, commits, PRs, CI |
| `clicky-research-report` | Web research → MD/PDF/DOCX/CSV reports |
| `cua-driver` | Computer Use instruction surface |
| `doc` | .docx create/edit (python-docx) |
| `frontend-design` | Frontend UI polish |
| `obsidian` | Obsidian vault read/search/edit |
| `pdf` | PDF read/create/render (reportlab/pdfplumber/pypdf, Poppler) |
| `spreadsheet` | .xlsx/.csv (formula-aware) |
| `vercel-deploy` | Deploy to Vercel |

Skill file format is the **same Hermes SKILL.md format**: YAML frontmatter (`name`, `description`) + markdown body. This is the format to adopt for OpenClicky.

**(C) What the changelog adds** (research 2026-09-04; the full mirror is in `reference/upstream/heyclicky-changelog.md`, kept current by `scripts/upstream-watch.mjs`). The bundle above is only the agent layer. HeyClicky also carries **app-teaching skills for 89 apps with browser-site matching**, injected "based on the program you're in" (v1.0.26, v1.0.28; served from the `/programs` route, never in the bundle), and a **community skills library** of about 100 skills: hover the notch, "Add skill", one click to activate; "Create a skill" types what it should do and the model writes it (v1.0.33); Skills 2.0 added capability-aware creation, a "My Skills" filter and approval emails (v1.0.34–36); team-private sharing came in v1.0.44 (`/skills/create`, `/skills/creations`, `/skills/activations/sync`). OpenClicky now implements: the agent layer (`skills/`), an app-teaching layer (`app-skills/`, 16 apps/sites, matched by bundle id or URL host and injected into the voice prompts), and a local user library with one-click activation and "Create a skill" through `POST /skills/create`. Not implemented: the approval queue, the "My Skills" filter, team sharing, and server-side activation sync.

---

## 10. The agent behavior contract (ClickyModelInstructions.md)

This is the single most valuable artifact for building a clone — the full prompt contract that makes the agent "feel" right. It's saved verbatim at:

**`reference/clicky-model-instructions-verbatim.md`**

Key doctrines to lift:

1. **Skill names are implementation labels**, not user commands — route by intent, don't teach users raw skill names.
2. **Routing ladder** (narrowest first): structured/local tools → resume owning thread → Composio (connected external APIs) → cua/Computer Use (last-mile native/browser UI only).
3. **Screenshots are context, not route selection** — seeing Gmail on screen ≠ explicit visible-UI request.
4. **Never silently switch** from a failed structured integration to visible UI control; point to Settings → Integrations instead.
5. **Eager doer, not drafter** — for writes the user asked for, just do it; confirm only for delete/archive/overwrite/send/spend.
6. **No `osascript`/`cliclick`/`Cmd+L`/raw CGEvent GUI shims** — use cua-driver; `open` only for finished files/URLs.
7. **macOS permission-prompt storm avoidance** — never scan multiple protected folders (Desktop/Documents/Downloads) to "find" a file; default to a project working dir; touch only the ONE folder the task needs, once.
8. **Blocked route ≠ auth problem** — if a capability isn't shipped, say so, don't offer fake "retry" buttons.
9. **Concrete verification** — verify writes with a structured read-back; a `successful: true` isn't enough.
10. **Browser work stays calm/backgrounded** — new background windows, no tab churn, no focus stealing.

---

## 11. macOS app details

- **Native SwiftUI**, `LSUIElement = true` (no Dock icon, menu-bar/agent app).
- Min macOS 14.2, built with Xcode 26.0 (SDK macOS 26.5), universal binary.
- URL schemes: `clicky://` and `heyclicky://` (auth callback).
- Hardened runtime + notarized Developer ID.
- Swift source files (from symbol table): `CodexAgentSession`, `CodexHUDWindow`, `CodexProtocolClient`, `CodexRuntimeBridge`, `ProactiveAgentsClient`, `RealtimeDuplexAudioEngine`, `RealtimeMicrophoneCapture`, `RealtimePlayer`, `RealtimeVoiceClient`, `ClaudeAPI`, `ClaudePromptProfile`, `ClaudeProxyChatRequest`, `ActiveDocumentReader`, `AgentPermissionInspector`, `BlueCursorView` (annotation highlight), `NotchRootView`, `NotchAgentsTab`, `NotchActivitySurface`, `FloatingSessionButtonManager`, `ScreenshotManager`, `LocalRootToolExecutor`, `SkillLibraryBackend`.

- Frameworks bundled: **Sparkle** (auto-update), **Sentry**, **PLCrashReporter**, **PostHog**.
- Helper binaries: `cua-driver`.
- Media: onboarding + paywall videos, spatial demo, 20+ TTS voice previews, ~14 UI sound effects, `steve.jpg` (Steve Jobs), `AppIcon.icns`.

---

## 12. Telemetry & distribution

- **PostHog** API key `phc_xcQPygmhTMzzYh8wNW92CCwoXmnzqyChAixh8zgpqC3C` @ `https://us.i.posthog.com`.
- **Sentry** DSN → `o4511254886154240.ingest.us.sentry.io/4511254887137280` (key redacted).
- **Sparkle** feed on the original vendor's GitHub Pages (`clicky-releases/appcast.xml`), Ed25519 pub key `2bZCRkVZa++RiAgbbZ08+asMwyKheOK9jkbJXu5mU6c=`, auto-check every 3600s.

---

## 13. Open-source building blocks for OpenClicky

| HeyClicky component | OpenClicky replacement |
|---|---|
| Native SwiftUI shell | SwiftUI (same) or Tauri (Rust + web UI) for faster iteration |
| Codex CLI (agent engine) | `openai/codex` CLI (open-source, same as bundled) — or `claude-code`, `aider`, `opencode` |
| cua-driver (Computer Use) | `trycua/cua` or the cua-driver skill pack — supports a `hermes` compat surface |
| Composio (integrations) | `ComposioHQ/composio` — open-source, self-hostable, MCP server |
| Supabase auth/DB | Self-hosted Supabase or plain Postgres + Auth |
| Cloudflare Worker (key proxy) | Cloudflare Worker / Hono / a tiny Fastify server |
| Realtime voice | OpenAI Realtime API (or open-source: `livekit-agents`, Whisper STT + a TTS provider) |
| STT fallback | Deepgram / Whisper (open) |
| Gate model | any cheap model (Claude Haiku, gpt-4o-mini, Gemini Flash) |
| Skills | Hermes SKILL.md format (MIT) |
| Analytics | PostHog (open-source) / Plausible |
| Crash reporting | Sentry (open-source) |
| Auto-update | Sparkle |

---

## 14. Suggested implementation roadmap

**Phase 0 — Agent core (get the "do work" lane working headless first)**
1. Vendor the Codex CLI (or opencode/claude-code) + a TOML config with `computer-use` and `composio` MCP servers.
2. Port `ClickyModelInstructions.md` → OpenClicky's own `ModelInstructions.md` (it's the product's soul).
3. Write the JSON-RPC stdio bridge to spawn agents, manage threads, and attach screenshots.

**Phase 1 — Backend**
4. Supabase auth (email) + a Worker that proxies OpenAI/Anthropic/Realtime/Deepgram and holds keys.
5. Implement `/agent/realtime/*` for voice session tokens, `/skills/*` for the skill library.

**Phase 2 — Voice**
6. OpenAI Realtime voice client (PTT + always-on + barge-in + AEC).
7. STT fallback (Deepgram/Whisper).

**Phase 3 — Native shell**
8. SwiftUI notch/HUD window, floating session button, screenshot manager (exclude own UI), document reader.
9. Permissions onboarding (Accessibility, Screen Recording, Mic, Speech Recognition, folders).

**Phase 4 — Integrations & Computer Use**
10. Composio MCP (Gmail/Notion/GitHub/Sheets/etc.) + Settings → Integrations UI.
11. cua-driver embedded daemon + `computer-use` MCP server.

**Phase 5 — Skills & polish**
12. Ship the OpenClicky skill set (`openclicky-*` + generic `doc`/`pdf`/`spreadsheet`/`frontend-design`).
13. Telemetry, crash reporting, auto-update, paywall (if you're commercializing).

---

## 15. Risks / legal notes

- **Hermes skills are MIT** (Nous Research) — safe to reuse with attribution. Keep the LICENSE + ATTRIBUTION. `powerpoint` skill is **Proprietary** (excluded from MIT reuse). `blender-toolkit` is MIT via `dev-gom/claude-code-marketplace`.
- **cua-driver** is a third-party product (trycua.com) — check its own license before bundling; it may not be redistributable under the same terms HeyClicky uses.
- **Codex CLI** is open-source (Apache/MIT, OpenAI) — bundling is fine, but it phones home to OpenAI auth; you'll want your own proxy as HeyClicky does.
- The model names (`gpt-5.6-luna`, `gpt-realtime-2.1`) are this build's choices — swap for whatever your stack uses.
- Don't copy the PostHog/Sentry/Supabase keys or the redacted secrets — they're the original developer's.
