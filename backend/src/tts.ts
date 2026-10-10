import type { Context } from "hono";
import { getEnv, type Env } from "./env.js";
import type { AccountContext } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const SPOKEN_CHARS = 400;
const CACHE_MS = 10 * 60_000;
let cached: { at: number; remaining: number } | undefined;
export function resetElevenLabsCache() { cached = undefined; }
const fresh = (now: number) => (cached && now - cached.at < CACHE_MS ? cached.remaining : undefined);
const remember = (at: number, remaining: number) => { cached = { at, remaining }; };

const base = (env: Env) => (env.ELEVENLABS_BASE_URL || "https://api.elevenlabs.io").replace(/\/$/, "");

/** What is left on the ElevenLabs plan this period, minus a safety margin; cached so every answer is not a lookup.
 *  A failed or unreadable lookup counts as nothing left (and is not cached), so the app falls back to the system voice. */
export async function elevenLabsCharsRemaining(env: Env, now = Date.now()): Promise<number> {
  const hit = fresh(now);
  if (hit !== undefined) return hit;
  try {
    const res = await fetch(`${base(env)}/v1/user/subscription`, { headers: { "xi-api-key": env.ELEVENLABS_API_KEY ?? "" } });
    if (!res.ok) return 0;
    const sub = (await res.json()) as { character_count?: number; character_limit?: number };
    const limit = Number(sub.character_limit ?? 0);
    const margin = Math.ceil(limit * Number(env.TTS_GLOBAL_MARGIN ?? 0.05));
    const remaining = Math.max(0, limit - Number(sub.character_count ?? 0) - margin);
    if (!Number.isFinite(remaining)) return 0;
    remember(now, remaining);
    return remaining;
  } catch {
    return 0;
  }
}

/** Trim at a sentence or word boundary so the voice never stops mid-word. */
function spokenPart(text: string): string {
  if (text.length <= SPOKEN_CHARS) return text;
  const cut = text.slice(0, SPOKEN_CHARS);
  const sentence = Math.max(cut.lastIndexOf(". "), cut.lastIndexOf("? "), cut.lastIndexOf("! "));
  return sentence > 100 ? cut.slice(0, sentence + 1) : cut.slice(0, cut.lastIndexOf(" ")).trimEnd();
}

export async function speakOnGrant(c: Context, ledger: SpendLedger): Promise<Response> {
  const env = getEnv(c);
  if (!env.ELEVENLABS_API_KEY) return c.json({ error: "tts_budget" }, 402);
  let req: { text?: string } = {};
  try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  const text = spokenPart((req.text ?? "").trim());
  if (!text) return c.json({ error: "body must be JSON with `text`" }, 400);
  const account = c.get("account" as never) as AccountContext;
  const remaining = await elevenLabsCharsRemaining(env);
  const reserved = await ledger.reserveCharacters(account.userId, text.length, account.limits, remaining);
  if (!reserved.ok) return c.json({ error: "tts_budget" }, 402); // the app falls back to the Mac's voice, silently
  const voiceId = env.ELEVENLABS_VOICE_ID || "21m00Tcm4TlvDq8ikWAM";
  let upstream: Response;
  try {
    upstream = await fetch(`${base(env)}/v1/text-to-speech/${voiceId}`, {
      method: "POST",
      headers: { "xi-api-key": env.ELEVENLABS_API_KEY, "content-type": "application/json", accept: "audio/mpeg" },
      body: JSON.stringify({ text, model_id: "eleven_flash_v2_5", voice_settings: { stability: 0.5, similarity_boost: 0.75 } }),
    });
  } catch {
    console.error("tts: ElevenLabs request failed");
    return c.json({ error: "tts_budget" }, 402);
  }
  if (!upstream.ok || !upstream.body) {
    console.error(`tts: ElevenLabs ${upstream.status}`);
    return c.json({ error: "tts_budget" }, 402);
  }
  return new Response(upstream.body, { headers: { "content-type": "audio/mpeg" } });
}
