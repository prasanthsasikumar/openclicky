import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { Hono } from "hono";
import { speakOnGrant, elevenLabsCharsRemaining, resetElevenLabsCache } from "../src/tts.js";
import { requireAccount } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";

beforeEach(() => resetElevenLabsCache());
afterEach(() => vi.unstubAllGlobals());

function stub(subscription: { character_count: number; character_limit: number }) {
  const f = vi.fn(async (url: string) =>
    url.includes("/v1/user/subscription")
      ? new Response(JSON.stringify(subscription), { headers: { "content-type": "application/json" } })
      : new Response(new Uint8Array([1, 2, 3]), { headers: { "content-type": "audio/mpeg" } }),
  );
  vi.stubGlobal("fetch", f);
  return f;
}

function app(ledger: MemorySpendLedger) {
  const a = new Hono();
  a.use("*", async (c, next) => { c.set("principal" as never, { sub: "u1", via: "supabase" } as never); await next(); });
  a.use("*", requireAccount(() => ledger));
  a.post("/tts", (c) => speakOnGrant(c, ledger));
  return a;
}
const env = { ELEVENLABS_API_KEY: "xi" };
const say = (a: Hono, text = "Click Battery.") => a.request("/tts", { method: "POST", body: JSON.stringify({ text }) }, env);

describe("elevenLabsCharsRemaining", () => {
  it("is the plan's remainder minus a 5% margin", async () => {
    stub({ character_count: 1000, character_limit: 10_000 });
    expect(await elevenLabsCharsRemaining(env)).toBe(10_000 - 1000 - 500);
  });

  it("is 0 when the subscription lookup throws", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("network down"); }));
    expect(await elevenLabsCharsRemaining(env)).toBe(0);
  });

  it("is 0 when the subscription lookup returns non-JSON", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("<html>oops</html>", { status: 200 })));
    expect(await elevenLabsCharsRemaining(env)).toBe(0);
  });
});

describe("speakOnGrant", () => {
  it("speaks with ElevenLabs and counts characters", async () => {
    stub({ character_count: 0, character_limit: 1_000_000 });
    const ledger = new MemorySpendLedger();
    const res = await say(app(ledger));
    expect(res.headers.get("content-type")).toBe("audio/mpeg");
    const s = await ledger.summary("u1", { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000, guestTotalMicro: 1_000_000, guestDays: 14, guestTtsChars: 2_000 });
    expect(s.ttsCharsMonth).toBe("Click Battery.".length);
  });

  it("answers tts_budget when ElevenLabs' allowance is used up", async () => {
    stub({ character_count: 9_999, character_limit: 10_000 });
    const res = await say(app(new MemorySpendLedger()), "A long answer.");
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "tts_budget" });
  });

  it("speaks only the first 400 characters", async () => {
    const f = stub({ character_count: 0, character_limit: 1_000_000 });
    await say(app(new MemorySpendLedger()), "word ".repeat(200));
    const ttsCall = f.mock.calls.find(([url]) => String(url).includes("text-to-speech"))!;
    expect(JSON.parse((ttsCall as unknown as [string, RequestInit])[1].body as string).text.length).toBeLessThanOrEqual(400);
  });

  it("answers tts_budget, not 500, when the subscription lookup throws", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("boom"); }));
    const res = await say(app(new MemorySpendLedger()));
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "tts_budget" });
  });

  it("answers tts_budget without upstream text or the key when text-to-speech throws", async () => {
    const log = vi.spyOn(console, "error").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      if (url.includes("/v1/user/subscription")) return new Response(JSON.stringify({ character_count: 0, character_limit: 1_000_000 }));
      throw new Error("upstream said xi leaked");
    }));
    const res = await say(app(new MemorySpendLedger()));
    const body = await res.text();
    expect(res.status).toBe(402);
    expect(JSON.parse(body)).toEqual({ error: "tts_budget" });
    expect(body).not.toContain("leaked");
    expect(JSON.stringify(log.mock.calls)).not.toContain("xi leaked");
    log.mockRestore();
  });

  it("answers tts_budget, not 500, when the ledger reservation rejects", async () => {
    stub({ character_count: 0, character_limit: 1_000_000 });
    const log = vi.spyOn(console, "error").mockImplementation(() => {});
    const ledger = new MemorySpendLedger();
    vi.spyOn(ledger, "reserveCharacters").mockRejectedValue(new Error("rpc down xi"));
    const res = await say(app(ledger));
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "tts_budget" });
    expect(JSON.stringify(log.mock.calls)).not.toContain("rpc down");
    log.mockRestore();
  });

  it("spends the cached plan remainder so accounts together cannot overspend between refreshes", async () => {
    stub({ character_count: 250, character_limit: 1000 });
    const a = app(new MemorySpendLedger());
    const text = "a".repeat(300);
    expect((await say(a, text)).status).toBe(200);
    expect((await say(a, text)).status).toBe(200);
    const third = await say(a, text);
    expect(third.status).toBe(402);
    expect(await third.json()).toEqual({ error: "tts_budget" });
  });
});
