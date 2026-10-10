import { describe, it, expect } from "vitest";
import { Hono } from "hono";
import { requireAccount, reserveOr402, accountSummary } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";
import type { Principal } from "../src/auth.js";

function appWith(ledger: MemorySpendLedger, principal: Principal = { sub: "u1", via: "supabase" }) {
  const app = new Hono();
  app.use("*", async (c, next) => { c.set("principal" as never, principal as never); await next(); });
  app.use("*", requireAccount(() => ledger));
  app.post("/chat", async (c) => {
    const r = await reserveOr402(c, ledger, 5_000);
    return r instanceof Response ? r : c.json({ reservationId: r.reservationId });
  });
  app.post("/v1/responses", (c) => c.json({ reached: true }));
  app.get("/billing/me", async (c) => c.json(await accountSummary(c, ledger)));
  return app;
}

describe("requireAccount", () => {
  it("lets a grant request through a grant route and reserves", async () => {
    const res = await appWith(new MemorySpendLedger()).request("/chat", { method: "POST" });
    expect(res.status).toBe(200);
    expect((await res.json()).reservationId).toBeTruthy();
  });

  it("refuses OpenAI routes on the grant with not_on_plan", async () => {
    const res = await appWith(new MemorySpendLedger()).request("/v1/responses", { method: "POST" });
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "not_on_plan" });
  });

  it("lets BYOK requests reach any route", async () => {
    const res = await appWith(new MemorySpendLedger()).request("/v1/responses", { method: "POST", headers: { "x-openclicky-openai-key": "sk-own" } });
    expect(res.status).toBe(200);
  });

  it("answers 402 with the limit and reset time", async () => {
    const ledger = new MemorySpendLedger();
    ledger.setAccount("u1", { dailyMicro: 1_000 });
    const res = await appWith(ledger).request("/chat", { method: "POST" });
    expect(res.status).toBe(402);
    const body = await res.json();
    expect(body.error).toBe("daily_limit");
    expect(typeof body.resets_at).toBe("string");
  });

  it("rate-limits to 20 requests a minute per user", async () => {
    const app = appWith(new MemorySpendLedger(), { sub: "busy", via: "supabase" });
    const statuses: number[] = [];
    for (let i = 0; i < 21; i++) statuses.push((await app.request("/billing/me")).status);
    expect(statuses.slice(0, 20).every((s) => s === 200)).toBe(true);
    expect(statuses[20]).toBe(429);
  });

  it("billing/me reports dollars and resets", async () => {
    const body = await (await appWith(new MemorySpendLedger()).request("/billing/me")).json();
    expect(body).toMatchObject({ byok: false, spentMonthUsd: 0, monthlyLimitUsd: 10, dailyLimitUsd: 2, budgetExhausted: false, blocked: false });
  });
});
