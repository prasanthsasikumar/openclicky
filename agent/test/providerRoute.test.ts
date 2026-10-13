import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { resolveConfig } from "../src/config.js";
import { ask } from "../src/ask.js";
import { gate } from "../src/gate.js";
import { transcribe } from "../src/audio.js";
import { RealtimeSession } from "../src/realtime.js";
import { openaiRoute, anthropicRoute } from "../src/providerRoute.js";

// Own keys are the primary route: straight to the vendor. The backend only for a capability with no own key.

type Seen = { url: string; headers: Record<string, string>; body: unknown };
function recorder(reply: () => Response) {
  const seen: Seen[] = [];
  const fetchImpl = (async (url: string, init: RequestInit) => {
    const headers = Object.fromEntries(Object.entries((init.headers ?? {}) as Record<string, string>).map(([k, v]) => [k.toLowerCase(), v]));
    seen.push({ url, headers, body: typeof init.body === "string" ? JSON.parse(init.body) : init.body });
    return reply();
  }) as unknown as typeof fetch;
  return { seen, fetchImpl };
}
const cfgWith = (env: Record<string, string>, token?: string) => resolveConfig({ backendUrl: "http://backend.test", token }, { HOME: "/tmp", ...env });

describe("provider routes", () => {
  it("own key → vendor; token only → backend without own keys; neither → none", () => {
    const own = cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user", OPENCLICKY_ANTHROPIC_KEY: "ak-user" }, "tok");
    expect(openaiRoute(own, "/v1/chat/completions", "/chat/completions")).toEqual({ url: "https://api.openai.com/v1/chat/completions", headers: { authorization: "Bearer sk-user" }, direct: true });
    expect(anthropicRoute(own, "/v1/messages")).toMatchObject({ url: "https://api.anthropic.com/v1/messages", headers: { "x-api-key": "ak-user", "anthropic-version": "2023-06-01" }, direct: true });

    const openaiOnly = cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }, "tok");
    const claudeViaAccount = anthropicRoute(openaiOnly, "/v1/messages")!;
    expect(claudeViaAccount).toEqual({ url: "http://backend.test/v1/messages", headers: { authorization: "Bearer tok" }, direct: false });
    expect(JSON.stringify(claudeViaAccount)).not.toContain("sk-user");

    expect(anthropicRoute(cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }), "/v1/messages")).toBeNull();
    expect(openaiRoute(cfgWith({}), "/agent/transcribe", "/audio/transcriptions")).toBeNull();
  });

  it("ask goes to OpenAI with the own key and a concrete model", async () => {
    const { seen, fetchImpl } = recorder(() => new Response(JSON.stringify({ choices: [{ message: { content: "hi" } }] }), { headers: { "content-type": "application/json" } }));
    expect(await ask(cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }), "q", { fetchImpl })).toBe("hi");
    expect(seen[0].url).toBe("https://api.openai.com/v1/chat/completions");
    expect(seen[0].headers.authorization).toBe("Bearer sk-user");
    expect((seen[0].body as { model: string }).model).toBe("gpt-5.5");
  });

  it("ask with neither a key nor a token says what is needed", async () => {
    await expect(ask(cfgWith({}), "q")).rejects.toThrow(/own OpenAI key/);
  });

  it("the gate goes to Anthropic with the own key, and is heuristic with only an OpenAI key", async () => {
    const { seen, fetchImpl } = recorder(() => new Response(JSON.stringify({ content: [{ type: "text", text: '{"lane":"ask","reason":"r"}' }] })));
    await gate(cfgWith({ OPENCLICKY_ANTHROPIC_KEY: "ak-user" }), "what is this", { fetchImpl });
    expect(seen[0].url).toBe("https://api.anthropic.com/v1/messages");
    expect(seen[0].headers["x-api-key"]).toBe("ak-user");
    expect((seen[0].body as { model: string }).model).toBe("claude-haiku-4-5-20251001");
    const none = await gate(cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }), "what is this", { fetchImpl });
    expect(none.gated).toBe(false);
    expect(seen).toHaveLength(1);
  });

  it("transcription uploads multipart to OpenAI with the own key", async () => {
    const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "oc-audio-")), "a.wav");
    fs.writeFileSync(file, "RIFFdata");
    const { seen, fetchImpl } = recorder(() => new Response(JSON.stringify({ text: " hello " })));
    expect(await transcribe(cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }), file, { language: "en", fetchImpl })).toBe("hello");
    expect(seen[0].url).toBe("https://api.openai.com/v1/audio/transcriptions");
    expect(seen[0].headers.authorization).toBe("Bearer sk-user");
    const form = seen[0].body as FormData;
    expect(form.get("model")).toBe("gpt-4o-mini-transcribe");
    expect(form.get("language")).toBe("en");
    expect(form.get("file")).toBeInstanceOf(Blob);
  });

  it("realtime mints the client secret at OpenAI with the own key", async () => {
    const { seen, fetchImpl } = recorder(() => new Response("nope", { status: 500 }));
    await expect(new RealtimeSession({ cfg: cfgWith({ OPENCLICKY_OPENAI_KEY: "sk-user" }), fetchImpl, voice: "marin", instructions: "be brief" }).start()).rejects.toThrow(/openai 500/);
    expect(seen[0].url).toBe("https://api.openai.com/v1/realtime/client_secrets");
    expect(seen[0].body).toEqual({ expires_after: { anchor: "created_at", seconds: 600 }, session: { type: "realtime", model: "gpt-realtime", instructions: "be brief", audio: { output: { voice: "marin" } } } });
  });

});
