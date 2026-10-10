import type { Env } from "./env.js";
import { PRICES_USD_PER_MTOK } from "./prices.js";

/** Which model the server uses for a grant request; the client's choice is not trusted. */
export type Purpose = "polish" | "edit" | "ask" | "gate";

const DEFAULTS: Record<Purpose, string> = {
  polish: "claude-haiku-4-5",
  edit: "claude-haiku-4-5",
  ask: "claude-sonnet-5-5",
  gate: "claude-haiku-4-5",
};

export function modelFor(purpose: Purpose, env: Env): string {
  const override = { polish: env.POLISH_MODEL, edit: env.EDIT_MODEL, ask: env.ASK_MODEL, gate: env.GATE_MODEL }[purpose];
  return override?.trim() || DEFAULTS[purpose];
}

export function isGrantModel(model: string): boolean {
  return model in PRICES_USD_PER_MTOK;
}
