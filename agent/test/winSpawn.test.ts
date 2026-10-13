import { describe, it, expect } from "vitest";
import { npmShimScript, resolveOnPath, spawnTarget } from "../src/winSpawn.js";
import { buildChildEnv } from "../src/codex.js";
import { windowsScreenshotCommand } from "../src/screenshot.js";
import { resolveConfig } from "../src/config.js";

const SHIM = [
  "@ECHO off",
  "GOTO start",
  ":find_dp0",
  "SET dp0=%~dp0",
  "EXIT /b",
  ":start",
  "SETLOCAL",
  "CALL :find_dp0",
  'endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js" %*',
].join("\r\n");

describe("Windows spawning", () => {
  const env = { PATH: "C:\\Windows\\System32;C:\\Users\\t\\AppData\\Roaming\\npm", PATHEXT: ".COM;.EXE;.BAT;.CMD" };
  const shim = "C:\\Users\\t\\AppData\\Roaming\\npm\\codex.cmd";
  const script = "C:\\Users\\t\\AppData\\Roaming\\npm\\node_modules\\@openai\\codex\\bin\\codex.js";

  it("reads the script an npm shim wraps", () => {
    expect(npmShimScript(shim, SHIM)).toBe(script);
    expect(npmShimScript(shim, "@echo off\r\nnode other.js %*\r\n")).toBeUndefined();
  });

  it("resolves a command through PATH and PATHEXT", () => {
    const existing = new Set([shim]);
    expect(resolveOnPath("codex", env, (p) => existing.has(p))).toBe(shim);
    expect(resolveOnPath("missing", env, (p) => existing.has(p))).toBeUndefined();
  });

  it("runs an npm shim's script with this Node, and leaves other platforms alone", () => {
    const existing = new Set([shim, script]);
    const target = spawnTarget("codex", ["app-server", "--stdio"], env, "win32", (p) => (p === shim ? SHIM : undefined), (p) => existing.has(p));
    expect(target).toEqual({ command: process.execPath, args: [script, "app-server", "--stdio"] });
    expect(spawnTarget("codex", ["x"], env, "darwin")).toEqual({ command: "codex", args: ["x"] });
  });

  it("runs an exe directly and any other batch file through cmd", () => {
    const exe = "C:\\Windows\\System32\\codex.exe";
    expect(spawnTarget("codex", ["x"], env, "win32", () => undefined, (p) => p === exe)).toEqual({ command: exe, args: ["x"] });
    const other = "C:\\Windows\\System32\\codex.bat";
    expect(spawnTarget("codex", ["x"], env, "win32", () => "@echo off\r\n", (p) => p === other)).toEqual({ command: `"${other}"`, args: ["x"], shell: true });
  });

  it("keeps the Windows system variables in Codex's environment", () => {
    const cfg = resolveConfig({ token: "t" }, { HOME: "/h" });
    const source = { PATH: "C:\\Windows", SystemRoot: "C:\\Windows", USERPROFILE: "C:\\Users\\t", APPDATA: "C:\\Users\\t\\AppData\\Roaming", AWS_SECRET_ACCESS_KEY: "secret" };
    const windows = buildChildEnv(cfg, source, "win32");
    expect(windows.SystemRoot).toBe("C:\\Windows");
    expect(windows.USERPROFILE).toBe("C:\\Users\\t");
    expect(windows.APPDATA).toBe("C:\\Users\\t\\AppData\\Roaming");
    expect(windows.AWS_SECRET_ACCESS_KEY).toBeUndefined();
    expect(buildChildEnv(cfg, source, "darwin").SystemRoot).toBeUndefined();
  });

  it("captures the screen with PowerShell, the path passed out of band", () => {
    const command = windowsScreenshotCommand("C:\\Temp\\it's a shot.png");
    expect(command.bin).toBe("powershell.exe");
    expect(command.env.OPENCLICKY_SCREENSHOT_PATH).toBe("C:\\Temp\\it's a shot.png");
    expect(command.args.at(-1)).toContain("PrimaryScreen.Bounds");
    expect(command.args.at(-1)).not.toContain("it's a shot");
    expect(windowsScreenshotCommand("x.png", { allDisplays: true }).args.at(-1)).toContain("VirtualScreen");
  });
});
