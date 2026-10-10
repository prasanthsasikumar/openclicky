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
