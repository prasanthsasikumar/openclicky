import { describe, it, expect } from "vitest";
import fs from "node:fs";
// @ts-expect-error — plain ESM script at the repo root, no types
import { parseAppcast, parseChangelog, renderEntry, findNew, planIssues } from "../../scripts/upstream-watch.mjs";

const fx = (n: string) => fs.readFileSync(new URL(`./fixtures/${n}`, import.meta.url), "utf8");

describe("upstream-watch", () => {
  it("parses the Sparkle appcast", () => {
    const items = parseAppcast(fx("appcast.xml"));
    expect(items).toHaveLength(2);
    expect(items[0]).toMatchObject({
      version: "1.0.48",
      build: "57",
      date: "Tue, 25 Aug 2026 20:56:10 +0530",
      url: expect.stringContaining("v1.0.48/HeyClicky.dmg"),
    });
    expect(items[1].version).toBe("1.0.47");
  });

  it("parses changelog entries with groups and items", () => {
    const entries = parseChangelog(fx("changelog.html"));
    expect(entries).toHaveLength(2);
    expect(entries[0].version).toBe("1.0.48");
    expect(entries[0].date).toBe("Aug 25, 2026");
    expect(entries[0].title).toBe("Walkthroughs go the distance");
    expect(entries[0].intro).toContain("continuation of the last update, 1.0.47");
    expect(entries[0].intro).toContain("<3"); // &lt; decoded
    expect(entries[0].groups[0].head).toBe("new");
    expect(entries[0].groups[0].items[0].lead).toBe("Always approve for agents");
    expect(entries[0].groups[0].items[0].text).toContain('"Always approve"'); // entities decoded
    expect(entries[0].groups[0].items[0].text).not.toContain("<"); // tags stripped
    expect(entries[0].groups[0].items[0].text).not.toContain("requested by"); // requester chips dropped
    expect(entries[0].groups.map((g: { head: string }) => g.head)).toEqual(["new", "walkthroughs", "audio", "dictation", "agents", "fixed"]);
    expect(entries[1].version).toBe("1.0.47");
  });

  it("finds versions not in state and renders markdown", () => {
    const entries = parseChangelog(fx("changelog.html"));
    const items = parseAppcast(fx("appcast.xml"));
    const fresh = findNew({ seen: { "1.0.47": {} } }, items, entries);
    expect(fresh.map((v: { version: string }) => v.version)).toEqual(["1.0.48"]);
    expect(fresh[0].entry?.title).toBe("Walkthroughs go the distance");
    expect(fresh[0].appcast?.build).toBe("57");
    expect(findNew({ seen: { "1.0.47": {}, "1.0.48": {} } }, items, entries)).toEqual([]);

    const md = renderEntry(entries[0], items[0]);
    expect(md).toMatch(/^## v1\.0\.48 — Walkthroughs go the distance \(Aug 25, 2026\)/);
    expect(md).toContain("### new");
    expect(md).toContain("- **Always approve for agents:**");
    expect(md).toContain("HeyClicky.dmg");
    expect(md).toContain("build 57");
    // Without an appcast item the download line is omitted, nothing else changes.
    expect(renderEntry(entries[0])).not.toContain("HeyClicky.dmg");
  });

  it("handles an appcast-only version (changelog not yet published)", () => {
    const items = parseAppcast(fx("appcast.xml"));
    const fresh = findNew({ seen: {} }, items, []);
    expect(fresh.map((v: { version: string }) => v.version)).toEqual(["1.0.48", "1.0.47"]);
    expect(renderEntry(undefined, items[0])).toMatch(/^## v1\.0\.48 \(Tue, 25 Aug 2026 20:56:10 \+0530\)/);
  });

  it("matches class tokens exactly (cl-group is not cut at a cl-group-note child)", () => {
    const html = `<article class="cl-entry"><span class="cl-version">v<!-- -->9.9.9</span><span class="cl-date">Jan 1, 2027</span>
      <h2 class="cl-title">T</h2><div class="cl-group"><h3 class="cl-group-head">new</h3><ul class="cl-items">
      <li><span class="cl-item-text"><strong class="cl-item-lead">First: </strong>one</span></li>
      <div class="cl-group-note">a note that carries the cl-group prefix</div>
      <li><span class="cl-item-text"><strong class="cl-item-lead">Second: </strong>two</span></li></ul></div>
      <div class="cl-group"><h3 class="cl-group-head">fixed</h3><ul class="cl-items"><li><span class="cl-item-text">bare</span></li></ul></div></article>`;
    const [e] = parseChangelog(html);
    expect(e.groups.map((g: { head: string }) => g.head)).toEqual(["new", "fixed"]);
    expect(e.groups[0].items.map((i: { lead: string }) => i.lead)).toEqual(["First", "Second"]);
    expect(e.groups[1].items[0]).toEqual({ lead: "", text: "bare" });
  });

  // Since November 2026 each entry names every app's version (Mac and Windows numbers differ) instead of one cl-version.
  const appsHtml = [
    ["Mac 2.0.7", "Windows 2.0.7", "Both at once"],
    ["Mac 2.0.5", "Windows 2.0.6", "Mac behind Windows"],
    ["Windows 2.0.4", "", "Windows only"],
    ["Mac 2.0.3", "", "Mac only"],
    ["Mac 2.0.1-2", "", "A combined release"],
  ].map(([first, second, title]) => `<article class="cl-entry"><div class="cl-aside">`
    + `<span class="cl-app-version" title="${first}"><img class="cl-app-logo" alt=""/><span class="cl-app-num">${first.split(" ")[1]}</span></span>`
    + (second ? `<span class="cl-app-version" title="${second}"><span class="cl-app-num">${second.split(" ")[1]}</span></span>` : "")
    + `<span class="cl-date">Dec 1, 2027</span></div><div class="cl-body"><h2 class="cl-title">${title}</h2>`
    + `<div class="cl-group"><h3 class="cl-group-head">new</h3><ul class="cl-items"><li><span class="cl-item-text">a thing</span></li></ul></div></div></article>`).join("");

  it("reads each app's version and keys an entry by its Windows version when it has one", () => {
    const entries = parseChangelog(appsHtml);
    expect(entries.map((e: { version: string; mac?: string; windows?: string }) => [e.version, e.mac, e.windows])).toEqual([
      ["2.0.7", "2.0.7", "2.0.7"],
      ["2.0.6", "2.0.5", "2.0.6"],
      ["2.0.4", undefined, "2.0.4"],
      ["2.0.3", "2.0.3", undefined],
      ["2.0.1-2", "2.0.1-2", undefined],
    ]);
    expect(entries[1].title).toBe("Mac behind Windows");
    expect(entries[1].groups[0].items[0].text).toBe("a thing");
    expect(renderEntry(entries[1])).toMatch(/^## v2\.0\.6 \[Mac 2\.0\.5, Windows 2\.0\.6\] — Mac behind Windows \(Dec 1, 2027\)/);
  });

  it("puts a Mac appcast item with the entry that names its Mac version", () => {
    const entries = parseChangelog(appsHtml);
    const appcast = (version: string) => ({ version, build: "", date: "", url: `https://example.com/${version}.dmg` });
    // Mac 2.0.5 belongs to the entry keyed 2.0.6, not to a "2.0.5"; Mac 2.0.9 has no entry yet.
    const fresh = findNew({ seen: { "2.0.1-2": {}, "2.0.3": {}, "2.0.4": {} } }, [appcast("2.0.9"), appcast("2.0.7"), appcast("2.0.5")], entries);
    expect(fresh.map((v: { version: string }) => v.version)).toEqual(["2.0.9", "2.0.7", "2.0.6"]);
    expect(fresh[2].appcast?.version).toBe("2.0.5");
    expect(fresh[2].entry?.title).toBe("Mac behind Windows");
    expect(fresh[0].entry).toBeUndefined();
    // A Mac number that only a Windows-keyed entry holds stands alone, marked as the Mac's.
    const clash = findNew({ seen: {} }, [appcast("2.0.4")], entries);
    expect(clash.map((v: { version: string }) => v.version)).toContain("2.0.4 (Mac)");
    expect(clash.find((v: { version: string }) => v.version === "2.0.4")?.appcast).toBeUndefined();
  });

  it("plans issues only for new versions that have none recorded", () => {
    const fresh = [{ version: "1.0.49" }, { version: "1.0.48" }];
    const state = { seen: { "1.0.49": { issue: null }, "1.0.48": { issue: "https://github.com/x/y/issues/7" } } };
    expect(planIssues(state, fresh).map((v: { version: string }) => v.version)).toEqual(["1.0.49"]);
    expect(planIssues({ seen: {} }, fresh)).toHaveLength(2); // not yet recorded → planned
    expect(planIssues(state, [])).toEqual([]); // versions seen earlier are never re-opened
  });
});
