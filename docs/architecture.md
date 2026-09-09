# How OpenClicky works

The pieces, how a request travels through them, where the skills live, and how upstream HeyClicky
releases are followed.

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

## Reference material

`REVERSE-ENGINEERING.md` is the master spec recovered from HeyClicky v1.0.48; `reference/` holds
the verbatim model instructions, the original Codex config, the bundled skills, and license notes.
Hermes skills are MIT (Nous Research); the `powerpoint` skill is proprietary and is not copied;
cua-driver is third-party — check its license before bundling.

