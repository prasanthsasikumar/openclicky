import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { screenshotArgs, captureScreen, defaultScreenshotPath } from "../src/screenshot.js";

describe("screenshot", () => {
  it("builds silent PNG screencapture args", () => {
    expect(screenshotArgs("/t/a.png")).toEqual(["-x", "-t", "png", "-m", "/t/a.png"]);
    expect(screenshotArgs("/t/a.png", { allDisplays: true })).toEqual(["-x", "-t", "png", "/t/a.png"]);
  });
  it("default path is a fresh png under the tmp dir", () => {
    const p = defaultScreenshotPath();
    expect(p.endsWith(".png")).toBe(true);
    expect(fs.existsSync(path.dirname(p))).toBe(true);
  });
  it("uses a fake capture binary and validates the output file", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-shot-"));
    const bin = path.join(dir, "fakecapture.sh");
    fs.writeFileSync(bin, '#!/bin/sh\nout="$5"; [ -n "$out" ] || out="$4"; printf PNG > "$out"\n', { mode: 0o755 });
    const out = path.join(dir, "shot.png");
    expect(captureScreen(out, { bin })).toBe(out);
    expect(fs.readFileSync(out, "utf8")).toBe("PNG");
  });
  it("throws with a permission hint when the binary fails", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-shot-"));
    const bin = path.join(dir, "failcapture.sh");
    fs.writeFileSync(bin, "#!/bin/sh\necho 'could not create image' >&2; exit 1\n", { mode: 0o755 });
    expect(() => captureScreen(path.join(dir, "x.png"), { bin })).toThrow(/Screen Recording permission/);
  });
});
