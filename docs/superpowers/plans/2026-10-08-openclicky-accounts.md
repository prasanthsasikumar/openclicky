# OpenClicky Accounts on the Anthropic Grant — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Anyone can sign up in the OpenClicky app with an email and get polished dictation, "ask about my screen" with pointing, spoken answers and quick actions, paid by FlowsXR's Anthropic grant (thinking) and ElevenLabs grant (voice), within hard spending limits.

**Architecture:** The existing Hono backend gains a dollar-denominated spend ledger (reserve before a request, settle from the reply's real token usage) backed by Postgres functions in the shared Supabase, a server-side model policy, a `/v1/polish` route, an account plan gate that keeps grant requests on Claude/ElevenLabs only, and an ElevenLabs character pool. The macOS app gains sign-up, an account capability profile that routes every lane accordingly (Apple speech for hearing, `/v1/polish`, Claude tools for quick actions, ElevenLabs with the system voice as fallback), and clear limit messages.

**Tech Stack:** TypeScript + Hono + vitest (backend, `backend/`), Supabase Postgres (PostgREST RPC) + Supabase Auth (GoTrue), Swift/SwiftUI + Swift Testing (`macos/OpenClicky/`).

**Spec:** `docs/superpowers/specs/2026-10-08-openclicky-accounts-design.md`

## Global Constraints

- Limits (spec §5.7): `ACCOUNT_DAILY_USD=2`, `ACCOUNT_MONTHLY_USD=10`, `GLOBAL_MONTHLY_BUDGET_USD=1000`, `MAX_ACCOUNTS=100`, `ACCOUNT_MONTHLY_TTS_CHARS=20000`, 20 requests/minute/user.
- Months and days are UTC calendar months/days.
- Money is stored as integer micro-dollars (`1 USD = 1_000_000`). Price × tokens: `$X per million tokens × N tokens = X·N micro-dollars`.
- Models (spec §5.1): `POLISH_MODEL=claude-haiku-4-5`, `EDIT_MODEL=claude-haiku-4-5`, `ASK_MODEL=claude-sonnet-5-5`, `GATE_MODEL=claude-haiku-4-5`. For grant requests the client's `model` is replaced, never trusted.
- Prices USD/MTok (input / output / cache write / cache read): Haiku 4.5 1 / 5 / 1.25 / 0.10; Sonnet 5.5 2 / 10 / 2.50 / 0.20. An unknown model is refused before forwarding.
- Grant requests may use only: `/v1/polish`, `/chat`, `/v1/messages`, `/tts`, `/billing/me`. Every other model route answers `402 {error:"not_on_plan"}` for grant requests. BYOK requests (header `x-openclicky-openai-key`) are never metered or gated.
- Error body contract: `402 { error: "personal_limit" | "daily_limit" | "monthly_budget" | "not_on_plan" | "accounts_full" | "blocked" | "tts_budget", resets_at?: string }`, `429 { error: "slow_down" }`. Upstream error text never reaches the client.
- Invite-only accounts are retired: no `invite` admin command; `oc_subscriptions.monthly_credits_override` is no longer read.
- App copy is lowercase, one sentence, names the consequence (spec §3).
- Do not edit `macos/OpenClicky` Swift files to fix unrelated concurrency warnings (CLAUDE.md "Do NOT").
- Run Mac tests with `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -derivedDataPath build/DerivedData -only-testing:OpenClickyTests`; relaunch `/Applications/OpenClicky.app` afterwards.
- Backend tests: `npm test -w backend`; types: `npm run typecheck`; lint: `npm run lint`.

## Review Focus

1. **Concurrent requests at the edge of a limit** — two questions fired at once when $0.01 is left must not both pass; exactly one gets through. (Task 2 test `concurrent reservations cannot both take the last cent`.)
2. **A stream the user cancels mid-answer** — tokens were spent upstream, so the reservation must still settle to the real cost, not stay held or vanish. (Task 4 test `a cancelled stream still settles`.)
3. **Anthropic's nested usage object** (`cache_creation: {...}`, `server_tool_use: {...}`) — the current flat-object regex misses it and would bill the fallback; usage must be read from SSE `message_start` + `message_delta`. (Task 1 test `reads nested Anthropic streaming usage`.)
4. **A signed-in user with no account row yet** (first request ever) gets the defaults, not a crash or $0 limit. (Task 2 test `first request creates nothing and uses defaults`.)
5. **The app when the backend says 402 mid-take** — dictation must still paste (local cleanup only), never lose the user's words. (Task 11 test `polish 402 falls back to local formatting`.)

---

## File Structure

Backend (`backend/`):
- Create `src/prices.ts` — price table, `costMicroUsd`, `estimateMicroUsd`, `parseAnthropicUsage`.
- Create `src/modelPolicy.ts` — `Purpose`, `modelFor`, `isGrantModel`.
- Create `src/ledger.ts` — `SpendLedger` interface, `MemorySpendLedger`, `SupabaseSpendLedger`, `limitsFromEnv`.
- Create `src/account.ts` — `requireAccount` middleware (plan gate, rate limit, sets `account` variable), `accountSummary`, `reserveOr402`.
- Create `src/polish.ts` — `POST /v1/polish` handler.
- Create `src/anthropicGrant.ts` — `proxyAnthropicOnGrant` (model policy, caps, cache_control, reserve → forward → settle).
- Create `src/tts.ts` — account-aware `/tts` with the character pool and ElevenLabs subscription cache.
- Modify `src/db.ts` — add `rpc()`.
- Modify `src/env.ts` — new env vars.
- Modify `src/app.ts` — wire the new middleware/routes; `/auth/config` gains `accountsOpen`; add `GET /auth/confirmed`.
- Modify `supabase/schema.sql` — tables, functions, trigger.
- Modify `scripts/admin.mjs` — `budget`, `limit`, `daily`, `block`, `unblock`, `remove`, `max-accounts`; remove `invite`.
- Tests: `test/prices.test.ts`, `test/ledger.test.ts`, `test/account.test.ts`, `test/polish.test.ts`, `test/grant-anthropic.test.ts`, `test/tts.test.ts`; update `test/app.test.ts`.

Mac app (`macos/OpenClicky/OpenClicky/`):
- Create `AccountCapabilities.swift` — decoded `/billing/me`, the per-lane decisions, limit messages.
- Create `Dictation/AccountTakePolisher.swift` — polisher that calls `/v1/polish`.
- Create `Dictation/UI/AccountSheet.swift` — sign up / sign in / forgot password.
- Modify `OpenClickyAuthSession.swift` — `signUp`, `waitForConfirmation`, `recover`.
- Modify `BillingStatus.swift` — new `BillingSummary` shape, usage bar.
- Modify `ClaudeAPI.swift` — optional `tools`, return tool calls from the stream.
- Modify `CompanionManager.swift` — account lane routing (Apple hearing, no realtime, tools, limit messages).
- Modify `Dictation/DictationTakeController.swift` — `makePolisher` picks the account polisher.
- Modify `RealtimeVoiceClient.swift` — `anthropicToolDefinitions()` next to `fastActionToolDefinitions()`.
- Modify `Dictation/UI/OnboardingWindow.swift`, `Dictation/UI/SettingsAccountPage.swift` — entry points to `AccountSheet`, usage bar.
- Tests: `OpenClickyTests/AccountCapabilitiesTests.swift`, `OpenClickyTests/AccountTakePolisherTests.swift`, `OpenClickyTests/ClaudeToolStreamTests.swift`, `OpenClickyTests/AccountAuthTests.swift`.

---

### Task 1: Prices, usage parsing and model policy

**Files:**
- Create: `backend/src/prices.ts`, `backend/src/modelPolicy.ts`
- Modify: `backend/src/env.ts` (add env vars)
- Test: `backend/test/prices.test.ts`

**Interfaces:**
- Produces:
  - `type TokenUsage = { inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number }`
  - `costMicroUsd(model: string, usage: TokenUsage): number | undefined`
  - `estimateMicroUsd(model: string, body: Record<string, unknown>): number | undefined`
  - `parseAnthropicUsage(text: string): TokenUsage | undefined`
  - `type Purpose = "polish" | "edit" | "ask" | "gate"`; `modelFor(purpose: Purpose, env: Env): string`; `isGrantModel(model: string): boolean`

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/prices.test.ts
import { describe, it, expect } from "vitest";
import { costMicroUsd, estimateMicroUsd, parseAnthropicUsage } from "../src/prices.js";
import { modelFor, isGrantModel } from "../src/modelPolicy.js";

describe("costMicroUsd", () => {
  it("prices Haiku input, output and cache tokens in micro-dollars", () => {
    // 1000 in × $1 + 200 out × $5 + 500 cache-write × $1.25 + 2000 cache-read × $0.10
    expect(costMicroUsd("claude-haiku-4-5", { inputTokens: 1000, outputTokens: 200, cacheWriteTokens: 500, cacheReadTokens: 2000 })).toBe(1000 + 1000 + 625 + 200);
  });
  it("prices Sonnet 5.5", () => {
    expect(costMicroUsd("claude-sonnet-5-5", { inputTokens: 1000, outputTokens: 100, cacheWriteTokens: 0, cacheReadTokens: 0 })).toBe(2000 + 1000);
  });
  it("refuses an unknown model", () => {
    expect(costMicroUsd("claude-opus-5-5", { inputTokens: 1, outputTokens: 1, cacheWriteTokens: 0, cacheReadTokens: 0 })).toBeUndefined();
  });
});

describe("estimateMicroUsd", () => {
  it("counts text at 3.5 chars per token, 1600 tokens per image, plus max_tokens of output", () => {
    const body = {
      max_tokens: 1000,
      system: "x".repeat(350),
      messages: [{ role: "user", content: [{ type: "image", source: { type: "base64", data: "AAAA" } }, { type: "text", text: "y".repeat(700) }] }],
    };
    // input = 100 + 1600 + 200 = 1900 tokens × $2 = 3800; output 1000 × $10 = 10000
    expect(estimateMicroUsd("claude-sonnet-5-5", body)).toBe(13_800);
  });
  it("is always at least the real cost for the same body", () => {
    const body = { max_tokens: 200, messages: [{ role: "user", content: "hello there" }] };
    const estimate = estimateMicroUsd("claude-haiku-4-5", body)!;
    const real = costMicroUsd("claude-haiku-4-5", { inputTokens: 4, outputTokens: 200, cacheWriteTokens: 0, cacheReadTokens: 0 })!;
    expect(estimate).toBeGreaterThanOrEqual(real);
  });
});

describe("parseAnthropicUsage", () => {
  it("reads nested Anthropic streaming usage", () => {
    const sse = [
      'event: message_start',
      'data: {"type":"message_start","message":{"usage":{"input_tokens":12,"cache_creation_input_tokens":800,"cache_read_input_tokens":3000,"cache_creation":{"ephemeral_5m_input_tokens":800},"output_tokens":1}}}',
      'event: message_delta',
      'data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":95,"server_tool_use":{"web_search_requests":0}}}',
    ].join("\n");
    expect(parseAnthropicUsage(sse)).toEqual({ inputTokens: 12, outputTokens: 95, cacheWriteTokens: 800, cacheReadTokens: 3000 });
  });
  it("reads a non-streamed JSON body", () => {
    expect(parseAnthropicUsage('{"usage":{"input_tokens":5,"output_tokens":7}}')).toEqual({ inputTokens: 5, outputTokens: 7, cacheWriteTokens: 0, cacheReadTokens: 0 });
  });
  it("returns undefined when there is no usage", () => {
    expect(parseAnthropicUsage("data: {\"type\":\"ping\"}")).toBeUndefined();
  });
});

describe("modelPolicy", () => {
  it("uses defaults and env overrides", () => {
    expect(modelFor("polish", {})).toBe("claude-haiku-4-5");
    expect(modelFor("ask", {})).toBe("claude-sonnet-5-5");
    expect(modelFor("ask", { ASK_MODEL: "claude-haiku-4-5" })).toBe("claude-haiku-4-5");
  });
  it("only priced models are grant models", () => {
    expect(isGrantModel("claude-haiku-4-5")).toBe(true);
    expect(isGrantModel("claude-opus-5-5")).toBe(false);
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- prices`
Expected: FAIL — `Cannot find module '../src/prices.js'`.

- [ ] **Step 3: Implement**

```ts
// backend/src/prices.ts
/**
 * What a grant request costs, in integer micro-dollars. Prices are USD per million tokens, so
 * price × tokens is already micro-dollars. Models not in the table are refused, never guessed.
 */
export type TokenUsage = { inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number };
type Price = { input: number; output: number; cacheWrite: number; cacheRead: number };

export const PRICES_USD_PER_MTOK: Record<string, Price> = {
  "claude-haiku-4-5": { input: 1, output: 5, cacheWrite: 1.25, cacheRead: 0.1 },
  "claude-sonnet-5-5": { input: 2, output: 10, cacheWrite: 2.5, cacheRead: 0.2 },
};

export function costMicroUsd(model: string, usage: TokenUsage): number | undefined {
  const price = PRICES_USD_PER_MTOK[model];
  if (!price) return undefined;
  return Math.ceil(
    usage.inputTokens * price.input + usage.outputTokens * price.output + usage.cacheWriteTokens * price.cacheWrite + usage.cacheReadTokens * price.cacheRead,
  );
}

const CHARS_PER_TOKEN = 3.5;
const TOKENS_PER_IMAGE = 1600;

function textTokens(text: string): number {
  return Math.ceil(text.length / CHARS_PER_TOKEN);
}

function contentTokens(content: unknown): number {
  if (typeof content === "string") return textTokens(content);
  if (!Array.isArray(content)) return 0;
  let tokens = 0;
  for (const block of content as Array<Record<string, unknown>>) {
    if (block.type === "image") tokens += TOKENS_PER_IMAGE;
    else if (typeof block.text === "string") tokens += textTokens(block.text);
    else if (block.type === "tool_result") tokens += contentTokens(block.content);
    else if (block.type === "tool_use") tokens += textTokens(JSON.stringify(block.input ?? {}));
  }
  return tokens;
}

/** Worst-case cost of a Messages body before it is sent: every input token uncached, every output token used. */
export function estimateMicroUsd(model: string, body: Record<string, unknown>): number | undefined {
  const price = PRICES_USD_PER_MTOK[model];
  if (!price) return undefined;
  let inputTokens = contentTokens(body.system);
  for (const message of (body.messages as Array<{ content: unknown }> | undefined) ?? []) inputTokens += contentTokens(message.content);
  if (Array.isArray(body.tools)) inputTokens += textTokens(JSON.stringify(body.tools));
  const outputTokens = Number(body.max_tokens ?? 0);
  return Math.ceil(inputTokens * price.input + outputTokens * price.output);
}

/** Usage from an Anthropic reply: SSE (`message_start` + `message_delta`) or a plain JSON body. */
export function parseAnthropicUsage(text: string): TokenUsage | undefined {
  const usage: TokenUsage = { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 };
  let found = false;
  const absorb = (u: Record<string, unknown> | undefined) => {
    if (!u) return;
    const num = (k: string) => (typeof u[k] === "number" ? (u[k] as number) : undefined);
    const i = num("input_tokens"), o = num("output_tokens"), w = num("cache_creation_input_tokens"), r = num("cache_read_input_tokens");
    if (i !== undefined) { usage.inputTokens = Math.max(usage.inputTokens, i); found = true; }
    if (o !== undefined) { usage.outputTokens = Math.max(usage.outputTokens, o); found = true; }
    if (w !== undefined) usage.cacheWriteTokens = Math.max(usage.cacheWriteTokens, w);
    if (r !== undefined) usage.cacheReadTokens = Math.max(usage.cacheReadTokens, r);
  };
  const visit = (json: Record<string, unknown>) => {
    absorb(json.usage as Record<string, unknown> | undefined);
    absorb((json.message as Record<string, unknown> | undefined)?.usage as Record<string, unknown> | undefined);
  };
  const trimmed = text.trim();
  if (trimmed.startsWith("{")) {
    try { visit(JSON.parse(trimmed)); } catch { /* not one JSON body; fall through to SSE lines */ }
  }
  for (const line of text.split("\n")) {
    if (!line.startsWith("data: ")) continue;
    try { visit(JSON.parse(line.slice(6))); } catch { /* keep-alive or partial line */ }
  }
  return found ? usage : undefined;
}
```

```ts
// backend/src/modelPolicy.ts
import type { Env } from "./env.js";
import { PRICES_USD_PER_MTOK } from "./prices.js";

/** Which model the server uses for a grant request; the client's choice is not trusted. */
export type Purpose = "polish" | "edit" | "ask" | "gate";

const DEFAULTS: Record<Purpose, string> = {
  polish: "claude-haiku-4-5",
  edit: "claude-haiku-4-5",
  ask: "claude-sonnet-5-5",
  gate: "claude-haiku-4-5",
};

export function modelFor(purpose: Purpose, env: Env): string {
  const override = { polish: env.POLISH_MODEL, edit: env.EDIT_MODEL, ask: env.ASK_MODEL, gate: env.GATE_MODEL }[purpose];
  return override?.trim() || DEFAULTS[purpose];
}

export function isGrantModel(model: string): boolean {
  return model in PRICES_USD_PER_MTOK;
}
```

Add to `Env` in `backend/src/env.ts` (inside the type, after `SESSION_TOKEN_TTL_SECONDS`):

```ts
  /** Accounts on the grant (spec 2026-10-08). Dollar amounts are plain numbers ("10"). */
  POLISH_MODEL?: string;
  EDIT_MODEL?: string;
  ASK_MODEL?: string;
  GATE_MODEL?: string;
  ACCOUNT_MONTHLY_USD?: string;
  ACCOUNT_DAILY_USD?: string;
  GLOBAL_MONTHLY_BUDGET_USD?: string;
  ACCOUNT_MONTHLY_TTS_CHARS?: string;
  TTS_GLOBAL_MARGIN?: string;
  MAX_ACCOUNTS?: string;
  /** "true" opens self-serve sign-up; anything else keeps it closed (existing accounts still work). */
  ACCOUNTS_OPEN?: string;
  /** Where the confirmation email's link lands (default: this backend's /auth/confirmed). */
  ACCOUNT_CONFIRM_REDIRECT_URL?: string;
```

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend -- prices && npm run typecheck`
Expected: PASS, no type errors.

- [ ] **Step 5: Commit**

```bash
git add backend/src/prices.ts backend/src/modelPolicy.ts backend/src/env.ts backend/test/prices.test.ts
git commit -m "feat(backend): dollar prices, Anthropic usage parsing and a server-side model policy for grant requests"
```

---

### Task 2: The spend ledger (memory + Supabase) and the schema

**Files:**
- Create: `backend/src/ledger.ts`
- Modify: `backend/src/db.ts` (add `rpc`), `backend/supabase/schema.sql`
- Test: `backend/test/ledger.test.ts`

**Interfaces:**
- Consumes: nothing from Task 1 except the micro-dollar convention.
- Produces:
  - `type LimitError = "personal_limit" | "daily_limit" | "monthly_budget" | "blocked"`
  - `type ReserveResult = { ok: true; reservationId: string } | { ok: false; error: LimitError; resetsAt: string }`
  - `interface SpendEvent { route: string; model?: string; inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number; characters: number }`
  - `interface SpendSummary { spentMonthMicro: number; spentTodayMicro: number; monthlyLimitMicro: number; dailyLimitMicro: number; globalSpentMicro: number; globalLimitMicro: number; ttsCharsMonth: number; ttsCharsLimit: number; monthEnd: string; dayEnd: string; blocked: boolean }`
  - `interface Limits { monthlyMicro: number; dailyMicro: number; globalMonthlyMicro: number; ttsCharsMonthly: number }`
  - `interface SpendLedger { reserve(userId: string, estimateMicro: number, limits: Limits, now?: Date): Promise<ReserveResult>; settle(reservationId: string, actualMicro: number, event: SpendEvent): Promise<void>; reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number, now?: Date): Promise<ReserveResult>; summary(userId: string, limits: Limits, now?: Date): Promise<SpendSummary> }`
  - `limitsFromEnv(env: Env): Limits`
  - `class MemorySpendLedger implements SpendLedger` (tests), `class SupabaseSpendLedger implements SpendLedger` (production)
  - `SupabaseRest.rpc<T>(fn: string, args: Record<string, unknown>): Promise<T>`

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/ledger.test.ts
import { describe, it, expect } from "vitest";
import { MemorySpendLedger, limitsFromEnv, type SpendEvent } from "../src/ledger.js";

const limits = { monthlyMicro: 10_000_000, dailyMicro: 2_000_000, globalMonthlyMicro: 1_000_000_000, ttsCharsMonthly: 20_000 };
const event: SpendEvent = { route: "/chat", model: "claude-sonnet-5-5", inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, characters: 0 };
const now = new Date("2026-10-08T10:00:00Z");

describe("limitsFromEnv", () => {
  it("reads dollars and defaults to the spec's numbers", () => {
    expect(limitsFromEnv({})).toEqual(limits);
    expect(limitsFromEnv({ ACCOUNT_DAILY_USD: "5" }).dailyMicro).toBe(5_000_000);
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
    if (r1.ok) await ledger.settle(r1.reservationId, 1_900_000, event);
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
    await ledger.settle(r.reservationId, 12_000, event);
    expect((await ledger.summary("u", limits, now)).spentTodayMicro).toBe(12_000);
  });

  it("holds older than 10 minutes are released", async () => {
    const ledger = new MemorySpendLedger();
    const edge = { ...limits, dailyMicro: 10_000 };
    expect((await ledger.reserve("u", 10_000, edge, now)).ok).toBe(true);
    const later = new Date(now.getTime() + 11 * 60_000);
    expect((await ledger.reserve("u", 10_000, edge, later)).ok).toBe(true);
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
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- ledger`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement the ledger**

```ts
// backend/src/ledger.ts
import type { Env } from "./env.js";
import type { SupabaseRest } from "./db.js";

/**
 * Grant spending, reserved before a request and settled from its real usage, so overlapping
 * requests can never spend past a limit. All money is integer micro-dollars; months and days are UTC.
 */
export type LimitError = "personal_limit" | "daily_limit" | "monthly_budget" | "blocked";
export type ReserveResult = { ok: true; reservationId: string } | { ok: false; error: LimitError; resetsAt: string };
export interface SpendEvent { route: string; model?: string; inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number; characters: number }
export interface SpendSummary {
  spentMonthMicro: number; spentTodayMicro: number; monthlyLimitMicro: number; dailyLimitMicro: number;
  globalSpentMicro: number; globalLimitMicro: number; ttsCharsMonth: number; ttsCharsLimit: number;
  monthEnd: string; dayEnd: string; blocked: boolean;
}
export interface Limits { monthlyMicro: number; dailyMicro: number; globalMonthlyMicro: number; ttsCharsMonthly: number }
export interface SpendLedger {
  reserve(userId: string, estimateMicro: number, limits: Limits, now?: Date): Promise<ReserveResult>;
  settle(reservationId: string, actualMicro: number, event: SpendEvent): Promise<void>;
  reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number, now?: Date): Promise<ReserveResult>;
  summary(userId: string, limits: Limits, now?: Date): Promise<SpendSummary>;
}

const usd = (value: string | undefined, fallback: number) => Math.round((Number.isFinite(Number(value)) && value ? Number(value) : fallback) * 1_000_000);
export function limitsFromEnv(env: Env): Limits {
  return {
    monthlyMicro: usd(env.ACCOUNT_MONTHLY_USD, 10),
    dailyMicro: usd(env.ACCOUNT_DAILY_USD, 2),
    globalMonthlyMicro: usd(env.GLOBAL_MONTHLY_BUDGET_USD, 1000),
    ttsCharsMonthly: Number(env.ACCOUNT_MONTHLY_TTS_CHARS ?? 20_000),
  };
}

export const HOLD_TTL_MS = 10 * 60_000;
export const monthStart = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
export const monthEnd = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1));
export const dayStart = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
export const dayEnd = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1));

type Entry = { id: string; userId: string; at: number; micro: number; chars: number; settled: boolean };
type Account = { monthlyMicro?: number; dailyMicro?: number; blocked?: boolean };

/** In-memory ledger for tests and unmetered dev backends. JS runs reserve's check-and-insert without awaiting in between, so it is atomic. */
export class MemorySpendLedger implements SpendLedger {
  private entries: Entry[] = [];
  private accounts = new Map<string, Account>();
  private nextId = 1;

  setAccount(userId: string, account: Account) { this.accounts.set(userId, { ...this.accounts.get(userId), ...account }); }

  private sweep(now: Date) {
    this.entries = this.entries.filter((e) => e.settled || now.getTime() - e.at < HOLD_TTL_MS);
  }
  private sum(filter: (e: Entry) => boolean, field: "micro" | "chars") {
    return this.entries.filter(filter).reduce((n, e) => n + e[field], 0);
  }
  private personal(userId: string, limits: Limits) {
    const a = this.accounts.get(userId) ?? {};
    return { monthly: a.monthlyMicro ?? limits.monthlyMicro, daily: a.dailyMicro ?? limits.dailyMicro, blocked: a.blocked ?? false };
  }

  async reserve(userId: string, estimateMicro: number, limits: Limits, now = new Date()): Promise<ReserveResult> {
    this.sweep(now);
    const mine = this.personal(userId, limits);
    if (mine.blocked) return { ok: false, error: "blocked", resetsAt: monthEnd(now).toISOString() };
    const month = monthStart(now).getTime(), day = dayStart(now).getTime();
    const today = this.sum((e) => e.userId === userId && e.at >= day, "micro");
    const thisMonth = this.sum((e) => e.userId === userId && e.at >= month, "micro");
    const everyone = this.sum((e) => e.at >= month, "micro");
    if (today + estimateMicro > mine.daily) return { ok: false, error: "daily_limit", resetsAt: dayEnd(now).toISOString() };
    if (thisMonth + estimateMicro > mine.monthly) return { ok: false, error: "personal_limit", resetsAt: monthEnd(now).toISOString() };
    if (everyone + estimateMicro > limits.globalMonthlyMicro) return { ok: false, error: "monthly_budget", resetsAt: monthEnd(now).toISOString() };
    const id = String(this.nextId++);
    this.entries.push({ id, userId, at: now.getTime(), micro: estimateMicro, chars: 0, settled: false });
    return { ok: true, reservationId: id };
  }

  async settle(reservationId: string, actualMicro: number, event: SpendEvent): Promise<void> {
    const entry = this.entries.find((e) => e.id === reservationId);
    if (!entry) return;
    entry.micro = actualMicro;
    entry.chars = event.characters;
    entry.settled = true;
  }

  async reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number, now = new Date()): Promise<ReserveResult> {
    this.sweep(now);
    if (this.personal(userId, limits).blocked) return { ok: false, error: "blocked", resetsAt: monthEnd(now).toISOString() };
    const month = monthStart(now).getTime();
    const mine = this.sum((e) => e.userId === userId && e.at >= month, "chars");
    if (mine + characters > limits.ttsCharsMonthly) return { ok: false, error: "personal_limit", resetsAt: monthEnd(now).toISOString() };
    if (characters > globalCharsRemaining) return { ok: false, error: "monthly_budget", resetsAt: monthEnd(now).toISOString() };
    const id = String(this.nextId++);
    this.entries.push({ id, userId, at: now.getTime(), micro: 0, chars: characters, settled: true });
    return { ok: true, reservationId: id };
  }

  async summary(userId: string, limits: Limits, now = new Date()): Promise<SpendSummary> {
    this.sweep(now);
    const mine = this.personal(userId, limits);
    const month = monthStart(now).getTime(), day = dayStart(now).getTime();
    return {
      spentMonthMicro: this.sum((e) => e.userId === userId && e.at >= month, "micro"),
      spentTodayMicro: this.sum((e) => e.userId === userId && e.at >= day, "micro"),
      monthlyLimitMicro: mine.monthly, dailyLimitMicro: mine.daily,
      globalSpentMicro: this.sum((e) => e.at >= month, "micro"), globalLimitMicro: limits.globalMonthlyMicro,
      ttsCharsMonth: this.sum((e) => e.userId === userId && e.at >= month, "chars"), ttsCharsLimit: limits.ttsCharsMonthly,
      monthEnd: monthEnd(now).toISOString(), dayEnd: dayEnd(now).toISOString(), blocked: mine.blocked,
    };
  }
}

/** Production ledger: the same rules, enforced inside Postgres functions (schema.sql) so concurrent containers agree. */
export class SupabaseSpendLedger implements SpendLedger {
  constructor(private readonly db: SupabaseRest) {}

  reserve(userId: string, estimateMicro: number, limits: Limits): Promise<ReserveResult> {
    return this.db.rpc<ReserveResult>("oc_reserve", {
      p_user: userId, p_estimate: estimateMicro, p_monthly: limits.monthlyMicro, p_daily: limits.dailyMicro, p_global: limits.globalMonthlyMicro,
    });
  }
  async settle(reservationId: string, actualMicro: number, e: SpendEvent): Promise<void> {
    await this.db.rpc("oc_settle", {
      p_reservation: reservationId, p_actual: actualMicro, p_route: e.route, p_model: e.model ?? null,
      p_input: e.inputTokens, p_output: e.outputTokens, p_cache_write: e.cacheWriteTokens, p_cache_read: e.cacheReadTokens, p_chars: e.characters,
    });
  }
  reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number): Promise<ReserveResult> {
    return this.db.rpc<ReserveResult>("oc_reserve_chars", { p_user: userId, p_chars: characters, p_limit: limits.ttsCharsMonthly, p_global_remaining: globalCharsRemaining });
  }
  summary(userId: string, limits: Limits): Promise<SpendSummary> {
    return this.db.rpc<SpendSummary>("oc_spend_summary", { p_user: userId, p_monthly: limits.monthlyMicro, p_daily: limits.dailyMicro, p_global: limits.globalMonthlyMicro, p_tts: limits.ttsCharsMonthly });
  }
}
```

Add to `SupabaseRest` in `backend/src/db.ts`:

```ts
  /** Call a Postgres function through PostgREST (`/rpc/<fn>`); returns its JSON result. */
  async rpc<T>(fn: string, args: Record<string, unknown>): Promise<T> {
    const res = await this.fetchImpl(`${this.base}/rpc/${fn}`, { method: "POST", headers: this.headers(), body: JSON.stringify(args) });
    await this.check(res, `rpc ${fn}`);
    return (await res.json()) as T;
  }
```

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend -- ledger && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Append the schema (same rules in SQL)**

Append to `backend/supabase/schema.sql`:

```sql
-- Accounts on the Anthropic grant (2026-10-08). Money in micro-dollars; months/days in UTC.
alter table public.oc_usage_events add column if not exists cost_micro_usd bigint not null default 0;
alter table public.oc_usage_events add column if not exists cache_write_tokens integer not null default 0;
alter table public.oc_usage_events add column if not exists cache_read_tokens integer not null default 0;
create index if not exists oc_usage_events_ts on public.oc_usage_events (ts);

create table if not exists public.oc_accounts (
  user_id text primary key,
  monthly_limit_micro_usd bigint,
  daily_limit_micro_usd bigint,
  blocked boolean not null default false,
  created_at timestamptz not null default now()
);
create table if not exists public.oc_reservations (
  id uuid primary key default gen_random_uuid(),
  user_id text not null,
  created_at timestamptz not null default now(),
  estimate_micro_usd bigint not null
);
create index if not exists oc_reservations_user on public.oc_reservations (user_id, created_at);
create table if not exists public.oc_settings (
  id boolean primary key default true check (id),
  max_accounts integer not null default 100
);
insert into public.oc_settings (id) values (true) on conflict (id) do nothing;
alter table public.oc_accounts enable row level security;
alter table public.oc_reservations enable row level security;
alter table public.oc_settings enable row level security;

create or replace function public.oc_reserve(p_user text, p_estimate bigint, p_monthly bigint, p_daily bigint, p_global bigint)
returns json language plpgsql security definer set search_path = public as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_day timestamptz := date_trunc('day', now() at time zone 'utc') at time zone 'utc';
  v_acct oc_accounts%rowtype;
  v_today bigint; v_month_spent bigint; v_everyone bigint; v_id uuid;
begin
  -- One reservation at a time across all users: the global pool is shared, so per-user locks are not enough.
  perform pg_advisory_xact_lock(hashtext('oc_reserve'));
  delete from oc_reservations where created_at < now() - interval '10 minutes';
  select * into v_acct from oc_accounts where user_id = p_user;
  if found and v_acct.blocked then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  select coalesce(sum(cost_micro_usd), 0) into v_today from oc_usage_events where user_id = p_user and ts >= v_day;
  select coalesce(sum(cost_micro_usd), 0) into v_month_spent from oc_usage_events where user_id = p_user and ts >= v_month;
  select coalesce(sum(cost_micro_usd), 0) into v_everyone from oc_usage_events where ts >= v_month;
  v_today := v_today + coalesce((select sum(estimate_micro_usd) from oc_reservations where user_id = p_user and created_at >= v_day), 0);
  v_month_spent := v_month_spent + coalesce((select sum(estimate_micro_usd) from oc_reservations where user_id = p_user), 0);
  v_everyone := v_everyone + coalesce((select sum(estimate_micro_usd) from oc_reservations), 0);
  if v_today + p_estimate > coalesce(v_acct.daily_limit_micro_usd, p_daily) then
    return json_build_object('ok', false, 'error', 'daily_limit', 'resetsAt', v_day + interval '1 day');
  end if;
  if v_month_spent + p_estimate > coalesce(v_acct.monthly_limit_micro_usd, p_monthly) then
    return json_build_object('ok', false, 'error', 'personal_limit', 'resetsAt', v_month + interval '1 month');
  end if;
  if v_everyone + p_estimate > p_global then
    return json_build_object('ok', false, 'error', 'monthly_budget', 'resetsAt', v_month + interval '1 month');
  end if;
  insert into oc_reservations (user_id, estimate_micro_usd) values (p_user, p_estimate) returning id into v_id;
  return json_build_object('ok', true, 'reservationId', v_id);
end $$;

create or replace function public.oc_settle(p_reservation uuid, p_actual bigint, p_route text, p_model text,
  p_input integer, p_output integer, p_cache_write integer, p_cache_read integer, p_chars integer)
returns void language plpgsql security definer set search_path = public as $$
declare v_user text;
begin
  delete from oc_reservations where id = p_reservation returning user_id into v_user;
  -- Swept after 10 minutes: a reply that slow is not billed; its hold has already expired.
  if v_user is null then return; end if;
  insert into oc_usage_events (user_id, route, model, input_tokens, output_tokens, cache_write_tokens, cache_read_tokens, characters, credits, cost_micro_usd)
  values (v_user, p_route, p_model, p_input, p_output, p_cache_write, p_cache_read, p_chars, 0, p_actual);
end $$;

create or replace function public.oc_reserve_chars(p_user text, p_chars integer, p_limit integer, p_global_remaining integer)
returns json language plpgsql security definer set search_path = public as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_used integer;
begin
  perform pg_advisory_xact_lock(hashtext('oc_reserve_chars'));
  if exists (select 1 from oc_accounts where user_id = p_user and blocked) then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  select coalesce(sum(characters), 0) into v_used from oc_usage_events where user_id = p_user and route = '/tts' and ts >= v_month;
  if v_used + p_chars > p_limit then
    return json_build_object('ok', false, 'error', 'personal_limit', 'resetsAt', v_month + interval '1 month');
  end if;
  if p_chars > p_global_remaining then
    return json_build_object('ok', false, 'error', 'monthly_budget', 'resetsAt', v_month + interval '1 month');
  end if;
  insert into oc_usage_events (user_id, route, model, characters, credits, cost_micro_usd) values (p_user, '/tts', 'eleven_flash_v2_5', p_chars, 0, 0);
  return json_build_object('ok', true, 'reservationId', '');
end $$;

create or replace function public.oc_spend_summary(p_user text, p_monthly bigint, p_daily bigint, p_global bigint, p_tts integer)
returns json language sql security definer set search_path = public as $$
  with b as (
    select date_trunc('month', now() at time zone 'utc') at time zone 'utc' as m,
           date_trunc('day', now() at time zone 'utc') at time zone 'utc' as d
  ), a as (select * from oc_accounts where user_id = p_user)
  select json_build_object(
    'spentMonthMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where user_id = p_user and ts >= b.m),
    'spentTodayMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where user_id = p_user and ts >= b.d),
    'monthlyLimitMicro', coalesce((select monthly_limit_micro_usd from a), p_monthly),
    'dailyLimitMicro', coalesce((select daily_limit_micro_usd from a), p_daily),
    'globalSpentMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where ts >= b.m),
    'globalLimitMicro', p_global,
    'ttsCharsMonth', (select coalesce(sum(characters), 0) from oc_usage_events, b where user_id = p_user and route = '/tts' and ts >= b.m),
    'ttsCharsLimit', p_tts,
    'monthEnd', (select m + interval '1 month' from b),
    'dayEnd', (select d + interval '1 day' from b),
    'blocked', coalesce((select blocked from a), false));
$$;

-- At most oc_settings.max_accounts sign-ups. Runs as the auth schema's insert; refusing raises, so GoTrue answers sign-up with an error.
create or replace function public.oc_enforce_max_accounts() returns trigger language plpgsql security definer set search_path = public as $$
begin
  if (select count(*) from auth.users) >= (select max_accounts from oc_settings) then
    raise exception 'accounts_full';
  end if;
  return new;
end $$;
drop trigger if exists oc_max_accounts on auth.users;
create trigger oc_max_accounts before insert on auth.users for each row execute function public.oc_enforce_max_accounts();

create or replace function public.oc_accounts_open() returns boolean language sql security definer set search_path = public as $$
  select (select count(*) from auth.users) < (select max_accounts from oc_settings);
$$;
revoke all on function public.oc_reserve, public.oc_settle, public.oc_reserve_chars, public.oc_spend_summary, public.oc_accounts_open from public, anon, authenticated;
```

- [ ] **Step 6: Commit**

```bash
git add backend/src/ledger.ts backend/src/db.ts backend/supabase/schema.sql backend/test/ledger.test.ts
git commit -m "feat(backend): reserve-then-settle spend ledger in micro-dollars, with daily, monthly, global and character limits"
```

---

### Task 3: The account gate (plan gate, rate limit, `/billing/me`)

**Files:**
- Create: `backend/src/account.ts`
- Modify: `backend/src/app.ts`
- Test: `backend/test/account.test.ts`

**Interfaces:**
- Consumes: `SpendLedger`, `Limits`, `limitsFromEnv`, `MemorySpendLedger` (Task 2); `OPENAI_KEY_HEADER` (`src/keys.ts`); `Principal` (`src/auth.ts`).
- Produces:
  - `type AccountContext = { userId: string; byok: boolean; limits: Limits }` set as `c.get("account")`
  - `GRANT_ROUTES: Set<string>` = `/v1/polish`, `/chat`, `/v1/messages`, `/tts`, `/billing/me`
  - `requireAccount(ledgerFor: (c: Context) => SpendLedger | undefined): MiddlewareHandler`
  - `reserveOr402(c: Context, ledger: SpendLedger, estimateMicro: number): Promise<{ reservationId: string } | Response>`
  - `accountSummary(c: Context, ledger: SpendLedger): Promise<AccountSummaryJson>` where `AccountSummaryJson = { byok: boolean; spentMonthUsd: number; monthlyLimitUsd: number; spentTodayUsd: number; dailyLimitUsd: number; ttsCharsMonth: number; ttsCharsLimit: number; monthEnd: string; dayEnd: string; budgetExhausted: boolean; blocked: boolean }`
  - `AppOptions.spendLedger?: SpendLedger` in `createApp`

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/account.test.ts
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
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- account`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

```ts
// backend/src/account.ts
import type { Context, MiddlewareHandler } from "hono";
import { getEnv } from "./env.js";
import { OPENAI_KEY_HEADER } from "./keys.js";
import type { Principal } from "./auth.js";
import { limitsFromEnv, type Limits, type SpendLedger } from "./ledger.js";

/** Who a request runs as on the grant. BYOK requests carry their own key and skip all of this. */
export type AccountContext = { userId: string; byok: boolean; limits: Limits };

/** The only routes a grant request may use: Claude and ElevenLabs, nothing on OpenAI. */
export const GRANT_ROUTES = new Set(["/v1/polish", "/chat", "/v1/messages", "/tts", "/billing/me"]);

const REQUESTS_PER_MINUTE = 20;
const recentRequests = new Map<string, number[]>();

function rateLimited(userId: string, now = Date.now()): boolean {
  const windowStart = now - 60_000;
  const times = (recentRequests.get(userId) ?? []).filter((t) => t > windowStart);
  if (times.length >= REQUESTS_PER_MINUTE) {
    recentRequests.set(userId, times);
    return true;
  }
  times.push(now);
  recentRequests.set(userId, times);
  return false;
}

export function requireAccount(ledgerFor: (c: Context) => SpendLedger | undefined): MiddlewareHandler {
  return async (c, next) => {
    const byok = Boolean(c.req.header(OPENAI_KEY_HEADER)?.trim());
    const userId = (c.get("principal" as never) as Principal | undefined)?.sub ?? "";
    const limits = limitsFromEnv(getEnv(c));
    c.set("account" as never, { userId, byok, limits } as never);
    if (byok || !ledgerFor(c)) return next(); // own key, or a self-hosted backend with no ledger: unmetered
    if (!GRANT_ROUTES.has(c.req.path)) return c.json({ error: "not_on_plan" }, 402);
    if (rateLimited(userId)) return c.json({ error: "slow_down" }, 429);
    return next();
  };
}

/** Reserve before forwarding; a refusal becomes the 402 the app understands. */
export async function reserveOr402(c: Context, ledger: SpendLedger, estimateMicro: number): Promise<{ reservationId: string } | Response> {
  const account = c.get("account" as never) as AccountContext;
  const result = await ledger.reserve(account.userId, estimateMicro, account.limits);
  if (result.ok) return { reservationId: result.reservationId };
  return c.json({ error: result.error, resets_at: result.resetsAt }, 402);
}

export type AccountSummaryJson = {
  byok: boolean; spentMonthUsd: number; monthlyLimitUsd: number; spentTodayUsd: number; dailyLimitUsd: number;
  ttsCharsMonth: number; ttsCharsLimit: number; monthEnd: string; dayEnd: string; budgetExhausted: boolean; blocked: boolean;
};

const dollars = (micro: number) => Math.round(micro / 10_000) / 100;

export async function accountSummary(c: Context, ledger: SpendLedger): Promise<AccountSummaryJson> {
  const account = c.get("account" as never) as AccountContext;
  const s = await ledger.summary(account.userId, account.limits);
  return {
    byok: account.byok,
    spentMonthUsd: dollars(s.spentMonthMicro), monthlyLimitUsd: dollars(s.monthlyLimitMicro),
    spentTodayUsd: dollars(s.spentTodayMicro), dailyLimitUsd: dollars(s.dailyLimitMicro),
    ttsCharsMonth: s.ttsCharsMonth, ttsCharsLimit: s.ttsCharsLimit,
    monthEnd: s.monthEnd, dayEnd: s.dayEnd,
    budgetExhausted: s.globalSpentMicro >= s.globalLimitMicro, blocked: s.blocked,
  };
}
```

Wire into `backend/src/app.ts`:
1. Import `requireAccount`, `accountSummary` from `./account.js` and `SupabaseSpendLedger`, `type SpendLedger` from `./ledger.js`.
2. Add `spendLedger?: SpendLedger` to `AppOptions`.
3. Next to `storeFor`, add the same lazy resolution for the ledger:

```ts
  let resolvedLedger: SpendLedger | undefined | null = options.spendLedger ?? null;
  const ledgerFor = (c: Context): SpendLedger | undefined => {
    if (resolvedLedger !== null) return resolvedLedger;
    const env = getEnv(c);
    resolvedLedger = env.SUPABASE_URL && env.SUPABASE_SERVICE_KEY ? new SupabaseSpendLedger(new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY)) : undefined;
    return resolvedLedger;
  };
```

4. Replace the `gate` middleware body's last line `return requireCredits(storeFor(c))(c, next);` with `return requireAccount(ledgerFor)(c, next);`.
5. Replace `app.get("/billing/me", (c) => c.json(billingSummary(c)));` with:

```ts
  app.get("/billing/me", async (c) => {
    const ledger = ledgerFor(c);
    if (!ledger) return c.json({ byok: true, spentMonthUsd: 0, monthlyLimitUsd: 0, spentTodayUsd: 0, dailyLimitUsd: 0, ttsCharsMonth: 0, ttsCharsLimit: 0, monthEnd: "", dayEnd: "", budgetExhausted: false, blocked: false });
    return c.json(await accountSummary(c, ledger));
  });
```

6. Remove the now-unused `requireCredits`/`billingSummary` imports (keep `SupabaseBillingStore` and `withStore` — Stripe routes still use them).

- [ ] **Step 4: Run to verify pass, and fix app.test.ts expectations**

Run: `npm test -w backend`
Expected: `account` tests PASS. `app.test.ts` cases that asserted credit-based 402s or `/billing/me`'s old shape now fail: update them to pass `spendLedger: new MemorySpendLedger()` to `createApp` and to assert the new `/billing/me` keys. Cases that call `/v1/chat/completions`, `/v1/responses`, `/agent/transcribe` or `/agent/realtime/session` without BYOK and with a billing store must now expect `402 {error:"not_on_plan"}`; cases with BYOK headers keep their expectations. Re-run until green.

- [ ] **Step 5: Commit**

```bash
git add backend/src/account.ts backend/src/app.ts backend/test/account.test.ts backend/test/app.test.ts
git commit -m "feat(backend): account gate keeps grant requests on Claude and ElevenLabs, rate-limits, and reports spend in dollars"
```

---

### Task 4: Grant-funded Claude requests (`/chat`, `/v1/messages`)

**Files:**
- Create: `backend/src/anthropicGrant.ts`
- Modify: `backend/src/app.ts` (route `/chat` and `/v1/messages` through it for grant requests)
- Test: `backend/test/grant-anthropic.test.ts`

**Interfaces:**
- Consumes: `modelFor`, `isGrantModel` (Task 1); `estimateMicroUsd`, `costMicroUsd`, `parseAnthropicUsage` (Task 1); `reserveOr402`, `AccountContext` (Task 3); `SpendLedger` (Task 2); `resolveProviderKeys` (`src/keys.ts`).
- Produces: `proxyAnthropicOnGrant(c: Context, ledger: SpendLedger, purpose: Purpose): Promise<Response>`; `prepareGrantBody(body: Record<string, unknown>, model: string): Record<string, unknown> | { error: string }`; `settleStream(upstream: Response, onDone: (text: string) => void): Response`.

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/grant-anthropic.test.ts
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
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- grant-anthropic`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

```ts
// backend/src/anthropicGrant.ts
import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { modelFor, isGrantModel, type Purpose } from "./modelPolicy.js";
import { estimateMicroUsd, costMicroUsd, parseAnthropicUsage } from "./prices.js";
import { reserveOr402 } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const MAX_TOKENS_CAP = 1024;
const MAX_IMAGES = 2;

/** The request as the grant allows it: server's model, capped output, cached system prompt, at most two images. */
export function prepareGrantBody(body: Record<string, unknown>, model: string): Record<string, unknown> | { error: string } {
  const messages = (body.messages as Array<{ content: unknown }> | undefined) ?? [];
  const images = messages.flatMap((m) => (Array.isArray(m.content) ? m.content : [])).filter((b) => (b as { type?: string }).type === "image");
  if (images.length > MAX_IMAGES) return { error: "too_many_images" };
  const system =
    typeof body.system === "string" && body.system
      ? [{ type: "text", text: body.system, cache_control: { type: "ephemeral" } }]
      : body.system;
  return { ...body, model, max_tokens: Math.min(Number(body.max_tokens ?? MAX_TOKENS_CAP), MAX_TOKENS_CAP), ...(system ? { system } : {}) };
}

/** Passes the body through and calls onDone with the (trailing) text once, however the stream ends. */
export function settleStream(upstream: Response, onDone: (text: string) => void): Response {
  const WINDOW = 512_000;
  let collected = "";
  let done = false;
  const finish = () => { if (!done) { done = true; onDone(collected); } };
  const decoder = new TextDecoder();
  const reader = upstream.body!.getReader();
  const body = new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const { done: end, value } = await reader.read();
        if (end) { finish(); controller.close(); return; }
        collected += decoder.decode(value, { stream: true });
        if (collected.length > WINDOW) collected = collected.slice(-WINDOW);
        controller.enqueue(value);
      } catch (e) { finish(); controller.error(e); }
    },
    cancel(reason) { finish(); return reader.cancel(reason); },
  });
  const headers = new Headers({ "content-type": upstream.headers.get("content-type") ?? "text/event-stream" });
  return new Response(body, { status: upstream.status, headers });
}

export async function proxyAnthropicOnGrant(c: Context, ledger: SpendLedger, purpose: Purpose): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const model = modelFor(purpose, env);
  if (!isGrantModel(model)) return c.json({ error: "model is not priced" }, 500);
  let raw: Record<string, unknown> = {};
  try { raw = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  const prepared = prepareGrantBody(raw, model);
  if ("error" in prepared) return c.json(prepared, 400);
  const estimate = estimateMicroUsd(model, prepared)!;
  const reserved = await reserveOr402(c, ledger, estimate);
  if (reserved instanceof Response) return reserved;
  const upstream = await fetch(keys.anthropicBase + "/v1/messages", {
    method: "POST",
    headers: { "content-type": "application/json", accept: c.req.header("accept") ?? "*/*", "x-api-key": keys.anthropicKey, "anthropic-version": "2023-06-01" },
    body: JSON.stringify(prepared),
  });
  const route = new URL(c.req.url).pathname;
  if (!upstream.ok || !upstream.body) {
    await ledger.settle(reserved.reservationId, 0, { route, model, inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, characters: 0 });
    console.error(`grant ${route}: upstream ${upstream.status}: ${(await upstream.text()).slice(0, 500)}`);
    return c.json({ error: `the assistant is unavailable (${upstream.status})` }, 502);
  }
  return settleStream(upstream, (text) => {
    const usage = parseAnthropicUsage(text);
    const actual = usage ? costMicroUsd(model, usage)! : estimate; // no usage reported: charge the hold in full
    const settle = ledger.settle(reserved.reservationId, actual, { route, model, ...(usage ?? { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 }), characters: 0 })
      .catch((e) => console.error(`settle failed: ${(e as Error).message}`));
    try { c.executionCtx.waitUntil(settle); } catch { /* Node: nothing to hand it to */ }
  });
}
```

In `backend/src/app.ts`, replace the two route lines:

```ts
  app.post("/chat", (c) => grantOr(c, "ask", () => proxyAnthropic(c, storeFor(c))));
  app.post("/v1/messages", (c) => grantOr(c, "gate", () => proxyAnthropic(c, storeFor(c))));
```

and define inside `createApp`, before the routes:

```ts
  /** BYOK and unmetered backends keep the plain proxy; grant requests go through the ledger. */
  const grantOr = (c: Context, purpose: Purpose, plain: () => Promise<Response>) => {
    const ledger = ledgerFor(c);
    const account = c.get("account" as never) as AccountContext | undefined;
    return ledger && account && !account.byok ? proxyAnthropicOnGrant(c, ledger, purpose) : plain();
  };
```

(import `proxyAnthropicOnGrant` from `./anthropicGrant.js`, `type Purpose` from `./modelPolicy.js`, `type AccountContext` from `./account.js`).

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/anthropicGrant.ts backend/src/app.ts backend/test/grant-anthropic.test.ts
git commit -m "feat(backend): grant-funded Claude requests use the server's model, cache the system prompt and settle real cost"
```

---

### Task 5: `POST /v1/polish`

**Files:**
- Create: `backend/src/polish.ts`
- Modify: `backend/src/app.ts`
- Test: `backend/test/polish.test.ts`

**Interfaces:**
- Consumes: `prepareGrantBody`, `settleStream` are not used (polish is non-streamed); uses `modelFor`, `estimateMicroUsd`, `costMicroUsd`, `parseAnthropicUsage`, `reserveOr402`, `SpendLedger`.
- Produces: `polishTake(c: Context, ledger: SpendLedger | undefined): Promise<Response>` — request `{ purpose: "polish" | "edit", system: string, text: string }`, response `{ text: string }`.

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/polish.test.ts
import { describe, it, expect, vi, afterEach } from "vitest";
import { Hono } from "hono";
import { polishTake } from "../src/polish.js";
import { requireAccount } from "../src/account.js";
import { MemorySpendLedger } from "../src/ledger.js";

afterEach(() => vi.unstubAllGlobals());

function app(ledger: MemorySpendLedger, reply = '{"content":[{"type":"text","text":"See you at seven."}],"usage":{"input_tokens":300,"output_tokens":20}}') {
  const fetchMock = vi.fn(async () => new Response(reply, { headers: { "content-type": "application/json" } }));
  vi.stubGlobal("fetch", fetchMock);
  const a = new Hono();
  a.use("*", async (c, next) => { c.set("principal" as never, { sub: "u1", via: "supabase" } as never); await next(); });
  a.use("*", requireAccount(() => ledger));
  a.post("/v1/polish", (c) => polishTake(c, ledger));
  return { a, fetchMock };
}

describe("/v1/polish", () => {
  it("returns the polished text on Haiku and charges its cost", async () => {
    const ledger = new MemorySpendLedger();
    const { a, fetchMock } = app(ledger);
    const res = await a.request("/v1/polish", { method: "POST", body: JSON.stringify({ purpose: "polish", system: "fix punctuation", text: "see you at seven" }) }, { ANTHROPIC_API_KEY: "sk" });
    expect(await res.json()).toEqual({ text: "See you at seven." });
    const sent = JSON.parse((fetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(sent.model).toBe("claude-haiku-4-5");
    const s = await ledger.summary("u1", { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000 });
    expect(s.spentTodayMicro).toBe(300 * 1 + 20 * 5);
  });

  it("rejects an over-long take before spending anything", async () => {
    const { a, fetchMock } = app(new MemorySpendLedger());
    const res = await a.request("/v1/polish", { method: "POST", body: JSON.stringify({ purpose: "polish", system: "s", text: "x".repeat(8001) }) }, { ANTHROPIC_API_KEY: "sk" });
    expect(res.status).toBe(413);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects an unknown purpose", async () => {
    const { a } = app(new MemorySpendLedger());
    const res = await a.request("/v1/polish", { method: "POST", body: JSON.stringify({ purpose: "essay", system: "s", text: "t" }) }, { ANTHROPIC_API_KEY: "sk" });
    expect(res.status).toBe(400);
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- polish`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

```ts
// backend/src/polish.ts
import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { modelFor } from "./modelPolicy.js";
import { estimateMicroUsd, costMicroUsd, parseAnthropicUsage } from "./prices.js";
import { reserveOr402, type AccountContext } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const MAX_TEXT = 8000;
const MAX_SYSTEM = 6000;

/** Dictation polish and Hey Clicky edits: the app sends what to do and the words; the server picks the model. */
export async function polishTake(c: Context, ledger: SpendLedger | undefined): Promise<Response> {
  const env = getEnv(c);
  let req: { purpose?: string; system?: string; text?: string } = {};
  try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  if (req.purpose !== "polish" && req.purpose !== "edit") return c.json({ error: "purpose must be polish or edit" }, 400);
  const text = typeof req.text === "string" ? req.text : "";
  const system = typeof req.system === "string" ? req.system : "";
  if (!text.trim()) return c.json({ error: "text is empty" }, 400);
  if (text.length > MAX_TEXT || system.length > MAX_SYSTEM) return c.json({ error: "take too long to polish" }, 413);

  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const model = modelFor(req.purpose, env);
  const body = {
    model,
    max_tokens: Math.min(2048, Math.ceil((text.length / 3.5) * 1.5) + 64),
    system: [{ type: "text", text: system, cache_control: { type: "ephemeral" } }],
    messages: [{ role: "user", content: text }],
  };
  const account = c.get("account" as never) as AccountContext | undefined;
  const metered = Boolean(ledger && account && !account.byok);
  const estimate = estimateMicroUsd(model, body) ?? 0;
  let reservationId = "";
  if (metered) {
    const reserved = await reserveOr402(c, ledger!, estimate);
    if (reserved instanceof Response) return reserved;
    reservationId = reserved.reservationId;
  }
  const upstream = await fetch(keys.anthropicBase + "/v1/messages", {
    method: "POST",
    headers: { "content-type": "application/json", "x-api-key": keys.anthropicKey, "anthropic-version": "2023-06-01" },
    body: JSON.stringify(body),
  });
  const replyText = await upstream.text();
  const usage = parseAnthropicUsage(replyText);
  if (metered) {
    await ledger!.settle(reservationId, usage ? costMicroUsd(model, usage)! : upstream.ok ? estimate : 0, {
      route: "/v1/polish", model, ...(usage ?? { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 }), characters: 0,
    });
  }
  if (!upstream.ok) {
    console.error(`polish: upstream ${upstream.status}: ${replyText.slice(0, 500)}`);
    return c.json({ error: `polish unavailable (${upstream.status})` }, 502);
  }
  const content = (JSON.parse(replyText).content as Array<{ type: string; text?: string }>) ?? [];
  return c.json({ text: content.filter((b) => b.type === "text").map((b) => b.text ?? "").join("").trim() });
}
```

In `backend/src/app.ts` add, next to the other `/v1/*` routes: `app.post("/v1/polish", (c) => polishTake(c, ledgerFor(c)));` (import `polishTake` from `./polish.js`). `/v1/*` already has `requireAuth` and the gate.

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/polish.ts backend/src/app.ts backend/test/polish.test.ts
git commit -m "feat(backend): /v1/polish lets the server pick the cheap model for dictation polish and edits"
```

---

### Task 6: ElevenLabs answers on the grant (`/tts` character pool)

**Files:**
- Create: `backend/src/tts.ts`
- Modify: `backend/src/app.ts`
- Test: `backend/test/tts.test.ts`

**Interfaces:**
- Consumes: `SpendLedger.reserveCharacters`, `AccountContext`, `synthesizeSpeech` (`src/proxy.ts`, unchanged, used for BYOK/unmetered).
- Produces: `speakOnGrant(c: Context, ledger: SpendLedger): Promise<Response>`; `elevenLabsCharsRemaining(env: Env, now?: number): Promise<number>` (cached 10 minutes; `TTS_GLOBAL_MARGIN` default 0.05).

- [ ] **Step 1: Write the failing tests**

```ts
// backend/test/tts.test.ts
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

describe("elevenLabsCharsRemaining", () => {
  it("is the plan's remainder minus a 5% margin", async () => {
    stub({ character_count: 1000, character_limit: 10_000 });
    expect(await elevenLabsCharsRemaining(env)).toBe(10_000 - 1000 - 500);
  });
});

describe("speakOnGrant", () => {
  it("speaks with ElevenLabs and counts characters", async () => {
    stub({ character_count: 0, character_limit: 1_000_000 });
    const ledger = new MemorySpendLedger();
    const res = await app(ledger).request("/tts", { method: "POST", body: JSON.stringify({ text: "Click Battery." }) }, env);
    expect(res.headers.get("content-type")).toBe("audio/mpeg");
    const s = await ledger.summary("u1", { monthlyMicro: 10e6, dailyMicro: 2e6, globalMonthlyMicro: 1e9, ttsCharsMonthly: 20000 });
    expect(s.ttsCharsMonth).toBe("Click Battery.".length);
  });

  it("answers tts_budget when ElevenLabs' allowance is used up", async () => {
    stub({ character_count: 9_999, character_limit: 10_000 });
    const res = await app(new MemorySpendLedger()).request("/tts", { method: "POST", body: JSON.stringify({ text: "A long answer." }) }, env);
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "tts_budget" });
  });

  it("speaks only the first 400 characters", async () => {
    const f = stub({ character_count: 0, character_limit: 1_000_000 });
    await app(new MemorySpendLedger()).request("/tts", { method: "POST", body: JSON.stringify({ text: "word ".repeat(200) }) }, env);
    const ttsCall = f.mock.calls.find(([url]) => String(url).includes("text-to-speech"))!;
    expect(JSON.parse((ttsCall[1] as RequestInit).body as string).text.length).toBeLessThanOrEqual(400);
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- tts`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement**

```ts
// backend/src/tts.ts
import type { Context } from "hono";
import { getEnv, type Env } from "./env.js";
import type { AccountContext } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const SPOKEN_CHARS = 400;
const CACHE_MS = 10 * 60_000;
let cached: { at: number; remaining: number } | undefined;
export function resetElevenLabsCache() { cached = undefined; }

const base = (env: Env) => (env.ELEVENLABS_BASE_URL || "https://api.elevenlabs.io").replace(/\/$/, "");

/** What is left on the ElevenLabs plan this period, minus a safety margin; cached so every answer is not a lookup. */
export async function elevenLabsCharsRemaining(env: Env, now = Date.now()): Promise<number> {
  if (cached && now - cached.at < CACHE_MS) return cached.remaining;
  const res = await fetch(`${base(env)}/v1/user/subscription`, { headers: { "xi-api-key": env.ELEVENLABS_API_KEY ?? "" } });
  if (!res.ok) return 0;
  const sub = (await res.json()) as { character_count?: number; character_limit?: number };
  const limit = Number(sub.character_limit ?? 0);
  const margin = Math.ceil(limit * Number(env.TTS_GLOBAL_MARGIN ?? 0.05));
  const remaining = Math.max(0, limit - Number(sub.character_count ?? 0) - margin);
  cached = { at: now, remaining };
  return remaining;
}

/** Trim at a sentence or word boundary so the voice never stops mid-word. */
function spokenPart(text: string): string {
  if (text.length <= SPOKEN_CHARS) return text;
  const cut = text.slice(0, SPOKEN_CHARS);
  const sentence = Math.max(cut.lastIndexOf(". "), cut.lastIndexOf("? "), cut.lastIndexOf("! "));
  return sentence > 100 ? cut.slice(0, sentence + 1) : cut.slice(0, cut.lastIndexOf(" ")).trimEnd();
}

export async function speakOnGrant(c: Context, ledger: SpendLedger): Promise<Response> {
  const env = getEnv(c);
  if (!env.ELEVENLABS_API_KEY) return c.json({ error: "tts_budget" }, 402);
  let req: { text?: string } = {};
  try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  const text = spokenPart((req.text ?? "").trim());
  if (!text) return c.json({ error: "body must be JSON with `text`" }, 400);
  const account = c.get("account" as never) as AccountContext;
  const remaining = await elevenLabsCharsRemaining(env);
  const reserved = await ledger.reserveCharacters(account.userId, text.length, account.limits, remaining);
  if (!reserved.ok) return c.json({ error: "tts_budget" }, 402); // the app falls back to the Mac's voice, silently
  const voiceId = env.ELEVENLABS_VOICE_ID || "21m00Tcm4TlvDq8ikWAM";
  const upstream = await fetch(`${base(env)}/v1/text-to-speech/${voiceId}`, {
    method: "POST",
    headers: { "xi-api-key": env.ELEVENLABS_API_KEY, "content-type": "application/json", accept: "audio/mpeg" },
    body: JSON.stringify({ text, model_id: "eleven_flash_v2_5", voice_settings: { stability: 0.5, similarity_boost: 0.75 } }),
  });
  if (!upstream.ok || !upstream.body) {
    console.error(`tts: ElevenLabs ${upstream.status}`);
    return c.json({ error: "tts_budget" }, 402);
  }
  return new Response(upstream.body, { headers: { "content-type": "audio/mpeg" } });
}
```

In `backend/src/app.ts` replace `app.post("/tts", ...)` with:

```ts
  app.post("/tts", (c) => {
    const ledger = ledgerFor(c);
    const account = c.get("account" as never) as AccountContext | undefined;
    return ledger && account && !account.byok ? speakOnGrant(c, ledger) : synthesizeSpeech(c, storeFor(c));
  });
```

(import `speakOnGrant` from `./tts.js`).

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/tts.ts backend/src/app.ts backend/test/tts.test.ts
git commit -m "feat(backend): ElevenLabs answers on the grant with a per-person character pool and a live plan remainder"
```

---

### Task 7: Open sign-up support on the backend (`/auth/config`, `/auth/confirmed`)

**Files:**
- Modify: `backend/src/app.ts`
- Test: `backend/test/app.test.ts` (add a `describe("sign-up support")`)

**Interfaces:**
- Consumes: `SupabaseRest.rpc` (Task 2), function `oc_accounts_open` (Task 2 schema).
- Produces: `GET /auth/config` → `{ supabaseUrl, publishableKey, accountsOpen: boolean, confirmRedirectUrl: string }`; `GET /auth/confirmed` → small HTML page; `POST /auth/signup` `{ email, password }` → `200 { ok: true }` | `402 { error: "accounts_full" }` | `400/409/429 { error: <plain sentence> }`.

> **Ruling (2026-10-10, Task 2 review):** db.flowsxr.com is shared by every FlowsXR project, so sign-up goes through the backend, not straight to Supabase: `POST /auth/signup` checks `ACCOUNTS_OPEN === "true"` and `rpc("oc_accounts_open")`, forwards to GoTrue `POST {SUPABASE_URL}/auth/v1/signup?redirect_to=<confirmRedirectUrl>` with header `apikey: SUPABASE_PUBLISHABLE_KEY`, and on success inserts `{ user_id: <returned user id> }` into `oc_accounts` (`SupabaseRest.insert`). Only users with an `oc_accounts` row can spend the grant (`oc_reserve` returns `not_on_plan` otherwise).

- [ ] **Step 1: Write the failing tests** (append to `backend/test/app.test.ts`)

Also add tests for `POST /auth/signup` using a stubbed `fetch` (vitest `vi.stubGlobal`): (a) `ACCOUNTS_OPEN` unset → `402 {error:"accounts_full"}` and GoTrue is never called; (b) open, `oc_accounts_open` RPC returns `true`, GoTrue returns `200 {"id":"u-new", ...}` → `200 {ok:true}`, the GoTrue call carries `redirect_to` and `apikey`, and an insert into `oc_accounts` with `user_id: "u-new"` is made; (c) GoTrue answers 422 "User already registered" → `409 {error:"that email already has an account — sign in instead."}`.

```ts
describe("sign-up support", () => {
  const base = { SUPABASE_URL: "https://db.example", SUPABASE_PUBLISHABLE_KEY: "pk" };
  it("auth/config says sign-up is closed unless ACCOUNTS_OPEN is true", async () => {
    const res = await createApp({ log: null }).request("/auth/config", {}, base);
    expect(await res.json()).toMatchObject({ accountsOpen: false, confirmRedirectUrl: "http://localhost/auth/confirmed" });
  });
  it("auth/confirmed is a page that sends people back to the app", async () => {
    const res = await createApp({ log: null }).request("/auth/confirmed");
    expect(res.headers.get("content-type")).toContain("text/html");
    expect(await res.text()).toContain("go back to OpenClicky");
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test -w backend -- app`
Expected: FAIL — `accountsOpen` missing / 404.

- [ ] **Step 3: Implement** — replace the `/auth/config` handler in `backend/src/app.ts`:

```ts
  app.get("/auth/config", async (c) => {
    const env = getEnv(c);
    if (!env.SUPABASE_URL || !env.SUPABASE_PUBLISHABLE_KEY) return c.json({ error: "sign-in is not configured on this backend" }, 404);
    let accountsOpen = env.ACCOUNTS_OPEN === "true";
    if (accountsOpen && env.SUPABASE_SERVICE_KEY) {
      try { accountsOpen = await new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY).rpc<boolean>("oc_accounts_open", {}); }
      catch (e) { console.error(`auth/config: ${(e as Error).message}`); accountsOpen = false; }
    }
    const origin = new URL(c.req.url).origin;
    return c.json({
      supabaseUrl: env.SUPABASE_URL.replace(/\/+$/, ""),
      publishableKey: env.SUPABASE_PUBLISHABLE_KEY,
      accountsOpen,
      confirmRedirectUrl: env.ACCOUNT_CONFIRM_REDIRECT_URL || `${origin}/auth/confirmed`,
    });
  });

  // Where the confirmation email's link lands: nothing to do here but go back to the app.
  app.get("/auth/confirmed", (c) =>
    c.html(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>You're in</title><body style="font:16px -apple-system,sans-serif;background:#F4F1EA;color:#22201C;display:grid;place-items:center;height:100vh;margin:0">
<div style="text-align:center"><h1 style="font-family:Georgia,serif;font-weight:400">you're in.</h1><p>go back to OpenClicky — it signs you in on its own.</p></div></body>`),
  );
```

Add the sign-up route (public — no `requireAuth`):

```ts
  app.post("/auth/signup", async (c) => {
    const env = getEnv(c);
    if (!env.SUPABASE_URL || !env.SUPABASE_PUBLISHABLE_KEY || !env.SUPABASE_SERVICE_KEY) return c.json({ error: "sign-up is not configured on this backend" }, 404);
    let req: { email?: string; password?: string } = {};
    try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
    const email = (req.email ?? "").trim(), password = req.password ?? "";
    if (!email.includes("@") || password.length < 8) return c.json({ error: "use an email address and a password of at least 8 characters." }, 400);
    const db = new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY);
    const open = env.ACCOUNTS_OPEN === "true" && (await db.rpc<boolean>("oc_accounts_open", {}).catch(() => false));
    if (!open) return c.json({ error: "accounts_full" }, 402);
    const redirect = env.ACCOUNT_CONFIRM_REDIRECT_URL || `${new URL(c.req.url).origin}/auth/confirmed`;
    const res = await fetch(`${env.SUPABASE_URL.replace(/\/+$/, "")}/auth/v1/signup?redirect_to=${encodeURIComponent(redirect)}`, {
      method: "POST",
      headers: { "content-type": "application/json", apikey: env.SUPABASE_PUBLISHABLE_KEY },
      body: JSON.stringify({ email, password }),
    });
    const text = await res.text();
    if (!res.ok) {
      if (text.includes("already registered")) return c.json({ error: "that email already has an account — sign in instead." }, 409);
      if (res.status === 429) return c.json({ error: "too many tries — wait a minute and try again." }, 429);
      console.error(`signup: GoTrue ${res.status}: ${text.slice(0, 300)}`);
      return c.json({ error: "couldn't create the account right now." }, 502);
    }
    const user = JSON.parse(text) as { id?: string; user?: { id?: string } };
    const userId = user.id ?? user.user?.id;
    if (userId) await db.insert("oc_accounts", { user_id: userId }).catch((e) => console.error(`signup: oc_accounts insert: ${(e as Error).message}`));
    return c.json({ ok: true });
  });
```

- [ ] **Step 4: Run to verify pass**

Run: `npm test -w backend && npm run typecheck && npm run lint`
Expected: PASS, lint 0 errors.

- [ ] **Step 5: Commit**

```bash
git add backend/src/app.ts backend/test/app.test.ts
git commit -m "feat(backend): sign-up through the backend (OpenClicky's own account cap), auth/config openness and a confirmation page"
```

---

### Task 8: Admin script — budget and per-person controls, invites removed

**Files:**
- Modify: `backend/scripts/admin.mjs`

**Interfaces:**
- Consumes: tables/functions from Task 2 via the existing `api(method, url, body)` helper and `requireUser(email)` in `admin.mjs`.
- Produces commands: `budget`, `limit <email> --usd N`, `daily <email> --usd N`, `block <email>`, `unblock <email>`, `remove <email>`, `max-accounts N`, `list`; `invite` prints "invites are retired: people sign up in the app".

- [ ] **Step 1: Read the current command dispatch** in `backend/scripts/admin.mjs` (the `args`-based switch after line 113) and the helpers `api`, `listUsers`, `findUser`, `requireUser`.

- [ ] **Step 2: Implement the commands** — add these handlers and wire them into the dispatch (keep `list`, `remove`; replace `invite`, `limit`, `revoke`, `restore`, `usage`):

```js
const MICRO = 1_000_000;
const restBase = () => `${process.env.SUPABASE_URL.replace(/\/+$/, "")}/rest/v1`;
const usdArg = () => {
  const i = args.indexOf("--usd");
  const v = Number(args[i + 1]);
  if (i < 0 || !Number.isFinite(v) || v < 0) throw new Error("give an amount: --usd 5");
  return Math.round(v * MICRO);
};
async function upsertAccount(userId, fields) {
  return api("POST", `${restBase()}/oc_accounts?on_conflict=user_id`, { user_id: userId, ...fields }, { prefer: "resolution=merge-duplicates" });
}
async function budget() {
  const month = new Date(Date.UTC(new Date().getUTCFullYear(), new Date().getUTCMonth(), 1)).toISOString();
  const rows = await api("GET", `${restBase()}/oc_usage_events?ts=gte.${encodeURIComponent(month)}&select=user_id,cost_micro_usd,route,characters`);
  const perUser = new Map();
  let total = 0, chars = 0;
  for (const r of rows) {
    total += Number(r.cost_micro_usd);
    if (r.route === "/tts") chars += Number(r.characters);
    perUser.set(r.user_id, (perUser.get(r.user_id) ?? 0) + Number(r.cost_micro_usd));
  }
  const users = await listUsers();
  const emailOf = new Map(users.map((u) => [u.id, u.email]));
  const limit = Number(process.env.GLOBAL_MONTHLY_BUDGET_USD ?? 1000);
  const [settings] = await api("GET", `${restBase()}/oc_settings?select=max_accounts`);
  console.log(`this month: $${(total / MICRO).toFixed(2)} of $${limit} · ${chars} spoken characters · accounts ${users.length} of ${settings?.max_accounts ?? 100}`);
  [...perUser.entries()].sort((a, b) => b[1] - a[1]).slice(0, 10).forEach(([id, micro]) => console.log(`  $${(micro / MICRO).toFixed(2)}  ${emailOf.get(id) ?? id}`));
}
```

Dispatch entries:

```js
  case "budget": await budget(); break;
  case "limit": { const u = await requireUser(args[1]); await upsertAccount(u.id, { monthly_limit_micro_usd: usdArg() }); console.log(`monthly limit set for ${args[1]}`); break; }
  case "daily": { const u = await requireUser(args[1]); await upsertAccount(u.id, { daily_limit_micro_usd: usdArg() }); console.log(`daily limit set for ${args[1]}`); break; }
  case "block": { const u = await requireUser(args[1]); await upsertAccount(u.id, { blocked: true }); console.log(`blocked ${args[1]}`); break; }
  case "unblock": { const u = await requireUser(args[1]); await upsertAccount(u.id, { blocked: false }); console.log(`unblocked ${args[1]}`); break; }
  case "max-accounts": { const n = Number(args[1]); if (!Number.isInteger(n) || n < 1) throw new Error("max-accounts needs a whole number"); await api("PATCH", `${restBase()}/oc_settings?id=eq.true`, { max_accounts: n }); console.log(`max accounts: ${n}`); break; }
  case "invite": console.log("invites are retired: people sign up in the app. Use `limit`/`daily` to give someone more."); break;
```

If `api()` does not accept a 4th `extraHeaders` argument, add it: merge `extraHeaders` into the request headers inside `api`.

Update the usage text printed for an unknown command to list: `list, budget, limit <email> --usd N, daily <email> --usd N, block <email>, unblock <email>, remove <email>, max-accounts N`.

- [ ] **Step 3: Verify read-only against the real database** (after Task 9 has applied the schema; until then, verify syntax only)

Run: `node --check backend/scripts/admin.mjs`
Expected: no output (syntax OK).
After Task 9: `npm run admin -w backend -- budget` prints `this month: $0.00 of $1000 · 0 spoken characters · accounts N of 100`.

- [ ] **Step 4: Commit**

```bash
git add backend/scripts/admin.mjs
git commit -m "feat(backend): admin budget, per-person limits, block and max-accounts; invites retired"
```

---

### Task 9: Apply the schema, configure Supabase Auth, deploy closed (ops — confirm with Prasanth before each outward step)

**Files:** none in the repo beyond `docs/` notes; server env on the VPS.

**Interfaces:**
- Consumes: Tasks 1–8 merged; `backend/supabase/schema.sql`; `scripts/deploy-backend.sh` (`npm run deploy:backend`, `--env` uploads `.dev.vars` as `backend.env`).
- Produces: live backend with `ACCOUNTS_OPEN=false`, the schema and functions in Supabase, Auth configured for confirmed email sign-up.

- [ ] **Step 1: Ask Prasanth for the Anthropic grant API key** and store it the same way as the ElevenLabs key: replace `ANTHROPIC_API_KEY` in `backend/.dev.vars` (git-ignored, mode 600) and remove `ANTHROPIC_BASE_URL` / `ANTHROPIC_MODEL_PREFIX` / Anthropic entries of `MODEL_ALIASES` if they point at OpenRouter (grant requests must go to `https://api.anthropic.com`). Confirm with: `curl -s https://api.anthropic.com/v1/models -H "x-api-key: $KEY" -H "anthropic-version: 2023-06-01" | head -c 200` → JSON listing models.

- [ ] **Step 2: Add the new settings to `backend/.dev.vars`:**

```
ACCOUNT_MONTHLY_USD=10
ACCOUNT_DAILY_USD=2
GLOBAL_MONTHLY_BUDGET_USD=1000
ACCOUNT_MONTHLY_TTS_CHARS=20000
MAX_ACCOUNTS=100
ACCOUNTS_OPEN=false
ACCOUNT_CONFIRM_REDIRECT_URL=https://api.openclicky.flowsxr.com/auth/confirmed
```

Remove `FREE_MONTHLY_CREDITS` (no longer read).

- [ ] **Step 3: Apply the schema** using the procedure in `/Users/prasanthsasikumar/Documents/GitHub/SUPABASE.md` ("load a schema file"). Then verify:

```bash
npm run admin -w backend -- budget
```
Expected: `this month: $0.00 of $1000 · 0 spoken characters · accounts N of 100`.

- [ ] **Step 4: Configure Supabase Auth** (self-hosted GoTrue at db.flowsxr.com; runbook `flowsxr-hq/infra/README.md`): email sign-up enabled, email confirmations **on** (`GOTRUE_MAILER_AUTOCONFIRM=false`), add `https://api.openclicky.flowsxr.com/auth/confirmed` to the allowed redirect URLs (`GOTRUE_URI_ALLOW_LIST`). Restart the auth container. Verify with a throwaway sign-up (`curl -X POST $SUPABASE_URL/auth/v1/signup -H "apikey: $PUB" -d '{"email":"openclicky-signup-test@flowsxr.com","password":"…"}'`) that an email arrives from hello@flowsxr.com and that a password grant before clicking the link fails with "Email not confirmed". Delete the test user afterwards (`npm run admin -w backend -- remove openclicky-signup-test@flowsxr.com`).

- [ ] **Step 5: Deploy** with `npm run deploy:backend -- --env` and check `https://api.openclicky.flowsxr.com/health` → `{"ok":true}` and `/auth/config` → `accountsOpen: false`.

- [ ] **Step 6: Record** the outcome in `docs/HANDOVER-2026-09-13.md` style note or a new `docs/accounts-runbook.md` (commands above, where keys live, how to open sign-up) and commit:

```bash
git add docs/accounts-runbook.md
git commit -m "docs: accounts runbook (schema, Supabase Auth settings, opening sign-up)"
```

---

### Task 10: App — the account profile (`AccountCapabilities`) and the new `/billing/me`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/AccountCapabilities.swift`
- Modify: `macos/OpenClicky/OpenClicky/BillingStatus.swift` (`BillingSummary` → new shape)
- Test: `macos/OpenClicky/OpenClickyTests/AccountCapabilitiesTests.swift`

**Interfaces:**
- Produces:
  - `struct BillingSummary: Decodable, Equatable { let byok: Bool; let spentMonthUsd: Double; let monthlyLimitUsd: Double; let spentTodayUsd: Double; let dailyLimitUsd: Double; let ttsCharsMonth: Int; let ttsCharsLimit: Int; let monthEnd: String; let dayEnd: String; let budgetExhausted: Bool; let blocked: Bool }`
  - `enum AccountKind { case ownKeys, account, signedOut }`
  - `struct AccountCapabilities { let kind: AccountKind; static func current(settings: OpenClickyConfiguration.Settings) -> AccountCapabilities; var usesRealtime: Bool; var usesAgent: Bool; var hearsOnDevice: Bool; var polishPath: String /* "/v1/polish" or "/v1/chat/completions" */ }`
  - `enum AccountLimitError: String { case personalLimit = "personal_limit", dailyLimit = "daily_limit", monthlyBudget = "monthly_budget", notOnPlan = "not_on_plan", accountsFull = "accounts_full", blocked = "blocked", ttsBudget = "tts_budget"; var message: String }`
  - `static func AccountLimitError.from(status: Int, body: Data) -> AccountLimitError?`
  - `enum UsageLevel { case plenty, runningLow, usedUp }`; `extension BillingSummary { var level: UsageLevel; var fractionUsed: Double }`

- [ ] **Step 1: Write the failing tests**

```swift
// macos/OpenClicky/OpenClickyTests/AccountCapabilitiesTests.swift
import Foundation
import Testing
@testable import OpenClicky

struct AccountCapabilitiesTests {
    @Test func decodesTheNewBillingSummary() throws {
        let json = #"{"byok":false,"spentMonthUsd":4.5,"monthlyLimitUsd":10,"spentTodayUsd":0.2,"dailyLimitUsd":2,"ttsCharsMonth":300,"ttsCharsLimit":20000,"monthEnd":"2026-11-01T00:00:00.000Z","dayEnd":"2026-10-09T00:00:00.000Z","budgetExhausted":false,"blocked":false}"#
        let summary = try JSONDecoder().decode(BillingSummary.self, from: Data(json.utf8))
        #expect(summary.fractionUsed == 0.45)
        #expect(summary.level == .plenty)
    }

    @Test func runningLowAtEightyPercentAndUsedUpAtTheLimit() throws {
        func summary(_ spent: Double, exhausted: Bool = false) -> BillingSummary {
            BillingSummary(byok: false, spentMonthUsd: spent, monthlyLimitUsd: 10, spentTodayUsd: 0, dailyLimitUsd: 2, ttsCharsMonth: 0, ttsCharsLimit: 20000, monthEnd: "", dayEnd: "", budgetExhausted: exhausted, blocked: false)
        }
        #expect(summary(8).level == .runningLow)
        #expect(summary(10).level == .usedUp)
        #expect(summary(1, exhausted: true).level == .usedUp)
    }

    @Test func accountProfileRoutesEveryLane() {
        let account = AccountCapabilities(kind: .account)
        #expect(!account.usesRealtime)
        #expect(!account.usesAgent)
        #expect(account.hearsOnDevice)
        #expect(account.polishPath == "/v1/polish")
        let own = AccountCapabilities(kind: .ownKeys)
        #expect(own.usesRealtime)
        #expect(own.polishPath == "/v1/chat/completions")
    }

    @Test func limitErrorsBecomePlainSentences() {
        let body = Data(#"{"error":"daily_limit","resets_at":"2026-10-09T00:00:00Z"}"#.utf8)
        let error = AccountLimitError.from(status: 402, body: body)
        #expect(error == .dailyLimit)
        #expect(error?.message == "you've used today's free allowance — it comes back tomorrow. dictation still works.")
        #expect(AccountLimitError.from(status: 500, body: body) == nil)
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: the Mac test command from Global Constraints with `-only-testing:OpenClickyTests/AccountCapabilitiesTests`
Expected: FAIL — `AccountCapabilities` not found.

- [ ] **Step 3: Implement**

Replace `BillingSummary` in `BillingStatus.swift` with the new fields (same order as the Interfaces block; memberwise init is synthesised). Update any view in `BillingStatus.swift` that read `used`/`limit`/`plan` to read `spentMonthUsd`/`monthlyLimitUsd` and show `level` (Task 15 restyles it).

```swift
// macos/OpenClicky/OpenClicky/AccountCapabilities.swift
//
//  AccountCapabilities.swift
//  OpenClicky
//
//  One answer to "what can this person use?", so every lane asks the same question: a free
//  account (signed in, on the grant: Apple hears, Claude thinks, ElevenLabs speaks, no realtime,
//  no agent), own keys (everything, unmetered), or signed out (offline dictation only).
//

import Foundation

enum AccountKind: Equatable { case ownKeys, account, signedOut }

struct AccountCapabilities: Equatable {
    let kind: AccountKind

    static func current(settings: OpenClickyConfiguration.Settings = OpenClickyConfiguration.settings) -> AccountCapabilities {
        if let key = settings.openaiApiKey, !key.trimmingCharacters(in: .whitespaces).isEmpty { return AccountCapabilities(kind: .ownKeys) }
        if OpenClickyConfiguration.isConfigured { return AccountCapabilities(kind: .account) }
        return AccountCapabilities(kind: .signedOut)
    }

    var usesRealtime: Bool { kind == .ownKeys }
    var usesAgent: Bool { kind == .ownKeys }
    var hearsOnDevice: Bool { kind != .ownKeys }
    var polishPath: String { kind == .ownKeys ? "/v1/chat/completions" : "/v1/polish" }
}

enum AccountLimitError: String, Equatable {
    case personalLimit = "personal_limit", dailyLimit = "daily_limit", monthlyBudget = "monthly_budget"
    case notOnPlan = "not_on_plan", accountsFull = "accounts_full", blocked = "blocked", ttsBudget = "tts_budget"

    static func from(status: Int, body: Data) -> AccountLimitError? {
        guard status == 402,
              let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let raw = json["error"] as? String else { return nil }
        return AccountLimitError(rawValue: raw)
    }

    var message: String {
        switch self {
        case .personalLimit: return "you've used this month's free allowance — it comes back on the 1st. dictation still works."
        case .dailyLimit: return "you've used today's free allowance — it comes back tomorrow. dictation still works."
        case .monthlyBudget: return "openclicky's free allowance is used up for now — you can keep going with your own key."
        case .notOnPlan: return "that needs your own key for now."
        case .accountsFull: return "openclicky's free accounts are full right now — you can use your own key instead."
        case .blocked: return "this account is paused. dictation still works."
        case .ttsBudget: return ""
        }
    }
}

enum UsageLevel: Equatable { case plenty, runningLow, usedUp }

extension BillingSummary {
    var fractionUsed: Double {
        guard monthlyLimitUsd > 0 else { return 0 }
        return min(1, (spentMonthUsd / monthlyLimitUsd * 100).rounded() / 100)
    }
    var level: UsageLevel {
        if budgetExhausted || blocked || fractionUsed >= 1 || (dailyLimitUsd > 0 && spentTodayUsd >= dailyLimitUsd) { return .usedUp }
        return fractionUsed >= 0.8 ? .runningLow : .plenty
    }
}
```

If `OpenClickyConfiguration.Settings` has no `openaiApiKey` property under that name, use the property `OpenClickyConfiguration.usesOwnKeys` already reads (see `NotchHUDPanels.swift` `planDescription`) and adjust the test to match; do not add a second key setting.

- [ ] **Step 4: Run to verify pass**

Run: the Mac test command with `-only-testing:OpenClickyTests/AccountCapabilitiesTests`, then the full `OpenClickyTests`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/AccountCapabilities.swift macos/OpenClicky/OpenClicky/BillingStatus.swift macos/OpenClicky/OpenClickyTests/AccountCapabilitiesTests.swift
git commit -m "feat(mac): one account profile decides every lane; plain sentences for each limit"
```

---

### Task 11: App — polish through `/v1/polish`

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Dictation/AccountTakePolisher.swift`
- Modify: `macos/OpenClicky/OpenClicky/Dictation/DictationTakeController.swift` (`makePolisher`), `macos/OpenClicky/OpenClicky/Dictation/DictationSettings.swift` (default for `polishOfflineTakes` when on an account)
- Test: `macos/OpenClicky/OpenClickyTests/AccountTakePolisherTests.swift`

**Interfaces:**
- Consumes: `AccountCapabilities`, `AccountLimitError` (Task 10); protocol `TakePolisher` (`Dictation/TakeFormatter.swift`: `var displayName: String { get }`, `func polish(system: String, user: String) async throws -> String`).
- Produces: `struct AccountTakePolisher: TakePolisher { var purpose: String = "polish"; var send: (URLRequest) async throws -> (Data, URLResponse) = { try await URLSession.shared.data(for: $0) } }`; error `AccountPolishError.limit(AccountLimitError)`.

- [ ] **Step 1: Write the failing tests**

```swift
// macos/OpenClicky/OpenClickyTests/AccountTakePolisherTests.swift
import Foundation
import Testing
@testable import OpenClicky

struct AccountTakePolisherTests {
    private func response(_ status: Int) -> HTTPURLResponse {
        HTTPURLResponse(url: URL(string: "https://x/v1/polish")!, statusCode: status, httpVersion: nil, headerFields: nil)!
    }

    @Test func sendsPurposeSystemAndTextAndNoModel() async throws {
        var captured: URLRequest?
        let polisher = AccountTakePolisher(purpose: "polish", send: { request in
            captured = request
            return (Data(#"{"text":"See you at seven."}"#.utf8), response(200))
        })
        let text = try await polisher.polish(system: "fix punctuation", user: "see you at seven")
        #expect(text == "See you at seven.")
        let body = try JSONSerialization.jsonObject(with: captured!.httpBody!) as! [String: Any]
        #expect(body["purpose"] as? String == "polish")
        #expect(body["text"] as? String == "see you at seven")
        #expect(body["model"] == nil)
        #expect(captured!.url!.path.hasSuffix("/v1/polish"))
    }

    @Test func polish402FallsBackToLocalFormatting() async {
        let polisher = AccountTakePolisher(purpose: "polish", send: { _ in
            (Data(#"{"error":"daily_limit"}"#.utf8), response(402))
        })
        await #expect(throws: AccountPolishError.limit(.dailyLimit)) {
            try await polisher.polish(system: "s", user: "words")
        }
    }
}
```

Also add, in `TakeFormatterTests.swift` (existing file), a test that `TakeFormatter.format` with a polisher that throws `AccountPolishError.limit(.dailyLimit)` returns the locally formatted text with `formattingDegraded == true` (follow the existing degraded-polisher test in that file; copy its shape and swap the error).

- [ ] **Step 2: Run to verify failure**

Run: Mac tests `-only-testing:OpenClickyTests/AccountTakePolisherTests`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement**

```swift
// macos/OpenClicky/OpenClicky/Dictation/AccountTakePolisher.swift
//
//  AccountTakePolisher.swift
//  OpenClicky
//
//  Polish and Hey Clicky edits for signed-in accounts: the app says what to do and sends the
//  words; the backend picks the model (a cheap one) and bills the grant. A limit throws a typed
//  error so the take still pastes with local cleanup.
//

import Foundation

enum AccountPolishError: Error, Equatable {
    case limit(AccountLimitError)
    case unavailable(Int)
}

struct AccountTakePolisher: TakePolisher {
    var purpose: String = "polish"
    var send: (URLRequest) async throws -> (Data, URLResponse) = { try await URLSession.shared.data(for: $0) }

    var displayName: String { "OpenClicky" }

    func polish(system: String, user: String) async throws -> String {
        guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/v1/polish") else { throw AccountPolishError.unavailable(0) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&request)
        request.httpBody = try JSONSerialization.data(withJSONObject: ["purpose": purpose, "system": system, "text": user])
        let (data, response) = try await send(request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if let limit = AccountLimitError.from(status: status, body: data) { throw AccountPolishError.limit(limit) }
        guard (200..<300).contains(status),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw AccountPolishError.unavailable(status) }
        return text
    }
}
```

In `DictationTakeController.swift`, change `makePolisher()`:

```swift
    static func makePolisher() -> (any TakePolisher)? {
        let sarvamKey = OpenClickyConfiguration.settings.sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !sarvamKey.isEmpty { return SarvamTakePolisher(client: SarvamSpeechClient(key: sarvamKey)) }
        switch AccountCapabilities.current().kind {
        case .account: return AccountTakePolisher()
        case .ownKeys: return BackendTakePolisher()
        case .signedOut: return nil
        }
    }
```

Find where Hey Clicky edits obtain their polisher (search `HeyClickyEditor` in `DictationTakeController.swift`); when the account kind is `.account`, construct `AccountTakePolisher(purpose: "edit")` there instead.

In `DictationSettings.swift` `init`, after `polishOfflineTakes = defaults.bool(forKey: Keys.polishOfflineTakes)`, apply the account default only when the user has never set it:

```swift
        if defaults.object(forKey: Keys.polishOfflineTakes) == nil, AccountCapabilities.current().kind == .account {
            polishOfflineTakes = true
        }
```

- [ ] **Step 4: Run to verify pass**

Run: Mac tests (`AccountTakePolisherTests`, `TakeFormatterTests`, then all `OpenClickyTests`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/Dictation/AccountTakePolisher.swift macos/OpenClicky/OpenClicky/Dictation/DictationTakeController.swift macos/OpenClicky/OpenClicky/Dictation/DictationSettings.swift macos/OpenClicky/OpenClickyTests/AccountTakePolisherTests.swift macos/OpenClicky/OpenClickyTests/TakeFormatterTests.swift
git commit -m "feat(mac): account polish and edits go through /v1/polish; a limit still pastes the take"
```

---

### Task 12: App — Claude tools in the ask lane (quick actions on accounts)

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/ClaudeAPI.swift` (tools + tool-call parsing), `macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift` (add `anthropicToolDefinitions()`)
- Test: `macos/OpenClicky/OpenClickyTests/ClaudeToolStreamTests.swift`

**Interfaces:**
- Consumes: `RealtimeVoiceClient.fastActionToolDefinitions()` (existing), `MacAction.parse(toolName:arguments:)` (existing).
- Produces:
  - `nonisolated static func RealtimeVoiceClient.anthropicToolDefinitions() -> [[String: Any]]` — each `{name, description, input_schema}`.
  - `struct ClaudeToolCall: Equatable { let id: String; let name: String; let arguments: [String: String] }` (values stringified; numbers as decimal strings).
  - `ClaudeAPI.analyzeImageStreaming(images:systemPrompt:conversationHistory:userPrompt:tools:onTextChunk:)` returning `(text: String, toolCalls: [ClaudeToolCall], duration: TimeInterval)`; `tools` defaults to `[]` so existing call sites compile after adding `, _` to their tuple destructuring.
  - `static func ClaudeAPI.parseToolCalls(fromSSELines lines: [String]) -> [ClaudeToolCall]` (pure, tested).

- [ ] **Step 1: Write the failing tests**

```swift
// macos/OpenClicky/OpenClickyTests/ClaudeToolStreamTests.swift
import Foundation
import Testing
@testable import OpenClicky

struct ClaudeToolStreamTests {
    @Test func anthropicToolsMirrorTheRealtimeOnes() {
        let tools = RealtimeVoiceClient.anthropicToolDefinitions()
        let names = tools.compactMap { $0["name"] as? String }
        #expect(names == ["open_app", "open_url", "create_folder", "reveal_in_finder", "set_volume", "media_control"])
        #expect(tools.allSatisfy { $0["input_schema"] is [String: Any] && $0["type"] == nil })
    }

    @Test func parsesAToolCallStreamedInPieces() {
        let lines = [
            #"data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"create_folder","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"name\": \"Launch"}}"#,
            #"data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":" Ideas\", \"location\": \"desktop\"}"}}"#,
            #"data: {"type":"content_block_stop","index":1}"#,
        ]
        let calls = ClaudeAPI.parseToolCalls(fromSSELines: lines)
        #expect(calls == [ClaudeToolCall(id: "toolu_1", name: "create_folder", arguments: ["name": "Launch Ideas", "location": "desktop"])])
    }

    @Test func numbersBecomeStringsAndParseStillAccepts() {
        let lines = [
            #"data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t","name":"set_volume","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"level\": 30}"}}"#,
            #"data: {"type":"content_block_stop","index":0}"#,
        ]
        let call = ClaudeAPI.parseToolCalls(fromSSELines: lines)[0]
        #expect(call.arguments["level"] == "30")
        #expect(MacAction.parse(toolName: call.name, arguments: call.arguments) == .action(.setVolume(level: 30)))
    }

    @Test func invalidJSONYieldsNoCall() {
        let lines = [
            #"data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t","name":"open_app","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"name\": "}}"#,
            #"data: {"type":"content_block_stop","index":0}"#,
        ]
        #expect(ClaudeAPI.parseToolCalls(fromSSELines: lines).isEmpty)
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: Mac tests `-only-testing:OpenClickyTests/ClaudeToolStreamTests`
Expected: FAIL — `anthropicToolDefinitions` / `parseToolCalls` not found.

- [ ] **Step 3: Implement**

In `RealtimeVoiceClient.swift`, directly after `fastActionToolDefinitions()`:

```swift
    /// The same quick actions as Anthropic tool definitions, for the ask lane on accounts (no realtime there).
    nonisolated static func anthropicToolDefinitions() -> [[String: Any]] {
        fastActionToolDefinitions().map { definition in
            [
                "name": definition["name"] as? String ?? "",
                "description": definition["description"] as? String ?? "",
                "input_schema": definition["parameters"] as? [String: Any] ?? ["type": "object", "properties": [String: Any]()],
            ]
        }
    }
```

In `ClaudeAPI.swift` add:

```swift
struct ClaudeToolCall: Equatable {
    let id: String
    let name: String
    let arguments: [String: String]
}

extension ClaudeAPI {
    /// Tool calls from an Anthropic SSE stream: `content_block_start` names the tool, `input_json_delta`
    /// pieces build its input, `content_block_stop` closes it. Input that is not valid JSON is dropped.
    static func parseToolCalls(fromSSELines lines: [String]) -> [ClaudeToolCall] {
        var open: [Int: (id: String, name: String, json: String)] = [:]
        var calls: [ClaudeToolCall] = []
        for line in lines where line.hasPrefix("data: ") {
            guard let data = line.dropFirst(6).data(using: .utf8),
                  let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let type = event["type"] as? String,
                  let index = event["index"] as? Int else { continue }
            switch type {
            case "content_block_start":
                if let block = event["content_block"] as? [String: Any], block["type"] as? String == "tool_use" {
                    open[index] = (block["id"] as? String ?? "", block["name"] as? String ?? "", "")
                }
            case "content_block_delta":
                if let delta = event["delta"] as? [String: Any], delta["type"] as? String == "input_json_delta",
                   let piece = delta["partial_json"] as? String, open[index] != nil {
                    open[index]!.json += piece
                }
            case "content_block_stop":
                guard let tool = open.removeValue(forKey: index) else { continue }
                let raw = tool.json.isEmpty ? "{}" : tool.json
                guard let object = try? JSONSerialization.jsonObject(with: Data(raw.utf8)) as? [String: Any] else { continue }
                var arguments: [String: String] = [:]
                for (key, value) in object {
                    if let string = value as? String { arguments[key] = string }
                    else if let number = value as? NSNumber { arguments[key] = number.stringValue }
                }
                calls.append(ClaudeToolCall(id: tool.id, name: tool.name, arguments: arguments))
            default:
                continue
            }
        }
        return calls
    }
}
```

`MacAction.parse` takes `[String: Any]`; `[String: String]` converts implicitly when passed (`arguments: call.arguments`), and its `set_volume` branch already accepts numeric strings.

Change `analyzeImageStreaming`:
1. Add parameter `tools: [[String: Any]] = []` after `userPrompt`.
2. Build the body with `"tools": tools` only when non-empty (`var body: [String: Any] = [...]; if !tools.isEmpty { body["tools"] = tools }`), and send `"model": model` as before (the backend replaces it on the grant).
3. Collect every SSE line into `var sseLines: [String] = []` inside the existing loop (append `line` before the `guard line.hasPrefix("data: ")`).
4. Return `(text: accumulatedResponseText, toolCalls: Self.parseToolCalls(fromSSELines: sseLines), duration: duration)` and update the return type.
5. Update callers: `let (fullResponseText, _) = try await claudeAPI.analyzeImageStreaming(` becomes `let (fullResponseText, toolCalls, _) = …` in `CompanionManager.sendTranscriptToClaudeWithScreenshot` (Task 13 uses `toolCalls`); any other caller adds `, _`.

- [ ] **Step 4: Run to verify pass**

Run: Mac tests (`ClaudeToolStreamTests`, then all `OpenClickyTests`).
Expected: PASS; the app target builds.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/ClaudeAPI.swift macos/OpenClicky/OpenClicky/RealtimeVoiceClient.swift macos/OpenClicky/OpenClicky/CompanionManager.swift macos/OpenClicky/OpenClickyTests/ClaudeToolStreamTests.swift
git commit -m "feat(mac): the ask lane can offer the quick actions to Claude as tools and read its calls from the stream"
```

---

### Task 13: App — route the companion for accounts (hearing, tools, voice, limits)

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/CompanionManager.swift`
- Test: `macos/OpenClicky/OpenClickyTests/AccountCapabilitiesTests.swift` (add the pure routing helper tests)

**Interfaces:**
- Consumes: `AccountCapabilities`, `AccountLimitError` (Task 10); `ClaudeToolCall`, `RealtimeVoiceClient.anthropicToolDefinitions()` (Task 12); existing `macActionRunner`, `speakWithSystemVoice(_:)`, `elevenLabsTTSClient`, `BuddyDictationManager.init(transcriptionProvider:)`, `AppleSpeechTranscriptionProvider`.
- Produces: `static func CompanionManager.spokenReply(text: String, toolOutcomes: [String]) -> String` (pure); account-aware `usesRealtimeVoice`; the ask lane passes tools and performs calls.

- [ ] **Step 1: Write the failing test** (append to `AccountCapabilitiesTests`)

```swift
    @MainActor @Test func toolOutcomesAreSpokenAfterTheAnswer() {
        #expect(CompanionManager.spokenReply(text: "", toolOutcomes: ["Created Launch Ideas on your Desktop."]) == "Created Launch Ideas on your Desktop.")
        #expect(CompanionManager.spokenReply(text: "Sure.", toolOutcomes: ["Opened Safari."]) == "Sure. Opened Safari.")
        #expect(CompanionManager.spokenReply(text: "It's in the sidebar.", toolOutcomes: []) == "It's in the sidebar.")
    }
```

- [ ] **Step 2: Run to verify failure**

Run: Mac tests `-only-testing:OpenClickyTests/AccountCapabilitiesTests`
Expected: FAIL — `spokenReply` not found.

- [ ] **Step 3: Implement in `CompanionManager.swift`**

1. Realtime only on own keys:

```swift
    private var usesRealtimeVoice: Bool { isRealtimeVoiceEnabled && OpenClickyConfiguration.isConfigured && AccountCapabilities.current().usesRealtime }
```

2. Agent only on own keys: in `sendTranscriptToClaudeWithScreenshot`, change `if isAgentModeEnabled && OpenClickyConfiguration.isConfigured {` to `if isAgentModeEnabled && OpenClickyConfiguration.isConfigured && AccountCapabilities.current().usesAgent {`.

3. Hearing on the Mac for accounts: where `BuddyDictationManager` is created for push-to-talk (search `BuddyDictationManager(` in `CompanionManager.swift`), when `AccountCapabilities.current().hearsOnDevice`, create it with `BuddyDictationManager(transcriptionProvider: AppleSpeechTranscriptionProvider())` (use the provider's existing initialiser; if it needs arguments, copy them from `BuddyTranscriptionProviderFactory.resolveProvider`'s `.appleSpeech` branch). Also re-apply it when the account changes: `BuddyDictationManager` already has a provider-swap method at line ~297 (`transcriptionProvider = provider`); call it from the place that observes sign-in/out (`OpenClickyAuthSession` store/signOut post `OpenClickyConfiguration` changes — subscribe where `CompanionManager` already watches settings reloads).

4. Tools in the ask lane, after the screenshot call:

```swift
                let tools = AccountCapabilities.current().kind == .account ? RealtimeVoiceClient.anthropicToolDefinitions() : []
                let (fullResponseText, toolCalls, _) = try await claudeAPI.analyzeImageStreaming(
                    images: labeledImages,
                    systemPrompt: …unchanged…,
                    conversationHistory: historyForAPI,
                    userPrompt: …unchanged…,
                    tools: tools,
                    onTextChunk: { _ in }
                )
                var toolOutcomes: [String] = []
                for call in toolCalls.prefix(2) {
                    switch MacAction.parse(toolName: call.name, arguments: call.arguments) {
                    case .action(let action): toolOutcomes.append((await macActionRunner.perform(action)).spokenSentence)
                    case .badArguments(let outcome): toolOutcomes.append(outcome.spokenSentence)
                    case .notAFastAction: toolOutcomes.append(AccountLimitError.notOnPlan.message)
                    }
                }
```

then use `let spokenText = Self.spokenReply(text: parseResult.spokenText, toolOutcomes: toolOutcomes)` where `spokenText` was set from `parseResult.spokenText`. Add the helper:

```swift
    static func spokenReply(text: String, toolOutcomes: [String]) -> String {
        let answer = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return ([answer] + toolOutcomes).filter { !$0.isEmpty }.joined(separator: " ")
    }
```

Append one line to the account system prompt (in the same `composeTalkInstructions` call, only when `tools` is non-empty): `"When the user asks you to open an app or a website, make or show a folder, change the volume or control playback, call the matching tool instead of describing the steps. For anything else you can only explain and point."`

5. Limits in the ask lane: in the `catch` of `sendTranscriptToClaudeWithScreenshot`, before `speakCreditsErrorFallback()`, map a 402:

```swift
            } catch let error as NSError where error.domain == "ClaudeAPI" && error.code == 402 {
                let body = Data(((error.userInfo[NSLocalizedDescriptionKey] as? String) ?? "").drop { $0 != "{" }.utf8)
                let message = AccountLimitError.from(status: 402, body: body)?.message ?? AccountLimitError.personalLimit.message
                speakWithSystemVoice(message)
```

(`ClaudeAPI` puts the response body after `API Error (402): `; `drop { $0 != "{" }` keeps the JSON.)

6. Voice: the existing `do { try await elevenLabsTTSClient.speakText(spokenText) } catch { speakWithSystemVoice(spokenText) }` already falls back on any error including `402 tts_budget`; leave it, but stop tracking `tts_budget` as an error: inside that `catch`, skip `ClickyAnalytics.trackTTSError` when `(error as NSError).code == 402`.

- [ ] **Step 4: Run to verify pass**

Run: Mac tests (all `OpenClickyTests`); build the app.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/CompanionManager.swift macos/OpenClicky/OpenClickyTests/AccountCapabilitiesTests.swift
git commit -m "feat(mac): on an account the companion hears on the Mac, offers quick actions to Claude, and says plainly when a limit is reached"
```

---

### Task 14: App — sign up, confirmation and recovery in `OpenClickyAuthSession`

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyAuthSession.swift`
- Test: `macos/OpenClicky/OpenClickyTests/AccountAuthTests.swift`

**Interfaces:**
- Consumes: `/auth/config` new fields `accountsOpen`, `confirmRedirectUrl` (Task 7).
- Produces:
  - `struct AuthConfig: Decodable { let supabaseUrl: String; let publishableKey: String; let accountsOpen: Bool?; let confirmRedirectUrl: String? }` (make the existing private struct internal for tests)
  - `enum SignUpState: Equatable { case idle, sending, awaitingConfirmation(email: String), signedIn, failed(String), full }`
  - `@Published private(set) var signUpState: SignUpState`
  - `func signUp(email: String, password: String) async`
  - `func waitForConfirmation(email: String, password: String, pollEvery seconds: Double = 5, timeout: Double = 15 * 60) async -> Bool`
  - `func recover(email: String) async -> Bool`
  - `static func signUpRequest(backendBaseURL: String, email: String, password: String) -> URLRequest?` (pure; targets the backend's `POST /auth/signup` — ruling 2026-10-10: Supabase is shared across FlowsXR projects, so the backend owns sign-up and the account cap)

- [ ] **Step 1: Write the failing tests**

```swift
// macos/OpenClicky/OpenClickyTests/AccountAuthTests.swift
import Foundation
import Testing
@testable import OpenClicky

struct AccountAuthTests {
    let config = OpenClickyAuthSession.AuthConfig(supabaseUrl: "https://db.example", publishableKey: "pk", accountsOpen: true, confirmRedirectUrl: "https://api.example/auth/confirmed")

    @Test func signUpGoesThroughTheBackend() throws {
        let request = try #require(OpenClickyAuthSession.signUpRequest(backendBaseURL: "https://api.example", email: "gran@example.com", password: "long-enough-1"))
        #expect(request.url?.absoluteString == "https://api.example/auth/signup")
        #expect(request.httpMethod == "POST")
        let body = try JSONSerialization.jsonObject(with: request.httpBody!) as! [String: String]
        #expect(body == ["email": "gran@example.com", "password": "long-enough-1"])
    }

    @Test func oldBackendsWithoutTheFieldDecode() throws {
        let decoded = try JSONDecoder().decode(OpenClickyAuthSession.AuthConfig.self, from: Data(#"{"supabaseUrl":"u","publishableKey":"k"}"#.utf8))
        #expect(decoded.accountsOpen == nil)
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: Mac tests `-only-testing:OpenClickyTests/AccountAuthTests`
Expected: FAIL — `AuthConfig` is private / `signUpRequest` missing.

- [ ] **Step 3: Implement** in `OpenClickyAuthSession.swift`:

1. Change `private struct AuthConfig` to `struct AuthConfig` and add `let accountsOpen: Bool?` and `let confirmRedirectUrl: String?`.
2. Add:

```swift
    enum SignUpState: Equatable { case idle, sending, awaitingConfirmation(email: String), signedIn, failed(String), full }
    @Published private(set) var signUpState: SignUpState = .idle

    static func signUpRequest(backendBaseURL: String, email: String, password: String) -> URLRequest? {
        guard let url = URL(string: "\(backendBaseURL)/auth/signup") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["email": email, "password": password])
        return request
    }

    /// Creates the account; Supabase emails a confirmation link (from hello@flowsxr.com). Then polls
    /// a password sign-in until the link has been tapped, so the person never has to come back and type.
    func signUp(email: String, password: String) async {
        signUpState = .sending
        do {
            let config = try await authConfig()
            guard config.accountsOpen != false else { signUpState = .full; return }
            guard let request = Self.signUpRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: email, password: password) else { signUpState = .failed("sign-up isn't available right now"); return }
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200..<300).contains(status) else {
                let json = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
                let error = json["error"] as? String ?? ""
                signUpState = error == "accounts_full" ? .full : .failed(error.isEmpty ? "couldn't create the account (\(status))." : error)
                return
            }
            signUpState = .awaitingConfirmation(email: email)
            if await waitForConfirmation(email: email, password: password) { signUpState = .signedIn }
        } catch {
            signUpState = .failed(Self.describe(error))
        }
    }

    func waitForConfirmation(email: String, password: String, pollEvery seconds: Double = 5, timeout: Double = 15 * 60) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline, !Task.isCancelled {
            if await signIn(email: email, password: password) { return true }
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
        return false
    }

    func recover(email: String) async -> Bool {
        guard let config = try? await authConfig(), let url = URL(string: "\(config.supabaseUrl)/auth/v1/recover") else { return false }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(config.publishableKey, forHTTPHeaderField: "apikey")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["email": email])
        let status = ((try? await URLSession.shared.data(for: request))?.1 as? HTTPURLResponse)?.statusCode ?? 0
        return (200..<300).contains(status)
    }

```

`signIn` already records `lastErrorText`; while waiting for confirmation it fails with "Email not confirmed" — clear `lastErrorText` on success (already done by `signIn`).

- [ ] **Step 4: Run to verify pass**

Run: Mac tests (`AccountAuthTests`, then all).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/OpenClicky/OpenClickyAuthSession.swift macos/OpenClicky/OpenClickyTests/AccountAuthTests.swift
git commit -m "feat(mac): sign up with an email, wait for the confirmation link, and recover a password"
```

---

### Task 15: App — the account sheet, walkthrough entry and usage bar

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Dictation/UI/AccountSheet.swift`
- Modify: `macos/OpenClicky/OpenClicky/Dictation/UI/OnboardingWindow.swift`, `macos/OpenClicky/OpenClicky/Dictation/UI/SettingsAccountPage.swift`, `macos/OpenClicky/OpenClicky/NotchHUDPanels.swift` (settings tab summary row "account · N% used")

**Interfaces:**
- Consumes: `OpenClickyAuthSession.signUp/recover/signIn/signUpState` (Task 14); `BillingSummary.level/fractionUsed` (Task 10); `BillingStatusModel.refresh()` (existing); Paper components (`PaperCard`, `PaperRow`, `PaperPillButtonStyle`, `PaperHeading`).
- Produces: `struct AccountSheet: View { init(startIn mode: AccountSheet.Mode = .create, onDone: @escaping () -> Void) }` with `enum Mode { case create, signIn }`; `struct AccountUsageBar: View { let summary: BillingSummary }`.

- [ ] **Step 1: Build `AccountSheet`** (Paper look, lowercase copy):

```swift
// macos/OpenClicky/OpenClicky/Dictation/UI/AccountSheet.swift
//
//  AccountSheet.swift
//  OpenClicky
//
//  Create a free account or sign in, in the app: email and password, then "check your email" while
//  the session waits for the confirmation link and signs in on its own.
//

import SwiftUI

struct AccountSheet: View {
    enum Mode { case create, signIn }
    @State var mode: Mode
    let onDone: () -> Void
    @ObservedObject private var auth = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var info: String?

    init(startIn mode: Mode = .create, onDone: @escaping () -> Void) {
        _mode = State(initialValue: mode)
        self.onDone = onDone
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: mode == .create ? "create a free account" : "sign in", size: 26)
            Text(mode == .create
                 ? "polished dictation and spoken answers, on us — up to $10 of use a month."
                 : "use the email and password you signed up with.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            switch auth.signUpState {
            case .awaitingConfirmation(let address):
                PaperCard(padding: 16) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("check your email").font(Paper.rowTitle).foregroundStyle(Paper.ink)
                        Text("we sent a link to \(address). tap it, then come back — openclicky signs you in on its own.")
                            .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    }
                }
            case .full:
                Text(AccountLimitError.accountsFull.message).font(Paper.caption).foregroundStyle(Paper.danger)
            default:
                TextField("email", text: $email).textFieldStyle(.roundedBorder).textContentType(.emailAddress)
                SecureField("password (8+ characters)", text: $password).textFieldStyle(.roundedBorder)
                if case .failed(let message) = auth.signUpState { Text(message).font(Paper.caption).foregroundStyle(Paper.danger) }
                if let text = auth.lastErrorText, mode == .signIn { Text(text).font(Paper.caption).foregroundStyle(Paper.danger) }
                if let info { Text(info).font(Paper.caption).foregroundStyle(Paper.inkSecondary) }
                HStack {
                    Button(mode == .create ? "create account" : "sign in") { submit() }
                        .buttonStyle(PaperPillButtonStyle(prominent: true))
                        .disabled(email.isEmpty || password.count < 8 || auth.signUpState == .sending)
                    Spacer()
                    if mode == .signIn {
                        Button("forgot password?") { Task { info = await auth.recover(email: email) ? "we sent a reset link to \(email)." : "couldn't send a reset link." } }
                            .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                    }
                    Button(mode == .create ? "i have an account" : "create one instead") { mode = mode == .create ? .signIn : .create }
                        .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
                }
            }
        }
        .padding(28)
        .frame(width: 440)
        .background(Paper.background)
        .onChange(of: auth.signUpState) { state in if state == .signedIn { onDone() } }
    }

    private func submit() {
        Task {
            if mode == .create { await auth.signUp(email: email, password: password) }
            else if await auth.signIn(email: email, password: password) { onDone() }
        }
    }
}

/// "plenty left / running low / used up · resets 1 Nov", with a thin bar.
struct AccountUsageBar: View {
    let summary: BillingSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Paper.lineSoft)
                    Capsule().fill(summary.level == .plenty ? Paper.success : Paper.accent)
                        .frame(width: max(4, geometry.size.width * summary.fractionUsed))
                }
            }
            .frame(height: 6)
            Text("\(label) · resets \(resetText)").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
        }
    }

    private var label: String {
        switch summary.level {
        case .plenty: return "plenty left this month"
        case .runningLow: return "running low this month"
        case .usedUp: return "used up for now"
        }
    }

    private var resetText: String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let date = formatter.date(from: summary.monthEnd) ?? ISO8601DateFormatter().date(from: summary.monthEnd) else { return "on the 1st" }
        return date.formatted(.dateTime.day().month(.abbreviated)).lowercased()
    }
}
```

- [ ] **Step 2: Walkthrough entry** — in `OnboardingWindow.swift`, add a final chapter "use clicky's brain" with three buttons: `create a free account` (presents `AccountSheet(startIn: .create)` as a sheet), `sign in` (`.signIn`), `use my own key` (opens Settings → account via `companionManager.showDictationWindow(settingsPage: .account)`), plus `skip — keep everything on this mac`. Follow the existing chapter structure in that file (same `ChapterView` pattern, same next/skip buttons).

- [ ] **Step 3: Settings → account** — in `SettingsAccountPage.swift`, when signed out show two buttons ("create a free account", "sign in") presenting `AccountSheet`; when signed in on an account, show `AccountUsageBar(summary:)` from `BillingStatusModel` and a "details" disclosure with `$X of $10 this month · $Y of $2 today · N spoken characters of 20,000`. Keep the existing own-key field and sign-out button.

- [ ] **Step 4: HUD summary** — in `NotchHUDPanels.swift`'s settings tab, the `BACKEND & ACCOUNT` summary card's `plan` row shows `account · \(Int(summary.fractionUsed * 100))% used` when signed in on an account (read from a `BillingStatusModel` refreshed when the tab appears).

- [ ] **Step 5: Build, run tests, look at it**

Run: Mac tests (all `OpenClickyTests`), then `cd macos/OpenClicky && scripts/release.sh --no-notarize` and open Settings → account and the walkthrough's last chapter.
Expected: tests pass; the sheet opens; "create account" is disabled until the password has 8 characters.

- [ ] **Step 6: Commit**

```bash
git add macos/OpenClicky/OpenClicky/Dictation/UI/AccountSheet.swift macos/OpenClicky/OpenClicky/Dictation/UI/OnboardingWindow.swift macos/OpenClicky/OpenClicky/Dictation/UI/SettingsAccountPage.swift macos/OpenClicky/OpenClicky/NotchHUDPanels.swift
git commit -m "feat(mac): create an account in the app, from the walkthrough or settings, and see how much is left"
```

---

### Task 16: End-to-end on the hosted backend, release, open sign-up (ops — confirm each outward step)

**Files:**
- Modify: `macos/OpenClicky/VERSION` (0.8.0), `macos/OpenClicky/AGENTS.md` (Key Files rows for the new files), `README.md` (accounts paragraph)

- [ ] **Step 1: End-to-end with sign-up still closed.** Temporarily allow one test address: `npm run admin -w backend -- max-accounts <current+1>` is not needed while closed; instead set `ACCOUNTS_OPEN=true` only on a local backend run (`npm run dev -w backend` with `.dev.vars`) pointed at by the app via `OPENCLICKY_BACKEND_URL`, and:
  1. Sign up `openclicky-e2e@flowsxr.com` in the app; confirm the email arrives and the link lands on `/auth/confirmed`; the app signs in on its own.
  2. Dictate with fn into TextEdit → polished text pastes; `admin budget` shows a few hundred micro-dollars.
  3. ⌃⌥ "where can I check my battery health?" in System Settings → spoken by ElevenLabs, pointer on Battery.
  4. ⌃×2 "make a folder called E2E Test on my desktop" → folder appears, sentence spoken.
  5. `npm run admin -w backend -- daily openclicky-e2e@flowsxr.com --usd 0.001` → next ⌃⌥ question speaks the daily-limit sentence; dictation still pastes (unpolished).
  6. Clean up: delete the folder, `admin remove openclicky-e2e@flowsxr.com`.
  Record the results in `docs/accounts-runbook.md`.

- [ ] **Step 2: Bump and document** — `echo 0.8.0 > macos/OpenClicky/VERSION`; add Key Files rows (`AccountCapabilities.swift`, `AccountTakePolisher.swift`, `AccountSheet.swift`; backend `ledger.ts`, `account.ts`, `polish.ts`, `anthropicGrant.ts`, `tts.ts`, `prices.ts`, `modelPolicy.ts`) to `macos/OpenClicky/AGENTS.md` and the backend README section; commit.

- [ ] **Step 3: Release** (with Prasanth's go-ahead): `npm run release:mac:publish`; push `main`.

- [ ] **Step 4: Open sign-up** (with Prasanth's go-ahead): set `ACCOUNTS_OPEN=true` in `backend/.dev.vars`, `npm run deploy:backend -- --env`, confirm `/auth/config` → `accountsOpen: true`. Watch `npm run admin -w backend -- budget` daily for two weeks.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky/VERSION macos/OpenClicky/AGENTS.md README.md docs/accounts-runbook.md
git commit -m "chore: 0.8.0 — free accounts on the grant"
```
