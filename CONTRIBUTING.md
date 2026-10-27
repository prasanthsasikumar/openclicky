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
| `linux/` | Python (GTK 3, X11) | Early Linux client: dictation, the pointer, asking about the screen by text, Settings. User guide: `docs/linux.md`; building and testing: `linux/README.md`. |
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

The app and the CLI both default to the hosted, invite-only backend
(`https://api.openclicky.flowsxr.com`), so pointing them at the one you just started is an explicit
override. A backend with no `SUPABASE_*` settings has no accounts and no metering: mint yourself a
dev token once and put it in `~/.openclicky/shell.json` as `token`, with `backendUrl` set to
`http://localhost:8787`:

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

## Add a skill for an app

A skill teaches OpenClicky one app: where things are, how the common tasks are done, what to point
at. It is one Markdown file, and it ships inside the next release once merged. Skills are only added
this way, as reviewed pull requests; the app has no skill editor.

1. Copy `app-skills/_template/` to `app-skills/<app-name>/` (lowercase, hyphens: `davinci-resolve`).
2. Fill in the frontmatter. Find the app's bundle id with `osascript -e 'id of app "App Name"'`; for a
   website, list its host (`mail.google.com`, not the whole `google.com`). Keep `surfaces: [talk]`.
3. Write the four sections in order (Layout, Common tasks, Pointing hints, Gotchas), spatially and
   concretely, under 3,500 characters. `app-skills/davinci-resolve/SKILL.md` is a good example;
   `app-skills/README.md` has the details.
4. Run `npm run check:skills`. It fails on a missing section, a body that is too long, a skill that
   is not talk-only, an app another skill already covers, and any link, code block or shell command:
   a skill describes the screen, it never sends people to a page or has them run something.
5. Try it: put the folder in `~/.openclicky/app-skills/` (or run the app from your checkout), open the
   app, hold ⌃⌥ and ask the questions people ask. Say in the pull request what you tried, and on which
   version of the app.

Not a writer? Open a **Request a skill** issue: name the app and the questions people ask, and someone
will write it.

## Changes people will notice

If a pull request changes what the app does for the person using it, add a bullet to `CHANGELOG.md`
under the next version (one line, the gist in bold, written for someone reading the update prompt).
That text is what the update dialog and the GitHub release show.

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
