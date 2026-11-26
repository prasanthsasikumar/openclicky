import type { Context, MiddlewareHandler } from "hono";
import { getEnv, type Env } from "./env.js";
import { requireAuth, type Principal } from "./auth.js";
import { OPENAI_KEY_HEADER, GEMINI_KEY_HEADER } from "./keys.js";
import { addressBlocks, ipOf, keyedHash } from "./accountAuth.js";

/**
 * Own key, no account (issue #4). A request that brings its own OpenAI key and no token runs as an
 * anonymous "byok" principal: it reaches only the model routes, on the user's keys (resolveProviderKeys
 * switches on the same header, so the backend's keys are never used), and is never metered or rate
 * limited: it is the person's key and the person's bill, and OpenClicky is free to use with one.
 *
 * A request that does carry an Authorization header is judged by that header alone: a bad token is
 * a 401, never a quiet fall back to anonymous.
 */

/** Kept for the life of the process when no secret is configured, so the subject is still not the raw address. */
let processSalt: string | undefined;
function saltFor(env: Env): string {
  const configured = env.IP_HASH_SALT || env.SESSION_TOKEN_SECRET || env.SUPABASE_SERVICE_KEY;
  if (configured) return configured;
  processSalt ??= Array.from(crypto.getRandomValues(new Uint8Array(32)), (b) => b.toString(16).padStart(2, "0")).join("");
  return processSalt;
}

export const hasOwnOpenAIKey = (c: Context) => Boolean(c.req.header(OPENAI_KEY_HEADER)?.trim() || c.req.header(GEMINI_KEY_HEADER)?.trim());
/** Alias: own paid OR free key, no account. */
export const hasOwnKey = hasOwnOpenAIKey;

/** The anonymous principal for this request: a keyed hash of the address block, never the address itself. */
export async function ownKeyPrincipal(c: Context): Promise<Principal> {
  const host = addressBlocks(ipOf(c)).host;
  return { sub: `byok:${(await keyedHash(saltFor(getEnv(c)), `byok:${host}`)).slice(0, 32)}`, via: "byok" };
}

/**
 * requireAuth for the model routes: a token is checked exactly as before; no token but an own
 * OpenAI key is let through as the anonymous byok principal.
 */
export const requireAuthOrOwnKey: MiddlewareHandler<{ Variables: { principal: Principal } }> = async (c, next) => {
  if (c.req.header("authorization") !== undefined || !hasOwnOpenAIKey(c)) return requireAuth(c, next);
  c.set("principal", await ownKeyPrincipal(c));
  await next();
};
