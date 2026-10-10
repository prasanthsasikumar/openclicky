import { describe, it, expect, vi, afterEach } from "vitest";
import { Hono } from "hono";
import { prepareGrantBody, proxyAnthropicOnGrant } from "../src/anthropicGrant.js";
import { requireAccount } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";

afterEach(() => vi.unstubAllGlobals());

describe("prepareGrantBody", () => {
  const msgs = [{ role: "user", content: "hi" }];
  it("keeps only custom tools and drops hosted tools, mcp_servers and container", () => {
    const custom = { name: "t", input_schema: { type: "object" } };
    const explicit = { type: "custom", name: "u", input_schema: { type: "object" } };
    const out = prepareGrantBody({ tools: [custom, { type: "web_search_20250305", name: "web_search" }, explicit, { type: "code_execution_20250522", name: "code_execution" }], mcp_servers: [{ url: "x" }], container: "c", messages: msgs }, "claude-sonnet-5-5") as Record<string, unknown>;
    expect(out.tools).toEqual([custom, explicit]);
    expect("mcp_servers" in out).toBe(false);
    expect("container" in out).toBe(false);
  });
  it("drops tools and tool_choice when only hosted tools were sent", () => {
    const out = prepareGrantBody({ tools: [{ type: "web_search_20250305", name: "web_search" }], tool_choice: { type: "tool", name: "web_search" }, messages: msgs }, "claude-sonnet-5-5") as Record<string, unknown>;
    expect("tools" in out).toBe(false);
    expect("tool_choice" in out).toBe(false);
  });
  it("clamps a non-numeric or non-positive max_tokens to the cap", () => {
    for (const bad of ["abc", 0, -5, null, NaN]) {
      expect((prepareGrantBody({ max_tokens: bad, messages: msgs }, "m") as Record<string, unknown>).max_tokens).toBe(1024);
    }
    expect((prepareGrantBody({ max_tokens: 300.7, messages: msgs }, "m") as Record<string, unknown>).max_tokens).toBe(300);
  });
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

const LIMITS = { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000 };
const post = (app: Hono, ledgerEnv = { ANTHROPIC_API_KEY: "sk-grant" }) =>
  app.request("/chat", { method: "POST", body: JSON.stringify({ max_tokens: 500, messages: [{ role: "user", content: "hi" }] }) }, ledgerEnv);

describe("proxyAnthropicOnGrant", () => {
  it("releases the hold and returns a generic 502 when the upstream fetch throws", async () => {
    const ledger = new MemorySpendLedger();
    const app = appWith(ledger, "");
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("socket hang up"); }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(app);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "the assistant is unavailable" });
    const open = await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS); // the whole daily limit is still free
    expect(open.ok).toBe(true);
    vi.restoreAllMocks();
  });

  it("settles a non-ok upstream at zero and keeps the upstream text out of the 502", async () => {
    const ledger = new MemorySpendLedger();
    const app = appWith(ledger, "");
    vi.stubGlobal("fetch", vi.fn(async () => new Response("secret upstream detail", { status: 529 })));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(app);
    expect(res.status).toBe(502);
    const text = await res.text();
    expect(text).toBe(JSON.stringify({ error: "the assistant is unavailable (529)" }));
    expect(text).not.toContain("secret");
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBe(0);
    vi.restoreAllMocks();
  });

  it("still returns the 502 when releasing the hold fails", async () => {
    const ledger = new MemorySpendLedger();
    const app = appWith(ledger, "");
    vi.stubGlobal("fetch", vi.fn(async () => new Response("x", { status: 500 })));
    vi.spyOn(ledger, "settle").mockRejectedValue(new Error("db down"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(app);
    expect(res.status).toBe(502);
    vi.restoreAllMocks();
  });

  it("charges the full estimate when the reply reports no usage", async () => {
    const ledger = new MemorySpendLedger();
    const res = await post(appWith(ledger, 'data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}\n\n'));
    await res.text();
    await new Promise((r) => setTimeout(r, 10));
    const s = await ledger.summary("u1", LIMITS);
    expect(s.spentTodayMicro).toBeGreaterThan(3000);
  });

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
