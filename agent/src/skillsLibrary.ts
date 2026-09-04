/**
 * The user's skill library on disk (shared with the macOS app's SkillLibraryStore — keep the layout in sync):
 *
 *   ~/.openclicky/skills/library/<id>/SKILL.md   every user skill
 *   ~/.openclicky/skills/activations.json         { active: [ids], updatedAt }
 *   ~/.openclicky/skills/active/<id> → library/<id>   symlinks for activated skills only; Codex loads this dir
 */
import fs from "node:fs";
import path from "node:path";
import { parseSkillMarkdown, slugify, type ParsedSkill } from "./skillMarkdown.js";

export { slugify };

export interface LibrarySkill extends ParsedSkill {
  id: string;
  path: string;
  active: boolean;
}
export interface Activations {
  active: string[];
  updatedAt?: string;
}

export const libraryDir = (dir: string) => path.join(dir, "library");
export const activeDir = (dir: string) => path.join(dir, "active");
const activationsPath = (dir: string) => path.join(dir, "activations.json");

export function readActivations(dir: string): Activations {
  try {
    const j = JSON.parse(fs.readFileSync(activationsPath(dir), "utf8"));
    return {
      active: Array.isArray(j.active) ? j.active.filter((x: unknown): x is string => typeof x === "string") : [],
      updatedAt: typeof j.updatedAt === "string" ? j.updatedAt : undefined,
    };
  } catch {
    return { active: [] };
  }
}

function writeActivations(dir: string, active: string[]) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(activationsPath(dir), JSON.stringify({ active: [...new Set(active)], updatedAt: new Date().toISOString() }, null, 2) + "\n");
}

export function listLibrary(dir: string): LibrarySkill[] {
  const lib = libraryDir(dir);
  if (!fs.existsSync(lib)) return [];
  const active = new Set(readActivations(dir).active);
  const out: LibrarySkill[] = [];
  for (const e of fs.readdirSync(lib, { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    const file = path.join(lib, e.name, "SKILL.md");
    if (!fs.existsSync(file)) continue;
    const parsed = parseSkillMarkdown(fs.readFileSync(file, "utf8"));
    if (!parsed) continue;
    out.push({ ...parsed, id: e.name, path: path.join(lib, e.name), active: active.has(e.name) });
  }
  return out.sort((a, b) => a.id.localeCompare(b.id));
}

/** Make active/ hold exactly one symlink per activated, existing skill. Returns the active ids. */
export function syncActiveDir(dir: string): string[] {
  const act = activeDir(dir);
  fs.mkdirSync(act, { recursive: true });
  const existing = new Set(listLibrary(dir).map((s) => s.id));
  const wanted = readActivations(dir).active.filter((id) => existing.has(id));
  for (const e of fs.readdirSync(act)) {
    if (!wanted.includes(e)) fs.rmSync(path.join(act, e), { recursive: true, force: true });
  }
  for (const id of wanted) {
    const link = path.join(act, id);
    let ok = false;
    try {
      ok = fs.lstatSync(link).isSymbolicLink() && fs.readlinkSync(link) === path.join(libraryDir(dir), id);
    } catch {
      ok = false;
    }
    if (!ok) {
      fs.rmSync(link, { recursive: true, force: true });
      fs.symlinkSync(path.join(libraryDir(dir), id), link);
    }
  }
  return wanted;
}

export function setActive(dir: string, id: string, on: boolean): string[] {
  const cur = readActivations(dir).active.filter((x) => x !== id);
  writeActivations(dir, on ? [...cur, id] : cur);
  return syncActiveDir(dir);
}

/** Validate + write library/<id>/SKILL.md and activate it. Throws on invalid markdown. */
export function createSkillFiles(dir: string, markdown: string): LibrarySkill {
  const parsed = parseSkillMarkdown(markdown);
  if (!parsed) throw new Error("SKILL.md needs frontmatter with name and description");
  const base = slugify(parsed.name);
  let id = base;
  let n = 2;
  while (fs.existsSync(path.join(libraryDir(dir), id))) id = `${base}-${n++}`;
  const skillPath = path.join(libraryDir(dir), id);
  fs.mkdirSync(skillPath, { recursive: true });
  fs.writeFileSync(path.join(skillPath, "SKILL.md"), markdown.endsWith("\n") ? markdown : markdown + "\n");
  setActive(dir, id, true);
  return { ...parsed, id, path: skillPath, active: true };
}
