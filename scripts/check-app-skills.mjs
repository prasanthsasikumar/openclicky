#!/usr/bin/env node
// Checks every app-teaching skill (app-skills/<id>/SKILL.md) before it can be merged, so a reviewer
// only has to read the words. App skills are added to what the voice model is told whenever their
// app is in front, so a skill must stay a set of notes about an app: the right shape and size,
// talk-only (never the agent that runs commands), and with nothing that sends people somewhere
// (links) or has them run something (commands). Run with `npm run check:skills`; CI runs it on
// every pull request. Folders starting with "_" (the template) are skipped.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The injection cap is 4,000 characters; the README asks for bodies under 3,500. */
export const MAXIMUM_BODY_CHARACTERS = 3500;
export const REQUIRED_SECTIONS = ["## Layout", "## Common tasks", "## Pointing hints", "## Gotchas"];
const ALLOWED_FIELDS = new Set(["name", "description", "apps", "sites", "surfaces", "integration"]);

const BUNDLE_IDENTIFIER = /^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;
const SITE_HOST = /^[a-z0-9-]+(\.[a-z0-9-]+)+$/;
const SKILL_ID = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/** Text that would send the person to a page, or have them (or a model) run something. */
const FORBIDDEN_PATTERNS = [
  { pattern: /https?:\/\/|www\./i, reason: "contains a web link; describe where to click instead" },
  { pattern: /```/, reason: "contains a code block; skills describe the screen, not commands" },
  { pattern: /\b(sudo|curl|wget|chmod|osascript|base64)\b|rm\s+-[a-z]*r|\|\s*(ba|z)?sh\b/i, reason: "mentions a shell command" },
  { pattern: /\b(ignore|disregard|forget)\b[^.\n]{0,40}\b(instructions|rules|prompt)\b/i, reason: "tries to change the assistant's instructions" },
];

function parseFrontmatter(markdown) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/.exec(markdown.replace(/^\uFEFF/, ""));
  if (!match) return null;
  const fields = {};
  for (const line of match[1].split(/\r?\n/)) {
    if (!line.trim()) continue;
    const keyValue = /^([A-Za-z_][\w-]*):\s*(.*)$/.exec(line);
    if (!keyValue) return { fields, body: match[2], badLine: line };
    fields[keyValue[1]] = keyValue[2].trim();
  }
  return { fields, body: match[2] };
}

/** `[a, "b"]` → ["a", "b"]. Lists are written inline in this folder; a block list reads as empty. */
function parseList(value) {
  if (value === undefined) return [];
  const inner = /^\[([^\]]*)\]$/.exec(value)?.[1] ?? value;
  return inner.split(",").map((item) => item.trim().replace(/^["']|["']$/g, "")).filter(Boolean);
}

/** Every problem with one skill file, as sentences; empty when it is fine. */
export function problemsWithSkill(skillId, markdown) {
  const problems = [];
  if (!SKILL_ID.test(skillId)) problems.push(`folder name "${skillId}" should be lowercase words joined by hyphens`);
  const parsed = parseFrontmatter(markdown);
  if (!parsed) return [...problems, "needs frontmatter between two --- lines at the very top"];
  const { fields, body, badLine } = parsed;
  if (badLine !== undefined) problems.push(`frontmatter line "${badLine}" is not "key: value" (write lists inline, [a, b])`);
  for (const field of Object.keys(fields)) {
    if (!ALLOWED_FIELDS.has(field)) problems.push(`unknown frontmatter field "${field}"`);
  }
  if (!fields.name) problems.push("needs a name");
  if (!fields.description) problems.push("needs a description");

  const apps = parseList(fields.apps);
  const sites = parseList(fields.sites);
  if (!apps.length && !sites.length) problems.push("needs apps (bundle ids) or sites (hosts) to match on");
  for (const app of apps) if (!BUNDLE_IDENTIFIER.test(app)) problems.push(`"${app}" does not look like a bundle id`);
  for (const site of sites) if (!SITE_HOST.test(site)) problems.push(`"${site}" does not look like a host (no https://, no path)`);
  const surfaces = parseList(fields.surfaces);
  if (surfaces.length !== 1 || surfaces[0] !== "talk") problems.push("surfaces must be [talk]: app skills only shape answers, never the agent");

  const trimmedBody = body.trim();
  if (trimmedBody.length > MAXIMUM_BODY_CHARACTERS) {
    problems.push(`body is ${trimmedBody.length} characters; keep it under ${MAXIMUM_BODY_CHARACTERS}`);
  }
  let searchFrom = 0;
  for (const section of REQUIRED_SECTIONS) {
    const sectionStart = trimmedBody.indexOf(`${section}\n`, searchFrom);
    if (sectionStart === -1) {
      problems.push(`needs the section "${section}" (sections in this order: ${REQUIRED_SECTIONS.join(", ")})`);
    } else {
      searchFrom = sectionStart + section.length;
    }
  }
  for (const { pattern, reason } of FORBIDDEN_PATTERNS) {
    if (pattern.test(markdown)) problems.push(reason);
  }
  return problems;
}

/** Problems across the whole folder, keyed by skill id: each skill's own, plus apps or sites claimed twice. */
export function problemsInAppSkillsFolder(appSkillsDirectory) {
  const problemsBySkill = new Map();
  const ownerOfMatch = new Map();
  const skillFolders = fs.readdirSync(appSkillsDirectory, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && !entry.name.startsWith("_"))
    .map((entry) => entry.name)
    .sort();
  for (const skillId of skillFolders) {
    const skillFile = path.join(appSkillsDirectory, skillId, "SKILL.md");
    if (!fs.existsSync(skillFile)) {
      problemsBySkill.set(skillId, ["the folder has no SKILL.md"]);
      continue;
    }
    const markdown = fs.readFileSync(skillFile, "utf8");
    const problems = problemsWithSkill(skillId, markdown);
    const fields = parseFrontmatter(markdown)?.fields ?? {};
    for (const match of [...parseList(fields.apps), ...parseList(fields.sites)]) {
      const owner = ownerOfMatch.get(match);
      if (owner) problems.push(`"${match}" is already taught by ${owner}`);
      else ownerOfMatch.set(match, skillId);
    }
    if (problems.length) problemsBySkill.set(skillId, problems);
  }
  return problemsBySkill;
}

const runDirectly = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (runDirectly) {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const appSkillsDirectory = process.argv[2] ?? path.resolve(here, "..", "app-skills");
  const problemsBySkill = problemsInAppSkillsFolder(appSkillsDirectory);
  if (!problemsBySkill.size) {
    process.stdout.write("app skills: all fine\n");
  } else {
    for (const [skillId, problems] of problemsBySkill) {
      for (const problem of problems) process.stdout.write(`app-skills/${skillId}/SKILL.md: ${problem}\n`);
    }
    process.exitCode = 1;
  }
}
