# OpenClicky

Open-source macOS AI voice assistant, modeled on HeyClicky. **This is the initial cut: a headless
vertical slice.** It ships the two pieces everything else is built on:

- `agent/` — a TypeScript bridge that drives **OpenAI Codex CLI** over JSON-RPC stdio (the "do work"
  lane), plus a lightweight **ask** lane for quick answers.
- `backend/` — a **Hono** API that holds the provider keys, verifies **Supabase** auth, and proxies
  model calls. Runs on Node or Cloudflare Workers unchanged.

Not in this cut (stubbed / TODO): the SwiftUI notch UI, voice/realtime, Computer Use (cua-driver),
Composio integrations, paywall, analytics, crash reporting, auto-update.

## Architecture

```
  openclicky run "…"            openclicky ask "…"
        │                              │
        ▼                              │ POST /v1/chat/completions
  agent/src/codex.ts                   │ (Authorization: Bearer <token>)
   spawn `codex app-server --stdio`    │
   CODEX_HOME=~/.openclicky/codex-home │
   JSON-RPC: initialize → thread/start │
             (or thread/resume) →      │
             turn/start → turn/completed
        │                              │
        ▼                              ▼
  Codex CLI ──── POST /v1/responses ──▶ backend/ (Hono)  ── holds OPENAI_API_KEY ──▶ OpenAI
   model_provider = "openclicky"        │  requireAuth: Supabase JWT or session token
   env_key = OPENCLICKY_SESSION_TOKEN   │  POST /agent/session-token  (JWT → session token)
   model_instructions_file =           │  POST /v1/messages          ──▶ Anthropic
     skills/ModelInstructions.md       │
   [[skills.config]] path = skills/    ▼
                                    Supabase Auth (your project)
```

Keys never leave the backend. The agent process explicitly strips `OPENAI_API_KEY` /
`ANTHROPIC_API_KEY` from Codex's environment and only ever presents the user's token.

### Repository layout

| Path | What |
|---|---|
| `agent/` | CLI + Codex bridge (`src/codex.ts`), ask lane (`src/ask.ts`), JSON-RPC client, config rendering |
| `backend/` | Hono app (`src/app.ts`), auth (`src/auth.ts`), streaming proxies (`src/proxy.ts`), Node entry, `wrangler.toml` |
| `skills/` | `ModelInstructions.md` (the agent behavior contract) + 15 ported skills (`openclicky-*`, `doc`, `pdf`, …) |
| `config/codex-config.toml` | Codex config template rendered into the isolated `CODEX_HOME` on every run |
| `scripts/port-skills.mjs` | Regenerates `skills/` from `reference/` (`npm run port-skills`) |
| `reference/`, `REVERSE-ENGINEERING.md` | Reverse-engineering notes this implementation follows |
| `docs/superpowers/plans/` | The implementation plan for this cut |

## Prerequisites

- Node 22+
- Codex CLI: `npm i -g @openai/codex` (developed against 0.152.1)
- A Supabase project (for real auth) — or just its JWT secret for local development
- An OpenAI API key (and optionally an Anthropic key) — configured on the **backend only**

## Setup

```bash
npm install
npm run build          # builds backend/ and agent/
npm test               # unit tests + an integration test against the real codex binary (fake model)

cp backend/.dev.vars.example backend/.dev.vars   # fill in keys + secrets
cp .env.example .env                              # agent-side settings (BACKEND_URL, OPENCLICKY_TOKEN, …)
```

`backend/.dev.vars` needs at least `OPENAI_API_KEY`, `SESSION_TOKEN_SECRET`, and either
`SUPABASE_JWT_SECRET` (HS256, the "JWT Secret" in Supabase → Settings → API) or `SUPABASE_URL`
(for projects on asymmetric signing keys, verified via JWKS). See `.env.example` for every variable.

## Run the backend

```bash
npm run dev -w backend            # Node, http://localhost:8787 (reads backend/.dev.vars)
npm run dev:worker -w backend     # same app under `wrangler dev` (Cloudflare Workers runtime)
curl localhost:8787/health        # → {"ok":true}
```

Endpoints:

| Route | Auth | Purpose |
|---|---|---|
| `GET /health` | none | liveness |
| `POST /agent/session-token` | Supabase JWT | returns `{ token, expiresAt, sub }` — a short-lived HS256 session token |
| `POST /v1/chat/completions` | JWT or session token | OpenAI Chat Completions proxy (streams SSE). `model: "default"` → `OPENAI_MODEL` |
| `POST /v1/responses` | JWT or session token | OpenAI Responses proxy — what Codex uses (Codex ≥ 0.15x no longer supports `wire_api = "chat"`) |
| `POST /v1/messages` | JWT or session token | Anthropic Messages proxy (the cheap "gate" lane; not wired into the agent yet) |

Deploy to Cloudflare: `cd backend && npx wrangler deploy`, then `wrangler secret put` each secret.

## Run the agent

```bash
export BACKEND_URL=http://localhost:8787
export OPENCLICKY_TOKEN=<supabase jwt or session token>   # see "Auth flow"

# full agent run (spawns Codex on a thread, prints the final answer + generated file paths)
npx openclicky run "create a file called hello.txt containing 'hi'"
npx openclicky run "now add a second line" --thread <id-printed-by-the-previous-run>
npx openclicky run "what is on my screen?" --image ~/Desktop/shot.png

# quick answer, no agent spawn (one chat completion through the backend)
npx openclicky ask "say hello in 5 words"
```

Flags: `--backend-url`, `--token`, `--image`, `--cwd` (agent working directory), `--model`
(Codex model override, e.g. `gpt-5.6-luna`), `--json`, `--verbose`. The thread id and progress
milestones go to stderr; the answer and `artifacts:` list go to stdout. Non-zero exit on failure.

Codex state lives in an isolated `CODEX_HOME` (`~/.openclicky/codex-home`, override with
`OPENCLICKY_CODEX_HOME`), separate from your personal `~/.codex`. It is stable across runs so
`--thread <id>` can resume; `config.toml` inside it is regenerated from `config/codex-config.toml`
on every run. Approvals are auto-accepted in this headless cut (the sandbox is `workspace-write`).

## Auth flow

1. The user signs in to **your** Supabase project and gets a JWT (`access_token`).
   Helper: `npx openclicky token --email … --password …` (needs `SUPABASE_URL` + `SUPABASE_ANON_KEY`).
2. The client exchanges it: `POST /agent/session-token` with `Authorization: Bearer <jwt>` →
   `{ token, expiresAt }`. The session token is an HS256 JWT signed with `SESSION_TOKEN_SECRET`,
   `typ: openclicky-session`, default lifetime 1h.
3. The agent presents that token on every model call (`Authorization: Bearer <token>`); Codex
   attaches it automatically because the provider config sets `env_key = "OPENCLICKY_SESSION_TOKEN"`.
   The backend also accepts a raw Supabase JWT, so step 2 is optional for scripts.

Local development without a Supabase project: put any `SUPABASE_JWT_SECRET` in `backend/.dev.vars`
and mint a user JWT with `npm run mint-jwt -w backend` — that is exactly the token shape Supabase
issues under its legacy HS256 secret, so it exercises the real verification path.

## Skills and the behavior contract

`skills/ModelInstructions.md` is the ported HeyClicky agent contract (routing ladder, approval gate,
style, macOS permission-prompt-storm avoidance), rebranded. It is loaded by Codex through
`model_instructions_file`, and the 15 skills in `skills/` are exposed through `[[skills.config]]`.
Regenerate both from `reference/` with `npm run port-skills`. See `skills/ATTRIBUTION.md`.

## Next cut

- Voice: OpenAI Realtime client (PTT + always-on, barge-in, AEC), Deepgram/Whisper STT fallback, `/agent/realtime/*`.
- Native SwiftUI shell: notch HUD, floating session button, screenshot manager, document reader, permissions onboarding.
- Computer Use: embed cua-driver, expose the `computer-use` MCP server (config stub is in `config/codex-config.toml`).
- Integrations: Composio MCP + Settings → Integrations; Haiku gate lane in front of `run`.
- Interactive approvals instead of auto-accept; `/skills/*` library endpoints.

## Reference material

`REVERSE-ENGINEERING.md` is the master spec recovered from HeyClicky v1.0.48; `reference/` holds
the verbatim model instructions, the original Codex config, the bundled skills, and license notes.
Hermes skills are MIT (Nous Research); the `powerpoint` skill is proprietary and is not copied;
cua-driver is third-party — check its license before bundling.
