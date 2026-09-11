import fs from "node:fs";
import path from "node:path";
import { repoRoot } from "./config.js";

/**
 * One source of truth for the version the CLI reports. `macos/OpenClicky/VERSION` is what
 * `release.sh` reads and what the shipped app carries, so the CLI reads the same file rather than
 * keeping its own copy — the hardcoded string it used to report ("0.2.0") matched neither the app,
 * nor either package.json, nor the released build.
 */
export const VERSION_FILE = path.join("macos", "OpenClicky", "VERSION");

/** The version, or "unknown" when the CLI is running somewhere the file is not shipped. */
export function readVersion(root: string = repoRoot()): string {
  try {
    const raw = fs.readFileSync(path.join(root, VERSION_FILE), "utf8").trim();
    return raw || "unknown";
  } catch {
    return "unknown";
  }
}
