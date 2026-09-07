import { Hono, type Context, type MiddlewareHandler } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, verifySupabaseJwt, issueSessionToken, bearerFrom, AuthError, type Principal } from "./auth.js";
import { proxyOpenAI, proxyAnthropic, createRealtimeSession, transcribeAudio, synthesizeSpeech, assemblyAiToken } from "./proxy.js";
import { SKILLS_MANIFEST } from "./skillsManifest.js";
import { createSkill } from "./skillsCreate.js";
import { requestLogger, type LogSink } from "./log.js";
import { requireCredits, billingSummary, SupabaseBillingStore, type BillingStore, type BillingContext } from "./billing.js";
import { SupabaseRest } from "./db.js";
import { handleStripeWebhook, createCheckoutSession, createPortalSession } from "./stripe.js";

export interface AppOptions {
  /** Structured request log sink (default: JSON lines on stdout). Pass `null` to disable. */
  log?: LogSink | null;
  /** Billing store (tests pass MemoryBillingStore). Default: Supabase when SUPABASE_URL + SUPABASE_SERVICE_KEY are set, else none. */
  billingStore?: BillingStore;
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
  const gate: MiddlewareHandler<{ Variables: Variables }> = (c, next) => requireCredits(storeFor(c))(c, next);
  const withStore = (handler: (c: Context, store: BillingStore) => Promise<Response>) => (c: Context) => {
    const store = storeFor(c);
    return store ? handler(c, store) : c.json({ error: "billing store not configured" }, 503);
  };

  app.get("/health", (c) => c.json({ ok: true }));

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

  // Native shell (macos/OpenClicky, forked from the original open-source Clicky app) speaks its original Worker contract.
  app.post("/chat", (c) => proxyAnthropic(c, storeFor(c))); // Claude vision + [POINT] pointing, streamed
  app.post("/tts", (c) => synthesizeSpeech(c, storeFor(c))); // ElevenLabs or OpenAI speech → audio/mpeg
  app.post("/transcribe-token", (c) => assemblyAiToken(c)); // AssemblyAI streaming token (optional)

  app.post("/v1/chat/completions", (c) => proxyOpenAI(c, "/chat/completions", storeFor(c))); // `ask` lane
  app.post("/v1/responses", (c) => proxyOpenAI(c, "/responses", storeFor(c))); // Codex agent lane (Codex >= 0.15x is Responses-only)
  app.post("/v1/messages", (c) => proxyAnthropic(c, storeFor(c))); // Anthropic gate lane

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
  app.get("/billing/me", (c) => c.json(billingSummary(c)));
  app.post("/billing/checkout", withStore(createCheckoutSession));
  app.post("/billing/portal", withStore(createPortalSession));

  // TODO(next cut): /agent/realtime/turn|warmup, /skills/activations/sync, /codex-thread-launch — see REVERSE-ENGINEERING.md §8.
  return app;
}

const app = createApp();
export default app;
