#!/usr/bin/env node
// Port HeyClicky's bundled skills + model instructions into skills/, rebranded for OpenClicky.
// Reproducible: re-run with `npm run port-skills` after editing the reference material.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const srcSkills = path.join(root, "reference", "clicky-bundled-skills");
const srcInstructions = path.join(root, "reference", "clicky-model-instructions-verbatim.md");
const dst = path.join(root, "skills");

const SKIP_DIRS = new Set(["powerpoint"]); // proprietary — never copied
const TEXT_EXT = new Set([".md", ".yaml", ".yml", ".sh", ".py", ".txt", ".json", ".toml"]);
const CLICKY_SKILLS =
  "artifacts|build-preview|creative-studio|dev-setup-doctor|email-assistant|google-workspace|repo-operator|research-report|crons|scheduled-crons";

export function rebrand(text) {
  return text
    .replace(/HeyClicky/g, "OpenClicky")
    .replace(/\bClicky\b/g, "OpenClicky")
    .replace(new RegExp(`\\bclicky-(${CLICKY_SKILLS})\\b`, "g"), "openclicky-$1")
    .replace(/\.clicky-preview\.log/g, ".openclicky-preview.log")
    .replace(/\bclicky\.codex\b/g, "openclicky.codex");
}

function renameSkillDir(name) {
  return name.startsWith("clicky-") ? `openclicky-${name.slice("clicky-".length)}` : name;
}

function copyTree(from, to) {
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const f = path.join(from, entry.name);
    const t = path.join(to, entry.name);
    if (entry.isDirectory()) {
      copyTree(f, t);
    } else if (TEXT_EXT.has(path.extname(entry.name).toLowerCase())) {
      fs.writeFileSync(t, rebrand(fs.readFileSync(f, "utf8")));
    } else {
      fs.copyFileSync(f, t); // binary assets byte-for-byte
    }
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const ported = [];
  for (const entry of fs.readdirSync(srcSkills, { withFileTypes: true })) {
    if (!entry.isDirectory() || SKIP_DIRS.has(entry.name)) continue;
    const target = renameSkillDir(entry.name);
    fs.rmSync(path.join(dst, target), { recursive: true, force: true });
    copyTree(path.join(srcSkills, entry.name), path.join(dst, target));
    ported.push(target);
  }
  fs.writeFileSync(path.join(dst, "ModelInstructions.md"), rebrand(fs.readFileSync(srcInstructions, "utf8")));
  console.log(`ported ${ported.length} skills + ModelInstructions.md -> skills/`);
  for (const p of ported) console.log(`  ${p}`);
}
