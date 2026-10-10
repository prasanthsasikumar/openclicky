import { Hono, type Context, type MiddlewareHandler } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, verifySupabaseJwt, issueSessionToken, bearerFrom, AuthError, type Principal } from "./auth.js";
import { proxyOpenAI, proxyAnthropic, createRealtimeSession, transcribeAudio, synthesizeSpeech, assemblyAiToken } from "./proxy.js";
import { SKILLS_MANIFEST } from "./skillsManifest.js";
import { createSkill } from "./skillsCreate.js";
import { requestLogger, type LogSink } from "./log.js";
import { SupabaseBillingStore, type BillingStore, type BillingContext } from "./billing.js";
import { SupabaseRest } from "./db.js";
import { requireAccount, accountSummary, type AccountContext } from "./account.js";
import { proxyAnthropicOnGrant } from "./anthropicGrant.js";
import { polishTake } from "./polish.js";
import { speakOnGrant } from "./tts.js";
import type { Purpose } from "./modelPolicy.js";
import { SupabaseSpendLedger, type SpendLedger } from "./ledger.js";
import { handleStripeWebhook, createCheckoutSession, createPortalSession } from "./stripe.js";

export interface AppOptions {
  /** Structured request log sink (default: JSON lines on stdout). Pass `null` to disable. */
  log?: LogSink | null;
  /** Billing store (tests pass MemoryBillingStore). Default: Supabase when SUPABASE_URL + SUPABASE_SERVICE_KEY are set, else none. */
  billingStore?: BillingStore;
  /** Spend ledger (tests pass MemorySpendLedger). Default: Supabase when SUPABASE_URL + SUPABASE_SERVICE_KEY are set, else none. */
  spendLedger?: SpendLedger;
}

type Variables = { principal: Principal; billing: BillingContext };

/**
 * OpenClicky backend: the key-holding proxy.
 * Runs unchanged under Node (`src/node.ts`) and Cloudflare Workers (`wrangler dev`, default export).
 *
 * Two ways to pay for model calls, both through here: a request that carries the user's own
 * provider keys (`x-openclicky-openai-key`, see keys.ts) runs on those and is never metered; any
 * other request runs on the backend's keys under the user's plan (billing.ts, Stripe in stripe.ts).
 */
export function createApp(options: AppOptions = {}) {
  const app = new Hono<{ Variables: Variables }>();
  if (options.log !== null) app.use("*", requestLogger(options.log));

  // The billing store is resolved lazily from the request env (Workers bindings are per request).
  let resolvedStore: BillingStore | undefined | null = options.billingStore ?? null;
  const storeFor = (c: Context): BillingStore | undefined => {
    if (resolvedStore !== null) return resolvedStore;
    const env = getEnv(c);
    resolvedStore = env.SUPABASE_URL && env.SUPABASE_SERVICE_KEY ? new SupabaseBillingStore(new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY)) : undefined;
    if (!resolvedStore) console.warn("billing: no SUPABASE_SERVICE_KEY; requests are not metered");
    return resolvedStore;
  };
  let resolvedLedger: SpendLedger | undefined | null = options.spendLedger ?? null;
  const ledgerFor = (c: Context): SpendLedger | undefined => {
    if (resolvedLedger !== null) return resolvedLedger;
    const env = getEnv(c);
    resolvedLedger = env.SUPABASE_URL && env.SUPABASE_SERVICE_KEY ? new SupabaseSpendLedger(new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY)) : undefined;
    return resolvedLedger;
  };
  /**
   * A backend with no Supabase at all is a deliberate self-hosted setup and runs unmetered. A
   * backend that names a Supabase project but has no service key is misconfigured, and treating
   * that as "unmetered" silently gives every request away for free — so metered routes refuse
   * instead of failing open.
   */
  const billingIsMisconfigured = (c: Context): boolean => {
    const env = getEnv(c);
    return Boolean(env.SUPABASE_URL) && !env.SUPABASE_SERVICE_KEY;
  };
  const gate: MiddlewareHandler<{ Variables: Variables }> = async (c, next) => {
    if (billingIsMisconfigured(c)) {
      console.error("billing: SUPABASE_URL is set but SUPABASE_SERVICE_KEY is missing; refusing metered requests");
      return c.json({ error: "billing is not configured on this backend" }, 503);
    }
    return requireAccount(ledgerFor)(c, next);
  };
  const withStore = (handler: (c: Context, store: BillingStore) => Promise<Response>) => (c: Context) => {
    const store = storeFor(c);
    return store ? handler(c, store) : c.json({ error: "billing store not configured" }, 503);
  };
  /** BYOK and unmetered backends keep the plain proxy; grant requests go through the ledger. */
  const grantOr = (c: Context, purpose: Purpose, plain: () => Promise<Response>) => {
    const ledger = ledgerFor(c);
    const account = c.get("account" as never) as AccountContext | undefined;
    return ledger && account && !account.byok ? proxyAnthropicOnGrant(c, ledger, purpose) : plain();
  };

  app.get("/health", (c) => c.json({ ok: true }));

  // What a client needs to sign in with email + password (Supabase Auth): public by design, so an
  // installed app only has to know the backend URL. 404 when the backend has no Supabase configured.
  app.get("/auth/config", async (c) => {
    const env = getEnv(c);
    if (!env.SUPABASE_URL || !env.SUPABASE_PUBLISHABLE_KEY) return c.json({ error: "sign-in is not configured on this backend" }, 404);
    let accountsOpen = env.ACCOUNTS_OPEN === "true";
    if (accountsOpen && env.SUPABASE_SERVICE_KEY) {
      try { accountsOpen = await new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY).rpc<boolean>("oc_accounts_open", {}); }
      catch (e) { console.error(`auth/config: ${(e as Error).message}`); accountsOpen = false; }
    }
    const origin = new URL(c.req.url).origin;
    return c.json({
      supabaseUrl: env.SUPABASE_URL.replace(/\/+$/, ""),
      publishableKey: env.SUPABASE_PUBLISHABLE_KEY,
      accountsOpen,
      confirmRedirectUrl: env.ACCOUNT_CONFIRM_REDIRECT_URL || `${origin}/auth/confirmed`,
    });
  });

  // Where the confirmation email's link lands: nothing to do here but go back to the app.
  app.get("/auth/confirmed", (c) =>
    c.html(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>You're in</title><body style="font:16px -apple-system,sans-serif;background:#F4F1EA;color:#22201C;display:grid;place-items:center;height:100vh;margin:0">
<div style="text-align:center"><h1 style="font-family:Georgia,serif;font-weight:400">you're in.</h1><p>go back to OpenClicky — it signs you in on its own.</p></div></body>`),
  );

  // Public sign-up. The Supabase instance is shared by every FlowsXR project, so OpenClicky's own cap
  // (oc_accounts_open) is enforced here rather than by letting the app talk to GoTrue directly.
  app.post("/auth/signup", async (c) => {
    const env = getEnv(c);
    if (!env.SUPABASE_URL || !env.SUPABASE_PUBLISHABLE_KEY || !env.SUPABASE_SERVICE_KEY) return c.json({ error: "sign-up is not configured on this backend" }, 404);
    let req: { email?: string; password?: string } = {};
    try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
    const email = (req.email ?? "").trim(), password = req.password ?? "";
    if (!email.includes("@") || password.length < 8) return c.json({ error: "use an email address and a password of at least 8 characters." }, 400);
    const db = new SupabaseRest(env.SUPABASE_URL, env.SUPABASE_SERVICE_KEY);
    const open = env.ACCOUNTS_OPEN === "true" && (await db.rpc<boolean>("oc_accounts_open", {}).catch(() => false));
    if (!open) return c.json({ error: "accounts_full" }, 402);
    const redirect = env.ACCOUNT_CONFIRM_REDIRECT_URL || `${new URL(c.req.url).origin}/auth/confirmed`;
    let res: Response, text: string;
    try {
      res = await fetch(`${env.SUPABASE_URL.replace(/\/+$/, "")}/auth/v1/signup?redirect_to=${encodeURIComponent(redirect)}`, {
        method: "POST",
        headers: { "content-type": "application/json", apikey: env.SUPABASE_PUBLISHABLE_KEY },
        body: JSON.stringify({ email, password }),
      });
      text = await res.text();
    } catch (e) {
      console.error(`signup: GoTrue unreachable: ${(e as Error).message}`);
      return c.json({ error: "couldn't create the account right now." }, 502);
    }
    if (!res.ok) {
      if (text.includes("already registered")) return c.json({ error: "that email already has an account — sign in instead." }, 409);
      if (res.status === 429) return c.json({ error: "too many tries — wait a minute and try again." }, 429);
      console.error(`signup: GoTrue ${res.status}: ${text.slice(0, 300)}`);
      return c.json({ error: "couldn't create the account right now." }, 502);
    }
    let userId: string | undefined;
    try {
      const user = JSON.parse(text) as { id?: string; user?: { id?: string } };
      userId = user.id ?? user.user?.id;
    } catch { /* GoTrue succeeded; the account row is best-effort below */ }
    if (userId) await db.insert("oc_accounts", { user_id: userId }).catch((e) => console.error(`signup: oc_accounts insert: ${(e as Error).message}`));
    else console.error("signup: GoTrue answered without a user id");
    return c.json({ ok: true });
  });

  // Exchange a Supabase JWT for a short-lived session token. Only Supabase JWTs are accepted here;
  // an already-exchanged session token cannot be re-exchanged.
  app.post("/agent/session-token", async (c) => {
    const env = getEnv(c);
    const token = bearerFrom(c.req.header("authorization"));
    if (!token) return c.json({ error: "missing Authorization: Bearer <supabase jwt>" }, 401);
    try {
      const p = await verifySupabaseJwt(token, env);
      const { token: session, expiresAt } = await issueSessionToken(p, env);
      return c.json({ token: session, expiresAt, sub: p.sub });
    } catch (e) {
      const status = e instanceof AuthError ? 401 : 500;
      return c.json({ error: (e as Error).message }, status);
    }
  });

  // Everything else under /agent/*, /v1/*, /skills/* and the native-shell routes needs a Supabase JWT or a session token.
  app.use("/agent/*", requireAuth);
  app.use("/v1/*", requireAuth);
  app.use("/skills/*", requireAuth);
  app.use("/chat", requireAuth);
  app.use("/tts", requireAuth);
  app.use("/transcribe-token", requireAuth);

  // Every model route passes the credits gate (a no-op for BYOK requests and unmetered backends).
  app.use("/agent/realtime/session", gate);
  app.use("/agent/transcribe", gate);
  app.use("/v1/*", gate);
  app.use("/skills/create", gate);
  app.use("/chat", gate);
  app.use("/tts", gate);
  // Mints a real AssemblyAI streaming credential on the backend's key: as metered as any model call.
  app.use("/transcribe-token", gate);

  // Native shell (macos/OpenClicky, forked from the original open-source Clicky app) speaks its original Worker contract.
  app.post("/chat", (c) => grantOr(c, "ask", () => proxyAnthropic(c, storeFor(c)))); // Claude vision + [POINT] pointing, streamed
  app.post("/tts", (c) => { // ElevenLabs or OpenAI speech → audio/mpeg; the grant gets the metered ElevenLabs path
    const ledger = ledgerFor(c);
    const account = c.get("account" as never) as AccountContext | undefined;
    return ledger && account && !account.byok ? speakOnGrant(c, ledger) : synthesizeSpeech(c, storeFor(c));
  });
  app.post("/transcribe-token", (c) => assemblyAiToken(c)); // AssemblyAI streaming token (optional)

  app.post("/v1/chat/completions", (c) => proxyOpenAI(c, "/chat/completions", storeFor(c))); // `ask` lane
  app.post("/v1/responses", (c) => proxyOpenAI(c, "/responses", storeFor(c))); // Codex agent lane (Codex >= 0.15x is Responses-only)
  app.post("/v1/polish", (c) => polishTake(c, ledgerFor(c))); // dictation polish and edits on the server-chosen model
  app.post("/v1/messages", (c) => grantOr(c, "gate", () => proxyAnthropic(c, storeFor(c)))); // Anthropic gate lane

  // Voice groundwork: ephemeral Realtime client secrets + server-side speech-to-text.
  app.post("/agent/realtime/session", (c) => createRealtimeSession(c, storeFor(c)));
  app.post("/agent/transcribe", (c) => transcribeAudio(c, storeFor(c)));

  // Skill library: the bundled agent skills + app-teaching skills, generated from skills/ and app-skills/ at build time.
  app.get("/skills/library", (c) => c.json({ skills: SKILLS_MANIFEST }));
  // "Create a skill": draft one SKILL.md from a one-line request; the client stores + activates it.
  app.post("/skills/create", (c) => createSkill(c, storeFor(c)));

  // Billing: the Stripe webhook authenticates by signature; everything else needs the user's token.
  app.post("/billing/webhook", withStore(handleStripeWebhook));
  // requireAuth only sets `principal`; widening its variables to include `billing` is safe.
  const authenticate = requireAuth as unknown as MiddlewareHandler<{ Variables: Variables }>;
  app.use("/billing/*", async (c, next) => (c.req.path === "/billing/webhook" ? next() : authenticate(c, next)));
  app.use("/billing/me", gate);
  app.get("/billing/me", async (c) => {
    const ledger = ledgerFor(c);
    if (!ledger) return c.json({ byok: true, spentMonthUsd: 0, monthlyLimitUsd: 0, spentTodayUsd: 0, dailyLimitUsd: 0, ttsCharsMonth: 0, ttsCharsLimit: 0, monthEnd: "", dayEnd: "", budgetExhausted: false, blocked: false });
    return c.json(await accountSummary(c, ledger));
  });
  app.post("/billing/checkout", withStore(createCheckoutSession));
  app.post("/billing/portal", withStore(createPortalSession));

  // TODO(next cut): /agent/realtime/turn|warmup, /skills/activations/sync, /codex-thread-launch — see REVERSE-ENGINEERING.md §8.
  return app;
}

const app = createApp();
export default app;
