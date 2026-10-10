/**
 * What a grant request costs, in integer micro-dollars. Prices are USD per million tokens, so
 * price × tokens is already micro-dollars. Models not in the table are refused, never guessed.
 */
export type TokenUsage = { inputTokens: number; outputTokens: number; cacheWriteTokens: number; cacheReadTokens: number };
type Price = { input: number; output: number; cacheWrite: number; cacheRead: number };

export const PRICES_USD_PER_MTOK: Record<string, Price> = {
  "claude-haiku-4-5": { input: 1, output: 5, cacheWrite: 1.25, cacheRead: 0.1 },
  "claude-sonnet-5-5": { input: 2, output: 10, cacheWrite: 2.5, cacheRead: 0.2 },
};

export function costMicroUsd(model: string, usage: TokenUsage): number | undefined {
  const price = PRICES_USD_PER_MTOK[model];
  if (!price) return undefined;
  return Math.ceil(
    usage.inputTokens * price.input + usage.outputTokens * price.output + usage.cacheWriteTokens * price.cacheWrite + usage.cacheReadTokens * price.cacheRead,
  );
}

const CHARS_PER_TOKEN = 3.5;
const TOKENS_PER_IMAGE = 1600;

function textTokens(text: string): number {
  return Math.ceil(text.length / CHARS_PER_TOKEN);
}

function contentTokens(content: unknown): number {
  if (typeof content === "string") return textTokens(content);
  if (!Array.isArray(content)) return 0;
  let tokens = 0;
  for (const block of content as Array<Record<string, unknown>>) {
    if (block.type === "image") tokens += TOKENS_PER_IMAGE;
    else if (typeof block.text === "string") tokens += textTokens(block.text);
    else if (block.type === "tool_result") tokens += contentTokens(block.content);
    else if (block.type === "tool_use") tokens += textTokens(JSON.stringify(block.input ?? {}));
  }
  return tokens;
}

/** Worst-case cost of a Messages body before it is sent: every input token uncached, every output token used. */
export function estimateMicroUsd(model: string, body: Record<string, unknown>): number | undefined {
  const price = PRICES_USD_PER_MTOK[model];
  if (!price) return undefined;
  let inputTokens = contentTokens(body.system);
  for (const message of (body.messages as Array<{ content: unknown }> | undefined) ?? []) inputTokens += contentTokens(message.content);
  if (Array.isArray(body.tools)) inputTokens += textTokens(JSON.stringify(body.tools));
  const outputTokens = Number(body.max_tokens ?? 0);
  return Math.ceil(inputTokens * price.input + outputTokens * price.output);
}

/** Usage from an Anthropic reply: SSE (`message_start` + `message_delta`) or a plain JSON body. */
export function parseAnthropicUsage(text: string): TokenUsage | undefined {
  const usage: TokenUsage = { inputTokens: 0, outputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0 };
  let found = false;
  const absorb = (u: Record<string, unknown> | undefined) => {
    if (!u) return;
    const num = (k: string) => (typeof u[k] === "number" ? (u[k]) : undefined);
    const i = num("input_tokens"), o = num("output_tokens"), w = num("cache_creation_input_tokens"), r = num("cache_read_input_tokens");
    if (i !== undefined) { usage.inputTokens = Math.max(usage.inputTokens, i); found = true; }
    if (o !== undefined) { usage.outputTokens = Math.max(usage.outputTokens, o); found = true; }
    if (w !== undefined) usage.cacheWriteTokens = Math.max(usage.cacheWriteTokens, w);
    if (r !== undefined) usage.cacheReadTokens = Math.max(usage.cacheReadTokens, r);
  };
  const visit = (json: Record<string, unknown>) => {
    absorb(json.usage as Record<string, unknown>);
    absorb((json.message as Record<string, unknown>)?.usage as Record<string, unknown>);
  };
  const trimmed = text.trim();
  if (trimmed.startsWith("{")) {
    try { visit(JSON.parse(trimmed)); } catch { /* not one JSON body; fall through to SSE lines */ }
  }
  for (const line of text.split("\n")) {
    if (!line.startsWith("data: ")) continue;
    try { visit(JSON.parse(line.slice(6))); } catch { /* keep-alive or partial line */ }
  }
  return found ? usage : undefined;
}
