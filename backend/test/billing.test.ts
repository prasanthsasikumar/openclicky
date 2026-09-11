import { describe, it, expect } from "vitest";
import { Hono } from "hono";
import {
  MemoryBillingStore,
  requireCredits,
  meterResponse,
  parseUsage,
  creditsForTokens,
  creditsForAudioSeconds,
  creditsForCharacters,
  FREE_PLAN_ID,
  type BillingContext,
} from "../src/billing.js";
import type { Principal } from "../src/auth.js";

const principal: Principal = { sub: "user-1", via: "session" };

/** An upstream whose usage block sits after more than 512 kB of content, the way a long agent
 *  turn's does. */
function longStreamingUpstream(): Response {
  const filler = `data: ${"x".repeat(1000)}\n\n`;
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      const encoder = new TextEncoder();
      for (let written = 0; written < 600_000; written += filler.length) controller.enqueue(encoder.encode(filler));
      controller.enqueue(encoder.encode('data: {"usage":{"prompt_tokens":1000,"completion_tokens":500}}\n\ndata: [DONE]\n\n'));
      controller.close();
    },
  });
  return new Response(stream, { headers: { "content-type": "text/event-stream" } });
}

/** An upstream that keeps streaming until the reader gives up, so a test can cancel it. */
function neverEndingUpstream(): Response {
  const stream = new ReadableStream<Uint8Array>({
    pull(controller) {
      controller.enqueue(new TextEncoder().encode("data: chunk\n\n"));
    },
  });
  return new Response(stream, { headers: { "content-type": "text/event-stream" } });
}

function appWith(store: MemoryBillingStore | undefined) {
  const app = new Hono<{ Variables: { principal: Principal; billing: BillingContext } }>();
  app.use("*", async (c, next) => {
    c.set("principal", principal);
    await next();
  });
  app.use("/v1/*", requireCredits(store));
  app.post("/v1/long", (c) => meterResponse(c, longStreamingUpstream(), "/v1/long", "gpt-test", store, () => 2));
  app.post("/v1/endless", (c) => meterResponse(c, neverEndingUpstream(), "/v1/endless", "gpt-test", store, () => 2));
  app.post("/v1/chat", (c) => {
    const upstream = new Response('data: {"usage":{"prompt_tokens":1000,"completion_tokens":500}}\n\ndata: [DONE]\n\n', {
      headers: { "content-type": "text/event-stream" },
    });
    return meterResponse(c, upstream, "/v1/chat", "gpt-test", store, () => 2);
  });
  return app;
}

describe("credit costs", () => {
  it("tokens: 1 per 1k input, 4 per 1k output, minimum 1", () => {
    expect(creditsForTokens(1000, 500)).toBe(3);
    expect(creditsForTokens(0, 0)).toBe(1);
  });
  it("audio: 1 per started 15 s", () => {
    expect(creditsForAudioSeconds(4)).toBe(1);
    expect(creditsForAudioSeconds(31)).toBe(3);
  });
  it("characters: 1 per 500", () => {
    expect(creditsForCharacters(1200)).toBe(3);
  });
});

describe("parseUsage", () => {
  it("reads OpenAI chat, Responses, and Anthropic shapes", () => {
    expect(parseUsage('{"usage":{"prompt_tokens":10,"completion_tokens":20}}')).toEqual({ inputTokens: 10, outputTokens: 20 });
    expect(parseUsage('data: {"type":"response.completed","response":{"usage":{"input_tokens":7,"output_tokens":9}}}')).toEqual({ inputTokens: 7, outputTokens: 9 });
    const anthropic =
      'event: message_start\ndata: {"type":"message_start","message":{"usage":{"input_tokens":50,"output_tokens":1}}}\n\nevent: message_delta\ndata: {"type":"message_delta","usage":{"output_tokens":40}}\n\n';
    expect(parseUsage(anthropic)).toEqual({ inputTokens: 50, outputTokens: 40 });
    expect(parseUsage("data: [DONE]")).toBeUndefined();
  });
});

describe("requireCredits + meterResponse", () => {
  it("passes through and meters nothing when no store is configured", async () => {
    const r = await appWith(undefined).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(200);
    expect(await r.text()).toContain("[DONE]");
  });

  it("skips the gate for BYOK requests", async () => {
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/chat", { method: "POST", headers: { "x-openclicky-openai-key": "sk-user" } });
    expect(r.status).toBe(200);
    expect(store.events).toHaveLength(0);
  });

  it("meters a free-tier user from the streamed usage", async () => {
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(200);
    await r.text();
    await new Promise((res) => setTimeout(res, 10));
    expect(store.events).toEqual([
      expect.objectContaining({ user_id: "user-1", route: "/v1/chat", model: "gpt-test", input_tokens: 1000, output_tokens: 500, credits: 3 }),
    ]);
  });

  it("finds the usage block after a reply longer than the window it keeps", async () => {
    // The window used to keep the FIRST 512 kB, and usage arrives at the end — so every long agent
    // turn fell back to the flat 2-credit charge instead of the ~1000 tokens it really cost.
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/long", { method: "POST" });
    expect(r.status).toBe(200);
    await r.text();
    await new Promise((res) => setTimeout(res, 10));
    expect(store.events).toEqual([
      expect.objectContaining({ route: "/v1/long", input_tokens: 1000, output_tokens: 500, credits: 3 }),
    ]);
  });

  it("charges a turn the client cancelled part-way through", async () => {
    // The tokens were spent upstream whether or not anyone read the answer; before this the whole
    // turn was free if the user hit stop.
    const store = new MemoryBillingStore();
    const r = await appWith(store).request("/v1/endless", { method: "POST" });
    expect(r.status).toBe(200);
    const reader = r.body!.getReader();
    await reader.read();
    await reader.cancel();
    await new Promise((res) => setTimeout(res, 10));
    expect(store.events).toEqual([expect.objectContaining({ route: "/v1/endless", credits: 2 })]);
  });

  it("refuses with 402 once the plan's credits are spent", async () => {
    const store = new MemoryBillingStore();
    store.events.push({ user_id: "user-1", route: "/v1/chat", input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: 200 });
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(402);
    expect(await r.json()).toMatchObject({ error: "credits_exhausted", used: 200, limit: 200, plan: FREE_PLAN_ID });
  });

  it("an invitee's own allowance replaces the plan's credits", async () => {
    const store = new MemoryBillingStore();
    store.subs.set("user-1", {
      user_id: "user-1",
      plan_id: "invite",
      status: "active",
      current_period_start: "2026-09-01T00:00:00Z",
      current_period_end: "2026-10-01T00:00:00Z",
      stripe_customer_id: null,
      stripe_subscription_id: null,
      monthly_credits_override: 5,
    });
    store.events.push({ user_id: "user-1", route: "/v1/chat", input_tokens: 0, output_tokens: 0, audio_seconds: 0, characters: 0, credits: 5 });
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(402);
    expect(await r.json()).toMatchObject({ error: "credits_exhausted", plan: "invite", used: 5, limit: 5 });
  });

  it("refuses with 402 when a paid subscription is not active", async () => {
    const store = new MemoryBillingStore();
    store.subs.set("user-1", {
      user_id: "user-1",
      plan_id: "pro",
      status: "canceled",
      current_period_start: "2026-09-01T00:00:00Z",
      current_period_end: "2026-10-01T00:00:00Z",
      stripe_customer_id: null,
      stripe_subscription_id: null,
    });
    const r = await appWith(store).request("/v1/chat", { method: "POST" });
    expect(r.status).toBe(402);
    expect(await r.json()).toMatchObject({ error: "subscription_inactive" });
  });
});
