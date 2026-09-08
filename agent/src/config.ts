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
  /** Composio MCP server URL; when set, the `composio` MCP server is rendered into the Codex config. */
  composioMcpUrl?: string;
  /** Composio consumer API key (`ck_…`, dashboard.composio.dev); sent as `x-consumer-api-key`. Without it
   *  Codex's MCP OAuth login (`codex mcp login composio`) authenticates the server. */
  composioApiKey?: string;
  /** Path to cua-driver; when set, the `computer-use` MCP server is rendered into the Codex config. */
  cuaDriverBin?: string;
  /** ffmpeg binary used for microphone capture in the voice lane. */
  ffmpegBin: string;
  /** The user's skill library root (library/, active/, activations.json). Shared with the macOS app. */
  userSkillsDir: string;
  /** The user's own OpenAI key (bring your own key): sent to the backend per request, never stored there. */
  openaiApiKey?: string;
  /** Optional Anthropic key for the Claude lanes when bringing your own keys. */
  anthropicApiKey?: string;
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
    backendUrl: stripSlash(flags.backendUrl ?? env.OPENCLICKY_BACKEND_URL ?? env.BACKEND_URL ?? "https://api.openclicky.flowsxr.com"),
    token: flags.token ?? env.OPENCLICKY_TOKEN ?? undefined,
    codexHome: flags.codexHome ?? env.OPENCLICKY_CODEX_HOME ?? path.join(home, ".openclicky", "codex-home"),
    codexBin: flags.codexBin ?? env.OPENCLICKY_CODEX_BIN ?? "codex",
    workspace: path.resolve(flags.workspace ?? env.OPENCLICKY_WORKSPACE ?? process.cwd()),
    model: flags.model ?? env.OPENCLICKY_MODEL ?? undefined,
    root: flags.root ?? repoRoot(),
    verbose: flags.verbose ?? env.OPENCLICKY_VERBOSE === "1",
    composioMcpUrl: flags.composioMcpUrl ?? env.COMPOSIO_MCP_URL ?? undefined,
    composioApiKey: flags.composioApiKey ?? env.COMPOSIO_API_KEY ?? undefined,
    cuaDriverBin: flags.cuaDriverBin ?? env.CUA_DRIVER_BIN ?? undefined,
    ffmpegBin: flags.ffmpegBin ?? env.OPENCLICKY_FFMPEG_BIN ?? "ffmpeg",
    userSkillsDir: path.resolve(flags.userSkillsDir ?? env.OPENCLICKY_USER_SKILLS_DIR ?? path.join(home, ".openclicky", "skills")),
    openaiApiKey: flags.openaiApiKey ?? env.OPENCLICKY_OPENAI_KEY ?? undefined,
    anthropicApiKey: flags.anthropicApiKey ?? env.OPENCLICKY_ANTHROPIC_KEY ?? undefined,
  };
}
