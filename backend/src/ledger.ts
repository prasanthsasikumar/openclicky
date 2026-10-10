import type { Env } from "./env.js";
import type { SupabaseRest } from "./db.js";

/**
 * Grant spending, reserved before a request and settled from its real usage, so overlapping
 * requests can never spend past a limit. All money is integer micro-dollars; months and days are UTC.
 */
export type LimitError = "personal_limit" | "daily_limit" | "monthly_budget" | "blocked" | "not_on_plan" | "confirm_email";
export type ReserveResult = { ok: true; reservationId: string } | { ok: false; error: LimitError; resetsAt: string | null };
export interface SpendEvent { route: string; model?: string; inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number; characters: number }
export interface SpendSummary {
  spentMonthMicro: number; spentTodayMicro: number; monthlyLimitMicro: number; dailyLimitMicro: number;
  globalSpentMicro: number; globalLimitMicro: number; ttsCharsMonth: number; ttsCharsLimit: number;
  monthEnd: string; dayEnd: string; blocked: boolean;
  /** Whether the user has an OpenClicky account row (grant spending needs one). */
  onPlan: boolean;
  /** The email is confirmed (Supabase no longer calls the user anonymous). */
  confirmed: boolean;
  /** Spend before confirmation by every account on this Mac, and its cap. */
  guestSpentMicro: number; guestLimitMicro: number;
  email: string | null;
}
export interface Limits { monthlyMicro: number; dailyMicro: number; globalMonthlyMicro: number; ttsCharsMonthly: number; guestTotalMicro: number; guestDays: number; guestTtsChars: number }
export interface SpendLedger {
  reserve(userId: string, estimateMicro: number, limits: Limits, now?: Date): Promise<ReserveResult>;
  /** Records the real cost. The usage is recorded for `userId` even when the hold has already been swept. */
  settle(reservationId: string, userId: string, actualMicro: number, event: SpendEvent, now?: Date): Promise<void>;
  reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number, now?: Date): Promise<ReserveResult>;
  summary(userId: string, limits: Limits, now?: Date): Promise<SpendSummary>;
}

const usd = (value: string | undefined, fallback: number) => Math.round((Number.isFinite(Number(value)) && value ? Number(value) : fallback) * 1_000_000);
const integer = (value: string | undefined, fallback: number) => (Number.isFinite(Number(value)) && value ? Number(value) : fallback);
export function limitsFromEnv(env: Env): Limits {
  return {
    monthlyMicro: usd(env.ACCOUNT_MONTHLY_USD, 10),
    dailyMicro: usd(env.ACCOUNT_DAILY_USD, 2),
    globalMonthlyMicro: usd(env.GLOBAL_MONTHLY_BUDGET_USD, 1000),
    ttsCharsMonthly: integer(env.ACCOUNT_MONTHLY_TTS_CHARS, 20_000),
    guestTotalMicro: usd(env.GUEST_TOTAL_USD, 1),
    guestDays: integer(env.GUEST_DAYS, 14),
    guestTtsChars: integer(env.GUEST_TTS_CHARS, 2_000),
  };
}

export const HOLD_TTL_MS = 10 * 60_000;
export const monthStart = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
export const monthEnd = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1));
export const dayStart = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
export const dayEnd = (now: Date) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1));

type Entry = { id: string; userId: string; at: number; micro: number; chars: number; settled: boolean };
type Account = {
  monthlyMicro?: number; dailyMicro?: number; blocked?: boolean;
  /** Absent means confirmed: accounts from before email-first sign-up. */
  confirmed?: boolean; confirmedAt?: number; deviceHash?: string; createdAt?: number; replaced?: boolean; email?: string;
};

/** In-memory ledger for tests and unmetered dev backends. JS runs reserve's check-and-insert without awaiting in between, so it is atomic. */
export class MemorySpendLedger implements SpendLedger {
  private entries: Entry[] = [];
  private accounts = new Map<string, Account>();
  private nextId = 1;
  private requireAccountRow: boolean;

  constructor(options?: { requireAccountRow?: boolean }) {
    this.requireAccountRow = options?.requireAccountRow ?? false;
  }

  setAccount(userId: string, account: Account) { this.accounts.set(userId, { ...this.accounts.get(userId), ...account }); }

  confirm(userId: string, at: Date) { this.setAccount(userId, { confirmed: true, confirmedAt: at.getTime() }); }

  private isConfirmed(userId: string) { return this.accounts.get(userId)?.confirmed ?? true; }

  /** Spend (held and settled) and characters before confirmation, by every account sharing this one's Mac. */
  private guestUsage(userId: string) {
    const me = this.accounts.get(userId);
    const peers = [...this.accounts.entries()].filter(([id, a]) => id === userId || (me?.deviceHash && a.deviceHash === me.deviceHash));
    const counts = (e: Entry) => peers.some(([id, a]) => id === e.userId && (!(a.confirmed ?? true) || (a.confirmedAt !== undefined && e.at < a.confirmedAt)));
    return { micro: this.sum(counts, "micro"), chars: this.sum(counts, "chars") };
  }

  /** A guest that was replaced by a newer one on its Mac, or left unconfirmed too long, may not spend. */
  private guestRefusal(userId: string, now: Date, limits: Limits): boolean {
    if (this.isConfirmed(userId)) return false;
    const a = this.accounts.get(userId)!;
    if (a.replaced) return true;
    return a.createdAt !== undefined && now.getTime() - a.createdAt > limits.guestDays * 86_400_000;
  }

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
    if (this.requireAccountRow && !this.accounts.has(userId)) return { ok: false, error: "not_on_plan", resetsAt: monthEnd(now).toISOString() };
    const mine = this.personal(userId, limits);
    if (mine.blocked) return { ok: false, error: "blocked", resetsAt: monthEnd(now).toISOString() };
    if (this.guestRefusal(userId, now, limits) || (!this.isConfirmed(userId) && this.guestUsage(userId).micro + estimateMicro > limits.guestTotalMicro)) {
      return { ok: false, error: "confirm_email", resetsAt: null };
    }
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

  async settle(reservationId: string, userId: string, actualMicro: number, event: SpendEvent, now = new Date()): Promise<void> {
    const entry = this.entries.find((e) => e.id === reservationId);
    if (!entry) { // swept: a reply that slow is still billed
      this.entries.push({ id: reservationId, userId, at: now.getTime(), micro: actualMicro, chars: event.characters, settled: true });
      return;
    }
    entry.micro = actualMicro;
    entry.chars = event.characters;
    entry.settled = true;
  }

  async reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number, now = new Date()): Promise<ReserveResult> {
    this.sweep(now);
    if (this.requireAccountRow && !this.accounts.has(userId)) return { ok: false, error: "not_on_plan", resetsAt: monthEnd(now).toISOString() };
    if (this.personal(userId, limits).blocked) return { ok: false, error: "blocked", resetsAt: monthEnd(now).toISOString() };
    if (this.guestRefusal(userId, now, limits) || (!this.isConfirmed(userId) && this.guestUsage(userId).chars + characters > limits.guestTtsChars)) {
      return { ok: false, error: "confirm_email", resetsAt: null };
    }
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
      onPlan: !this.requireAccountRow || this.accounts.has(userId),
      confirmed: this.isConfirmed(userId),
      guestSpentMicro: this.guestUsage(userId).micro, guestLimitMicro: limits.guestTotalMicro,
      email: this.accounts.get(userId)?.email ?? null,
    };
  }
}

/** Production ledger: the same rules, enforced inside Postgres functions (schema.sql) so concurrent containers agree. */
export class SupabaseSpendLedger implements SpendLedger {
  constructor(private readonly db: SupabaseRest) {}

  reserve(userId: string, estimateMicro: number, limits: Limits): Promise<ReserveResult> {
    return this.db.rpc<ReserveResult>("oc_reserve", {
      p_user: userId, p_estimate: estimateMicro, p_monthly: limits.monthlyMicro, p_daily: limits.dailyMicro, p_global: limits.globalMonthlyMicro,
      p_guest_total: limits.guestTotalMicro, p_guest_days: limits.guestDays,
    });
  }
  async settle(reservationId: string, userId: string, actualMicro: number, e: SpendEvent): Promise<void> {
    await this.db.rpc("oc_settle", {
      p_reservation: reservationId, p_user: userId, p_actual: actualMicro, p_route: e.route, p_model: e.model ?? null,
      p_input: e.inputTokens, p_output: e.outputTokens, p_cache_write: e.cacheWriteTokens, p_cache_read: e.cacheReadTokens, p_chars: e.characters,
    });
  }
  reserveCharacters(userId: string, characters: number, limits: Limits, globalCharsRemaining: number): Promise<ReserveResult> {
    return this.db.rpc<ReserveResult>("oc_reserve_chars", {
      p_user: userId, p_chars: characters, p_limit: limits.ttsCharsMonthly, p_global_remaining: globalCharsRemaining,
      p_guest_chars: limits.guestTtsChars, p_guest_days: limits.guestDays,
    });
  }
  summary(userId: string, limits: Limits): Promise<SpendSummary> {
    return this.db.rpc<SpendSummary>("oc_spend_summary", {
      p_user: userId, p_monthly: limits.monthlyMicro, p_daily: limits.dailyMicro, p_global: limits.globalMonthlyMicro, p_tts: limits.ttsCharsMonthly,
      p_guest_total: limits.guestTotalMicro,
    });
  }
}
