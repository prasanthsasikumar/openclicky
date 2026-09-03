import { Hono } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, verifySupabaseJwt, issueSessionToken, bearerFrom, AuthError, type Principal } from "./auth.js";
import { proxyOpenAI, proxyAnthropic, createRealtimeSession, transcribeAudio, synthesizeSpeech, assemblyAiToken } from "./proxy.js";
import { SKILLS_MANIFEST } from "./skillsManifest.js";
import { requestLogger, type LogSink } from "./log.js";

export interface AppOptions {
  /** Structured request log sink (default: JSON lines on stdout). Pass `null` to disable. */
  log?: LogSink | null;
}

/**
 * OpenClicky backend: the key-holding proxy.
 * Runs unchanged under Node (`src/node.ts`) and Cloudflare Workers (`wrangler dev`, default export).
 */
export function createApp(options: AppOptions = {}) {
  const app = new Hono<{ Variables: { principal: Principal } }>();
  if (options.log !== null) app.use("*", requestLogger(options.log));

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

  // Native shell (macos/OpenClicky, forked from the original open-source Clicky app) speaks its original Worker contract.
  app.post("/chat", (c) => proxyAnthropic(c)); // Claude vision + [POINT] pointing, streamed
  app.post("/tts", (c) => synthesizeSpeech(c)); // ElevenLabs or OpenAI speech → audio/mpeg
  app.post("/transcribe-token", (c) => assemblyAiToken(c)); // AssemblyAI streaming token (optional)

  app.post("/v1/chat/completions", (c) => proxyOpenAI(c, "/chat/completions")); // `ask` lane
  app.post("/v1/responses", (c) => proxyOpenAI(c, "/responses")); // Codex agent lane (Codex >= 0.15x is Responses-only)
  app.post("/v1/messages", (c) => proxyAnthropic(c)); // Anthropic gate lane

  // Voice groundwork: ephemeral Realtime client secrets + server-side speech-to-text.
  app.post("/agent/realtime/session", (c) => createRealtimeSession(c));
  app.post("/agent/transcribe", (c) => transcribeAudio(c));

  // Skill library: the bundled skill set, generated from skills/ at build time.
  app.get("/skills/library", (c) => c.json({ skills: SKILLS_MANIFEST }));

  // TODO(next cut): /agent/realtime/turn|warmup, /skills/create|activations, /codex-thread-launch — see REVERSE-ENGINEERING.md §8.
  return app;
}

const app = createApp();
export default app;
