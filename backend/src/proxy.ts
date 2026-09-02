import type { Context } from "hono";
import { getEnv } from "./env.js";

/** Fill in the server-side default model when the caller sends none (or the sentinel "default"). */
export function applyModelDefault(body: Record<string, unknown>, model?: string): Record<string, unknown> {
  if (model && (body.model === undefined || body.model === "default")) return { ...body, model };
  return body;
}

async function readJson(c: Context): Promise<Record<string, unknown>> {
  const text = await c.req.text();
  if (!text) return {};
  try {
    return JSON.parse(text) as Record<string, unknown>;
  } catch {
    return {};
  }
}

const HOP_BY_HOP = new Set(["content-length", "connection", "keep-alive", "transfer-encoding", "content-encoding"]);

/** Stream the upstream body straight back to the client (SSE stays SSE). */
function passthrough(upstream: Response): Response {
  const headers = new Headers();
  upstream.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase())) headers.set(k, v);
  });
  return new Response(upstream.body, { status: upstream.status, headers });
}

/** OpenAI-compatible proxy. `upstreamPath` is `/chat/completions` or `/responses`. */
export async function proxyOpenAI(c: Context, upstreamPath: string): Promise<Response> {
  const env = getEnv(c);
  if (!env.OPENAI_API_KEY) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const base = (env.OPENAI_BASE_URL || "https://api.openai.com/v1").replace(/\/$/, "");
  const body = applyModelDefault(await readJson(c), env.OPENAI_MODEL);
  const upstream = await fetch(base + upstreamPath, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
    },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}

/**
 * Mint an ephemeral OpenAI Realtime client secret so a voice client can open the WebSocket/WebRTC
 * session itself without ever holding the real key (HeyClicky's /agent/realtime/session).
 */
export async function createRealtimeSession(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.OPENAI_API_KEY) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const base = (env.OPENAI_BASE_URL || "https://api.openai.com/v1").replace(/\/$/, "");
  const req = await readJson(c);
  const session: Record<string, unknown> = {
    type: "realtime",
    model: env.OPENAI_REALTIME_MODEL || "gpt-realtime",
    ...(req.instructions ? { instructions: req.instructions } : {}),
    ...(req.voice ? { audio: { output: { voice: req.voice } } } : {}),
  };
  const upstream = await fetch(base + "/realtime/client_secrets", {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${env.OPENAI_API_KEY}` },
    body: JSON.stringify({ expires_after: { anchor: "created_at", seconds: Number(req.expiresInSeconds ?? 600) }, session }),
  });
  return passthrough(upstream);
}

/**
 * Speech-to-text. Accepts JSON `{ audio: <base64>, mime?: "audio/wav", language?, prompt? }` and
 * builds the multipart upload server-side so the model choice and key stay here.
 */
export async function transcribeAudio(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.OPENAI_API_KEY) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const base = (env.OPENAI_BASE_URL || "https://api.openai.com/v1").replace(/\/$/, "");
  const req = await readJson(c);
  if (typeof req.audio !== "string" || !req.audio) return c.json({ error: "body must be JSON with base64 `audio`" }, 400);
  const mime = typeof req.mime === "string" ? req.mime : "audio/wav";
  const ext = mime.includes("mp3") || mime.includes("mpeg") ? "mp3" : mime.includes("webm") ? "webm" : mime.includes("m4a") || mime.includes("mp4") ? "m4a" : "wav";
  const bytes = Uint8Array.from(atob(req.audio), (ch) => ch.charCodeAt(0));
  const form = new FormData();
  form.append("file", new Blob([bytes], { type: mime }), `audio.${ext}`);
  form.append("model", env.OPENAI_TRANSCRIBE_MODEL || "gpt-4o-mini-transcribe");
  form.append("response_format", "json");
  if (typeof req.language === "string") form.append("language", req.language);
  if (typeof req.prompt === "string") form.append("prompt", req.prompt);
  const upstream = await fetch(base + "/audio/transcriptions", {
    method: "POST",
    headers: { authorization: `Bearer ${env.OPENAI_API_KEY}` },
    body: form,
  });
  if (!upstream.ok) return c.json({ error: `transcription upstream ${upstream.status}: ${(await upstream.text()).slice(0, 300)}` }, 502);
  const json = (await upstream.json()) as { text?: string };
  return c.json({ text: json.text ?? "" });
}

/** Anthropic Messages proxy. */
export async function proxyAnthropic(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.ANTHROPIC_API_KEY) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const base = (env.ANTHROPIC_BASE_URL || "https://api.anthropic.com").replace(/\/$/, "");
  const body = applyModelDefault(await readJson(c), env.ANTHROPIC_MODEL);
  const upstream = await fetch(base + "/v1/messages", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      "x-api-key": env.ANTHROPIC_API_KEY,
      "anthropic-version": c.req.header("anthropic-version") ?? "2023-06-01",
    },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}
