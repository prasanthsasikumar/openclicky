import type { Env } from "./env.js";

export const OPENAI_KEY_HEADER = "x-openclicky-openai-key";
export const ANTHROPIC_KEY_HEADER = "x-openclicky-anthropic-key";
export const GEMINI_KEY_HEADER = "x-openclicky-gemini-key";

export interface ProviderKeys {
  /** True when the request brought its own OpenAI key: use it, meter nothing. */
  byok: boolean;
  openaiKey?: string;
  /** No trailing slash. */
  openaiBase: string;
  anthropicKey?: string;
  /** No trailing slash. */
  anthropicBase: string;
  /** Free Google Gemini API key (Google AI Studio); OpenAI-compatible upstream. No paid key needed. */
  geminiKey?: string;
  /** No trailing slash. Gemini OpenAI-compatible base. */
  geminiBase: string;
  /** Apply MODEL_ALIASES / *_MODEL_PREFIX (OpenRouter naming). False for BYOK: the keys are the vendors' own. */
  mapModels: boolean;
  /** Default model for the Anthropic lanes when the client sends "default". */
  anthropicDefaultModel?: string;
}

const stripSlash = (u: string) => u.replace(/\/+$/, "");
const clean = (v: string | null | undefined) => {
  const t = (v ?? "").trim();
  return t ? t : undefined;
};

/**
 * Which keys this request runs on. A request that carries its own OpenAI key is "bring your own
 * key": it goes to the vendors' APIs with the user's keys (so OpenRouter aliases do not apply) and
 * is never metered. Anything else runs on the backend's keys and is metered by billing.ts.
 */
export function resolveProviderKeys(headers: { get(name: string): string | null | undefined }, env: Env): ProviderKeys {
  const userOpenAI = clean(headers.get(OPENAI_KEY_HEADER));
  const userAnthropic = clean(headers.get(ANTHROPIC_KEY_HEADER));
  const userGemini = clean(headers.get(GEMINI_KEY_HEADER));
  if (userOpenAI || userGemini) {
    return {
      byok: true,
      openaiKey: userOpenAI,
      openaiBase: stripSlash(env.BYOK_OPENAI_BASE_URL || "https://api.openai.com/v1"),
      anthropicKey: userAnthropic,
      anthropicBase: stripSlash(env.BYOK_ANTHROPIC_BASE_URL || "https://api.anthropic.com"),
      mapModels: false,
      anthropicDefaultModel: env.BYOK_ANTHROPIC_MODEL || "claude-haiku-4-5-20251001",
      geminiKey: userGemini,
      geminiBase: stripSlash(env.GEMINI_BASE_URL || "https://generativelanguage.googleapis.com/v1beta/openai"),
    };
  }
  return {
    byok: false,
    openaiKey: env.OPENAI_API_KEY,
    geminiKey: env.GEMINI_API_KEY ?? userGemini,
    geminiBase: stripSlash(env.GEMINI_BASE_URL || "https://generativelanguage.googleapis.com/v1beta/openai"),
    openaiBase: stripSlash(env.OPENAI_BASE_URL || "https://api.openai.com/v1"),
    anthropicKey: env.ANTHROPIC_API_KEY,
    anthropicBase: stripSlash(env.ANTHROPIC_BASE_URL || "https://api.anthropic.com"),
    mapModels: true,
    anthropicDefaultModel: env.ANTHROPIC_MODEL,
  };
}
