import { describe, it, expect } from "vitest";
import { MemorySpendLedger, limitsFromEnv, type SpendEvent } from "../src/ledger.js";

const limits = { monthlyMicro: 10_000_000, dailyMicro: 2_000_000, globalMonthlyMicro: 1_000_000_000, ttsCharsMonthly: 20_000, guestTotalMicro: 1_000_000, guestDays: 14, guestTtsChars: 2_000 };
const event: SpendEvent = { route: "/chat", model: "claude-sonnet-5-5", inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, characters: 0 };
const now = new Date("2026-10-08T10:00:00Z");

describe("limitsFromEnv", () => {
  it("reads dollars and defaults to the spec's numbers", () => {
    expect(limitsFromEnv({})).toEqual(limits);
    expect(limitsFromEnv({ ACCOUNT_DAILY_USD: "5" }).dailyMicro).toBe(5_000_000);
  });
  it("falls back to 20000 for invalid ACCOUNT_MONTHLY_TTS_CHARS", () => {
    expect(limitsFromEnv({ ACCOUNT_MONTHLY_TTS_CHARS: "abc" }).ttsCharsMonthly).toBe(20_000);
    expect(limitsFromEnv({ ACCOUNT_MONTHLY_TTS_CHARS: "" }).ttsCharsMonthly).toBe(20_000);
  });
});

describe("MemorySpendLedger", () => {
  it("first request creates nothing and uses defaults", async () => {
    const ledger = new MemorySpendLedger();
    const summary = await ledger.summary("new-user", limits, now);
    expect(summary.spentMonthMicro).toBe(0);
    expect(summary.monthlyLimitMicro).toBe(10_000_000);
    expect(summary.monthEnd).toBe("2026-11-01T00:00:00.000Z");
    expect(summary.dayEnd).toBe("2026-10-09T00:00:00.000Z");
  });

  it("refuses the daily limit, then the monthly limit, with reset times", async () => {
    const ledger = new MemorySpendLedger();
    const r1 = await ledger.reserve("u", 1_900_000, limits, now);
    expect(r1.ok).toBe(true);
    if (r1.ok) await ledger.settle(r1.reservationId, "u", 1_900_000, event);
    const r2 = await ledger.reserve("u", 200_000, limits, now);
    expect(r2).toEqual({ ok: false, error: "daily_limit", resetsAt: "2026-10-09T00:00:00.000Z" });
    const tight = { ...limits, dailyMicro: 100_000_000, monthlyMicro: 2_000_000 };
    expect(await ledger.reserve("u", 200_000, tight, now)).toEqual({ ok: false, error: "personal_limit", resetsAt: "2026-11-01T00:00:00.000Z" });
  });

  it("refuses when everyone's budget would be crossed", async () => {
    const ledger = new MemorySpendLedger();
    const small = { ...limits, globalMonthlyMicro: 1_000_000 };
    const a = await ledger.reserve("a", 900_000, small, now);
    expect(a.ok).toBe(true);
    expect((await ledger.reserve("b", 200_000, small, now)).ok).toBe(false);
  });

  it("concurrent reservations cannot both take the last cent", async () => {
    const ledger = new MemorySpendLedger();
    const edge = { ...limits, dailyMicro: 10_000 };
    const [x, y] = await Promise.all([ledger.reserve("u", 10_000, edge, now), ledger.reserve("u", 10_000, edge, now)]);
    expect([x.ok, y.ok].filter(Boolean)).toHaveLength(1);
  });

  it("settle releases the hold and records the real cost", async () => {
    const ledger = new MemorySpendLedger();
    const r = await ledger.reserve("u", 50_000, limits, now);
    if (!r.ok) throw new Error("expected ok");
    await ledger.settle(r.reservationId, "u", 12_000, event);
    expect((await ledger.summary("u", limits, now)).spentTodayMicro).toBe(12_000);
  });

  it("holds older than 10 minutes are released", async () => {
    const ledger = new MemorySpendLedger();
    const edge = { ...limits, dailyMicro: 10_000 };
    expect((await ledger.reserve("u", 10_000, edge, now)).ok).toBe(true);
    const later = new Date(now.getTime() + 11 * 60_000);
    expect((await ledger.reserve("u", 10_000, edge, later)).ok).toBe(true);
  });

  it("a reply settled after its hold was swept is still billed", async () => {
    const ledger = new MemorySpendLedger();
    const r = await ledger.reserve("u", 50_000, limits, now);
    if (!r.ok) throw new Error("expected ok");
    const later = new Date(now.getTime() + 11 * 60_000);
    expect((await ledger.summary("u", limits, later)).spentTodayMicro).toBe(0); // the sweep dropped the hold
    await ledger.settle(r.reservationId, "u", 30_000, event, later);
    expect((await ledger.summary("u", limits, later)).spentTodayMicro).toBe(30_000);
  });

  it("summary says whether the user has an account row", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    expect((await ledger.summary("nobody", limits, now)).onPlan).toBe(false);
    ledger.setAccount("known", {});
    expect((await ledger.summary("known", limits, now)).onPlan).toBe(true);
    expect((await new MemorySpendLedger().summary("anyone", limits, now)).onPlan).toBe(true);
  });

  it("a per-user override and a block apply", async () => {
    const ledger = new MemorySpendLedger();
    ledger.setAccount("vip", { monthlyMicro: 50_000_000, dailyMicro: 20_000_000, blocked: false });
    expect((await ledger.reserve("vip", 15_000_000, limits, now)).ok).toBe(true);
    ledger.setAccount("bad", { blocked: true });
    expect(await ledger.reserve("bad", 1, limits, now)).toMatchObject({ ok: false, error: "blocked" });
  });

  it("characters: personal monthly pool and the global remainder", async () => {
    const ledger = new MemorySpendLedger();
    expect((await ledger.reserveCharacters("u", 19_000, limits, 1_000_000, now)).ok).toBe(true);
    expect(await ledger.reserveCharacters("u", 2_000, limits, 1_000_000, now)).toMatchObject({ ok: false, error: "personal_limit" });
    expect(await ledger.reserveCharacters("v", 500, limits, 100, now)).toMatchObject({ ok: false, error: "monthly_budget" });
  });

  it("requireAccountRow enforces account existence", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    expect(await ledger.reserve("unknown", 100, limits, now)).toMatchObject({ ok: false, error: "not_on_plan" });
    expect(await ledger.reserveCharacters("unknown", 100, limits, 1000, now)).toMatchObject({ ok: false, error: "not_on_plan" });
    ledger.setAccount("known", {});
    expect((await ledger.reserve("known", 100, limits, now)).ok).toBe(true);
  });
});

describe("guests (unconfirmed accounts)", () => {
  const device = "a".repeat(64);
  it("limitsFromEnv reads the guest settings", () => {
    expect(limitsFromEnv({ GUEST_TOTAL_USD: "0.5", GUEST_DAYS: "7", GUEST_TTS_CHARS: "100" })).toMatchObject({ guestTotalMicro: 500_000, guestDays: 7, guestTtsChars: 100 });
  });
  it("a guest is refused past $1 with confirm_email and no reset time", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("g", { confirmed: false, deviceHash: device, createdAt: now.getTime() });
    const r = await ledger.reserve("g", 900_000, limits, now);
    expect(r.ok).toBe(true);
    if (r.ok) await ledger.settle(r.reservationId, "g", 900_000, event, now);
    expect(await ledger.reserve("g", 200_000, limits, now)).toEqual({ ok: false, error: "confirm_email", resetsAt: null });
  });
  it("a replaced guest's spend still counts for the Mac", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("old", { confirmed: false, deviceHash: device, createdAt: now.getTime() });
    const r = await ledger.reserve("old", 800_000, limits, now);
    if (r.ok) await ledger.settle(r.reservationId, "old", 800_000, event, now);
    ledger.setAccount("old", { replaced: true });
    ledger.setAccount("new", { confirmed: false, deviceHash: device, createdAt: now.getTime() });
    expect((await ledger.reserve("new", 300_000, limits, now)).ok).toBe(false);
    expect((await ledger.reserve("new", 100_000, limits, now)).ok).toBe(true);
    expect(await ledger.reserve("old", 1, limits, now)).toMatchObject({ ok: false, error: "confirm_email" });
  });
  it("a guest older than guestDays is refused", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("g", { confirmed: false, deviceHash: device, createdAt: now.getTime() - 15 * 86_400_000 });
    expect(await ledger.reserve("g", 1, limits, now)).toMatchObject({ ok: false, error: "confirm_email" });
  });
  it("confirming lifts the guest cap; spend before confirming stays on the Mac's pool", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("g", { confirmed: false, deviceHash: device, createdAt: now.getTime() });
    const r = await ledger.reserve("g", 900_000, limits, now);
    if (r.ok) await ledger.settle(r.reservationId, "g", 900_000, event, now);
    const later = new Date(now.getTime() + 60_000);
    ledger.confirm("g", later);
    expect((await ledger.reserve("g", 1_000_000, limits, later)).ok).toBe(true);
    ledger.setAccount("g2", { confirmed: false, deviceHash: device, createdAt: later.getTime() });
    expect((await ledger.reserve("g2", 200_000, limits, later)).ok).toBe(false); // 0.9 already used on this Mac
  });
  it("guest characters are pooled per Mac", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("g", { confirmed: false, deviceHash: device, createdAt: now.getTime() });
    expect((await ledger.reserveCharacters("g", 1_500, limits, 1_000_000, now)).ok).toBe(true);
    expect(await ledger.reserveCharacters("g", 600, limits, 1_000_000, now)).toMatchObject({ ok: false, error: "confirm_email" });
  });
  it("summary reports confirmation, the Mac's guest spend and the email", async () => {
    const ledger = new MemorySpendLedger({ requireAccountRow: true });
    ledger.setAccount("g", { confirmed: false, deviceHash: device, createdAt: now.getTime(), email: "gran@example.com" });
    const r = await ledger.reserve("g", 250_000, limits, now);
    if (r.ok) await ledger.settle(r.reservationId, "g", 250_000, event, now);
    expect(await ledger.summary("g", limits, now)).toMatchObject({ confirmed: false, guestSpentMicro: 250_000, guestLimitMicro: 1_000_000, email: "gran@example.com" });
  });
  it("accounts without the guest fields stay confirmed (existing behaviour)", async () => {
    const ledger = new MemorySpendLedger();
    expect((await ledger.summary("anyone", limits, now)).confirmed).toBe(true);
    expect((await ledger.reserve("anyone", 1_500_000, limits, now)).ok).toBe(true);
  });
});
