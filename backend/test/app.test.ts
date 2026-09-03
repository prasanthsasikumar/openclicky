import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import { SignJWT } from "jose";
import { createApp } from "../src/app.js";
import type { Env } from "../src/env.js";

type Seen = { url: string; auth?: string; apiKey?: string; contentType?: string; raw: string; body: Record<string, unknown> };
let upstream: http.Server;
let upstreamUrl: string;
const seen: Seen[] = [];

beforeAll(async () => {
  // Fake OpenAI/Anthropic upstream: JSON for realtime/transcription, SSE for everything else.
  upstream = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      const isJson = (req.headers["content-type"] ?? "").includes("application/json");
      seen.push({
        url: req.url!,
        auth: req.headers.authorization,
        apiKey: req.headers["x-api-key"] as string | undefined,
        contentType: req.headers["content-type"],
        raw: b,
        body: b && isJson ? JSON.parse(b) : {},
      });
      if (req.url!.endsWith("/realtime/client_secrets")) {
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ value: "ek_test_secret", expires_at: 1234, session: JSON.parse(b).session }));
      }
      if (req.url!.endsWith("/audio/transcriptions")) {
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ text: "hello from whisper" }));
      }
      if (req.url!.endsWith("/audio/speech") || req.url!.includes("/text-to-speech/")) {
        res.writeHead(200, { "content-type": "audio/mpeg" });
        return void res.end(Buffer.from("ID3fake-mp3"));
      }
      if (req.url!.startsWith("/v3/token")) {
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ token: "aai_temp_token", expires_in_seconds: 480 }));
      }
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
const logged: any[] = [];
const app = createApp({ log: (e) => logged.push(e) });
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

  it("proxies /v1/messages to Anthropic with x-api-key and the server-side default model", async () => {
    const r = await call("/v1/messages", json({ model: "claude", messages: [] }, await jwt()));
    expect(r.status).toBe(200);
    await r.text();
    expect(seen.at(-1)!.url).toBe("/v1/messages");
    expect(seen.at(-1)!.apiKey).toBe("ak-upstream");
    expect(seen.at(-1)!.auth).toBeUndefined();
    expect(seen.at(-1)!.body.model).toBe("claude");
    const d = await call("/v1/messages", json({ model: "default", messages: [] }, await jwt()), { ANTHROPIC_MODEL: "claude-haiku-4-5" });
    expect(d.status).toBe(200);
    await d.text();
    expect(seen.at(-1)!.body.model).toBe("claude-haiku-4-5");
  });

  it("mints a Realtime client secret server-side", async () => {
    const r = await call("/agent/realtime/session", json({ voice: "marin", instructions: "be brief" }, await jwt()), { OPENAI_REALTIME_MODEL: "gpt-realtime-test" });
    expect(r.status).toBe(200);
    const body = (await r.json()) as any;
    expect(body.value).toBe("ek_test_secret");
    const up = seen.at(-1)!;
    expect(up.url).toBe("/v1/realtime/client_secrets");
    expect(up.auth).toBe("Bearer sk-upstream");
    expect(up.body.session).toMatchObject({ type: "realtime", model: "gpt-realtime-test", instructions: "be brief", audio: { output: { voice: "marin" } } });
    expect((await call("/agent/realtime/session", { method: "POST" })).status).toBe(401);
  });

  it("transcribes base64 audio via a server-side multipart upload", async () => {
    const audio = Buffer.from("RIFF....WAVEfmt ").toString("base64");
    const r = await call("/agent/transcribe", json({ audio, mime: "audio/wav", language: "en" }, await jwt()), { OPENAI_TRANSCRIBE_MODEL: "stt-test" });
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ text: "hello from whisper" });
    const up = seen.at(-1)!;
    expect(up.url).toBe("/v1/audio/transcriptions");
    expect(up.contentType).toMatch(/^multipart\/form-data/);
    expect(up.raw).toContain('name="model"');
    expect(up.raw).toContain("stt-test");
    expect(up.raw).toContain('filename="audio.wav"');
    expect(up.raw).toContain("RIFF....WAVEfmt ");
    const bad = await call("/agent/transcribe", json({}, await jwt()));
    expect(bad.status).toBe(400);
  });

  it("serves the skills library (auth required)", async () => {
    expect((await call("/skills/library")).status).toBe(401);
    const r = await call("/skills/library", { headers: { authorization: `Bearer ${await jwt()}` } });
    expect(r.status).toBe(200);
    const { skills } = (await r.json()) as { skills: { id: string; name: string; description: string; kind: string; files: string[] }[] };
    expect(skills.length).toBe(15);
    const artifacts = skills.find((s) => s.id === "openclicky-artifacts")!;
    expect(artifacts.name).toBe("openclicky-artifacts");
    expect(artifacts.kind).toBe("workflow");
    expect(artifacts.files).toContain("SKILL.md");
    expect(skills.find((s) => s.id === "pdf")!.kind).toBe("capability");
    expect(skills.some((s) => s.id === "powerpoint")).toBe(false);
  });

  it("serves the native shell's /chat, /tts and /transcribe-token contract", async () => {
    expect((await call("/chat", { method: "POST" })).status).toBe(401);
    expect((await call("/tts", { method: "POST" })).status).toBe(401);
    expect((await call("/transcribe-token", { method: "POST" })).status).toBe(401);
    const token = await jwt();

    const chat = await call("/chat", json({ model: "claude-sonnet-4-6", stream: true, messages: [] }, token));
    expect(chat.status).toBe(200);
    await chat.text();
    expect(seen.at(-1)).toMatchObject({ url: "/v1/messages", apiKey: "ak-upstream" });
    expect(seen.at(-1)!.body.model).toBe("claude-sonnet-4-6");

    // OpenAI speech when no ElevenLabs key
    const tts = await call("/tts", json({ text: "hello there" }, token), { OPENAI_TTS_VOICE: "cedar" });
    expect(tts.status).toBe(200);
    expect(tts.headers.get("content-type")).toBe("audio/mpeg");
    expect(Buffer.from(await tts.arrayBuffer()).toString()).toBe("ID3fake-mp3");
    expect(seen.at(-1)).toMatchObject({ url: "/v1/audio/speech", auth: "Bearer sk-upstream" });
    expect(seen.at(-1)!.body).toMatchObject({ input: "hello there", voice: "cedar", response_format: "mp3" });
    // ElevenLabs when configured
    const el = await call("/tts", json({ text: "hi" }, token), { ELEVENLABS_API_KEY: "xi-key", ELEVENLABS_VOICE_ID: "voice1", ELEVENLABS_BASE_URL: upstreamUrl });
    expect(el.status).toBe(200);
    await el.arrayBuffer();
    expect(seen.at(-1)!.url).toBe("/v1/text-to-speech/voice1");
    expect(seen.at(-1)!.body.text).toBe("hi");
    expect((await call("/tts", json({}, token))).status).toBe(400);

    expect((await call("/transcribe-token", { method: "POST", headers: { authorization: `Bearer ${token}` } })).status).toBe(501);
    const tok = await call("/transcribe-token", { method: "POST", headers: { authorization: `Bearer ${token}` } }, { ASSEMBLYAI_API_KEY: "aai", ASSEMBLYAI_BASE_URL: upstreamUrl });
    expect(tok.status).toBe(200);
    expect(await tok.json()).toEqual({ token: "aai_temp_token", expires_in_seconds: 480 });
    expect(seen.at(-1)!.auth).toBe("aai");
  });

  it("logs one structured entry per request with the principal (never the token)", async () => {
    logged.length = 0;
    await call("/health");
    const r = await call("/v1/chat/completions", json({ messages: [] }, await jwt()));
    await r.text();
    await call("/v1/chat/completions", { method: "POST" });
    expect(logged.map((e) => [e.path, e.status, e.sub ?? null])).toEqual([
      ["/health", 200, null],
      ["/v1/chat/completions", 200, "user-1"],
      ["/v1/chat/completions", 401, null],
    ]);
    expect(logged[1]).toMatchObject({ method: "POST", via: "supabase", ms: expect.any(Number), ts: expect.any(String) });
    expect(JSON.stringify(logged)).not.toContain("eyJ");
  });

  it("502 when upstream key missing", async () => {
    const r = await call("/v1/chat/completions", json({}, await jwt()), { OPENAI_API_KEY: undefined });
    expect(r.status).toBe(502);
    const a = await call("/v1/messages", json({}, await jwt()), { ANTHROPIC_API_KEY: undefined });
    expect(a.status).toBe(502);
  });
});
