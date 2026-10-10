import { describe, it, expect, vi, afterEach } from "vitest";
import { Hono } from "hono";
import { prepareGrantBody, proxyAnthropicOnGrant } from "../src/anthropicGrant.js";
import { requireAccount } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";

afterEach(() => vi.unstubAllGlobals());

describe("prepareGrantBody", () => {
  it("forces the model, caps max_tokens and caches the system prompt", () => {
    const out = prepareGrantBody({ model: "claude-opus-5-5", max_tokens: 9000, system: "be brief", messages: [{ role: "user", content: "hi" }] }, "claude-sonnet-5-5") as Record<string, unknown>;
    expect(out.model).toBe("claude-sonnet-5-5");
    expect(out.max_tokens).toBe(1024);
    expect(out.system).toEqual([{ type: "text", text: "be brief", cache_control: { type: "ephemeral" } }]);
  });
  it("refuses more than two images", () => {
    const image = { type: "image", source: { type: "base64", media_type: "image/jpeg", data: "AA" } };
    const out = prepareGrantBody({ messages: [{ role: "user", content: [image, image, image] }] }, "claude-sonnet-5-5");
    expect(out).toEqual({ error: "too_many_images" });
  });
  it("removes a client thinking field but keeps tools", () => {
    const tools = [{ name: "t", input_schema: { type: "object" } }];
    const out = prepareGrantBody({ thinking: { type: "enabled", budget_tokens: 8000 }, tools, messages: [{ role: "user", content: "hi" }] }, "claude-sonnet-5-5") as Record<string, unknown>;
    expect("thinking" in out).toBe(false);
    expect(out.tools).toEqual(tools);
  });
});

function appWith(ledger: MemorySpendLedger, upstreamBody: string) {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(upstreamBody, { headers: { "content-type": "text/event-stream" } })));
  const app = new Hono();
  app.use("*", async (c, next) => { c.set("principal" as never, { sub: "u1", via: "supabase" } as never); await next(); });
  app.use("*", requireAccount(() => ledger));
  app.post("/chat", (c) => proxyAnthropicOnGrant(c, ledger, "ask"));
  return app;
}

const SSE =
  'data: {"type":"message_start","message":{"usage":{"input_tokens":1000,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"output_tokens":1}}}\n\n' +
  'data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}\n\n' +
  'data: {"type":"message_delta","usage":{"output_tokens":100}}\n\n';

describe("proxyAnthropicOnGrant", () => {
  it("settles to the real cost once the stream has gone by", async () => {
    const ledger = new MemorySpendLedger();
    const res = await appWith(ledger, SSE).request("/chat", { method: "POST", body: JSON.stringify({ max_tokens: 500, messages: [{ role: "user", content: "hi" }] }) }, { ANTHROPIC_API_KEY: "sk-grant" });
    await res.text();
    await new Promise((r) => setTimeout(r, 10));
    const s = await ledger.summary("u1", { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000 });
    expect(s.spentTodayMicro).toBe(1000 * 2 + 100 * 10); // Sonnet 5.5: 3000 micro-dollars
  });

  it("a cancelled stream still settles", async () => {
    const ledger = new MemorySpendLedger();
    const res = await appWith(ledger, SSE).request("/chat", { method: "POST", body: JSON.stringify({ max_tokens: 500, messages: [{ role: "user", content: "hi" }] }) }, { ANTHROPIC_API_KEY: "sk-grant" });
    await res.body?.cancel();
    await new Promise((r) => setTimeout(r, 10));
    const s = await ledger.summary("u1", { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000 });
    expect(s.spentTodayMicro).toBeGreaterThan(0);
  });
});
