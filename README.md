# OpenClicky

Open-source macOS AI voice assistant, modeled on HeyClicky. Current state: a **headless agent core
plus a first native shell scaffold**. What works today:

- `agent/` — a TypeScript CLI that drives **OpenAI Codex CLI** over JSON-RPC stdio (the "do work"
  lane), a lightweight **ask** lane, a cheap **gate** that routes between them, screenshots, a
  push-to-talk **voice** lane (ffmpeg + server-side speech-to-text), an always-on **talk** loop
  (OpenAI Realtime with a `send_to_agent` tool), and thread management.
- `backend/` — a **Hono** API that holds the provider keys, verifies **Supabase** auth, proxies model
  calls, mints Realtime voice secrets, transcribes audio, and serves the skill library. Runs on Node
  or Cloudflare Workers unchanged.
- `skills/` + `config/` — the ported agent behavior contract and 15 skills, rendered into an isolated
  Codex home on every run; optional Composio / cua-driver MCP servers via env.
- **Skills, three layers** (see [Skills](#skills)): the 15 built-in agent skills, 16 **app-teaching
  skills** in `app-skills/` that are matched to the app or site in front of you and injected into the
  voice prompts, and a **user library** in `~/.openclicky/skills` you fill from the notch HUD
  ("Create a skill" drafts one through the backend), the `openclicky skills` CLI, or by hand.
  Activated skills reach both talk and the Codex agent.
- **Pointing from Realtime voice**: the voice model gets a `point_at` tool, so "where is the export
  button?" flies the buddy to it while the answer is spoken, one call per step for walkthroughs.
- `scripts/upstream-watch.mjs` + a daily GitHub Action — follow HeyClicky releases (appcast +
  changelog) and open an issue per new version so features can be ported here.
- `macos/OpenClicky` — the native shell: a renamed fork of the original open-source Clicky app (MIT;
  see its LICENSE) with its cursor buddy, ScreenCaptureKit capture, push-to-talk, pointing, and
  TTS, rerouted through the OpenClicky backend and given an **Agent mode** that hands "do work"
  requests to a Codex thread via the CLI. Push-to-talk runs on **OpenAI Realtime** in-process
  (echo-cancelled AVAudioEngine, `send_to_agent` tool → Codex) with the transcribe + speak path as
  the fallback when Realtime is off or unavailable. `macos/OpenClickyShell` is a minimal SwiftPM panel kept
  as a headless smoke harness.

Not there yet: active-document reading, Composio/cua-driver themselves (only the wiring), HeyClicky's
skill approval queue and team-shared skills, drawing/circling annotations on screen, paywall,
analytics, crash reporting, auto-update.

## Architecture

```
  macos/OpenClickyShell (menu bar + ⌥Space panel)      openclicky voice   openclicky do "…"
        │ runs the CLI as a subprocess                       │ ffmpeg mic → /agent/transcribe
        ▼                                                    ▼                  │ gate: /v1/messages
  agent/  ──── ask lane: POST /v1/chat/completions ──────────────────────────▶  │   {"lane":"ask"|"agent"}
        │                                                                       ▼
        │ agent lane: spawn `codex app-server --stdio`, CODEX_HOME=~/.openclicky/codex-home
        │ initialize → thread/start | thread/resume → turn/start → (approvals) → turn/completed
        ▼
  Codex CLI ──── POST /v1/responses ──▶ backend/ (Hono) ── holds OPENAI_API_KEY ──▶ OpenAI
   model_provider = "openclicky"        │  requireAuth: Supabase JWT or session token
   env_key = OPENCLICKY_SESSION_TOKEN   │  POST /agent/session-token   JWT → session token
   model_instructions_file =           │  POST /v1/messages           ──▶ Anthropic (gate)
     skills/ModelInstructions.md       │  POST /agent/realtime/session ──▶ OpenAI Realtime secret
   [[skills.config]] path = skills/    │  POST /agent/transcribe       ──▶ OpenAI speech-to-text
   [mcp_servers.composio|computer-use] │  GET  /skills/library
                                       ▼
                                    Supabase Auth (your project)
```

Keys never leave the backend. The agent process strips `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` from
Codex's environment and only ever presents the user's token; the shell does the same for the CLI.

### Repository layout

| Path | What |
|---|---|
| `agent/src/codex.ts` | Codex bridge: spawn, JSON-RPC, threads, turns, streaming deltas, approvals, artifacts |
| `agent/src/cli.ts` | `openclicky run|ask|do|voice|talk|threads|skills|token` |
| `agent/src/gate.ts`, `ask.ts`, `audio.ts`, `screenshot.ts` | gate lane, ask lane, ffmpeg capture + STT, `screencapture` |
| `agent/src/codexHome.ts`, `config/codex-config.toml` | renders the isolated `CODEX_HOME/config.toml` (provider, skills, user skills, MCP servers) |
| `agent/src/skillsLibrary.ts`, `skillMarkdown.ts` | the user skill library on disk (`~/.openclicky/skills`): list, activate, sync `active/` symlinks, create; SKILL.md frontmatter parser |
| `backend/src/app.ts`, `auth.ts`, `proxy.ts` | routes, Supabase/session auth, streaming proxies, Realtime secret, STT |
| `backend/src/skillsCreate.ts` | `POST /skills/create`: drafts a SKILL.md from a one-line request (capability-aware) |
| `backend/scripts/` | `mint-dev-jwt.mjs` (local auth), `build-skills-manifest.mjs` (`skills/` + `app-skills/` → `src/skillsManifest.ts`) |
| `app-skills/` | 16 app-teaching skills (Finder, Safari, Chrome, Xcode, VS Code, Terminal, Figma, Slack, Mail, Notes, Preview, System Settings, Gmail, Google Docs, GitHub, YouTube); see its README |
| `docs/skills.md` | skill authoring guide (format, matching, budgets, activation) |
| `scripts/upstream-watch.mjs`, `reference/upstream/` | HeyClicky release watcher and its recorded versions / changelog mirror |
| `.github/workflows/upstream-watch.yml` | daily run of the watcher; opens an `upstream` issue per new HeyClicky version |
| `skills/` | `ModelInstructions.md` + 15 skills; regenerate with `npm run port-skills` |
| `macos/OpenClicky/` | primary native shell, renamed fork of the original open-source Clicky app + OpenClicky integration (`OPENCLICKY.md`); `SkillLibraryStore`, `AppSkillMatcher`, `SkillPromptBuilder`, `FrontmostAppObserver` are the skills side |
| `macos/OpenClickyShell/` | minimal SwiftPM menu-bar panel used as a headless smoke harness |
| `reference/`, `REVERSE-ENGINEERING.md`, `docs/superpowers/plans/` | reverse-engineering notes and the plans for each cut |

## Prerequisites

- Node 22+, and Codex CLI: `npm i -g @openai/codex` (developed against 0.152.1)
- `ffmpeg` for the voice lane (`brew install ffmpeg`); Xcode/Swift 5.9+ for the macOS shell
- A Supabase project (or just a JWT secret for local dev) and an OpenAI key; optionally an Anthropic
  key for the gate — all configured on the **backend only**

## Setup

```bash
npm install
npm run build          # builds backend/ and agent/ (regenerates the skills manifest)
npm test               # unit tests + integration tests against the real codex binary (fake model)

cp backend/.dev.vars.example backend/.dev.vars   # keys + secrets (backend)
cp .env.example .env                              # agent settings (BACKEND_URL, OPENCLICKY_TOKEN, …)
npm link -w agent                                 # optional: puts `openclicky` on your PATH
```

`backend/.dev.vars` needs at least `OPENAI_API_KEY`, `SESSION_TOKEN_SECRET`, and either
`SUPABASE_JWT_SECRET` (HS256, Supabase → Settings → API → JWT Secret) or `SUPABASE_URL` (asymmetric
keys, verified via JWKS). `ANTHROPIC_API_KEY` enables the gate; without it `do`/`voice` fall back to a
local heuristic. See `.env.example` for every variable.

## Choosing a provider

The backend speaks three upstream dialects, so any of these work:

| Setup | Agent (Codex) | Gate + teacher (Claude) | Ask | Speech in | Speech out | Realtime voice |
|---|---|---|---|---|---|---|
| OpenAI key + Anthropic key | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **OpenRouter key only** | ✓ (Responses API) | ✓ (Anthropic-format `/messages`) | ✓ | ✓ (`openai/gpt-4o-mini-transcribe`) | Mac system voice | ✗ |
| OpenRouter + ElevenLabs | ✓ | ✓ | ✓ | ✓ | ✓ | ✗ |

For OpenRouter set `OPENAI_BASE_URL=https://openrouter.ai/api/v1`, `ANTHROPIC_BASE_URL=https://openrouter.ai/api`,
the same key in both `*_API_KEY`s, and the model mapping (`OPENAI_MODEL_PREFIX=openai/`,
`ANTHROPIC_MODEL_PREFIX=anthropic/`, `MODEL_ALIASES=claude-sonnet-4-6=anthropic/claude-sonnet-4.6,…`); the
commented block in `.env.example` has the full set. When `/tts` has no provider the app speaks with the
built-in macOS voice.

Keep the backend running at login so the app works straight from Spotlight:

```bash
scripts/install-backend-service.sh       # launchd user agent org.openclicky.backend on :8787 (logs in ~/Library/Logs/OpenClicky)
scripts/install-backend-service.sh --uninstall
```

## Run the backend

```bash
npm run dev -w backend            # Node, http://localhost:8787 (reads backend/.dev.vars)
npm run dev:worker -w backend     # same app under `wrangler dev` (Cloudflare Workers runtime)
curl localhost:8787/health        # → {"ok":true}
```

| Route | Auth | Purpose |
|---|---|---|
| `GET /health` | none | liveness |
| `POST /agent/session-token` | Supabase JWT | `{ token, expiresAt, sub }` — short-lived HS256 session token |
| `POST /v1/chat/completions` | token | OpenAI Chat Completions proxy (SSE). `model: "default"` → `OPENAI_MODEL` |
| `POST /v1/responses` | token | OpenAI Responses proxy — what Codex uses (`wire_api = "chat"` is gone in Codex ≥ 0.15x) |
| `POST /v1/messages` | token | Anthropic Messages proxy. `model: "default"` → `ANTHROPIC_MODEL` (the gate) |
| `POST /agent/realtime/session` | token | mints an ephemeral OpenAI Realtime client secret (`OPENAI_REALTIME_MODEL`) |
| `POST /agent/transcribe` | token | `{ audio: <base64>, mime?, language? }` → `{ text }` via `OPENAI_TRANSCRIBE_MODEL` |
| `GET /skills/library` | token | the bundled skill manifest (id, name, description, kind, files; app skills carry `apps`/`sites`) |
| `POST /skills/create` | token | `{ request, capabilities? }` → `{ id, name, description, markdown }`: the model drafts one SKILL.md (`SKILL_CREATE_MODEL`, else `OPENAI_MODEL`) |

"token" = a Supabase JWT or an exchanged session token. Every request is logged as one JSON line
(method, path, status, ms, user id — never bodies or tokens). Deploy: `cd backend && npx wrangler deploy`,
then `wrangler secret put` each secret.

## Run the agent

```bash
export BACKEND_URL=http://localhost:8787
export OPENCLICKY_TOKEN=<supabase jwt or session token>   # see "Auth flow"

openclicky run "create a file called hello.txt containing 'hi'"     # full agent run on a new thread
openclicky run "now add a second line" --thread <id>                # resume (id is printed on stderr)
openclicky run "what is on my screen?" --screenshot                 # capture + attach (Screen Recording permission)
openclicky run "clean up the repo" --approve                        # ask before escalated commands / file changes
openclicky ask "say hello in 5 words"                               # one chat completion, no agent
openclicky do "what does ENOENT mean"                               # gate picks ask; `do "fix the build"` picks agent
openclicky voice --seconds 5                                        # record mic → transcribe → gate → ask/run
openclicky voice --file note.wav --transcribe-only
openclicky talk --voice marin                                       # always-on conversation via OpenAI Realtime (Ctrl-C to hang up)
openclicky threads list | show <id> | archive <id>
openclicky skills list | activate <id> | deactivate <id> | path       # the user skill library
openclicky skills create "reply to emails in my voice, short and warm"  # backend drafts it, saved + activated
```

`talk` is the HeyClicky-style voice loop: the backend mints an ephemeral Realtime client secret,
the CLI streams microphone PCM (ffmpeg) to OpenAI Realtime over WebSocket, plays spoken replies
(ffplay), handles barge-in with server VAD, and exposes a `send_to_agent` tool so the voice model
hands real work to a Codex thread (kept across the conversation). Transcripts print as `you:` /
`openclicky:`. Needs `ffmpeg` + `ffplay` and Microphone permission for your terminal.

Flags on `run`/`do`/`voice`: `--thread`, `--cwd`, `--model` (e.g. `gpt-5.6-luna`), `--approve`,
`--image`, `--screenshot`, `--json`, `--events`, `--verbose`, `--backend-url`, `--token`. Agent text
streams to stdout as it is generated; milestones (`thread: …`, `ran: …`, approvals) go to stderr;
`artifacts:` lists new/changed files in the workspace. Non-zero exit on failure. `--events` switches
to JSON Lines on stdout (`lane`, `event`, `delta`, `answer`, `result`, `error`) for UIs like the shell.

Codex state lives in an isolated `CODEX_HOME` (`~/.openclicky/codex-home`, `OPENCLICKY_CODEX_HOME`),
separate from your personal `~/.codex`, stable across runs so threads can be resumed. Its
`config.toml` is regenerated from `config/codex-config.toml` on every run. Approvals are
auto-accepted by default (the sandbox is `workspace-write`); `--approve` switches Codex to
`on-request` and prompts you. Set `COMPOSIO_MCP_URL` / `CUA_DRIVER_BIN` to render the `composio` /
`computer-use` MCP servers into the config.

## Run the macOS app

The primary shell is `macos/OpenClicky`: a renamed fork of the original open-source Clicky app (MIT,
MIT) wired to OpenClicky. See `macos/OpenClicky/OPENCLICKY.md` for the rename map and what changed.

```bash
open macos/OpenClicky/OpenClicky.xcodeproj     # set your signing team, then Run
```

Configure `~/.openclicky/shell.json` (panel → Backend row, or create it by hand):

```json
{ "cliCommand": ["node", "/path/to/openclicky/agent/dist/cli.js"],
  "backendUrl": "http://localhost:8787", "token": "<supabase jwt or session token>",
  "workspace": "/Users/you/OpenClicky", "transcriptionProvider": "openai",
  "appSkillsPath": "/path/to/openclicky/app-skills" }
```

`appSkillsPath` is optional: when it is missing the app derives `app-skills/` from the CLI path
(`…/agent/dist/cli.js` → repo root), else falls back to `~/.openclicky/app-skills`.

Hold ctrl+option and speak. With **Agent mode** on (default), a cheap gate classifies each
utterance: questions get the original teacher lane (Claude vision, spoken reply, the blue cursor
flies to and points at UI elements); "make / fix / create / run…" goes to a Codex thread through
`openclicky run --events` with the screenshot attached, and the final message is spoken. The
thread is resumed across turns so follow-ups keep context. The panel shows live agent milestones
and a "Reveal files" shortcut. Transcription, Claude, and TTS all go through the backend
(`/agent/transcribe`, `/chat`, `/tts`); no keys live in the app. Analytics are off unless you add a
PostHog key to Info.plist; launch-at-login is opt-in.

**Pointing from Realtime voice.** The Realtime session carries a `point_at(x, y, label)` function
tool next to `send_to_agent`. Every turn already attaches the cursor screen as an image, so when you
ask "where is the export button?" or "how do I open a new tab?", the model calls `point_at` with
pixel coordinates in that screenshot while it keeps talking; the app maps them to the display
(scale, y-flip, display origin) and the buddy flies there with the label as its bubble. Several
calls in one reply make a short walkthrough (one point per step); a repeated point re-flies. Tool
results go out at once, and the reply continues with a single `response.create` after the model's
turn ends, so batched calls do not race.

**Skills in the voice prompts.** At every key-down the app looks at the frontmost app (bundle id;
for browsers the front tab's URL through Accessibility, falling back to the window title), picks
the matching app-teaching skill, adds your activated talk skills, and sends the combined
instructions to the Realtime session (`session.update`, only when the text changed; the log shows
`instructions updated (N chars)`). The Claude teacher lane appends the same block to its system
prompt. The notch HUD's Home tab lists the library with on/off toggles and a "Create a skill" field.

### Releases and your local install

```bash
npm run release:mac            # Release build, signed, verified, zip + dmg, installed to /Applications
npm run release:mac:publish    # …and tag vX.Y.Z (from macos/OpenClicky/VERSION) + GitHub release
```

The script signs with your Apple Development certificate by default (fine for this Mac; other
Macs must right-click → Open). For public downloads set `OPENCLICKY_SIGN_IDENTITY="Developer ID
Application: …"` and, once `xcrun notarytool store-credentials` is set up, `OPENCLICKY_NOTARY_PROFILE`.
Bump `macos/OpenClicky/VERSION` before publishing. Releases: https://github.com/prasanthsasikumar/openclicky/releases

The minimal SwiftPM panel is still available for headless checks:

```bash
cd macos/OpenClickyShell && swift build -c release && .build/release/OpenClickyShell   # ⌥Space toggles
```

## Auth flow

1. The user signs in to **your** Supabase project and gets a JWT (`access_token`).
   Helper: `openclicky token --email … --password …` (needs `SUPABASE_URL` + `SUPABASE_ANON_KEY`).
2. The client exchanges it: `POST /agent/session-token` with `Authorization: Bearer <jwt>` →
   `{ token, expiresAt }`. The session token is an HS256 JWT signed with `SESSION_TOKEN_SECRET`,
   `typ: openclicky-session`, default lifetime 1h; it cannot be re-exchanged.
3. The agent presents that token on every backend call; Codex attaches it automatically because the
   provider config sets `env_key = "OPENCLICKY_SESSION_TOKEN"`. Raw Supabase JWTs are accepted too.

Local development without a Supabase project: put any `SUPABASE_JWT_SECRET` in `backend/.dev.vars`
and mint a user JWT with `npm run mint-jwt -w backend` — the same token shape Supabase issues under
its legacy HS256 secret, so it exercises the real verification path.

## Skills

HeyClicky ships skills in three layers, and so does OpenClicky. All of them are Hermes-style
`SKILL.md` files (YAML frontmatter with `name`, `description`, optional `apps`, `sites`, `surfaces`;
lists inline like `[a, b]`; Markdown body). Nothing is compiled into the app or the backend; the
files are read at run time. `docs/skills.md` is the authoring guide.

| Layer | Where | Who reads it |
|---|---|---|
| Built-in agent skills (15) | `skills/` | Codex, always on, via `[[skills.config]]` |
| App-teaching skills (16) | `app-skills/<id>/SKILL.md`, `apps: [bundle ids]` / `sites: [hosts]`, `surfaces: [talk]` | the Mac app: the one matching the frontmost app or site is injected into the Realtime and teacher prompts (≤ 4,000 chars) |
| User library | `~/.openclicky/skills/library/<id>/SKILL.md` (`OPENCLICKY_USER_SKILLS_DIR`) | activated ones only: Codex through `~/.openclicky/skills/active/` (symlinks, second `[[skills.config]]`), and the voice prompts when `surfaces` includes `talk` (≤ 6,000 chars total) |

Activation lives in `~/.openclicky/skills/activations.json` (`{ "active": [ids], "updatedAt" }`).
The CLI and the app write the same files: `openclicky skills list|activate|deactivate|create`, the
HUD's Home tab (toggles, "Create a skill", open folder), or drop a folder into `library/` by hand.
Every agent run re-syncs `active/` so Codex sees the current set. "Create a skill" posts your one
line to `POST /skills/create`; the backend asks the model for a SKILL.md that only relies on the
capabilities it is told about (built-in skill ids plus `composio` / `computer-use` when configured),
validates it, and the client saves and activates it. Set `SKILL_CREATE_MODEL` on the backend to use
a different model than `OPENAI_MODEL` for drafting. Matching rules: bundle id exact; a site matches
the browser host or any of its subdomains; a site skill beats the browser's own app skill; the most
specific site wins. Add an app skill by creating the folder and running `npm run build` (the
backend manifest, `GET /skills/library`, lists it with `kind: "app"`); the app picks it up on the
next voice turn.

Not ported from HeyClicky: the community approval queue with email, "My Skills" filter, and
team-private sharing (v1.0.34–36, v1.0.44).

### The behavior contract

`skills/ModelInstructions.md` is the ported HeyClicky agent contract (routing ladder, approval gate,
style, macOS permission-prompt-storm avoidance), rebranded. Codex loads it via
`model_instructions_file`; the 15 skills are exposed through `[[skills.config]]` and served by
`GET /skills/library`. Regenerate from `reference/` with `npm run port-skills`. See `skills/ATTRIBUTION.md`.

## Upstream: following HeyClicky

heyclicky.com has no RSS feed, but two machine-readable sources exist: the Sparkle appcast at
`farzaa.github.io/clicky-releases/appcast.xml` (version, build, date, DMG) and the changelog page.
`scripts/upstream-watch.mjs` fetches both, compares them with `reference/upstream/heyclicky-versions.json`,
and prints what is new.

```bash
node scripts/upstream-watch.mjs            # list versions not yet recorded
npm run upstream:check                     # …and record them (state JSON + reference/upstream/heyclicky-changelog.md)
node scripts/upstream-watch.mjs --issues   # …and open one GitHub issue per new version (needs gh + GH_TOKEN)
```

`.github/workflows/upstream-watch.yml` runs the `--issues` form daily (and on demand): each new
HeyClicky version becomes an issue labelled `upstream` with the changelog entry, the DMG link, and a
port checklist, and the updated state files are committed back. State is written before any issue
is opened and the issue URL is stored per version, so a failed `gh` call is retried next run and
never duplicated; the exit code is non-zero only when a fetch fails. `reference/upstream/heyclicky-changelog.md`
is a full mirror of the changelog (v1.0 → v1.0.48 at the time of writing), newest first.

For email notifications without CI: on github.com/farzaa/clicky-releases choose Watch → Custom →
Releases.

## Verification notes

Everything above was verified against the real Codex binary with a fake OpenAI/Anthropic upstream
(no provider keys were available while building). Model-quality behavior with real keys is untested;
with real keys, drop `--model gpt-5.2` (only the fake needs a classic-tools model — Codex's default
`gpt-5.6-*` models use its "code mode", which the backend passes through unchanged).

## Next

- Shell (`macos/OpenClicky`): stream agent milestones onto the cursor bubble, a text-input mode and
  Keychain token entry (upstream PR #80 is a good template), active-document reader, Sparkle feed
  for our own releases.
- Voice: wake word, spoken task-finished summaries, Deepgram/Whisper STT fallback.
- Backend: `/agent/realtime/turn|warmup`, `/skills/activations/sync` (cross-machine activations), Composio session brokering.
- Skills: the approval queue + "My Skills" filter, team sharing, and more app skills (HeyClicky covers 89 apps).
- Pointing: drawing/circling on screen (HeyClicky's spatial context and hand-drawn annotations).
- Agent: Composio + cua-driver end-to-end once those services are configured; barge-in/always-on voice.

## Reference material

`REVERSE-ENGINEERING.md` is the master spec recovered from HeyClicky v1.0.48; `reference/` holds
the verbatim model instructions, the original Codex config, the bundled skills, and license notes.
Hermes skills are MIT (Nous Research); the `powerpoint` skill is proprietary and is not copied;
cua-driver is third-party — check its license before bundling.
