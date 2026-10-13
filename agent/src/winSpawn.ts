/**
 * Windows: `spawn("codex", …)` without a shell finds only `codex.exe`, not the `codex.cmd` shim npm
 * installs, and running a .cmd through cmd.exe would mangle arguments and leave the real process
 * orphaned when the cmd.exe wrapper is killed. So an npm shim is read for the script it wraps and
 * that script is run with this Node directly (the same thing the Windows app's ProcessRunner does).
 * Everywhere else, and for anything that is not an npm shim, the command is returned unchanged.
 */
import fs from "node:fs";
import path from "node:path";

export interface SpawnTarget {
  command: string;
  args: string[];
  /** Only for a .cmd/.bat that is not an npm shim: it has to go through cmd.exe. */
  shell?: boolean;
}

/** The JS file an npm .cmd shim runs: the line `"%_prog%"  "%dp0%\node_modules\…\cli.js" %*`. */
export function npmShimScript(shimPath: string, shimText: string): string | undefined {
  const match = shimText.match(/"%~?dp0%?\\([^"]+\.(?:c|m)?js)"/i);
  return match ? path.win32.join(path.win32.dirname(shimPath), match[1]) : undefined;
}

/** Where `command` resolves on PATH + PATHEXT (Windows rules), or undefined. */
export function resolveOnPath(command: string, env: NodeJS.ProcessEnv, exists: (p: string) => boolean = fs.existsSync): string | undefined {
  if (path.win32.isAbsolute(command) || command.includes("\\") || command.includes("/")) return exists(command) ? command : undefined;
  const extensions = (env.PATHEXT ?? ".COM;.EXE;.BAT;.CMD").split(";").filter(Boolean);
  const hasExtension = path.win32.extname(command) !== "";
  for (const dir of (env.PATH ?? env.Path ?? "").split(";").filter(Boolean)) {
    const candidates = hasExtension ? [command] : extensions.map((ext) => command + ext.toLowerCase());
    for (const name of candidates) {
      const full = path.win32.join(dir.replace(/^"|"$/g, ""), name);
      if (exists(full)) return full;
    }
  }
  return undefined;
}

export function spawnTarget(
  command: string,
  args: string[],
  env: NodeJS.ProcessEnv = process.env,
  platform: NodeJS.Platform = process.platform,
  readText: (p: string) => string | undefined = (p) => {
    try {
      return fs.readFileSync(p, "utf8");
    } catch {
      return undefined;
    }
  },
  exists: (p: string) => boolean = fs.existsSync,
): SpawnTarget {
  if (platform !== "win32") return { command, args };
  const resolved = resolveOnPath(command, env, exists);
  if (!resolved || !/\.(cmd|bat)$/i.test(resolved)) return { command: resolved ?? command, args };
  const text = readText(resolved);
  const script = text ? npmShimScript(resolved, text) : undefined;
  if (script && exists(script)) return { command: process.execPath, args: [script, ...args] };
  // Node joins a shell command line unquoted; quote the path (it may sit under "Program Files").
  return { command: `"${resolved}"`, args, shell: true };
}
