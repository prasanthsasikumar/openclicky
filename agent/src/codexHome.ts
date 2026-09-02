import fs from "node:fs";
import path from "node:path";
import type { AgentConfig } from "./config.js";

export interface RenderVars {
  root: string;
  backendUrl: string;
  workspace: string;
  model?: string;
  composioMcpUrl?: string;
  cuaDriverBin?: string;
}

const tomlString = (s: string) => JSON.stringify(s);

/** TOML for the optional MCP servers (mirrors reference/codex-config.toml, keys stay server-side). */
export function renderMcpServers(v: Pick<RenderVars, "composioMcpUrl" | "cuaDriverBin">): string {
  const blocks: string[] = [];
  if (v.composioMcpUrl) {
    blocks.push(
      [
        "[mcp_servers.composio]",
        `url = ${tomlString(v.composioMcpUrl)}`,
        // The Composio MCP endpoint is expected to sit behind the OpenClicky backend (or accept the
        // same session token); the agent never holds a Composio key.
        'bearer_token_env_var = "OPENCLICKY_SESSION_TOKEN"',
        "tool_timeout_sec = 120.0",
      ].join("\n"),
    );
  }
  if (v.cuaDriverBin) {
    blocks.push(
      [
        "[mcp_servers.computer-use]",
        `command = ${tomlString(v.cuaDriverBin)}`,
        'args = ["--socket"]',
        'env = { CUA_DRIVER_EMBEDDED = "1", CUA_DRIVER_RS_TELEMETRY_ENABLED = "false", CUA_DRIVER_RS_UPDATE_CHECK = "false" }',
        "startup_timeout_sec = 20.0",
      ].join("\n"),
    );
  }
  return blocks.join("\n\n");
}

/** Fill the placeholders in config/codex-config.toml. */
export function renderCodexConfig(template: string, v: RenderVars): string {
  const modelLine = v.model ? `model = ${tomlString(v.model)}` : "";
  return template
    .replaceAll("{{MODEL_LINE}}", modelLine)
    .replaceAll("{{OPENCLICKY_ROOT}}", v.root)
    .replaceAll("{{BACKEND_URL}}", v.backendUrl.replace(/\/+$/, ""))
    .replaceAll("{{WORKSPACE}}", v.workspace)
    .replaceAll("{{MCP_SERVERS}}", renderMcpServers(v));
}

/**
 * Materialize the isolated CODEX_HOME: create the directory and (re)write config.toml from the
 * template so the backend URL, skills path, MCP servers, and trusted workspace are always current.
 */
export function ensureCodexHome(cfg: AgentConfig): { configPath: string } {
  const templatePath = path.join(cfg.root, "config", "codex-config.toml");
  const template = fs.readFileSync(templatePath, "utf8");
  fs.mkdirSync(cfg.codexHome, { recursive: true });
  const configPath = path.join(cfg.codexHome, "config.toml");
  fs.writeFileSync(
    configPath,
    renderCodexConfig(template, {
      root: cfg.root,
      backendUrl: cfg.backendUrl,
      workspace: cfg.workspace,
      model: cfg.model,
      composioMcpUrl: cfg.composioMcpUrl,
      cuaDriverBin: cfg.cuaDriverBin,
    }),
  );
  return { configPath };
}
