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
