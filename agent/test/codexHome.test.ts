import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { renderCodexConfig, ensureCodexHome } from "../src/codexHome.js";
import { resolveConfig } from "../src/config.js";

const tpl = `{{MODEL_LINE}}\nbase_url = "{{BACKEND_URL}}/v1"\npath = "{{OPENCLICKY_ROOT}}/skills"\n[projects."{{WORKSPACE}}"]\n`;

describe("renderCodexConfig", () => {
  it("fills placeholders and omits model line when unset", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x/", workspace: "/w" });
    expect(out).toContain('base_url = "http://x/v1"');
    expect(out).toContain('path = "/r/skills"');
    expect(out).toContain('[projects."/w"]');
    expect(out).not.toContain("{{");
    expect(out).not.toMatch(/^model = /m);
  });
  it("emits model line when set", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x", workspace: "/w", model: "gpt-5.6-luna" });
    expect(out).toMatch(/^model = "gpt-5.6-luna"/m);
  });
});

describe("ensureCodexHome", () => {
  it("writes config.toml from the real template", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: "/tmp/ws" });
    const { configPath } = ensureCodexHome(cfg);
    const text = fs.readFileSync(configPath, "utf8");
    expect(text).toContain('base_url = "http://127.0.0.1:1/v1"');
    expect(text).toContain(`model_instructions_file = "${cfg.root}/skills/ModelInstructions.md"`);
    expect(text).toContain(`path = "${cfg.root}/skills"`);
    expect(text).toContain('[projects."/tmp/ws"]');
    expect(text).not.toContain("{{");
    expect(text).not.toContain("clicky-crons]");
  });
});
