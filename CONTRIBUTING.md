# Contributing to OpenClicky

OpenClicky is MIT-licensed and meant to be hacked on. You do not need an account on the hosted
backend, a Supabase project, or anyone's permission: everything runs on your own machine with your
own model keys.

## What is in the box

| Part | Language | What it does |
|---|---|---|
| `backend/` | TypeScript (Hono) | The only thing that holds model keys. Proxies OpenAI / Anthropic / Realtime / speech, serves the skill library, meters credits (optional). Runs on Node or Cloudflare Workers. |
| `agent/` | TypeScript | The `openclicky` CLI: the Codex agent lane, the gate, ask, voice, skills. |
| `macos/OpenClicky/` | Swift | The Mac app: notch HUD, cursor buddy, push-to-talk, pointing, dictation. |
| `skills/`, `app-skills/` | Markdown | Skills the agent and the voice model read. |
| `docs/superpowers/plans/` | Markdown | The implementation plans behind each cut, if you want the reasoning. |

Read `README.md` for the architecture and `macos/OpenClicky/AGENTS.md` for the app's file map and
conventions. Both are kept current on purpose; if you change how something works, change the doc too.

## Setting up

Prerequisites: macOS 14.2+ (for the app), Xcode 16+, Node 22, and at least an OpenAI key. An
Anthropic or OpenRouter key adds the Claude lanes (see "Choosing a provider" in the README).

```bash
git clone https://github.com/prasanthsasikumar/openclicky && cd openclicky
npm ci
cp .env.example backend/.dev.vars     # fill in OPENAI_API_KEY (+ ANTHROPIC_API_KEY or the OpenRouter block)
npm run build -w agent
npm run dev -w backend                # http://localhost:8787
```

A backend with no `SUPABASE_*` settings has no accounts and no metering: mint yourself a dev token
once and put it in `~/.openclicky/shell.json` as `token`, with `backendUrl` set to `http://localhost:8787`:

```bash
npm run mint-jwt -w backend -- --sub you --email you@example.com
```

Then open `macos/OpenClicky/OpenClicky.xcodeproj`, set your signing team, and run. The same
`shell.json` also takes `openaiApiKey` if you would rather have the app carry your key per request
(bring your own key) than keep it on the backend.

## Running the tests

```bash
npm test                                  # backend (vitest) + agent (vitest)
cd macos/OpenClicky && xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky \
  -destination 'platform=macOS' -derivedDataPath build/DerivedData build-for-testing CODE_SIGNING_ALLOWED=NO \
  && xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' \
  -derivedDataPath build/DerivedData test-without-building -only-testing:OpenClickyTests CODE_SIGNING_ALLOWED=NO
```

Quit any running copy of OpenClicky before the Swift tests: the test host cannot attach while
another instance is up. CI runs the TypeScript tests on every pull request.

## Making changes

- Small, focused pull requests against `main`. Describe what changed and why; a screenshot or a
  short recording helps for anything visible in the HUD.
- Add or update tests with the change. Backend and agent code is unit-tested with vitest; the app
  has Swift Testing suites for the pure parts (shortcut recognition, flight planning, pointing math,
  skills). Live behaviour that cannot be unit-tested is fine to verify by hand; say so in the PR.
- Follow the naming rules in `macos/OpenClicky/AGENTS.md` (clear over concise, no one-letter names,
  comments say why). The same spirit applies to the TypeScript.
- Do not commit keys, tokens, or `shell.json`. `.env` and `backend/.dev.vars` are git-ignored;
  `.env.example` documents every variable.
- Commit messages: imperative mood, `feat(area): …` / `fix(area): …` / `docs: …`, explaining why.

## Where help is welcome

- Pointing accuracy on more apps and sites (`ScreenTextLocator`, `BrowserTabLocator`, app skills).
- New app-teaching skills under `app-skills/` (one Markdown file per app or site).
- Other transcription and speech providers behind the existing provider protocols.
- Windows or Linux clients against the same backend contract.
- Hosting recipes: the backend runs on Node, Docker (`backend/Dockerfile`), or Cloudflare Workers.

## Reporting problems

Open an issue with the app version (Settings shows it), macOS version, what you did, what happened,
and the relevant lines from `~/Library/Logs/OpenClicky/backend.log` (backend) or the app log. Never
paste tokens or keys.
