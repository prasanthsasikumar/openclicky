import { describe, it, expect } from "vitest";
import { resolveProviderKeys, OPENAI_KEY_HEADER, ANTHROPIC_KEY_HEADER } from "../src/keys.js";
import type { Env } from "../src/env.js";

const headers = (h: Record<string, string>) => ({ get: (n: string) => h[n.toLowerCase()] ?? null });
const env: Env = {
  OPENAI_API_KEY: "sk-backend",
  OPENAI_BASE_URL: "https://openrouter.ai/api/v1",
  ANTHROPIC_API_KEY: "ak-backend",
  ANTHROPIC_BASE_URL: "https://openrouter.ai/api",
  ANTHROPIC_MODEL: "anthropic/claude-haiku-4.5",
};

describe("resolveProviderKeys", () => {
  it("uses backend keys and model mapping when no BYOK header is present", () => {
    const k = resolveProviderKeys(headers({}), env);
    expect(k.byok).toBe(false);
    expect(k.openaiKey).toBe("sk-backend");
    expect(k.openaiBase).toBe("https://openrouter.ai/api/v1");
    expect(k.anthropicKey).toBe("ak-backend");
    expect(k.anthropicBase).toBe("https://openrouter.ai/api");
    expect(k.mapModels).toBe(true);
    expect(k.anthropicDefaultModel).toBe("anthropic/claude-haiku-4.5");
  });

  it("uses the request's OpenAI key against OpenAI directly and stops model mapping", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "sk-user" }), env);
    expect(k.byok).toBe(true);
    expect(k.openaiKey).toBe("sk-user");
    expect(k.openaiBase).toBe("https://api.openai.com/v1");
    expect(k.mapModels).toBe(false);
    // No Anthropic key was brought: the Anthropic lanes have nothing to use.
    expect(k.anthropicKey).toBeUndefined();
  });

  it("uses the request's Anthropic key against Anthropic directly", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "sk-user", [ANTHROPIC_KEY_HEADER]: "ak-user" }), env);
    expect(k.anthropicKey).toBe("ak-user");
    expect(k.anthropicBase).toBe("https://api.anthropic.com");
    expect(k.anthropicDefaultModel).toBe("claude-haiku-4-5-20251001");
  });

  it("honours BYOK base overrides and trims whitespace", () => {
    const k = resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "  sk-user \n" }), { ...env, BYOK_OPENAI_BASE_URL: "http://127.0.0.1:9/v1/" });
    expect(k.openaiKey).toBe("sk-user");
    expect(k.openaiBase).toBe("http://127.0.0.1:9/v1");
  });

  it("ignores an empty header", () => {
    expect(resolveProviderKeys(headers({ [OPENAI_KEY_HEADER]: "   " }), env).byok).toBe(false);
  });
});
