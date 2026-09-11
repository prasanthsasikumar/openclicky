import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import { SignJWT } from "jose";
import { createApp } from "../src/app.js";
import type { Env } from "../src/env.js";
import { parseSkillMarkdown } from "../src/skillMarkdown.js";
import { MemoryBillingStore } from "../src/billing.js";

type Seen = { url: string; auth?: string; apiKey?: string; contentType?: string; raw: string; body: Record<string, unknown> };
const MOCK_SKILL = "```markdown\n---\nname: Reply In My Voice\ndescription: Draft email replies in the user's own voice.\nsurfaces: [talk, agent]\n---\n# Reply In My Voice\n\n## Use When\nThe user asks for a reply.\n```";
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
      if (req.url!.endsWith("/chat/completions") && b.includes('"stream":false')) {
        // Non-streaming chat: POST /skills/create asks the model for a SKILL.md.
        const content = b.includes("BROKEN") ? "no frontmatter" : b.includes("COMMENTED") ? MOCK_SKILL.replace("surfaces: [talk, agent]", "surfaces: [talk]   # talk = applies when chatting") : MOCK_SKILL;
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ choices: [{ message: { role: "assistant", content } }] }));
      }
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
  new SignJWT({ role: "authenticated", email: "dev@example.com" })
    .setAudience("authenticated")
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

describe("billing configuration", () => {
  it("refuses metered routes when a Supabase project is named but has no service key", async () => {
    // Failing open here silently gave every request away for free for the life of the isolate.
    const r = await call("/chat", json({ messages: [] }, await jwt()), { SUPABASE_URL: "https://project.supabase.co" });
    expect(r.status).toBe(503);
    expect(await r.json()).toMatchObject({ error: "billing is not configured on this backend" });
  });

  it("runs unmetered when no Supabase is configured at all, which is a deliberate self-host", async () => {
    const r = await call("/chat", json({ messages: [{ role: "user", content: "hi" }] }, await jwt()));
    expect(r.status).toBe(200);
  });

  it("puts /transcribe-token behind the credits gate like every other model route", async () => {
    // It mints a real AssemblyAI credential on the backend's key, so it costs money like the rest.
    const r = await call("/transcribe-token", { method: "POST", headers: { authorization: `Bearer ${await jwt()}` } }, { SUPABASE_URL: "https://project.supabase.co" });
    expect(r.status).toBe(503);
  });
});

describe("app", () => {
  it("GET /health", async () => {
    const r = await call("/health");
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ ok: true });
  });

  it("GET /auth/config publishes the Supabase sign-in details, or 404 without them", async () => {
    const off = await call("/auth/config");
    expect(off.status).toBe(404);
    const on = await call("/auth/config", {}, { SUPABASE_URL: "https://db.example.com/", SUPABASE_PUBLISHABLE_KEY: "sb_publishable_x" });
    expect(on.status).toBe(200);
    expect(await on.json()).toEqual({ supabaseUrl: "https://db.example.com", publishableKey: "sb_publishable_x" });
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
    expect(skills.filter((s) => s.kind !== "app").length).toBe(15); // agent skills; app-teaching skills are extra
    const artifacts = skills.find((s) => s.id === "openclicky-artifacts")!;
    expect(artifacts.name).toBe("openclicky-artifacts");
    expect(artifacts.kind).toBe("workflow");
    expect(artifacts.files).toContain("SKILL.md");
    expect(skills.find((s) => s.id === "pdf")!.kind).toBe("capability");
    expect(skills.some((s) => s.id === "powerpoint")).toBe(false);
  });

  it("creates a skill from a one-line request", async () => {
    const r = await call("/skills/create", json({ request: "reply to emails in my voice", capabilities: ["gmail"] }, await jwt()));
    expect(r.status).toBe(200);
    const j = await r.json();
    expect(j).toMatchObject({ id: "reply-in-my-voice", name: "Reply In My Voice", description: "Draft email replies in the user's own voice." });
    expect(j.markdown.startsWith("---\n")).toBe(true);
    expect(j.markdown).not.toContain("```");
    const sent = seen.at(-1)!;
    expect(sent.url).toBe("/v1/chat/completions");
    expect(sent.auth).toBe("Bearer sk-upstream");
    expect(sent.body.stream).toBe(false);
    expect(sent.body.model).toBe("gpt-test");
    expect(JSON.stringify(sent.body.messages)).toContain("gmail");
    expect(JSON.stringify(sent.body.messages)).toContain("openclicky-email-assistant");
  });
  it("rejects bad bodies and invalid model output", async () => {
    expect((await call("/skills/create", { method: "POST" })).status).toBe(401);
    expect((await call("/skills/create", json({}, await jwt()))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "BROKEN" }, await jwt()))).status).toBe(502);
    expect((await call("/skills/create", json({ request: "x" }, await jwt()), { OPENAI_API_KEY: "" })).status).toBe(503);
    expect((await call("/skills/create", json({ request: "x" }, await jwt()), { OPENAI_MODEL: "", SKILL_CREATE_MODEL: "" })).status).toBe(503);
  });
  it("caps the request and capability sizes", async () => {
    const token = await jwt();
    expect((await call("/skills/create", json({ request: "x".repeat(2001) }, token))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "ok", capabilities: Array(21).fill("gmail") }, token))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "ok", capabilities: ["y".repeat(65)] }, token))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "ok", capabilities: "gmail" }, token))).status).toBe(400);
    expect((await call("/skills/create", json({ request: "ok", capabilities: ["gmail"] }, token))).status).toBe(200);
  });
  it("tolerates a model that echoes a trailing comment on the surfaces line", async () => {
    const r = await call("/skills/create", json({ request: "COMMENTED" }, await jwt()));
    expect(r.status).toBe(200);
    const { markdown } = await r.json();
    expect(markdown).toContain("surfaces: [talk]   # talk");
    expect(parseSkillMarkdown(markdown)!.surfaces).toEqual(["talk"]);
  });
  it("uses SKILL_CREATE_MODEL over OPENAI_MODEL when set", async () => {
    const r = await call("/skills/create", json({ request: "x" }, await jwt()), { SKILL_CREATE_MODEL: "gpt-skills" });
    expect(r.status).toBe(200);
    expect(seen.at(-1)!.body.model).toBe("gpt-skills");
  });
  it("serves app skills in the library manifest", async () => {
    const r = await call("/skills/library", { headers: { authorization: `Bearer ${await jwt()}` } });
    const { skills } = await r.json();
    expect(skills.some((s: any) => s.kind === "app" && s.apps?.length)).toBe(true);
    expect(skills.some((s: any) => s.kind === "app" && s.sites?.length)).toBe(true);
    expect(skills.every((s: any) => s.kind !== "app" || s.surfaces?.includes("talk"))).toBe(true);
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

  it("maps model names for aggregators via MODEL_ALIASES and prefixes", async () => {
    const mapping = { MODEL_ALIASES: "claude-sonnet-4-6=anthropic/claude-sonnet-4.6", OPENAI_MODEL_PREFIX: "openai/", ANTHROPIC_MODEL_PREFIX: "anthropic/" };
    const r = await call("/v1/responses", json({ model: "gpt-5.6-luna", input: "x" }, await jwt()), mapping);
    expect(r.status).toBe(200);
    await r.text();
    expect(seen.at(-1)!.body.model).toBe("openai/gpt-5.6-luna");
    const a = await call("/chat", json({ model: "claude-sonnet-4-6", messages: [] }, await jwt()), mapping);
    await a.text();
    expect(seen.at(-1)!.body.model).toBe("anthropic/claude-sonnet-4.6");
    const already = await call("/chat", json({ model: "anthropic/claude-haiku-4.5", messages: [] }, await jwt()), mapping);
    await already.text();
    expect(seen.at(-1)!.body.model).toBe("anthropic/claude-haiku-4.5");
  });

  it("502 when upstream key missing", async () => {
    const r = await call("/v1/chat/completions", json({}, await jwt()), { OPENAI_API_KEY: undefined });
    expect(r.status).toBe(502);
    const a = await call("/v1/messages", json({}, await jwt()), { ANTHROPIC_API_KEY: undefined });
    expect(a.status).toBe(502);
  });

  const byokHeaders = (token: string, extra: Record<string, string> = {}) => ({
    authorization: `Bearer ${token}`,
    "content-type": "application/json",
    "x-openclicky-openai-key": "sk-user",
    ...extra,
  });

  it("BYOK: a request with its own OpenAI key is sent with that key to the BYOK base", async () => {
    const token = await jwt();
    const r = await app.request(
      "/v1/chat/completions",
      { method: "POST", headers: byokHeaders(token), body: JSON.stringify({ model: "default", messages: [], stream: true }) },
      { ...env, OPENAI_BASE_URL: upstreamUrl + "/wrong", BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
    );
    expect(r.status).toBe(200);
    await r.text();
    const last = seen.at(-1)!;
    expect(last.url).toBe("/v1/chat/completions");
    expect(last.auth).toBe("Bearer sk-user");
    // "default" resolves to the backend default model, but OpenRouter aliases/prefixes are not applied.
    expect(last.body.model).toBe("gpt-test");
  });

  it("BYOK: the Realtime client secret is minted with the user's key", async () => {
    const token = await jwt();
    const r = await app.request("/agent/realtime/session", { method: "POST", headers: byokHeaders(token), body: "{}" }, { ...env, BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" });
    expect(r.status).toBe(200);
    expect(seen.at(-1)!.auth).toBe("Bearer sk-user");
  });

  it("BYOK: Anthropic lanes need the user's Anthropic key", async () => {
    const token = await jwt();
    const missing = await call("/v1/messages", { method: "POST", headers: byokHeaders(token), body: JSON.stringify({ model: "default", messages: [] }) });
    expect(missing.status).toBe(402);
    expect(await missing.json()).toEqual({ error: "byok_missing_anthropic_key" });

    const withKey = await app.request(
      "/chat",
      { method: "POST", headers: byokHeaders(token, { "x-openclicky-anthropic-key": "ak-user" }), body: JSON.stringify({ model: "default", messages: [] }) },
      { ...env, BYOK_ANTHROPIC_BASE_URL: upstreamUrl },
    );
    expect(withKey.status).toBe(200);
    await withKey.text();
    const last = seen.at(-1)!;
    expect(last.apiKey).toBe("ak-user");
    expect(last.body.model).toBe("claude-haiku-4-5-20251001");
  });

  describe("billing", () => {
    const settle = () => new Promise((res) => setTimeout(res, 20));

    it("meters a Codex turn and reports it on /billing/me", async () => {
      const store = new MemoryBillingStore();
      const billed = createApp({ log: null, billingStore: store });
      const token = await jwt();
      const r = await billed.request("/v1/responses", json({ model: "default", input: "hi", stream: true }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      expect(r.status).toBe(200);
      await r.text();
      await settle();
      expect(store.events).toHaveLength(1);
      // The fake upstream reports no usage → the flat fallback.
      expect(store.events[0]).toMatchObject({ user_id: "user-1", route: "/v1/responses", credits: 2 });

      const me = await billed.request("/billing/me", { headers: { authorization: `Bearer ${token}` } }, env);
      expect(me.status).toBe(200);
      expect(await me.json()).toMatchObject({ byok: false, plan: "free", used: 2, limit: 200 });
    });

    it("charges a Realtime session mint a flat rate and transcription by audio length", async () => {
      const store = new MemoryBillingStore();
      const billed = createApp({ log: null, billingStore: store });
      const token = await jwt();
      await billed.request("/agent/realtime/session", json({}, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      const wav = Buffer.alloc(44 + 16000 * 2 * 20).toString("base64"); // 20 s of 16 kHz mono PCM16
      await billed.request("/agent/transcribe", json({ audio: wav, mime: "audio/wav" }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      await settle();
      expect(store.events.map((e) => [e.route, e.credits])).toEqual([
        ["/agent/realtime/session", 30],
        ["/agent/transcribe", 2],
      ]);
      expect(store.events[1].audio_seconds).toBeCloseTo(20, 0);
    });

    it("blocks a metered user at 402 but lets a BYOK request through", async () => {
      const store = new MemoryBillingStore();
      store.events.push({ user_id: "user-1", route: "x", input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: 200 });
      const billed = createApp({ log: null, billingStore: store });
      const token = await jwt();
      const blocked = await billed.request("/v1/chat/completions", json({ model: "default", messages: [] }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      expect(blocked.status).toBe(402);
      const byok = await billed.request(
        "/v1/chat/completions",
        { method: "POST", headers: byokHeaders(token), body: JSON.stringify({ model: "default", messages: [] }) },
        { ...env, BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
      );
      expect(byok.status).toBe(200);
      await byok.text();
      expect(store.events).toHaveLength(1);
    });

    it("FREE_MONTHLY_CREDITS=0 makes the backend invite-only for users on OpenClicky's keys", async () => {
      const billed = createApp({ log: null, billingStore: new MemoryBillingStore() });
      const token = await jwt();
      const blocked = await billed.request("/v1/chat/completions", json({ model: "default", messages: [] }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1", FREE_MONTHLY_CREDITS: "0" });
      expect(blocked.status).toBe(402);
      expect(await blocked.json()).toMatchObject({ error: "credits_exhausted", plan: "free", limit: 0 });
      const me = await billed.request("/billing/me", { headers: { authorization: `Bearer ${token}` } }, { ...env, FREE_MONTHLY_CREDITS: "0" });
      expect(await me.json()).toMatchObject({ plan: "free", limit: 0 });
    });

    it("/billing/me reports byok for a request with its own key", async () => {
      const billed = createApp({ log: null, billingStore: new MemoryBillingStore() });
      const me = await billed.request("/billing/me", { headers: byokHeaders(await jwt()) }, env);
      expect(await me.json()).toMatchObject({ byok: true, plan: "byok" });
    });

    it("streamed chat completions ask the upstream to include usage for metered users only", async () => {
      const billed = createApp({ log: null, billingStore: new MemoryBillingStore() });
      const token = await jwt();
      const metered = await billed.request("/v1/chat/completions", json({ model: "default", messages: [], stream: true }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      await metered.text();
      expect(seen.at(-1)!.body.stream_options).toEqual({ include_usage: true });
      const byok = await billed.request(
        "/v1/chat/completions",
        { method: "POST", headers: byokHeaders(token), body: JSON.stringify({ model: "default", messages: [], stream: true }) },
        { ...env, BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
      );
      await byok.text();
      expect(seen.at(-1)!.body.stream_options).toBeUndefined();
    });
  });
});
