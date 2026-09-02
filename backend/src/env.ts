import type { Context } from "hono";

/**
 * Backend configuration. On Cloudflare Workers these come from `wrangler secret` / `.dev.vars`;
 * on Node they come from `process.env` (populated from `.dev.vars` by `node.ts`).
 */
export type Env = {
  OPENAI_API_KEY?: string;
  OPENAI_BASE_URL?: string;
  OPENAI_MODEL?: string;
  ANTHROPIC_API_KEY?: string;
  ANTHROPIC_BASE_URL?: string;
  SUPABASE_URL?: string;
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
