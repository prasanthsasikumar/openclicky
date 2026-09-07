import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { chargeCredits, meterResponse, creditsForAudioSeconds, creditsForCharacters, CREDIT_COSTS, type BillingStore } from "./billing.js";

/** Fill in the server-side default model when the caller sends none (or the sentinel "default"). */
export function applyModelDefault(body: Record<string, unknown>, model?: string): Record<string, unknown> {
  if (model && (body.model === undefined || body.model === "default")) return { ...body, model };
  return body;
}

/**
 * Map client model names onto what the upstream expects. Aggregators like OpenRouter want
 * `openai/gpt-5.5` or `anthropic/claude-sonnet-4.6` while clients send plain `gpt-5.5` /
 * `claude-sonnet-4-6`. `aliases` is "from=to,from=to"; `prefix` is prepended to names that have no "/".
 */
export function mapModelName(model: unknown, aliases?: string, prefix?: string): unknown {
  if (typeof model !== "string" || !model) return model;
  const aliasMap = new Map(
    (aliases ?? "")
      .split(",")
      .map((pair) => pair.trim())
      .filter(Boolean)
      .map((pair) => {
        const [from, to] = pair.split("=").map((s) => s.trim());
        return [from, to] as [string, string];
      }),
  );
  if (aliasMap.has(model)) return aliasMap.get(model);
  if (prefix && !model.includes("/")) return prefix + model;
  return model;
}

function applyModelMapping(body: Record<string, unknown>, aliases?: string, prefix?: string): Record<string, unknown> {
  const mapped = mapModelName(body.model, aliases, prefix);
  return mapped === body.model ? body : { ...body, model: mapped };
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

/**
 * OpenAI-compatible proxy. `upstreamPath` is `/chat/completions` or `/responses`. Runs on the
 * request's own key (BYOK) or the backend's; metered users are charged from the usage the
 * upstream reports once the stream has gone by.
 */
export async function proxyOpenAI(c: Context, upstreamPath: string, store?: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.openaiKey) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  let body = applyModelDefault(await readJson(c), env.OPENAI_MODEL);
  if (keys.mapModels) body = applyModelMapping(body, env.MODEL_ALIASES, env.OPENAI_MODEL_PREFIX);
  // A streamed chat completion only reports token usage in its last chunk when asked to.
  if (store && !keys.byok && upstreamPath === "/chat/completions" && body.stream === true && body.stream_options === undefined) {
    body = { ...body, stream_options: { include_usage: true } };
  }
  const upstream = await fetch(keys.openaiBase + upstreamPath, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      authorization: `Bearer ${keys.openaiKey}`,
    },
    body: JSON.stringify(body),
  });
  return meterResponse(c, upstream, "/v1" + upstreamPath, typeof body.model === "string" ? body.model : undefined, store, () => CREDIT_COSTS.flatTokenFallback);
}

/**
 * Mint an ephemeral OpenAI Realtime client secret so a voice client can open the WebSocket/WebRTC
 * session itself without ever holding the real key (HeyClicky's /agent/realtime/session). The
 * Realtime socket bypasses the backend, so a metered user is charged a flat rate per session.
 */
export async function createRealtimeSession(c: Context, store?: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.openaiKey) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const req = await readJson(c);
  const session: Record<string, unknown> = {
    type: "realtime",
    model: env.OPENAI_REALTIME_MODEL || "gpt-realtime",
    ...(req.instructions ? { instructions: req.instructions } : {}),
    ...(req.voice ? { audio: { output: { voice: req.voice } } } : {}),
  };
  const upstream = await fetch(keys.openaiBase + "/realtime/client_secrets", {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${keys.openaiKey}` },
    body: JSON.stringify({ expires_after: { anchor: "created_at", seconds: Number(req.expiresInSeconds ?? 600) }, session }),
  });
  if (upstream.ok) {
    chargeCredits(c, store, { route: "/agent/realtime/session", model: String(session.model), input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: CREDIT_COSTS.realtimeSession });
  }
  return passthrough(upstream);
}

/**
 * Speech-to-text. Accepts JSON `{ audio: <base64>, mime?: "audio/wav", language?, prompt? }` and
 * builds the multipart upload server-side so the model choice and key stay here.
 */
export async function transcribeAudio(c: Context, store?: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.openaiKey) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const req = await readJson(c);
  if (typeof req.audio !== "string" || !req.audio) return c.json({ error: "body must be JSON with base64 `audio`" }, 400);
  const mime = typeof req.mime === "string" ? req.mime : "audio/wav";
  const ext = mime.includes("mp3") || mime.includes("mpeg") ? "mp3" : mime.includes("webm") ? "webm" : mime.includes("m4a") || mime.includes("mp4") ? "m4a" : "wav";
  const bytes = Uint8Array.from(atob(req.audio), (ch) => ch.charCodeAt(0));
  const form = new FormData();
  form.append("file", new Blob([bytes], { type: mime }), `audio.${ext}`);
  const transcribeModel = env.OPENAI_TRANSCRIBE_MODEL || "gpt-4o-mini-transcribe";
  form.append("model", transcribeModel);
  form.append("response_format", "json");
  if (typeof req.language === "string") form.append("language", req.language);
  if (typeof req.prompt === "string") form.append("prompt", req.prompt);
  const upstream = await fetch(keys.openaiBase + "/audio/transcriptions", {
    method: "POST",
    headers: { authorization: `Bearer ${keys.openaiKey}` },
    body: form,
  });
  if (!upstream.ok) return c.json({ error: `transcription upstream ${upstream.status}: ${(await upstream.text()).slice(0, 300)}` }, 502);
  const json = (await upstream.json()) as { text?: string };
  // WAV from the app is 16 kHz mono PCM16 (32 kB/s); compressed uploads are guessed at 16 kB/s.
  const audioSeconds = mime === "audio/wav" ? Math.max(0, bytes.length - 44) / 32000 : bytes.length / 16000;
  chargeCredits(c, store, { route: "/agent/transcribe", model: transcribeModel, input_tokens: 0, output_tokens: 0, audio_seconds: audioSeconds, characters: 0, credits: creditsForAudioSeconds(audioSeconds) });
  return c.json({ text: json.text ?? "" });
}

/**
 * Text-to-speech for the native shell (upstream Clicky's `/tts` contract: JSON `{ text, ... }` in,
 * `audio/mpeg` out). Uses ElevenLabs when configured, otherwise OpenAI speech.
 */
export async function synthesizeSpeech(c: Context, store?: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  const req = await readJson(c);
  const text = typeof req.text === "string" ? req.text : "";
  if (!text.trim()) return c.json({ error: "body must be JSON with `text`" }, 400);
  const charge = (model: string) =>
    chargeCredits(c, store, { route: "/tts", model, input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: text.length, credits: creditsForCharacters(text.length) });
  // ElevenLabs is the backend's own account: a bring-your-own-key user gets OpenAI speech on their key.
  if (env.ELEVENLABS_API_KEY && !keys.byok) {
    const voiceId = env.ELEVENLABS_VOICE_ID || "21m00Tcm4TlvDq8ikWAM";
    const base = (env.ELEVENLABS_BASE_URL || "https://api.elevenlabs.io").replace(/\/$/, "");
    const modelId = typeof req.model_id === "string" ? req.model_id : "eleven_flash_v2_5";
    const upstream = await fetch(`${base}/v1/text-to-speech/${voiceId}`, {
      method: "POST",
      headers: { "xi-api-key": env.ELEVENLABS_API_KEY, "content-type": "application/json", accept: "audio/mpeg" },
      body: JSON.stringify({ text, model_id: modelId, voice_settings: req.voice_settings ?? { stability: 0.5, similarity_boost: 0.75 } }),
    });
    if (upstream.ok) charge(modelId);
    return passthrough(upstream);
  }
  if (!keys.openaiKey) return c.json({ error: "backend missing ELEVENLABS_API_KEY or OPENAI_API_KEY" }, 502);
  const ttsModel = env.OPENAI_TTS_MODEL || "gpt-4o-mini-tts";
  const upstream = await fetch(keys.openaiBase + "/audio/speech", {
    method: "POST",
    headers: { authorization: `Bearer ${keys.openaiKey}`, "content-type": "application/json" },
    body: JSON.stringify({ model: ttsModel, voice: env.OPENAI_TTS_VOICE || "marin", input: text, response_format: "mp3" }),
  });
  if (upstream.ok) charge(ttsModel);
  return passthrough(upstream);
}

/** Short-lived AssemblyAI streaming token for the native shell's push-to-talk (upstream `/transcribe-token`). */
export async function assemblyAiToken(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.ASSEMBLYAI_API_KEY) return c.json({ error: "backend missing ASSEMBLYAI_API_KEY; use the openai or apple transcription provider" }, 501);
  const base = (env.ASSEMBLYAI_BASE_URL || "https://streaming.assemblyai.com").replace(/\/$/, "");
  const upstream = await fetch(`${base}/v3/token?expires_in_seconds=480`, { headers: { authorization: env.ASSEMBLYAI_API_KEY } });
  return passthrough(upstream);
}

/** Anthropic Messages proxy (the gate lane and the app's `/chat` teacher lane). */
export async function proxyAnthropic(c: Context, store?: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (keys.byok && !keys.anthropicKey) return c.json({ error: "byok_missing_anthropic_key" }, 402);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  let body = applyModelDefault(await readJson(c), keys.anthropicDefaultModel);
  if (keys.mapModels) body = applyModelMapping(body, env.MODEL_ALIASES, env.ANTHROPIC_MODEL_PREFIX);
  const upstream = await fetch(keys.anthropicBase + "/v1/messages", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      "x-api-key": keys.anthropicKey,
      "anthropic-version": c.req.header("anthropic-version") ?? "2023-06-01",
    },
    body: JSON.stringify(body),
  });
  return meterResponse(c, upstream, new URL(c.req.url).pathname, typeof body.model === "string" ? body.model : undefined, store, () => CREDIT_COSTS.flatTokenFallback);
}
