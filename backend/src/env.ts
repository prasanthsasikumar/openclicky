import type { Context } from "hono";

/**
 * Backend configuration. On Cloudflare Workers these come from `wrangler secret` / `.dev.vars`;
 * on Node they come from `process.env` (populated from `.dev.vars` by `node.ts`).
 */
export type Env = {
  OPENAI_API_KEY?: string;
  OPENAI_BASE_URL?: string;
  OPENAI_MODEL?: string;
  /** Realtime voice model minted by POST /agent/realtime/session (default gpt-realtime). */
  OPENAI_REALTIME_MODEL?: string;
  /** Speech-to-text model used by POST /agent/transcribe (default gpt-4o-mini-transcribe). */
  OPENAI_TRANSCRIBE_MODEL?: string;
  /** Model used by POST /skills/create to draft a SKILL.md (default: OPENAI_MODEL). */
  SKILL_CREATE_MODEL?: string;
  ANTHROPIC_API_KEY?: string;
  ANTHROPIC_BASE_URL?: string;
  ANTHROPIC_MODEL?: string;
  /** "from=to,from=to" model renames applied to every proxied request (e.g. for OpenRouter). */
  MODEL_ALIASES?: string;
  /** Prepended to model names without a "/" on the OpenAI / Anthropic routes (e.g. "openai/", "anthropic/"). */
  OPENAI_MODEL_PREFIX?: string;
  ANTHROPIC_MODEL_PREFIX?: string;
  /** BYOK: where a user's own keys are sent (defaults: the vendors' APIs). Overridable for tests. */
  BYOK_OPENAI_BASE_URL?: string;
  BYOK_ANTHROPIC_BASE_URL?: string;
  /** Anthropic model used for "default" on BYOK requests (Anthropic's own id, no OpenRouter prefix). */
  BYOK_ANTHROPIC_MODEL?: string;
  /** Supabase service key (server-side, bypasses RLS): enables the billing store. Without it nothing is metered. */
  SUPABASE_SERVICE_KEY?: string;
  /** Free-tier allowance for signed-in users with no subscription row (default 200). */
  FREE_MONTHLY_CREDITS?: string;
  /** Stripe (subscriptions). Checkout and portal need STRIPE_SECRET_KEY; the webhook needs STRIPE_WEBHOOK_SECRET. */
  STRIPE_SECRET_KEY?: string;
  STRIPE_WEBHOOK_SECRET?: string;
  STRIPE_SUCCESS_URL?: string;
  STRIPE_CANCEL_URL?: string;
  STRIPE_PORTAL_RETURN_URL?: string;
  /** Native shell TTS (`/tts`): ElevenLabs when set, else OpenAI speech with OPENAI_TTS_MODEL/VOICE. */
  ELEVENLABS_API_KEY?: string;
  ELEVENLABS_VOICE_ID?: string;
  ELEVENLABS_BASE_URL?: string;
  OPENAI_TTS_MODEL?: string;
  OPENAI_TTS_VOICE?: string;
  /** Native shell streaming STT (`/transcribe-token`). Optional; the shell falls back to /agent/transcribe. */
  ASSEMBLYAI_API_KEY?: string;
  ASSEMBLYAI_BASE_URL?: string;
  SUPABASE_URL?: string;
  /** Client-side Supabase key (safe to publish): handed to the app by GET /auth/config so it can sign in. */
  SUPABASE_PUBLISHABLE_KEY?: string;
  SUPABASE_JWT_SECRET?: string;
  SESSION_TOKEN_SECRET?: string;
  SESSION_TOKEN_TTL_SECONDS?: string;
};

/**
 * Merge `process.env` (Node) with request bindings (Workers, or the env passed to `app.request`).
 * Bindings win so a Worker's secrets and test envs override anything ambient.
 */
export function getEnv(c: Context): Env {
  const ambient: Env = typeof process !== "undefined" && process.env ? (process.env as Env) : {};
  const bindings = (c.env ?? {}) as Env;
  return { ...ambient, ...bindings };
}
