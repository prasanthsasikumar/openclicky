import { describe, it, expect } from "vitest";
import { SignJWT } from "jose";
import {
  verifySupabaseJwt,
  issueSessionToken,
  verifySessionToken,
  authenticate,
  AuthError,
  bearerFrom,
} from "../src/auth.js";
import type { Env } from "../src/env.js";

const env: Env = {
  SUPABASE_JWT_SECRET: "supabase-test-secret-supabase-test-secret",
  SESSION_TOKEN_SECRET: "session-secret-session-secret-session",
  SESSION_TOKEN_TTL_SECONDS: "60",
};
const key = (s: string) => new TextEncoder().encode(s);

async function supabaseJwt(over: Record<string, unknown> = {}, secret = env.SUPABASE_JWT_SECRET!) {
  return new SignJWT({ role: "authenticated", email: "dev@example.com", ...over })
    .setProtectedHeader({ alg: "HS256" })
    .setAudience("authenticated")
    .setSubject("user-123")
    .setIssuedAt()
    .setExpirationTime("10m")
    .sign(key(secret));
}

describe("verifySupabaseJwt", () => {
  /** The keys Supabase signs with the same secret but which are not a user: both are handed out
   *  publicly or to servers, and neither carries a subject. */
  async function keyWithoutASubject(claims: Record<string, unknown>) {
    return new SignJWT(claims)
      .setProtectedHeader({ alg: "HS256" })
      .setAudience("authenticated")
      .setIssuedAt()
      .setExpirationTime("10m")
      .sign(key(env.SUPABASE_JWT_SECRET!));
  }

  it("rejects the project's anon key, which ships in every client", async () => {
    // It verifies against the same secret and has no `sub`; before this check every holder of the
    // public anon key authenticated as one shared user literally named "undefined".
    const anon = await keyWithoutASubject({ role: "anon" });
    await expect(verifySupabaseJwt(anon, env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects the service_role key", async () => {
    const serviceRole = await keyWithoutASubject({ role: "service_role" });
    await expect(verifySupabaseJwt(serviceRole, env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects a token whose role is not authenticated even when it has a subject", async () => {
    await expect(verifySupabaseJwt(await supabaseJwt({ role: "anon" }), env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects a token minted for another audience", async () => {
    const otherAudience = await new SignJWT({ role: "authenticated" })
      .setProtectedHeader({ alg: "HS256" })
      .setAudience("some-other-service")
      .setSubject("user-123")
      .setIssuedAt()
      .setExpirationTime("10m")
      .sign(key(env.SUPABASE_JWT_SECRET!));
    await expect(verifySupabaseJwt(otherAudience, env)).rejects.toBeInstanceOf(AuthError);
  });

  it("accepts a JWT signed with SUPABASE_JWT_SECRET", async () => {
    const p = await verifySupabaseJwt(await supabaseJwt(), env);
    expect(p).toMatchObject({ sub: "user-123", email: "dev@example.com", via: "supabase" });
  });
  it("rejects a JWT signed with another secret", async () => {
    await expect(
      verifySupabaseJwt(await supabaseJwt({}, "wrong-secret-wrong-secret-wrong-secret"), env),
    ).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects an expired JWT", async () => {
    const expired = await new SignJWT({ role: "authenticated" })
      .setProtectedHeader({ alg: "HS256" })
      .setAudience("authenticated")
      .setSubject("u")
      .setExpirationTime(Math.floor(Date.now() / 1000) - 120)
      .sign(key(env.SUPABASE_JWT_SECRET!));
    await expect(verifySupabaseJwt(expired, env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects garbage", async () => {
    await expect(verifySupabaseJwt("not-a-jwt", env)).rejects.toBeInstanceOf(AuthError);
  });
});

describe("session tokens", () => {
  it("round-trips", async () => {
    const { token, expiresAt } = await issueSessionToken({ sub: "user-123", email: "dev@example.com" }, env);
    expect(expiresAt).toBeGreaterThan(Date.now() / 1000);
    const p = await verifySessionToken(token, env);
    expect(p).toMatchObject({ sub: "user-123", email: "dev@example.com", via: "session" });
  });
  it("is not accepted as a Supabase JWT and vice versa", async () => {
    const { token } = await issueSessionToken({ sub: "u" }, env);
    await expect(verifySupabaseJwt(token, env)).rejects.toBeInstanceOf(AuthError);
    await expect(verifySessionToken(await supabaseJwt(), env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects a session token signed with the Supabase secret", async () => {
    const forged = await new SignJWT({})
      .setProtectedHeader({ alg: "HS256", typ: "openclicky-session" })
      .setSubject("u")
      .setIssuer("openclicky-backend")
      .setExpirationTime("5m")
      .sign(key(env.SUPABASE_JWT_SECRET!));
    await expect(verifySessionToken(forged, env)).rejects.toBeInstanceOf(AuthError);
  });
});

describe("authenticate", () => {
  it("accepts either token kind", async () => {
    expect((await authenticate(await supabaseJwt(), env)).via).toBe("supabase");
    const { token } = await issueSessionToken({ sub: "u" }, env);
    expect((await authenticate(token, env)).via).toBe("session");
  });
  it("fails without secrets configured", async () => {
    await expect(authenticate(await supabaseJwt(), {})).rejects.toBeInstanceOf(AuthError);
  });
});

describe("bearerFrom", () => {
  it("parses Bearer header", () => {
    expect(bearerFrom("Bearer abc")).toBe("abc");
    expect(bearerFrom("bearer abc")).toBe("abc");
    expect(bearerFrom(undefined)).toBeUndefined();
    expect(bearerFrom("Basic x")).toBeUndefined();
  });
});
