#!/usr/bin/env node
// Invite-only accounts for OpenClicky's hosted backend. Talks to Supabase directly (Auth admin API +
// the oc_* billing tables) with the service key from backend/.dev.vars; nothing here goes through
// the backend.
//
//   npm run admin -w backend -- invite <email> [--password <pw>] [--credits 1000]   create the user (or reuse) + allowance
//   npm run admin -w backend -- limit <email> --credits <n>                         change the monthly allowance
//   npm run admin -w backend -- revoke <email>                                      block the account (status canceled)
//   npm run admin -w backend -- restore <email>                                     unblock
//   npm run admin -w backend -- list                                                everyone with a subscription row
//   npm run admin -w backend -- usage [--month 2026-09]                             credits used per user this month
//
// The allowance resets on calendar months (billing.ts). Passwords are printed once; send them out of band.
import { config as loadDotenv } from "dotenv";
import { randomBytes } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
loadDotenv({ path: path.resolve(here, "..", ".dev.vars"), quiet: true });
loadDotenv({ path: path.resolve(here, "..", "..", ".env"), quiet: true });

const SUPABASE_URL = (process.env.SUPABASE_URL ?? "").replace(/\/+$/, "");
const SERVICE_KEY = process.env.SUPABASE_SERVICE_KEY;
if (!SUPABASE_URL || !SERVICE_KEY) {
  console.error("SUPABASE_URL and SUPABASE_SERVICE_KEY are required (backend/.dev.vars)");
  process.exit(1);
}
const headers = { apikey: SERVICE_KEY, authorization: `Bearer ${SERVICE_KEY}`, "content-type": "application/json" };

const args = process.argv.slice(2);
const command = args[0];
const positional = args.slice(1).filter((a, i, all) => !a.startsWith("--") && !(i > 0 && all[i - 1].startsWith("--")));
/** A valueless flag such as `--yes`; `opt` expects a value and would miss it. */
const flag = (name) => args.includes(`--${name}`);
const opt = (name, def) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};

async function api(method, url, body) {
  const res = await fetch(url, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${url} → ${res.status}: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}
const rest = (table, query = "") => `${SUPABASE_URL}/rest/v1/${table}${query ? "?" + query : ""}`;

/** All auth users (paged); small deployments only. */
async function listUsers() {
  const users = [];
  for (let page = 1; page < 50; page++) {
    const json = await api("GET", `${SUPABASE_URL}/auth/v1/admin/users?page=${page}&per_page=200`);
    const batch = json.users ?? [];
    users.push(...batch);
    if (batch.length < 200) break;
  }
  return users;
}

async function findUser(email) {
  const users = await listUsers();
  return users.find((u) => (u.email ?? "").toLowerCase() === email.toLowerCase());
}

async function requireUser(email) {
  const user = await findUser(email);
  if (!user) throw new Error(`no account for ${email}`);
  return user;
}

function periodBounds(month) {
  const [y, m] = month ? month.split("-").map(Number) : [new Date().getUTCFullYear(), new Date().getUTCMonth() + 1];
  return { start: new Date(Date.UTC(y, m - 1, 1)).toISOString(), end: new Date(Date.UTC(y, m, 1)).toISOString(), label: `${y}-${String(m).padStart(2, "0")}` };
}

async function upsertSubscription(userId, fields) {
  const now = new Date().toISOString();
  const row = {
    user_id: userId,
    plan_id: "invite",
    status: "active",
    current_period_start: now,
    current_period_end: new Date(Date.now() + 365 * 86400_000).toISOString(),
    stripe_customer_id: null,
    stripe_subscription_id: null,
    updated_at: now,
    ...fields,
  };
  await api("POST", rest("oc_subscriptions", "on_conflict=user_id"), row).catch(async (e) => {
    // Upsert needs the merge-duplicates preference.
    const res = await fetch(rest("oc_subscriptions", "on_conflict=user_id"), {
      method: "POST",
      headers: { ...headers, prefer: "resolution=merge-duplicates" },
      body: JSON.stringify(row),
    });
    if (!res.ok) throw new Error(`upsert failed (${res.status}): ${(await res.text()).slice(0, 300)}\n(first attempt: ${e.message})`);
  });
}

async function patchSubscription(userId, fields) {
  const res = await fetch(rest("oc_subscriptions", `user_id=eq.${encodeURIComponent(userId)}`), {
    method: "PATCH",
    headers: { ...headers, prefer: "return=representation" },
    body: JSON.stringify({ ...fields, updated_at: new Date().toISOString() }),
  });
  if (!res.ok) throw new Error(`update failed (${res.status}): ${(await res.text()).slice(0, 300)}`);
  const rows = await res.json();
  if (rows.length === 0) throw new Error("no subscription row for that user (run invite first)");
}

/** One line from the terminal. Returns "" when stdin is not a TTY, so a piped run never hangs. */
function promptLine(question) {
  if (!process.stdin.isTTY) return Promise.resolve("");
  process.stdout.write(question);
  return new Promise((resolve) => {
    process.stdin.setEncoding("utf8");
    process.stdin.once("data", (d) => {
      process.stdin.pause();
      resolve(String(d));
    });
    process.stdin.resume();
  });
}

const commands = {
  async invite() {
    const email = positional[0];
    if (!email) throw new Error("usage: invite <email> [--password <pw>] [--credits <n>]");
    const credits = Number(opt("credits", "1000"));
    let user = await findUser(email);
    let password = opt("password");
    if (!user) {
      password ??= randomBytes(9).toString("base64url");
      user = await api("POST", `${SUPABASE_URL}/auth/v1/admin/users`, { email, password, email_confirm: true });
      console.log(`created ${email} (id ${user.id})`);
      console.log(`password: ${password}`);
    } else {
      console.log(`${email} already exists (id ${user.id})${password ? "; password left unchanged" : ""}`);
    }
    await upsertSubscription(user.id, { monthly_credits_override: credits, status: "active" });
    console.log(`allowance: ${credits} credits per calendar month`);
  },

  async limit() {
    const email = positional[0];
    const credits = Number(opt("credits"));
    if (!email || !Number.isFinite(credits)) throw new Error("usage: limit <email> --credits <n>");
    const user = await requireUser(email);
    await patchSubscription(user.id, { monthly_credits_override: credits });
    console.log(`${email}: ${credits} credits per month`);
  },

  async revoke() {
    const email = positional[0];
    if (!email) throw new Error("usage: revoke <email>");
    const user = await requireUser(email);
    await patchSubscription(user.id, { status: "canceled" });
    console.log(`${email}: blocked (subscription_inactive)`);
  },

  async restore() {
    const email = positional[0];
    if (!email) throw new Error("usage: restore <email>");
    const user = await requireUser(email);
    await patchSubscription(user.id, { status: "active" });
    console.log(`${email}: active`);
  },

  async remove() {
    const email = positional[0];
    if (!email) throw new Error("usage: remove <email> (add --yes to skip the confirmation)");
    const user = await requireUser(email);
    // Deleting an auth user cannot be undone, and the command sits one letter away from `revoke`,
    // which only suspends. Make the operator type the address back unless they opted out.
    if (!flag("yes")) {
      const typed = await promptLine(`permanently delete ${email} and its subscription row? type the email to confirm: `);
      if (typed.trim() !== email) {
        console.log("not confirmed; nothing was deleted");
        return;
      }
    }
    const res = await fetch(rest("oc_subscriptions", `user_id=eq.${encodeURIComponent(user.id)}`), { method: "DELETE", headers });
    if (!res.ok) throw new Error(`could not delete the subscription row (${res.status})`);
    await api("DELETE", `${SUPABASE_URL}/auth/v1/admin/users/${user.id}`);
    console.log(`${email}: account deleted (usage history kept)`);
  },

  async list() {
    const [users, subs] = await Promise.all([listUsers(), api("GET", rest("oc_subscriptions", "select=*"))]);
    const byId = new Map(users.map((u) => [u.id, u]));
    for (const s of subs) {
      const u = byId.get(s.user_id);
      console.log(`${(u?.email ?? s.user_id).padEnd(36)} ${s.plan_id.padEnd(8)} ${s.status.padEnd(9)} ${s.monthly_credits_override ?? "(plan)"} credits/month`);
    }
    if (subs.length === 0) console.log("(no subscription rows)");
  },

  async usage() {
    const { start, end, label } = periodBounds(opt("month"));
    const [users, events] = await Promise.all([
      listUsers(),
      api("GET", rest("oc_usage_events", `ts=gte.${encodeURIComponent(start)}&ts=lt.${encodeURIComponent(end)}&select=user_id,route,credits`)),
    ]);
    const byId = new Map(users.map((u) => [u.id, u]));
    const totals = new Map();
    for (const e of events) {
      const t = totals.get(e.user_id) ?? { credits: 0, calls: 0, routes: new Map() };
      t.credits += Number(e.credits);
      t.calls += 1;
      t.routes.set(e.route, (t.routes.get(e.route) ?? 0) + Number(e.credits));
      totals.set(e.user_id, t);
    }
    console.log(`usage for ${label}`);
    for (const [userId, t] of [...totals.entries()].sort((a, b) => b[1].credits - a[1].credits)) {
      const routes = [...t.routes.entries()].map(([r, c]) => `${r} ${c}`).join(", ");
      console.log(`${(byId.get(userId)?.email ?? userId).padEnd(36)} ${String(t.credits).padStart(6)} credits  ${t.calls} calls  (${routes})`);
    }
    if (totals.size === 0) console.log("(no usage)");
  },
};

if (!commands[command]) {
  console.error("commands: invite, limit, revoke, restore, remove (destructive), list, usage");
  process.exit(2);
}
commands[command]().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
