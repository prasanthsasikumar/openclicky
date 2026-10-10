import { describe, it, expect, vi, afterEach } from "vitest";
import { SignJWT } from "jose";
import { issueSessionToken } from "../src/auth.js";
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
  const calls: { method: string; url: string; body?: any; auth?: string }[] = [];
  let insertFailures = opts.insertFails ?? 0;
  vi.stubGlobal("fetch", vi.fn(async (input: any, init: any = {}) => {
    const url = String(input), method = init.method ?? "GET";
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ method, url, body, auth: init.headers?.Authorization });
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
  it("an attach failure leaves a live guest on the Mac untouched", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const s = supabase({ rows: [{ user_id: "u-guest", email: "old@example.com", device_hash: device, confirmed_at: null, replaced_at: null }], users: { "u-guest": { is_anonymous: true } }, attach: () => new Response('{"msg":"boom"}', { status: 500 }) });
    const res = await start({ email: "gran@example.com", device });
    expect(res.status).toBe(502);
    expect(s.rows.find((r) => r.user_id === "u-guest")!.replaced_at).toBeNull();
    expect(s.users["u-guest"]).toBeDefined();
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
  it("creates the row for a confirmed auth user that has none, when accounts are open", async () => {
    const s = supabase();
    const res = await code({ email: "gran@example.com", code: "123456" });
    expect((await res.json()).status).toBe("signed_in");
    expect(s.rows).toContainEqual(expect.objectContaining({ user_id: "u-old", email: "gran@example.com" }));
    expect(s.rows[0].confirmed_at).not.toBeNull();
  });
  it("402 accounts_full, no row and no session, when a row-less user arrives and accounts are closed", async () => {
    const s = supabase({ open: false });
    const res = await code({ email: "gran@example.com", code: "123456" });
    expect(res.status).toBe(402);
    expect(await res.json()).toEqual({ error: "accounts_full" });
    expect(s.rows).toHaveLength(0);
    const s2 = supabase();
    const res2 = await createApp({ log: null }).request("/auth/code", post({ email: "gran@example.com", code: "123456" }, "6.6.6.6"), { ...env, ACCOUNTS_OPEN: "false" });
    expect(res2.status).toBe(402);
    expect(JSON.stringify(await res2.json())).not.toContain("access_token");
    expect(s2.rows).toHaveLength(0);
  });
  it("a row-less user whose insert fails gets 502 and no session", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    supabase({ insertFails: 1 });
    const res = await code({ email: "gran@example.com", code: "123456" });
    expect(res.status).toBe(502);
    expect(JSON.stringify(await res.json())).not.toContain("access_token");
  });
  it("429 slow_down on the 11th call from one IP in an hour", async () => {
    supabase({ rows: [{ user_id: "u-old", email: "gran@example.com", device_hash: device, confirmed_at: "2026-10-01T00:00:00Z", replaced_at: null }] });
    const app = createApp({ log: null });
    for (let i = 0; i < 10; i++) expect((await app.request("/auth/code", post({ email: "gran@example.com", code: "123456" }, "7.7.7.7"), env)).status).toBe(200);
    const res = await app.request("/auth/code", post({ email: "gran@example.com", code: "123456" }, "7.7.7.7"), env);
    expect(res.status).toBe(429);
    expect(await res.json()).toEqual({ error: "slow_down" });
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

  const token = () => new SignJWT({ role: "authenticated" }).setProtectedHeader({ alg: "HS256" }).setAudience("authenticated").setSubject("u-guest").setIssuedAt().setExpirationTime("10m").sign(new TextEncoder().encode(env.SUPABASE_JWT_SECRET));
  const resend = async () => createApp({ log: null }).request("/auth/resend", { method: "POST", headers: { authorization: `Bearer ${await token()}` } }, env);
  const guestRow: Row = { user_id: "u-guest", email: "gran@example.com", device_hash: device, confirmed_at: null, replaced_at: null };
  it("re-sends the confirmation link for a guest, attaching with the caller's own token", async () => {
    const jwt = await token();
    const s = supabase({ rows: [guestRow], users: { "u-guest": { is_anonymous: true } } });
    const res = await createApp({ log: null }).request("/auth/resend", { method: "POST", headers: { authorization: `Bearer ${jwt}` } }, env);
    expect(await res.json()).toEqual({ status: "sent" });
    const attach = s.calls.find((c) => c.url.includes("/auth/v1/user") && c.method === "PUT")!;
    expect(attach.body).toEqual({ email: "gran@example.com" });
    expect(attach.auth).toBe(`Bearer ${jwt}`);
  });
  it("says already_confirmed when the user is no longer anonymous", async () => {
    const s = supabase({ rows: [guestRow], users: { "u-guest": { is_anonymous: false } } });
    expect(await (await resend()).json()).toEqual({ status: "already_confirmed" });
    expect(s.calls.some((c) => c.url.includes("/auth/v1/user"))).toBe(false);
  });
  it("429 on the 4th resend for one user", async () => {
    supabase({ rows: [guestRow], users: { "u-guest": { is_anonymous: true } } });
    const jwt = await token();
    const app = createApp({ log: null });
    const go = () => app.request("/auth/resend", { method: "POST", headers: { authorization: `Bearer ${jwt}` } }, env);
    for (let i = 0; i < 3; i++) expect((await go()).status).toBe(200);
    const res = await go();
    expect(res.status).toBe(429);
    expect(await res.json()).toEqual({ error: "slow_down" });
  });
  it("a backend session token gets 400 bad_request without calling GoTrue", async () => {
    const s = supabase();
    const { token: st } = await issueSessionToken({ sub: "u-guest" }, { ...env, SESSION_TOKEN_SECRET: "s".repeat(40) });
    const res = await createApp({ log: null }).request("/auth/resend", { method: "POST", headers: { authorization: `Bearer ${st}` } }, { ...env, SESSION_TOKEN_SECRET: "s".repeat(40) });
    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({ error: "bad_request" });
    expect(s.calls).toHaveLength(0);
  });
});
