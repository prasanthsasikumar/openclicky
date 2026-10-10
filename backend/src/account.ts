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

/** What a grant route answers when the ledger itself cannot be reached: the app says the free service is having trouble. */
export const ledgerUnavailable = (c: Context) => c.json({ error: "unavailable" }, 503);

/** Reserve before forwarding; a refusal becomes the 402 the app understands. */
export async function reserveOr402(c: Context, ledger: SpendLedger, estimateMicro: number): Promise<{ reservationId: string } | Response> {
  const account = c.get("account" as never) as AccountContext;
  // A zero, negative or NaN hold would let a request through that the ledger never counted.
  if (!Number.isFinite(estimateMicro) || estimateMicro <= 0) return c.json({ error: "request could not be priced" }, 400);
  let result: Awaited<ReturnType<SpendLedger["reserve"]>>;
  try { result = await ledger.reserve(account.userId, estimateMicro, account.limits); }
  catch (e) { console.error(`ledger reserve failed: ${(e as Error).message}`); return ledgerUnavailable(c); }
  if (result.ok) return { reservationId: result.reservationId };
  return c.json(result.resetsAt ? { error: result.error, resets_at: result.resetsAt } : { error: result.error }, 402);
}

/** grant: metered on the backend's grant; byok: the request carries its own key; unmetered: a backend with no grant mode. */
export type Plan = "grant" | "byok" | "unmetered";

export type AccountSummaryJson = {
  plan: Plan; onPlan: boolean; byok: boolean; spentMonthUsd: number; monthlyLimitUsd: number; spentTodayUsd: number; dailyLimitUsd: number;
  ttsCharsMonth: number; ttsCharsLimit: number; monthEnd: string; dayEnd: string; budgetExhausted: boolean; blocked: boolean;
  confirmed: boolean; guestSpentUsd: number; guestLimitUsd: number; email: string | null;
};

/** "prasanth@flowsxr.com" → "p•••@flowsxr.com": enough for the person to recognise, no more. */
export function maskEmail(email: string | null): string | null {
  if (!email) return null;
  const at = email.indexOf("@");
  return at < 1 ? null : `${email[0]}•••${email.slice(at)}`;
}

const dollars = (micro: number) => Math.round(micro / 10_000) / 100;

export async function accountSummary(c: Context, ledger: SpendLedger): Promise<AccountSummaryJson> {
  const account = c.get("account" as never) as AccountContext;
  const s = await ledger.summary(account.userId, account.limits);
  return {
    plan: account.byok ? "byok" : "grant",
    // An older schema has no onPlan: count the user as on the plan rather than shut them out.
    onPlan: account.byok || (s.onPlan !== false && !s.blocked),
    byok: account.byok,
    spentMonthUsd: dollars(s.spentMonthMicro), monthlyLimitUsd: dollars(s.monthlyLimitMicro),
    spentTodayUsd: dollars(s.spentTodayMicro), dailyLimitUsd: dollars(s.dailyLimitMicro),
    ttsCharsMonth: s.ttsCharsMonth, ttsCharsLimit: s.ttsCharsLimit,
    monthEnd: s.monthEnd, dayEnd: s.dayEnd,
    budgetExhausted: s.globalSpentMicro >= s.globalLimitMicro, blocked: s.blocked,
    // Older schemas send no confirmation: count the account as confirmed rather than shut it out.
    confirmed: account.byok || s.confirmed !== false,
    guestSpentUsd: dollars(s.guestSpentMicro ?? 0), guestLimitUsd: dollars(s.guestLimitMicro ?? 0),
    email: maskEmail(s.email ?? null),
  };
}
