import { describe, it, expect } from "vitest";
import { costMicroUsd, estimateMicroUsd, parseAnthropicUsage } from "../src/prices.js";
import { modelFor, isGrantModel } from "../src/modelPolicy.js";

describe("costMicroUsd", () => {
  it("prices Haiku input, output and cache tokens in micro-dollars", () => {
    // 1000 in × $1 + 200 out × $5 + 500 cache-write × $1.25 + 2000 cache-read × $0.10
    expect(costMicroUsd("claude-haiku-4-5", { inputTokens: 1000, outputTokens: 200, cacheWriteTokens: 500, cacheReadTokens: 2000 })).toBe(1000 + 1000 + 625 + 200);
  });
  it("prices Sonnet 5.5", () => {
    expect(costMicroUsd("claude-sonnet-5-5", { inputTokens: 1000, outputTokens: 100, cacheWriteTokens: 0, cacheReadTokens: 0 })).toBe(2000 + 1000);
  });
  it("refuses an unknown model", () => {
    expect(costMicroUsd("claude-opus-5-5", { inputTokens: 1, outputTokens: 1, cacheWriteTokens: 0, cacheReadTokens: 0 })).toBeUndefined();
  });
});

describe("estimateMicroUsd", () => {
  it("counts text at 3.5 chars per token, 1600 tokens per image, plus max_tokens of output", () => {
    const body = {
      max_tokens: 1000,
      system: "x".repeat(350),
      messages: [{ role: "user", content: [{ type: "image", source: { type: "base64", data: "AAAA" } }, { type: "text", text: "y".repeat(700) }] }],
    };
    // input = 100 + 1600 + 200 = 1900 tokens × $2.50 (cache-write rate, the worst case) = 4750; output 1000 × $10 = 10000
    expect(estimateMicroUsd("claude-sonnet-5-5", body)).toBe(14_750);
  });
  it("is always at least the real cost for the same body", () => {
    const body = { max_tokens: 200, messages: [{ role: "user", content: "hello there" }] };
    const estimate = estimateMicroUsd("claude-haiku-4-5", body)!;
    const real = costMicroUsd("claude-haiku-4-5", { inputTokens: 4, outputTokens: 200, cacheWriteTokens: 0, cacheReadTokens: 0 })!;
    expect(estimate).toBeGreaterThanOrEqual(real);
  });
});

describe("estimateMicroUsd worst case", () => {
  it("covers a reply whose whole input was written to the cache", () => {
    const body = { max_tokens: 100, system: "s".repeat(3500), messages: [{ role: "user", content: "hi" }] };
    const estimate = estimateMicroUsd("claude-haiku-4-5", body)!;
    const cachedWrite = costMicroUsd("claude-haiku-4-5", { inputTokens: 0, outputTokens: 100, cacheWriteTokens: 1001, cacheReadTokens: 0 })!;
    expect(estimate).toBeGreaterThanOrEqual(cachedWrite);
  });
});

describe("parseAnthropicUsage", () => {
  it("reads nested Anthropic streaming usage", () => {
    const sse = [
      'event: message_start',
      'data: {"type":"message_start","message":{"usage":{"input_tokens":12,"cache_creation_input_tokens":800,"cache_read_input_tokens":3000,"cache_creation":{"ephemeral_5m_input_tokens":800},"output_tokens":1}}}',
      'event: message_delta',
      'data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":95,"server_tool_use":{"web_search_requests":0}}}',
    ].join("\n");
    expect(parseAnthropicUsage(sse)).toEqual({ inputTokens: 12, outputTokens: 95, cacheWriteTokens: 800, cacheReadTokens: 3000 });
  });
  it("reads a non-streamed JSON body", () => {
    expect(parseAnthropicUsage('{"usage":{"input_tokens":5,"output_tokens":7}}')).toEqual({ inputTokens: 5, outputTokens: 7, cacheWriteTokens: 0, cacheReadTokens: 0 });
  });
  it("returns undefined when there is no usage", () => {
    expect(parseAnthropicUsage("data: {\"type\":\"ping\"}")).toBeUndefined();
  });
});

describe("modelPolicy", () => {
  it("uses defaults and env overrides", () => {
    expect(modelFor("polish", {})).toBe("claude-haiku-4-5");
    expect(modelFor("ask", {})).toBe("claude-sonnet-5-5");
    expect(modelFor("ask", { ASK_MODEL: "claude-haiku-4-5" })).toBe("claude-haiku-4-5");
  });
  it("only priced models are grant models", () => {
    expect(isGrantModel("claude-haiku-4-5")).toBe(true);
    expect(isGrantModel("claude-opus-5-5")).toBe(false);
  });
});
