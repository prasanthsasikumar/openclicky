import type { AgentConfig } from "./config.js";

/**
 * Own keys are the primary route: a call the person has a key for goes straight to the vendor, with
 * the same request the backend's bring-your-own-key path would have sent (backend/src/proxy.ts,
 * keys.ts). The OpenClicky backend is only for a capability with no own key, on the person's account
 * token, and then without the own keys: the key never leaves this Mac except to its vendor.
 */
export const OPENAI_DIRECT_BASE = "https://api.openai.com/v1";
export const ANTHROPIC_DIRECT_BASE = "https://api.anthropic.com";
/** Google Gemini API (free tier; OpenAI-compatible). https://aistudio.google.com/apikey */
export const GEMINI_DIRECT_BASE = "https://generativelanguage.googleapis.com/v1beta/openai";
export const GEMINI_DEFAULT_MODEL = "gemini-2.5-flash";
/** Local Ollama server (optional; no key). */
export const OLLAMA_DEFAULT_BASE = "http://localhost:11434";
export const ANTHROPIC_VERSION = "2023-06-01";

/** The models the backend's BYOK path picks when a client sends "default" (keys.ts, proxy.ts, .dev.vars.example). */
export const DIRECT_DEFAULTS = {
  openaiModel: "gpt-5.5",
  anthropicModel: "claude-haiku-4-5-20251001",
  geminiModel: GEMINI_DEFAULT_MODEL,
  realtimeModel: "gpt-realtime",
  transcribeModel: "gpt-4o-mini-transcribe",
} as const;

export interface Route {
  url: string;
  headers: Record<string, string>;
  /** True when the call goes to the vendor on the person's own key. */
  direct: boolean;
}

type Creds = Pick<AgentConfig, "backendUrl" | "token" | "openaiApiKey" | "anthropicApiKey" | "geminiApiKey" | "ollamaBaseUrl" | "ollamaModel">;

const backendRoute = (cfg: Creds, backendPath: string, extra: Record<string, string>): Route | null =>
  cfg.token ? { url: `${cfg.backendUrl}${backendPath}`, headers: { ...extra, authorization: `Bearer ${cfg.token}` }, direct: false } : null;

/** OpenAI capabilities: the person's OpenAI key straight to OpenAI, else the backend on their token, else null. */
export function openaiRoute(cfg: Creds, backendPath: string, openaiPath: string, extra: Record<string, string> = {}): Route | null {
  if (cfg.openaiApiKey) return { url: OPENAI_DIRECT_BASE + openaiPath, headers: { ...extra, authorization: `Bearer ${cfg.openaiApiKey}` }, direct: true };
  return backendRoute(cfg, backendPath, extra);
}

/** Claude capabilities: the person's Anthropic key straight to Anthropic, else the backend on their token, else null. */
export function anthropicRoute(cfg: Creds, backendPath: string, extra: Record<string, string> = {}): Route | null {
  if (cfg.anthropicApiKey) {
    return { url: `${ANTHROPIC_DIRECT_BASE}/v1/messages`, headers: { ...extra, "x-api-key": cfg.anthropicApiKey, "anthropic-version": ANTHROPIC_VERSION }, direct: true };
  }
  return backendRoute(cfg, backendPath, extra);
}

/**
 * Chat-completions route for the ask/gate lanes. Prefers a free Google Gemini key, then a local
 * Ollama server, then an own OpenAI key, then the OpenClicky backend. No paid key is required.
 */
export function chatRoute(cfg: Creds, backendPath = "/v1/chat/completions", extra: Record<string, string> = {}): Route | null {
  const headers = { ...extra, "content-type": "application/json" };
  if (cfg.geminiApiKey) return { url: GEMINI_DIRECT_BASE + "/chat/completions", headers: { ...headers, authorization: `Bearer ${cfg.geminiApiKey}` }, direct: true };
  if (cfg.ollamaBaseUrl) return { url: stripSlash(cfg.ollamaBaseUrl) + "/v1/chat/completions", headers, direct: true };
  return openaiRoute(cfg, backendPath, "/chat/completions", extra);
}

/** Default model for a chat route: Gemini / Ollama local / OpenAI / backend "default". */
export function chatModel(cfg: Creds, route: Route): string {
  if (cfg.geminiApiKey && route.direct) return DIRECT_DEFAULTS.geminiModel;
  if (cfg.ollamaBaseUrl && route.direct) return cfg.ollamaModel ?? "llama3.2";
  if (cfg.openaiApiKey && route.direct) return DIRECT_DEFAULTS.openaiModel;
  return "default";
}

const stripSlash = (u: string) => u.replace(/\/+$/, "");

/** What a capability with no route needs, said plainly. */
export const needsOpenAI = "needs your own OpenAI key (OPENCLICKY_OPENAI_KEY) or an OpenClicky token (OPENCLICKY_TOKEN / --token); a free Google Gemini key (OPENCLICKY_GEMINI_KEY / GEMINI_API_KEY) or a local Ollama server (OPENCLICKY_OLLAMA_BASE_URL) also works";
export const needsAnthropic = "needs your own Anthropic key (OPENCLICKY_ANTHROPIC_KEY) or an OpenClicky token (OPENCLICKY_TOKEN / --token)";

/** The OpenAI Realtime client-secret request the backend sends (createRealtimeSession in proxy.ts). */
export function realtimeSecretBody(opts: { voice?: string; instructions?: string; expiresInSeconds?: number }) {
  return {
    expires_after: { anchor: "created_at", seconds: opts.expiresInSeconds ?? 600 },
    session: {
      type: "realtime",
      model: DIRECT_DEFAULTS.realtimeModel,
      ...(opts.instructions ? { instructions: opts.instructions } : {}),
      ...(opts.voice ? { audio: { output: { voice: opts.voice } } } : {}),
    },
  };
}

