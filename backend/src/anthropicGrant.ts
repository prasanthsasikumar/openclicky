import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { modelFor, isGrantModel, type Purpose } from "./modelPolicy.js";
import { estimateMicroUsd, costMicroUsd, parseAnthropicUsage } from "./prices.js";
import { reserveOr402 } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const MAX_TOKENS_CAP = 1024;
const MAX_IMAGES = 2;

/** The request as the grant allows it: server's model, capped output, cached system prompt, at most two images, no extended thinking. */
export function prepareGrantBody(body: Record<string, unknown>, model: string): Record<string, unknown> | { error: string } {
  const messages = (body.messages as Array<{ content: unknown }> | undefined) ?? [];
  const images = messages.flatMap((m) => (Array.isArray(m.content) ? m.content : [])).filter((b) => (b as { type?: string }).type === "image");
  if (images.length > MAX_IMAGES) return { error: "too_many_images" };
  const system =
    typeof body.system === "string" && body.system
      ? [{ type: "text", text: body.system, cache_control: { type: "ephemeral" } }]
      : body.system;
  const prepared: Record<string, unknown> = { ...body, model, max_tokens: Math.min(Number(body.max_tokens ?? MAX_TOKENS_CAP), MAX_TOKENS_CAP), ...(system ? { system } : {}) };
  delete prepared.thinking; // the grant does not pay for extended thinking, and the estimate must stay a worst case
  return prepared;
}

/** Passes the body through and calls onDone with the (trailing) text once, however the stream ends. */
export function settleStream(upstream: Response, onDone: (text: string) => void): Response {
  const WINDOW = 512_000;
  let collected = "";
  let done = false;
  const finish = () => { if (!done) { done = true; onDone(collected); } };
  const decoder = new TextDecoder();
  const reader = upstream.body!.getReader();
  const body = new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const { done: end, value } = await reader.read();
        if (end) { finish(); controller.close(); return; }
        collected += decoder.decode(value, { stream: true });
        if (collected.length > WINDOW) collected = collected.slice(-WINDOW);
        controller.enqueue(value);
      } catch (e) { finish(); controller.error(e); }
    },
    cancel(reason) { finish(); return reader.cancel(reason); },
  });
  const headers = new Headers({ "content-type": upstream.headers.get("content-type") ?? "text/event-stream" });
  return new Response(body, { status: upstream.status, headers });
}

export async function proxyAnthropicOnGrant(c: Context, ledger: SpendLedger, purpose: Purpose): Promise<Response> {
  const env = getEnv(c);
  const keys = resolveProviderKeys(c.req.raw.headers, env);
  if (!keys.anthropicKey) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const model = modelFor(purpose, env);
  if (!isGrantModel(model)) return c.json({ error: "model is not priced" }, 500);
  let raw: Record<string, unknown> = {};
  try { raw = JSON.parse((await c.req.text()) || "{}"); } catch { return c.json({ error: "body must be JSON" }, 400); }
  const prepared = prepareGrantBody(raw, model);
  if ("error" in prepared) return c.json(prepared, 400);
  const estimate = estimateMicroUsd(model, prepared)!;
  const reserved = await reserveOr402(c, ledger, estimate);
  if (reserved instanceof Response) return reserved;
  const upstream = await fetch(keys.anthropicBase + "/v1/messages", {
    method: "POST",
    headers: { "content-type": "application/json", accept: c.req.header("accept") ?? "*/*", "x-api-key": keys.anthropicKey, "anthropic-version": "2023-06-01" },
    body: JSON.stringify(prepared),
  });
  const route = new URL(c.req.url).pathname;
  if (!upstream.ok || !upstream.body) {
    await ledger.settle(reserved.reservationId, 0, { route, model, inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, characters: 0 });
    console.error(`grant ${route}: upstream ${upstream.status}: ${(await upstream.text()).slice(0, 500)}`);
    return c.json({ error: `the assistant is unavailable (${upstream.status})` }, 502);
  }
  return settleStream(upstream, (text) => {
    const usage = parseAnthropicUsage(text);
    const actual = usage ? costMicroUsd(model, usage)! : estimate; // no usage reported: charge the hold in full
    const settle = ledger.settle(reserved.reservationId, actual, { route, model, ...(usage ?? { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 }), characters: 0 })
      .catch((e) => console.error(`settle failed: ${(e as Error).message}`));
    try { c.executionCtx.waitUntil(settle); } catch { /* Node: nothing to hand it to */ }
  });
}
