import fs from "node:fs";
import path from "node:path";
import type { AgentConfig } from "./config.js";

export interface RenderVars {
  root: string;
  backendUrl: string;
  workspace: string;
  model?: string;
}

/** Fill the placeholders in config/codex-config.toml. */
export function renderCodexConfig(template: string, v: RenderVars): string {
  const modelLine = v.model ? `model = ${JSON.stringify(v.model)}` : "";
  return template
    .replaceAll("{{MODEL_LINE}}", modelLine)
    .replaceAll("{{OPENCLICKY_ROOT}}", v.root)
    .replaceAll("{{BACKEND_URL}}", v.backendUrl.replace(/\/+$/, ""))
    .replaceAll("{{WORKSPACE}}", v.workspace);
}

/**
 * Materialize the isolated CODEX_HOME: create the directory and (re)write config.toml from the
 * template so the backend URL, skills path, and trusted workspace are always current.
 */
export function ensureCodexHome(cfg: AgentConfig): { configPath: string } {
  const templatePath = path.join(cfg.root, "config", "codex-config.toml");
  const template = fs.readFileSync(templatePath, "utf8");
  fs.mkdirSync(cfg.codexHome, { recursive: true });
  const configPath = path.join(cfg.codexHome, "config.toml");
  fs.writeFileSync(
    configPath,
    renderCodexConfig(template, { root: cfg.root, backendUrl: cfg.backendUrl, workspace: cfg.workspace, model: cfg.model }),
  );
  return { configPath };
}
