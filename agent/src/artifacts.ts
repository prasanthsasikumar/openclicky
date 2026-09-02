import fs from "node:fs";
import path from "node:path";

const SKIP = new Set(["node_modules", ".git", "dist", ".venv", "__pycache__"]);
const MAX_DEPTH = 6;
const MAX_FILES = 20_000;

type Snapshot = Map<string, string>; // abs path → `${mtimeMs}:${size}`

/** Record every regular file under `dir` (bounded depth/count; skips dependency and VCS dirs). */
export function snapshotWorkspace(dir: string): Snapshot {
  const snap: Snapshot = new Map();
  const walk = (d: string, depth: number) => {
    if (depth > MAX_DEPTH || snap.size >= MAX_FILES) return;
    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (SKIP.has(e.name) || (e.name.startsWith(".") && e.isDirectory())) continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p, depth + 1);
      else if (e.isFile()) {
        try {
          const st = fs.statSync(p);
          snap.set(p, `${st.mtimeMs}:${st.size}`);
        } catch {
          /* vanished */
        }
      }
    }
  };
  walk(dir, 0);
  return snap;
}

/** Files that are new or changed between two snapshots (sorted absolute paths). */
export function diffSnapshots(before: Snapshot, after: Snapshot): string[] {
  const out: string[] = [];
  for (const [p, sig] of after) if (before.get(p) !== sig) out.push(p);
  return out.sort();
}

/** Paths reported by Codex `fileChange` items. */
export function artifactsFromItems(items: unknown[]): string[] {
  const out: string[] = [];
  for (const it of items as any[]) {
    if (it?.type !== "fileChange" || !Array.isArray(it.changes)) continue;
    for (const ch of it.changes) if (typeof ch?.path === "string") out.push(ch.path);
  }
  return out;
}
