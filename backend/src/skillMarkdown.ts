/**
 * Hermes-style SKILL.md: `---` YAML frontmatter (flat keys, inline `[a, b]` lists) + Markdown body.
 * Identical to agent/src/skillMarkdown.ts (separate npm workspaces, no shared package) — keep in sync.
 */
export interface ParsedSkill {
  name: string;
  description: string;
  apps: string[];
  sites: string[];
  surfaces: string[];
  body: string;
}

function parseList(v: string): string[] {
  const inner = v.trim().replace(/^\[/, "").replace(/\]$/, "");
  return inner
    .split(",")
    .map((s) => s.trim().replace(/^["']|["']$/g, ""))
    .filter(Boolean);
}

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
  return {
    name: fm.name,
    description: fm.description,
    apps: fm.apps ? parseList(fm.apps) : [],
    sites: fm.sites ? parseList(fm.sites) : [],
    surfaces,
    body: m[2].trim(),
  };
}

/** kebab-case id from a skill name ("Write Like Me" → "write-like-me"). */
export const slugify = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "") || "skill";
