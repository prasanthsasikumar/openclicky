#!/usr/bin/env node
// Watch HeyClicky releases so new upstream features can be ported here.
//
// Sources (heyclicky.com has no RSS):
//   - Sparkle appcast:  https://farzaa.github.io/clicky-releases/appcast.xml  (version, build, date, DMG)
//   - Changelog page:   https://www.heyclicky.com/changelog                    (title, intro, grouped items)
//
// Usage: node scripts/upstream-watch.mjs [--write] [--issues]
//   (no flags)  print versions not yet recorded in reference/upstream/heyclicky-versions.json
//   --write     record them there and prepend their entries to reference/upstream/heyclicky-changelog.md
//   --issues    open one GitHub issue per new version (needs `gh` + GH_TOKEN), label `upstream`
// Exit code is 0 unless a fetch fails. Pure parsers are exported for tests; main() only runs
// when the file is executed directly.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const APPCAST_URL = "https://farzaa.github.io/clicky-releases/appcast.xml";
export const CHANGELOG_URL = "https://www.heyclicky.com/changelog";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const STATE_FILE = path.join(root, "reference", "upstream", "heyclicky-versions.json");
const LOG_FILE = path.join(root, "reference", "upstream", "heyclicky-changelog.md");

// ---------- HTML helpers ----------

const ENTITIES = { quot: '"', apos: "'", lt: "<", gt: ">", amp: "&", nbsp: " ", hellip: "…", mdash: "—", ndash: "–", rsquo: "’", lsquo: "‘", rdquo: "”", ldquo: "“" };

/** Strip React's `<!-- -->` markers and every tag, then decode entities and collapse whitespace. */
export function decode(html) {
  return (html ?? "")
    .replace(/<!--[\s\S]*?-->/g, "")
    .replace(/<[^>]+>/g, "")
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(Number(d)))
    .replace(/&([a-z]+);/gi, (m, name) => ENTITIES[name.toLowerCase()] ?? m)
    .replace(/\s+/g, " ")
    .trim();
}

/** Inner HTML of the first element whose class attribute contains `cls` (elements do not nest same-class). */
function firstByClass(html, cls) {
  const re = new RegExp(`<([a-z0-9]+)[^>]*class="[^"]*\\b${cls}\\b[^"]*"[^>]*>([\\s\\S]*?)<\\/\\1>`, "i");
  const m = re.exec(html);
  return m ? m[2] : "";
}

/** Split `html` into the chunks that start with an opening tag carrying class `cls` (chunk = up to the next such tag). */
function splitByClass(html, cls) {
  const re = new RegExp(`<[a-z0-9]+[^>]*class="[^"]*\\b${cls}\\b[^"]*"[^>]*>`, "gi");
  const starts = [];
  let m;
  while ((m = re.exec(html))) starts.push(m.index);
  return starts.map((s, i) => html.slice(s, i + 1 < starts.length ? starts[i + 1] : undefined));
}

// ---------- Parsers ----------

/** Sparkle appcast → [{ version, build, date, url }], in feed order (newest first). */
export function parseAppcast(xml) {
  const items = [];
  const re = /<item>([\s\S]*?)<\/item>/g;
  let m;
  while ((m = re.exec(xml))) {
    const item = m[1];
    const pick = (tag) => (new RegExp(`<${tag}>([^<]*)<\\/${tag}>`).exec(item) ?? [])[1]?.trim();
    const version = pick("sparkle:shortVersionString") ?? pick("title");
    const url = (/<enclosure[^>]*\burl="([^"]+)"/.exec(item) ?? [])[1];
    if (!version) continue;
    items.push({ version, build: pick("sparkle:version") ?? "", date: pick("pubDate") ?? "", url: url ?? "" });
  }
  return items;
}

/**
 * heyclicky.com/changelog → [{ version, date, title, intro, groups: [{ head, items: [{ lead, text }] }] }].
 * Markup: <article class="cl-entry"> <span class="cl-version">v<!-- -->1.0.48</span> <span class="cl-date">…</span>
 * <h2 class="cl-title">…</h2> <p class="cl-intro">…</p> <div class="cl-group"><h3 class="cl-group-head">new</h3>
 * <ul class="cl-items"><li><span class="cl-item-text"><strong class="cl-item-lead">Lead: </strong>text</span>
 * <span class="cl-reqs">requested by …</span></li>…
 */
export function parseChangelog(html) {
  const entries = [];
  for (const article of splitByClass(html, "cl-entry")) {
    const version = decode(firstByClass(article, "cl-version")).replace(/^v/i, "");
    if (!/^\d+(\.\d+)+/.test(version)) continue;
    const groups = [];
    for (const group of splitByClass(article, "cl-group")) {
      const head = decode(firstByClass(group, "cl-group-head"));
      const items = [];
      const liRe = /<li\b[^>]*>([\s\S]*?)<\/li>/g;
      let li;
      while ((li = liRe.exec(group))) {
        const itemText = firstByClass(li[1], "cl-item-text");
        if (!itemText) continue;
        const lead = decode(firstByClass(itemText, "cl-item-lead")).replace(/:\s*$/, "");
        const full = decode(itemText);
        const text = lead && full.startsWith(lead) ? full.slice(lead.length).replace(/^:\s*/, "").trim() : full;
        items.push({ lead, text });
      }
      if (head || items.length) groups.push({ head, items });
    }
    entries.push({
      version,
      date: decode(firstByClass(article, "cl-date")),
      title: decode(firstByClass(article, "cl-title")),
      intro: decode(firstByClass(article, "cl-intro")),
      groups,
    });
  }
  return entries;
}

// ---------- Diff + render ----------

const versionKey = (v) => v.split(".").map((n) => parseInt(n, 10) || 0); // "34-36" (a combined release) sorts as 34
const compareVersionsDesc = (a, b) => {
  const x = versionKey(a), y = versionKey(b);
  for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (y[i] ?? 0) - (x[i] ?? 0);
  return 0;
};

/** Versions present in the appcast or the changelog but absent from `state.seen`, newest first. */
export function findNew(state, appcastItems, changelogEntries) {
  const seen = state?.seen ?? {};
  const byVersion = new Map();
  for (const a of appcastItems) byVersion.set(a.version, { version: a.version, appcast: a, entry: undefined });
  for (const e of changelogEntries) {
    const cur = byVersion.get(e.version) ?? { version: e.version, appcast: undefined, entry: undefined };
    cur.entry = e;
    byVersion.set(e.version, cur);
  }
  return [...byVersion.values()].filter((v) => !(v.version in seen)).sort((a, b) => compareVersionsDesc(a.version, b.version));
}

/** One Markdown section for a version, from the changelog entry and/or the appcast item. */
export function renderEntry(entry, appcast) {
  const version = entry?.version ?? appcast?.version;
  const date = entry?.date || appcast?.date || "";
  const lines = [`## v${version}${entry?.title ? ` — ${entry.title}` : ""}${date ? ` (${date})` : ""}`, ""];
  if (appcast?.url) lines.push(`Download: ${appcast.url}${appcast.build ? ` (build ${appcast.build})` : ""}`, "");
  if (entry?.intro) lines.push(entry.intro, "");
  for (const g of entry?.groups ?? []) {
    if (g.head) lines.push(`### ${g.head}`, "");
    for (const it of g.items) lines.push(it.lead ? `- **${it.lead}:** ${it.text}` : `- ${it.text}`);
    lines.push("");
  }
  return lines.join("\n").trimEnd() + "\n";
}

// ---------- CLI ----------

const readState = () => {
  try {
    return JSON.parse(fs.readFileSync(STATE_FILE, "utf8"));
  } catch {
    return { seen: {} };
  }
};

async function fetchText(url) {
  const r = await fetch(url, { headers: { "user-agent": "openclicky-upstream-watch" } });
  if (!r.ok) throw new Error(`${url} → HTTP ${r.status}`);
  return r.text();
}

function writeState(state, fresh) {
  for (const v of fresh) {
    state.seen[v.version] = {
      build: v.appcast?.build ?? "",
      date: v.entry?.date || v.appcast?.date || "",
      url: v.appcast?.url ?? "",
      title: v.entry?.title ?? "",
      recordedAt: new Date().toISOString(),
    };
  }
  state.seen = Object.fromEntries(Object.entries(state.seen).sort(([a], [b]) => compareVersionsDesc(a, b)));
  fs.mkdirSync(path.dirname(STATE_FILE), { recursive: true });
  fs.writeFileSync(STATE_FILE, JSON.stringify({ appcast: APPCAST_URL, changelog: CHANGELOG_URL, ...state }, null, 2) + "\n");
}

function writeLog(fresh) {
  const header = `# HeyClicky changelog (mirrored)\n\nRecorded by \`scripts/upstream-watch.mjs\` from ${CHANGELOG_URL} and the Sparkle appcast. Newest first.\n\n`;
  const existing = fs.existsSync(LOG_FILE) ? fs.readFileSync(LOG_FILE, "utf8") : "";
  const body = existing.startsWith(header) ? existing.slice(header.length) : existing;
  const sections = fresh.map((v) => renderEntry(v.entry, v.appcast)).join("\n");
  fs.writeFileSync(LOG_FILE, header + sections + (body ? "\n" + body : ""));
}

function openIssues(fresh) {
  execFileSync("gh", ["label", "create", "upstream", "--color", "B60205", "--description", "HeyClicky upstream release", "--force"], { stdio: "inherit" });
  for (const v of fresh) {
    const body = renderEntry(v.entry, v.appcast) + "\n## Port checklist\n- [ ] Read the entry\n- [ ] Decide what to port\n- [ ] Link the PR\n";
    const tmp = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "upstream-")), "body.md");
    fs.writeFileSync(tmp, body);
    const title = `HeyClicky v${v.version} released${v.entry?.title ? `: ${v.entry.title}` : ""}`;
    execFileSync("gh", ["issue", "create", "--title", title, "--label", "upstream", "--body-file", tmp], { stdio: "inherit" });
  }
}

export async function main(argv = process.argv.slice(2)) {
  const write = argv.includes("--write");
  const issues = argv.includes("--issues");
  const [appcastXml, changelogHtml] = await Promise.all([fetchText(APPCAST_URL), fetchText(CHANGELOG_URL)]);
  const appcast = parseAppcast(appcastXml);
  const changelog = parseChangelog(changelogHtml);
  const state = readState();
  const fresh = findNew(state, appcast, changelog);
  if (!fresh.length) {
    console.log(`no new HeyClicky versions (latest recorded: ${Object.keys(state.seen)[0] ?? "none"}; appcast ${appcast[0]?.version ?? "?"}, changelog ${changelog[0]?.version ?? "?"})`);
    return;
  }
  for (const v of fresh) console.log(`new: v${v.version}${v.entry?.title ? ` — ${v.entry.title}` : ""}${v.entry?.date ? ` (${v.entry.date})` : ""}${v.appcast ? "" : " [changelog only]"}${v.entry ? "" : " [appcast only]"}`);
  if (issues) openIssues(fresh);
  if (write) {
    writeLog(fresh);
    writeState(state, fresh);
    console.log(`recorded ${fresh.length} version(s) in ${path.relative(root, STATE_FILE)} and ${path.relative(root, LOG_FILE)}`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((e) => {
    console.error(`upstream-watch: ${e.message}`);
    process.exit(1);
  });
}
