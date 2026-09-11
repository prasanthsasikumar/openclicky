import type { Context, MiddlewareHandler } from "hono";
import type { Principal } from "./auth.js";
import { getEnv } from "./env.js";
import { OPENAI_KEY_HEADER } from "./keys.js";
import type { SupabaseRest } from "./db.js";

/**
 * Billing for users on OpenClicky's keys: a plan grants monthly credits, every metered route
 * records what it cost, and the gate refuses once the period's credits are spent. Requests that
 * bring their own key (keys.ts) never touch any of this.
 */

export type PlanId = string;
export interface Plan {
  id: PlanId;
  name: string;
  monthly_credits: number;
  stripe_price_id: string | null;
}
export interface Subscription {
  user_id: string;
  plan_id: PlanId;
  /** Stripe's status: active | trialing | past_due | canceled | unpaid (free-tier users have no row). */
  status: string;
  current_period_start: string;
  current_period_end: string;
  stripe_customer_id: string | null;
  stripe_subscription_id: string | null;
  /** Invite-only accounts: a per-user allowance that replaces the plan's monthly credits. */
  monthly_credits_override?: number | null;
}
export interface UsageEvent {
  user_id: string;
  route: string;
  model?: string;
  input_tokens: number;
  output_tokens: number;
  audio_seconds: number;
  characters: number;
  credits: number;
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

/** 1 credit per 1k input tokens plus 4 per 1k output tokens, at least 1. */
export const creditsForTokens = (inputTokens: number, outputTokens: number) => Math.max(1, Math.ceil((inputTokens + 4 * outputTokens) / 1000));
/** 1 credit per started 15 s of audio. */
export const creditsForAudioSeconds = (seconds: number) => Math.max(1, Math.ceil(seconds / 15));
/** 1 credit per 500 characters spoken. */
export const creditsForCharacters = (chars: number) => Math.max(1, Math.ceil(chars / 500));

export class MemoryBillingStore implements BillingStore {
  plans: Plan[] = [
    { id: "free", name: "Free", monthly_credits: DEFAULT_FREE_MONTHLY_CREDITS, stripe_price_id: null },
    { id: "invite", name: "Invite", monthly_credits: 1000, stripe_price_id: null },
    { id: "starter", name: "Starter", monthly_credits: 3000, stripe_price_id: "price_starter" },
    { id: "pro", name: "Pro", monthly_credits: 12000, stripe_price_id: "price_pro" },
  ];
  subs = new Map<string, Subscription>();
  events: UsageEvent[] = [];
  async plan(id: PlanId) {
    return this.plans.find((p) => p.id === id);
  }
  async planByPrice(priceId: string) {
    return this.plans.find((p) => p.stripe_price_id === priceId);
  }
  async subscription(userId: string) {
    return this.subs.get(userId);
  }
  async upsertSubscription(sub: Subscription) {
    this.subs.set(sub.user_id, sub);
  }
  async creditsUsedSince(userId: string, _sinceIso: string) {
    return this.events.filter((e) => e.user_id === userId).reduce((n, e) => n + e.credits, 0);
  }
  async recordUsage(event: UsageEvent) {
    this.events.push(event);
  }
}

export class SupabaseBillingStore implements BillingStore {
  constructor(private readonly db: SupabaseRest) {}
  async plan(id: PlanId) {
    return (await this.db.select<Plan>("oc_plans", `id=eq.${encodeURIComponent(id)}&select=*`))[0];
  }
  async planByPrice(priceId: string) {
    return (await this.db.select<Plan>("oc_plans", `stripe_price_id=eq.${encodeURIComponent(priceId)}&select=*`))[0];
  }
  async subscription(userId: string) {
    return (await this.db.select<Subscription>("oc_subscriptions", `user_id=eq.${encodeURIComponent(userId)}&select=*`))[0];
  }
  async upsertSubscription(sub: Subscription) {
    await this.db.upsert("oc_subscriptions", { ...sub, updated_at: new Date().toISOString() }, "user_id");
  }
  async creditsUsedSince(userId: string, sinceIso: string) {
    const rows = await this.db.select<{ credits: number }>(
      "oc_usage_events",
      `user_id=eq.${encodeURIComponent(userId)}&ts=gte.${encodeURIComponent(sinceIso)}&select=credits`,
    );
    return rows.reduce((n, r) => n + Number(r.credits), 0);
  }
  async recordUsage(event: UsageEvent) {
    await this.db.insert("oc_usage_events", { ...event });
  }
}

export interface BillingContext {
  byok: boolean;
  userId: string;
  plan?: Plan;
  /** Subscription status, or "free" (no subscription), "byok", "unmetered" (no store configured). */
  status: string;
  periodStart: string;
  periodEnd: string;
  used: number;
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
 * `/billing/me` is report-only: an exhausted user can still see their status.
 */
export function requireCredits(store: BillingStore | undefined): MiddlewareHandler<{ Variables: { principal: Principal; billing: BillingContext } }> {
  return async (c, next) => {
    const byok = Boolean(c.req.header(OPENAI_KEY_HEADER)?.trim());
    const userId = c.get("principal")?.sub ?? "";
    if (!store || byok || !userId) {
      const period = calendarPeriod();
      c.set("billing", { byok, userId, status: byok ? "byok" : "unmetered", periodStart: period.start, periodEnd: period.end, used: 0 });
      await next();
      return;
    }
    const reportOnly = c.req.path === "/billing/me";
    const sub = await store.subscription(userId);
    const planId = sub?.plan_id ?? FREE_PLAN_ID;
    // FREE_MONTHLY_CREDITS=0 makes a backend invite-only: signed-in users without an allowance get nothing.
    const freeCredits = Number(getEnv(c).FREE_MONTHLY_CREDITS ?? DEFAULT_FREE_MONTHLY_CREDITS);
    const storedPlan = await store.plan(planId);
    const basePlan: Plan =
      planId === FREE_PLAN_ID
        ? { id: FREE_PLAN_ID, name: "Free", monthly_credits: Number.isFinite(freeCredits) ? freeCredits : DEFAULT_FREE_MONTHLY_CREDITS, stripe_price_id: null }
        : (storedPlan ?? { id: planId, name: planId, monthly_credits: 0, stripe_price_id: null });
    // An invitee's row carries its own allowance; the plan row is just the label.
    const plan: Plan = sub?.monthly_credits_override != null ? { ...basePlan, monthly_credits: sub.monthly_credits_override } : basePlan;
    const status = sub?.status ?? "free";
    if (sub && plan.id !== FREE_PLAN_ID && !ACTIVE_STATUSES.has(status) && !reportOnly) {
      return c.json({ error: "subscription_inactive", plan: plan.id, status }, 402);
    }
    // Stripe-managed subscriptions follow Stripe's billing period; invites and the free tier reset on calendar months.
    const isStripeManaged = Boolean(sub?.stripe_subscription_id);
    const period = sub && isStripeManaged && ACTIVE_STATUSES.has(status) ? { start: sub.current_period_start, end: sub.current_period_end } : calendarPeriod();
    const used = await store.creditsUsedSince(userId, period.start);
    if (used >= plan.monthly_credits && !reportOnly) {
      return c.json({ error: "credits_exhausted", plan: plan.id, used, limit: plan.monthly_credits, resets_at: period.end }, 402);
    }
    c.set("billing", { byok: false, userId, plan, status, periodStart: period.start, periodEnd: period.end, used });
    await next();
  };
}

/** Token usage from a JSON or SSE body: OpenAI chat (`prompt_/completion_tokens`), Responses and Anthropic (`input_/output_tokens`). */
export function parseUsage(text: string): { inputTokens: number; outputTokens: number } | undefined {
  let input = 0;
  let output = 0;
  let found = false;
  for (const m of text.matchAll(/"usage"\s*:\s*(\{[^{}]*\})/g)) {
    try {
      const u = JSON.parse(m[1]) as Record<string, number>;
      const i = u.input_tokens ?? u.prompt_tokens;
      const o = u.output_tokens ?? u.completion_tokens;
      if (typeof i === "number") {
        input = Math.max(input, i);
        found = true;
      }
      if (typeof o === "number") {
        output = Math.max(output, o);
        found = true;
      }
    } catch {
      // Not a flat usage object (nested detail objects); keep scanning.
    }
  }
  return found ? { inputTokens: input, outputTokens: output } : undefined;
}

/** Record a usage row without holding the response (Workers keep the promise alive via waitUntil). */
export function chargeCredits(c: Context, store: BillingStore | undefined, event: Omit<UsageEvent, "user_id">): void {
  const billing = c.get("billing") as BillingContext | undefined;
  if (!store || !billing || billing.byok || !billing.userId) return;
  const promise = store.recordUsage({ user_id: billing.userId, ...event }).catch((e) => console.error(`usage not recorded: ${(e as Error).message}`));
  try {
    // Hono's getter throws where there is no execution context (Node); the promise then runs on its own.
    c.executionCtx.waitUntil(promise);
  } catch {
    // Node: nothing to hand the promise to.
  }
}

const HOP_BY_HOP = new Set(["content-length", "connection", "keep-alive", "transfer-encoding", "content-encoding"]);

/**
 * Pass the upstream body through to the client and, once it has all gone by, charge credits from
 * the usage it reported (or `fallback()` credits when it reported none). Streams stay streams.
 */
export function meterResponse(
  c: Context,
  upstream: Response,
  route: string,
  model: string | undefined,
  store: BillingStore | undefined,
  fallback: () => number,
): Response {
  const billing = c.get("billing") as BillingContext | undefined;
  const headers = new Headers();
  upstream.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase())) headers.set(k, v);
  });
  if (!store || !billing || billing.byok || !upstream.ok || !upstream.body) {
    return new Response(upstream.body, { status: upstream.status, headers });
  }
  // Usage sits at the END of the stream, so a bounded *trailing* window is kept — an earlier
  // version kept the first 512 kB, which meant any reply longer than that lost its usage block and
  // was billed the flat fallback instead of what it actually cost.
  const USAGE_WINDOW_BYTES = 512_000;
  let collected = "";
  let charged = false;
  const decoder = new TextDecoder();
  const meter = () => {
    // A cancelled stream still consumed tokens upstream: charge once, whichever way the stream ends.
    if (charged) return;
    charged = true;
    const usage = parseUsage(collected);
    const credits = usage ? creditsForTokens(usage.inputTokens, usage.outputTokens) : fallback();
    chargeCredits(c, store, {
      route,
      model,
      input_tokens: usage?.inputTokens ?? 0,
      output_tokens: usage?.outputTokens ?? 0,
      audio_seconds: 0,
      characters: 0,
      credits,
    });
  };
  // Read through a ReadableStream rather than a TransformStream: its `cancel` hook is the one the
  // runtime calls when the client goes away (closed the connection, pressed stop), and that path
  // has to charge too — the tokens were spent upstream whether or not anyone read the answer.
  const reader = upstream.body.getReader();
  const metered = new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const { done, value } = await reader.read();
        if (done) {
          meter();
          controller.close();
          return;
        }
        collected += decoder.decode(value, { stream: true });
        if (collected.length > USAGE_WINDOW_BYTES) collected = collected.slice(-USAGE_WINDOW_BYTES);
        controller.enqueue(value);
      } catch (e) {
        meter();
        controller.error(e);
      }
    },
    cancel(reason) {
      meter();
      return reader.cancel(reason);
    },
  });
  return new Response(metered, { status: upstream.status, headers });
}

/** What the app shows in Settings. */
export function billingSummary(c: Context): { byok: boolean; plan: string; status: string; used: number; limit: number; periodEnd: string } {
  const b = c.get("billing") as BillingContext;
  return {
    byok: b.byok,
    plan: b.plan?.id ?? (b.byok ? "byok" : "unmetered"),
    status: b.status,
    used: b.used,
    limit: b.plan?.monthly_credits ?? 0,
    periodEnd: b.periodEnd,
  };
}
