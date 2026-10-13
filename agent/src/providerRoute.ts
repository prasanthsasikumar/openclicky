import type { AgentConfig } from "./config.js";

/**
 * Own keys are the primary route: a call the person has a key for goes straight to the vendor, with
 * the same request the backend's bring-your-own-key path would have sent (backend/src/proxy.ts,
 * keys.ts). The OpenClicky backend is only for a capability with no own key, on the person's account
 * token, and then without the own keys: the key never leaves this Mac except to its vendor.
 */
export const OPENAI_DIRECT_BASE = "https://api.openai.com/v1";
export const ANTHROPIC_DIRECT_BASE = "https://api.anthropic.com";
export const ANTHROPIC_VERSION = "2023-06-01";

/** The models the backend's BYOK path picks when a client sends "default" (keys.ts, proxy.ts, .dev.vars.example). */
export const DIRECT_DEFAULTS = {
  openaiModel: "gpt-5.5",
  anthropicModel: "claude-haiku-4-5-20251001",
  realtimeModel: "gpt-realtime",
  transcribeModel: "gpt-4o-mini-transcribe",
} as const;

export interface Route {
  url: string;
  headers: Record<string, string>;
  /** True when the call goes to the vendor on the person's own key. */
  direct: boolean;
}

type Creds = Pick<AgentConfig, "backendUrl" | "token" | "openaiApiKey" | "anthropicApiKey">;

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

/** What a capability with no route needs, said plainly. */
export const needsOpenAI = "needs your own OpenAI key (OPENCLICKY_OPENAI_KEY) or an OpenClicky token (OPENCLICKY_TOKEN / --token)";
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

