import { describe, it, expect } from "vitest";
import fs from "node:fs";
// @ts-expect-error — plain ESM script at the repo root, no types
import { parseAppcast, parseChangelog, renderEntry, findNew } from "../../scripts/upstream-watch.mjs";

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
});
