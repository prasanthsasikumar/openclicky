import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { snapshotWorkspace, diffSnapshots, artifactsFromItems } from "../src/artifacts.js";

describe("artifacts", () => {
  it("detects new and modified files, ignoring node_modules/.git", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-art-"));
    fs.writeFileSync(path.join(dir, "old.txt"), "a");
    fs.mkdirSync(path.join(dir, "node_modules"));
    fs.writeFileSync(path.join(dir, "node_modules", "x.js"), "");
    fs.mkdirSync(path.join(dir, "sub"));
    fs.writeFileSync(path.join(dir, "sub", "keep.txt"), "k");

    const before = snapshotWorkspace(dir);
    fs.writeFileSync(path.join(dir, "new.txt"), "b");
    fs.writeFileSync(path.join(dir, "old.txt"), "changed"); // size changes even if mtime granularity is coarse
    fs.writeFileSync(path.join(dir, "node_modules", "y.js"), "");
    fs.mkdirSync(path.join(dir, "sub", "deep"));
    fs.writeFileSync(path.join(dir, "sub", "deep", "n.txt"), "n");

    expect(diffSnapshots(before, snapshotWorkspace(dir))).toEqual([
      path.join(dir, "new.txt"),
      path.join(dir, "old.txt"),
      path.join(dir, "sub", "deep", "n.txt"),
    ]);
  });

  it("extracts fileChange paths", () => {
    expect(
      artifactsFromItems([{ type: "fileChange", changes: [{ path: "/a/b.txt", kind: "add" }] }, { type: "agentMessage" }, null]),
    ).toEqual(["/a/b.txt"]);
  });
});
