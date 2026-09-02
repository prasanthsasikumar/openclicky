# OpenClicky — Initial Cut Implementation Prompt

> Paste this whole file into Claude Code as the task. It is self-contained.

---

## Mission

Build the **initial cut** of **OpenClicky**, an open-source macOS AI voice
assistant, modeled on HeyClicky (reverse-engineered). This first cut is a
**headless vertical slice**: a working agent that takes a task, runs it through
an agent engine, and returns results — plus the key-holding backend it talks to.
Do NOT build the native UI, voice, or Computer Use yet (those are later phases).

## Read these first (in this order)

Read them fully before writing code:

1. `/Users/prasanthsasikumar/Documents/github/openclicky/REVERSE-ENGINEERING.md` — the master spec.
2. `/Users/prasanthsasikumar/Documents/github/openclicky/reference/clicky-model-instructions-verbatim.md` — the agent behavior contract to port.
3. `/Users/prasanthsasikumar/Documents/github/openclicky/reference/codex-config.toml` — the Codex CLI config to reproduce.
4. `/Users/prasanthsasikumar/Documents/github/openclicky/reference/clicky-bundled-skills/` — the skill templates.

The working directory is `/Users/prasanthsasikumar/Documents/github/openclicky`
(already contains `README.md`, `REVERSE-ENGINEERING.md`, and `reference/`).

## What to build

A monorepo with three parts, in this directory:

```
openclicky/
  agent/          # TypeScript bridge that drives the agent engine (Codex CLI)
  backend/        # Hono (Cloudflare Worker-compatible) API that holds model keys
  skills/         # ported skills + ModelInstructions.md
  config/         # codex config template
  .env.example
```

### Part 1 — `backend/` (the key-holding proxy)

A Hono TypeScript server (runs locally via `node` / `wrangler dev`, and is
deployable to Cloudflare Workers unchanged). This is the piece that lets users
run agents **without ever holding provider keys on the client** — same pattern
HeyClicky uses.

Endpoints:

- `GET /health` → `{ ok: true }`.
- `POST /v1/chat/completions` — OpenAI-compatible proxy to OpenAI (or any
  OpenAI-compatible base URL). Reads `OPENAI_API_KEY`, `OPENAI_BASE_URL`,
  `OPENAI_MODEL` from env/`.dev.vars`. Streams SSE responses.
- `POST /v1/messages` — Anthropic Messages proxy (`ANTHROPIC_API_KEY`).
- `POST /agent/session-token` — verifies a Supabase JWT (`Authorization: Bearer <jwt>`)
  and returns a short-lived session token (a signed value the agent attaches to
  subsequent calls). Use `SUPABASE_URL` + `SUPABASE_JWT_SECRET` (or `SUPABASE_SERVICE_ROLE_KEY`)
  to verify. On invalid JWT return 401.
- Middleware: every `/v1/*` and `/agent/*` route (except `/health`) requires a
  valid Supabase JWT or session token.

Auth flow to implement: client signs into Supabase (their own instance, see
`.env.example`), gets a JWT, exchanges it for a session token, then the agent
presents that token on model calls.

### Part 2 — `agent/` (the agent engine bridge)

TypeScript. Drives **OpenAI Codex CLI** (install via `npm i -g @openai/codex`,
or download the binary) exactly the way HeyClicky does:

- Spawn `codex` as a subprocess.
- Talk to it over **JSON-RPC on stdio** (Codex's `codex exec` / protocol).
- Isolate state: set a per-run `CODEX_HOME` to a temp dir so runs don't collide.
- Thread management: map user tasks to Codex threads; support resuming a thread.
- Attach a screenshot path as image context when one is provided (stub is fine —
  pass a local image path through to Codex's image support; if Codex doesn't
  accept it in this build, log "screenshot attach not yet wired" and continue).
- Two call modes, mirroring HeyClicky's two-tier routing:
  - `run` — full agent run (`sessions_spawn` equivalent).
  - `ask` — lightweight "higher model" path: a single chat completion against
    the backend (no agent spawn), for quick questions.

Provide a CLI:

```
openclicky run "add a .gitignore for a node project" [--thread <id>] [--image <path>]
openclicky ask "what does this error mean: ..." [--image <path>]
```

It must:
- Take `--backend-url` (default `http://localhost:8787`) and `--token`
  (Supabase JWT or session token) as config (env vars or flags).
- Print the agent's final answer and any generated artifact paths to stdout.
- Exit non-zero on failure.

### Part 3 — `skills/` + `config/`

- Port `reference/clicky-model-instructions-verbatim.md` → `skills/ModelInstructions.md`,
  rebranding "HeyClicky"/"Clicky" → "OpenClicky". Keep the routing doctrine,
  approval gate, style rules, and the macOS permission-prompt-storm guidance.
  Keep it faithful — this is the product's behavior contract.
- Copy the 16 bundled skills from `reference/clicky-bundled-skills/` into
  `skills/`, renaming the `clicky-*` ones to `openclicky-*` (update their
  `name:` frontmatter to match). Leave `cua-driver`, `doc`, `frontend-design`,
  `obsidian`, `pdf`, `spreadsheet`, `vercel-deploy` names as-is.
- Produce `config/codex-config.toml` from `reference/codex-config.toml`, but:
  - Remove `clicky-crons` (Remote Tasks are out of scope for this cut).
  - Point `model_instructions_file` at `skills/ModelInstructions.md`.
  - Point `[[skills.config]].path` at `skills/`.
  - Configure the model provider to use `backend/` as the OpenAI-compatible
    base URL so keys stay server-side (set `model_provider` / base URL + the
    session-token auth header as Codex supports it; if Codex can't do custom
    auth headers cleanly, document the exact env vars needed and wire it for a
    local API key for now, with a TODO to move to the proxy).

### Part 4 — repo scaffolding

- `package.json` (or a `pnpm`/`npm` workspace) so `backend/` and `agent/` build
  and run with `npm install && npm run build`.
- `.env.example` documenting every variable (`SUPABASE_URL`,
  `SUPABASE_ANON_KEY`, `SUPABASE_JWT_SECRET`, `OPENAI_API_KEY`, `OPENAI_MODEL`,
  `ANTHROPIC_API_KEY`, `BACKEND_URL`, `SESSION_TOKEN_SECRET`).
- A `README.md` at the repo root explaining: architecture, setup, how to run
  the backend, how to run the agent, and how the auth flow works.

## Definition of done (verify each with real output)

1. `cd backend && npm install && npm run dev` starts the server; `curl localhost:8787/health` returns `{ "ok": true }`.
2. `curl -X POST localhost:8787/v1/chat/completions` with a real OpenAI key in
   `.dev.vars` returns a streamed completion.
3. `openclicky ask "say hello in 5 words"` returns a real model answer (through the backend).
4. `openclicky run "create a file called hello.txt containing 'hi'"` creates
   `hello.txt` and prints the result. This is the smoke test that the whole
   pipeline works end to end.
5. `openclicky run ... --thread <id>` twice on the same thread resumes rather
   than starting fresh.
6. Auth: an unauthenticated `POST /v1/chat/completions` returns 401; with a
   valid Supabase JWT + exchanged session token it succeeds.

## Hard rules

- **No fake results.** Every claim of "working" must be backed by actual
  command output you ran. If Codex isn't installed or a model call fails, say
  so and fix or document the blocker.
- **Keys stay server-side.** The `agent/` code must never read `OPENAI_API_KEY`
  or `ANTHROPIC_API_KEY` from local env for model calls — it goes through
  `backend/`. (A documented local-dev fallback is acceptable only if clearly
  marked and gated behind an explicit flag.)
- **Don't copy secrets.** Do not reuse any PostHog/Sentry/Supabase key found in
  the reference material. The user provides their own Supabase instance.
- **Don't copy the Proprietary `powerpoint` skill.** Skip it entirely.
- **Scope discipline.** Do NOT implement: native SwiftUI shell, voice/realtime,
  cua-driver Computer Use wiring, Composio integrations, paywall, PostHog,
  Sentry, Sparkle. Stub or `TODO` them and move on.
- **TypeScript + Hono**, no new frameworks unless justified. Keep it small and
  readable — this is a first cut, not a production system.

## When done

Report: what was built, the exact commands you ran to verify each
definition-of-done item (with the real output), any blockers, and a short list
of "next cut" items you'd tackle after this (voice, native shell, Computer Use).
