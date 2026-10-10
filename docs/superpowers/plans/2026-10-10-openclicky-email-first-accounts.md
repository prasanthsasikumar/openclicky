# Email-first accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Setup asks only for an email; the app gets a working grant account at once (a Supabase anonymous user with the email attached), capped at $1 per Mac until the email is confirmed; sign-in elsewhere is email + 6-digit code; passwords are gone.

**Architecture:** The backend fronts all auth (`/auth/start`, `/auth/code`, `/auth/resend`) through a small GoTrue client, so it can enforce the caps. Confirmation is read inside Postgres from `auth.users.is_anonymous` (authoritative, no token refresh needed) and stamped once into `oc_accounts.confirmed_at`; pre-confirmation spend is pooled per `device_hash`. The Mac app sends a salted SHA-256 of its IOPlatformUUID and replaces its password forms with one email form plus a code step.

**Tech Stack:** Hono + TypeScript + vitest (backend), PostgREST/GoTrue on Supabase Cloud, SwiftUI + Swift Testing (macOS app).

**Spec:** `docs/superpowers/specs/2026-10-10-openclicky-email-first-accounts-design.md` (amends `2026-10-08-openclicky-accounts-design.md`).

## Global Constraints

- Guest (unconfirmed) cap: **$1.00 total per Mac, lifetime** — `GUEST_TOTAL_USD`, default `1`.
- Guest lifetime **14 days** — `GUEST_DAYS`, default `14`.
- Guest spoken characters: **2,000 per Mac before confirming** — `GUEST_TTS_CHARS`, default `2000`.
- Confirmed: `$2/day`, `$10/month`, `$1,000` global, `20,000` TTS chars — unchanged.
- Confirmed accounts cap `100` (`MAX_ACCOUNTS` / `oc_settings.max_accounts`) counts confirmed only; live guests cap `300` (`oc_settings.max_guests`).
- Confirmed accounts per Mac: **2** — `MAX_ACCOUNTS_PER_DEVICE`, default `2`.
- Device hash: `SHA-256("openclicky-device-v1:" + IOPlatformUUID)`, lowercase hex, exactly 64 chars `[0-9a-f]`.
- Per-IP limits: `/auth/start` 5/hour, `/auth/code` 10/hour; `/auth/resend` 3/hour per user.
- Error codes the app relies on: `400 bad_email | bad_request | bad_code`, `401 bad_code`, `402 accounts_full | device_limit | confirm_email`, `429 slow_down`, `502 auth_unavailable`.
- No GoTrue error text, email codes, or tokens are ever logged or returned to the client.
- No passwords anywhere: `/auth/signup`, `/auth/reset`, `ACCOUNT_RESET_REDIRECT_URL`, `resetRedirectUrl`, password fields and "forgot password" are removed.
- UI copy is lower-case, plain words, matching the existing Paper style.
- Never deploy without `GRANT_ACCOUNTS=true`. Deploy, release, and opening sign-up are outside this plan (rollout needs Prasanth's go-ahead).

## Rulings (refinements of the spec, decided while planning)

- **Confirmation source:** the spec says "from the JWT". The plan reads `auth.users.is_anonymous` inside Postgres instead (`oc_confirmed`). It is authoritative the moment the link is clicked, so neither the backend's `Principal` nor the app's token refresh has to change. Cost if wrong: none for correctness; the app only polls `/billing/me`.
- **`max_accounts_per_device`** lives in env (`MAX_ACCOUNTS_PER_DEVICE`) only, not in `oc_settings` — only the backend reads it.
- **Guest TTS** is capped per Mac at 2,000 characters before confirmation (spec silent; without it each replaced guest gets a fresh 20k ElevenLabs characters). A refusal is the existing silent `tts_budget` fallback.
- **Resend** re-sends by attaching the same email again (`PUT /auth/v1/user`), which GoTrue answers by mailing a new link; this avoids depending on `/resend`'s `email_change` semantics.
- **Notch HUD** sign-in form is replaced by one row that opens Settings → account (a code flow does not fit the HUD).

## Review Focus

1. A guest whose link was clicked but who has not used the app since (row `confirmed_at` still null) starts again on the same Mac → must get `code_sent`, never have their now-confirmed account deleted as a "live guest". (Task 3 test "a clicked-but-unused guest is treated as confirmed, not replaced".)
2. Same email typed twice on the same Mac before confirming (reinstall) → a fresh guest replaces the old one, the old auth user is deleted, and the $1 is not reset. (Task 1 test "a replaced guest's spend still counts for the Mac"; Task 3 test "a live guest on the Mac is replaced".)
3. Email typed with capitals/spaces (`" Gran@Example.com "`) → treated as `gran@example.com` everywhere, so the existing-account lookup works. (Task 3 test "emails are trimmed and lower-cased".)
4. Network drops between `/auth/start` creating the anonymous user and attaching the email → no orphan auth user left with no row. (Task 3 test "attach failure deletes the new anonymous user".)
5. Person closes the sheet while a code request is in flight → no state stuck in `.sending`; reopening shows the form. (Task 7 test "a cancelled start returns to idle".)

---

## File Structure

Backend:
- Modify `backend/supabase/schema.sql` — new columns, `oc_confirmed`, `oc_guest_usage`, new signatures for `oc_reserve`, `oc_reserve_chars`, `oc_spend_summary`, `oc_accounts_open`.
- Modify `backend/src/ledger.ts` — `Limits` guest fields, `confirm_email`, summary fields, memory + Supabase ledgers.
- Modify `backend/src/env.ts` — new env names.
- Modify `backend/src/db.ts` — `update` (PATCH).
- Create `backend/src/gotrue.ts` — the GoTrue calls the backend makes.
- Create `backend/src/accountAuth.ts` — `/auth/start`, `/auth/code`, `/auth/resend`, rate limiter, email/device validation.
- Modify `backend/src/app.ts` — register account auth; delete `/auth/signup`, `/auth/reset`, `resetPage`, `signupAllowed`; `/auth/config` drops `resetRedirectUrl`.
- Modify `backend/src/account.ts` — `/billing/me` fields, `resets_at` may be absent.
- Modify `backend/scripts/admin.mjs` — `add` creates a confirmed account; `prune`; `budget` shows guests.
- Tests: `backend/test/ledger.test.ts`, `backend/test/gotrue.test.ts` (new), `backend/test/accountAuth.test.ts` (new), `backend/test/account.test.ts`, `backend/test/app.test.ts` (signup block removed), `backend/test/db.test.ts`.

Mac app (`macos/OpenClicky/OpenClicky/…`):
- Create `DeviceIdentity.swift`.
- Modify `OpenClickyAuthSession.swift` — email flow replaces sign-up/sign-in/recover.
- Modify `BillingStatus.swift` — summary fields, unconfirmed sentence, HUD row.
- Modify `AccountCapabilities.swift` — `confirmEmail`, `deviceLimit`.
- Create `Dictation/UI/EmailAccountForm.swift` — the one email→code form, used by onboarding and the sheet.
- Modify `Dictation/UI/AccountSheet.swift`, `Dictation/UI/OnboardingWindow.swift`, `Dictation/UI/SettingsAccountPage.swift`.
- Tests: `OpenClickyTests/DeviceIdentityTests.swift` (new), `OpenClickyTests/EmailAccountFlowTests.swift` (new), `OpenClickyTests/AccountSheetTests.swift`.

Docs: `docs/accounts-runbook.md`.

---

### Task 1: Schema and ledger — guests, per-Mac pool, confirmation

**Files:**
- Modify: `backend/supabase/schema.sql` (the `oc_reserve`, `oc_reserve_chars`, `oc_spend_summary`, `oc_accounts_open` definitions and the final `revoke` line)
- Modify: `backend/src/ledger.ts`, `backend/src/env.ts`
- Test: `backend/test/ledger.test.ts`

**Interfaces:**
- Produces: `Limits` gains `guestTotalMicro: number; guestDays: number; guestTtsChars: number`. `LimitError` gains `"confirm_email"`. `ReserveResult` failure `resetsAt: string | null`. `SpendSummary` gains `confirmed: boolean; guestSpentMicro: number; guestLimitMicro: number; email: string | null`. `MemorySpendLedger.setAccount(userId, { confirmed?, deviceHash?, createdAt?, replaced?, email?, monthlyMicro?, dailyMicro?, blocked? })` and `MemorySpendLedger.confirm(userId, at: Date)`. `SpendLedger` method signatures are unchanged.
- Postgres: `oc_reserve(p_user text, p_estimate bigint, p_monthly bigint, p_daily bigint, p_global bigint, p_guest_total bigint, p_guest_days integer)`, `oc_reserve_chars(p_user text, p_chars integer, p_limit integer, p_global_remaining integer, p_guest_chars integer, p_guest_days integer)`, `oc_spend_summary(p_user text, p_monthly bigint, p_daily bigint, p_global bigint, p_tts integer, p_guest_total bigint)`, `oc_accounts_open(p_guest_days integer default 14)`.

- [ ] **Step 1: Write the failing tests** — append to `backend/test/ledger.test.ts`, and change the `limits` constant at the top to include the guest fields:

```ts
const limits = { monthlyMicro: 10_000_000, dailyMicro: 2_000_000, globalMonthlyMicro: 1_000_000_000, ttsCharsMonthly: 20_000, guestTotalMicro: 1_000_000, guestDays: 14, guestTtsChars: 2_000 };
```

```ts
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && npx vitest run test/ledger.test.ts`
Expected: FAIL (type errors on `confirmed`, `confirm`, `guestTotalMicro`; `confirm_email` never returned).

- [ ] **Step 3: Implement `ledger.ts` and `env.ts`**

In `env.ts` add beside the other account settings:

```ts
  /** Pre-confirmation allowance per Mac, in dollars (default 1). */
  GUEST_TOTAL_USD?: string;
  /** Days a guest may stay unconfirmed (default 14). */
  GUEST_DAYS?: string;
  /** Spoken characters per Mac before confirming (default 2000). */
  GUEST_TTS_CHARS?: string;
  /** Confirmed accounts one Mac may create (default 2). */
  MAX_ACCOUNTS_PER_DEVICE?: string;
```

Remove `ACCOUNT_RESET_REDIRECT_URL` from `env.ts`.

In `ledger.ts`:

```ts
export type LimitError = "personal_limit" | "daily_limit" | "monthly_budget" | "blocked" | "not_on_plan" | "confirm_email";
export type ReserveResult = { ok: true; reservationId: string } | { ok: false; error: LimitError; resetsAt: string | null };
```

`SpendSummary` gains:

```ts
  /** The email is confirmed (Supabase no longer calls the user anonymous). */
  confirmed: boolean;
  /** Spend before confirmation by every account on this Mac, and its cap. */
  guestSpentMicro: number; guestLimitMicro: number;
  email: string | null;
```

`Limits` and `limitsFromEnv`:

```ts
export interface Limits { monthlyMicro: number; dailyMicro: number; globalMonthlyMicro: number; ttsCharsMonthly: number; guestTotalMicro: number; guestDays: number; guestTtsChars: number }
// in limitsFromEnv's object:
    guestTotalMicro: usd(env.GUEST_TOTAL_USD, 1),
    guestDays: integer(env.GUEST_DAYS, 14),
    guestTtsChars: integer(env.GUEST_TTS_CHARS, 2_000),
```

`MemorySpendLedger` — replace `type Account` and add the guest logic:

```ts
type Account = {
  monthlyMicro?: number; dailyMicro?: number; blocked?: boolean;
  /** Absent means confirmed: accounts from before email-first sign-up. */
  confirmed?: boolean; confirmedAt?: number; deviceHash?: string; createdAt?: number; replaced?: boolean; email?: string;
};
```

Add these private/public members to `MemorySpendLedger`:

```ts
  confirm(userId: string, at: Date) { this.setAccount(userId, { confirmed: true, confirmedAt: at.getTime() }); }

  private isConfirmed(userId: string) { return this.accounts.get(userId)?.confirmed ?? true; }

  /** Spend and characters before confirmation, by every account sharing this one's Mac. */
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
```

In `reserve`, after the `blocked` check:

```ts
    if (this.guestRefusal(userId, now, limits) || (!this.isConfirmed(userId) && this.guestUsage(userId).micro + estimateMicro > limits.guestTotalMicro)) {
      return { ok: false, error: "confirm_email", resetsAt: null };
    }
```

In `reserveCharacters`, after the `blocked` check:

```ts
    if (this.guestRefusal(userId, now, limits) || (!this.isConfirmed(userId) && this.guestUsage(userId).chars + characters > limits.guestTtsChars)) {
      return { ok: false, error: "confirm_email", resetsAt: null };
    }
```

In `summary`, add to the returned object:

```ts
      confirmed: this.isConfirmed(userId),
      guestSpentMicro: this.guestUsage(userId).micro, guestLimitMicro: limits.guestTotalMicro,
      email: this.accounts.get(userId)?.email ?? null,
```

`SupabaseSpendLedger` passes the new parameters:

```ts
  reserve(userId: string, estimateMicro: number, limits: Limits): Promise<ReserveResult> {
    return this.db.rpc<ReserveResult>("oc_reserve", {
      p_user: userId, p_estimate: estimateMicro, p_monthly: limits.monthlyMicro, p_daily: limits.dailyMicro, p_global: limits.globalMonthlyMicro,
      p_guest_total: limits.guestTotalMicro, p_guest_days: limits.guestDays,
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
```

Every other test file that builds a `Limits` literal must add the three guest fields (`grep -rn "ttsCharsMonthly:" backend/test` lists them); use `guestTotalMicro: 1_000_000, guestDays: 14, guestTtsChars: 2_000`.

- [ ] **Step 4: Rewrite the SQL** in `backend/supabase/schema.sql`.

After the `insert into public.oc_settings …` line, add:

```sql
-- Email-first accounts (2026-10-10): a guest is an anonymous auth user with its email pending.
alter table public.oc_accounts add column if not exists email text;
alter table public.oc_accounts add column if not exists device_hash text;
alter table public.oc_accounts add column if not exists confirmed_at timestamptz;
alter table public.oc_accounts add column if not exists replaced_at timestamptz;
create index if not exists oc_accounts_email on public.oc_accounts (email);
create index if not exists oc_accounts_device on public.oc_accounts (device_hash);
alter table public.oc_settings add column if not exists max_guests integer not null default 300;

-- auth.users is the authority: is_anonymous flips the moment the link is clicked. The first time an
-- account is seen confirmed, confirmed_at is stamped so its earlier spend stays pre-confirmation.
create or replace function public.oc_confirmed(p_user text) returns boolean
language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare v_confirmed boolean;
begin
  select not coalesce(u.is_anonymous, false) into v_confirmed from auth.users u where u.id::text = p_user;
  v_confirmed := coalesce(v_confirmed, false);
  if v_confirmed then update oc_accounts set confirmed_at = now() where user_id = p_user and confirmed_at is null; end if;
  return v_confirmed;
end $$;

-- Spend (held + settled) and spoken characters before confirmation, by every account on this one's Mac.
create or replace function public.oc_guest_usage(p_user text) returns table(micro bigint, chars bigint)
language sql security definer set search_path = public set timezone = 'UTC' as $$
  with me as (select user_id, device_hash from oc_accounts where user_id = p_user),
  peers as (
    select a.user_id, a.confirmed_at from oc_accounts a, me
    where a.user_id = me.user_id or (me.device_hash is not null and a.device_hash = me.device_hash)
  )
  select
    (coalesce((select sum(e.cost_micro_usd) from oc_usage_events e join peers p on p.user_id = e.user_id where p.confirmed_at is null or e.ts < p.confirmed_at), 0)
     + coalesce((select sum(r.estimate_micro_usd) from oc_reservations r join peers p on p.user_id = r.user_id where p.confirmed_at is null), 0))::bigint,
    coalesce((select sum(e.characters) from oc_usage_events e join peers p on p.user_id = e.user_id where e.route = '/tts' and (p.confirmed_at is null or e.ts < p.confirmed_at)), 0)::bigint;
$$;
```

Replace the existing `oc_reserve` definition with (note the leading `drop` of the old signature):

```sql
drop function if exists public.oc_reserve(text, bigint, bigint, bigint, bigint);
create or replace function public.oc_reserve(p_user text, p_estimate bigint, p_monthly bigint, p_daily bigint, p_global bigint, p_guest_total bigint, p_guest_days integer)
returns json language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_day timestamptz := date_trunc('day', now() at time zone 'utc') at time zone 'utc';
  v_acct oc_accounts%rowtype;
  v_today bigint; v_month_spent bigint; v_everyone bigint; v_id uuid; v_guest bigint;
begin
  perform pg_advisory_xact_lock(hashtext('oc_reserve'));
  delete from oc_reservations where created_at < now() - interval '10 minutes';
  select * into v_acct from oc_accounts where user_id = p_user;
  if not found then
    return json_build_object('ok', false, 'error', 'not_on_plan', 'resetsAt', v_month + interval '1 month');
  end if;
  if v_acct.blocked then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  if not oc_confirmed(p_user) then
    if v_acct.replaced_at is not null or v_acct.created_at < now() - make_interval(days => p_guest_days) then
      return json_build_object('ok', false, 'error', 'confirm_email', 'resetsAt', null);
    end if;
    select micro into v_guest from oc_guest_usage(p_user);
    if v_guest + p_estimate > p_guest_total then
      return json_build_object('ok', false, 'error', 'confirm_email', 'resetsAt', null);
    end if;
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
```

Replace `oc_reserve_chars`:

```sql
drop function if exists public.oc_reserve_chars(text, integer, integer, integer);
create or replace function public.oc_reserve_chars(p_user text, p_chars integer, p_limit integer, p_global_remaining integer, p_guest_chars integer, p_guest_days integer)
returns json language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_used integer; v_acct oc_accounts%rowtype; v_guest bigint;
begin
  perform pg_advisory_xact_lock(hashtext('oc_reserve_chars'));
  select * into v_acct from oc_accounts where user_id = p_user;
  if not found then
    return json_build_object('ok', false, 'error', 'not_on_plan', 'resetsAt', v_month + interval '1 month');
  end if;
  if v_acct.blocked then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  if not oc_confirmed(p_user) then
    select chars into v_guest from oc_guest_usage(p_user);
    if v_acct.replaced_at is not null or v_acct.created_at < now() - make_interval(days => p_guest_days) or v_guest + p_chars > p_guest_chars then
      return json_build_object('ok', false, 'error', 'confirm_email', 'resetsAt', null);
    end if;
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
```

Replace `oc_spend_summary` (now plpgsql-free `sql`, but volatile because `oc_confirmed` writes):

```sql
drop function if exists public.oc_spend_summary(text, bigint, bigint, bigint, integer);
create or replace function public.oc_spend_summary(p_user text, p_monthly bigint, p_daily bigint, p_global bigint, p_tts integer, p_guest_total bigint)
returns json language sql volatile security definer set search_path = public set timezone = 'UTC' as $$
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
    'blocked', coalesce((select blocked from a), false),
    'onPlan', exists(select 1 from oc_accounts where user_id = p_user),
    'confirmed', oc_confirmed(p_user),
    'guestSpentMicro', (select micro from oc_guest_usage(p_user)),
    'guestLimitMicro', p_guest_total,
    'email', (select email from a));
$$;
```

Replace `oc_accounts_open`:

```sql
drop function if exists public.oc_accounts_open();
create or replace function public.oc_accounts_open(p_guest_days integer default 14) returns boolean
language sql security definer set search_path = public set timezone = 'UTC' as $$
  -- Confirmed accounts fill max_accounts; live guests (unconfirmed, not replaced, not expired) fill max_guests.
  select
    (select count(*) from oc_accounts a join auth.users u on u.id::text = a.user_id where not coalesce(u.is_anonymous, false))
      < (select max_accounts from oc_settings)
    and (select count(*) from oc_accounts a join auth.users u on u.id::text = a.user_id
         where coalesce(u.is_anonymous, false) and a.replaced_at is null and a.created_at > now() - make_interval(days => p_guest_days))
      < (select max_guests from oc_settings);
$$;
```

Final revoke line becomes:

```sql
revoke all on function public.oc_reserve, public.oc_settle, public.oc_reserve_chars, public.oc_spend_summary, public.oc_accounts_open, public.oc_confirmed, public.oc_guest_usage from public, anon, authenticated;
```

- [ ] **Step 5: Run the tests and typecheck**

Run: `cd backend && npx vitest run test/ledger.test.ts && npm run typecheck`
Expected: PASS; typecheck clean (fix every `Limits` literal the compiler names).

- [ ] **Step 6: Commit**

```bash
git add backend/supabase/schema.sql backend/src/ledger.ts backend/src/env.ts backend/test
git commit -m "feat(backend): guests — \$1 per Mac until the email is confirmed, read from auth.users"
```

---

### Task 2: GoTrue client and `SupabaseRest.update`

**Files:**
- Create: `backend/src/gotrue.ts`
- Modify: `backend/src/db.ts`
- Test: `backend/test/gotrue.test.ts` (new), `backend/test/db.test.ts`

**Interfaces:**
- Produces:
  - `type GoTrueSession = { access_token: string; refresh_token: string; expires_in: number; user: { id: string } }`
  - `class GoTrueError extends Error { status: number; code: string }` — `code` is GoTrue's `error_code` (e.g. `"email_exists"`, `"otp_expired"`, `"over_email_send_rate_limit"`), or `"http_<status>"`.
  - `class GoTrue(url, publishableKey, serviceKey, fetchImpl?)` with `signUpAnonymously(): Promise<GoTrueSession>`, `attachEmail(accessToken, email, redirectTo): Promise<void>`, `sendCode(email): Promise<void>`, `verifyCode(email, code): Promise<GoTrueSession>`, `getUser(userId): Promise<{ id: string; is_anonymous: boolean; email: string | null } | null>`, `deleteUser(userId): Promise<void>`.
  - `SupabaseRest.update(table: string, query: string, fields: Record<string, unknown>): Promise<void>`.

- [ ] **Step 1: Write the failing tests** — `backend/test/gotrue.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { GoTrue, GoTrueError } from "../src/gotrue.js";

function fake(reply: (url: string, init: RequestInit) => Response) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = vi.fn(async (url: any, init: any) => { calls.push({ url: String(url), init }); return reply(String(url), init); });
  return { gt: new GoTrue("https://p.supabase.co/", "pk", "sk", fetchImpl as unknown as typeof fetch), calls };
}
const session = { access_token: "a", refresh_token: "r", expires_in: 3600, user: { id: "u1" } };
const headers = (i: RequestInit) => i.headers as Record<string, string>;

describe("GoTrue", () => {
  it("signs up anonymously with the publishable key", async () => {
    const { gt, calls } = fake(() => new Response(JSON.stringify(session)));
    expect(await gt.signUpAnonymously()).toEqual(session);
    expect(calls[0].url).toBe("https://p.supabase.co/auth/v1/signup");
    expect(headers(calls[0].init).apikey).toBe("pk");
    expect(calls[0].init.body).toBe("{}");
  });
  it("attaches an email as the user, with redirect_to", async () => {
    const { gt, calls } = fake(() => new Response("{}"));
    await gt.attachEmail("tok", "gran@example.com", "https://api/auth/confirmed");
    expect(calls[0].url).toBe("https://p.supabase.co/auth/v1/user?redirect_to=" + encodeURIComponent("https://api/auth/confirmed"));
    expect(calls[0].init.method).toBe("PUT");
    expect(headers(calls[0].init).Authorization).toBe("Bearer tok");
    expect(JSON.parse(String(calls[0].init.body))).toEqual({ email: "gran@example.com" });
  });
  it("sends a code without creating users", async () => {
    const { gt, calls } = fake(() => new Response("{}"));
    await gt.sendCode("gran@example.com");
    expect(calls[0].url).toBe("https://p.supabase.co/auth/v1/otp");
    expect(JSON.parse(String(calls[0].init.body))).toEqual({ email: "gran@example.com", create_user: false });
  });
  it("verifies a code as type email", async () => {
    const { gt, calls } = fake(() => new Response(JSON.stringify(session)));
    expect(await gt.verifyCode("gran@example.com", "123456")).toEqual(session);
    expect(JSON.parse(String(calls[0].init.body))).toEqual({ type: "email", email: "gran@example.com", token: "123456" });
  });
  it("reads and deletes users with the service key", async () => {
    const { gt, calls } = fake((url, init) => init.method === "DELETE" ? new Response("{}") : new Response(JSON.stringify({ id: "u1", is_anonymous: true, email: "" })));
    expect(await gt.getUser("u1")).toEqual({ id: "u1", is_anonymous: true, email: null });
    await gt.deleteUser("u1");
    expect(calls[0].url).toBe("https://p.supabase.co/auth/v1/admin/users/u1");
    expect(headers(calls[0].init).Authorization).toBe("Bearer sk");
    expect(calls[1].init.method).toBe("DELETE");
  });
  it("getUser answers null for a missing user", async () => {
    const { gt } = fake(() => new Response('{"code":404,"error_code":"user_not_found"}', { status: 404 }));
    expect(await gt.getUser("gone")).toBeNull();
  });
  it("failures carry status and error_code, never the response text", async () => {
    const { gt } = fake(() => new Response('{"code":422,"error_code":"email_exists","msg":"secret detail"}', { status: 422 }));
    const err = await gt.attachEmail("t", "a@b.co", "x").catch((e) => e);
    expect(err).toBeInstanceOf(GoTrueError);
    expect(err).toMatchObject({ status: 422, code: "email_exists" });
    expect(String(err.message)).not.toContain("secret detail");
  });
});
```

Append to `backend/test/db.test.ts`:

```ts
it("update PATCHes the matching rows", async () => {
  const calls: { url: string; init: any }[] = [];
  const db = new SupabaseRest("https://p.supabase.co", "sk", (async (url: any, init: any) => { calls.push({ url: String(url), init }); return new Response(null, { status: 204 }); }) as any);
  await db.update("oc_accounts", "user_id=eq.u1", { replaced_at: "2026-10-10T00:00:00Z" });
  expect(calls[0].url).toBe("https://p.supabase.co/rest/v1/oc_accounts?user_id=eq.u1");
  expect(calls[0].init.method).toBe("PATCH");
  expect(JSON.parse(calls[0].init.body)).toEqual({ replaced_at: "2026-10-10T00:00:00Z" });
});
```

(Check `db.test.ts`'s existing constructor call and match it if `SupabaseRest`'s third argument differs.)

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && npx vitest run test/gotrue.test.ts test/db.test.ts`
Expected: FAIL — module `../src/gotrue.js` not found; `db.update` is not a function.

- [ ] **Step 3: Implement**

`backend/src/db.ts`, beside `upsert`:

```ts
  /** PATCH every row matching `query` (a PostgREST filter, e.g. `user_id=eq.abc`). */
  async update(table: string, query: string, fields: Record<string, unknown>): Promise<void> {
    const res = await this.fetchImpl(`${this.base}/${table}?${query}`, { method: "PATCH", headers: this.headers({ prefer: "return=minimal" }), body: JSON.stringify(fields) });
    await this.check(res, `update ${table}`);
  }
```

`backend/src/gotrue.ts`:

```ts
/**
 * The Supabase Auth (GoTrue) calls the backend makes for email-first accounts. The app never talks
 * to GoTrue for these: the backend fronts them so it can enforce the caps. Errors keep GoTrue's
 * status and error_code only — its message text never leaves this file.
 */
export type GoTrueSession = { access_token: string; refresh_token: string; expires_in: number; user: { id: string } };

export class GoTrueError extends Error {
  constructor(readonly status: number, readonly code: string) {
    super(`GoTrue ${status} ${code}`);
  }
}

export class GoTrue {
  private readonly base: string;
  constructor(url: string, private readonly publishableKey: string, private readonly serviceKey: string,
    private readonly fetchImpl: typeof fetch = (...args) => fetch(...args)) {
    this.base = `${url.replace(/\/+$/, "")}/auth/v1`;
  }

  private async call(path: string, init: { method?: string; body?: unknown; token?: string; admin?: boolean }): Promise<unknown> {
    const key = init.admin ? this.serviceKey : this.publishableKey;
    const res = await this.fetchImpl(`${this.base}${path}`, {
      method: init.method ?? "POST",
      headers: { "content-type": "application/json", apikey: key, Authorization: `Bearer ${init.token ?? key}` },
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
    });
    const text = await res.text();
    if (!res.ok) {
      let code = `http_${res.status}`;
      try { const j = JSON.parse(text) as { error_code?: string }; if (j.error_code) code = j.error_code; } catch { /* keep http_<status> */ }
      throw new GoTrueError(res.status, code);
    }
    return text ? JSON.parse(text) : undefined;
  }

  signUpAnonymously(): Promise<GoTrueSession> {
    return this.call("/signup", { body: {} }) as Promise<GoTrueSession>;
  }
  /** GoTrue mails a confirmation link to `email`; clicking it makes the anonymous user permanent. Calling it again re-sends. */
  async attachEmail(accessToken: string, email: string, redirectTo: string): Promise<void> {
    await this.call(`/user?redirect_to=${encodeURIComponent(redirectTo)}`, { method: "PUT", body: { email }, token: accessToken });
  }
  async sendCode(email: string): Promise<void> {
    await this.call("/otp", { body: { email, create_user: false } });
  }
  verifyCode(email: string, code: string): Promise<GoTrueSession> {
    return this.call("/verify", { body: { type: "email", email, token: code } }) as Promise<GoTrueSession>;
  }
  async getUser(userId: string): Promise<{ id: string; is_anonymous: boolean; email: string | null } | null> {
    try {
      const u = (await this.call(`/admin/users/${encodeURIComponent(userId)}`, { method: "GET", admin: true })) as { id: string; is_anonymous?: boolean; email?: string };
      return { id: u.id, is_anonymous: Boolean(u.is_anonymous), email: u.email || null };
    } catch (e) {
      if (e instanceof GoTrueError && e.status === 404) return null;
      throw e;
    }
  }
  async deleteUser(userId: string): Promise<void> {
    await this.call(`/admin/users/${encodeURIComponent(userId)}`, { method: "DELETE", admin: true });
  }
}
```

- [ ] **Step 4: Run tests**

Run: `cd backend && npx vitest run test/gotrue.test.ts test/db.test.ts && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/gotrue.ts backend/src/db.ts backend/test/gotrue.test.ts backend/test/db.test.ts
git commit -m "feat(backend): GoTrue client for anonymous sign-up, email attach, codes; SupabaseRest.update"
```

---

### Task 3: `/auth/start`, `/auth/code`, `/auth/resend`; passwords removed

**Files:**
- Create: `backend/src/accountAuth.ts`
- Modify: `backend/src/app.ts` (remove `SIGNUP_LIMIT`, `signupAllowed`, `TOO_MANY_SIGNUPS`, `resetPage`, the `/auth/reset` and `/auth/signup` routes; `/auth/config` loses `resetRedirectUrl`; call `registerAccountAuth(app)` after `/auth/confirmed`; update the `/auth/confirmed` copy)
- Test: `backend/test/accountAuth.test.ts` (new); `backend/test/app.test.ts` (delete every `signup …` test and the `stub`/`creds` helpers they use; update the `/auth/config` and `/auth/confirmed` expectations)

**Interfaces:**
- Consumes: `GoTrue`, `GoTrueError` (Task 2), `SupabaseRest.select/insert/update/rpc`, `requireAuth`, `bearerFrom` (auth.ts), `getEnv`.
- Produces: `registerAccountAuth(app: Hono<any>): void`; `normalizeEmail(raw: unknown): string | null`; `isDeviceHash(raw: unknown): raw is string`; `allow(hits: Map<string, number[]>, key: string, limit: number, now?: number): boolean`.
- HTTP:
  - `POST /auth/start {email, device}` → `200 {status:"signed_in", session:{access_token, refresh_token, expires_in}, confirmed:false}` | `200 {status:"code_sent"}` | `400 {error:"bad_email"|"bad_request"}` | `402 {error:"accounts_full"|"device_limit"}` | `429 {error:"slow_down"}` | `502 {error:"auth_unavailable"}` | `404` when Supabase is not configured.
  - `POST /auth/code {email, code}` → `200 {status:"signed_in", session, confirmed:true}` | `400 {error:"bad_code"}` (not six digits) | `401 {error:"bad_code"}` | `429` | `502`.
  - `POST /auth/resend` (Bearer Supabase JWT) → `200 {status:"sent"|"already_confirmed"}` | `429` | `502`.

- [ ] **Step 1: Write the failing tests** — `backend/test/accountAuth.test.ts`:

```ts
import { describe, it, expect, vi, afterEach } from "vitest";
import { createApp } from "../src/app.js";
import { normalizeEmail, isDeviceHash, allow } from "../src/accountAuth.js";

const env = { SUPABASE_URL: "https://p.supabase.co", SUPABASE_PUBLISHABLE_KEY: "pk", SUPABASE_SERVICE_KEY: "sk", ACCOUNTS_OPEN: "true", SUPABASE_JWT_SECRET: "x".repeat(32) };
const device = "d".repeat(64);
const post = (body: unknown, ip = "1.1.1.1") => ({ method: "POST", headers: { "content-type": "application/json", "x-forwarded-for": ip }, body: JSON.stringify(body) });
const session = { access_token: "acc", refresh_token: "ref", expires_in: 3600, user: { id: "u-new" } };

type Row = { user_id: string; email: string | null; device_hash: string | null; confirmed_at: string | null; replaced_at: string | null };
/** A fake Supabase: oc_accounts rows in memory, auth users in memory, every call recorded. */
function supabase(opts: { rows?: Row[]; users?: Record<string, { is_anonymous: boolean }>; open?: boolean; attach?: () => Response; insertFails?: number; verify?: () => Response } = {}) {
  const rows = [...(opts.rows ?? [])];
  const users = { ...(opts.users ?? {}) };
  const calls: { method: string; url: string; body?: any }[] = [];
  let insertFailures = opts.insertFails ?? 0;
  vi.stubGlobal("fetch", vi.fn(async (input: any, init: any = {}) => {
    const url = String(input), method = init.method ?? "GET";
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ method, url, body });
    const u = new URL(url);
    if (u.pathname === "/rest/v1/rpc/oc_accounts_open") return new Response(JSON.stringify(opts.open ?? true));
    if (u.pathname === "/rest/v1/oc_accounts") {
      const q = u.searchParams;
      const match = (r: Row) =>
        (!q.get("email") || r.email === q.get("email")!.slice(3)) &&
        (!q.get("device_hash") || r.device_hash === q.get("device_hash")!.slice(3)) &&
        (!q.get("user_id") || r.user_id === q.get("user_id")!.slice(3)) &&
        (q.get("confirmed_at") !== "not.is.null" || r.confirmed_at !== null);
      if (method === "GET") return new Response(JSON.stringify(rows.filter(match)));
      if (method === "POST") {
        if (insertFailures-- > 0) return new Response("nope", { status: 500 });
        rows.push({ email: null, device_hash: null, confirmed_at: null, replaced_at: null, ...body });
        return new Response(JSON.stringify([body]), { status: 201 });
      }
      if (method === "PATCH") { rows.filter(match).forEach((r) => Object.assign(r, body)); return new Response(null, { status: 204 }); }
    }
    if (u.pathname === "/auth/v1/signup") { users["u-new"] = { is_anonymous: true }; return new Response(JSON.stringify(session)); }
    if (u.pathname === "/auth/v1/user") return opts.attach ? opts.attach() : new Response("{}");
    if (u.pathname === "/auth/v1/otp") return new Response("{}");
    if (u.pathname === "/auth/v1/verify") return opts.verify ? opts.verify() : new Response(JSON.stringify({ ...session, user: { id: "u-old" } }));
    if (u.pathname.startsWith("/auth/v1/admin/users/")) {
      const id = decodeURIComponent(u.pathname.split("/").pop()!);
      if (method === "DELETE") { delete users[id]; return new Response("{}"); }
      return users[id] ? new Response(JSON.stringify({ id, ...users[id] })) : new Response('{"error_code":"user_not_found"}', { status: 404 });
    }
    throw new Error(`unexpected ${method} ${url}`);
  }));
  return { rows, users, calls };
}
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
const start = (body: unknown, ip?: string, e: Record<string, string> = env) => createApp({ log: null }).request("/auth/start", post(body, ip), e);

describe("helpers", () => {
  it("emails are trimmed and lower-cased; junk is null", () => {
    expect(normalizeEmail("  Gran@Example.COM ")).toBe("gran@example.com");
    expect(normalizeEmail("nope")).toBeNull();
    expect(normalizeEmail("a@b")).toBeNull();
    expect(normalizeEmail(42)).toBeNull();
  });
  it("device hashes are 64 lowercase hex", () => {
    expect(isDeviceHash(device)).toBe(true);
    expect(isDeviceHash("D".repeat(64))).toBe(false);
    expect(isDeviceHash("d".repeat(63))).toBe(false);
  });
  it("allow counts per key within an hour", () => {
    const hits = new Map<string, number[]>();
    for (let i = 0; i < 5; i++) expect(allow(hits, "ip", 5, 1000)).toBe(true);
    expect(allow(hits, "ip", 5, 1000)).toBe(false);
    expect(allow(hits, "ip", 5, 1000 + 3_600_001)).toBe(true);
  });
});

describe("POST /auth/start", () => {
  it("creates a guest: anonymous user, email attached with redirect, row recorded, session returned", async () => {
    const s = supabase();
    const res = await start({ email: " Gran@Example.com ", device });
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ status: "signed_in", session: { access_token: "acc", refresh_token: "ref", expires_in: 3600 }, confirmed: false });
    const attach = s.calls.find((c) => c.url.includes("/auth/v1/user"))!;
    expect(attach.url).toContain("redirect_to=" + encodeURIComponent("http://localhost/auth/confirmed"));
    expect(attach.body).toEqual({ email: "gran@example.com" });
    expect(s.rows).toContainEqual(expect.objectContaining({ user_id: "u-new", email: "gran@example.com", device_hash: device }));
  });
  it("an email with a confirmed account gets a code instead", async () => {
    const s = supabase({ rows: [{ user_id: "u-old", email: "gran@example.com", device_hash: "e".repeat(64), confirmed_at: "2026-10-01T00:00:00Z", replaced_at: null }] });
    const res = await start({ email: "gran@example.com", device });
    expect(await res.json()).toEqual({ status: "code_sent" });
    expect(s.calls.some((c) => c.url.endsWith("/auth/v1/otp"))).toBe(true);
    expect(s.calls.some((c) => c.url.endsWith("/auth/v1/signup"))).toBe(false);
  });
  it("is 402 accounts_full when closed, without touching auth", async () => {
    const s = supabase({ open: false });
    const res = await start({ email: "a@b.co", device });
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "accounts_full" });
    expect(s.calls.some((c) => c.url.includes("/auth/v1/"))).toBe(false);
    expect((await start({ email: "a@b.co", device }, "2.2.2.2", { ...env, ACCOUNTS_OPEN: "false" })).status).toBe(402);
  });
  it("is 402 device_limit when the Mac already has two confirmed accounts for other emails", async () => {
    const confirmed = (id: string, email: string): Row => ({ user_id: id, email, device_hash: device, confirmed_at: "2026-10-01T00:00:00Z", replaced_at: null });
    supabase({ rows: [confirmed("u1", "one@x.co"), confirmed("u2", "two@x.co")] });
    const res = await start({ email: "three@x.co", device });
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "device_limit" });
  });
  it("a live guest on the Mac is replaced and its auth user deleted", async () => {
    const s = supabase({ rows: [{ user_id: "u-guest", email: "gran@example.com", device_hash: device, confirmed_at: null, replaced_at: null }], users: { "u-guest": { is_anonymous: true } } });
    const res = await start({ email: "gran@example.com", device });
    expect(res.status).toBe(200);
    expect(s.rows.find((r) => r.user_id === "u-guest")!.replaced_at).not.toBeNull();
    expect(s.users["u-guest"]).toBeUndefined();
  });
  it("a clicked-but-unused guest is treated as confirmed, not replaced", async () => {
    const s = supabase({ rows: [{ user_id: "u-clicked", email: "gran@example.com", device_hash: device, confirmed_at: null, replaced_at: null }], users: { "u-clicked": { is_anonymous: false } } });
    const res = await start({ email: "gran@example.com", device });
    expect(await res.json()).toEqual({ status: "code_sent" });
    expect(s.users["u-clicked"]).toBeDefined();
    expect(s.rows[0].confirmed_at).not.toBeNull();
    expect(s.rows[0].replaced_at).toBeNull();
  });
  it("email taken in auth (confirmed elsewhere): the new anonymous user is deleted and a code is sent", async () => {
    const s = supabase({ attach: () => new Response('{"error_code":"email_exists"}', { status: 422 }) });
    const res = await start({ email: "gran@example.com", device });
    expect(await res.json()).toEqual({ status: "code_sent" });
    expect(s.users["u-new"]).toBeUndefined();
  });
  it("attach failure deletes the new anonymous user and answers 502", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const s = supabase({ attach: () => new Response('{"msg":"boom"}', { status: 500 }) });
    const res = await start({ email: "gran@example.com", device });
    expect(res.status).toBe(502);
    expect(await res.json()).toEqual({ error: "auth_unavailable" });
    expect(s.users["u-new"]).toBeUndefined();
    expect(s.rows).toHaveLength(0);
  });
  it("retries the row insert once; a second failure deletes the auth user and answers 502", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const ok = supabase({ insertFails: 1 });
    expect((await start({ email: "a@b.co", device }, "3.3.3.3")).status).toBe(200);
    expect(ok.rows).toHaveLength(1);
    const bad = supabase({ insertFails: 2 });
    expect((await start({ email: "a@b.co", device }, "4.4.4.4")).status).toBe(502);
    expect(bad.users["u-new"]).toBeUndefined();
  });
  it("rejects a bad email or device before any call", async () => {
    const s = supabase();
    expect(await (await start({ email: "nope", device })).json()).toEqual({ error: "bad_email" });
    expect(await (await start({ email: "a@b.co", device: "short" })).json()).toEqual({ error: "bad_request" });
    expect(s.calls).toHaveLength(0);
  });
  it("five starts per IP per hour", async () => {
    supabase();
    const app = createApp({ log: null });
    for (let i = 0; i < 5; i++) expect((await app.request("/auth/start", post({ email: `p${i}@b.co`, device }, "9.9.9.9"), env)).status).not.toBe(429);
    const res = await app.request("/auth/start", post({ email: "p6@b.co", device }, "9.9.9.9"), env);
    expect(res.status).toBe(429);
    expect(await res.json()).toEqual({ error: "slow_down" });
  });
  it("never leaks GoTrue text or logs the email's session", async () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    supabase({ attach: () => new Response('{"msg":"internal secret"}', { status: 500 }) });
    const res = await start({ email: "gran@example.com", device });
    expect(JSON.stringify(await res.json())).not.toContain("secret");
    expect(JSON.stringify(err.mock.calls)).not.toContain("acc");
  });
});

describe("POST /auth/code", () => {
  const code = (body: unknown, ip?: string) => createApp({ log: null }).request("/auth/code", post(body, ip), env);
  it("verifies, stamps confirmation on the row, and returns the session", async () => {
    const s = supabase({ rows: [{ user_id: "u-old", email: "gran@example.com", device_hash: device, confirmed_at: null, replaced_at: null }] });
    const res = await code({ email: "Gran@example.com", code: "123456" });
    expect(await res.json()).toEqual({ status: "signed_in", session: { access_token: "acc", refresh_token: "ref", expires_in: 3600 }, confirmed: true });
    expect(s.rows[0].confirmed_at).not.toBeNull();
  });
  it("creates the row for a confirmed auth user that has none", async () => {
    const s = supabase();
    await code({ email: "gran@example.com", code: "123456" });
    expect(s.rows).toContainEqual(expect.objectContaining({ user_id: "u-old", email: "gran@example.com" }));
    expect(s.rows[0].confirmed_at).not.toBeNull();
  });
  it("a wrong or expired code is 401 bad_code; a malformed one is 400", async () => {
    supabase({ verify: () => new Response('{"error_code":"otp_expired"}', { status: 403 }) });
    expect(await (await code({ email: "a@b.co", code: "000000" })).json()).toEqual({ error: "bad_code" });
    expect((await code({ email: "a@b.co", code: "12345" }, "5.5.5.5")).status).toBe(400);
  });
});

describe("POST /auth/resend", () => {
  it("needs a token", async () => {
    supabase();
    expect((await createApp({ log: null }).request("/auth/resend", { method: "POST" }, env)).status).toBe(401);
  });
});
```

(A signed-in `/auth/resend` test needs an HS256 user JWT: mint one with `jose`'s `SignJWT` using `env.SUPABASE_JWT_SECRET`, `sub: "u-guest"`, `role: "authenticated"`, `aud: "authenticated"`, then assert `{status:"sent"}` with the attach call carrying `Bearer <that token>`, and `{status:"already_confirmed"}` when `users["u-guest"].is_anonymous` is false. Follow how `backend/test/auth.test.ts` mints tokens.)

In `backend/test/app.test.ts`: delete the `signup …` tests and the `stub`/`creds` helpers; change the `/auth/config` expectation to `toMatchObject({ accountsOpen: false, confirmRedirectUrl: "http://localhost/auth/confirmed" })` and assert `resetRedirectUrl` is absent; change the `/auth/confirmed` expectation to `toContain("your openclicky account is confirmed")`; add `expect((await createApp({ log: null }).request("/auth/signup", { method: "POST" })).status).toBe(404)` and the same for `GET /auth/reset`.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && npx vitest run test/accountAuth.test.ts test/app.test.ts`
Expected: FAIL — `../src/accountAuth.js` not found.

- [ ] **Step 3: Implement `backend/src/accountAuth.ts`**

```ts
import type { Context, Hono } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, bearerFrom, type Principal } from "./auth.js";
import { SupabaseRest } from "./db.js";
import { GoTrue, GoTrueError, type GoTrueSession } from "./gotrue.js";

/**
 * Email-first accounts. Setup sends only an email and this Mac's device hash: a new email becomes a
 * guest at once (an anonymous Supabase user with the email attached, which mails a confirmation
 * link); an email that already has a confirmed account gets a 6-digit code instead. The caps live
 * here and in Postgres, which is why the app never talks to GoTrue for these.
 */
const HOUR = 3_600_000;
export function allow(hits: Map<string, number[]>, key: string, limit: number, now = Date.now()): boolean {
  for (const [k, v] of hits) { const live = v.filter((t) => now - t < HOUR); if (live.length) hits.set(k, live); else hits.delete(k); }
  const mine = hits.get(key) ?? [];
  if (mine.length >= limit) return false;
  hits.set(key, [...mine, now]);
  return true;
}

export function normalizeEmail(raw: unknown): string | null {
  if (typeof raw !== "string") return null;
  const email = raw.trim().toLowerCase();
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) && email.length <= 254 ? email : null;
}
export const isDeviceHash = (raw: unknown): raw is string => typeof raw === "string" && /^[0-9a-f]{64}$/.test(raw);

type Row = { user_id: string; email: string | null; confirmed_at: string | null; replaced_at: string | null };
const clientSession = (s: GoTrueSession) => ({ access_token: s.access_token, refresh_token: s.refresh_token, expires_in: s.expires_in });
const ipOf = (c: Context) => c.req.header("x-forwarded-for")?.split(",")[0]?.trim() || c.req.header("x-real-ip") || "unknown";
const enc = encodeURIComponent;
const nowIso = () => new Date().toISOString();

async function body(c: Context): Promise<Record<string, unknown>> {
  try { const v = JSON.parse((await c.req.text()) || "{}"); return v && typeof v === "object" ? v as Record<string, unknown> : {}; } catch { return {}; }
}

export function registerAccountAuth(app: Hono<any>) {
  const startHits = new Map<string, number[]>(), codeHits = new Map<string, number[]>(), resendHits = new Map<string, number[]>();

  const clients = (c: Context) => {
    const env = getEnv(c);
    if (!env.SUPABASE_URL || !env.SUPABASE_PUBLISHABLE_KEY || !env.SUPABASE_SERVICE_KEY) return null;
    return {
      env,
      db: new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY),
      gt: new GoTrue(env.SUPABASE_URL, env.SUPABASE_PUBLISHABLE_KEY, env.SUPABASE_SERVICE_KEY),
      redirect: env.ACCOUNT_CONFIRM_REDIRECT_URL || `${new URL(c.req.url).origin}/auth/confirmed`,
    };
  };
  const notConfigured = (c: Context) => c.json({ error: "sign-in is not configured on this backend" }, 404);
  const failed = (c: Context, where: string, e: unknown) => {
    if (e instanceof GoTrueError && e.status === 429) return c.json({ error: "slow_down" }, 429);
    console.error(`${where}: ${e instanceof GoTrueError ? e.message : (e as Error).message}`);
    return c.json({ error: "auth_unavailable" }, 502);
  };

  app.post("/auth/start", async (c) => {
    const k = clients(c);
    if (!k) return notConfigured(c);
    const req = await body(c);
    const email = normalizeEmail(req.email);
    if (!email) return c.json({ error: "bad_email" }, 400);
    if (!isDeviceHash(req.device)) return c.json({ error: "bad_request" }, 400);
    const device = req.device;
    if (!allow(startHits, ipOf(c), 5)) return c.json({ error: "slow_down" }, 429);
    const { env, db, gt, redirect } = k;
    const guestDays = Number(env.GUEST_DAYS) || 14;
    const perDevice = Number(env.MAX_ACCOUNTS_PER_DEVICE) || 2;
    const sendCode = async () => { await gt.sendCode(email); return c.json({ status: "code_sent" }); };
    try {
      if ((await db.select<Row>("oc_accounts", `email=eq.${enc(email)}&confirmed_at=not.is.null&select=user_id&limit=1`)).length) return await sendCode();
      const open = env.ACCOUNTS_OPEN === "true" && (await db.rpc<boolean>("oc_accounts_open", { p_guest_days: guestDays }).catch(() => false));
      if (!open) return c.json({ error: "accounts_full" }, 402);

      // Sort this Mac's accounts into confirmed and live guests. auth.users decides: a link clicked
      // since the row was last read is confirmed even though confirmed_at is still empty.
      const onDevice = (await db.select<Row>("oc_accounts", `device_hash=eq.${device}&replaced_at=is.null&select=user_id,email,confirmed_at,replaced_at`)).filter((r) => !r.replaced_at);
      const confirmed: Row[] = [], guests: Row[] = [];
      for (const row of onDevice) {
        if (row.confirmed_at) { confirmed.push(row); continue; }
        const user = await gt.getUser(row.user_id);
        if (user && !user.is_anonymous) {
          await db.update("oc_accounts", `user_id=eq.${enc(row.user_id)}`, { confirmed_at: nowIso() });
          confirmed.push(row);
        } else guests.push(row);
      }
      if (confirmed.some((r) => r.email === email)) return await sendCode();
      if (confirmed.filter((r) => r.email !== email).length >= perDevice) return c.json({ error: "device_limit" }, 402);
      for (const guest of guests) {
        await db.update("oc_accounts", `user_id=eq.${enc(guest.user_id)}`, { replaced_at: nowIso() });
        await gt.deleteUser(guest.user_id).catch((e) => console.error(`auth/start: delete replaced guest: ${(e as Error).message}`));
      }

      const session = await gt.signUpAnonymously();
      const discard = () => gt.deleteUser(session.user.id).catch((e) => console.error(`auth/start: delete new guest: ${(e as Error).message}`));
      try {
        await gt.attachEmail(session.access_token, email, redirect);
      } catch (e) {
        await discard();
        if (e instanceof GoTrueError && e.code === "email_exists") return await sendCode();
        throw e;
      }
      let recorded = false;
      for (let attempt = 1; attempt <= 2 && !recorded; attempt++) {
        recorded = await db.insert("oc_accounts", { user_id: session.user.id, email, device_hash: device }).then(() => true, (e) => {
          console.error(`auth/start: oc_accounts insert (try ${attempt}): ${(e as Error).message}`);
          return false;
        });
      }
      if (!recorded) { await discard(); return c.json({ error: "auth_unavailable" }, 502); }
      return c.json({ status: "signed_in", session: clientSession(session), confirmed: false });
    } catch (e) {
      return failed(c, "auth/start", e);
    }
  });

  app.post("/auth/code", async (c) => {
    const k = clients(c);
    if (!k) return notConfigured(c);
    const req = await body(c);
    const email = normalizeEmail(req.email);
    const code = typeof req.code === "string" ? req.code.trim() : "";
    if (!email || !/^\d{6}$/.test(code)) return c.json({ error: "bad_code" }, 400);
    if (!allow(codeHits, ipOf(c), 10)) return c.json({ error: "slow_down" }, 429);
    const { db, gt } = k;
    let session: GoTrueSession;
    try { session = await gt.verifyCode(email, code); }
    catch (e) {
      if (e instanceof GoTrueError && e.status >= 400 && e.status < 500 && e.status !== 429) return c.json({ error: "bad_code" }, 401);
      return failed(c, "auth/code", e);
    }
    try {
      const id = session.user.id;
      const rows = await db.select<Row>("oc_accounts", `user_id=eq.${enc(id)}&select=user_id,confirmed_at`);
      if (!rows.length) await db.insert("oc_accounts", { user_id: id, email, confirmed_at: nowIso() });
      else if (!rows[0].confirmed_at) await db.update("oc_accounts", `user_id=eq.${enc(id)}`, { confirmed_at: nowIso() });
    } catch (e) {
      console.error(`auth/code: account row: ${(e as Error).message}`); // signed in anyway; oc_confirmed stamps it on first use
    }
    return c.json({ status: "signed_in", session: clientSession(session), confirmed: true });
  });

  app.post("/auth/resend", requireAuth, async (c) => {
    const k = clients(c);
    if (!k) return notConfigured(c);
    const principal = c.get("principal" as never) as Principal;
    if (!allow(resendHits, principal.sub, 3)) return c.json({ error: "slow_down" }, 429);
    const token = bearerFrom(c.req.header("authorization"))!;
    const { db, gt, redirect } = k;
    try {
      const user = await gt.getUser(principal.sub);
      if (user && !user.is_anonymous) return c.json({ status: "already_confirmed" });
      const [row] = await db.select<Row>("oc_accounts", `user_id=eq.${enc(principal.sub)}&select=user_id,email`);
      if (!row?.email) return c.json({ error: "bad_request" }, 400);
      await gt.attachEmail(token, row.email, redirect);
      return c.json({ status: "sent" });
    } catch (e) {
      return failed(c, "auth/resend", e);
    }
  });
}
```

In `app.ts`: import `registerAccountAuth` from `./accountAuth.js`; delete `SIGNUP_LIMIT`, `SIGNUP_WINDOW_MS`, `signupAllowed`, `TOO_MANY_SIGNUPS`, `resetPage`, the `/auth/reset` route, the `/auth/signup` route and its `signupHits`; remove `resetRedirectUrl` from `/auth/config`; pass `{ p_guest_days: Number(env.GUEST_DAYS) || 14 }` to the `oc_accounts_open` rpc in `/auth/config`; replace the `/auth/confirmed` body text with:

```html
<div style="text-align:center"><h1 style="font-family:Georgia,serif;font-weight:400">you're confirmed.</h1><p>your openclicky account is confirmed — you can close this tab.</p></div>
```

and call `registerAccountAuth(app);` right after the `/auth/confirmed` route.

- [ ] **Step 4: Run tests**

Run: `cd backend && npx vitest run && npm run typecheck && npm run lint`
Expected: all PASS, 0 lint errors.

- [ ] **Step 5: Commit**

```bash
git add backend/src backend/test
git commit -m "feat(backend): email-first auth — /auth/start, /auth/code, /auth/resend; passwords removed"
```

---

### Task 4: `/billing/me` reports confirmation and the guest pool

**Files:**
- Modify: `backend/src/account.ts`
- Test: `backend/test/account.test.ts`

**Interfaces:**
- Consumes: `SpendSummary.confirmed/guestSpentMicro/guestLimitMicro/email` (Task 1).
- Produces: `AccountSummaryJson` gains `confirmed: boolean; guestSpentUsd: number; guestLimitUsd: number; email: string | null` (masked); `maskEmail(email: string | null): string | null`. `reserveOr402` omits `resets_at` when the ledger gives `null`.

- [ ] **Step 1: Write the failing tests** — append to `backend/test/account.test.ts` (reuse that file's existing helpers for building a context/app; the expectations are what matter):

```ts
import { maskEmail } from "../src/account.js";

describe("email-first fields", () => {
  it("masks all but the first letter of the local part", () => {
    expect(maskEmail("prasanth@flowsxr.com")).toBe("p•••@flowsxr.com");
    expect(maskEmail("a@b.co")).toBe("a•••@b.co");
    expect(maskEmail(null)).toBeNull();
  });
});
```

and a `/billing/me` test through `createApp({ log: null, spendLedger: ledger })` with `GRANT_ACCOUNTS: "true"` and a `MemorySpendLedger({ requireAccountRow: true })` whose account is `{ confirmed: false, deviceHash: "a".repeat(64), createdAt: Date.now(), email: "gran@example.com" }`, asserting the JSON contains `{ confirmed: false, guestSpentUsd: 0, guestLimitUsd: 1, email: "g•••@example.com" }`. Mint the user token the same way the existing `/billing/me` tests in `app.test.ts` do.

And: a guest over its cap calling `/v1/polish` gets `402 { error: "confirm_email" }` with no `resets_at` key.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && npx vitest run test/account.test.ts test/app.test.ts`
Expected: FAIL — `maskEmail` not exported; fields missing.

- [ ] **Step 3: Implement** in `account.ts`:

```ts
/** "prasanth@flowsxr.com" → "p•••@flowsxr.com": enough for the person to recognise, no more. */
export function maskEmail(email: string | null): string | null {
  if (!email) return null;
  const at = email.indexOf("@");
  return at < 1 ? null : `${email[0]}•••${email.slice(at)}`;
}
```

`AccountSummaryJson` adds `confirmed: boolean; guestSpentUsd: number; guestLimitUsd: number; email: string | null;` and `accountSummary` adds:

```ts
    // Older schemas send no confirmation: count the account as confirmed rather than shut it out.
    confirmed: account.byok || s.confirmed !== false,
    guestSpentUsd: dollars(s.guestSpentMicro ?? 0), guestLimitUsd: dollars(s.guestLimitMicro ?? 0),
    email: maskEmail(s.email ?? null),
```

`reserveOr402` last line:

```ts
  return c.json(result.resetsAt ? { error: result.error, resets_at: result.resetsAt } : { error: result.error }, 402);
```

The unmetered `/billing/me` literal in `app.ts` gains `confirmed: true, guestSpentUsd: 0, guestLimitUsd: 0, email: null`.

- [ ] **Step 4: Run tests**

Run: `cd backend && npx vitest run && npm run typecheck`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/account.ts backend/src/app.ts backend/test
git commit -m "feat(backend): /billing/me says whether the email is confirmed and how much of the guest \$1 is used"
```

---

### Task 5: Admin script — `add` makes a confirmed account, `prune`, guests in `budget`

**Files:**
- Modify: `backend/scripts/admin.mjs`

**Interfaces:**
- Consumes: Supabase Auth admin API (`POST /auth/v1/admin/users`, `DELETE /auth/v1/admin/users/{id}`), `oc_accounts` columns from Task 1.
- Produces: `add <email>` creates the auth user if missing (`{ email, email_confirm: true }`) and upserts `{ user_id, email, confirmed_at: now }`; `prune [--yes]` deletes auth users of guests that are replaced or older than `GUEST_DAYS` (default 14) and stamps `replaced_at` on expired ones (rows stay); `budget` prints `accounts N confirmed of CAP · G live guests`.

- [ ] **Step 1: Implement.** In `add`, replace `requireUser(email)` with a find-or-create:

```js
  async add() {
    const email = needEmail("add <email>").toLowerCase();
    let user = (await listUsers()).find((u) => u.email?.toLowerCase() === email);
    if (!user) user = await api("POST", `${AUTH}/admin/users`, { email, email_confirm: true });
    await upsertAccount(user.id, { email, confirmed_at: new Date().toISOString() });
    console.log(`${email}: confirmed account ready (default limits) — sign in on the Mac with this email and the emailed code`);
  },
```

(`AUTH` and `listUsers` already exist in the script for the admin API; use whatever names it defines for the auth base URL — `grep -n "admin/users" backend/scripts/admin.mjs`.)

Add `prune`:

```js
  async prune() {
    const days = Number(process.env.GUEST_DAYS ?? 14);
    const cutoff = Date.now() - days * 86_400_000;
    const rows = await api("GET", rest("oc_accounts", "confirmed_at=is.null&select=user_id,email,created_at,replaced_at"));
    const users = new Map((await listUsers()).map((u) => [u.id, u]));
    const doomed = rows.filter((r) => users.get(r.user_id)?.is_anonymous && (r.replaced_at || Date.parse(r.created_at) < cutoff));
    console.log(`${doomed.length} guest login(s) to delete (replaced, or unconfirmed for ${days}+ days); their usage rows stay`);
    if (!doomed.length || (!args.includes("--yes") && !(await confirm("delete them?")))) return;
    for (const r of doomed) {
      await api("DELETE", `${AUTH}/admin/users/${r.user_id}`);
      if (!r.replaced_at) await api("PATCH", rest("oc_accounts", `user_id=eq.${r.user_id}`), { replaced_at: new Date().toISOString() });
    }
    console.log("done");
  },
```

(If the script has no `confirm` helper, reuse the one `remove` uses for its confirmation prompt.)

In `budget`, count confirmed as `!u.is_anonymous` instead of `email_confirmed_at`, and count live guests as accounts whose auth user `is_anonymous` and whose row has no `replaced_at`; print `accounts ${confirmed} confirmed of ${cap} · ${guests} live guests`. Add `prune [--yes]` to the usage header comment and the `commands:` error line.

- [ ] **Step 2: Verify syntax**

Run: `cd backend && node --check scripts/admin.mjs`
Expected: no output (valid).

- [ ] **Step 3: Commit**

```bash
git add backend/scripts/admin.mjs
git commit -m "feat(admin): add creates a confirmed account; prune deletes stale guest logins; budget counts guests"
```

---

### Task 6: Mac — device identity and the account summary fields

**Files:**
- Create: `macos/OpenClicky/OpenClicky/DeviceIdentity.swift`
- Modify: `macos/OpenClicky/OpenClicky/BillingStatus.swift`, `macos/OpenClicky/OpenClicky/AccountCapabilities.swift`
- Test: `macos/OpenClicky/OpenClickyTests/DeviceIdentityTests.swift` (new), `macos/OpenClicky/OpenClickyTests/AccountSheetTests.swift`

**Interfaces:**
- Produces: `enum DeviceIdentity { static func hash(platformUUID: String) -> String; static var current: String? }`. `BillingSummary` gains `var confirmed: Bool? = nil`, `var guestSpentUsd: Double? = nil`, `var guestLimitUsd: Double? = nil`, `var email: String? = nil`, and `var isUnconfirmed: Bool { confirmed == false }`. `AllowanceStanding` gains `.unconfirmed`. `AccountLimitError` gains `.confirmEmail = "confirm_email"` and `.deviceLimit = "device_limit"`.

- [ ] **Step 1: Write the failing tests** — `OpenClickyTests/DeviceIdentityTests.swift`:

```swift
import Testing
@testable import OpenClicky

struct DeviceIdentityTests {
    @Test func theHashIsSaltedSHA256InLowercaseHex() {
        // echo -n "openclicky-device-v1:ABC" | shasum -a 256
        #expect(DeviceIdentity.hash(platformUUID: "ABC") == "09dfa12193ca778d98a1c1d3b219ba1f2e98a5d1fe755d048e0b1b4f8faff226")
    }
    @Test func thisMacHasAStable64CharacterHash() {
        let first = DeviceIdentity.current
        #expect(first?.count == 64)
        #expect(first?.allSatisfy { "0123456789abcdef".contains($0) } == true)
        #expect(DeviceIdentity.current == first)
    }
}
```

(The constant is `printf 'openclicky-device-v1:ABC' | shasum -a 256`.)

Append to `AccountSheetTests`:

```swift
    @Test func anUnconfirmedAccountSaysCheckYourInbox() {
        var guest = summary(spentMonth: 0.25)
        guest.confirmed = false; guest.guestSpentUsd = 0.25; guest.guestLimitUsd = 1
        #expect(guest.allowanceStanding == .unconfirmed)
        #expect(guest.allowanceSentence(resetDay: "nov 1") == "confirm your email to unlock the full free allowance · 25% of the starter used")
        guest.guestSpentUsd = 1
        #expect(guest.allowanceSentence(resetDay: "nov 1") == "the starter allowance is used — confirm your email to keep going")
    }

    @Test func confirmEmailAndDeviceLimitHaveSentences() {
        #expect(AccountLimitError.confirmEmail.message == "confirm your email to keep going — we sent you a link.")
        #expect(AccountLimitError.deviceLimit.message == "this mac already has two openclicky accounts — sign in with one of them.")
        #expect(AccountLimitError.from(status: 402, body: Data(#"{"error":"confirm_email"}"#.utf8)) == .confirmEmail)
    }
```

(`summary(...)` returns a `let`; change the helper's call site to `var guest = summary(...)` — `BillingSummary`'s new properties are `var`, so they can be set.)

- [ ] **Step 2: Run to verify they fail**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/DeviceIdentityTests -only-testing:OpenClickyTests/AccountSheetTests 2>&1 | tail -20`
Expected: build FAILS (`DeviceIdentity` not found).

- [ ] **Step 3: Implement**

`DeviceIdentity.swift`:

```swift
//
//  DeviceIdentity.swift
//  OpenClicky
//
//  One stable, anonymous id for this Mac, so the backend can give each Mac one starter allowance
//  however often the app is reinstalled. It is a salted SHA-256 of the hardware UUID: the UUID itself
//  never leaves the Mac. Apple-silicon and Intel Macs both have one.
//

import CryptoKit
import Foundation
import IOKit

enum DeviceIdentity {
    static func hash(platformUUID: String) -> String {
        SHA256.hash(data: Data("openclicky-device-v1:\(platformUUID)".utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static var current: String? {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"))
        guard service != 0 else { return nil }
        defer { IOObjectRelease(service) }
        guard let uuid = IORegistryEntryCreateCFProperty(service, kIOPlatformUUIDKey as CFString, kCFAllocatorDefault, 0)?
            .takeRetainedValue() as? String, !uuid.isEmpty else { return nil }
        return hash(platformUUID: uuid)
    }
}
```

Add the file to the app target in `OpenClicky.xcodeproj` (if the project uses folder references / synchronized groups, nothing to do — check with `grep -n "fileSystemSynchronizedGroups\|PBXFileSystemSynchronizedRootGroup" OpenClicky.xcodeproj/project.pbxproj`).

`BillingStatus.swift` — `BillingSummary` gains:

```swift
    /// Email-first accounts: false until the confirmation link is clicked; older backends omit it.
    var confirmed: Bool? = nil
    /// The Mac's starter allowance (spend before confirming) and its cap.
    var guestSpentUsd: Double? = nil
    var guestLimitUsd: Double? = nil
    /// Masked, e.g. "p•••@flowsxr.com".
    var email: String? = nil
```

`AllowanceStanding` adds `unconfirmed`; in `allowanceStanding`, right after the `blocked` line: `if confirmed == false { return .unconfirmed }`. In `allowanceSentence`:

```swift
        case .unconfirmed:
            let limit = guestLimitUsd ?? 0, spent = guestSpentUsd ?? 0
            if limit > 0 && spent >= limit { return "the starter allowance is used — confirm your email to keep going" }
            let percent = limit > 0 ? Int((spent / limit * 100).rounded()) : 0
            return "confirm your email to unlock the full free allowance · \(percent)% of the starter used"
```

`AccountCapabilities.swift` — `AccountLimitError` adds `case confirmEmail = "confirm_email", deviceLimit = "device_limit"` with messages exactly as in the test.

Any exhaustive `switch` over `AllowanceStanding` elsewhere (`grep -rn "case .paused" OpenClicky`) gets an `.unconfirmed` arm that treats it like `.runningLow` for colour/notices.

- [ ] **Step 4: Run tests**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky
git commit -m "feat(mac): device hash, and the account summary knows about unconfirmed emails"
```

---

### Task 7: Mac — the email flow in `OpenClickyAuthSession`

**Files:**
- Modify: `macos/OpenClicky/OpenClicky/OpenClickyAuthSession.swift`
- Test: `macos/OpenClicky/OpenClickyTests/EmailAccountFlowTests.swift` (new); remove from `AccountSheetTests.swift` the tests `aSecondSignUpWaitsForTheFirst`, `createNeedsEightCharactersAndSignInAnyPassword`, `signInFailuresReadAsOnePlainSentence`

**Interfaces:**
- Consumes: `DeviceIdentity.current` (Task 6), `AccountLimitError` (Task 6), backend contract from Task 3.
- Produces:
  - `enum EmailFlowState: Equatable { case idle, sending, needsCode(email: String), signedIn(confirmed: Bool), failed(String) }` as `OpenClickyAuthSession.EmailFlowState`, published as `emailFlow`.
  - `func start(email: String) async`, `func submitCode(_ code: String) async`, `func resendLink() async -> Bool`, `func forgetSettledFlow()`, `func watchConfirmation()`.
  - `nonisolated static func startRequest(backendBaseURL: String, email: String, device: String) -> URLRequest?`, `codeRequest(backendBaseURL:email:code:)`, `resendRequest(backendBaseURL:token:)`.
  - `nonisolated static func outcome(status: Int, body: Data, email: String) -> StartOutcome` with `enum StartOutcome: Equatable { case signedIn(Session, confirmed: Bool), codeSent, failed(String) }` and `struct Session: Decodable, Equatable { access_token, refresh_token: String; expires_in: Double }`.
  - `nonisolated static func canStart(from: EmailFlowState) -> Bool` (false only while `.sending`).
  - Removed: `signIn(email:password:)`, `signUp`, `signUpRequest`, `recoverRequest`, `recover`, `waitForConfirmation`, `canStartSignUp`, `SignUpState`, `signUpState`, `forgetSettledSignUp`, `AuthConfig.resetRedirectUrl`.

- [ ] **Step 1: Write the failing tests** — `OpenClickyTests/EmailAccountFlowTests.swift`:

```swift
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct EmailAccountFlowTests {
    private let device = String(repeating: "d", count: 64)
    private func json(_ s: String) -> Data { Data(s.utf8) }

    @Test func startPostsTheEmailAndDevice() throws {
        let request = try #require(OpenClickyAuthSession.startRequest(backendBaseURL: "https://api.example", email: "gran@example.com", device: device))
        #expect(request.url?.absoluteString == "https://api.example/auth/start")
        #expect(request.httpMethod == "POST")
        let body = try JSONSerialization.jsonObject(with: try #require(request.httpBody)) as? [String: String]
        #expect(body == ["email": "gran@example.com", "device": device])
    }

    @Test func eachAnswerBecomesOneOutcome() {
        let signedIn = OpenClickyAuthSession.outcome(status: 200, body: json(#"{"status":"signed_in","session":{"access_token":"a","refresh_token":"r","expires_in":3600},"confirmed":false}"#), email: "g@x.co")
        #expect(signedIn == .signedIn(.init(access_token: "a", refresh_token: "r", expires_in: 3600), confirmed: false))
        #expect(OpenClickyAuthSession.outcome(status: 200, body: json(#"{"status":"code_sent"}"#), email: "g@x.co") == .codeSent)
        #expect(OpenClickyAuthSession.outcome(status: 402, body: json(#"{"error":"accounts_full"}"#), email: "g@x.co") == .failed(AccountLimitError.accountsFull.message))
        #expect(OpenClickyAuthSession.outcome(status: 402, body: json(#"{"error":"device_limit"}"#), email: "g@x.co") == .failed(AccountLimitError.deviceLimit.message))
        #expect(OpenClickyAuthSession.outcome(status: 400, body: json(#"{"error":"bad_email"}"#), email: "g") == .failed("that doesn't look like an email address."))
        #expect(OpenClickyAuthSession.outcome(status: 401, body: json(#"{"error":"bad_code"}"#), email: "g@x.co") == .failed("that code didn't work — check it, or ask for a new one."))
        #expect(OpenClickyAuthSession.outcome(status: 429, body: json(#"{"error":"slow_down"}"#), email: "g@x.co") == .failed("too many tries — wait a few minutes and try again."))
        #expect(OpenClickyAuthSession.outcome(status: 502, body: json("{}"), email: "g@x.co") == .failed("couldn't reach openclicky right now — try again in a minute."))
        #expect(OpenClickyAuthSession.outcome(status: 200, body: json("not json"), email: "g@x.co") == .failed("couldn't reach openclicky right now — try again in a minute."))
    }

    @Test func codeAndResendRequests() throws {
        let code = try #require(OpenClickyAuthSession.codeRequest(backendBaseURL: "https://api.example", email: "g@x.co", code: "123456"))
        #expect(code.url?.path == "/auth/code")
        #expect((try JSONSerialization.jsonObject(with: try #require(code.httpBody)) as? [String: String]) == ["email": "g@x.co", "code": "123456"])
        let resend = try #require(OpenClickyAuthSession.resendRequest(backendBaseURL: "https://api.example", token: "tok"))
        #expect(resend.url?.path == "/auth/resend")
        #expect(resend.value(forHTTPHeaderField: "Authorization") == "Bearer tok")
    }

    @Test func onlyASendInFlightBlocksANewStart() {
        #expect(!OpenClickyAuthSession.canStart(from: .sending))
        #expect(OpenClickyAuthSession.canStart(from: .idle))
        #expect(OpenClickyAuthSession.canStart(from: .needsCode(email: "g@x.co")))
        #expect(OpenClickyAuthSession.canStart(from: .failed("no")))
    }

    @Test func aCancelledStartReturnsToIdle() {
        #expect(OpenClickyAuthSession.isCancellation(CancellationError()))
        #expect(OpenClickyAuthSession.stateAfterCancellation(.sending) == .idle)
        #expect(OpenClickyAuthSession.stateAfterCancellation(.needsCode(email: "g@x.co")) == .needsCode(email: "g@x.co"))
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/EmailAccountFlowTests 2>&1 | tail -20`
Expected: build FAILS (`startRequest` not found).

- [ ] **Step 3: Implement.** In `OpenClickyAuthSession.swift`, update the header comment to describe the email flow, then replace the sign-up/sign-in/recover section with:

```swift
    enum EmailFlowState: Equatable { case idle, sending, needsCode(email: String), signedIn(confirmed: Bool), failed(String) }
    @Published private(set) var emailFlow: EmailFlowState = .idle

    struct Session: Decodable, Equatable { let access_token: String; let refresh_token: String; let expires_in: Double }
    enum StartOutcome: Equatable { case signedIn(Session, confirmed: Bool), codeSent, failed(String) }

    nonisolated static let unreachableMessage = "couldn't reach openclicky right now — try again in a minute."

    private nonisolated static func post(_ backendBaseURL: String, _ path: String, _ body: [String: String]?, token: String? = nil) -> URLRequest? {
        guard let url = URL(string: "\(backendBaseURL)\(path)") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body { request.httpBody = try? JSONSerialization.data(withJSONObject: body) }
        return request
    }
    nonisolated static func startRequest(backendBaseURL: String, email: String, device: String) -> URLRequest? {
        post(backendBaseURL, "/auth/start", ["email": email, "device": device])
    }
    nonisolated static func codeRequest(backendBaseURL: String, email: String, code: String) -> URLRequest? {
        post(backendBaseURL, "/auth/code", ["email": email, "code": code])
    }
    nonisolated static func resendRequest(backendBaseURL: String, token: String) -> URLRequest? {
        post(backendBaseURL, "/auth/resend", nil, token: token)
    }

    /// The backend's answer to /auth/start or /auth/code, as one thing the form can show.
    nonisolated static func outcome(status: Int, body: Data, email: String) -> StartOutcome {
        struct Reply: Decodable { let status: String?; let session: Session?; let confirmed: Bool?; let error: String? }
        let reply = try? JSONDecoder().decode(Reply.self, from: body)
        if (200..<300).contains(status) {
            if reply?.status == "code_sent" { return .codeSent }
            if let session = reply?.session { return .signedIn(session, confirmed: reply?.confirmed ?? false) }
            return .failed(unreachableMessage)
        }
        switch reply?.error {
        case "accounts_full": return .failed(AccountLimitError.accountsFull.message)
        case "device_limit": return .failed(AccountLimitError.deviceLimit.message)
        case "bad_email": return .failed("that doesn't look like an email address.")
        case "bad_code": return .failed("that code didn't work — check it, or ask for a new one.")
        case "slow_down": return .failed("too many tries — wait a few minutes and try again.")
        default: return .failed(unreachableMessage)
        }
    }

    nonisolated static func canStart(from state: EmailFlowState) -> Bool { state != .sending }

    /// A closed form cancels its task: a send in flight goes back to the form, anything else stays.
    nonisolated static func stateAfterCancellation(_ state: EmailFlowState) -> EmailFlowState { state == .sending ? .idle : state }

    nonisolated static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        return false
    }

    /// Setup's one question: the email. A new email is signed in at once (unconfirmed); an email
    /// that already has an account gets a 6-digit code (`needsCode`).
    func start(email: String) async {
        guard Self.canStart(from: emailFlow) else { return }
        let trimmed = email.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard let device = DeviceIdentity.current,
              let request = Self.startRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: trimmed, device: device) else {
            emailFlow = .failed(Self.unreachableMessage); return
        }
        await run(request, email: trimmed)
    }

    func submitCode(_ code: String) async {
        guard case .needsCode(let email) = emailFlow,
              let request = Self.codeRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, email: email, code: code.filter(\.isNumber)) else { return }
        await run(request, email: email, keepsCodeOnFailure: true)
    }

    private func run(_ request: URLRequest, email: String, keepsCodeOnFailure: Bool = false) async {
        let before = emailFlow
        emailFlow = .sending
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            switch Self.outcome(status: (response as? HTTPURLResponse)?.statusCode ?? 0, body: data, email: email) {
            case .signedIn(let session, let confirmed):
                store(session, email: email)
                emailFlow = .signedIn(confirmed: confirmed)
                AccountProfileStore.shared.refresh()
                ShellSettingsRevision.shared.noteChanged()
                if !confirmed { watchConfirmation() }
            case .codeSent:
                emailFlow = .needsCode(email: email)
            case .failed(let message):
                // A wrong code leaves the code field up, with the sentence under it.
                lastErrorText = message
                emailFlow = keepsCodeOnFailure ? before : .failed(message)
            }
        } catch {
            if Self.isCancellation(error) || Task.isCancelled {
                // Back to whatever the form showed before this send (the email step, or the code step).
                emailFlow = Self.stateAfterCancellation(before)
            } else {
                print("🔐 Account request failed: \(Self.describe(error))")
                emailFlow = .failed(Self.unreachableMessage)
            }
        }
    }

    /// Mails the confirmation link again.
    func resendLink() async -> Bool {
        let token = OpenClickyConfiguration.settings.token
        guard !token.isEmpty, let request = Self.resendRequest(backendBaseURL: OpenClickyConfiguration.backendBaseURL, token: token) else { return false }
        let status = ((try? await URLSession.shared.data(for: request))?.1 as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(status) { watchConfirmation(); return true }
        return false
    }

    /// A fresh form starts empty: a finished or failed attempt is forgotten, one in flight is kept.
    func forgetSettledFlow() {
        switch emailFlow {
        case .sending, .needsCode: return
        default: emailFlow = .idle; lastErrorText = nil
        }
    }

    private var confirmationWatch: Task<Void, Never>?
    /// While the email is unconfirmed, asks /billing/me once a minute for 15 minutes, so the
    /// "check your inbox" note disappears soon after the link is clicked.
    func watchConfirmation() {
        confirmationWatch?.cancel()
        confirmationWatch = Task { @MainActor in
            for _ in 0..<15 {
                try? await Task.sleep(nanoseconds: 60 * 1_000_000_000)
                guard !Task.isCancelled else { return }
                if let summary = try? await BillingStatusModel.fetchSummary() {
                    AccountProfileStore.shared.absorb(summary)
                    if summary.confirmed != false { objectWillChange.send(); return }
                }
            }
        }
    }
```

Also set `lastErrorText = nil` as the first line of `run`, so an old message never sits under a new attempt.

`store` changes to take the backend's `Session`:

```swift
    private func store(_ session: Session, email: String) {
        OpenClickyConfiguration.update { settings in
            settings.token = session.access_token
            settings.refreshToken = session.refresh_token
            settings.tokenExpiresAt = Date().timeIntervalSince1970 + session.expires_in
            settings.accountEmail = email
        }
    }
```

`refreshIfNeeded` keeps calling Supabase's `token?grant_type=refresh_token` (anonymous users refresh the same way) and writes through a small adapter: `store(Session(access_token: t.access_token, refresh_token: t.refresh_token, expires_in: t.expires_in), email: t.user?.email ?? settings.accountEmail ?? "")`. Keep `TokenResponse`, `authConfig`, `requestToken` for it; delete `AuthConfig.resetRedirectUrl`. Keep `accountsOpen`, `refreshAccountsOpen`, `offersCreateAccount`. `signOut` also cancels `confirmationWatch` and sets `emailFlow = .idle`. `CompanionManager` or any other caller of a removed method fails to compile — fix each (`grep -rn "signIn(email\|signUp(\|recover(\|signUpState" macos/OpenClicky/OpenClicky`); the UI ones are rewritten in Task 8, so in this task make them compile by switching to `start(email:)`/`emailFlow` minimally.

Also call `watchConfirmation()` from `start()` (the app-launch hook) when `AccountProfileStore`'s last summary said `confirmed == false` — or simpler: in `start()`, `Task { if (try? await BillingStatusModel.fetchSummary())?.confirmed == false { watchConfirmation() } }`.

- [ ] **Step 4: Run tests**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' 2>&1 | tail -30`
Expected: build succeeds; all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add macos/OpenClicky
git commit -m "feat(mac): email-first sign-in — start with an email, a code for existing accounts, no passwords"
```

---

### Task 8: Mac — one email form in onboarding, the sheet, settings and the HUD

**Files:**
- Create: `macos/OpenClicky/OpenClicky/Dictation/UI/EmailAccountForm.swift`
- Modify: `Dictation/UI/AccountSheet.swift`, `Dictation/UI/OnboardingWindow.swift`, `Dictation/UI/SettingsAccountPage.swift`, `BillingStatus.swift` (`NotchAccountSection`)
- Test: `macos/OpenClicky/OpenClickyTests/AccountSheetTests.swift`

**Interfaces:**
- Consumes: `OpenClickyAuthSession.emailFlow/start/submitCode/resendLink/forgetSettledFlow` (Task 7), `BillingSummary.isUnconfirmed` (Task 6).
- Produces: `struct EmailAccountForm: View { init(onSignedIn: @escaping () -> Void, onUseOwnKey: (() -> Void)?) }`; `EmailAccountForm.canSubmitEmail(_:) -> Bool`, `EmailAccountForm.canSubmitCode(_:) -> Bool`; `struct EmailAccountFormContent: View` (stateless, for drawing each state). `AccountSheet(onDone:onUseOwnKey:)` — no `Mode`, no `startIn:`.

- [ ] **Step 1: Write the failing tests** — append to `AccountSheetTests`:

```swift
    @Test func theEmailFormNeedsAnAddressAndTheCodeSixDigits() {
        #expect(!EmailAccountForm.canSubmitEmail("gran"))
        #expect(EmailAccountForm.canSubmitEmail(" gran@example.com "))
        #expect(!EmailAccountForm.canSubmitCode("12345"))
        #expect(EmailAccountForm.canSubmitCode("123 456"))
        #expect(!EmailAccountForm.canSubmitCode("12a456"))
    }

    @Test func noPasswordFieldIsLeftInTheApp() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("OpenClicky")
        let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)!.compactMap { $0 as? URL }.filter { $0.pathExtension == "swift" }
        for file in files {
            let text = try String(contentsOf: file, encoding: .utf8)
            #expect(!text.contains("SecureField"), "\(file.lastPathComponent) still has a password field")
            #expect(!text.contains("forgot password"), "\(file.lastPathComponent) still offers a password reset")
        }
    }
```

(If `NotchComposerTextField(isSecure: true)` is used for API keys elsewhere, the test only looks for `SecureField` and `forgot password`; leave key fields alone. If `SecureField` is legitimately used for API keys in Settings, narrow the test to the files `AccountSheet.swift`, `EmailAccountForm.swift`, `OnboardingWindow.swift`, `BillingStatus.swift`.)

- [ ] **Step 2: Run to verify they fail**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' -only-testing:OpenClickyTests/AccountSheetTests 2>&1 | tail -20`
Expected: build FAILS (`EmailAccountForm` not found).

- [ ] **Step 3: Implement `EmailAccountForm.swift`**

```swift
//
//  EmailAccountForm.swift
//  OpenClicky
//
//  The one account form: an email, then — only if that email already has an account — a 6-digit
//  code from the inbox. A new email is signed in straight away and the confirmation link can be
//  clicked whenever. Used by onboarding and by the account sheet. The view owns the task doing the
//  work and cancels it when it goes away.
//

import SwiftUI

struct EmailAccountForm: View {
    let onSignedIn: () -> Void
    var onUseOwnKey: (() -> Void)? = nil
    @ObservedObject private var auth = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var code = ""
    @State private var work = AccountWorkSlot()

    static func canSubmitEmail(_ raw: String) -> Bool {
        let email = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let at = email.firstIndex(of: "@"), at != email.startIndex else { return false }
        return email[email.index(after: at)...].contains(".")
    }
    static func canSubmitCode(_ raw: String) -> Bool {
        let digits = raw.filter { !$0.isWhitespace }
        return digits.count == 6 && digits.allSatisfy(\.isNumber)
    }

    var body: some View {
        EmailAccountFormContent(state: auth.emailFlow, email: $email, code: $code,
                                onSubmitEmail: submitEmail, onSubmitCode: submitCode,
                                onDifferentEmail: { auth.resetFlow(); code = "" }, onUseOwnKey: onUseOwnKey)
            .onAppear { auth.forgetSettledFlow() }
            .onDisappear { work.cancel() }
            .onChange(of: auth.emailFlow) { _, state in if case .signedIn = state { onSignedIn() } }
    }

    private func submitEmail() {
        guard Self.canSubmitEmail(email), !work.isRunning else { return }
        let address = email
        work.start { await auth.start(email: address) }
    }
    private func submitCode() {
        guard Self.canSubmitCode(code), !work.isRunning else { return }
        let digits = code
        work.start { await auth.submitCode(digits) }
    }
}

/// The form's look for one state, with no state of its own.
struct EmailAccountFormContent: View {
    let state: OpenClickyAuthSession.EmailFlowState
    @Binding var email: String
    @Binding var code: String
    let onSubmitEmail: () -> Void
    let onSubmitCode: () -> Void
    let onDifferentEmail: () -> Void
    var onUseOwnKey: (() -> Void)? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if case .needsCode(let address) = state { codeStep(address) } else { emailStep }
        }
    }

    private var isSending: Bool { state == .sending }

    private var emailStep: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                TextField("your email", text: $email)
                    .textFieldStyle(.roundedBorder).textContentType(.emailAddress)
                    .onSubmit(onSubmitEmail)
                Button(isSending ? "one moment…" : "continue", action: onSubmitEmail)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isSending || !EmailAccountForm.canSubmitEmail(email))
            }
            Text("no password — we'll send a link to confirm it's you.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            if case .failed(let message) = state {
                Text(message).font(Paper.caption).foregroundStyle(Paper.danger).fixedSize(horizontal: false, vertical: true)
            }
            if let onUseOwnKey {
                Button("use my own key instead", action: onUseOwnKey)
                    .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
            }
        }
    }

    private func codeStep(_ address: String) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("you already have an account — we sent a 6-digit code to \(address).")
                .font(Paper.body(13)).foregroundStyle(Paper.ink).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                TextField("123456", text: $code)
                    .textFieldStyle(.roundedBorder).textContentType(.oneTimeCode).frame(width: 120)
                    .onSubmit(onSubmitCode)
                Button(isSending ? "checking…" : "sign in", action: onSubmitCode)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isSending || !EmailAccountForm.canSubmitCode(code))
            }
            if let message = OpenClickyAuthSession.shared.lastErrorText {
                Text(message).font(Paper.caption).foregroundStyle(Paper.danger)
            }
            Button("use a different email", action: onDifferentEmail)
                .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
        }
    }
}
```

Add to `OpenClickyAuthSession` (Task 7's file) the method the "use a different email" button calls — `forgetSettledFlow` deliberately keeps `.needsCode`, this one does not:

```swift
    func resetFlow() { emailFlow = .idle; lastErrorText = nil }
```

`AccountSheet.swift` — replace `AccountSheet`/`AccountSheetContent` with a thin sheet; keep `AccountWorkSlot` and `AccountUsageBar` as they are:

```swift
struct AccountSheet: View {
    let onDone: () -> Void
    var onUseOwnKey: (() -> Void)? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: "your free openclicky account", size: 26)
            Text("polished dictation and spoken answers, on us — up to $10 of use a month once your email is confirmed.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            EmailAccountForm(onSignedIn: onDone, onUseOwnKey: onUseOwnKey)
            Button("not now", action: onDone)
                .buttonStyle(PaperPillButtonStyle(quiet: true)).keyboardShortcut(.cancelAction)
        }
        .padding(28)
        .frame(width: 440, alignment: .leading)
        .background(Paper.background)
    }
}
```

Update the file header comment to match.

`OnboardingWindow.swift` — the `account` step body becomes:

```swift
    private var account: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("your email gets you a free openclicky account — polished dictation and spoken answers, nothing to set up.")
                .font(Paper.body(14)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            if let accountEmail = authSession.accountEmail {
                HStack(spacing: 8) {
                    Image(systemName: "checkmark").font(.system(size: 12, weight: .bold))
                    Text("you're set, \(accountEmail). check your inbox to unlock the full free allowance.").font(Paper.body(14, weight: .medium))
                }
                .foregroundStyle(Paper.success).padding(.top, 6)
            } else if OpenClickyAuthSession.offersCreateAccount(accountsOpen: authSession.accountsOpen) {
                EmailAccountForm(onSignedIn: {}, onUseOwnKey: { companionManager.showDictationWindow(settingsPage: .account) })
            } else {
                Text(AccountLimitError.accountsFull.message).font(Paper.body(13)).foregroundStyle(Paper.ink)
                accountChoice("key", "use my own key", "if you already have an openai key.") { companionManager.showDictationWindow(settingsPage: .account) }
            }
            if authSession.accountEmail == nil {
                Button("skip — keep everything on this mac") { finish() }
                    .buttonStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.inkTertiary).pointerCursor().padding(.top, 2)
            }
        }
        .task { await authSession.refreshAccountsOpen() }
    }
```

Delete `accountSheetMode` and its `.sheet(item:)` from the onboarding window (keep `accountChoice` — still used). Update the file's header comment line about "the offer of a free account".

`SettingsAccountPage.swift`:
- `sheetMode: AccountSheet.Mode?` becomes `@State private var showsAccountSheet = false`; the sheet is `AccountSheet(onDone: { showsAccountSheet = false }, onUseOwnKey: { showsAccountSheet = false })`; the two buttons that opened `.create` / `.signIn` become one "set up or sign in with your email" button.
- Search keywords drop `"password"` and add `"code"`.
- Signed-in card: under the email, when `model.summary?.isUnconfirmed == true`, show `Text("unconfirmed — check your inbox for the link.")` plus a `Button("resend link")` that runs `await OpenClickyAuthSession.shared.resendLink()` and then shows `"sent — check your inbox."` or `"couldn't send it — try again in a minute."` (a `@State var resendNote: String?`).

`BillingStatus.swift` `NotchAccountSection`:
- Delete `email`, `password`, `isSigningIn`, `signInFailureText`, `signInForm`, `credentialField`, `signIn()`.
- When `!OpenClickyConfiguration.isConfigured`: `action("person.crop.circle.badge.plus", "free account", "set it up with your email in settings", openAccountPage)`.

- [ ] **Step 4: Run tests and build**

Run: `cd macos/OpenClicky && pkill -x OpenClicky; xcodebuild test -project OpenClicky.xcodeproj -scheme OpenClicky -destination 'platform=macOS' 2>&1 | tail -30`
Expected: all PASS, no warnings introduced in the touched files (`xcodebuild … 2>&1 | grep -E "warning:.*(EmailAccountForm|AccountSheet|OnboardingWindow|SettingsAccountPage|BillingStatus)"` prints nothing).

- [ ] **Step 5: Look at it.** Build with `./scripts/release.sh --dev` (see the release procedure memory/doc), launch, open onboarding's account step and Settings → account, and take window screenshots by window id. Check: the email field and continue button fit at 440 pt; the code step's sentence wraps; nothing overflows. Fix layout before committing.

- [ ] **Step 6: Commit**

```bash
git add macos/OpenClicky
git commit -m "feat(mac): one email form for onboarding, the account sheet and settings; HUD points to it"
```

---

### Task 9: Supabase settings, live end-to-end, runbook

This task runs against the dedicated Supabase Cloud project (`hcmnepxtfngtrlbknumu`). Secrets come from `backend/.supabase-cloud.secrets` and `backend/.hosted.vars`; never print them.

**Files:**
- Modify: `docs/accounts-runbook.md`

- [ ] **Step 1: Apply the schema.** `set -a; . backend/.supabase-cloud.secrets; set +a; psql "$SUPABASE_DB_URL" -v ON_ERROR_STOP=1 -f backend/supabase/schema.sql`, then run it a second time to prove it is idempotent. Expected: both runs succeed. Then `psql "$SUPABASE_DB_URL" -c "\df public.oc_*"` lists one signature per function (no stale overloads).

- [ ] **Step 2: Auth settings** via the Management API (`PATCH https://api.supabase.com/v1/projects/$SUPABASE_PROJECT_REF/config/auth`, `Authorization: Bearer $SUPABASE_ACCESS_TOKEN`) with:

```json
{
  "external_anonymous_users_enabled": true,
  "rate_limit_anonymous_users": 300,
  "mailer_otp_length": 6,
  "mailer_otp_exp": 900,
  "mailer_subjects_email_change": "Confirm your OpenClicky email",
  "mailer_templates_email_change_content": "<p>Hi! Confirm this email for your free OpenClicky account:</p><p><a href=\"{{ .ConfirmationURL }}\">Confirm my email</a></p><p>If you didn't ask for this, ignore this email.</p>",
  "mailer_subjects_magic_link": "Your OpenClicky code",
  "mailer_templates_magic_link_content": "<p>Your OpenClicky sign-in code is</p><h2>{{ .Token }}</h2><p>It works for 15 minutes. If you didn't ask for it, ignore this email.</p>",
  "mailer_secure_email_change_enabled": false
}
```

Then `GET` the config and check those fields read back.

- [ ] **Step 3: Live e2e on a local backend** (port 8790, `.hosted.vars` exported, `ACCOUNTS_OPEN=true`, `GUEST_TOTAL_USD=0.002` so the cap is reachable in a few calls). With `EMAIL=prasanth+ocguest@flowsxr.com` and `DEVICE=$(printf 'openclicky-device-v1:e2e' | shasum -a 256 | cut -d' ' -f1)`:
  1. `POST /auth/start` → `signed_in`, `confirmed:false`; `/billing/me` shows `confirmed:false`, `guestLimitUsd` 0 (rounded), email masked.
  2. `/v1/polish` a few times until `402 confirm_email`; check `oc_usage_events` rows exist for the user.
  3. Repeat `/auth/start` with the same email + device → new guest; old auth user gone (`GET /auth/v1/admin/users/<old>` → 404), old row has `replaced_at`, and the new guest is still refused (`confirm_email`) because the Mac's pool is spent.
  4. Confirm the new guest without email: `GET /auth/v1/admin/users/<id>` shows `is_anonymous: true`; ask Prasanth to click the link in the `+ocguest` inbox (or, if he is away, confirm via admin `PUT /auth/v1/admin/users/<id> {"email_confirm": true}` and check `is_anonymous` flips; record which was used). Then `/billing/me` → `confirmed:true`, and `/v1/polish` works again.
  5. `/auth/start` with the same email from a different device hash → `code_sent`. Code verification needs the inbox: if Prasanth is available, have him read the code and run `/auth/code`; otherwise record this step as not run live.
  6. Clean up: delete the test auth users and their `oc_accounts`, `oc_usage_events`, `oc_reservations` rows; stop the backend; remove temp files.

- [ ] **Step 4: Runbook.** Update `docs/accounts-runbook.md`: Supabase Cloud project (ref, region) replaces the shared self-hosted instance; email-first flow; new env vars (`GUEST_TOTAL_USD`, `GUEST_DAYS`, `GUEST_TTS_CHARS`, `MAX_ACCOUNTS_PER_DEVICE`); `ACCOUNT_RESET_REDIRECT_URL` removed; the Step 2 auth settings; `admin add <email>` and weekly `admin prune`; rollout order (schema → settings → deploy with `GRANT_ACCOUNTS=true`, `ACCOUNTS_OPEN=false` → `admin add` → release 0.8.0 → `ACCOUNTS_OPEN=true` → revoke the access token).

- [ ] **Step 5: Commit**

```bash
git add docs/accounts-runbook.md
git commit -m "docs: accounts runbook — Supabase Cloud, email-first guests, prune"
```
