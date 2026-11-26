import type { AgentConfig } from "./config.js";
import { anthropicRoute, chatRoute, chatModel, DIRECT_DEFAULTS } from "./providerRoute.js";

/**
 * The cheap "gate" in front of every launch (HeyClicky's Haiku launch-label gate): decide whether a
 * request is a quick question for the ask lane or real work for a Codex agent thread.
 */
export type Lane = "ask" | "agent";
export interface GateDecision {
  lane: Lane;
  reason: string;
  /** false when the gate could not run and the default lane was used. */
  gated: boolean;
}

export const GATE_SYSTEM_PROMPT = `You are OpenClicky's launch gate. Classify the user's request into exactly one lane:
- "ask": a question, explanation, opinion, translation, summary of provided text, or anything answerable in a short spoken reply with no files, tools, apps, or web work.
- "agent": anything that requires doing work: creating/editing files or code, running commands, research with sources, using apps or integrations, multi-step tasks, or anything referring to "my" files/repo/project.
Reply with ONLY a JSON object: {"lane":"ask"|"agent","reason":"<one short sentence>"}`;

export function parseGateReply(text: string): { lane: Lane; reason: string } | undefined {
  const m = /\{[\s\S]*\}/.exec(text);
  if (!m) return undefined;
  try {
    const j = JSON.parse(m[0]);
    if (j.lane === "ask" || j.lane === "agent") return { lane: j.lane, reason: String(j.reason ?? "") };
  } catch {
    /* fall through */
  }
  return undefined;
}

/** Local heuristic used when the gate model is unavailable. Errs toward the agent lane. */
export function heuristicLane(text: string): Lane {
  const t = text.trim().toLowerCase();
  const asksLike = /^(what|who|why|how|when|where|which|is|are|does|do you|can you tell|could you explain|should|explain|define|translate|summari[sz]e|tell me)\b/.test(t);
  const workLike = /\b(create|make|write|edit|fix|add|remove|delete|run|build|deploy|install|refactor|rename|move|open|search the web|research|report|commit|push|file|repo|project|folder|script)\b/.test(t);
  return asksLike && !workLike ? "ask" : "agent";
}

export async function gate(cfg: AgentConfig, text: string, opts: { fetchImpl?: typeof fetch } = {}): Promise<GateDecision> {
  const f = opts.fetchImpl ?? fetch;
  // With no Anthropic key, a free Google Gemini key or a local Ollama server decides the lane.
  if (!cfg.anthropicApiKey && (cfg.geminiApiKey || cfg.ollamaBaseUrl)) {
    return gateViaOpenAIChat(cfg, text, f);
  }
  // Claude decides the lane: the person's Anthropic key straight to Anthropic, else the backend on their token.
  const route = anthropicRoute(cfg, "/v1/messages", { "content-type": "application/json" });
  if (!route) return { lane: heuristicLane(text), reason: "no anthropic key or token; heuristic", gated: false };
  try {
    const res = await f(route.url, {
      method: "POST",
      headers: route.headers,
      body: JSON.stringify({
        model: route.direct ? DIRECT_DEFAULTS.anthropicModel : "default",
        max_tokens: 80,
        system: GATE_SYSTEM_PROMPT,
        messages: [{ role: "user", content: text }],
      }),
    });
    if (!res.ok) return { lane: heuristicLane(text), reason: `gate unavailable (${route.direct ? "anthropic" : "backend"} ${res.status}); heuristic`, gated: false };
    const json: any = await res.json();
    const reply = (json.content ?? []).filter((c: any) => c.type === "text").map((c: any) => c.text).join("\n");
    const parsed = parseGateReply(reply);
    if (!parsed) return { lane: heuristicLane(text), reason: "gate reply unparseable; heuristic", gated: false };
    return { ...parsed, gated: true };
  } catch (e) {
    return { lane: heuristicLane(text), reason: `gate error (${(e as Error).message}); heuristic`, gated: false };
  }
}


/** Gate via an OpenAI-compatible chat endpoint (Google Gemini free tier or local Ollama). */
async function gateViaOpenAIChat(cfg: AgentConfig, text: string, f: typeof fetch): Promise<GateDecision> {
  const route = chatRoute(cfg, "/v1/messages", { accept: "application/json" });
  if (!route) return { lane: heuristicLane(text), reason: "no gate route; heuristic", gated: false };
  try {
    const res = await f(route.url, {
      method: "POST", headers: route.headers,
      body: JSON.stringify({ model: chatModel(cfg, route), stream: false, messages: [{ role: "system", content: GATE_SYSTEM_PROMPT }, { role: "user", content: text }] }),
    });
    if (!res.ok) return { lane: heuristicLane(text), reason: `gate unavailable (${res.status}); heuristic`, gated: false };
    const json: any = await res.json();
    const reply = json.choices?.[0]?.message?.content ?? "";
    const parsed = parseGateReply(typeof reply === "string" ? reply : JSON.stringify(reply));
    if (!parsed) return { lane: heuristicLane(text), reason: "gate reply unparseable; heuristic", gated: false };
    return { ...parsed, gated: true };
  } catch (e) {
    return { lane: heuristicLane(text), reason: `gate error (${(e as Error).message}); heuristic`, gated: false };
  }
}
