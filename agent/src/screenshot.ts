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
 * Windows has no `screencapture`: PowerShell copies the primary screen (or the whole virtual
 * desktop) into a PNG through System.Drawing, DPI-aware so a scaled display is captured at its real
 * size. The output path travels in an environment variable so no quoting reaches the script.
 */
export function windowsScreenshotCommand(outPath: string, opts: { allDisplays?: boolean } = {}): { bin: string; args: string[]; env: NodeJS.ProcessEnv } {
  const bounds = opts.allDisplays ? "[System.Windows.Forms.SystemInformation]::VirtualScreen" : "[System.Windows.Forms.Screen]::PrimaryScreen.Bounds";
  const script = [
    "$ErrorActionPreference = 'Stop'",
    "Add-Type -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool SetProcessDPIAware();' -Name Dpi -Namespace OpenClicky",
    "[OpenClicky.Dpi]::SetProcessDPIAware() | Out-Null",
    "Add-Type -AssemblyName System.Windows.Forms, System.Drawing",
    `$b = ${bounds}`,
    "$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height",
    "$g = [System.Drawing.Graphics]::FromImage($bmp)",
    "$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)",
    "$bmp.Save($env:OPENCLICKY_SCREENSHOT_PATH, [System.Drawing.Imaging.ImageFormat]::Png)",
    "$g.Dispose(); $bmp.Dispose()",
  ].join("; ");
  return {
    bin: "powershell.exe",
    args: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
    env: { ...process.env, OPENCLICKY_SCREENSHOT_PATH: outPath },
  };
}

/**
 * Linux has no one screenshot tool: the first one installed that works in this session, as
 * [command, arguments]. Wayland desktops refuse X11 tools, so grim (wlroots), gnome-screenshot and
 * spectacle (KDE) come first; scrot and ImageMagick's import read an X11 screen.
 */
export function linuxScreenshotCommands(outPath: string, wayland: boolean): [string, string[]][] {
  const waylandTools: [string, string[]][] = [
    ["grim", [outPath]],
    ["gnome-screenshot", ["-f", outPath]],
    ["spectacle", ["-b", "-n", "-f", "-o", outPath]],
  ];
  const x11Tools: [string, string[]][] = [
    ["scrot", ["-o", outPath]],
    ["import", ["-window", "root", outPath]],
    ["gnome-screenshot", ["-f", outPath]],
  ];
  return wayland ? waylandTools : x11Tools;
}

function captureLinuxScreen(outPath: string): string {
  const wayland = process.env.XDG_SESSION_TYPE === "wayland" || Boolean(process.env.WAYLAND_DISPLAY);
  const tried: string[] = [];
  for (const [bin, args] of linuxScreenshotCommands(outPath, wayland)) {
    const r = spawnSync(bin, args, { encoding: "utf8" });
    if (r.error) continue; // not installed
    if (r.status === 0 && fs.existsSync(outPath) && fs.statSync(outPath).size > 0) return outPath;
    tried.push(`${bin} (exit ${r.status}): ${(r.stderr || r.stdout || "").trim() || "no output"}`);
  }
  throw new Error(
    tried.length
      ? `screen capture failed: ${tried.join("; ")}`
      : `no screenshot tool found: install ${wayland ? "grim (Sway, Hyprland), gnome-screenshot or spectacle" : "scrot or imagemagick"}`,
  );
}

/**
 * Capture the screen to a PNG (macOS `screencapture`, Windows PowerShell, a Linux tool). Throws with
 * a permission hint on failure: on macOS the terminal running OpenClicky needs Screen Recording
 * access in System Settings → Privacy & Security.
 */
export function captureScreen(outPath = defaultScreenshotPath(), opts: { allDisplays?: boolean; bin?: string } = {}): string {
  if (process.platform === "win32" && !opts.bin) {
    const command = windowsScreenshotCommand(outPath, opts);
    const r = spawnSync(command.bin, command.args, { encoding: "utf8", env: command.env, windowsHide: true });
    if (r.error) throw new Error(`powershell failed to start: ${r.error.message}`);
    if (r.status !== 0 || !fs.existsSync(outPath) || fs.statSync(outPath).size === 0) {
      throw new Error(`screen capture failed (exit ${r.status}): ${(r.stderr || r.stdout || "").trim() || "no output"}. Is the desktop locked or the session disconnected?`);
    }
    return outPath;
  }
  if (process.platform === "linux" && !opts.bin) return captureLinuxScreen(outPath);
  if (process.platform !== "darwin" && !opts.bin) throw new Error("screenshot capture is only supported on macOS (screencapture), Windows and Linux");
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
