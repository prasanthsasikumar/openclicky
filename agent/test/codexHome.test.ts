import { describe, it, expect } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { renderCodexConfig, renderMcpServers, renderSandboxWritableRoots, ensureCodexHome } from "../src/codexHome.js";
import { resolveConfig } from "../src/config.js";

const tpl = `{{MODEL_LINE}}\nbase_url = "{{BACKEND_URL}}/v1"\npath = "{{OPENCLICKY_ROOT}}/skills"\n[projects."{{WORKSPACE}}"]\n`;

describe("renderCodexConfig", () => {
  it("fills placeholders and omits model line when unset", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x/", workspace: "/w", userSkillsActive: "/u/active" });
    expect(out).toContain('base_url = "http://x/v1"');
    expect(out).toContain('path = "/r/skills"');
    expect(out).toContain('[projects."/w"]');
    expect(out).not.toContain("{{");
    expect(out).not.toMatch(/^model = /m);
  });
  it("emits model line when set", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x", workspace: "/w", model: "gpt-5.6-luna", userSkillsActive: "/u/active" });
    expect(out).toMatch(/^model = "gpt-5.6-luna"/m);
  });
});

describe("renderMcpServers", () => {
  it("renders nothing by default", () => {
    expect(renderMcpServers({})).toBe("");
    expect(renderCodexConfig("a\n{{MCP_SERVERS}}\nb", { root: "/r", backendUrl: "x", workspace: "/w", userSkillsActive: "/u/active" })).toBe("a\n\nb");
  });
  it("renders composio and computer-use blocks when configured", () => {
    const out = renderMcpServers({ composioMcpUrl: "https://mcp.example/composio", cuaDriverBin: "/opt/cua-driver" });
    expect(out).toContain('[mcp_servers.composio]\nurl = "https://mcp.example/composio"\nrequired = true\ndefault_tools_approval_mode = "approve"\nstartup_timeout_sec = 30.0\ntool_timeout_sec = 120.0');
    expect(out).not.toContain("bearer_token_env_var");
    expect(out).toContain('[mcp_servers.computer-use]\ncommand = "/opt/cua-driver"\nargs = ["--socket"]');
    expect(out).toContain('CUA_DRIVER_EMBEDDED = "1"');
  });
  it("sends the Composio consumer key as a header when configured", () => {
    const out = renderMcpServers({ composioMcpUrl: "https://connect.composio.dev/mcp", composioApiKey: "ck_test" });
    expect(out).toContain('[mcp_servers.composio]\nurl = "https://connect.composio.dev/mcp"\nhttp_headers = { "x-consumer-api-key" = "ck_test" }\nrequired = true');
    expect(out).not.toContain("bearer_token_env_var");
  });
});

describe("renderSandboxWritableRoots", () => {
  it("renders nothing when no extra roots are configured", () => {
    // Codex's workspace-write sandbox then allows writes in the thread's cwd only, as before.
    expect(renderSandboxWritableRoots([])).toBe("");
    expect(renderSandboxWritableRoots(undefined)).toBe("");
  });
  it("renders the roots Codex may write to outside the workspace", () => {
    const out = renderSandboxWritableRoots(["/Users/x", "/Volumes/Work"]);
    expect(out).toContain("[sandbox_workspace_write]");
    expect(out).toContain('writable_roots = ["/Users/x", "/Volumes/Work"]');
  });
  it("is filled into the template through its own placeholder", () => {
    const out = renderCodexConfig("a\n{{SANDBOX_WRITABLE_ROOTS}}\nb", {
      root: "/r", backendUrl: "x", workspace: "/w", userSkillsActive: "/u/active", writableRoots: ["/Users/x"],
    });
    expect(out).toContain('writable_roots = ["/Users/x"]');
    expect(out).not.toContain("{{");
  });
});

describe("writable roots configuration", () => {
  it("defaults to the whole home folder", () => {
    const cfg = resolveConfig({}, { HOME: "/Users/x" });
    expect(cfg.writableRoots).toEqual(["/Users/x"]);
  });
  it("takes a comma-separated override and expands ~", () => {
    const cfg = resolveConfig({}, {
      HOME: "/Users/x",
      OPENCLICKY_WRITABLE_ROOTS: "~/Desktop, /Volumes/Work ,",
    });
    expect(cfg.writableRoots).toEqual(["/Users/x/Desktop", "/Volumes/Work"]);
  });
  it("takes an empty override to mean workspace-only", () => {
    const cfg = resolveConfig({}, { HOME: "/Users/x", OPENCLICKY_WRITABLE_ROOTS: "" });
    expect(cfg.writableRoots).toEqual([]);
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
    expect(text).not.toMatch(/^\[mcp_servers\.composio\]/m);
  });
  it("includes MCP servers from config", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: "/tmp/ws", composioMcpUrl: "https://mcp.example/c", cuaDriverBin: "/opt/cua" });
    const text = fs.readFileSync(ensureCodexHome(cfg).configPath, "utf8");
    expect(text).toMatch(/^\[mcp_servers\.composio\]\nurl = "https:\/\/mcp\.example\/c"/m);
    expect(text).toMatch(/^\[mcp_servers\.computer-use\]\ncommand = "\/opt\/cua"/m);
  });
  it("renders the user skills active dir and creates it", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const userSkillsDir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-user-skills-"));
    const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: "/tmp/ws", userSkillsDir });
    const text = fs.readFileSync(ensureCodexHome(cfg).configPath, "utf8");
    expect(text).toContain(`path = "${path.join(userSkillsDir, "active")}"`);
    expect(fs.existsSync(path.join(userSkillsDir, "active"))).toBe(true);
  });
  it("still writes config.toml when the user skills dir is unusable", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const userSkillsDir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "oc-bad-skills-")), "skills");
    fs.writeFileSync(userSkillsDir, "not a directory");
    const warnings: string[] = [];
    const orig = process.stderr.write;
    process.stderr.write = ((chunk: string | Uint8Array) => { warnings.push(String(chunk)); return true; });
    try {
      const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: "/tmp/ws", userSkillsDir });
      const text = fs.readFileSync(ensureCodexHome(cfg).configPath, "utf8");
      expect(text).toContain(`path = "${path.join(userSkillsDir, "active")}"`);
    } finally {
      process.stderr.write = orig;
    }
    expect(warnings.join("")).toMatch(/warning: user skills not synced/);
  });
});

describe("path escaping", () => {
  // A `"` breaks out of the template's own quotes and a `\` is a TOML escape introducer; either one
  // in an interpolated path must not produce broken or attacker-influenced TOML (the injection this
  // guards against: a workspace/skills path with a `"` splicing arbitrary keys into config.toml).
  const tricky = '/tmp/oc "quoted" \\ dir';

  it("escapes a root containing a quote and a backslash so the skills path still parses to the same value", () => {
    const out = renderCodexConfig(tpl, { root: tricky, backendUrl: "http://x", workspace: "/w", userSkillsActive: "/u/active" });
    const m = out.match(/path = "((?:[^"\\]|\\.)*)"/);
    expect(m).not.toBeNull();
    expect(JSON.parse(`"${m![1]}"`)).toBe(`${tricky}/skills`);
    // No stray unescaped quote broke out of the string onto its own line.
    expect(out.split("\n").filter((l) => l.startsWith("path = ")).length).toBe(1);
  });

  it("escapes a workspace containing a quote and a backslash so [projects.\"...\"] still parses to the same value", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x", workspace: tricky, userSkillsActive: "/u/active" });
    const m = out.match(/\[projects\."((?:[^"\\]|\\.)*)"\]/);
    expect(m).not.toBeNull();
    expect(JSON.parse(`"${m![1]}"`)).toBe(tricky);
  });

  it("round-trips a tricky workspace path through the real config template", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-home-"));
    const cfg = resolveConfig({ codexHome: home, backendUrl: "http://127.0.0.1:1", workspace: tricky });
    const text = fs.readFileSync(ensureCodexHome(cfg).configPath, "utf8");
    const m = text.match(/\[projects\."((?:[^"\\]|\\.)*)"\]/);
    expect(m).not.toBeNull();
    expect(JSON.parse(`"${m![1]}"`)).toBe(tricky);
  });
});

describe("computer-use driver discovery", () => {
  it("defaults to the installed cua-driver so the config renders the MCP server", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cua-"));
    const driver = path.join(home, ".local", "bin", "cua-driver");
    fs.mkdirSync(path.dirname(driver), { recursive: true });
    fs.writeFileSync(driver, "#!/bin/sh\n", { mode: 0o755 });

    const cfg = resolveConfig({}, { HOME: home });
    expect(cfg.cuaDriverBin).toBe(driver);
    expect(renderMcpServers({ cuaDriverBin: cfg.cuaDriverBin })).toContain("[mcp_servers.computer-use]");
  });

  it("prefers the driver in HOME over the app bundle", () => {
    // Not asserted: that an arbitrary HOME yields undefined. The candidate list ends with
    // /Applications/CuaDriver.app, which exists on any machine where CuaDriver is installed, so
    // that assertion would pass or fail depending on whose machine ran it.
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cua-home-"));
    const driver = path.join(home, ".local", "bin", "cua-driver");
    fs.mkdirSync(path.dirname(driver), { recursive: true });
    fs.writeFileSync(driver, "#!/bin/sh\n", { mode: 0o755 });

    expect(resolveConfig({}, { HOME: home }).cuaDriverBin).toBe(driver);
  });

  it("lets an explicit setting win, and an empty string disable it", () => {
    const home = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cua2-"));
    const driver = path.join(home, ".local", "bin", "cua-driver");
    fs.mkdirSync(path.dirname(driver), { recursive: true });
    fs.writeFileSync(driver, "#!/bin/sh\n", { mode: 0o755 });

    expect(resolveConfig({}, { HOME: home, CUA_DRIVER_BIN: "/opt/other" }).cuaDriverBin).toBe("/opt/other");
    expect(resolveConfig({}, { HOME: home, CUA_DRIVER_BIN: "" }).cuaDriverBin).toBeUndefined();
  });
});

describe("version", () => {
  it("reports the version the app ships, not a copy of its own", async () => {
    // The CLI used to report a hardcoded "0.2.0" that matched neither VERSION, nor either
    // package.json, nor the released build.
    const { readVersion, VERSION_FILE } = await import("../src/version.js");
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "oc-version-"));
    fs.mkdirSync(path.dirname(path.join(root, VERSION_FILE)), { recursive: true });
    fs.writeFileSync(path.join(root, VERSION_FILE), "9.9.9\n");
    expect(readVersion(root)).toBe("9.9.9");
  });
  it("says unknown rather than throwing when the file is not there", async () => {
    const { readVersion } = await import("../src/version.js");
    expect(readVersion(fs.mkdtempSync(path.join(os.tmpdir(), "oc-noversion-")))).toBe("unknown");
  });
});
