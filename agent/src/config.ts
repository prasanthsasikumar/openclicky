import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export interface AgentConfig {
  /** OpenClicky backend base URL (no trailing slash). */
  backendUrl: string;
  /** Supabase JWT or exchanged session token presented to the backend. */
  token?: string;
  /** Isolated CODEX_HOME. Stable across runs so threads can be resumed. */
  codexHome: string;
  /** Codex binary (name on PATH or absolute path). */
  codexBin: string;
  /** Directory the agent works in. */
  workspace: string;
  /** Optional model override for Codex runs. */
  model?: string;
  /** Repo root (contains skills/ and config/). */
  root: string;
  verbose: boolean;
}

/** Repo root: agent/src/config.ts or agent/dist/config.js → ../../ */
export function repoRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
}

const stripSlash = (u: string) => u.replace(/\/+$/, "");

/** Flags win over env, env over defaults. */
export function resolveConfig(flags: Partial<AgentConfig> = {}, env: NodeJS.ProcessEnv = process.env): AgentConfig {
  const home = env.HOME ?? os.homedir();
  return {
    backendUrl: stripSlash(flags.backendUrl ?? env.OPENCLICKY_BACKEND_URL ?? env.BACKEND_URL ?? "http://localhost:8787"),
    token: flags.token ?? env.OPENCLICKY_TOKEN ?? undefined,
    codexHome: flags.codexHome ?? env.OPENCLICKY_CODEX_HOME ?? path.join(home, ".openclicky", "codex-home"),
    codexBin: flags.codexBin ?? env.OPENCLICKY_CODEX_BIN ?? "codex",
    workspace: path.resolve(flags.workspace ?? env.OPENCLICKY_WORKSPACE ?? process.cwd()),
    model: flags.model ?? env.OPENCLICKY_MODEL ?? undefined,
    root: flags.root ?? repoRoot(),
    verbose: flags.verbose ?? env.OPENCLICKY_VERBOSE === "1",
  };
}
