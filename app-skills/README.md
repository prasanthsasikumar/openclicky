# App-teaching skills

Short teaching notes about one application or website. When the user talks to OpenClicky, the
app looks at what is in front (frontmost app bundle id, or the browser's current URL) and injects
the matching skill into the voice prompt so the answer and the pointing are specific to that tool.
These skills are not loaded by the Codex agent lane; they only shape talk replies.

## File format

One folder per skill, `app-skills/<id>/SKILL.md`, Hermes-style: YAML frontmatter + Markdown body.
Lists are written inline (`[a, b]`), never as YAML block lists.

```
---
name: Figma
description: Teaching notes for Figma — where the tools live, how to do the common tasks, what to point at.
apps: [com.figma.Desktop]
sites: [figma.com]
surfaces: [talk]
---

## Layout
…
## Common tasks
…
## Pointing hints
…
## Gotchas
…
```

- `name`, `description`: required.
- `apps`: bundle identifiers. Matching is an exact, case-sensitive string compare against the
  frontmost application's bundle id. Omit for site-only skills.
- `sites`: URL hosts. A site matches when the browser's current host is equal to the entry or ends
  with `"." + entry`. So `google.com` would match `mail.google.com`, `docs.google.com` and every other
  Google host — list the specific host you mean (`mail.google.com`) unless you really want the whole
  domain. Omit for apps with no web version. When a browser's URL is unavailable, its window title is
  searched for the site string as a fallback (browsers only; other apps match by bundle id).
- `integration`: the Composio toolkit slug (`gmail`, `youtube`, `github`, `slack`, `figma`, `googledocs`)
  when the app has an account worth connecting. Only skills with this key get the "Connect <app> to
  OpenClicky" card in the notch HUD; leave it out for plain apps (Terminal, Finder, Xcode…).
- `surfaces`: keep `[talk]` for app skills. (`agent` would expose the file to the Codex agent lane,
  which app-teaching notes are not written for.)
- Precedence: a matching site skill wins over the browser's own app skill, so a Gmail tab in Safari
  gets the Gmail notes rather than the Safari notes. Among several site matches the most specific
  site wins (the longest matching site string, so `mail.google.com` beats `google.com`); ties fall
  back to alphabetical folder-id order. For `apps`, the first skill in alphabetical folder order whose
  bundle id matches wins.

## Writing guidelines

The reader is a voice assistant that sees a screenshot and can fly a pointer to a coordinate, so
write spatially and concretely: "the right-hand inspector", "the toolbar at the top", "the sidebar
on the left". Sections, in this order:

- `## Layout` — where the main regions and controls are.
- `## Common tasks` — five to eight one-liners: what the user asks for, the menu path, the shortcut.
- `## Pointing hints` — for the frequent questions, which control or region to point at.
- `## Gotchas` — traps and version differences.

Budget: the app skill is injected with a cap of 4,000 characters, so keep the body under about
3,500 characters (200–400 words). Longer bodies are truncated at injection time.

## Adding one

1. Create `app-skills/<id>/SKILL.md` with the frontmatter above. Find a bundle id with
   `osascript -e 'id of app "Name"'` or `mdls -name kMDItemCFBundleIdentifier /Applications/Name.app`.
2. Run `npm run build` so the backend's skill manifest (`GET /skills/library`, `kind: "app"`) picks it up.
3. The Mac app reads this folder directly at run time (next voice turn), no rebuild needed.
