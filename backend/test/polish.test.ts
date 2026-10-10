import { describe, it, expect, vi, afterEach } from "vitest";
import { Hono } from "hono";
import { polishTake } from "../src/polish.js";
import { requireAccount } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const LIMITS = { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000, guestTotalMicro: 1_000_000, guestDays: 14, guestTtsChars: 2_000 };
const OK_REPLY = '{"content":[{"type":"text","text":"See you at seven."}],"usage":{"input_tokens":300,"output_tokens":20}}';
const ENV = { ANTHROPIC_API_KEY: "sk" };
const payload = { purpose: "polish", system: "fix punctuation", text: "see you at seven" };

function app(ledger: MemorySpendLedger, impl?: () => Promise<Response>) {
  const fetchMock = vi.fn(impl ?? (async () => new Response(OK_REPLY, { headers: { "content-type": "application/json" } })));
  vi.stubGlobal("fetch", fetchMock);
  const a = new Hono();
  a.use("*", async (c, next) => { c.set("principal" as never, { sub: "u1", via: "supabase" } as never); await next(); });
  a.use("*", requireAccount(() => ledger));
  a.post("/v1/polish", (c) => polishTake(c, ledger));
  return { a, fetchMock };
}

const post = (a: Hono, body: unknown = payload, headers: Record<string, string> = {}) =>
  a.request("/v1/polish", { method: "POST", headers, body: JSON.stringify(body) }, ENV);

describe("/v1/polish", () => {
  it("returns the polished text on Haiku and charges its cost", async () => {
    const ledger = new MemorySpendLedger();
    const { a, fetchMock } = app(ledger);
    const res = await post(a);
    expect(await res.json()).toEqual({ text: "See you at seven." });
    const sent = JSON.parse((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].body as string);
    expect(sent.model).toBe("claude-haiku-4-5");
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBe(300 * 1 + 20 * 5);
  });

  it("rejects an over-long take before spending anything", async () => {
    const { a, fetchMock } = app(new MemorySpendLedger());
    const res = await post(a, { purpose: "polish", system: "s", text: "x".repeat(8001) });
    expect(res.status).toBe(413);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects an unknown purpose", async () => {
    const { a } = app(new MemorySpendLedger());
    expect((await post(a, { purpose: "essay", system: "s", text: "t" })).status).toBe(400);
  });

  it("releases the hold and returns a generic 502 when the upstream fetch throws", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => { throw new Error("socket hang up"); });
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "polish unavailable" });
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("gives the upstream call a timeout; a timeout releases the hold and answers the generic 502", async () => {
    const ledger = new MemorySpendLedger();
    const { a, fetchMock } = app(ledger, async () => { throw new DOMException("The operation timed out.", "TimeoutError"); });
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "polish unavailable" });
    expect((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].signal).toBeInstanceOf(AbortSignal);
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("settles a 529 at zero and keeps the upstream text out of the 502", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => new Response("secret upstream detail", { status: 529 }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    const text = await res.text();
    expect(text).toBe(JSON.stringify({ error: "polish unavailable" }));
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBe(0);
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("releases the hold when the upstream body cannot be read", async () => {
    const ledger = new MemorySpendLedger();
    const broken = new Response(new ReadableStream({ start(ctl) { ctl.error(new Error("reset")); } }));
    const { a } = app(ledger, async () => broken);
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "polish unavailable" });
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("releases the hold when the reply is not JSON", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => new Response("<html>oops</html>"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "polish unavailable" });
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBe(0);
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("releases the hold when the reply has no content", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => new Response('{"id":"x"}'));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect((await ledger.reserve("u1", LIMITS.dailyMicro, LIMITS)).ok).toBe(true);
  });

  it("charges the full estimate when a good reply carries no usage", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => new Response('{"content":[{"type":"text","text":"Hi."}]}'));
    const res = await post(a);
    expect(await res.json()).toEqual({ text: "Hi." });
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBeGreaterThan(0);
  });

  it("still returns the 502 when releasing the hold fails", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger, async () => new Response("x", { status: 500 }));
    vi.spyOn(ledger, "settle").mockRejectedValue(new Error("db down"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await post(a);
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "polish unavailable" });
  });

  it("does not meter a BYOK request", async () => {
    const ledger = new MemorySpendLedger();
    const { a } = app(ledger);
    const res = await post(a, payload, { "x-openclicky-openai-key": "sk-own", "x-openclicky-anthropic-key": "sk-ant-own" });
    expect(res.status).toBe(200);
    expect((await ledger.summary("u1", LIMITS)).spentTodayMicro).toBe(0);
  });
});
