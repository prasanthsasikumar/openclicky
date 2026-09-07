import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import type { AgentConfig } from "./config.js";
import { backendHeaders } from "./backendHeaders.js";

export interface RecordOptions {
  seconds: number;
  outPath?: string;
  /** AVFoundation audio device index (macOS). Default 0 = system default microphone. */
  device?: string;
  ffmpegBin?: string;
}

/** ffmpeg arguments for a mono 16 kHz WAV capture from the macOS microphone (AVFoundation). */
export function ffmpegRecordArgs(opts: { seconds: number; outPath: string; device?: string }): string[] {
  return ["-hide_banner", "-loglevel", "error", "-y", "-f", "avfoundation", "-i", `:${opts.device ?? "0"}`, "-t", String(opts.seconds), "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", opts.outPath];
}

export function defaultRecordingPath(): string {
  const dir = path.join(os.tmpdir(), "openclicky-audio");
  fs.mkdirSync(dir, { recursive: true });
  return path.join(dir, `rec-${new Date().toISOString().replace(/[:.]/g, "-")}.wav`);
}

/** Record the microphone for `seconds` (push-to-talk stand-in). Throws with a permission hint on failure. */
export function recordAudio(opts: RecordOptions): string {
  const outPath = opts.outPath ?? defaultRecordingPath();
  const bin = opts.ffmpegBin ?? "ffmpeg";
  const r = spawnSync(bin, ffmpegRecordArgs({ seconds: opts.seconds, outPath, device: opts.device }), { encoding: "utf8" });
  if (r.error) throw new Error(`${bin} failed to start: ${r.error.message} (install with \`brew install ffmpeg\`)`);
  if (r.status !== 0 || !fs.existsSync(outPath) || fs.statSync(outPath).size === 0) {
    throw new Error(
      `${bin} recording failed (exit ${r.status}): ${(r.stderr || r.stdout || "").trim() || "no output"}. ` +
        "Grant Microphone permission to your terminal in System Settings → Privacy & Security → Microphone.",
    );
  }
  return outPath;
}

const MIME: Record<string, string> = { ".wav": "audio/wav", ".mp3": "audio/mpeg", ".m4a": "audio/m4a", ".webm": "audio/webm", ".ogg": "audio/ogg" };

/** Speech-to-text through the backend (`POST /agent/transcribe`); the model and key stay server-side. */
export async function transcribe(cfg: AgentConfig, filePath: string, opts: { language?: string; fetchImpl?: typeof fetch } = {}): Promise<string> {
  if (!cfg.token) throw new Error("missing token: set OPENCLICKY_TOKEN or pass --token");
  const f = opts.fetchImpl ?? fetch;
  const res = await f(`${cfg.backendUrl}/agent/transcribe`, {
    method: "POST",
    headers: backendHeaders(cfg, { "content-type": "application/json" }),
    body: JSON.stringify({
      audio: fs.readFileSync(filePath).toString("base64"),
      mime: MIME[path.extname(filePath).toLowerCase()] ?? "audio/wav",
      ...(opts.language ? { language: opts.language } : {}),
    }),
  });
  if (!res.ok) throw new Error(`backend ${res.status}: ${(await res.text()).slice(0, 500)}`);
  const json = (await res.json()) as { text?: string };
  return (json.text ?? "").trim();
}
