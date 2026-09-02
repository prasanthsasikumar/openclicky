import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

/** Arguments for macOS `screencapture`: silent (-x), PNG, main display only unless `allDisplays`. */
export function screenshotArgs(outPath: string, opts: { allDisplays?: boolean } = {}): string[] {
  const args = ["-x", "-t", "png"];
  if (!opts.allDisplays) args.push("-m");
  args.push(outPath);
  return args;
}

export function defaultScreenshotPath(): string {
  const dir = path.join(os.tmpdir(), "openclicky-screenshots");
  fs.mkdirSync(dir, { recursive: true });
  return path.join(dir, `shot-${new Date().toISOString().replace(/[:.]/g, "-")}.png`);
}

/**
 * Capture the screen to a PNG (macOS only). Throws with a permission hint on failure: the
 * terminal running OpenClicky needs Screen Recording access in System Settings → Privacy & Security.
 */
export function captureScreen(outPath = defaultScreenshotPath(), opts: { allDisplays?: boolean; bin?: string } = {}): string {
  if (process.platform !== "darwin" && !opts.bin) throw new Error("screenshot capture is only supported on macOS (screencapture)");
  const r = spawnSync(opts.bin ?? "screencapture", screenshotArgs(outPath, opts), { encoding: "utf8" });
  if (r.error) throw new Error(`screencapture failed to start: ${r.error.message}`);
  if (r.status !== 0 || !fs.existsSync(outPath) || fs.statSync(outPath).size === 0) {
    throw new Error(
      `screencapture failed (exit ${r.status}): ${(r.stderr || r.stdout || "").trim() || "no output"}. ` +
        "Grant Screen Recording permission to your terminal in System Settings → Privacy & Security → Screen Recording.",
    );
  }
  return outPath;
}
