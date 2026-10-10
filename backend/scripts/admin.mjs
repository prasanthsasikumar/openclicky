#!/usr/bin/env node
// Accounts and budget for OpenClicky's hosted backend. Talks to Supabase directly (Auth admin API +
// the oc_* tables) with the service key from backend/.dev.vars; nothing here goes through the backend.
// People sign up in the app, which creates their oc_accounts row; the Supabase auth is shared with
// other FlowsXR products, so the only auth users this script deletes are anonymous guest logins (prune).
//
//   npm run admin -w backend -- list                          everyone with an oc_accounts row
//   npm run admin -w backend -- budget                        this month's spend, characters, account count, top 10
//   npm run admin -w backend -- add <email>                   make a confirmed account for this email (creates the login if missing)
//   npm run admin -w backend -- prune [--yes]                 delete stale guest logins (replaced, or older than GUEST_DAYS, default 14)
//   npm run admin -w backend -- limit <email> --usd N         monthly limit for one person
//   npm run admin -w backend -- daily <email> --usd N         daily limit for one person
//   npm run admin -w backend -- block <email> | unblock <email>
//   npm run admin -w backend -- remove <email> [--yes]        delete the oc_accounts row only (auth user untouched)
//   npm run admin -w backend -- max-accounts N                cap on confirmed accounts
//
// limit/daily/block/unblock create the oc_accounts row when it is missing.
import { config as loadDotenv } from "dotenv";
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

async function api(method, url, body, extraHeaders) {
  const res = await fetch(url, { method, headers: { ...headers, ...extraHeaders }, body: body ? JSON.stringify(body) : undefined });
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

const MICRO = 1_000_000;
const usd = (micro) => `$${(Number(micro) / MICRO).toFixed(2)}`;
const usdOpt = () => {
  const v = Number(opt("usd"));
  if (!opt("usd") || !Number.isFinite(v) || v < 0) throw new Error("give an amount: --usd 5");
  return Math.round(v * MICRO);
};
const needEmail = (usage) => {
  if (!positional[0]) throw new Error(`usage: ${usage}`);
  return positional[0];
};
const monthStart = () => new Date(Date.UTC(new Date().getUTCFullYear(), new Date().getUTCMonth(), 1)).toISOString();
/** Every row of a table query; PostgREST caps one response at 1000 rows, so page by id until a short page. */
async function fetchAll(table, query) {
  const rows = [];
  for (let offset = 0; ; offset += 1000) {
    const page = await api("GET", rest(table, `${query}&order=id&limit=1000&offset=${offset}`));
    rows.push(...page);
    if (page.length < 1000) return rows;
  }
}
const upsertAccount = (userId, fields) =>
  api("POST", rest("oc_accounts", "on_conflict=user_id"), { user_id: userId, ...fields }, { prefer: "resolution=merge-duplicates" });
const maxAccounts = async () => (await api("GET", rest("oc_settings", "select=max_accounts")))?.[0]?.max_accounts ?? 100;

const commands = {
  async budget() {
    const [events, accounts, users, cap] = await Promise.all([
      fetchAll("oc_usage_events", `ts=gte.${encodeURIComponent(monthStart())}&select=user_id,cost_micro_usd,route,characters`),
      api("GET", rest("oc_accounts", "select=user_id,replaced_at")),
      listUsers(),
      maxAccounts(),
    ]);
    const perUser = new Map();
    let total = 0;
    let chars = 0;
    for (const r of events) {
      total += Number(r.cost_micro_usd);
      if (r.route === "/tts") chars += Number(r.characters);
      perUser.set(r.user_id, (perUser.get(r.user_id) ?? 0) + Number(r.cost_micro_usd));
    }
    // The auth is shared with other products: only OpenClicky accounts count, and only confirmed ones fill the cap.
    const accountIds = new Set(accounts.map((a) => a.user_id));
    const byId = new Map(users.map((u) => [u.id, u]));
    const confirmed = [...accountIds].filter((id) => byId.has(id) && !byId.get(id).is_anonymous).length;
    const guests = accounts.filter((a) => byId.get(a.user_id)?.is_anonymous && !a.replaced_at).length;
    const limit = Number(process.env.GLOBAL_MONTHLY_BUDGET_USD ?? 1000);
    console.log(`this month: ${usd(total)} of $${limit} · ${chars} spoken characters · accounts ${confirmed} confirmed of ${cap} · ${guests} live guests`);
    [...perUser.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 10)
      .forEach(([id, micro]) => console.log(`  ${usd(micro)}  ${byId.get(id)?.email ?? id}`));
  },

  async add() {
    const email = needEmail("add <email>").toLowerCase();
    let user = await findUser(email);
    if (!user) user = await api("POST", `${SUPABASE_URL}/auth/v1/admin/users`, { email, email_confirm: true });
    await upsertAccount(user.id, { email, confirmed_at: new Date().toISOString() });
    console.log(`${email}: confirmed account ready (default limits) — sign in on the Mac with this email and the emailed code`);
    console.log("note: this login may be shared with another FlowsXR product; the account now counts toward the max-accounts cap.");
  },

  async prune() {
    const days = Number(process.env.GUEST_DAYS ?? 14);
    const cutoff = Date.now() - days * 86_400_000;
    const rows = await api("GET", rest("oc_accounts", "confirmed_at=is.null&select=user_id,email,created_at,replaced_at"));
    const users = new Map((await listUsers()).map((u) => [u.id, u]));
    // Only anonymous logins are ever deleted; a confirmed (non-anonymous) user is never touched.
    const doomed = rows.filter((r) => users.get(r.user_id)?.is_anonymous === true && (r.replaced_at || Date.parse(r.created_at) < cutoff));
    console.log(`${doomed.length} guest login(s) to delete (replaced, or unconfirmed for ${days}+ days); their usage rows stay`);
    if (!doomed.length) return;
    if (!flag("yes")) {
      const typed = await promptLine("type yes to delete them: ");
      if (typed.trim() !== "yes") {
        console.log("not confirmed; nothing was changed");
        return;
      }
    }
    for (const r of doomed) {
      if (!r.replaced_at) await api("PATCH", rest("oc_accounts", `user_id=eq.${encodeURIComponent(r.user_id)}`), { replaced_at: new Date().toISOString() });
      await api("DELETE", `${SUPABASE_URL}/auth/v1/admin/users/${encodeURIComponent(r.user_id)}`);
    }
    console.log("done");
  },

  async limit() {
    const email = needEmail("limit <email> --usd N");
    const micro = usdOpt();
    const user = await requireUser(email);
    await upsertAccount(user.id, { monthly_limit_micro_usd: micro });
    console.log(`monthly limit set for ${email}: ${usd(micro)}`);
  },

  async daily() {
    const email = needEmail("daily <email> --usd N");
    const micro = usdOpt();
    const user = await requireUser(email);
    await upsertAccount(user.id, { daily_limit_micro_usd: micro });
    console.log(`daily limit set for ${email}: ${usd(micro)}`);
  },

  async block() {
    const email = needEmail("block <email>");
    const user = await requireUser(email);
    await upsertAccount(user.id, { blocked: true });
    console.log(`blocked ${email}`);
  },

  async unblock() {
    const email = needEmail("unblock <email>");
    const user = await requireUser(email);
    await upsertAccount(user.id, { blocked: false });
    console.log(`unblocked ${email}`);
  },

  async remove() {
    const email = needEmail("remove <email> (add --yes to skip the confirmation)");
    const user = await requireUser(email);
    if (!flag("yes")) {
      const typed = await promptLine(`remove the OpenClicky account row for ${email}? the login itself stays. type the email to confirm: `);
      if (typed.trim() !== email) {
        console.log("not confirmed; nothing was changed");
        return;
      }
    }
    const deleted = await api("DELETE", rest("oc_accounts", `user_id=eq.${encodeURIComponent(user.id)}`), undefined, { prefer: "return=representation" });
    if (!deleted?.length) {
      console.log(`${email} had no OpenClicky account row — nothing removed`);
      return;
    }
    console.log(`${email}: OpenClicky account row deleted. The auth login was NOT deleted (it is shared with other FlowsXR products) and usage history is kept.`);
  },

  async "max-accounts"() {
    const n = Number(args[1]);
    if (!Number.isInteger(n) || n < 1) throw new Error("max-accounts needs a whole number");
    const updated = await api("PATCH", rest("oc_settings", "id=eq.true"), { max_accounts: n }, { prefer: "return=representation" });
    if (!updated?.length) throw new Error("oc_settings has no row; apply backend/supabase/schema.sql first");
    console.log(`max accounts: ${n}`);
  },

  async invite() {
    console.log("invites are retired: people sign up in the app. Use `limit`/`daily` to give someone more, or `add` for an existing login.");
  },

  async list() {
    const [users, accounts, events] = await Promise.all([
      listUsers(),
      api("GET", rest("oc_accounts", "select=*")),
      fetchAll("oc_usage_events", `ts=gte.${encodeURIComponent(monthStart())}&select=user_id,cost_micro_usd`),
    ]);
    const byId = new Map(users.map((u) => [u.id, u]));
    const spent = new Map();
    for (const e of events) spent.set(e.user_id, (spent.get(e.user_id) ?? 0) + Number(e.cost_micro_usd));
    const lim = (m) => (m == null ? "(default)" : usd(m));
    for (const a of accounts) {
      const email = byId.get(a.user_id)?.email ?? a.user_id;
      console.log(`${email.padEnd(36)} ${a.blocked ? "BLOCKED" : "active "} month ${lim(a.monthly_limit_micro_usd)}  day ${lim(a.daily_limit_micro_usd)}  spent ${usd(spent.get(a.user_id) ?? 0)}`);
    }
    if (accounts.length === 0) console.log("(no accounts)");
  },
};

if (!commands[command]) {
  console.error("commands: list, budget, add <email>, limit <email> --usd N, daily <email> --usd N, block <email>, unblock <email>, remove <email>, prune [--yes], max-accounts N");
  process.exit(2);
}
commands[command]().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
