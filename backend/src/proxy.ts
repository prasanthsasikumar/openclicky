import type { Context } from "hono";
import { getEnv } from "./env.js";

/** Fill in the server-side default model when the caller sends none (or the sentinel "default"). */
export function applyModelDefault(body: Record<string, unknown>, model?: string): Record<string, unknown> {
  if (model && (body.model === undefined || body.model === "default")) return { ...body, model };
  return body;
}

async function readJson(c: Context): Promise<Record<string, unknown>> {
  const text = await c.req.text();
  if (!text) return {};
  try {
    return JSON.parse(text) as Record<string, unknown>;
  } catch {
    return {};
  }
}

const HOP_BY_HOP = new Set(["content-length", "connection", "keep-alive", "transfer-encoding", "content-encoding"]);

/** Stream the upstream body straight back to the client (SSE stays SSE). */
function passthrough(upstream: Response): Response {
  const headers = new Headers();
  upstream.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase())) headers.set(k, v);
  });
  return new Response(upstream.body, { status: upstream.status, headers });
}

/** OpenAI-compatible proxy. `upstreamPath` is `/chat/completions` or `/responses`. */
export async function proxyOpenAI(c: Context, upstreamPath: string): Promise<Response> {
  const env = getEnv(c);
  if (!env.OPENAI_API_KEY) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const base = (env.OPENAI_BASE_URL || "https://api.openai.com/v1").replace(/\/$/, "");
  const body = applyModelDefault(await readJson(c), env.OPENAI_MODEL);
  const upstream = await fetch(base + upstreamPath, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
    },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}

/** Anthropic Messages proxy. */
export async function proxyAnthropic(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.ANTHROPIC_API_KEY) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const base = (env.ANTHROPIC_BASE_URL || "https://api.anthropic.com").replace(/\/$/, "");
  const upstream = await fetch(base + "/v1/messages", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: c.req.header("accept") ?? "*/*",
      "x-api-key": env.ANTHROPIC_API_KEY,
      "anthropic-version": c.req.header("anthropic-version") ?? "2023-06-01",
    },
    body: await c.req.text(),
  });
  return passthrough(upstream);
}
