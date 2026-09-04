# Skills library, Realtime pointing, upstream watch — design (2026-09-04)

Approved in chat on 2026-09-04: library + create + app skills; local storage under
`~/.openclicky/skills`; multi-step `point_at` tool in the Realtime lane; daily GitHub Action that
opens an issue per new HeyClicky release.

## 1. What HeyClicky ships (research summary)

Sources: heyclicky.com/changelog (v1.0 → v1.0.48), the shipped v1.0.48 bundle recovered in
`REVERSE-ENGINEERING.md` §8–9, github.com/farzaa/clicky-releases (Sparkle appcast).

| Layer | Count | Where it lives upstream | OpenClicky today |
|---|---|---|---|
| Bundled agent skills (Hermes `SKILL.md`) | 15 (14 MIT + proprietary `powerpoint`) | app bundle `ClickyBundledSkills/`, Codex `[[skills.config]]` | ported to `skills/` (14) |
| App-teaching skills, injected by the program in front + browser-site matching (v1.0.26, v1.0.28) | 89 apps | server (`/programs`), text never shipped in the bundle | none |
| Community skill library: one-click activate from the notch, "Create a skill" (type what it should do), approval queue + email, "My Skills" filter, team-private sharing (v1.0.33, v1.0.34–36, v1.0.44) | ~100 | server `/skills/library`, `/skills/create`, `/skills/creations`, `/skills/activations/sync` | `GET /skills/library` serves the bundled manifest only |
| Vendored Hermes skills in the onboarding picker | 25 | app bundle, not wired | not vendored |

Pointing upstream: the original open-source Clicky pointed with a `[POINT:x,y:label:screenN]` tag
appended to the Claude reply (our teacher lane still does this). HeyClicky then added drawing
(circle/highlight, v1.0.25–26), hand-drawn Excalidraw-style shapes (v1.0.40), and step-by-step
walkthroughs up to 15 steps (v1.0.48). Our default push-to-talk path since v0.3.0 is OpenAI
Realtime, which receives the screenshot but has no way to point.

Update feeds: the changelog page has no RSS. Machine-readable: the Sparkle appcast
`https://farzaa.github.io/clicky-releases/appcast.xml` (version, build, date, DMG URL) and the
GitHub releases of `farzaa/clicky-releases` (Atom: `/releases.atom`). Human feeds: x.com/heyclicky.

## 2. Skills

### 2.1 Three sources, one format

All skills are Hermes-style `SKILL.md`: YAML frontmatter + Markdown body. Frontmatter keys used by
OpenClicky (all optional except `name`, `description`):

```yaml
---
name: figma
description: Teaching notes for Figma — where the tools live, common tasks.
apps: [com.figma.Desktop]          # bundle identifiers (app skills only)
sites: [figma.com]                 # browser host suffixes (app skills only)
surfaces: [talk, agent]            # default [talk, agent]; talk = injected into voice prompts
---
```

| Source | Path | Who edits | Agent (Codex) | Talk (Realtime + Claude teacher) |
|---|---|---|---|---|
| Built-in agent skills | `skills/` (repo) | maintainers | always on (`[[skills.config]]`) | not injected (they are routing docs for the agent) |
| App-teaching skills | `app-skills/<id>/SKILL.md` (repo) | maintainers, contributors | not loaded | the one matching the frontmost app / site is injected per turn |
| User library | `~/.openclicky/skills/library/<id>/SKILL.md` | the user (HUD "Create a skill", or drop a folder in) | activated ones only, via `~/.openclicky/skills/active/` | activated ones with `talk` in `surfaces`, capped |

Skills stay separate from app code: nothing is compiled into the binary or the backend beyond the
existing generated manifest; the app and the CLI read the directories at run time.

### 2.2 Activation model

`~/.openclicky/skills/activations.json`:

```json
{ "active": ["write-like-me", "yc-advice"], "updatedAt": "2026-09-04T10:00:00Z" }
```

`agent/src/skillsLibrary.ts` (new) is the single owner of the on-disk layout:

- `listLibrary(home)` → entries `{ id, name, description, surfaces, path, active }` from `library/`.
- `setActive(home, id, on)` → rewrites `activations.json`.
- `syncActiveDir(home)` → makes `active/` contain exactly one symlink per active id (removes stale
  ones). Called by `ensureCodexHome` on every agent run so Codex always sees the current set, and by
  the CLI `skills` commands.
- `createSkill(home, markdown)` → validates frontmatter, derives `id` (kebab-case of `name`, de-duped),
  writes `library/<id>/SKILL.md`, activates it.

`config/codex-config.toml` gains a second block:

```toml
[[skills.config]]
path = "{{USER_SKILLS_ACTIVE}}"
enabled = true
```

rendered by `codexHome.ts` from `~/.openclicky/skills/active` (created empty if missing so Codex
never sees a dangling path).

CLI: `openclicky skills list|activate <id>|deactivate <id>|create "<what it should do>"` (create calls
the backend, prints the id). This is the headless verification path.

### 2.3 Creating a skill

`POST /skills/create` (backend, auth required):

```json
{ "request": "reply to emails in my voice: short, warm, no exclamation marks",
  "capabilities": ["gmail", "computer-use"] }
→ { "id": "reply-in-my-voice", "name": "…", "description": "…", "markdown": "---\nname: …" }
```

The backend calls the chat model (same `OPENAI_BASE_URL`/`OPENAI_API_KEY` as the `ask` lane, model
`SKILL_CREATE_MODEL` defaulting to `ASK_MODEL`) with a fixed system prompt: produce one `SKILL.md`
in the Hermes format, "capability-aware" (only reference the capabilities listed in the request:
built-in skill names from the manifest plus integrations the client reports), body ≤ 1,500 words,
`surfaces` chosen by intent (a writing style → `[talk, agent]`, an operational workflow → `[agent]`).
The response is validated (frontmatter present, `name`/`description` non-empty) before returning.
No approval queue, no email, no team sharing in this cut.

### 2.4 App-teaching skills

`app-skills/` seeds 12 apps chosen for the demo use-cases: Finder, Safari, Google Chrome, Xcode,
VS Code, Terminal, Figma, Slack, Mail, Notes, Preview, System Settings, plus site entries for
gmail.com, docs.google.com, github.com, youtube.com (sites live inside the browser skills'
frontmatter or in their own folders; a folder may list only `sites`). Each body is 200–400 words:
where the main controls are, the 5–8 tasks people ask about, keyboard shortcuts, and pointing hints
("the Inspector is the right-hand panel"). `app-skills/README.md` documents the format and how to
add one; `npm run build` regenerates the manifest, which now includes `kind: "app"` entries with
`apps` and `sites`.

Matching (`AppSkillMatcher.swift`, pure function + tests):

1. `NSWorkspace.shared.frontmostApplication` → bundle id, name.
2. If the bundle id is a known browser (Safari, Chrome, Arc, Edge, Brave, Firefox), read the front
   window's document URL through the Accessibility API (`AXDocument` on the window for Safari,
   `AXURL` on the web area for Chromium) and match `sites` by host suffix. Fallback: the window
   title. If Accessibility is not granted, fall back to the browser's own app skill.
3. First match wins; a skill listing both `apps` and `sites` matches on either.

### 2.5 Injection into the talk lanes

`SkillPromptBuilder` (Swift) assembles, per turn:

```
<active skills>
## Skill: <name>
<body>
…
<app skill>
## The app in front: <name> (<bundle id> / <host>)
<body>
```

Budget: app skill ≤ 4,000 chars, active talk skills ≤ 6,000 chars total (oldest activation dropped
first, logged). The Realtime client gets an `instructionsProvider` closure; before each turn
(`endPushToTalk` tail, and `input_audio_buffer.committed` in always-on) it builds the instructions
and sends `session.update` only if the text changed. The Claude teacher lane appends the same block
to its system prompt. The Codex agent lane needs nothing: active skills are on disk.

### 2.6 HUD

Home tab "Add skills" becomes a real list: built-in app skills are summarized as one line ("12 app
skills, auto"), the user library shows each skill with a toggle, and a "Create a skill" field
(one line + return) calls `POST /skills/create` through the app and activates the result, showing
the new name. Errors show inline. Data model: `SkillLibraryStore` (ObservableObject) that reads the
same files `skillsLibrary.ts` writes, watches the directory with `DispatchSource`, and writes
`activations.json` directly (no CLI round-trip for toggles). Skill creation goes through the backend
directly with the shell token.

## 3. Pointing in the Realtime lane

Add a second function tool to the Realtime session:

```json
{ "type": "function", "name": "point_at",
  "description": "Fly the on-screen cursor to a UI element in the attached screenshot and show a label. Call it while explaining each step; call it again for the next step.",
  "parameters": { "type": "object",
    "properties": { "x": {"type":"integer"}, "y": {"type":"integer"},
                    "label": {"type":"string","description":"1-3 words, e.g. 'export button'"} },
    "required": ["x","y","label"] } }
```

Coordinates are pixels in the last attached screenshot (top-left origin), as the caption already
states. Instructions gain: "Every request has a screenshot. When the user asks how to do something
or where something is, point at it with point_at while you explain, one call per step, in order.
Do not point for general questions."

Client side: `screenContextProvider` returns the `CompanionScreenCapture` it used (plus caption and
JPEG), and the client keeps it as `lastScreenCapture`. `handleToolCall` for `point_at` calls
`onPointAt(x, y, label)` and returns `"pointing at <label>"`. `CompanionManager.pointAt(screenshotPoint:label:in:)`
is extracted from the existing teacher-lane code (screenshot px → display points → AppKit global,
clamp, `detectedElementBubbleText = label`, `launchDockedCursorForPointing`,
`detectedElementScreenLocation = …`). The overlay retargets when the location changes while it is
already pointing (`onChange` handler already reacts; the flight restarts from the current buddy
position). A tool call that arrives during playback is honoured immediately so the point lands
while the sentence is spoken. The tool result → `response.create` continuation is the existing
path, which is what lets the model continue with "then …" + the next `point_at`.

No drawing/circling in this cut.

## 4. Upstream watch

`scripts/upstream-watch.mjs`:

- Fetches the appcast (versions, build numbers, dates, DMG URLs) and `heyclicky.com/changelog`
  (per-version sections, extracted by the version headings; HTML → text).
- State file `reference/upstream/heyclicky-versions.json` (`{ "seen": { "1.0.48": {build, date, url} } }`)
  and a human log `reference/upstream/heyclicky-changelog.md` (one section per version, newest
  first; seeded from the full current changelog on first run).
- `node scripts/upstream-watch.mjs` prints new versions; `--write` updates both files; `--issues`
  creates one GitHub issue per new version (`gh issue create`, label `upstream`, title
  `HeyClicky v1.0.49 released`, body = changelog section + DMG link + "port checklist" template).
- `npm run upstream:check` = `--write`.

`.github/workflows/upstream-watch.yml`: daily cron (and manual dispatch), Node 22, runs the script
with `--write --issues`, commits state changes with `[skip ci]`. Needs `issues: write`,
`contents: write`. Also documented: press Watch → Custom → Releases on `farzaa/clicky-releases`
for email.

## 5. Files

New: `agent/src/skillsLibrary.ts` (+ test), `backend/src/skillsCreate.ts` (+ test),
`app-skills/**`, `macos/OpenClicky/OpenClicky/SkillLibraryStore.swift`, `AppSkillMatcher.swift`,
`SkillPromptBuilder.swift`, tests in `OpenClickyTests`, `scripts/upstream-watch.mjs`,
`reference/upstream/*`, `.github/workflows/upstream-watch.yml`, `docs/skills.md`.

Changed: `config/codex-config.toml`, `agent/src/codexHome.ts`, `agent/src/cli.ts`,
`backend/src/app.ts`, `backend/scripts/build-skills-manifest.mjs`, `RealtimeVoiceClient.swift`,
`CompanionManager.swift`, `CompanionScreenCaptureUtility.swift`, `NotchHUDPanels.swift`,
`README.md`, `macos/OpenClicky/OPENCLICKY.md`, `package.json`.

## 6. Testing

- vitest: `skillsLibrary` (list/activate/sync/create on a temp home), `codexHome` renders the second
  skills block, `/skills/create` against a fake upstream (valid markdown, invalid markdown → 502,
  missing key → 503), `upstream-watch` parsers on fixture appcast + changelog HTML.
- Swift unit tests: SKILL.md frontmatter parsing, app/site matching, prompt budget, screenshot →
  screen point mapping.
- Headless: `openclicky skills create "…"` then `openclicky skills list`; app
  `--openclicky-smoke-talk-file` with an utterance like "how do i open a new tab" on a Safari screen
  logs a `point_at` call; `xcodebuild … build` and `test`.
- Manual (user): hold ctrl+option in Figma and ask where the export button is; the buddy flies there.
