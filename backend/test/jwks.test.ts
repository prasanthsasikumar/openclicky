/**
 * Supabase projects on asymmetric signing keys publish a JWKS at /auth/v1/.well-known/jwks.json.
 * Prove the backend verifies ES256 user JWTs against it when no SUPABASE_JWT_SECRET is configured.
 */
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import { SignJWT, generateKeyPair, exportJWK } from "jose";
import { createApp } from "../src/app.js";
import { verifySupabaseJwt, AuthError } from "../src/auth.js";

let server: http.Server;
let supabaseUrl: string;
let privateKey: CryptoKey;
let otherKey: CryptoKey;
let hits = 0;

beforeAll(async () => {
  const kp = await generateKeyPair("ES256");
  privateKey = kp.privateKey;
  otherKey = (await generateKeyPair("ES256")).privateKey;
  const jwk = { ...(await exportJWK(kp.publicKey)), kid: "sb-key-1", alg: "ES256", use: "sig" };
  server = http.createServer((req, res) => {
    hits++;
    if (req.url === "/auth/v1/.well-known/jwks.json") {
      res.writeHead(200, { "content-type": "application/json" });
      return void res.end(JSON.stringify({ keys: [jwk] }));
    }
    res.writeHead(404).end();
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  supabaseUrl = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
afterAll(() => server.close());

const sign = (key: CryptoKey, kid = "sb-key-1") =>
  new SignJWT({ role: "authenticated", email: "jwks@example.com" })
    .setAudience("authenticated")
    .setProtectedHeader({ alg: "ES256", kid })
    .setSubject("user-jwks")
    .setIssuedAt()
    .setIssuer(`${supabaseUrl}/auth/v1`)
    .setExpirationTime("5m")
    .sign(key);

describe("Supabase JWKS verification", () => {
  it("verifies an ES256 JWT via the project's JWKS", async () => {
    const p = await verifySupabaseJwt(await sign(privateKey), { SUPABASE_URL: supabaseUrl });
    expect(p).toMatchObject({ sub: "user-jwks", email: "jwks@example.com", via: "supabase" });
    expect(hits).toBeGreaterThan(0);
  });
  it("rejects a JWT signed by a different key", async () => {
    await expect(verifySupabaseJwt(await sign(otherKey), { SUPABASE_URL: supabaseUrl })).rejects.toBeInstanceOf(AuthError);
  });
  it("exchanges a JWKS-verified JWT for a session token end to end", async () => {
    const app = createApp({ log: null });
    const env = { SUPABASE_URL: supabaseUrl, SESSION_TOKEN_SECRET: "session-secret-session-secret-session" };
    const r = await app.request("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await sign(privateKey)}` } }, env);
    expect(r.status).toBe(200);
    expect(await r.json()).toMatchObject({ sub: "user-jwks", token: expect.any(String) });
    const bad = await app.request("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await sign(otherKey)}` } }, env);
    expect(bad.status).toBe(401);
  });
});
