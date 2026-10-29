#!/usr/bin/env node
// Release notes for one version, read from CHANGELOG.md.
//
//   node scripts/release-notes.mjs check <version>                      exit 1 (with a message) when the section is missing or empty
//   node scripts/release-notes.mjs markdown <version> [--platform mac]  the bullets one platform's users get (all platforms + theirs)
//   node scripts/release-notes.mjs html <version> [--platform mac]      the same as a small HTML page for the Sparkle update dialog
//   node scripts/release-notes.mjs github <version> --platforms mac,windows [--mac-signing notarized|developer-id|development]
//                                       [--repo owner/name] [--ref v0.10.0] [--windows-signed]
//                                                                        the GitHub release page: what's new by platform, then downloads
//                                                                        (build number and commit are --ref's, default HEAD)
//
// A version's section may be split with "### All platforms", "### Mac", "### Windows" and "### Linux";
// bullets before the first of those count for every platform, so an unsplit section still works.
// The changelog is bullets and short paragraphs with **bold**, *italics*, `code` and [links](url);
// that is the whole Markdown this converter knows, so the appcast needs no extra dependency.

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const changelogPath = join(dirname(fileURLToPath(import.meta.url)), "..", "CHANGELOG.md");

export const PLATFORMS = ["mac", "windows", "linux"];
const PLATFORM_TITLES = { all: "All platforms", mac: "Mac", windows: "Windows", linux: "Linux" };
const HEADING_TO_PLATFORM = { "all platforms": "all", everywhere: "all", mac: "mac", macos: "mac", windows: "windows", linux: "linux" };

export function sectionFor(changelog, version) {
  const lines = changelog.split(/\r?\n/);
  const heading = new RegExp(`^##\\s+${version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(\\s|$)`);
  const start = lines.findIndex((line) => heading.test(line));
  if (start === -1) return null;
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    if (/^##\s/.test(lines[i])) { end = i; break; }
  }
  return lines.slice(start + 1, end).join("\n").trim();
}

// The section's text per platform ("all", "mac", "windows", "linux"), each trimmed; missing ones are "".
// A "###" heading that names no platform is an error, so a typo can't quietly drop notes.
export function splitByPlatform(section) {
  const parts = { all: [], mac: [], windows: [], linux: [] };
  let current = "all";
  for (const line of section.split("\n")) {
    const heading = /^###\s+(.*?)\s*$/.exec(line);
    if (heading) {
      const platform = HEADING_TO_PLATFORM[heading[1].toLowerCase()];
      if (!platform) throw new Error(`CHANGELOG.md: "### ${heading[1]}" is not one of: All platforms, Mac, Windows, Linux`);
      current = platform;
      continue;
    }
    parts[current].push(line);
  }
  return Object.fromEntries(Object.entries(parts).map(([key, value]) => [key, value.join("\n").trim()]));
}

export function notesForPlatform(section, platform) {
  const parts = splitByPlatform(section);
  return [parts.all, platform ? parts[platform] : ""].filter(Boolean).join("\n");
}

function escapeHtml(text) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function inline(text) {
  const codes = [];
  let out = escapeHtml(text).replace(/`([^`]+)`/g, (_, code) => {
    codes.push(`<code>${code}</code>`);
    return `${codes.length - 1}`;
  });
  out = out
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<a href="$2">$1</a>')
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  return out.replace(/(\d+)/g, (_, i) => codes[Number(i)]);
}

export function toHtml(markdown) {
  const blocks = [];
  let list = [];
  let paragraph = [];
  const flush = () => {
    if (list.length) { blocks.push(`<ul>\n${list.map((item) => `  <li>${inline(item)}</li>`).join("\n")}\n</ul>`); list = []; }
    if (paragraph.length) { blocks.push(`<p>${inline(paragraph.join(" "))}</p>`); paragraph = []; }
  };
  for (const raw of markdown.split("\n")) {
    const line = raw.trimEnd();
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);
    const heading = /^(#{3,6})\s+(.*)$/.exec(line);
    if (bullet) {
      if (paragraph.length) flush();
      list.push(bullet[1]);
    } else if (/^\s+\S/.test(line) && list.length) {
      list[list.length - 1] += ` ${line.trim()}`;
    } else if (heading) {
      flush();
      blocks.push(`<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>`);
    } else if (line.trim() === "") {
      flush();
    } else {
      if (list.length) flush();
      paragraph.push(line.trim());
    }
  }
  flush();
  return blocks.join("\n");
}

export function page(version, markdown) {
  return `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="color-scheme" content="light dark">
<style>
  body { font: 13px/1.5 -apple-system, system-ui, sans-serif; margin: 12px 16px; color: CanvasText; background: Canvas; }
  h2 { font-size: 15px; margin: 0 0 10px; }
  ul { padding-left: 20px; margin: 0; }
  li { margin-bottom: 8px; }
  p { margin: 0 0 8px; }
  code { font-family: ui-monospace, Menlo, monospace; font-size: 12px; }
  a { color: LinkText; }
</style>
</head>
<body>
<h2>What's new in OpenClicky ${escapeHtml(version)}</h2>
${toHtml(markdown)}
</body>
</html>
`;
}

const MAC_SIGNING = {
  notarized: "Signed with Developer ID and notarized by Apple.",
  "developer-id": "Signed with Developer ID, not notarized: right-click the app → Open the first time.",
  development: "Signed for development only: on another Mac, right-click the app → Open the first time.",
};

// The GitHub release body. `platforms` are the ones whose files are attached to the release; the
// Windows and Linux workflows rebuild the body when they attach theirs, so a platform's notes and
// download rows appear together with its files.
export function githubBody(section, { version, platforms, repo, build, commit, macSigning = "notarized", windowsSigned = false }) {
  const parts = splitByPlatform(section);
  const shipped = PLATFORMS.filter((platform) => platforms.includes(platform));
  const out = ["## What's new", ""];
  const groups = ["all", ...shipped].filter((key) => parts[key]);
  if (groups.length === 1 && groups[0] === "all") {
    out.push(parts.all, "");
  } else {
    for (const key of groups) out.push(`### ${PLATFORM_TITLES[key]}`, "", parts[key], "");
  }

  const download = (name) => `[\`${name}\`](https://github.com/${repo}/releases/download/v${version}/${name})`;
  const windowsNote = windowsSigned
    ? "Code-signed. Updates itself."
    : "Not code-signed yet: if SmartScreen warns, choose *More info* → *Run anyway*. Updates itself.";
  const rows = [];
  if (shipped.includes("mac")) {
    rows.push(`| macOS 14.2 or later, Apple silicon or Intel | ${download(`OpenClicky-${version}.dmg`)} | ${MAC_SIGNING[macSigning]} Open the dmg and click **Move to Applications**. Updates itself. |`);
  }
  if (shipped.includes("windows")) {
    rows.push(`| Windows 10 (2004 or later) or 11, x64 | ${download("OpenClicky.Windows-win-Setup.exe")} | ${windowsNote} |`);
    rows.push(`| Windows 10 (2004 or later) or 11, ARM64 | ${download("OpenClicky.Windows-win-arm64-Setup.exe")} | ${windowsNote} |`);
  }
  if (shipped.includes("linux")) {
    rows.push(`| Linux on X11 | ${download(`openclicky_linux-${version}-py3-none-any.whl`)} | Install with pip; see [OpenClicky on Linux](https://github.com/${repo}/blob/main/docs/linux.md). |`);
  }
  out.push("## Downloads", "", "| System | File | Notes |", "| --- | --- | --- |", ...rows, "");
  const extras = [];
  if (shipped.includes("windows")) extras.push("The `Portable.zip` files run OpenClicky on Windows without installing it.");
  extras.push(`Setup, keys and the agent lane are covered in the [README](https://github.com/${repo}#readme).`);
  out.push(extras.join(" "), "");
  out.push(`<sub>Built from ${commit}${shipped.includes("mac") ? ` · Mac build ${build}` : ""}.</sub>`);
  return out.join("\n") + "\n";
}

function option(args, name) {
  const index = args.indexOf(name);
  return index === -1 ? undefined : args[index + 1];
}

function git(...args) {
  return execFileSync("git", args, { cwd: dirname(changelogPath), encoding: "utf8" }).trim();
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [command, version, ...args] = process.argv.slice(2);
  if (!command || !version) {
    console.error("usage: release-notes.mjs check|markdown|html|github <version> [options]");
    process.exit(2);
  }
  const notes = sectionFor(readFileSync(changelogPath, "utf8"), version);
  if (!notes) {
    console.error(`CHANGELOG.md has no "## ${version}" section with text under it. Write what the release changes there first; the update dialog shows it.`);
    process.exit(1);
  }
  const platform = option(args, "--platform");
  if (platform && !PLATFORMS.includes(platform)) { console.error(`unknown platform: ${platform}`); process.exit(2); }
  try {
    if (command === "check") splitByPlatform(notes);
    else if (command === "markdown") process.stdout.write(notesForPlatform(notes, platform) + "\n");
    else if (command === "html") process.stdout.write(page(version, notesForPlatform(notes, platform)));
    else if (command === "github") {
      const platforms = (option(args, "--platforms") ?? "mac").split(",").filter(Boolean);
      const unknown = platforms.filter((name) => !PLATFORMS.includes(name));
      if (unknown.length) { console.error(`unknown platform: ${unknown.join(", ")}`); process.exit(2); }
      const macSigning = option(args, "--mac-signing") ?? "notarized";
      if (!MAC_SIGNING[macSigning]) { console.error(`unknown --mac-signing: ${macSigning}`); process.exit(2); }
      process.stdout.write(githubBody(notes, {
        version,
        platforms,
        repo: option(args, "--repo") ?? process.env.GITHUB_REPOSITORY ?? "prasanthsasikumar/openclicky",
        build: git("rev-list", "--count", option(args, "--ref") ?? "HEAD"),
        commit: git("rev-parse", "--short", `${option(args, "--ref") ?? "HEAD"}^{commit}`),
        macSigning,
        windowsSigned: args.includes("--windows-signed"),
      }));
    } else { console.error(`unknown command: ${command}`); process.exit(2); }
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
