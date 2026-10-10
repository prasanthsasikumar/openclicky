import type { Context } from "hono";
import { getEnv } from "./env.js";
import { resolveProviderKeys } from "./keys.js";
import { modelFor, isGrantModel, type Purpose } from "./modelPolicy.js";
import { estimateMicroUsd, estimateInputTokens, costMicroUsd, parseAnthropicUsage } from "./prices.js";
import { reserveOr402, type AccountContext } from "./account.js";
import type { SpendLedger } from "./ledger.js";

const MAX_TOKENS_CAP = 1024;
const MAX_IMAGES = 2;
const MAX_INPUT_TOKENS = 60_000;
/** An upstream call that has not finished in five minutes is abandoned (and settled like any failure). */
export const UPSTREAM_TIMEOUT_MS = 300_000;
const ALLOWED_BLOCKS = new Set(["text", "image", "tool_use", "tool_result"]);

type Block = Record<string, unknown>;
const UNSUPPORTED = { error: "unsupported content" };

/**
 * Only blocks the estimate can price: text, images, tool calls and their results (recursively).
 * Returns the blocks without any client cache_control, or undefined when something else is in there.
 */
function grantContent(content: unknown): unknown {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return undefined;
  const out: Block[] = [];
  for (const raw of content) {
    if (!raw || typeof raw !== "object" || !ALLOWED_BLOCKS.has(String((raw as Block).type))) return undefined;
    const { cache_control: _dropped, ...block } = raw as Block;
    if (block.type === "tool_result" && block.content !== undefined) {
      const inner = grantContent(block.content);
      if (inner === undefined) return undefined;
      block.content = inner;
    }
    out.push(block);
  }
  return out;
}

function countImages(content: unknown): number {
  if (!Array.isArray(content)) return 0;
  return (content as Block[]).reduce((n, b) => n + (b.type === "image" ? 1 : b.type === "tool_result" ? countImages(b.content) : 0), 0);
}

/** The request as the grant allows it: server's model, capped output, cached system prompt, at most two images, no extended thinking. */
export function prepareGrantBody(body: Record<string, unknown>, model: string): Record<string, unknown> | { error: string } {
  const messages: Block[] = [];
  for (const m of (Array.isArray(body.messages) ? body.messages : []) as Block[]) {
    const content = grantContent(m?.content);
    if (content === undefined) return UNSUPPORTED;
    messages.push({ ...m, content });
  }
  if (messages.reduce((n, m) => n + countImages(m.content), 0) > MAX_IMAGES) return { error: "too_many_images" };
  // The server alone decides what is cached: client cache_control is stripped, then the system prompt's last block is marked.
  let system: unknown;
  if (typeof body.system === "string") system = body.system ? [{ type: "text", text: body.system }] : undefined;
  else if (body.system !== undefined) {
    const blocks = grantContent(body.system);
    if (!Array.isArray(blocks) || blocks.some((b) => (b as Block).type !== "text")) return UNSUPPORTED;
    system = blocks.length ? blocks : undefined;
  }
  if (Array.isArray(system)) system[system.length - 1] = { ...(system[system.length - 1] as Block), cache_control: { type: "ephemeral" } };
  const requested = Number(body.max_tokens);
  const maxTokens = Number.isFinite(requested) && requested >= 1 ? Math.min(Math.floor(requested), MAX_TOKENS_CAP) : MAX_TOKENS_CAP;
  const prepared: Record<string, unknown> = { ...body, model, max_tokens: maxTokens, messages };
  if (system) prepared.system = system;
  else delete prepared.system;
  // The grant does not pay for extended thinking, server-side tools or hosted MCP/containers: they bill outside the token estimate.
  delete prepared.thinking;
  delete prepared.mcp_servers;
  delete prepared.container;
  if (Array.isArray(body.tools)) {
    const custom = body.tools
      .filter((t) => { const type = (t as { type?: unknown } | null)?.type; return type === undefined || type === "custom"; })
      .map((t) => { const { cache_control: _dropped, ...tool } = t as Block; return tool; });
    if (custom.length) prepared.tools = custom;
    else { delete prepared.tools; delete prepared.tool_choice; }
  }
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
  if (estimateInputTokens(prepared) > MAX_INPUT_TOKENS) return c.json({ error: "request too large" }, 413);
  const estimate = estimateMicroUsd(model, prepared)!;
  const reserved = await reserveOr402(c, ledger, estimate);
  if (reserved instanceof Response) return reserved;
  const route = new URL(c.req.url).pathname;
  const userId = (c.get("account" as never) as AccountContext).userId;
  const release = async () => {
    try { await ledger.settle(reserved.reservationId, userId, 0, { route, model, inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, characters: 0 }); }
    catch (e) { console.error(`grant ${route}: releasing the hold failed: ${(e as Error).message}`); }
  };
  let upstream: Response;
  try {
    upstream = await fetch(keys.anthropicBase + "/v1/messages", {
      method: "POST",
      headers: { "content-type": "application/json", accept: c.req.header("accept") ?? "*/*", "x-api-key": keys.anthropicKey, "anthropic-version": "2023-06-01" },
      body: JSON.stringify(prepared),
      signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
    });
  } catch (e) {
    await release();
    console.error(`grant ${route}: upstream fetch failed: ${(e as Error).message}`);
    return c.json({ error: "the assistant is unavailable" }, 502);
  }
  if (!upstream.ok || !upstream.body) {
    await release();
    try { console.error(`grant ${route}: upstream ${upstream.status}: ${(await upstream.text()).slice(0, 500)}`); }
    catch (e) { console.error(`grant ${route}: upstream ${upstream.status}, body unreadable: ${(e as Error).message}`); }
    return c.json({ error: `the assistant is unavailable (${upstream.status})` }, 502);
  }
  return settleStream(upstream, (text) => {
    const usage = parseAnthropicUsage(text);
    // The final usage arrives in message_delta; a stream that ended (or was cancelled) before it is charged the hold in full.
    const complete = usage !== undefined && /"type"\s*:\s*"message_delta"/.test(text);
    const actual = complete && usage ? costMicroUsd(model, usage)! : estimate;
    const settle = ledger.settle(reserved.reservationId, userId, actual, { route, model, ...(usage ?? { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 }), characters: 0 })
      .catch((e) => console.error(`settle failed: ${(e as Error).message}`));
    try { c.executionCtx.waitUntil(settle); } catch { /* Node: nothing to hand it to */ }
  });
}
