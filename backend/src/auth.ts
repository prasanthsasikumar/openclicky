import { SignJWT, jwtVerify, createRemoteJWKSet, decodeProtectedHeader } from "jose";
import type { MiddlewareHandler } from "hono";
import { getEnv, type Env } from "./env.js";

export class AuthError extends Error {
  status = 401;
}

export type Principal = { sub: string; email?: string; via: "supabase" | "session" };

/** `typ` header that marks tokens minted by this backend (vs. Supabase user JWTs). */
const SESSION_TYP = "openclicky-session";
const SESSION_ISSUER = "openclicky-backend";
const enc = (s: string) => new TextEncoder().encode(s);

export function bearerFrom(header: string | undefined): string | undefined {
  if (!header) return undefined;
  const m = /^Bearer\s+(.+)$/i.exec(header.trim());
  return m?.[1];
}

function safeHeader(token: string) {
  try {
    return decodeProtectedHeader(token);
  } catch {
    throw new AuthError("malformed token");
  }
}

/**
 * Verify a Supabase user JWT. Uses HS256 with SUPABASE_JWT_SECRET when configured (legacy Supabase
 * signing), otherwise the project's JWKS endpoint (asymmetric keys).
 */
export async function verifySupabaseJwt(token: string, env: Env): Promise<Principal> {
  const header = safeHeader(token);
  if (header.typ === SESSION_TYP) throw new AuthError("session token is not a Supabase JWT");
  try {
    // The token's algorithm picks the path: HS256 tokens (legacy secret, dev-minted JWTs) verify
    // with the shared secret; anything else (self-hosted Supabase signing with an asymmetric key,
    // e.g. ES256) verifies against the project's JWKS. Both may be configured at once.
    if (env.SUPABASE_JWT_SECRET && (header.alg === "HS256" || !env.SUPABASE_URL)) {
      const { payload } = await jwtVerify(token, enc(env.SUPABASE_JWT_SECRET), { algorithms: ["HS256"] });
      return { sub: String(payload.sub), email: payload.email as string | undefined, via: "supabase" };
    }
    if (env.SUPABASE_URL) {
      const jwks = createRemoteJWKSet(new URL("/auth/v1/.well-known/jwks.json", env.SUPABASE_URL));
      const { payload } = await jwtVerify(token, jwks);
      return { sub: String(payload.sub), email: payload.email as string | undefined, via: "supabase" };
    }
  } catch (e) {
    if (e instanceof AuthError) throw e;
    throw new AuthError(`invalid Supabase JWT: ${(e as Error).message}`);
  }
  throw new AuthError("backend has no SUPABASE_JWT_SECRET or SUPABASE_URL configured");
}

export async function issueSessionToken(
  p: { sub: string; email?: string },
  env: Env,
): Promise<{ token: string; expiresAt: number }> {
  if (!env.SESSION_TOKEN_SECRET) throw new AuthError("SESSION_TOKEN_SECRET not configured");
  const ttl = Number(env.SESSION_TOKEN_TTL_SECONDS ?? 3600);
  const expiresAt = Math.floor(Date.now() / 1000) + ttl;
  const token = await new SignJWT({ email: p.email })
    .setProtectedHeader({ alg: "HS256", typ: SESSION_TYP })
    .setSubject(p.sub)
    .setIssuedAt()
    .setExpirationTime(expiresAt)
    .setIssuer(SESSION_ISSUER)
    .sign(enc(env.SESSION_TOKEN_SECRET));
  return { token, expiresAt };
}

export async function verifySessionToken(token: string, env: Env): Promise<Principal> {
  if (!env.SESSION_TOKEN_SECRET) throw new AuthError("SESSION_TOKEN_SECRET not configured");
  const header = safeHeader(token);
  if (header.typ !== SESSION_TYP) throw new AuthError("not a session token");
  try {
    const { payload } = await jwtVerify(token, enc(env.SESSION_TOKEN_SECRET), {
      algorithms: ["HS256"],
      issuer: SESSION_ISSUER,
    });
    return { sub: String(payload.sub), email: payload.email as string | undefined, via: "session" };
  } catch (e) {
    throw new AuthError(`invalid session token: ${(e as Error).message}`);
  }
}

/** Accept either an exchanged session token or a raw Supabase JWT. */
export async function authenticate(token: string, env: Env): Promise<Principal> {
  const header = safeHeader(token);
  return header.typ === SESSION_TYP ? verifySessionToken(token, env) : verifySupabaseJwt(token, env);
}

export const requireAuth: MiddlewareHandler<{ Variables: { principal: Principal } }> = async (c, next) => {
  const token = bearerFrom(c.req.header("authorization"));
  if (!token) return c.json({ error: "missing Authorization: Bearer <token>" }, 401);
  try {
    c.set("principal", await authenticate(token, getEnv(c)));
  } catch (e) {
    return c.json({ error: e instanceof Error ? e.message : "unauthorized" }, 401);
  }
  await next();
};
