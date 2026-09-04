# Writing skills for OpenClicky

A skill is one Markdown file that steers OpenClicky: a writing style, a workflow, or teaching notes
for an app. This guide covers the file format, where each kind of skill lives, how the app picks
them, and how to check that one was actually used. `app-skills/README.md` has the extra rules for
app-teaching skills.

## The file

`SKILL.md`, Hermes-style: YAML frontmatter between `---` lines, then a Markdown body.

```
---
name: Reply In My Voice
description: Draft email replies the way I write them. Use when the user asks to answer or reply to an email.
surfaces: [talk, agent]
---

## Use when
…
## Rules
…
## Do not
…
```

Frontmatter keys:

| Key | Required | Meaning |
|---|---|---|
| `name` | yes | Short Title Case name; the folder id is derived from it (`reply-in-my-voice`) |
| `description` | yes | One sentence: what it does and when to use it. The agent routes on this |
| `surfaces` | no | Where it applies: `talk` (voice and teacher replies), `agent` (Codex runs). Default `[talk, agent]` |
| `apps` | no | App-teaching skills only: bundle identifiers, exact match |
| `sites` | no | App-teaching skills only: URL hosts; a host matches when it equals the entry or ends with `.` + entry |

Lists are written inline, `[a, b]`, never as YAML block lists. Values may be quoted. A `#` after
whitespace on an unquoted line starts a comment and is stripped. A file without frontmatter, or
without `name` and `description`, is ignored (the CLI, the backend and the app all use the same
parser rules: `agent/src/skillMarkdown.ts`, `backend/src/skillMarkdown.ts`, `SkillFile.swift`).

## Three places skills live

| Kind | Path | Loaded by |
|---|---|---|
| Built-in agent skills | `skills/<id>/SKILL.md` (repo) | Codex on every run, `[[skills.config]]` in `config/codex-config.toml`. Always on |
| App-teaching skills | `app-skills/<id>/SKILL.md` (repo), `surfaces: [talk]` | The Mac app, per voice turn, the one matching the frontmost app or site |
| User library | `~/.openclicky/skills/library/<id>/SKILL.md` | Activated ones only: Codex through `~/.openclicky/skills/active/`, and the voice prompts when `surfaces` includes `talk` |

The user library root can be moved with `OPENCLICKY_USER_SKILLS_DIR` (CLI and app read it). The app
finds `app-skills/` through the `appSkillsPath` key in `~/.openclicky/shell.json`, else next to the
CLI it runs (`…/agent/dist/cli.js` → repo root), else `~/.openclicky/app-skills`.

## How matching works (app-teaching skills)

At every push-to-talk key-down the app reads the frontmost application:

1. Bundle identifier and localized name (`NSWorkspace`).
2. For browsers (Safari, Chrome, Arc, Edge, Brave, Firefox) the front tab's URL through the
   Accessibility API, and the window title. This needs the Accessibility permission; without it the
   title is still available and the browser's own skill is used.

Then `AppSkillMatcher` picks one skill, in this order:

1. A `sites` entry matching the URL host: equal, or the host ends with `.` + entry. So `google.com`
   matches `mail.google.com` and `docs.google.com`, and `notgoogle.com` does not. Among several
   matches the most specific site wins (the longest matching entry), then alphabetical folder id.
2. A `sites` entry found in the lowercase window title (fallback when there is no URL).
3. An `apps` entry equal to the bundle identifier; first alphabetical folder wins.

A site skill therefore beats the browser's app skill: a Gmail tab in Safari gets the Gmail notes.

## Budgets

`SkillPromptBuilder` assembles the block that is appended to the Realtime instructions and to the
Claude teacher system prompt:

```
## Skill: <name>
<body>

## The app in front: <app name> (<bundle id>, <host>)
<body>
```

- Activated talk skills: whole skills only, up to 6,000 characters in total. A skill that would
  cross the budget is skipped (logged as `🧩 Skills: skipping …`).
- The app skill: up to 4,000 characters, truncated with `…`. Keep app-skill bodies under about
  3,500 characters (200 to 400 words).

Keep skills short. They ride along with every turn, and the Realtime session re-reads its
instructions each time they change.

## Adding an app-teaching skill

1. Create `app-skills/<id>/SKILL.md` with `name`, `description`, `apps` and/or `sites`, and
   `surfaces: [talk]`. Find a bundle id with `osascript -e 'id of app "Name"'`.
2. Write the four sections in this order: `## Layout`, `## Common tasks`, `## Pointing hints`,
   `## Gotchas`. Be spatial ("the right-hand inspector", "top-left of the toolbar"): the reader can
   see a screenshot and fly a pointer to a coordinate.
3. Run `npm run build` so `backend/src/skillsManifest.ts` (served by `GET /skills/library`, `kind:
   "app"`) includes it. The app reads the folder directly; the next voice turn uses it.

## Creating a user skill

Three ways, all producing the same files:

- **Notch HUD**: hover the notch, Home tab, type what the skill should do into "Create a skill" and
  press return. The app posts your line to `POST /skills/create`; the backend asks the model
  (`SKILL_CREATE_MODEL`, else `OPENAI_MODEL`) for a SKILL.md that only relies on the capabilities it
  is told about, validates it, and the app saves it to `library/<id>/` and activates it. Toggles in
  the same list activate and deactivate; the folder button opens `~/.openclicky/skills`.
- **CLI**: `openclicky skills create "reply to emails in my voice"` (prints the id), then
  `openclicky skills list | activate <id> | deactivate <id> | path`.
- **By hand**: drop a folder with a `SKILL.md` into `~/.openclicky/skills/library/` and activate it
  with the CLI or the HUD. The app watches the folder and reloads.

Ids are the kebab-case name; a second skill with the same name gets `-2`, `-3`.

## How activation reaches Codex

`~/.openclicky/skills/activations.json` holds `{ "active": [ids], "updatedAt": "…" }`. Both the CLI
and the app rewrite it atomically and then make `~/.openclicky/skills/active/` contain exactly one
symlink per activated skill that still exists (`active/<id>` → `library/<id>`); stale links are
removed. The agent's `ensureCodexHome` re-syncs that directory on every run and renders it into the
Codex config as a second skills path:

```toml
[[skills.config]]
path = "/Users/you/.openclicky/skills/active"
enabled = true
```

So an activated skill is visible to the agent on the next run without touching the repo's `skills/`.

## Checking what was injected

- Realtime lane: the app log (Console, or the terminal when running the binary) prints
  `🎙️ Realtime: instructions updated (N chars)` whenever the instructions change; N grows by the
  size of the injected block. `🧩 Skills: skipping …` means a talk skill did not fit the budget.
- Teacher lane (Claude): the same block is appended to the system prompt; there is no separate log
  line, but the reply references the app-specific notes.
- Agent lane: `ls -l ~/.openclicky/skills/active` shows what Codex will load;
  `openclicky skills list` shows `[on]` / `[off]`.
- Headless: `OpenClicky.app/Contents/MacOS/OpenClicky --openclicky-smoke-talk-file utterance.wav`
  runs one push-to-talk turn with the app in front and prints the instructions update, any
  `point_at` calls (`🎯 Element pointing …`) and the spoken reply.
