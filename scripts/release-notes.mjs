#!/usr/bin/env node
// Release notes for one version, read from CHANGELOG.md.
//
//   node scripts/release-notes.mjs check <version>     exit 1 (with a message) when the section is missing or empty
//   node scripts/release-notes.mjs markdown <version>  the section's body, as written
//   node scripts/release-notes.mjs html <version>      the same as a small HTML page for the Sparkle update dialog
//
// The changelog is bullets and short paragraphs with **bold**, *italics*, `code` and [links](url);
// that is the whole Markdown this converter knows, so the appcast needs no extra dependency.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const changelogPath = join(dirname(fileURLToPath(import.meta.url)), "..", "CHANGELOG.md");

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

function escapeHtml(text) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function inline(text) {
  const codes = [];
  let out = escapeHtml(text).replace(/`([^`]+)`/g, (_, code) => {
    codes.push(`<code>${code}</code>`);
    return `\uE000${codes.length - 1}\uE000`;
  });
  out = out
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<a href="$2">$1</a>')
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  return out.replace(/\uE000(\d+)\uE000/g, (_, i) => codes[Number(i)]);
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

const [command, version] = process.argv.slice(2);
if (import.meta.url === `file://${process.argv[1]}`) {
  if (!command || !version) {
    console.error("usage: release-notes.mjs check|markdown|html <version>");
    process.exit(2);
  }
  const notes = sectionFor(readFileSync(changelogPath, "utf8"), version);
  if (!notes) {
    console.error(`CHANGELOG.md has no "## ${version}" section with text under it. Write what the release changes there first; the update dialog shows it.`);
    process.exit(1);
  }
  if (command === "markdown") process.stdout.write(notes + "\n");
  else if (command === "html") process.stdout.write(page(version, notes));
  else if (command !== "check") { console.error(`unknown command: ${command}`); process.exit(2); }
}
