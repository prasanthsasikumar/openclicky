import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import { SignJWT } from "jose";
import { createApp } from "../src/app.js";
import type { Env } from "../src/env.js";

type Seen = { url: string; auth?: string; apiKey?: string; body: Record<string, unknown> };
let upstream: http.Server;
let upstreamUrl: string;
const seen: Seen[] = [];

beforeAll(async () => {
  // Fake OpenAI/Anthropic upstream that streams two SSE chunks.
  upstream = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      seen.push({
        url: req.url!,
        auth: req.headers.authorization,
        apiKey: req.headers["x-api-key"] as string | undefined,
        body: b ? JSON.parse(b) : {},
      });
      res.writeHead(200, { "content-type": "text/event-stream" });
      res.write('data: {"chunk":1}\n\n');
      setTimeout(() => {
        res.write("data: [DONE]\n\n");
        res.end();
      }, 10);
    });
  });
  await new Promise<void>((r) => upstream.listen(0, "127.0.0.1", r));
  upstreamUrl = `http://127.0.0.1:${(upstream.address() as { port: number }).port}`;
});
afterAll(() => upstream.close());

const env: Env = {
  SUPABASE_JWT_SECRET: "supabase-test-secret-supabase-test-secret",
  SESSION_TOKEN_SECRET: "session-secret-session-secret-session",
  OPENAI_API_KEY: "sk-upstream",
  OPENAI_MODEL: "gpt-test",
  ANTHROPIC_API_KEY: "ak-upstream",
};
const app = createApp();
const call = (path: string, init: RequestInit = {}, over: Partial<Env> = {}) =>
  app.request(path, init, { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1", ANTHROPIC_BASE_URL: upstreamUrl, ...over });
const jwt = () =>
  new SignJWT({ email: "dev@example.com" })
    .setProtectedHeader({ alg: "HS256" })
    .setSubject("user-1")
    .setIssuedAt()
    .setExpirationTime("5m")
    .sign(new TextEncoder().encode(env.SUPABASE_JWT_SECRET!));
const json = (body: unknown, token: string) => ({
  method: "POST",
  headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
  body: JSON.stringify(body),
});

describe("app", () => {
  it("GET /health", async () => {
    const r = await call("/health");
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ ok: true });
  });

  it("401 without auth on /v1/* and /agent/*", async () => {
    expect((await call("/v1/chat/completions", { method: "POST", body: "{}" })).status).toBe(401);
    expect((await call("/v1/responses", { method: "POST", body: "{}" })).status).toBe(401);
    expect((await call("/v1/messages", { method: "POST", body: "{}" })).status).toBe(401);
    expect((await call("/agent/session-token", { method: "POST" })).status).toBe(401);
    const bad = await call("/v1/chat/completions", { method: "POST", headers: { authorization: "Bearer nope" } });
    expect(bad.status).toBe(401);
    expect(await bad.json()).toMatchObject({ error: expect.any(String) });
  });

  it("exchanges Supabase JWT for session token, which then authorizes proxy calls", async () => {
    const r = await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await jwt()}` } });
    expect(r.status).toBe(200);
    const { token, sub, expiresAt } = (await r.json()) as { token: string; sub: string; expiresAt: number };
    expect(sub).toBe("user-1");
    expect(expiresAt).toBeGreaterThan(Date.now() / 1000);

    const p = await call(
      "/v1/chat/completions",
      json({ model: "default", messages: [{ role: "user", content: "hi" }], stream: true }, token),
    );
    expect(p.status).toBe(200);
    expect(p.headers.get("content-type")).toContain("text/event-stream");
    expect(await p.text()).toContain("[DONE]");
    const last = seen.at(-1)!;
    expect(last.url).toBe("/v1/chat/completions");
    expect(last.auth).toBe("Bearer sk-upstream");
    expect(last.body.model).toBe("gpt-test");
  });

  it("a raw Supabase JWT also authorizes proxy calls", async () => {
    const p = await call("/v1/chat/completions", json({ messages: [] }, await jwt()));
    expect(p.status).toBe(200);
    await p.text();
  });

  it("session token cannot be re-exchanged", async () => {
    const first = await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await jwt()}` } });
    const { token } = (await first.json()) as { token: string };
    const again = await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${token}` } });
    expect(again.status).toBe(401);
  });

  it("proxies /v1/responses keeping the caller's model", async () => {
    const r = await call("/v1/responses", json({ model: "gpt-5.6-luna", input: "x", stream: true }, await jwt()));
    expect(r.status).toBe(200);
    await r.text();
    expect(seen.at(-1)!.url).toBe("/v1/responses");
    expect(seen.at(-1)!.body.model).toBe("gpt-5.6-luna");
  });

  it("proxies /v1/messages to Anthropic with x-api-key", async () => {
    const r = await call("/v1/messages", json({ model: "claude", messages: [] }, await jwt()));
    expect(r.status).toBe(200);
    await r.text();
    expect(seen.at(-1)!.url).toBe("/v1/messages");
    expect(seen.at(-1)!.apiKey).toBe("ak-upstream");
    expect(seen.at(-1)!.auth).toBeUndefined();
  });

  it("502 when upstream key missing", async () => {
    const r = await call("/v1/chat/completions", json({}, await jwt()), { OPENAI_API_KEY: undefined });
    expect(r.status).toBe(502);
    const a = await call("/v1/messages", json({}, await jwt()), { ANTHROPIC_API_KEY: undefined });
    expect(a.status).toBe(502);
  });
});
