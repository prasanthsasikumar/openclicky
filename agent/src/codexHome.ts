import fs from "node:fs";
import path from "node:path";
import type { AgentConfig } from "./config.js";
import { activeDir, syncActiveDir } from "./skillsLibrary.js";

export interface RenderVars {
  root: string;
  backendUrl: string;
  workspace: string;
  model?: string;
  composioMcpUrl?: string;
  composioApiKey?: string;
  cuaDriverBin?: string;
  /** Directory of symlinks to the user's activated skills (see skillsLibrary.ts). */
  userSkillsActive: string;
}

const tomlString = (s: string) => JSON.stringify(s);

/** TOML for the optional MCP servers (mirrors reference/codex-config.toml, keys stay server-side). */
export function renderMcpServers(v: Pick<RenderVars, "composioMcpUrl" | "composioApiKey" | "cuaDriverBin">): string {
  const blocks: string[] = [];
  if (v.composioMcpUrl) {
    blocks.push(
      [
        "[mcp_servers.composio]",
        `url = ${tomlString(v.composioMcpUrl)}`,
        // With a consumer key (COMPOSIO_API_KEY) Codex sends it as Composio's `x-consumer-api-key`
        // header. Without one the server is left to Codex's MCP OAuth: `codex mcp login composio`
        // (run once with this CODEX_HOME; `openclicky integrations login`) stores the token in
        // CODEX_HOME and Composio Connect's login page does the rest.
        ...(v.composioApiKey ? [`http_headers = { "x-consumer-api-key" = ${tomlString(v.composioApiKey)} }`] : []),
        // Codex starts the first turn without waiting for optional servers, so the model would see no
        // Composio tools on the turn that needs them; `required` makes it wait (and fail loudly).
        "required = true",
        // The headless doctrine auto-accepts approvals (the user's instruction is the approval); with
        // approval_policy = "never" Codex would otherwise auto-reject Composio's approval-gated tools.
        'default_tools_approval_mode = "approve"',
        "startup_timeout_sec = 30.0",
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
    .replaceAll("{{USER_SKILLS_ACTIVE}}", v.userSkillsActive)
    .replaceAll("{{MCP_SERVERS}}", renderMcpServers(v));
}

/**
 * Materialize the isolated CODEX_HOME: create the directory and (re)write config.toml from the
 * template so the backend URL, skills paths, MCP servers, and trusted workspace are always current.
 * Also syncs the user's `active/` skills dir so Codex sees exactly the activated skills.
 */
export function ensureCodexHome(cfg: AgentConfig): { configPath: string } {
  const templatePath = path.join(cfg.root, "config", "codex-config.toml");
  const template = fs.readFileSync(templatePath, "utf8");
  fs.mkdirSync(cfg.codexHome, { recursive: true });
  const configPath = path.join(cfg.codexHome, "config.toml");
  // A broken user skills dir must not stop the agent: warn and run with whatever active/ holds.
  try {
    syncActiveDir(cfg.userSkillsDir);
  } catch (e) {
    process.stderr.write(`warning: user skills not synced: ${(e as Error).message}\n`);
  }
  fs.writeFileSync(
    configPath,
    renderCodexConfig(template, {
      root: cfg.root,
      backendUrl: cfg.backendUrl,
      workspace: cfg.workspace,
      model: cfg.model,
      composioMcpUrl: cfg.composioMcpUrl,
      composioApiKey: cfg.composioApiKey,
      cuaDriverBin: cfg.cuaDriverBin,
      userSkillsActive: activeDir(cfg.userSkillsDir),
    }),
  );
  return { configPath };
}
