import fs from "node:fs";
import path from "node:path";
import type { AgentConfig } from "./config.js";

export interface AskOptions {
  imagePath?: string;
  system?: string;
  /** Streamed text deltas as they arrive. */
  onDelta?: (text: string) => void;
  fetchImpl?: typeof fetch;
}

export const ASK_SYSTEM_PROMPT =
  "You are OpenClicky's quick-answer lane (the \"higher model\"). Answer the user's question directly and " +
  "concisely, in a voice that could be read aloud. If a screenshot is attached, treat it as the user's " +
  "current desktop context. Do not claim to have performed actions; this lane only answers.";

const MIME: Record<string, string> = { ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".gif": "image/gif", ".webp": "image/webp" };

export function imageToDataUrl(imagePath: string): string {
  const mime = MIME[path.extname(imagePath).toLowerCase()] ?? "image/png";
  return `data:${mime};base64,${fs.readFileSync(imagePath).toString("base64")}`;
}

/** Extract text deltas from an OpenAI chat-completions SSE buffer. Returns the joined text and the unparsed tail. */
export function parseSseChunk(buffer: string): { text: string; rest: string; done: boolean } {
  let text = "";
  let done = false;
  let rest = buffer;
  let i: number;
  while ((i = rest.indexOf("\n\n")) >= 0) {
    const event = rest.slice(0, i);
    rest = rest.slice(i + 2);
    for (const line of event.split("\n")) {
      if (!line.startsWith("data:")) continue;
      const data = line.slice(5).trim();
      if (data === "[DONE]") {
        done = true;
        continue;
      }
      try {
        const json = JSON.parse(data);
        const delta = json.choices?.[0]?.delta?.content ?? json.choices?.[0]?.message?.content;
        if (typeof delta === "string") text += delta;
      } catch {
        /* ignore keep-alives / partial */
      }
    }
  }
  return { text, rest, done };
}

/** Whole-buffer convenience for tests and non-streaming responses. */
export function parseSseStream(text: string): string {
  return parseSseChunk(text.endsWith("\n\n") ? text : text + "\n\n").text;
}

/**
 * The lightweight "ask" lane: one chat completion through the backend, no agent spawn.
 * Keys never touch this process; the backend picks the model (OPENAI_MODEL) when we send "default".
 */
export async function ask(cfg: AgentConfig, question: string, opts: AskOptions = {}): Promise<string> {
  if (!cfg.token) throw new Error("missing token: set OPENCLICKY_TOKEN or pass --token (Supabase JWT or session token)");
  const f = opts.fetchImpl ?? fetch;

  const userContent: unknown = opts.imagePath
    ? [
        { type: "text", text: question },
        { type: "image_url", image_url: { url: imageToDataUrl(opts.imagePath) } },
      ]
    : question;

  const res = await f(`${cfg.backendUrl}/v1/chat/completions`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "text/event-stream", authorization: `Bearer ${cfg.token}` },
    body: JSON.stringify({
      model: "default",
      stream: true,
      messages: [
        { role: "system", content: opts.system ?? ASK_SYSTEM_PROMPT },
        { role: "user", content: userContent },
      ],
    }),
  });
  if (!res.ok) throw new Error(`backend ${res.status}: ${(await res.text()).slice(0, 500)}`);

  const contentType = res.headers.get("content-type") ?? "";
  if (!contentType.includes("text/event-stream")) {
    // Non-streaming upstream (e.g. a provider that ignores stream:true).
    const json: any = await res.json();
    const text = json.choices?.[0]?.message?.content ?? "";
    opts.onDelta?.(text);
    return text;
  }

  let out = "";
  let buffer = "";
  const decoder = new TextDecoder();
  for await (const chunk of res.body as unknown as AsyncIterable<Uint8Array>) {
    buffer += decoder.decode(chunk, { stream: true });
    const { text, rest, done } = parseSseChunk(buffer);
    buffer = rest;
    if (text) {
      out += text;
      opts.onDelta?.(text);
    }
    if (done) break;
  }
  return out;
}
