import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { parseSkillMarkdown } from "../src/skillMarkdown.js";
import { listLibrary, setActive, syncActiveDir, createSkillFiles, readActivations, slugify } from "../src/skillsLibrary.js";

const md = (name: string, extra = "") => `---\nname: ${name}\ndescription: d ${name}\n${extra}---\n\n# ${name}\nbody\n`;
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "oc-skills-"));

describe("parseSkillMarkdown", () => {
  it("parses frontmatter with inline lists", () => {
    const s = parseSkillMarkdown(md("figma", 'apps: [com.figma.Desktop, "com.figma.Beta"]\nsites: [figma.com]\nsurfaces: [talk]\n'))!;
    expect(s.name).toBe("figma");
    expect(s.apps).toEqual(["com.figma.Desktop", "com.figma.Beta"]);
    expect(s.sites).toEqual(["figma.com"]);
    expect(s.surfaces).toEqual(["talk"]);
    expect(s.body).toContain("# figma");
  });
  it("ignores trailing comments after inline lists and scalars", () => {
    const s = parseSkillMarkdown(md("x", "surfaces: [talk, agent]   # talk = chatting; agent = doing work\napps: [com.a.b] # comment\n"))!;
    expect(s.surfaces).toEqual(["talk", "agent"]);
    expect(s.apps).toEqual(["com.a.b"]);
    expect(parseSkillMarkdown("---\nname: x # not part of the name\ndescription: \"keep # inside quotes\"\n---\n")).toMatchObject({ name: "x", description: "keep # inside quotes" });
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
    expect(fs.lstatSync(path.join(dir, "active", "same-2")).isSymbolicLink()).toBe(true);
    expect(slugify("  Hello, World! ")).toBe("hello-world");
  });
  it("ignores activations of skills that no longer exist", () => {
    const dir = tmp();
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, "activations.json"), JSON.stringify({ active: ["gone"] }));
    expect(syncActiveDir(dir)).toEqual([]);
  });
  it("rejects invalid markdown on create", () => {
    expect(() => createSkillFiles(tmp(), "no frontmatter")).toThrow(/frontmatter/);
  });
});
