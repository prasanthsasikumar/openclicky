import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { modelFor, isGrantModel } from "./modelPolicy.js";
import { estimateMicroUsd, costMicroUsd, parseAnthropicUsage } from "./prices.js";
import { reserveOr402, type AccountContext } from "./account.js";
import type { SpendLedger } from "./ledger.js";
import { UPSTREAM_TIMEOUT_MS } from "./anthropicGrant.js";

const MAX_TEXT = 8000;
const MAX_SYSTEM = 6000;
const NO_USAGE = { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 };

/** Dictation polish and Hey Clicky edits: the app sends what to do and the words; the server picks the model. */
export async function polishTake(c: Context, ledger: SpendLedger | undefined): Promise<Response> {
  const env = getEnv(c);
  let req: { purpose?: string; system?: string; text?: string } = {};
  try { req = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  if (req.purpose !== "polish" && req.purpose !== "edit") return c.json({ error: "purpose must be polish or edit" }, 400);
  const text = typeof req.text === "string" ? req.text : "";
  const system = typeof req.system === "string" ? req.system : "";
  if (!text.trim()) return c.json({ error: "text is empty" }, 400);
  if (text.length > MAX_TEXT || system.length > MAX_SYSTEM) return c.json({ error: "take too long to polish" }, 413);

  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const model = modelFor(req.purpose, env);
  const body = {
    model,
    max_tokens: Math.min(2048, Math.ceil((text.length / 3.5) * 1.5) + 64),
    system: [{ type: "text", text: system, cache_control: { type: "ephemeral" } }],
    messages: [{ role: "user", content: text }],
  };
  const account = c.get("account" as never) as AccountContext | undefined;
  const metered = Boolean(ledger && account && !account.byok);
  if (metered && !isGrantModel(model)) return c.json({ error: "model is not priced" }, 500);
  const estimate = estimateMicroUsd(model, body) ?? 0;
  let reservationId = "";
  if (metered) {
    const reserved = await reserveOr402(c, ledger!, estimate);
    if (reserved instanceof Response) return reserved;
    reservationId = reserved.reservationId;
  }
  const settle = async (micro: number, usage = NO_USAGE) => {
    if (!metered) return;
    try { await ledger!.settle(reservationId, account!.userId, micro, { route: "/v1/polish", model, ...usage, characters: 0 }); }
    catch (e) { console.error(`polish: settling failed: ${(e as Error).message}`); }
  };
  const unavailable = () => c.json({ error: "polish unavailable" }, 502);

  let upstream: Response;
  let replyText: string;
  try {
    upstream = await fetch(keys.anthropicBase + "/v1/messages", {
      method: "POST",
      headers: { "content-type": "application/json", "x-api-key": keys.anthropicKey, "anthropic-version": "2023-06-01" },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
    });
    replyText = await upstream.text();
  } catch (e) {
    await settle(0);
    console.error(`polish: upstream call failed: ${(e as Error).message}`);
    return unavailable();
  }
  if (!upstream.ok) {
    await settle(0);
    console.error(`polish: upstream ${upstream.status}: ${replyText.slice(0, 500)}`);
    return unavailable();
  }
  let content: Array<{ type: string; text?: string }>;
  try {
    const parsed = JSON.parse(replyText) as { content?: unknown };
    if (!Array.isArray(parsed.content)) throw new Error("reply has no content");
    content = parsed.content;
  } catch (e) {
    await settle(0);
    console.error(`polish: unusable reply: ${(e as Error).message}`);
    return unavailable();
  }
  const usage = parseAnthropicUsage(replyText);
  await settle(usage ? (costMicroUsd(model, usage) ?? estimate) : estimate, usage ?? NO_USAGE); // no usage reported: charge the hold in full
  return c.json({ text: content.filter((b) => b.type === "text").map((b) => b.text ?? "").join("").trim() });
}
