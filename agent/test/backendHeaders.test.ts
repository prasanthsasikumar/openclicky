import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { backendHeaders } from "../src/backendHeaders.js";
import { resolveConfig, repoRoot } from "../src/config.js";
import { renderCodexConfig } from "../src/codexHome.js";

describe("backendHeaders", () => {
  it("adds the bearer token and, when present, the user's provider keys", () => {
    expect(backendHeaders({ token: "t" })).toEqual({ authorization: "Bearer t" });
    expect(backendHeaders({ token: "t", openaiApiKey: "sk-user", anthropicApiKey: "ak-user" }, { "content-type": "application/json" })).toEqual({
      "content-type": "application/json",
      authorization: "Bearer t",
      "x-openclicky-openai-key": "sk-user",
      "x-openclicky-anthropic-key": "ak-user",
    });
  });

  it("reads the keys from the environment", () => {
    const cfg = resolveConfig({}, { HOME: "/tmp", OPENCLICKY_OPENAI_KEY: "sk-env", OPENCLICKY_ANTHROPIC_KEY: "ak-env" } as NodeJS.ProcessEnv);
    expect(cfg.openaiApiKey).toBe("sk-env");
    expect(cfg.anthropicApiKey).toBe("ak-env");
    expect(resolveConfig({}, { HOME: "/tmp" } as NodeJS.ProcessEnv).openaiApiKey).toBeUndefined();
  });

  it("renders env_http_headers so Codex forwards the user's provider keys to the backend", () => {
    const template = fs.readFileSync(path.join(repoRoot(), "config", "codex-config.toml"), "utf8");
    const out = renderCodexConfig(template, { root: "/r", backendUrl: "http://b", workspace: "/w", userSkillsActive: "/s" });
    expect(out).toContain('env_http_headers = { "x-openclicky-openai-key" = "OPENCLICKY_OPENAI_KEY", "x-openclicky-anthropic-key" = "OPENCLICKY_ANTHROPIC_KEY" }');
  });
});
