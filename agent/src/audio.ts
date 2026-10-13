import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import type { AgentConfig } from "./config.js";
import { openaiRoute, needsOpenAI, DIRECT_DEFAULTS } from "./providerRoute.js";

export interface RecordOptions {
  seconds: number;
  outPath?: string;
  /** AVFoundation audio device index (macOS). Default 0 = system default microphone. */
  device?: string;
  ffmpegBin?: string;
}

/** ffmpeg's microphone input: AVFoundation `:<index>` on macOS, DirectShow `audio=<device name>` on Windows. */
export function ffmpegMicInput(device: string | undefined, platform: NodeJS.Platform = process.platform): string[] {
  if (platform === "win32") return ["-f", "dshow", "-i", `audio=${device ?? ""}`];
  return ["-f", "avfoundation", "-i", `:${device ?? "0"}`];
}

/**
 * The DirectShow audio devices in the order `ffmpeg -list_devices true -f dshow -i dummy` prints
 * them (lines like `[dshow @ 0x…] "Microphone (Realtek Audio)" (audio)`).
 */
export function parseDshowAudioDevices(listing: string): string[] {
  return Array.from(listing.matchAll(/"([^"]+)"\s+\(audio\)/g), (match) => match[1]);
}

/**
 * Windows: DirectShow has no "default" device index, so a numeric `--device` (the default "0")
 * picks that entry of ffmpeg's device list; a name is used as given. Elsewhere `device` is returned
 * unchanged.
 */
export function resolveMicDevice(ffmpegBin: string, device: string | undefined, platform: NodeJS.Platform = process.platform): string | undefined {
  if (platform !== "win32" || (device !== undefined && !/^\d+$/.test(device))) return device;
  const r = spawnSync(ffmpegBin, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], { encoding: "utf8", windowsHide: true });
  if (r.error) throw new Error(`${ffmpegBin} failed to start: ${r.error.message} (install with \`winget install ffmpeg\`)`);
  const devices = parseDshowAudioDevices(`${r.stderr ?? ""}\n${r.stdout ?? ""}`);
  const name = devices[Number(device ?? "0")];
  if (!name) throw new Error(`no microphone ${device ?? "0"} among ffmpeg's DirectShow audio devices (${devices.length} found); pass --device "<name>"`);
  return name;
}

/** ffmpeg arguments for a mono 16 kHz WAV capture from the microphone (AVFoundation / DirectShow). */
export function ffmpegRecordArgs(opts: { seconds: number; outPath: string; device?: string; platform?: NodeJS.Platform }): string[] {
  return ["-hide_banner", "-loglevel", "error", "-y", ...ffmpegMicInput(opts.device, opts.platform), "-t", String(opts.seconds), "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", opts.outPath];
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
  const windows = process.platform === "win32";
  const device = windows ? resolveMicDevice(bin, opts.device) : opts.device;
  const r = spawnSync(bin, ffmpegRecordArgs({ seconds: opts.seconds, outPath, device }), { encoding: "utf8" });
  if (r.error) throw new Error(`${bin} failed to start: ${r.error.message} (install with \`${windows ? "winget install ffmpeg" : "brew install ffmpeg"}\`)`);
  if (r.status !== 0 || !fs.existsSync(outPath) || fs.statSync(outPath).size === 0) {
    throw new Error(
      `${bin} recording failed (exit ${r.status}): ${(r.stderr || r.stdout || "").trim() || "no output"}. ` +
        (windows
          ? "Allow desktop apps to use the microphone in Settings → Privacy & security → Microphone."
          : "Grant Microphone permission to your terminal in System Settings → Privacy & Security → Microphone."),
    );
  }
  return outPath;
}

const MIME: Record<string, string> = { ".wav": "audio/wav", ".mp3": "audio/mpeg", ".m4a": "audio/m4a", ".webm": "audio/webm", ".ogg": "audio/ogg" };

/**
 * Speech-to-text. With the person's own OpenAI key: the multipart upload the backend would have
 * built, straight to OpenAI. Otherwise `POST /agent/transcribe` on their token.
 */
export async function transcribe(cfg: AgentConfig, filePath: string, opts: { language?: string; fetchImpl?: typeof fetch } = {}): Promise<string> {
  const route = openaiRoute(cfg, "/agent/transcribe", "/audio/transcriptions");
  if (!route) throw new Error(`transcription ${needsOpenAI}`);
  const f = opts.fetchImpl ?? fetch;
  const mime = MIME[path.extname(filePath).toLowerCase()] ?? "audio/wav";
  const audio = fs.readFileSync(filePath);
  let body: BodyInit;
  if (route.direct) {
    const form = new FormData();
    form.append("file", new Blob([audio], { type: mime }), `audio${path.extname(filePath) || ".wav"}`);
    form.append("model", DIRECT_DEFAULTS.transcribeModel);
    form.append("response_format", "json");
    if (opts.language) form.append("language", opts.language);
    body = form;
  } else {
    route.headers["content-type"] = "application/json";
    body = JSON.stringify({ audio: audio.toString("base64"), mime, ...(opts.language ? { language: opts.language } : {}) });
  }
  const res = await f(route.url, { method: "POST", headers: route.headers, body });
  if (!res.ok) throw new Error(`${route.direct ? "openai" : "backend"} ${res.status}: ${(await res.text()).slice(0, 500)}`);
  const json = (await res.json()) as { text?: string };
  return (json.text ?? "").trim();
}
