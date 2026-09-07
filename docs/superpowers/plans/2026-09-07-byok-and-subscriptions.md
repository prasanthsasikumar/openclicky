# Bring-Your-Own-Key and Subscriptions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let any OpenClicky user either send their own provider keys with each request (no metering, they pay OpenAI/Anthropic) or use OpenClicky's keys under a monthly credit plan sold through Stripe, with the backend as the single door for both.

**Architecture:** The backend keeps holding OpenClicky's keys. A request that carries `x-openclicky-openai-key` (and optionally `x-openclicky-anthropic-key`) is a BYOK request: the key is used for that request and never stored, and nothing is metered. A request without the header uses OpenClicky's keys and passes through a billing gate: look up the user's plan in Supabase (`oc_subscriptions`, `oc_plans`), refuse with 402 when the month's credits are spent, proxy, then record an `oc_usage_events` row with token counts parsed from the upstream response. Stripe Checkout sells plans; a Stripe webhook keeps `oc_subscriptions` current. The CLI forwards the user's keys as headers (Codex via `env_http_headers`), and the Mac app reads them from `~/.openclicky/shell.json`.

**Tech Stack:** Hono backend (Node + Cloudflare Workers), Supabase self-hosted at `https://db.flowsxr.com` via PostgREST over `fetch`, Stripe REST API over `fetch` (no SDK), vitest, TypeScript CLI, Swift app.

**Spec:** The decisions recorded in this conversation on 2026-09-07: keys stay on the user's Mac and travel per request; subscribers buy monthly credits per plan; Stripe is the payment provider. No separate spec document exists; this plan is the spec.

## Global Constraints

- Backend code must run unchanged on Node and Cloudflare Workers: `fetch`, WebCrypto, no Node-only modules in `backend/src` except in `node.ts`.
- Provider keys never appear in logs, usage rows, or error messages.
- A backend with no `SUPABASE_SERVICE_KEY` configured behaves exactly as today (no billing gate, no metering), so self-hosters are unaffected.
- Header names: `x-openclicky-openai-key`, `x-openclicky-anthropic-key` (lowercase in code; HTTP headers are case-insensitive).
- Table names are prefixed `oc_` in the `public` schema of the shared Supabase (SUPABASE.md option A).
- Credit costs are constants in `backend/src/billing.ts` (see Task 3) and may be tuned later; plan allowances live in `oc_plans` rows.
- Codex 0.152.1 is the agent runtime; it supports `env_http_headers` on `[model_providers.*]` (verified by inspecting the binary).
- Another Claude session is editing `CompanionManager.swift`, `RealtimeVoiceClient.swift`, `NotchHUDPanels.swift`, and `OpenClickyApp.swift`; app-side tasks touch only `OpenClickyConfiguration.swift` and new files, plus a 3-line insertion in `NotchHUDPanels.swift` done last.
- Tests: `npm test -w backend` (vitest), `npm test -w agent` (vitest), Swift via `xcodebuild build-for-testing` / `test-without-building -only-testing:OpenClickyTests CODE_SIGNING_ALLOWED=NO` in `macos/OpenClicky`.
- Commit after every task; messages follow the repo's `feat(area): ...` / `fix(area): ...` style with the Claude co-author trailer.

---

## File map

| File | Responsibility |
|---|---|
| `backend/src/keys.ts` (new) | Resolve which provider keys and base URLs a request uses (BYOK headers vs backend env). |
| `backend/src/db.ts` (new) | Tiny PostgREST client over `fetch` with the Supabase service key. |
| `backend/src/billing.ts` (new) | Plans, credit costs, `BillingStore` interface + Supabase and in-memory implementations, `requireCredits` middleware, `recordUsage`, usage parsing from upstream responses. |
| `backend/src/stripe.ts` (new) | Stripe Checkout / portal session creation and webhook signature verification + event handling. |
| `backend/src/proxy.ts` | Use resolved keys; meter each route. |
| `backend/src/skillsCreate.ts` | Use resolved keys; meter. |
| `backend/src/app.ts` | Wire `keys`, billing gate, `/billing/*` routes; `createApp({ billingStore })` option. |
| `backend/src/env.ts` | New env vars. |
| `backend/supabase/schema.sql` (new) | Tables, indexes, RLS, seed plans. |
| `backend/test/keys.test.ts`, `billing.test.ts`, `stripe.test.ts` (new), `app.test.ts` | Tests. |
| `agent/src/config.ts`, `agent/src/backendHeaders.ts` (new), `ask.ts`, `gate.ts`, `audio.ts`, `realtime.ts`, `cli.ts`, `codex.ts` | Forward BYOK keys. |
| `config/codex-config.toml` | `env_http_headers` on the provider. |
| `macos/OpenClicky/OpenClicky/OpenClickyConfiguration.swift` | `openaiApiKey` / `anthropicApiKey` settings, headers, CLI env. |
| `macos/OpenClicky/OpenClicky/BillingStatus.swift` (new) | Fetches `/billing/me`; the Settings "Account" section view. |
| `macos/OpenClicky/OpenClicky/NotchHUDPanels.swift` | Insert the Account section (3 lines). |
| `README.md`, `macos/OpenClicky/AGENTS.md` | Docs. |

---

### Task 1: Per-request provider key resolution (`keys.ts`)

**Files:**
- Create: `backend/src/keys.ts`
- Modify: `backend/src/env.ts`
- Test: `backend/test/keys.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export const OPENAI_KEY_HEADER = "x-openclicky-openai-key";
  export const ANTHROPIC_KEY_HEADER = "x-openclicky-anthropic-key";
  export interface ProviderKeys {
    /** True when the request brought its own OpenAI key: use it, meter nothing. */
    byok: boolean;
    openaiKey?: string;
    openaiBase: string;          // no trailing slash
    anthropicKey?: string;
    anthropicBase: string;       // no trailing slash
    /** Apply MODEL_ALIASES / *_MODEL_PREFIX (OpenRouter naming). False for BYOK: keys are the vendors' own. */
    mapModels: boolean;
    /** Default model for the Anthropic lanes when the client sends "default". */
    anthropicDefaultModel?: string;
  }
  export function resolveProviderKeys(headers: { get(name: string): string | null | undefined }, env: Env): ProviderKeys;
  ```

- [ ] **Step 1: Add env vars**

In `backend/src/env.ts`, inside `Env`, after `ANTHROPIC_MODEL_PREFIX`:

```ts
  /** BYOK: where a user's own keys are sent (defaults: the vendors' APIs). Overridable for tests. */
  BYOK_OPENAI_BASE_URL?: string;
  BYOK_ANTHROPIC_BASE_URL?: string;
  /** Anthropic model used for "default" on BYOK requests (Anthropic's own id, no OpenRouter prefix). */
  BYOK_ANTHROPIC_MODEL?: string;
```

- [ ] **Step 2: Write the failing tests**

`backend/test/keys.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { resolveProviderKeys, OPENAI_KEY_HEADER, ANTHROPIC_KEY_HEADER } from "../src/keys.js";
import type { Env } from "../src/env.js";

const headers = (h: Record<string, string>) => ({ get: (n: string) => h[n.toLowerCase()] ?? null });
const env: Env = {
  OPENAI_API_KEY: "sk-backend",
  OPENAI_BASE_URL: "https://openrouter.ai/api/v1",
  ANTHROPIC_API_KEY: "ak-backend",
  ANTHROPIC_BASE_URL: "https://openrouter.ai/api",
  ANTHROPIC_MODEL: "anthropic/claude-haiku-4.5",
};

describe("resolveProviderKeys", () => {
  it("uses backend keys and model mapping when no BYOK header is present", () => {
    const k = resolveProviderKeys(headers({}), env);
    expect(k.byok).toBe(false);
    expect(k.openaiKey).toBe("sk-backend");
    expect(k.openaiBase).toBe("https://openrouter.ai/api/v1");
    expect(k.anthropicKey).toBe("ak-backend");
    expect(k.anthropicBase).toBe("https://openrouter.ai/api");
    expect(k.mapModels).toBe(true);
    expect(k.anthropicDefaultModel).toBe("anthropic/claude-haiku-4.5");
  });

  it("uses the request's OpenAI key against OpenAI directly and stops model mapping", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "sk-user" }), env);
    expect(k.byok).toBe(true);
    expect(k.openaiKey).toBe("sk-user");
    expect(k.openaiBase).toBe("https://api.openai.com/v1");
    expect(k.mapModels).toBe(false);
    // No Anthropic key was brought: the Anthropic lanes have nothing to use.
    expect(k.anthropicKey).toBeUndefined();
  });

  it("uses the request's Anthropic key against Anthropic directly", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "sk-user", [ANTHROPIC_KEY_HEADER]: "ak-user" }), env);
    expect(k.anthropicKey).toBe("ak-user");
    expect(k.anthropicBase).toBe("https://api.anthropic.com");
    expect(k.anthropicDefaultModel).toBe("claude-haiku-4-5-20251001");
  });

  it("honours BYOK base overrides and trims whitespace", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "  sk-user \n" }), { ...env, BYOK_OPENAI_BASE_URL: "http://127.0.0.1:9/v1/" });
    expect(k.openaiKey).toBe("sk-user");
    expect(k.openaiBase).toBe("http://127.0.0.1:9/v1");
  });

  it("ignores an empty header", () => {
    expect(resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "   " }), env).byok).toBe(false);
  });
});
```

- [ ] **Step 3: Run the tests to see them fail**

Run: `npm test -w backend -- keys`
Expected: FAIL, cannot find module `../src/keys.js`.

- [ ] **Step 4: Implement `keys.ts`**

```ts
import type { Env } from "./env.js";

export const OPENAI_KEY_HEADER = "x-openclicky-openai-key";
export const ANTHROPIC_KEY_HEADER = "x-openclicky-anthropic-key";

export interface ProviderKeys {
  /** True when the request brought its own OpenAI key: use it, meter nothing. */
  byok: boolean;
  openaiKey?: string;
  openaiBase: string;
  anthropicKey?: string;
  anthropicBase: string;
  /** Apply MODEL_ALIASES / *_MODEL_PREFIX (OpenRouter naming). False for BYOK: the keys are the vendors' own. */
  mapModels: boolean;
  anthropicDefaultModel?: string;
}

const stripSlash = (u: string) => u.replace(/\/+$/, "");
const clean = (v: string | null | undefined) => {
  const t = (v ?? "").trim();
  return t ? t : undefined;
};

/**
 * Which keys this request runs on. A request that carries its own OpenAI key is "bring your own
 * key": it goes to the vendors' APIs with the user's keys (so OpenRouter aliases do not apply) and
 * is never metered. Anything else runs on the backend's keys and is metered by billing.ts.
 */
export function resolveProviderKeys(headers: { get(name: string): string | null | undefined }, env: Env): ProviderKeys {
  const userOpenAI = clean(headers.get(OPENAI_KEY_HEADER));
  const userAnthropic = clean(headers.get(ANTHROPIC_KEY_HEADER));
  if (userOpenAI) {
    return {
      byok: true,
      openaiKey: userOpenAI,
      openaiBase: stripSlash(env.BYOK_OPENAI_BASE_URL || "https://api.openai.com/v1"),
      anthropicKey: userAnthropic,
      anthropicBase: stripSlash(env.BYOK_ANTHROPIC_BASE_URL || "https://api.anthropic.com"),
      mapModels: false,
      anthropicDefaultModel: env.BYOK_ANTHROPIC_MODEL || "claude-haiku-4-5-20251001",
    };
  }
  return {
    byok: false,
    openaiKey: env.OPENAI_API_KEY,
    openaiBase: stripSlash(env.OPENAI_BASE_URL || "https://api.openai.com/v1"),
    anthropicKey: env.ANTHROPIC_API_KEY,
    anthropicBase: stripSlash(env.ANTHROPIC_BASE_URL || "https://api.anthropic.com"),
    mapModels: true,
    anthropicDefaultModel: env.ANTHROPIC_MODEL,
  };
}
```

- [ ] **Step 5: Run the tests**

Run: `npm test -w backend -- keys`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add backend/src/keys.ts backend/src/env.ts backend/test/keys.test.ts
git commit -m "feat(backend): resolve per-request provider keys (bring your own key headers)"
```

---

### Task 2: Route the proxies through the resolved keys

**Files:**
- Modify: `backend/src/proxy.ts` (every `env.OPENAI_API_KEY` / `env.ANTHROPIC_API_KEY` site), `backend/src/skillsCreate.ts:38-52`
- Test: `backend/test/app.test.ts`

**Interfaces:**
- Consumes: `resolveProviderKeys` from Task 1.
- Produces: `proxyOpenAI`, `createRealtimeSession`, `transcribeAudio`, `synthesizeSpeech`, `proxyAnthropic`, `createSkill` accept BYOK headers. Error `{ error: "byok_missing_anthropic_key" }` (402) when a BYOK request hits an Anthropic lane without an Anthropic key.

- [ ] **Step 1: Write the failing tests**

Append to `backend/test/app.test.ts` inside `describe("app")`:

```ts
  it("BYOK: a request with its own OpenAI key is sent with that key to the BYOK base", async () => {
    const token = await jwt();
    const r = await app.request(
      "/v1/chat/completions",
      { ...json({ model: "default", messages: [], stream: true }, token), headers: { ...json({}, token).headers, "x-openclicky-openai-key": "sk-user" } },
      { ...env, OPENAI_BASE_URL: upstreamUrl + "/wrong", BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
    );
    expect(r.status).toBe(200);
    const last = seen.at(-1)!;
    expect(last.url).toBe("/v1/chat/completions");
    expect(last.auth).toBe("Bearer sk-user");
    // "default" resolves to the backend default model, but OpenRouter aliases/prefixes are not applied.
    expect(last.body.model).toBe("gpt-test");
  });

  it("BYOK: the Realtime client secret is minted with the user's key", async () => {
    const token = await jwt();
    const r = await app.request(
      "/agent/realtime/session",
      { method: "POST", headers: { authorization: `Bearer ${token}`, "content-type": "application/json", "x-openclicky-openai-key": "sk-user" }, body: "{}" },
      { ...env, BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
    );
    expect(r.status).toBe(200);
    expect(seen.at(-1)!.auth).toBe("Bearer sk-user");
  });

  it("BYOK: Anthropic lanes need the user's Anthropic key", async () => {
    const token = await jwt();
    const headers = { authorization: `Bearer ${token}`, "content-type": "application/json", "x-openclicky-openai-key": "sk-user" };
    const missing = await call("/v1/messages", { method: "POST", headers, body: JSON.stringify({ model: "default", messages: [] }) });
    expect(missing.status).toBe(402);
    expect(await missing.json()).toEqual({ error: "byok_missing_anthropic_key" });

    const withKey = await app.request(
      "/chat",
      { method: "POST", headers: { ...headers, "x-openclicky-anthropic-key": "ak-user" }, body: JSON.stringify({ model: "default", messages: [] }) },
      { ...env, BYOK_ANTHROPIC_BASE_URL: upstreamUrl },
    );
    expect(withKey.status).toBe(200);
    const last = seen.at(-1)!;
    expect(last.apiKey).toBe("ak-user");
    expect(last.body.model).toBe("claude-haiku-4-5-20251001");
  });
```

- [ ] **Step 2: Run to see them fail**

Run: `npm test -w backend -- app`
Expected: FAIL: `last.auth` is `Bearer sk-upstream`, and the 402 case returns 200.

- [ ] **Step 3: Rewrite the key sites in `proxy.ts`**

Replace the top of `proxyOpenAI`:

```ts
export async function proxyOpenAI(c: Context, upstreamPath: string): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.openaiKey) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  let body = applyModelDefault(await readJson(c), env.OPENAI_MODEL);
  if (keys.mapModels) body = applyModelMapping(body, env.MODEL_ALIASES, env.OPENAI_MODEL_PREFIX);
  const upstream = await fetch(keys.openaiBase + upstreamPath, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      authorization: `Bearer ${keys.openaiKey}`,
    },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}
```

Add `import { resolveProviderKeys } from "./keys.js";` at the top. Apply the same pattern to `createRealtimeSession` (`keys.openaiBase + "/realtime/client_secrets"`, `Bearer ${keys.openaiKey}`), `transcribeAudio` (`keys.openaiBase + "/audio/transcriptions"`), and the OpenAI branch of `synthesizeSpeech` (`keys.openaiBase + "/audio/speech"`; the ElevenLabs branch stays on `env.ELEVENLABS_API_KEY` but only when `!keys.byok`, so a BYOK user is never charged your ElevenLabs quota: `if (env.ELEVENLABS_API_KEY && !keys.byok) { ... }`).

`proxyAnthropic` becomes:

```ts
export async function proxyAnthropic(c: Context): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (keys.byok && !keys.anthropicKey) return c.json({ error: "byok_missing_anthropic_key" }, 402);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  let body = applyModelDefault(await readJson(c), keys.anthropicDefaultModel);
  if (keys.mapModels) body = applyModelMapping(body, env.MODEL_ALIASES, env.ANTHROPIC_MODEL_PREFIX);
  const upstream = await fetch(keys.anthropicBase + "/v1/messages", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      "x-api-key": keys.anthropicKey,
      "anthropic-version": c.req.header("anthropic-version") ?? "2023-06-01",
    },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}
```

In `skillsCreate.ts`, replace the model/key block:

```ts
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  const model = env.SKILL_CREATE_MODEL || env.OPENAI_MODEL;
  if (!keys.openaiKey || !model) {
    return c.json({ error: "skill creation needs OPENAI_API_KEY and OPENAI_MODEL (or SKILL_CREATE_MODEL) on the backend" }, 503);
  }
  ...
  const upstream = await fetch(keys.openaiBase + "/chat/completions", {
    method: "POST",
    headers: { authorization: `Bearer ${keys.openaiKey}`, "content-type": "application/json" },
    body: JSON.stringify({
      model: keys.mapModels ? mapModelName(model, env.MODEL_ALIASES, env.OPENAI_MODEL_PREFIX) : model,
```

- [ ] **Step 4: Run the whole backend suite**

Run: `npm test -w backend`
Expected: PASS, including the three new tests and every existing one (the existing tests send no BYOK header, so behaviour is unchanged).

- [ ] **Step 5: Commit**

```bash
git add backend/src/proxy.ts backend/src/skillsCreate.ts backend/test/app.test.ts
git commit -m "feat(backend): every model route honours bring-your-own-key headers"
```

---

### Task 3: Billing store, plans, credit costs, and usage parsing (`db.ts`, `billing.ts`, schema)

**Files:**
- Create: `backend/src/db.ts`, `backend/src/billing.ts`, `backend/supabase/schema.sql`
- Modify: `backend/src/env.ts`
- Test: `backend/test/billing.test.ts`

**Interfaces:**
- Produces:
  ```ts
  // db.ts
  export class SupabaseRest {
    constructor(baseUrl: string, serviceKey: string, fetchImpl?: typeof fetch);
    select<T>(table: string, query: string): Promise<T[]>;          // query = PostgREST query string, e.g. "user_id=eq.abc&select=*"
    insert<T>(table: string, row: Record<string, unknown>): Promise<T>;
    upsert<T>(table: string, row: Record<string, unknown>, onConflict: string): Promise<T>;
  }
  // billing.ts
  export type PlanId = string;
  export interface Plan { id: PlanId; name: string; monthly_credits: number; stripe_price_id: string | null }
  export interface Subscription { user_id: string; plan_id: PlanId; status: string; current_period_start: string; current_period_end: string; stripe_customer_id: string | null; stripe_subscription_id: string | null }
  export interface UsageEvent { user_id: string; route: string; model?: string; input_tokens: number; output_tokens: number; audio_seconds: number; characters: number; credits: number }
  export interface BillingStore {
    plan(id: PlanId): Promise<Plan | undefined>;
    planByPrice(stripePriceId: string): Promise<Plan | undefined>;
    subscription(userId: string): Promise<Subscription | undefined>;
    upsertSubscription(sub: Subscription): Promise<void>;
    creditsUsedSince(userId: string, sinceIso: string): Promise<number>;
    recordUsage(event: UsageEvent): Promise<void>;
  }
  export class MemoryBillingStore implements BillingStore { plans: Plan[]; subs: Map<string, Subscription>; events: UsageEvent[] }
  export class SupabaseBillingStore implements BillingStore { constructor(db: SupabaseRest) }
  export const FREE_PLAN_ID = "free";
  export const CREDIT_COSTS = { realtimeSession: 30, skillCreate: 2, flatTokenFallback: 2 };
  export function creditsForTokens(inputTokens: number, outputTokens: number): number;   // ceil((in + 4*out) / 1000), min 1
  export function creditsForAudioSeconds(seconds: number): number;                        // ceil(seconds / 15), min 1
  export function creditsForCharacters(chars: number): number;                            // ceil(chars / 500), min 1
  export interface BillingContext { byok: boolean; userId: string; plan?: Plan; periodStart: string; periodEnd: string; used: number }
  export type BillingVariables = { billing: BillingContext };
  export function requireCredits(store: BillingStore | undefined): MiddlewareHandler<{ Variables: { principal: Principal; billing: BillingContext } }>;
  export function meterResponse(c: Context, upstream: Response, route: string, model: string | undefined, store: BillingStore | undefined, fallback: () => number): Response;
  export function parseUsage(text: string): { inputTokens: number; outputTokens: number } | undefined;
  export function billingSummary(c: Context): { byok: boolean; plan: string; status: string; used: number; limit: number; periodEnd: string };
  ```

- [ ] **Step 1: Add env vars**

In `env.ts` add:

```ts
  /** Supabase service key (server-side, bypasses RLS): enables the billing store. Without it nothing is metered. */
  SUPABASE_SERVICE_KEY?: string;
  /** Free-tier allowance for signed-in users with no subscription row (default 200). */
  FREE_MONTHLY_CREDITS?: string;
```

(`SUPABASE_URL` already exists.)

- [ ] **Step 2: Write the schema**

`backend/supabase/schema.sql`:

```sql
-- OpenClicky billing tables (shared Supabase, "oc_" prefix in public). Idempotent.
create table if not exists public.oc_plans (
  id text primary key,
  name text not null,
  monthly_credits integer not null,
  stripe_price_id text unique
);

create table if not exists public.oc_subscriptions (
  user_id text primary key,
  plan_id text not null references public.oc_plans(id),
  status text not null,                      -- active | trialing | past_due | canceled | unpaid
  current_period_start timestamptz not null,
  current_period_end timestamptz not null,
  stripe_customer_id text,
  stripe_subscription_id text unique,
  updated_at timestamptz not null default now()
);

create table if not exists public.oc_usage_events (
  id bigserial primary key,
  user_id text not null,
  ts timestamptz not null default now(),
  route text not null,
  model text,
  input_tokens integer not null default 0,
  output_tokens integer not null default 0,
  audio_seconds numeric not null default 0,
  characters integer not null default 0,
  credits numeric not null
);
create index if not exists oc_usage_events_user_ts on public.oc_usage_events (user_id, ts desc);

-- Only the service key touches these tables; users never read them directly.
alter table public.oc_plans enable row level security;
alter table public.oc_subscriptions enable row level security;
alter table public.oc_usage_events enable row level security;

insert into public.oc_plans (id, name, monthly_credits, stripe_price_id) values
  ('free', 'Free', 200, null),
  ('starter', 'Starter', 3000, null),
  ('pro', 'Pro', 12000, null)
on conflict (id) do nothing;
```

Load it once (SUPABASE.md "load a schema file"):

```bash
scp backend/supabase/schema.sql root@104.168.48.121:/tmp/oc_schema.sql && ssh root@104.168.48.121 'docker cp /tmp/oc_schema.sql supabase-db:/tmp/ && docker exec supabase-db psql -U postgres -d postgres -v ON_ERROR_STOP=1 -f /tmp/oc_schema.sql'
```

Then set the Stripe price ids on the `starter` and `pro` rows from Studio once Task 5 creates them.

- [ ] **Step 3: Write the failing tests**

`backend/test/billing.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { Hono } from "hono";
import {
  MemoryBillingStore, requireCredits, meterResponse, parseUsage, creditsForTokens, creditsForAudioSeconds, creditsForCharacters,
  FREE_PLAN_ID, type BillingContext,
} from "../src/billing.js";
import type { Principal } from "../src/auth.js";

const principal: Principal = { sub: "user-1", via: "session" };

function appWith(store: MemoryBillingStore | undefined) {
  const app = new Hono<{ Variables: { principal: Principal; billing: BillingContext } }>();
  app.use("*", async (c, next) => { c.set("principal", principal); await next(); });
  app.use("/v1/*", requireCredits(store));
  app.post("/v1/chat", (c) => {
    const upstream = new Response('data: {"usage":{"prompt_tokens":1000,"completion_tokens":500}}\n\ndata: [DONE]\n\n', { headers: { "content-type": "text/event-stream" } });
    return meterResponse(c, upstream, "/v1/chat", "gpt-test", store, () => 2);
  });
  return app;
}

describe("credit costs", () => {
  it("tokens: 1 per 1k input, 4 per 1k output, minimum 1", () => {
    expect(creditsForTokens(1000, 500)).toBe(3);
    expect(creditsForTokens(0, 0)).toBe(1);
  });
  it("audio: 1 per started 15 s", () => {
    expect(creditsForAudioSeconds(4)).toBe(1);
    expect(creditsForAudioSeconds(31)).toBe(3);
  });
  it("characters: 1 per 500", () => {
    expect(creditsForCharacters(1200)).toBe(3);
  });
});

describe("parseUsage", () => {
  it("reads OpenAI chat, Responses, and Anthropic shapes", () => {
    expect(parseUsage('{"usage":{"prompt_tokens":10,"completion_tokens":20}}')).toEqual({ inputTokens: 10, outputTokens: 20 });
    expect(parseUsage('data: {"type":"response.completed","response":{"usage":{"input_tokens":7,"output_tokens":9}}}')).toEqual({ inputTokens: 7, outputTokens: 9 });
    const anthropic = 'event: message_start\ndata: {"type":"message_start","message":{"usage":{"input_tokens":50,"output_tokens":1}}}\n\nevent: message_delta\ndata: {"type":"message_delta","usage":{"output_tokens":40}}\n\n';
    expect(parseUsage(anthropic)).toEqual({ inputTokens: 50, outputTokens: 40 });
    expect(parseUsage("data: [DONE]")).toBeUndefined();
  });
});

describe("requireCredits + meterResponse", () => {
  it("passes through and meters nothing when no store is configured", async () => {
    const r = await appWith(undefined).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(200);
    expect(await r.text()).toContain("[DONE]");
  });

  it("skips the gate for BYOK requests", async () => {
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/chat", { method: "POST", headers: { "x-openclicky-openai-key": "sk-user" } });
    expect(r.status).toBe(200);
    expect(store.events).toHaveLength(0);
  });

  it("meters a free-tier user from the streamed usage", async () => {
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(200);
    await r.text();
    await new Promise((res) => setTimeout(res, 10));
    expect(store.events).toEqual([expect.objectContaining({ user_id: "user-1", route: "/v1/chat", model: "gpt-test", input_tokens: 1000, output_tokens: 500, credits: 3 })]);
  });

  it("refuses with 402 once the plan's credits are spent", async () => {
    const store = new MemoryBillingStore();
    store.events.push({ user_id: "user-1", route: "/v1/chat", input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: 200 });
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(402);
    expect(await r.json()).toMatchObject({ error: "credits_exhausted", used: 200, limit: 200, plan: FREE_PLAN_ID });
  });

  it("refuses with 402 when a paid subscription is not active", async () => {
    const store = new MemoryBillingStore();
    store.subs.set("user-1", { user_id: "user-1", plan_id: "pro", status: "canceled", current_period_start: "2026-09-01T00:00:00Z", current_period_end: "2026-10-01T00:00:00Z", stripe_customer_id: null, stripe_subscription_id: null });
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(402);
    expect(await r.json()).toMatchObject({ error: "subscription_inactive" });
  });
});
```

- [ ] **Step 4: Run to see them fail**

Run: `npm test -w backend -- billing`
Expected: FAIL, cannot find module `../src/billing.js`.

- [ ] **Step 5: Implement `db.ts`**

```ts
/** Minimal PostgREST client (Supabase REST) over fetch: works on Node and Workers, no SDK. */
export class SupabaseRest {
  private readonly base: string;
  constructor(baseUrl: string, private readonly serviceKey: string, private readonly fetchImpl: typeof fetch = fetch) {
    this.base = baseUrl.replace(/\/+$/, "") + "/rest/v1";
  }

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return { apikey: this.serviceKey, authorization: `Bearer ${this.serviceKey}`, "content-type": "application/json", ...extra };
  }

  private async check(res: Response, what: string): Promise<void> {
    if (!res.ok) throw new Error(`supabase ${what} failed (${res.status}): ${(await res.text()).slice(0, 300)}`);
  }

  async select<T>(table: string, query: string): Promise<T[]> {
    const res = await this.fetchImpl(`${this.base}/${table}?${query}`, { headers: this.headers() });
    await this.check(res, `select ${table}`);
    return (await res.json()) as T[];
  }

  async insert<T>(table: string, row: Record<string, unknown>): Promise<T> {
    const res = await this.fetchImpl(`${this.base}/${table}`, { method: "POST", headers: this.headers({ prefer: "return=representation" }), body: JSON.stringify(row) });
    await this.check(res, `insert ${table}`);
    return ((await res.json()) as T[])[0];
  }

  async upsert<T>(table: string, row: Record<string, unknown>, onConflict: string): Promise<T> {
    const res = await this.fetchImpl(`${this.base}/${table}?on_conflict=${onConflict}`, {
      method: "POST",
      headers: this.headers({ prefer: "resolution=merge-duplicates,return=representation" }),
      body: JSON.stringify(row),
    });
    await this.check(res, `upsert ${table}`);
    return ((await res.json()) as T[])[0];
  }
}
```

- [ ] **Step 6: Implement `billing.ts`**

```ts
import type { Context, MiddlewareHandler } from "hono";
import type { Principal } from "./auth.js";
import { OPENAI_KEY_HEADER } from "./keys.js";
import type { SupabaseRest } from "./db.js";

export type PlanId = string;
export interface Plan { id: PlanId; name: string; monthly_credits: number; stripe_price_id: string | null }
export interface Subscription {
  user_id: string; plan_id: PlanId; status: string;
  current_period_start: string; current_period_end: string;
  stripe_customer_id: string | null; stripe_subscription_id: string | null;
}
export interface UsageEvent {
  user_id: string; route: string; model?: string;
  input_tokens: number; output_tokens: number; audio_seconds: number; characters: number; credits: number;
}

export interface BillingStore {
  plan(id: PlanId): Promise<Plan | undefined>;
  planByPrice(stripePriceId: string): Promise<Plan | undefined>;
  subscription(userId: string): Promise<Subscription | undefined>;
  upsertSubscription(sub: Subscription): Promise<void>;
  creditsUsedSince(userId: string, sinceIso: string): Promise<number>;
  recordUsage(event: UsageEvent): Promise<void>;
}

export const FREE_PLAN_ID = "free";
export const DEFAULT_FREE_MONTHLY_CREDITS = 200;
/** Per-request costs that do not depend on the response. */
export const CREDIT_COSTS = { realtimeSession: 30, skillCreate: 2, flatTokenFallback: 2 };

export const creditsForTokens = (inputTokens: number, outputTokens: number) => Math.max(1, Math.ceil((inputTokens + 4 * outputTokens) / 1000));
export const creditsForAudioSeconds = (seconds: number) => Math.max(1, Math.ceil(seconds / 15));
export const creditsForCharacters = (chars: number) => Math.max(1, Math.ceil(chars / 500));

export class MemoryBillingStore implements BillingStore {
  plans: Plan[] = [
    { id: "free", name: "Free", monthly_credits: DEFAULT_FREE_MONTHLY_CREDITS, stripe_price_id: null },
    { id: "starter", name: "Starter", monthly_credits: 3000, stripe_price_id: "price_starter" },
    { id: "pro", name: "Pro", monthly_credits: 12000, stripe_price_id: "price_pro" },
  ];
  subs = new Map<string, Subscription>();
  events: UsageEvent[] = [];
  async plan(id: PlanId) { return this.plans.find((p) => p.id === id); }
  async planByPrice(priceId: string) { return this.plans.find((p) => p.stripe_price_id === priceId); }
  async subscription(userId: string) { return this.subs.get(userId); }
  async upsertSubscription(sub: Subscription) { this.subs.set(sub.user_id, sub); }
  async creditsUsedSince(userId: string, _sinceIso: string) { return this.events.filter((e) => e.user_id === userId).reduce((n, e) => n + e.credits, 0); }
  async recordUsage(event: UsageEvent) { this.events.push(event); }
}

export class SupabaseBillingStore implements BillingStore {
  constructor(private readonly db: SupabaseRest) {}
  async plan(id: PlanId) { return (await this.db.select<Plan>("oc_plans", `id=eq.${encodeURIComponent(id)}&select=*`))[0]; }
  async planByPrice(priceId: string) { return (await this.db.select<Plan>("oc_plans", `stripe_price_id=eq.${encodeURIComponent(priceId)}&select=*`))[0]; }
  async subscription(userId: string) { return (await this.db.select<Subscription>("oc_subscriptions", `user_id=eq.${encodeURIComponent(userId)}&select=*`))[0]; }
  async upsertSubscription(sub: Subscription) { await this.db.upsert("oc_subscriptions", { ...sub, updated_at: new Date().toISOString() }, "user_id"); }
  async creditsUsedSince(userId: string, sinceIso: string) {
    const rows = await this.db.select<{ credits: number }>("oc_usage_events", `user_id=eq.${encodeURIComponent(userId)}&ts=gte.${encodeURIComponent(sinceIso)}&select=credits`);
    return rows.reduce((n, r) => n + Number(r.credits), 0);
  }
  async recordUsage(event: UsageEvent) { await this.db.insert("oc_usage_events", event); }
}

export interface BillingContext {
  byok: boolean; userId: string; plan?: Plan; status: string;
  periodStart: string; periodEnd: string; used: number;
}
export type BillingVariables = { billing: BillingContext };

/** First and next first-of-month in UTC, for users without a Stripe period. */
function calendarPeriod(now = new Date()): { start: string; end: string } {
  const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const end = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1));
  return { start: start.toISOString(), end: end.toISOString() };
}

const ACTIVE_STATUSES = new Set(["active", "trialing"]);

/**
 * The billing gate. BYOK requests and backends without a store pass untouched. Everyone else gets
 * their plan and current-period usage attached as `billing`, or a 402 when out of credits.
 */
export function requireCredits(store: BillingStore | undefined): MiddlewareHandler<{ Variables: { principal: Principal; billing: BillingContext } }> {
  return async (c, next) => {
    const byok = Boolean(c.req.header(OPENAI_KEY_HEADER)?.trim());
    const userId = c.get("principal")?.sub ?? "";
    if (!store || byok || !userId) {
      c.set("billing", { byok, userId, status: byok ? "byok" : "unmetered", ...periodDefaults(), used: 0 });
      await next();
      return;
    }
    const sub = await store.subscription(userId);
    const planId = sub?.plan_id ?? FREE_PLAN_ID;
    const plan = (await store.plan(planId)) ?? { id: FREE_PLAN_ID, name: "Free", monthly_credits: DEFAULT_FREE_MONTHLY_CREDITS, stripe_price_id: null };
    const status = sub?.status ?? "free";
    if (sub && plan.id !== FREE_PLAN_ID && !ACTIVE_STATUSES.has(status)) {
      return c.json({ error: "subscription_inactive", plan: plan.id, status }, 402);
    }
    const period = sub && ACTIVE_STATUSES.has(status) ? { start: sub.current_period_start, end: sub.current_period_end } : calendarPeriod();
    const used = await store.creditsUsedSince(userId, period.start);
    if (used >= plan.monthly_credits) {
      return c.json({ error: "credits_exhausted", plan: plan.id, used, limit: plan.monthly_credits, resets_at: period.end }, 402);
    }
    c.set("billing", { byok: false, userId, plan, status, periodStart: period.start, periodEnd: period.end, used });
    await next();
  };
}

function periodDefaults() {
  const p = calendarPeriod();
  return { periodStart: p.start, periodEnd: p.end };
}

/** Token usage from a JSON or SSE body: OpenAI chat (`prompt_/completion_tokens`), Responses and Anthropic (`input_/output_tokens`). */
export function parseUsage(text: string): { inputTokens: number; outputTokens: number } | undefined {
  let input = 0, output = 0, found = false;
  for (const m of text.matchAll(/"usage"\s*:\s*(\{[^{}]*\})/g)) {
    try {
      const u = JSON.parse(m[1]) as Record<string, number>;
      const i = u.input_tokens ?? u.prompt_tokens;
      const o = u.output_tokens ?? u.completion_tokens;
      if (typeof i === "number") { input = Math.max(input, i); found = true; }
      if (typeof o === "number") { output = Math.max(output, o); found = true; }
    } catch { /* not a flat usage object; keep scanning */ }
  }
  return found ? { inputTokens: input, outputTokens: output } : undefined;
}

/** Record a usage row without holding the response (Workers keep the promise alive via waitUntil). */
export function chargeCredits(c: Context, store: BillingStore | undefined, event: Omit<UsageEvent, "user_id">): void {
  const billing = c.get("billing") as BillingContext | undefined;
  if (!store || !billing || billing.byok || !billing.userId) return;
  const promise = store.recordUsage({ user_id: billing.userId, ...event }).catch((e) => console.error(`usage not recorded: ${(e as Error).message}`));
  const ctx = (c as { executionCtx?: { waitUntil(p: Promise<unknown>): void } }).executionCtx;
  try { ctx?.waitUntil(promise); } catch { /* Node: no execution context */ }
}

/**
 * Pass the upstream body through to the client and, once it has all gone by, charge credits from
 * the usage it reported (or `fallback()` credits when it reported none). Streams stay streams.
 */
export function meterResponse(c: Context, upstream: Response, route: string, model: string | undefined, store: BillingStore | undefined, fallback: () => number): Response {
  const billing = c.get("billing") as BillingContext | undefined;
  const headers = new Headers();
  upstream.headers.forEach((v, k) => { if (!["content-length", "connection", "keep-alive", "transfer-encoding", "content-encoding"].includes(k.toLowerCase())) headers.set(k, v); });
  if (!store || !billing || billing.byok || !upstream.ok || !upstream.body) {
    return new Response(upstream.body, { status: upstream.status, headers });
  }
  let collected = "";
  const decoder = new TextDecoder();
  const tap = new TransformStream<Uint8Array, Uint8Array>({
    transform(chunk, controller) {
      if (collected.length < 512_000) collected += decoder.decode(chunk, { stream: true });
      controller.enqueue(chunk);
    },
    flush() {
      const usage = parseUsage(collected);
      const credits = usage ? creditsForTokens(usage.inputTokens, usage.outputTokens) : fallback();
      chargeCredits(c, store, { route, model, input_tokens: usage?.inputTokens ?? 0, output_tokens: usage?.outputTokens ?? 0, audio_seconds: 0, characters: 0, credits });
    },
  });
  return new Response(upstream.body.pipeThrough(tap), { status: upstream.status, headers });
}

/** What the app shows in Settings. */
export function billingSummary(c: Context): { byok: boolean; plan: string; status: string; used: number; limit: number; periodEnd: string } {
  const b = c.get("billing") as BillingContext;
  return { byok: b.byok, plan: b.plan?.id ?? (b.byok ? "byok" : "unmetered"), status: b.status, used: b.used, limit: b.plan?.monthly_credits ?? 0, periodEnd: b.periodEnd };
}
```

- [ ] **Step 7: Run the tests**

Run: `npm test -w backend -- billing`
Expected: PASS (all 8).

- [ ] **Step 8: Commit**

```bash
git add backend/src/db.ts backend/src/billing.ts backend/src/env.ts backend/supabase/schema.sql backend/test/billing.test.ts
git commit -m "feat(backend): billing store, credit costs, usage parsing, and the credits gate"
```

---

### Task 4: Wire the gate and metering into every model route

**Files:**
- Modify: `backend/src/app.ts`, `backend/src/proxy.ts`, `backend/src/skillsCreate.ts`
- Test: `backend/test/app.test.ts`

**Interfaces:**
- Consumes: Task 3's `requireCredits`, `meterResponse`, `chargeCredits`, `creditsFor*`, `CREDIT_COSTS`, `SupabaseBillingStore`, `SupabaseRest`, `billingSummary`.
- Produces: `createApp({ log, billingStore })`; `GET /billing/me` → `{ byok, plan, status, used, limit, periodEnd }`; every model route charges credits for metered users.

- [ ] **Step 1: Write the failing tests**

Append to `backend/test/app.test.ts`:

```ts
  describe("billing", () => {
    it("meters a Codex turn and reports it on /billing/me", async () => {
      const store = new MemoryBillingStore();
      const billed = createApp({ log: null, billingStore: store });
      const token = await jwt();
      const r = await billed.request("/v1/responses", json({ model: "default", input: "hi", stream: true }, token), { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1" });
      expect(r.status).toBe(200);
      await r.text();
      await new Promise((res) => setTimeout(res, 20));
      expect(store.events).toHaveLength(1);
      expect(store.events[0]).toMatchObject({ user_id: "user-1", route: "/v1/responses", credits: 2 }); // the fake upstream reports no usage → flat fallback

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
      await new Promise((res) => setTimeout(res, 20));
      expect(store.events.map((e) => [e.route, e.credits])).toEqual([["/agent/realtime/session", 30], ["/agent/transcribe", 2]]);
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
        { ...json({ model: "default", messages: [] }, token), headers: { ...json({}, token).headers, "x-openclicky-openai-key": "sk-user" } },
        { ...env, BYOK_OPENAI_BASE_URL: upstreamUrl + "/v1" },
      );
      expect(byok.status).toBe(200);
      expect(store.events).toHaveLength(1);
    });

    it("/billing/me reports byok for a request with its own key", async () => {
      const billed = createApp({ log: null, billingStore: new MemoryBillingStore() });
      const me = await billed.request("/billing/me", { headers: { authorization: `Bearer ${await jwt()}`, "x-openclicky-openai-key": "sk-user" } }, env);
      expect(await me.json()).toMatchObject({ byok: true, plan: "byok" });
    });
  });
```

Add `import { MemoryBillingStore } from "../src/billing.js";` at the top of the test file.

- [ ] **Step 2: Run to see them fail**

Run: `npm test -w backend -- app`
Expected: FAIL: `createApp` ignores `billingStore`, `/billing/me` is 404.

- [ ] **Step 3: Wire `app.ts`**

```ts
import { requireCredits, billingSummary, SupabaseBillingStore, type BillingStore, type BillingContext } from "./billing.js";
import { SupabaseRest } from "./db.js";

export interface AppOptions {
  log?: LogSink | null;
  /** Billing store (tests pass MemoryBillingStore). Default: Supabase when SUPABASE_URL + SUPABASE_SERVICE_KEY are set, else none. */
  billingStore?: BillingStore;
}

export function createApp(options: AppOptions = {}) {
  const app = new Hono<{ Variables: { principal: Principal; billing: BillingContext } }>();
  ...
  // The billing store is resolved lazily from the request env (Workers bindings are per request).
  let resolvedStore: BillingStore | undefined | null = options.billingStore ?? null;
  const storeFor = (c: Context): BillingStore | undefined => {
    if (resolvedStore !== null) return resolvedStore;
    const env = getEnv(c);
    resolvedStore = env.SUPABASE_URL && env.SUPABASE_SERVICE_KEY ? new SupabaseBillingStore(new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY)) : undefined;
    if (!resolvedStore) console.warn("billing: no SUPABASE_SERVICE_KEY; requests are not metered");
    return resolvedStore;
  };
  const gate: MiddlewareHandler<{ Variables: { principal: Principal; billing: BillingContext } }> = (c, next) => requireCredits(storeFor(c))(c, next);
  app.use("/agent/realtime/session", gate);
  app.use("/agent/transcribe", gate);
  app.use("/v1/*", gate);
  app.use("/skills/create", gate);
  app.use("/chat", gate);
  app.use("/tts", gate);
  app.use("/billing/*", requireAuth);
  app.use("/billing/me", gate);
  app.get("/billing/me", (c) => c.json(billingSummary(c)));
```

Route handlers pass the store: `app.post("/chat", (c) => proxyAnthropic(c, storeFor(c)))`, etc. (`import type { Context, MiddlewareHandler } from "hono"`.)

Note `requireCredits` for `/billing/me` must not 402: in `requireCredits`, skip the two 402 checks when `c.req.path === "/billing/me"` (set `billing` and continue so the summary can show "exhausted"). Add that condition: `const reportOnly = c.req.path === "/billing/me";` and wrap both `return c.json(..., 402)` lines in `if (!reportOnly)`.

- [ ] **Step 4: Meter in `proxy.ts` and `skillsCreate.ts`**

Each function gains a `store: BillingStore | undefined` parameter (default `undefined` so old call sites compile).

- `proxyOpenAI(c, upstreamPath, store?)`: for metered users on `/chat/completions` with `stream: true`, set `body.stream_options = { include_usage: true }` so the final chunk carries usage. Return `meterResponse(c, upstream, "/v1" + upstreamPath, String(body.model ?? ""), store, () => CREDIT_COSTS.flatTokenFallback)` instead of `passthrough(upstream)`.
- `proxyAnthropic(c, store?)`: return `meterResponse(c, upstream, new URL(c.req.url).pathname, String(body.model ?? ""), store, () => CREDIT_COSTS.flatTokenFallback)`.
- `createRealtimeSession(c, store?)`: after a 2xx upstream, `chargeCredits(c, store, { route: "/agent/realtime/session", model: String(session.model), input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: CREDIT_COSTS.realtimeSession })`.
- `transcribeAudio(c, store?)`: `const audioSeconds = mime === "audio/wav" ? Math.max(0, bytes.length - 44) / 32000 : bytes.length / 16000;` (16 kHz mono PCM16 for WAV; a rough 16 kB/s for compressed audio). After a 2xx: `chargeCredits(c, store, { route: "/agent/transcribe", model: <model>, input_tokens: 0, output_tokens: 0, audio_seconds: audioSeconds, characters: 0, credits: creditsForAudioSeconds(audioSeconds) })`.
- `synthesizeSpeech(c, store?)`: after a 2xx: `chargeCredits(c, store, { route: "/tts", model: <model>, input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: text.length, credits: creditsForCharacters(text.length) })`.
- `createSkill(c, store?)`: after a 2xx: `chargeCredits(c, store, { route: "/skills/create", model, input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: CREDIT_COSTS.skillCreate })`.

- [ ] **Step 5: Run the whole suite**

Run: `npm test -w backend`
Expected: PASS.

- [ ] **Step 6: Configure the local backend and load the schema**

Append to `backend/.dev.vars` (values from SUPABASE.md; never commit):

```
SUPABASE_URL=https://db.flowsxr.com
SUPABASE_SERVICE_KEY=<SUPABASE_SECRET_KEY from SUPABASE.md>
```

Load the schema (command in Task 3 Step 2), rebuild and restart the service:

```bash
npm run build -w backend && launchctl kickstart -k gui/$(id -u)/org.openclicky.backend
curl -s -H "Authorization: Bearer $(python3 -c "import json;print(json.load(open('$HOME/.openclicky/shell.json'))['token'])")" http://localhost:8787/billing/me
```

Expected: `{"byok":false,"plan":"free","status":"free","used":0,"limit":200,"periodEnd":"..."}` (used may be higher once the app has talked).

- [ ] **Step 7: Commit**

```bash
git add backend/src/app.ts backend/src/proxy.ts backend/src/skillsCreate.ts backend/test/app.test.ts
git commit -m "feat(backend): credits gate and usage metering on every model route; GET /billing/me"
```

---

### Task 5: Stripe checkout, portal, and webhook (`stripe.ts`)

**Files:**
- Create: `backend/src/stripe.ts`
- Modify: `backend/src/app.ts`, `backend/src/env.ts`
- Test: `backend/test/stripe.test.ts`

**Interfaces:**
- Consumes: `BillingStore` (Task 3).
- Produces:
  ```ts
  export async function createCheckoutSession(c: Context, store: BillingStore): Promise<Response>;   // POST /billing/checkout { plan } → { url }
  export async function createPortalSession(c: Context, store: BillingStore): Promise<Response>;     // POST /billing/portal → { url }
  export async function handleStripeWebhook(c: Context, store: BillingStore): Promise<Response>;     // POST /billing/webhook (no auth; signature)
  export async function verifyStripeSignature(payload: string, header: string, secret: string, nowSeconds?: number): Promise<boolean>;
  ```
- Env: `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `STRIPE_SUCCESS_URL`, `STRIPE_CANCEL_URL`, `STRIPE_PORTAL_RETURN_URL`.

- [ ] **Step 1: Add env vars**

```ts
  /** Stripe (subscriptions). Checkout and portal need STRIPE_SECRET_KEY; the webhook needs STRIPE_WEBHOOK_SECRET. */
  STRIPE_SECRET_KEY?: string;
  STRIPE_WEBHOOK_SECRET?: string;
  STRIPE_SUCCESS_URL?: string;
  STRIPE_CANCEL_URL?: string;
  STRIPE_PORTAL_RETURN_URL?: string;
```

- [ ] **Step 2: Write the failing tests**

`backend/test/stripe.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { Hono } from "hono";
import { verifyStripeSignature, handleStripeWebhook, createCheckoutSession } from "../src/stripe.js";
import { MemoryBillingStore } from "../src/billing.js";
import type { Principal } from "../src/auth.js";

const secret = "whsec_test";
async function sign(payload: string, t: number) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${t}.${payload}`));
  return `t=${t},v1=${Array.from(new Uint8Array(sig)).map((b) => b.toString(16).padStart(2, "0")).join("")}`;
}

describe("verifyStripeSignature", () => {
  it("accepts a fresh, correctly signed payload and rejects a tampered or stale one", async () => {
    const now = 1_700_000_000;
    const header = await sign("{}", now);
    expect(await verifyStripeSignature("{}", header, secret, now + 60)).toBe(true);
    expect(await verifyStripeSignature("{ }", header, secret, now + 60)).toBe(false);
    expect(await verifyStripeSignature("{}", header, secret, now + 600)).toBe(false);
    expect(await verifyStripeSignature("{}", "garbage", secret, now)).toBe(false);
  });
});

describe("webhook", () => {
  it("activates the plan from checkout.session.completed and updates it on subscription events", async () => {
    const store = new MemoryBillingStore();
    const app = new Hono();
    app.post("/billing/webhook", (c) => handleStripeWebhook(c, store));
    const env = { STRIPE_WEBHOOK_SECRET: secret, STRIPE_SECRET_KEY: "sk_test" };
    const fetchSub = vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
      if (String(url).includes("/v1/subscriptions/sub_1")) {
        return new Response(JSON.stringify({ id: "sub_1", customer: "cus_1", status: "active", current_period_start: 1_700_000_000, current_period_end: 1_702_592_000, items: { data: [{ price: { id: "price_pro" } }] }, metadata: { user_id: "user-1" } }));
      }
      throw new Error("unexpected fetch " + url);
    });
    const now = 1_700_000_100;
    const completed = JSON.stringify({ type: "checkout.session.completed", data: { object: { client_reference_id: "user-1", customer: "cus_1", subscription: "sub_1" } } });
    let r = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": await sign(completed, now) }, body: completed }, env);
    expect(r.status).toBe(200);
    expect(store.subs.get("user-1")).toMatchObject({ plan_id: "pro", status: "active", stripe_customer_id: "cus_1", stripe_subscription_id: "sub_1" });

    const canceled = JSON.stringify({ type: "customer.subscription.deleted", data: { object: { id: "sub_1", customer: "cus_1", status: "canceled", current_period_start: 1_700_000_000, current_period_end: 1_702_592_000, items: { data: [{ price: { id: "price_pro" } }] }, metadata: { user_id: "user-1" } } } });
    r = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": await sign(canceled, now) }, body: canceled }, env);
    expect(r.status).toBe(200);
    expect(store.subs.get("user-1")?.status).toBe("canceled");

    const bad = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": "t=1,v1=00" }, body: completed }, env);
    expect(bad.status).toBe(400);
    fetchSub.mockRestore();
  });
});

describe("checkout", () => {
  it("creates a subscription checkout for the plan's price with the user as client_reference_id", async () => {
    const store = new MemoryBillingStore();
    const app = new Hono<{ Variables: { principal: Principal } }>();
    app.use("*", async (c, next) => { c.set("principal", { sub: "user-1", email: "dev@example.com", via: "session" }); await next(); });
    app.post("/billing/checkout", (c) => createCheckoutSession(c, store));
    let form = "";
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (_url, init) => { form = String(init?.body); return new Response(JSON.stringify({ url: "https://checkout.stripe.com/c/abc" })); });
    const r = await app.request("/billing/checkout", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ plan: "pro" }) }, { STRIPE_SECRET_KEY: "sk_test", STRIPE_SUCCESS_URL: "https://x/ok", STRIPE_CANCEL_URL: "https://x/no" });
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ url: "https://checkout.stripe.com/c/abc" });
    const params = new URLSearchParams(form);
    expect(params.get("mode")).toBe("subscription");
    expect(params.get("line_items[0][price]")).toBe("price_pro");
    expect(params.get("client_reference_id")).toBe("user-1");
    expect(params.get("subscription_data[metadata][user_id]")).toBe("user-1");
    fetchSpy.mockRestore();
  });
});
```

- [ ] **Step 3: Run to see them fail**

Run: `npm test -w backend -- stripe`
Expected: FAIL, cannot find module `../src/stripe.js`.

- [ ] **Step 4: Implement `stripe.ts`**

```ts
import type { Context } from "hono";
import { getEnv } from "./env.js";
import type { BillingStore, Subscription } from "./billing.js";
import type { Principal } from "./auth.js";

const STRIPE_API = "https://api.stripe.com";
const TOLERANCE_SECONDS = 300;

async function stripe(env: { STRIPE_SECRET_KEY?: string }, method: "GET" | "POST", path: string, form?: URLSearchParams): Promise<Response> {
  if (!env.STRIPE_SECRET_KEY) throw new Error("STRIPE_SECRET_KEY not configured");
  return fetch(STRIPE_API + path, {
    method,
    headers: { authorization: `Bearer ${env.STRIPE_SECRET_KEY}`, ...(form ? { "content-type": "application/x-www-form-urlencoded" } : {}) },
    body: form?.toString(),
  });
}

/** Stripe-Signature: `t=<unix>,v1=<hex hmac-sha256 of "<t>.<payload>">` (several v1 allowed). */
export async function verifyStripeSignature(payload: string, header: string, secret: string, nowSeconds = Math.floor(Date.now() / 1000)): Promise<boolean> {
  const parts = Object.fromEntries(header.split(",").map((kv) => kv.split("=") as [string, string]).filter((kv) => kv.length === 2));
  const t = Number(parts.t);
  const provided = header.split(",").filter((kv) => kv.startsWith("v1=")).map((kv) => kv.slice(3));
  if (!Number.isFinite(t) || provided.length === 0 || Math.abs(nowSeconds - t) > TOLERANCE_SECONDS) return false;
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${t}.${payload}`));
  const expected = Array.from(new Uint8Array(mac)).map((b) => b.toString(16).padStart(2, "0")).join("");
  return provided.some((sig) => sig.length === expected.length && timingSafeEqual(sig, expected));
}

function timingSafeEqual(a: string, b: string): boolean {
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

interface StripeSubscription {
  id: string; customer: string; status: string; current_period_start: number; current_period_end: number;
  items: { data: { price: { id: string } }[] }; metadata?: Record<string, string>;
}

async function subscriptionRow(store: BillingStore, sub: StripeSubscription, userId: string | undefined): Promise<Subscription | undefined> {
  const uid = userId ?? sub.metadata?.user_id;
  const priceId = sub.items?.data?.[0]?.price?.id;
  if (!uid || !priceId) return undefined;
  const plan = await store.planByPrice(priceId);
  if (!plan) return undefined;
  return {
    user_id: uid, plan_id: plan.id, status: sub.status,
    current_period_start: new Date(sub.current_period_start * 1000).toISOString(),
    current_period_end: new Date(sub.current_period_end * 1000).toISOString(),
    stripe_customer_id: sub.customer, stripe_subscription_id: sub.id,
  };
}

/** POST /billing/webhook — no bearer auth; the Stripe signature is the auth. */
export async function handleStripeWebhook(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  if (!env.STRIPE_WEBHOOK_SECRET) return c.json({ error: "STRIPE_WEBHOOK_SECRET not configured" }, 503);
  const payload = await c.req.text();
  const header = c.req.header("stripe-signature") ?? "";
  if (!(await verifyStripeSignature(payload, header, env.STRIPE_WEBHOOK_SECRET))) return c.json({ error: "bad signature" }, 400);
  const event = JSON.parse(payload) as { type: string; data: { object: Record<string, unknown> } };
  const object = event.data.object;
  if (event.type === "checkout.session.completed") {
    const userId = object.client_reference_id as string | undefined;
    const subId = object.subscription as string | undefined;
    if (userId && subId) {
      const res = await stripe(env, "GET", `/v1/subscriptions/${subId}`);
      if (res.ok) {
        const row = await subscriptionRow(store, (await res.json()) as StripeSubscription, userId);
        if (row) await store.upsertSubscription(row);
      }
    }
  } else if (event.type === "customer.subscription.updated" || event.type === "customer.subscription.deleted") {
    const row = await subscriptionRow(store, object as unknown as StripeSubscription, undefined);
    if (row) await store.upsertSubscription(row);
  }
  return c.json({ received: true });
}

/** POST /billing/checkout { plan } → { url }: a Stripe Checkout page for the plan's price. */
export async function createCheckoutSession(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const principal = c.get("principal") as Principal;
  const body = (await c.req.json().catch(() => ({}))) as { plan?: string };
  const plan = body.plan ? await store.plan(body.plan) : undefined;
  if (!plan?.stripe_price_id) return c.json({ error: "unknown plan or plan has no Stripe price" }, 400);
  const form = new URLSearchParams({
    mode: "subscription",
    "line_items[0][price]": plan.stripe_price_id,
    "line_items[0][quantity]": "1",
    client_reference_id: principal.sub,
    "subscription_data[metadata][user_id]": principal.sub,
    success_url: env.STRIPE_SUCCESS_URL ?? "https://openclicky.app/subscribed",
    cancel_url: env.STRIPE_CANCEL_URL ?? "https://openclicky.app/",
    ...(principal.email ? { customer_email: principal.email } : {}),
  });
  const existing = await store.subscription(principal.sub);
  if (existing?.stripe_customer_id) { form.delete("customer_email"); form.set("customer", existing.stripe_customer_id); }
  const res = await stripe(env, "POST", "/v1/checkout/sessions", form);
  if (!res.ok) return c.json({ error: `stripe ${res.status}: ${(await res.text()).slice(0, 300)}` }, 502);
  const { url } = (await res.json()) as { url: string };
  return c.json({ url });
}

/** POST /billing/portal → { url }: Stripe's customer portal (cancel, upgrade, invoices). */
export async function createPortalSession(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const principal = c.get("principal") as Principal;
  const existing = await store.subscription(principal.sub);
  if (!existing?.stripe_customer_id) return c.json({ error: "no subscription" }, 404);
  const form = new URLSearchParams({ customer: existing.stripe_customer_id, return_url: env.STRIPE_PORTAL_RETURN_URL ?? "https://openclicky.app/" });
  const res = await stripe(env, "POST", "/v1/billing_portal/sessions", form);
  if (!res.ok) return c.json({ error: `stripe ${res.status}` }, 502);
  const { url } = (await res.json()) as { url: string };
  return c.json({ url });
}
```

- [ ] **Step 5: Wire the routes in `app.ts`**

Before `app.use("/billing/*", requireAuth)` add the unauthenticated webhook:

```ts
  app.post("/billing/webhook", (c) => { const s = storeFor(c); return s ? handleStripeWebhook(c, s) : c.json({ error: "billing store not configured" }, 503); });
  app.use("/billing/*", async (c, next) => (c.req.path === "/billing/webhook" ? next() : requireAuth(c, next)));
  app.post("/billing/checkout", (c) => { const s = storeFor(c); return s ? createCheckoutSession(c, s) : c.json({ error: "billing store not configured" }, 503); });
  app.post("/billing/portal", (c) => { const s = storeFor(c); return s ? createPortalSession(c, s) : c.json({ error: "billing store not configured" }, 503); });
```

(Replace the earlier plain `app.use("/billing/*", requireAuth)` from Task 4 with this conditional form.)

- [ ] **Step 6: Run the suite**

Run: `npm test -w backend`
Expected: PASS.

- [ ] **Step 7: Stripe account setup (manual, the user does this)**

1. In the Stripe dashboard (test mode first) create two recurring prices: Starter and Pro. Copy their `price_...` ids into `oc_plans.stripe_price_id` via Studio.
2. Add to `.dev.vars`: `STRIPE_SECRET_KEY=sk_test_...`, `STRIPE_WEBHOOK_SECRET=whsec_...` (from `stripe listen --forward-to localhost:8787/billing/webhook` locally, or the dashboard endpoint once hosted), `STRIPE_SUCCESS_URL`, `STRIPE_CANCEL_URL`, `STRIPE_PORTAL_RETURN_URL`.
3. Enable the customer portal in Stripe settings.
4. Smoke: `curl -X POST -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' -d '{"plan":"starter"}' localhost:8787/billing/checkout` → open the URL, pay with `4242 4242 4242 4242`, then `GET /billing/me` shows `plan: starter, status: active`.

- [ ] **Step 8: Commit**

```bash
git add backend/src/stripe.ts backend/src/app.ts backend/src/env.ts backend/test/stripe.test.ts
git commit -m "feat(backend): Stripe checkout, customer portal, and subscription webhook"
```

---

### Task 6: CLI forwards the user's keys (ask, gate, transcribe, realtime, skills, Codex)

**Files:**
- Create: `agent/src/backendHeaders.ts`
- Modify: `agent/src/config.ts`, `agent/src/ask.ts:75`, `agent/src/gate.ts:46`, `agent/src/audio.ts:49`, `agent/src/realtime.ts:109`, `agent/src/cli.ts:476`, `agent/src/codex.ts` (where the Codex process env is built), `config/codex-config.toml`
- Test: `agent/test/backendHeaders.test.ts`, `agent/test/codexHome.test.ts`

**Interfaces:**
- Produces:
  ```ts
  // config.ts additions
  openaiApiKey?: string;      // env OPENCLICKY_OPENAI_KEY
  anthropicApiKey?: string;   // env OPENCLICKY_ANTHROPIC_KEY
  // backendHeaders.ts
  export function backendHeaders(cfg: Pick<AgentConfig, "token" | "openaiApiKey" | "anthropicApiKey">, extra?: Record<string, string>): Record<string, string>;
  ```

- [ ] **Step 1: Write the failing tests**

`agent/test/backendHeaders.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { backendHeaders } from "../src/backendHeaders.js";
import { resolveConfig } from "../src/config.js";

describe("backendHeaders", () => {
  it("adds the bearer token and, when present, the user's provider keys", () => {
    expect(backendHeaders({ token: "t" })).toEqual({ authorization: "Bearer t" });
    expect(backendHeaders({ token: "t", openaiApiKey: "sk-user", anthropicApiKey: "ak-user" }, { "content-type": "application/json" })).toEqual({
      "content-type": "application/json",
      authorization: "Bearer t",
      "x-openclicky-openai-key": "sk-user",
      "x-openclicky-anthropic-key": "ak-user",
    });
  });
  it("reads the keys from the environment", () => {
    const cfg = resolveConfig({}, { HOME: "/tmp", OPENCLICKY_OPENAI_KEY: "sk-env", OPENCLICKY_ANTHROPIC_KEY: "ak-env" } as NodeJS.ProcessEnv);
    expect(cfg.openaiApiKey).toBe("sk-env");
    expect(cfg.anthropicApiKey).toBe("ak-env");
  });
});
```

In `agent/test/codexHome.test.ts` add:

```ts
it("renders env_http_headers so Codex forwards the user's provider keys to the backend", () => {
  const template = fs.readFileSync(path.join(repoRoot(), "config", "codex-config.toml"), "utf8");
  const out = renderCodexConfig(template, { root: "/r", backendUrl: "http://b", workspace: "/w", userSkillsActive: "/s" });
  expect(out).toContain('env_http_headers = { "x-openclicky-openai-key" = "OPENCLICKY_OPENAI_KEY", "x-openclicky-anthropic-key" = "OPENCLICKY_ANTHROPIC_KEY" }');
});
```

(Use the file's existing imports for `fs`, `path`, `repoRoot`, `renderCodexConfig`.)

- [ ] **Step 2: Run to see them fail**

Run: `npm test -w agent`
Expected: FAIL on the two new tests.

- [ ] **Step 3: Implement**

`agent/src/config.ts`: add to `AgentConfig`:

```ts
  /** The user's own OpenAI key (bring your own key): sent to the backend per request, never stored there. */
  openaiApiKey?: string;
  /** Optional Anthropic key for the Claude lanes when bringing your own keys. */
  anthropicApiKey?: string;
```

and in `resolveConfig`:

```ts
    openaiApiKey: flags.openaiApiKey ?? env.OPENCLICKY_OPENAI_KEY ?? undefined,
    anthropicApiKey: flags.anthropicApiKey ?? env.OPENCLICKY_ANTHROPIC_KEY ?? undefined,
```

`agent/src/backendHeaders.ts`:

```ts
import type { AgentConfig } from "./config.js";

/** Headers every backend call carries: the bearer token plus the user's own provider keys when they brought them. */
export function backendHeaders(cfg: Pick<AgentConfig, "token" | "openaiApiKey" | "anthropicApiKey">, extra: Record<string, string> = {}): Record<string, string> {
  return {
    ...extra,
    authorization: `Bearer ${cfg.token ?? ""}`,
    ...(cfg.openaiApiKey ? { "x-openclicky-openai-key": cfg.openaiApiKey } : {}),
    ...(cfg.anthropicApiKey ? { "x-openclicky-anthropic-key": cfg.anthropicApiKey } : {}),
  };
}
```

Replace each `headers: { "content-type": "application/json", authorization: \`Bearer ${cfg.token}\` }` (ask.ts, gate.ts, audio.ts, realtime.ts, cli.ts skills create) with `headers: backendHeaders(cfg, { "content-type": "application/json" })` (ask.ts also keeps `accept: "text/event-stream"` in the extra map).

`config/codex-config.toml`, inside `[model_providers.openclicky]` after `env_key`:

```toml
# Bring your own key: when the user set OPENCLICKY_OPENAI_KEY / OPENCLICKY_ANTHROPIC_KEY, Codex sends them
# to the backend as headers and the backend uses them instead of its own (unset variables send nothing).
env_http_headers = { "x-openclicky-openai-key" = "OPENCLICKY_OPENAI_KEY", "x-openclicky-anthropic-key" = "OPENCLICKY_ANTHROPIC_KEY" }
```

`agent/src/codex.ts`: where the Codex child process env is built, add `...(cfg.openaiApiKey ? { OPENCLICKY_OPENAI_KEY: cfg.openaiApiKey } : {})` and the Anthropic equivalent (check the existing env construction and keep its shape).

- [ ] **Step 4: Run the agent suite and a headless check**

Run: `npm test -w agent` → PASS. Then `npm run build -w agent` and:

```bash
OPENCLICKY_OPENAI_KEY=sk-not-real node agent/dist/cli.js ask "say hi" 2>&1 | head -3
```

Expected: an upstream 401 from OpenAI reported by the backend (proves the header reached the vendor with the user's key rather than yours). Then without the env var the same command answers normally.

- [ ] **Step 5: Commit**

```bash
git add agent/src config/codex-config.toml agent/test
git commit -m "feat(agent): forward bring-your-own-key headers on every backend call, Codex included"
```

---

### Task 7: Mac app: keys in shell.json, headers, and the Account section

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyConfiguration.swift` (settings struct, `authorize`, `cliProcessEnvironment`)
- Create: `macos/OpenClicky/OpenClicky/BillingStatus.swift`
- Modify: `macos/OpenClicky/OpenClicky/NotchHUDPanels.swift` (insert one `section("ACCOUNT")` block in `NotchSettingsView`, after the BACKEND section)
- Test: `macos/OpenClicky/OpenClickyTests/OpenClickyConfigurationTests.swift`

**Interfaces:**
- Consumes: `GET /billing/me`, `POST /billing/checkout`, `POST /billing/portal` (Tasks 4-5); headers from Task 1.
- Produces:
  ```swift
  // OpenClickyShellSettings
  var openaiApiKey: String? = nil
  var anthropicApiKey: String? = nil
  // OpenClickyConfiguration
  static var usesOwnKeys: Bool
  static func providerKeyHeaders() -> [String: String]     // the two x-openclicky-* headers when set
  // BillingStatus.swift
  struct BillingSummary: Decodable { let byok: Bool; let plan: String; let status: String; let used: Int; let limit: Int; let periodEnd: String }
  @MainActor final class BillingStatusModel: ObservableObject { @Published var summary: BillingSummary?; @Published var errorText: String?; func refresh(); func openCheckout(plan: String); func openPortal() }
  struct NotchAccountSection: View
  ```

- [ ] **Step 1: Write the failing test**

`OpenClickyConfigurationTests.swift`:

```swift
import Foundation
import Testing
@testable import OpenClicky

struct OpenClickyConfigurationTests {
    @Test func providerKeyHeadersComeFromTheSettings() {
        var settings = OpenClickyShellSettings()
        settings.openaiApiKey = " sk-user "
        settings.anthropicApiKey = ""
        let headers = OpenClickyConfiguration.providerKeyHeaders(from: settings)
        #expect(headers == ["x-openclicky-openai-key": "sk-user"])
        #expect(OpenClickyConfiguration.usesOwnKeys(settings))
        #expect(!OpenClickyConfiguration.usesOwnKeys(OpenClickyShellSettings()))
    }

    @Test func cliEnvironmentCarriesTheUserKeysUnderOpenClickyNames() {
        var settings = OpenClickyShellSettings()
        settings.openaiApiKey = "sk-user"
        let environment = OpenClickyConfiguration.cliProcessEnvironment(from: settings)
        #expect(environment["OPENCLICKY_OPENAI_KEY"] == "sk-user")
        #expect(environment["OPENAI_API_KEY"] == nil)
    }
}
```

- [ ] **Step 2: Build the tests to see them fail**

Run (in `macos/OpenClicky`): `xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -derivedDataPath build/DerivedData build-for-testing CODE_SIGNING_ALLOWED=NO 2>&1 | grep -E "error:|TEST BUILD"`
Expected: compile errors for `providerKeyHeaders(from:)`, `usesOwnKeys`, `cliProcessEnvironment(from:)`.

- [ ] **Step 3: Implement in `OpenClickyConfiguration.swift`**

Add to `OpenClickyShellSettings`:

```swift
    /// Bring your own key: your OpenAI key (and optionally an Anthropic key for the Claude lanes).
    /// Sent to the backend with every request in `x-openclicky-*` headers; the backend uses them
    /// instead of its own and meters nothing. Leave empty to use OpenClicky's keys under your plan.
    var openaiApiKey: String? = nil
    var anthropicApiKey: String? = nil
```

Add to `OpenClickyConfiguration`:

```swift
    private static func cleaned(_ value: String?) -> String? {
        let trimmed = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    static func providerKeyHeaders(from settings: OpenClickyShellSettings = settings) -> [String: String] {
        var headers: [String: String] = [:]
        if let openaiApiKey = cleaned(settings.openaiApiKey) { headers["x-openclicky-openai-key"] = openaiApiKey }
        if let anthropicApiKey = cleaned(settings.anthropicApiKey) { headers["x-openclicky-anthropic-key"] = anthropicApiKey }
        return headers
    }

    static func usesOwnKeys(_ settings: OpenClickyShellSettings = settings) -> Bool {
        cleaned(settings.openaiApiKey) != nil
    }
    static var usesOwnKeys: Bool { usesOwnKeys(settings) }
```

Change `authorize` to add the key headers:

```swift
    /// Adds the bearer token every backend request needs, plus the user's own provider keys if any.
    static func authorize(_ request: inout URLRequest) {
        if let token {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        for (headerName, headerValue) in providerKeyHeaders() {
            request.setValue(headerValue, forHTTPHeaderField: headerName)
        }
    }
```

Refactor `cliProcessEnvironment` into `static func cliProcessEnvironment(from settings: OpenClickyShellSettings = settings) -> [String: String]` (keep the computed `static var cliProcessEnvironment: [String: String] { cliProcessEnvironment() }` so call sites compile) and add, before the `removeValue` lines:

```swift
        if let openaiApiKey = cleaned(settings.openaiApiKey) { environment["OPENCLICKY_OPENAI_KEY"] = openaiApiKey }
        if let anthropicApiKey = cleaned(settings.anthropicApiKey) { environment["OPENCLICKY_ANTHROPIC_KEY"] = anthropicApiKey }
```

Inside that function use `settings.token`/`backendUrl` derived from the passed settings (compute `token` and `backendBaseURL` locally from `settings` rather than the static ones so the test's settings are honoured).

Also in `RealtimeVoiceClient.openConnection` the secret request already calls `OpenClickyConfiguration.authorize(&secretRequest)`, so BYOK covers Realtime with no change to that file.

- [ ] **Step 4: Create `BillingStatus.swift`**

```swift
//
//  BillingStatus.swift
//  OpenClicky
//
//  The Settings "Account" section: whether this Mac uses its own provider keys, or which plan and
//  how many credits are left this period (GET /billing/me), with Subscribe / Manage buttons that
//  open Stripe Checkout / the customer portal in the browser.
//

import AppKit
import SwiftUI

struct BillingSummary: Decodable, Equatable {
    let byok: Bool
    let plan: String
    let status: String
    let used: Int
    let limit: Int
    let periodEnd: String
}

@MainActor
final class BillingStatusModel: ObservableObject {
    @Published var summary: BillingSummary?
    @Published var errorText: String?
    @Published var isBusy = false

    func refresh() {
        guard OpenClickyConfiguration.isConfigured else { errorText = "Sign in first"; return }
        Task {
            do {
                var request = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)/billing/me")!)
                OpenClickyConfiguration.authorize(&request)
                let (data, response) = try await URLSession.shared.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
                summary = try JSONDecoder().decode(BillingSummary.self, from: data)
                errorText = nil
            } catch {
                errorText = "Could not load your plan (\(error.localizedDescription))"
            }
        }
    }

    func openCheckout(plan: String) { openBillingPage(path: "/billing/checkout", body: ["plan": plan]) }
    func openPortal() { openBillingPage(path: "/billing/portal", body: [:]) }

    private func openBillingPage(path: String, body: [String: String]) {
        isBusy = true
        Task {
            defer { isBusy = false }
            do {
                var request = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)\(path)")!)
                request.httpMethod = "POST"
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = try JSONSerialization.data(withJSONObject: body)
                OpenClickyConfiguration.authorize(&request)
                let (data, _) = try await URLSession.shared.data(for: request)
                guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let urlString = json["url"] as? String, let url = URL(string: urlString) else {
                    throw URLError(.badServerResponse)
                }
                NSWorkspace.shared.open(url)
            } catch {
                errorText = "Could not open the billing page (\(error.localizedDescription))"
            }
        }
    }
}

/// Rows for the Settings tab. `row(icon,title,value)` and `action(icon,title,detail,action)` are
/// passed in so this section uses the same row styles as the rest of Settings.
struct NotchAccountSection<Row: View, ActionRow: View>: View {
    @StateObject private var model = BillingStatusModel()
    let row: (_ systemImage: String, _ title: String, _ value: String) -> Row
    let action: (_ systemImage: String, _ title: String, _ detail: String?, _ action: @escaping () -> Void) -> ActionRow

    var body: some View {
        Group {
            if OpenClickyConfiguration.usesOwnKeys {
                row("key.fill", "Keys", "your own (not metered)")
                action("doc.text", "Change keys", "openaiApiKey / anthropicApiKey in shell.json") { OpenClickyConfiguration.revealSettingsFile() }
            } else if let summary = model.summary {
                row("creditcard", "Plan", summary.plan.capitalized + (summary.status == "free" ? "" : " · \(summary.status)"))
                row("gauge", "Credits", "\(summary.used) / \(summary.limit) used · resets \(Self.shortDate(summary.periodEnd))")
                if summary.plan == "free" {
                    action("sparkles", "Subscribe", "Starter or Pro, billed monthly") { model.openCheckout(plan: "starter") }
                } else {
                    action("person.crop.circle", "Manage subscription", "Invoices, upgrade, cancel") { model.openPortal() }
                }
                action("key", "Use my own API key instead", "Add openaiApiKey to shell.json") { OpenClickyConfiguration.revealSettingsFile() }
            } else {
                row("creditcard", "Plan", model.errorText ?? "loading…")
            }
        }
        .onAppear { model.refresh() }
    }

    private static func shortDate(_ iso: String) -> String {
        guard let date = ISO8601DateFormatter().date(from: iso) else { return iso }
        return date.formatted(date: .abbreviated, time: .omitted)
    }
}
```

- [ ] **Step 5: Insert the section in `NotchSettingsView`**

In `NotchHUDPanels.swift`, right after the `section("BACKEND") { ... }` block:

```swift
                section("ACCOUNT") {
                    NotchAccountSection(row: { settingRow(systemImage: $0, title: $1, value: $2) },
                                        action: { actionRow(systemImage: $0, title: $1, detail: $2, action: $3) })
                }
```

Check the exact signatures of `settingRow`/`actionRow` in that file first (they are `private func settingRow(systemImage:title:value:)` and `actionRow(systemImage:title:detail:action:)`) and adjust the closures if the labels differ. If the other session has this file open, coordinate: make this one insertion and nothing else.

- [ ] **Step 6: Build, run the tests, install**

```bash
cd macos/OpenClicky
xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -derivedDataPath build/DerivedData build-for-testing CODE_SIGNING_ALLOWED=NO 2>&1 | grep -E "error:|TEST BUILD"
xcodebuild -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -derivedDataPath build/DerivedData test-without-building -only-testing:OpenClickyTests CODE_SIGNING_ALLOWED=NO 2>&1 | grep -E " failed|TEST EXECUTE"
cd ../.. && npm run release:mac
```

Expected: TEST BUILD SUCCEEDED, TEST EXECUTE SUCCEEDED, app installed. Open Settings in the notch: the ACCOUNT section shows "Plan Free · Credits N / 200 used". Add `"openaiApiKey": "sk-..."` to `~/.openclicky/shell.json`, relaunch: it shows "Keys: your own (not metered)", and a talk turn works while `backend.log` shows no new `oc_usage_events` row (check Studio).

- [ ] **Step 7: Commit**

```bash
git add macos/OpenClicky/OpenClicky/OpenClickyConfiguration.swift macos/OpenClicky/OpenClicky/BillingStatus.swift macos/OpenClicky/OpenClicky/NotchHUDPanels.swift macos/OpenClicky/OpenClickyTests/OpenClickyConfigurationTests.swift
git commit -m "feat(mac): bring-your-own-key settings and the Account section (plan, credits, subscribe)"
```

---

### Task 8: Docs

**Files:**
- Modify: `README.md` (backend section: BYOK headers, billing env vars, `/billing/*` routes, schema load), `macos/OpenClicky/AGENTS.md` (route table + key files rows for `BillingStatus.swift`; note that `OpenClickyConfiguration.authorize` adds key headers), `backend/wrangler.toml` comment listing the new secrets.

- [ ] **Step 1: README additions**

Under the backend routes table add:

| Route | Auth | Purpose |
|---|---|---|
| `GET /billing/me` | token | `{ byok, plan, status, used, limit, periodEnd }` |
| `POST /billing/checkout` | token | `{ plan }` → `{ url }` Stripe Checkout |
| `POST /billing/portal` | token | `{ url }` Stripe customer portal |
| `POST /billing/webhook` | Stripe signature | subscription lifecycle |

And a "Bring your own key" paragraph: set `openaiApiKey` (and optionally `anthropicApiKey`) in `~/.openclicky/shell.json` or `OPENCLICKY_OPENAI_KEY` for the CLI; requests then carry `x-openclicky-openai-key` and are not metered. Plus the env list: `SUPABASE_SERVICE_KEY`, `FREE_MONTHLY_CREDITS`, `STRIPE_*`, `BYOK_*`.

- [ ] **Step 2: Commit**

```bash
git add README.md macos/OpenClicky/AGENTS.md backend/wrangler.toml
git commit -m "docs: bring-your-own-key, credits, and Stripe billing"
```

---

## Self-review notes

- Spec coverage: BYOK per request (Tasks 1, 2, 6, 7); credits per plan with enforcement and metering (Tasks 3, 4); Stripe (Task 5); both modes selectable by the user (Task 7's Account section + shell.json). Hosting the backend publicly is Phase 3 and intentionally not in this plan.
- Realtime metering is per session mint (30 credits), not per token, because the Realtime socket bypasses the backend; a later task can add `POST /agent/usage` fed from the app's `response.done` events.
- `requireCredits` on `/billing/me` is report-only (Task 4 Step 3), so an exhausted user can still see their status.
- Type names used across tasks: `BillingStore`, `MemoryBillingStore`, `SupabaseBillingStore`, `BillingContext`, `UsageEvent`, `Subscription`, `Plan`, `chargeCredits`, `meterResponse`, `parseUsage`, `resolveProviderKeys`, `ProviderKeys`, `backendHeaders`, `providerKeyHeaders(from:)`, `usesOwnKeys(_:)`, `cliProcessEnvironment(from:)`, `BillingSummary`, `BillingStatusModel`, `NotchAccountSection`.
