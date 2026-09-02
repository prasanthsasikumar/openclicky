import { Hono } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, verifySupabaseJwt, issueSessionToken, bearerFrom, AuthError, type Principal } from "./auth.js";
import { proxyOpenAI, proxyAnthropic } from "./proxy.js";

/**
 * OpenClicky backend: the key-holding proxy.
 * Runs unchanged under Node (`src/node.ts`) and Cloudflare Workers (`wrangler dev`, default export).
 */
export function createApp() {
  const app = new Hono<{ Variables: { principal: Principal } }>();

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

  // Everything else under /agent/* and /v1/* needs a Supabase JWT or a session token.
  app.use("/agent/*", requireAuth);
  app.use("/v1/*", requireAuth);

  app.post("/v1/chat/completions", (c) => proxyOpenAI(c, "/chat/completions")); // `ask` lane
  app.post("/v1/responses", (c) => proxyOpenAI(c, "/responses")); // Codex agent lane (Codex >= 0.15x is Responses-only)
  app.post("/v1/messages", (c) => proxyAnthropic(c)); // Anthropic gate lane

  // TODO(next cut): /agent/realtime/* (voice), /skills/*, /codex-thread-launch — see REVERSE-ENGINEERING.md §8.
  return app;
}

const app = createApp();
export default app;
