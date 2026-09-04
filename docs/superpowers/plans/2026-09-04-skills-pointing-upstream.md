# Skills library, Realtime pointing, upstream watch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give OpenClicky HeyClicky's three skill layers (bundled, per-app teaching, user library with one-click activate and "Create a skill"), let the Realtime voice lane point at things on screen step by step, and watch HeyClicky releases automatically.

**Architecture:** Skills are Hermes-format `SKILL.md` files read at run time from `skills/` (agent, always on), `app-skills/` (talk, matched to the frontmost app) and `~/.openclicky/skills/library` (user, activated via `activations.json` → symlinks in `active/` that Codex loads). The Swift app injects matching skills into the Realtime and Claude prompts per turn and exposes a `point_at` function tool to gpt-realtime that reuses the existing buddy flight. A Node script + GitHub Action polls the HeyClicky appcast and changelog and opens issues.

**Tech Stack:** TypeScript (Node 22, vitest, Hono), Swift 5.9 / SwiftUI / AppKit / Accessibility API, OpenAI Realtime API function tools, GitHub Actions + `gh`.

**Spec:** `docs/superpowers/specs/2026-09-04-skills-pointing-upstream-design.md`

## Global Constraints

- Skill format: YAML frontmatter (`name`, `description`, optional `apps`, `sites`, `surfaces`) + Markdown body; lists written inline `[a, b]`.
- User data root: `~/.openclicky/skills/{library,active,activations.json}`; never write skills into the repo from the app.
- Keys never leave the backend; the app calls `POST /skills/create` with the shell token.
- Talk injection budgets: app skill ≤ 4,000 chars, active talk skills ≤ 6,000 chars total.
- Never call AVAudioEngine from `@MainActor` code (see `RealtimeAudioEngine`).
- Commit after every task; commit trailer per the session's attribution rule.
- Verification: `npm test` (both workspaces), `xcodebuild -project macos/OpenClicky/OpenClicky.xcodeproj -scheme OpenClicky build CODE_SIGNING_ALLOWED=NO`, Swift tests via `xcodebuild test` when signing allows, headless CLI checks.

---

### Task 1: `skillsLibrary.ts` — on-disk user library, activation, `active/` sync, CLI

**Files:**
- Create: `agent/src/skillMarkdown.ts`, `agent/src/skillsLibrary.ts`
- Modify: `agent/src/config.ts` (add `userSkillsDir`), `agent/src/codexHome.ts`, `config/codex-config.toml`, `agent/src/cli.ts`
- Test: `agent/test/skillsLibrary.test.ts`, `agent/test/codexHome.test.ts`

**Interfaces:**
- Produces `parseSkillMarkdown(md: string): { name, description, apps: string[], sites: string[], surfaces: string[], body: string } | null`
- Produces `listLibrary(dir)`, `readActivations(dir)`, `setActive(dir, id, on)`, `syncActiveDir(dir)`, `createSkillFiles(dir, markdown)`, `slugify(name)`
- `AgentConfig.userSkillsDir` (default `~/.openclicky/skills`), rendered into the template as `{{USER_SKILLS_ACTIVE}}`.

- [ ] **Step 1: Failing tests**

```ts
// agent/test/skillsLibrary.test.ts
import { describe, it, expect } from "vitest";
import fs from "node:fs"; import os from "node:os"; import path from "node:path";
import { parseSkillMarkdown } from "../src/skillMarkdown.js";
import { listLibrary, setActive, syncActiveDir, createSkillFiles, readActivations, slugify } from "../src/skillsLibrary.js";

const md = (name: string, extra = "") => `---\nname: ${name}\ndescription: d ${name}\n${extra}---\n\n# ${name}\nbody\n`;
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "oc-skills-"));

describe("parseSkillMarkdown", () => {
  it("parses frontmatter with inline lists", () => {
    const s = parseSkillMarkdown(md("figma", "apps: [com.figma.Desktop, \"com.figma.Beta\"]\nsites: [figma.com]\nsurfaces: [talk]\n"))!;
    expect(s.name).toBe("figma"); expect(s.apps).toEqual(["com.figma.Desktop", "com.figma.Beta"]);
    expect(s.sites).toEqual(["figma.com"]); expect(s.surfaces).toEqual(["talk"]); expect(s.body).toContain("# figma");
  });
  it("defaults surfaces to talk+agent and rejects missing frontmatter", () => {
    expect(parseSkillMarkdown(md("x"))!.surfaces).toEqual(["talk", "agent"]);
    expect(parseSkillMarkdown("# no frontmatter")).toBeNull();
    expect(parseSkillMarkdown("---\nname: x\n---\n")).toBeNull(); // description required
  });
});

describe("skillsLibrary", () => {
  it("creates, lists, activates and syncs symlinks", () => {
    const dir = tmp();
    const { id } = createSkillFiles(dir, md("Write Like Me"));
    expect(id).toBe("write-like-me");
    expect(fs.existsSync(path.join(dir, "library", id, "SKILL.md"))).toBe(true);
    expect(listLibrary(dir)).toMatchObject([{ id, name: "Write Like Me", active: true }]);
    expect(fs.lstatSync(path.join(dir, "active", id)).isSymbolicLink()).toBe(true);
    setActive(dir, id, false);
    expect(readActivations(dir).active).toEqual([]);
    expect(fs.existsSync(path.join(dir, "active", id))).toBe(false);
  });
  it("dedupes ids and drops stale links on sync", () => {
    const dir = tmp();
    expect(createSkillFiles(dir, md("Same")).id).toBe("same");
    expect(createSkillFiles(dir, md("Same")).id).toBe("same-2");
    fs.symlinkSync("/nowhere", path.join(dir, "active", "ghost"));
    syncActiveDir(dir);
    expect(fs.existsSync(path.join(dir, "active", "ghost"))).toBe(false);
    expect(slugify("  Hello, World! ")).toBe("hello-world");
  });
  it("ignores activations of skills that no longer exist", () => {
    const dir = tmp();
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, "activations.json"), JSON.stringify({ active: ["gone"] }));
    expect(syncActiveDir(dir)).toEqual([]);
  });
});
```

Add to `agent/test/codexHome.test.ts` `ensureCodexHome` block:

```ts
  it("renders the user skills active dir and creates it", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const userSkillsDir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-user-skills-"));
    const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: "/tmp/ws", userSkillsDir });
    const text = fs.readFileSync(ensureCodexHome(cfg).configPath, "utf8");
    expect(text).toContain(`path = "${path.join(userSkillsDir, "active")}"`);
    expect(fs.existsSync(path.join(userSkillsDir, "active"))).toBe(true);
  });
```

- [ ] **Step 2: Run** `npm test -w agent -- skillsLibrary codexHome` → FAIL (module not found).

- [ ] **Step 3: Implement**

`agent/src/skillMarkdown.ts`:

```ts
export interface ParsedSkill { name: string; description: string; apps: string[]; sites: string[]; surfaces: string[]; body: string; }

function parseList(v: string): string[] {
  const inner = v.trim().replace(/^\[/, "").replace(/\]$/, "");
  return inner.split(",").map((s) => s.trim().replace(/^["']|["']$/g, "")).filter(Boolean);
}

/** Hermes-style SKILL.md: `---` YAML frontmatter (flat keys, inline lists) + Markdown body. */
export function parseSkillMarkdown(md: string): ParsedSkill | null {
  const m = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/.exec(md.replace(/^﻿/, ""));
  if (!m) return null;
  const fm: Record<string, string> = {};
  for (const line of m[1].split(/\r?\n/)) {
    const kv = /^([A-Za-z_][\w-]*):\s*(.*)$/.exec(line);
    if (kv) fm[kv[1]] = kv[2].trim().replace(/^(["'])(.*)\1$/, "$2");
  }
  if (!fm.name || !fm.description) return null;
  const surfaces = fm.surfaces ? parseList(fm.surfaces) : ["talk", "agent"];
  return { name: fm.name, description: fm.description, apps: fm.apps ? parseList(fm.apps) : [], sites: fm.sites ? parseList(fm.sites) : [], surfaces, body: m[2].trim() };
}
```

`agent/src/skillsLibrary.ts`:

```ts
import fs from "node:fs"; import path from "node:path";
import { parseSkillMarkdown, type ParsedSkill } from "./skillMarkdown.js";

export interface LibrarySkill extends ParsedSkill { id: string; path: string; active: boolean; }
export interface Activations { active: string[]; updatedAt?: string; }

export const libraryDir = (dir: string) => path.join(dir, "library");
export const activeDir = (dir: string) => path.join(dir, "active");
const activationsPath = (dir: string) => path.join(dir, "activations.json");

export const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "skill";

export function readActivations(dir: string): Activations {
  try { const j = JSON.parse(fs.readFileSync(activationsPath(dir), "utf8")); return { active: Array.isArray(j.active) ? j.active.filter((x: unknown) => typeof x === "string") : [], updatedAt: j.updatedAt }; }
  catch { return { active: [] }; }
}
function writeActivations(dir: string, active: string[]) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(activationsPath(dir), JSON.stringify({ active: [...new Set(active)], updatedAt: new Date().toISOString() }, null, 2) + "\n");
}

export function listLibrary(dir: string): LibrarySkill[] {
  const lib = libraryDir(dir); if (!fs.existsSync(lib)) return [];
  const active = new Set(readActivations(dir).active); const out: LibrarySkill[] = [];
  for (const e of fs.readdirSync(lib, { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    const file = path.join(lib, e.name, "SKILL.md"); if (!fs.existsSync(file)) continue;
    const parsed = parseSkillMarkdown(fs.readFileSync(file, "utf8")); if (!parsed) continue;
    out.push({ ...parsed, id: e.name, path: path.join(lib, e.name), active: active.has(e.name) });
  }
  return out.sort((a, b) => a.id.localeCompare(b.id));
}

/** Make active/ hold exactly one symlink per activated, existing skill. Returns the active ids. */
export function syncActiveDir(dir: string): string[] {
  const act = activeDir(dir); fs.mkdirSync(act, { recursive: true });
  const existing = new Set(listLibrary(dir).map((s) => s.id));
  const wanted = readActivations(dir).active.filter((id) => existing.has(id));
  for (const e of fs.readdirSync(act)) if (!wanted.includes(e)) fs.rmSync(path.join(act, e), { recursive: true, force: true });
  for (const id of wanted) { const link = path.join(act, id); if (!fs.existsSync(link)) { fs.rmSync(link, { force: true }); fs.symlinkSync(path.join(libraryDir(dir), id), link); } }
  return wanted;
}

export function setActive(dir: string, id: string, on: boolean): string[] {
  const cur = readActivations(dir).active.filter((x) => x !== id);
  writeActivations(dir, on ? [...cur, id] : cur);
  return syncActiveDir(dir);
}

/** Validate + write library/<id>/SKILL.md and activate it. Throws on invalid markdown. */
export function createSkillFiles(dir: string, markdown: string): LibrarySkill {
  const parsed = parseSkillMarkdown(markdown); if (!parsed) throw new Error("SKILL.md needs frontmatter with name and description");
  const base = slugify(parsed.name); let id = base; let n = 2;
  while (fs.existsSync(path.join(libraryDir(dir), id))) id = `${base}-${n++}`;
  const skillPath = path.join(libraryDir(dir), id); fs.mkdirSync(skillPath, { recursive: true });
  fs.writeFileSync(path.join(skillPath, "SKILL.md"), markdown.endsWith("\n") ? markdown : markdown + "\n");
  setActive(dir, id, true);
  return { ...parsed, id, path: skillPath, active: true };
}
```

`config.ts`: add `userSkillsDir: string` (`flags.userSkillsDir ?? env.OPENCLICKY_USER_SKILLS_DIR ?? path.join(home, ".openclicky", "skills")`). `codexHome.ts`: `RenderVars.userSkillsActive: string`, `.replaceAll("{{USER_SKILLS_ACTIVE}}", v.userSkillsActive)`; in `ensureCodexHome` call `syncActiveDir(cfg.userSkillsDir)` before rendering with `userSkillsActive: activeDir(cfg.userSkillsDir)`. Template: after the existing `[[skills.config]]` block add

```toml
# User library (HUD "Add skills" / `openclicky skills`): symlinks to activated skills only.
[[skills.config]]
path = "{{USER_SKILLS_ACTIVE}}"
enabled = true
```

`cli.ts`: `skills` command group — `list [--json]`, `activate <id>`, `deactivate <id>`, `create <request> [--capability <name>...]` (POST `${backendUrl}/skills/create` with bearer token, body `{request, capabilities}`; on 2xx `createSkillFiles(cfg.userSkillsDir, json.markdown)`; print `id`). Also `path` prints the library dir.

- [ ] **Step 4: Run** `npm test -w agent` → PASS. `npm run build -w agent`.
- [ ] **Step 5: Commit** `feat(agent): user skill library with activation + Codex active dir; openclicky skills CLI`.

---

### Task 2: Backend `POST /skills/create` + manifest with app skills

**Files:**
- Create: `backend/src/skillMarkdown.ts` (copy of agent's), `backend/src/skillsCreate.ts`
- Modify: `backend/src/app.ts`, `backend/src/env.ts` (`SKILL_CREATE_MODEL?`), `backend/scripts/build-skills-manifest.mjs`, `.env.example`/`backend/.dev.vars.example` (document `SKILL_CREATE_MODEL`)
- Test: `backend/test/app.test.ts`

**Interfaces:** request `{ request: string; capabilities?: string[] }` → `200 { id, name, description, markdown }`; `400` bad body; `503` no OpenAI key/model; `502` model produced invalid markdown.

- [ ] **Step 1: Failing tests** — in the fake upstream add: when `req.url.endsWith("/chat/completions") && b.includes('"stream":false')` respond JSON `{ choices: [{ message: { content: MOCK_SKILL } }] }` where `MOCK_SKILL` is a fenced SKILL.md (` ```markdown\n---\nname: Reply In My Voice\ndescription: …\nsurfaces: [talk, agent]\n---\n# Reply…\n``` `). If the request text contains "BROKEN" return `content: "no frontmatter"`. Tests:

```ts
  it("creates a skill from a one-line request", async () => {
    const r = await call("/skills/create", json({ request: "reply to emails in my voice", capabilities: ["gmail"] }, await jwt()));
    expect(r.status).toBe(200);
    const j = await r.json();
    expect(j).toMatchObject({ id: "reply-in-my-voice", name: "Reply In My Voice" });
    expect(j.markdown.startsWith("---\n")).toBe(true);
    const sent = seen.at(-1)!;
    expect(sent.body.stream).toBe(false);
    expect(JSON.stringify(sent.body.messages)).toContain("gmail");
  });
  it("rejects bad bodies and invalid model output", async () => {
    expect((await call("/skills/create", json({}, await jwt()))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "BROKEN" }, await jwt()))).status).toBe(502);
    expect((await call("/skills/create", json({ request: "x" }, await jwt()), { OPENAI_API_KEY: "" })).status).toBe(503);
  });
  it("serves app skills in the library manifest", async () => {
    const r = await call("/skills/library", { headers: { authorization: `Bearer ${await jwt()}` } });
    const { skills } = await r.json();
    expect(skills.some((s: any) => s.kind === "app" && s.apps?.length)).toBe(true);
  });
```

- [ ] **Step 2: Run** `npm test -w backend` → FAIL.
- [ ] **Step 3: Implement** `skillsCreate.ts`:

```ts
const SYSTEM = `You write skills for OpenClicky, a Mac voice assistant. A skill is ONE Markdown file in this exact format:
---
name: <Short Title Case name>
description: <one sentence: what it does and when to use it>
surfaces: [talk, agent]   # talk = applies when the user is chatting/asking; agent = applies when the agent does work. Use [talk] for styles/knowledge, [agent] for operational workflows, both when unsure.
---
<body: ≤ 600 words. Headings: Use When, Steps/Rules, Voice or Style (if relevant), Do Not.>
Only rely on capabilities from this list: {{CAPS}}. Never invent tools or integrations. Output the file only, no commentary.`;

export async function createSkill(c: Context): Promise<Response> {
  const env = getEnv(c);
  const body = await c.req.json().catch(() => ({})) as { request?: unknown; capabilities?: unknown };
  if (typeof body.request !== "string" || !body.request.trim()) return c.json({ error: "request (string) is required" }, 400);
  const model = env.SKILL_CREATE_MODEL || env.OPENAI_MODEL;
  if (!env.OPENAI_API_KEY || !model) return c.json({ error: "skill creation needs OPENAI_API_KEY and OPENAI_MODEL (or SKILL_CREATE_MODEL) on the backend" }, 503);
  const caps = Array.isArray(body.capabilities) ? body.capabilities.filter((x): x is string => typeof x === "string") : [];
  const capList = [...SKILLS_MANIFEST.filter((s) => s.kind !== "app").map((s) => s.id), ...caps].join(", ") || "none";
  const base = (env.OPENAI_BASE_URL ?? "https://api.openai.com/v1").replace(/\/+$/, "");
  const upstream = await fetch(base + "/chat/completions", { method: "POST", headers: { authorization: `Bearer ${env.OPENAI_API_KEY}`, "content-type": "application/json" },
    body: JSON.stringify({ model: mapModelName(model, env.MODEL_ALIASES, env.OPENAI_MODEL_PREFIX), stream: false, messages: [{ role: "system", content: SYSTEM.replace("{{CAPS}}", capList) }, { role: "user", content: `Skill request: ${body.request.trim()}` }] }) });
  if (!upstream.ok) return c.json({ error: `upstream ${upstream.status}` }, 502);
  const j = await upstream.json() as { choices?: { message?: { content?: string } }[] };
  const raw = (j.choices?.[0]?.message?.content ?? "").trim();
  const markdown = raw.replace(/^```[a-z]*\n?/i, "").replace(/\n?```\s*$/, "").trim() + "\n";
  const parsed = parseSkillMarkdown(markdown);
  if (!parsed) return c.json({ error: "the model did not return a valid SKILL.md" }, 502);
  return c.json({ id: slugify(parsed.name), name: parsed.name, description: parsed.description, markdown });
}
```

(`slugify` lives in `backend/src/skillMarkdown.ts` too.) Route: `app.post("/skills/create", (c) => createSkill(c));` under the existing `/skills/*` auth. Manifest script: scan `app-skills/` as well, parse `apps`/`sites` inline lists, emit `kind: "app"`, `apps`, `sites` (both optional in the interface).

- [ ] **Step 4:** `npm test -w backend` → PASS (needs Task 3's `app-skills/` for the manifest test; create one seed skill here if running before Task 3).
- [ ] **Step 5: Commit** `feat(backend): POST /skills/create drafts a SKILL.md; manifest includes app skills`.

---

### Task 3: `app-skills/` seed set

**Files:**
- Create: `app-skills/README.md`, and `app-skills/<id>/SKILL.md` for: `finder` (com.apple.finder), `safari` (com.apple.Safari), `google-chrome` (com.google.Chrome), `xcode` (com.apple.dt.Xcode), `vscode` (com.microsoft.VSCode), `terminal` (com.apple.Terminal, com.googlecode.iterm2), `figma` (com.figma.Desktop; sites figma.com), `slack` (com.tinyspeck.slackmacGap; sites app.slack.com), `mail` (com.apple.mail), `notes` (com.apple.Notes), `preview` (com.apple.Preview), `system-settings` (com.apple.systempreferences), `gmail` (sites mail.google.com), `google-docs` (sites docs.google.com), `github` (sites github.com), `youtube` (sites youtube.com).

Each SKILL.md: frontmatter (`name`, `description`, `apps`/`sites`, `surfaces: [talk]`), body 200–400 words with sections **Layout** (where the main regions/controls are, in pointing-friendly terms: "top-left", "right-hand inspector"), **Common tasks** (5–8, each one line: menu path + shortcut), **Pointing hints** (what to point at for the frequent questions), **Gotchas**. README documents the frontmatter, matching rules (bundle id, site host suffix), budgets, and "add a folder, run `npm run build`".

- [ ] **Step 1:** write the files. **Step 2:** `npm run build -w backend` regenerates the manifest; `npm test -w backend` passes the app-skill manifest test. **Step 3: Commit** `feat(skills): 16 app-teaching skills injected by frontmost app/site`.

---

### Task 4: Upstream watch script, fixtures, workflow

**Files:**
- Create: `scripts/upstream-watch.mjs`, `agent/test/fixtures/appcast.xml`, `agent/test/fixtures/changelog.html` (trimmed real pages: 2 entries), `agent/test/upstreamWatch.test.ts`, `reference/upstream/heyclicky-versions.json`, `reference/upstream/heyclicky-changelog.md`, `.github/workflows/upstream-watch.yml`
- Modify: `package.json` (`"upstream:check": "node scripts/upstream-watch.mjs --write"`)

**Interfaces:** `parseAppcast(xml) → [{version, build, date, url}]`, `parseChangelog(html) → [{version, date, title, intro, groups: [{head, items: [{lead, text}]}]}]`, `renderEntry(entry, appcastItem?) → markdown`, `diff(state, appcast, changelog) → newVersions[]`.

- [ ] **Step 1: Failing tests**

```ts
import { describe, it, expect } from "vitest";
import fs from "node:fs"; import path from "node:path";
import { parseAppcast, parseChangelog, renderEntry, findNew } from "../../scripts/upstream-watch.mjs";
const fx = (n: string) => fs.readFileSync(path.join(__dirname, "fixtures", n), "utf8");

describe("upstream-watch", () => {
  it("parses the Sparkle appcast", () => {
    const items = parseAppcast(fx("appcast.xml"));
    expect(items[0]).toMatchObject({ version: "1.0.48", build: "57", url: expect.stringContaining("v1.0.48/HeyClicky.dmg") });
  });
  it("parses changelog entries with groups and items", () => {
    const entries = parseChangelog(fx("changelog.html"));
    expect(entries[0].version).toBe("1.0.48");
    expect(entries[0].title).toBe("Walkthroughs go the distance");
    expect(entries[0].groups[0].head).toBe("new");
    expect(entries[0].groups[0].items[0].lead).toBe("Always approve for agents");
    expect(entries[0].groups[0].items[0].text).toContain('"Always approve"'); // entities decoded
  });
  it("finds versions not in state and renders markdown", () => {
    const entries = parseChangelog(fx("changelog.html")); const items = parseAppcast(fx("appcast.xml"));
    expect(findNew({ seen: { "1.0.47": {} } }, items, entries).map((v) => v.version)).toEqual(["1.0.48"]);
    const md = renderEntry(entries[0], items[0]);
    expect(md).toMatch(/^## v1\.0\.48 — Walkthroughs go the distance \(Aug 25, 2026\)/);
    expect(md).toContain("- **Always approve for agents:**");
    expect(md).toContain("HeyClicky.dmg");
  });
});
```

- [ ] **Step 2:** run → FAIL. **Step 3: Implement** with regex parsing (no deps): `decode()` handles `&quot; &#x27; &#39; &lt; &gt; &amp; &nbsp;` and strips `<!-- -->` + tags; changelog split on `<article class="cl-entry">`; version from `cl-version`, date from `cl-date`, title `cl-title`, intro `cl-intro`, groups from `<div class="cl-group">` → head `cl-group-head`, items `<li>` → lead `cl-item-lead` (strip trailing `: `), text = decoded `cl-item-text` minus lead. `main()` (guarded by `process.argv[1] === fileURLToPath(import.meta.url)`): fetch both URLs, load state, compute new, print; `--write`: update JSON (`seen[version] = {build, date, url, title}`) and prepend rendered entries to the changelog md (seed the whole file if it doesn't exist); `--issues`: for each new version run `gh label create upstream --color B60205 --description "HeyClicky upstream release" --force` once then `gh issue create --title "HeyClicky v<v> released: <title>" --label upstream --body-file <tmp>` with body = rendered entry + "## Port checklist\n- [ ] Read the entry\n- [ ] Decide what to port\n- [ ] Link the PR". Exit 0 always; non-zero only on fetch failure.
- [ ] **Step 4:** tests PASS; `node scripts/upstream-watch.mjs --write` seeds `reference/upstream/*` from the live site (commit the seed). Workflow:

```yaml
name: upstream-watch
on:
  schedule: [{ cron: "17 6 * * *" }]
  workflow_dispatch:
permissions: { contents: write, issues: write }
jobs:
  watch:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - run: node scripts/upstream-watch.mjs --write --issues
        env: { GH_TOKEN: ${{ github.token }} }
      - run: |
          git config user.name "upstream-watch[bot]"; git config user.email "actions@github.com"
          git add reference/upstream && git diff --cached --quiet || git commit -m "chore(upstream): record new HeyClicky release [skip ci]" && git push
```

- [ ] **Step 5: Commit** `feat: watch HeyClicky releases (appcast + changelog) and open issues`.

---

### Task 5: Swift `SkillFile`, `AppSkillMatcher`, `SkillPromptBuilder`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/SkillFile.swift`, `AppSkillMatcher.swift`, `SkillPromptBuilder.swift`
- Test: `macos/OpenClicky/OpenClickyTests/SkillTests.swift`

**Interfaces:**

```swift
struct SkillFile: Equatable { let id: String; let name: String; let description: String; let apps: [String]; let sites: [String]; let surfaces: Set<String>; let body: String
  static func parse(_ markdown: String, id: String) -> SkillFile?
  static func load(directory: URL) -> [SkillFile]      // every <dir>/<id>/SKILL.md
  var isForTalk: Bool { surfaces.contains("talk") } }
struct FrontAppContext: Equatable { var bundleIdentifier: String?; var appName: String?; var url: URL?; var windowTitle: String? }
enum AppSkillMatcher { static func match(_ ctx: FrontAppContext, in skills: [SkillFile]) -> SkillFile? }
enum SkillPromptBuilder { static func build(activeSkills: [SkillFile], appSkill: SkillFile?, front: FrontAppContext?, activeBudget: Int = 6000, appBudget: Int = 4000) -> String }
```

- [ ] **Step 1: Failing tests** (Swift Testing): parse inline lists + default surfaces; `match` prefers a site skill over the browser's app skill when the URL host matches (`mail.google.com` → gmail, host suffix `google.com` must NOT match `notgoogle.com`); falls back to window title containing the site; bundle match; nil when nothing matches; `build` truncates the app skill to the budget with a `…` marker and drops whole active skills beyond 6,000 chars, omitting non-talk skills; empty when nothing to inject.
- [ ] **Step 2:** `xcodebuild test -project macos/OpenClicky/OpenClicky.xcodeproj -scheme OpenClicky -only-testing:OpenClickyTests/SkillTests CODE_SIGNING_ALLOWED=NO` → FAIL to compile. (If test hosting needs signing, use the team from `release.sh`.)
- [ ] **Step 3: Implement.** Frontmatter parser mirrors `skillMarkdown.ts` (regex `^---\n([\s\S]*?)\n---\n?([\s\S]*)$`, inline `[a, b]` lists, quotes stripped). Matcher order: sites by host (`host == site || host.hasSuffix("." + site)`), then sites by lowercase window title containment, then apps by bundle id. Builder output:

```
## Skill: <name>
<body>

## The app in front: <appName> (<bundle id>[, <host>])
<body[≤ budget]>
```

- [ ] **Step 4:** tests PASS. **Step 5: Commit** `feat(mac): skill files, app/site matcher, prompt builder`.

---

### Task 6: `SkillLibraryStore` + `FrontmostAppObserver`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/SkillLibraryStore.swift`, `FrontmostAppObserver.swift`
- Modify: `OpenClickyConfiguration.swift` (`appSkillsDirectory`, `userSkillsDirectory`, optional `appSkillsPath` in shell.json)

**Interfaces:**

```swift
@MainActor final class SkillLibraryStore: ObservableObject {
  @Published private(set) var librarySkills: [SkillFile]; @Published private(set) var activeIds: Set<String>; @Published private(set) var appSkills: [SkillFile]
  @Published var lastError: String?; @Published var isCreating = false
  func reload(); func setActive(_ id: String, _ on: Bool); func createSkill(request: String) async throws -> SkillFile
  var activeTalkSkills: [SkillFile] }
enum FrontmostAppObserver { static func current(excludingBundleIdentifier own: String?) -> FrontAppContext }
```

- Directories: `userSkillsDirectory = ~/.openclicky/skills` (library/, active/, activations.json — same layout and symlink semantics as `skillsLibrary.ts`; `setActive` rewrites `activations.json` and re-syncs `active/`). `appSkillsDirectory`: `settings.appSkillsPath` if set, else derived from `cliCommand.last` (`…/agent/dist/cli.js` → `../../app-skills`), else `~/.openclicky/app-skills`.
- Watch `library/` with `DispatchSource.makeFileSystemObjectSource(.write)` → `reload()` (debounced 300 ms).
- `createSkill`: POST `\(backendBaseURL)/skills/create` with `OpenClickyConfiguration.authorize`, body `{request, capabilities}` where capabilities = `["composio"]` if `composioMcpUrl != nil` + `["computer-use"]` if `cuaDriverBin != nil`; write `library/<id>/SKILL.md` (dedupe id with `-2`, `-3`), activate, reload, return.
- `FrontmostAppObserver.current`: `NSWorkspace.shared.frontmostApplication`; if it is our own bundle, use `NSWorkspace.shared.runningApplications` ordering? No — just return what is frontmost; the HUD is a non-activating panel so the user's app stays frontmost. For browsers (`com.apple.Safari`, `com.google.Chrome`, `company.thebrowser.Browser`, `com.microsoft.edgemac`, `com.brave.Browser`, `org.mozilla.firefox`): `AXUIElementCreateApplication(pid)` → `kAXFocusedWindowAttribute` → title via `kAXTitleAttribute`; URL: `AXDocument` on the window (Safari) else breadth-first search (depth ≤ 8, ≤ 400 nodes) for role `AXWebArea` and read `AXURL`. Guard with `AXIsProcessTrusted()`; return nil URL when not trusted.

- [ ] **Step 1:** implement; **Step 2:** build (`xcodebuild … build CODE_SIGNING_ALLOWED=NO`); **Step 3:** headless check — `openclicky skills create "…"` then relaunch app later shows it (verified in Task 9). **Step 4: Commit** `feat(mac): skill library store + frontmost app/site detection`.

---

### Task 7: Realtime `point_at` tool + `CompanionManager.pointAt`

**Files:**
- Modify: `RealtimeVoiceClient.swift` (tools, tool handling, `RealtimeScreenContext`, `lastScreenCapture`, `onPointAt`, instructions text), `CompanionScreenCaptureUtility.swift` (`captureCursorScreenContext` returns the capture), `CompanionManager.swift` (extract `pointAt`, wire `onPointAt`), `OverlayWindow.swift` (retarget while pointing)
- Test: `OpenClickyTests/PointingTests.swift` (`CompanionManager.screenLocation(forScreenshotPoint:in:)`)

**Interfaces:**

```swift
struct RealtimeScreenContext { let jpeg: Data; let caption: String; let capture: CompanionScreenCapture }
// RealtimeVoiceClient
var screenContextProvider: (() async -> RealtimeScreenContext?)?
var onPointAt: ((CGPoint, String, CompanionScreenCapture) -> Void)?
// CompanionManager
static func screenLocation(forScreenshotPoint p: CGPoint, in capture: CompanionScreenCapture) -> CGPoint  // AppKit global coords, clamped
func pointAt(screenshotPoint: CGPoint, label: String?, in capture: CompanionScreenCapture)
```

- [ ] **Step 1: Failing test** — a `CompanionScreenCapture` with screenshot 1280×800, display 2560×1600 points at frame origin (0,0): point (640, 400) → global (1280, 800); (99999, -5) clamps to (2560, 1600) and (0, 1600)… (y = displayHeight − localY: top-left screenshot y=0 → AppKit y = 1600).
- [ ] **Step 2:** run → FAIL. **Step 3: Implement.**
  - Extract the mapping from `sendTranscriptToClaudeWithScreenshot` (lines ~866–895) into `screenLocation(forScreenshotPoint:in:)`; `pointAt` does: `voiceState = .idle` only if it is `.processing`; `launchDockedCursorForPointing()`; `detectedElementBubbleText = label`; `detectedElementDisplayFrame = capture.displayFrame`; `detectedElementScreenLocation = location`; analytics call. The teacher lane calls `pointAt`.
  - Realtime tools: add

```swift
let pointTool: [String: Any] = ["type": "function", "name": "point_at",
  "description": "Fly the on-screen cursor buddy to a UI element in the attached screenshot and show a short label. Use it while you explain: one call per step, in order, as you say each step. Coordinates are pixels in the screenshot, origin top-left.",
  "parameters": ["type": "object", "properties": ["x": ["type": "integer"], "y": ["type": "integer"], "label": ["type": "string", "description": "1-3 words naming the element, e.g. 'export button'"]], "required": ["x", "y", "label"]]]
```

  - `handleToolCall`: `point_at` → parse ints, `onPointAt?(CGPoint(x:y:), label, capture)` if `lastScreenCapture` else output "no screenshot attached"; output `"pointing at \(label)"`. Keep the existing `function_call_output` + `response.create` continuation.
  - `defaultInstructions` gains: `When the user asks how to do something, where something is, or what to click, point at it with the point_at tool while you explain — one call per step, in order, and keep speaking between calls. Do not point for general questions or things they are obviously already looking at.`
  - `attachScreenContext` stores `lastScreenCapture = context.capture`.
  - `CompanionManager`: `realtimeVoiceClient.onPointAt = { [weak self] p, label, capture in self?.pointAt(screenshotPoint: p, label: label, in: capture) }`. Check `OverlayWindow.buddyIsVisibleOnThisScreen` / voice-state gating so the buddy is visible in `.responding`; if the spinner hides the triangle, treat `.responding` like `.idle` for the pointing branch.
  - Overlay retarget: in `startNavigatingToElement`, if `buddyNavigationMode == .pointingAtTarget`, cancel the pending fly-back timers (`navigationBubbleOpacity = 0`, invalidate `navigationAnimationTimer`) and start a new flight from the current buddy position.
- [ ] **Step 4:** build + tests PASS. Headless: with the backend up and `.env` sourced, `--openclicky-smoke-talk-file` using `say -o howto.wav --data-format=LEI16@24000 "how do i open a new tab here"` with Safari in front; the log must show `agent task`-style line `point_at … ` and `🎯 Element pointing`. **Step 5: Commit** `feat(mac): point_at tool lets Realtime voice point at the screen step by step`.

---

### Task 8: Inject skills into the talk lanes

**Files:**
- Modify: `RealtimeVoiceClient.swift` (`instructionsProvider`, `refreshInstructionsIfNeeded()`), `CompanionManager.swift` (owns `skillLibraryStore`, builds prompts; Claude teacher prompt append)

- [ ] **Step 1:** In `RealtimeVoiceClient`: `var instructionsProvider: (() -> String)?`, `private var sentInstructions = ""`; `func refreshInstructionsIfNeeded()` builds `instructionsProvider?() ?? instructions`, and if different from `sentInstructions` sends `["type": "session.update", "session": ["type": "realtime", "instructions": text]]` and logs `instructions updated (N chars)`. Call it in `beginPushToTalk()` (before audio start) and in `input_audio_buffer.speech_started` for always-on; `sessionUpdate(mode:)` uses the provider too and records `sentInstructions`.
- [ ] **Step 2:** `CompanionManager`: `let skillLibraryStore = SkillLibraryStore()`; `func talkSkillsBlock() -> String` = `SkillPromptBuilder.build(activeSkills: skillLibraryStore.activeTalkSkills, appSkill: AppSkillMatcher.match(front, in: skillLibraryStore.appSkills), front: front)` with `front = FrontmostAppObserver.current(excludingBundleIdentifier: Bundle.main.bundleIdentifier)`; `instructionsProvider = { [weak self] in RealtimeVoiceClient.defaultInstructions + "\n\n" + (self?.talkSkillsBlock() ?? "") }`. Teacher lane: `systemPrompt: Self.companionVoiceResponseSystemPrompt + "\n\n" + talkSkillsBlock()`.
- [ ] **Step 3:** build; smoke-talk-file with Safari in front logs `instructions updated` and the reply references Safari specifics. **Step 4: Commit** `feat(mac): inject app + active skills into Realtime and teacher prompts`.

---

### Task 9: HUD skills UI

**Files:**
- Modify: `NotchHUDPanels.swift` (`NotchHomeView` "Add skills" section)

- [ ] **Step 1:** Replace the four static tiles with: a line `"\(store.appSkills.count) app skills · auto by app"`; a scrollable list (max height ~110) of `store.librarySkills` rows: name, description (1 line, 55% white), `Toggle` bound to `store.activeIds.contains(id)` calling `store.setActive`; a "Create a skill…" `TextField` + return → `Task { try await store.createSkill(request:) }` with `isCreating` spinner and `lastError` in red under it; an "Open folder" icon button (`NSWorkspace.shared.open(userSkillsDirectory)`). Empty state text: "No skills yet — type what one should do".
- [ ] **Step 2:** build; run the installed app, hover the notch, create a skill ("answer like a pirate"), toggle it, ask a question → reply changes. **Step 3: Commit** `feat(mac): skill library in the notch HUD (activate, create)`.

---

### Task 10: Docs, release build, memory

**Files:**
- Modify: `README.md` (skills section: three layers, CLI, HUD, `/skills/create`, upstream watch), `macos/OpenClicky/OPENCLICKY.md` (new files table rows), `docs/skills.md` (new: authoring guide linking `app-skills/README.md`), `REVERSE-ENGINEERING.md` §9 (pointer to the changelog research), `macos/OpenClicky/VERSION` bump to 0.4.0.

- [ ] **Step 1:** write docs. **Step 2:** `npm test`, `npm run build`, `npm run release:mac` (installs to /Applications, no publish). **Step 3: Commit** `docs: skills layers, pointing, upstream watch; v0.4.0`. **Step 4:** update memory (`openclicky-realtime-in-app.md` or a new `openclicky-skills.md`): layout of `~/.openclicky/skills`, the `point_at` tool, the upstream watch state file.
