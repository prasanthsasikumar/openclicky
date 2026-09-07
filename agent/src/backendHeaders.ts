import type { AgentConfig } from "./config.js";

/**
 * Headers every backend call carries: the bearer token plus the user's own provider keys when
 * they brought them (bring your own key). The backend uses those keys instead of its own for
 * that request and meters nothing; without them the request runs under the user's plan.
 */
export function backendHeaders(cfg: Pick<AgentConfig, "token" | "openaiApiKey" | "anthropicApiKey">, extra: Record<string, string> = {}): Record<string, string> {
  return {
    ...extra,
    authorization: `Bearer ${cfg.token ?? ""}`,
    ...(cfg.openaiApiKey ? { "x-openclicky-openai-key": cfg.openaiApiKey } : {}),
    ...(cfg.anthropicApiKey ? { "x-openclicky-anthropic-key": cfg.anthropicApiKey } : {}),
  };
}
