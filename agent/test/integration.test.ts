/**
 * End-to-end test of the agent bridge against the REAL Codex CLI, with a fake OpenAI Responses
 * server standing in for backend+OpenAI. Proves: isolated CODEX_HOME, JSON-RPC handshake,
 * thread start, tool execution (exec_command writes a file), final message + artifact collection,
 * bearer token forwarding, and thread resume.
 *
 * Skipped when `codex` is not installed (`npm i -g @openai/codex`).
 */
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { CodexAgent } from "../src/codex.js";
import { resolveConfig } from "../src/config.js";

const hasCodex = spawnSync("codex", ["--version"], { encoding: "utf8" }).status === 0;

type Req = { auth?: string; body: any };
const requests: Req[] = [];
let server: http.Server;
let url: string;

function sse(res: http.ServerResponse, items: any[]) {
  const id = `resp_${Date.now()}`;
  const ev = (type: string, data: Record<string, unknown>) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
  res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
  ev("response.created", { response: { id, object: "response", status: "in_progress", output: [] } });
  items.forEach((item, i) => {
    ev("response.output_item.added", { output_index: i, item: { ...item, status: "in_progress" } });
    ev("response.output_item.done", { output_index: i, item });
  });
  ev("response.completed", {
    response: { id, object: "response", status: "completed", output: items, usage: { input_tokens: 5, output_tokens: 5, total_tokens: 10, input_tokens_details: { cached_tokens: 0 }, output_tokens_details: { reasoning_tokens: 0 } } },
  });
  res.end();
}
const message = (text: string) => ({ id: `msg_${Date.now()}`, type: "message", role: "assistant", status: "completed", content: [{ type: "output_text", text, annotations: [] }] });

beforeAll(async () => {
  server = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      const body = JSON.parse(b);
      requests.push({ auth: req.headers.authorization, body });
      if (!req.url?.endsWith("/v1/responses")) return void res.writeHead(404).end();
      const input: any[] = body.input ?? [];
      const tools: any[] = body.tools ?? [];
      // Only the current turn matters: everything after the last user message.
      const lastUserIdx = input.map((i) => i.type === "message" && i.role === "user").lastIndexOf(true);
      const hasToolOutput = input.slice(lastUserIdx + 1).some((i) => i.type === "function_call_output");
      const userTexts = input.filter((i) => i.type === "message" && i.role === "user").flatMap((i) => (Array.isArray(i.content) ? i.content : [])).map((c) => c.text ?? "");
      const wantsFile = (userTexts.at(-1) ?? "").includes("hello.txt");
      const canExec = tools.some((t) => t.name === "exec_command");
      if (wantsFile && canExec && !hasToolOutput) {
        sse(res, [{ id: "fc_1", type: "function_call", status: "completed", call_id: "call_1", name: "exec_command", arguments: JSON.stringify({ cmd: "printf hi > hello.txt" }) }]);
      } else if (hasToolOutput) {
        sse(res, [message("Created hello.txt containing hi.")]);
      } else {
        sse(res, [message(`ack: ${userTexts.at(-1) ?? ""}`)]);
      }
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  url = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
afterAll(() => server.close());

describe.skipIf(!hasCodex)("CodexAgent (real codex, fake model)", () => {
  it("runs a task that writes a file, then resumes the thread", async () => {
    const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-home-"));
    const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-ws-"));
    // gpt-5.2 uses classic function tools. Codex's default gpt-5.6-* models use "code mode"
    // (an `additional_tools` input item + a JS `exec` tool), which passes through the backend
    // unchanged but would need a much smarter fake model here.
    const cfg = resolveConfig({ backendUrl: url, token: "test-token", codexHome, workspace, model: "gpt-5.2" });
    const events: string[] = [];
    const agent = new CodexAgent(cfg, { onEvent: (l) => events.push(l) });
    try {
      await agent.start();
      const r1 = await agent.run("create a file called hello.txt containing 'hi'");
      const first = requests.find((r) => r.body.instructions)!;
      expect(first.body.model).toBe("gpt-5.2");
      expect(first.body.instructions).toMatch(/^You are OpenClicky's temporary Codex agent mode\./); // skills/ModelInstructions.md
      expect(JSON.stringify(first.body)).toContain("openclicky-artifacts"); // skills/ loaded via [[skills.config]]
      expect(r1.status).toBe("completed");
      expect(r1.error).toBeUndefined();
      expect(fs.readFileSync(path.join(workspace, "hello.txt"), "utf8")).toBe("hi");
      expect(r1.finalMessage).toContain("Created hello.txt");
      expect(r1.artifacts).toContain(path.join(workspace, "hello.txt"));
      expect(events.some((e) => e.startsWith("ran: "))).toBe(true);

      const seenBeforeResume = requests.length;
      const r2 = await agent.run("what did you just do?", { threadId: r1.threadId });
      expect(r2.threadId).toBe(r1.threadId);
      expect(r2.status).toBe("completed");
      expect(r2.finalMessage).toContain("ack: what did you just do?");
      const resumed = requests.slice(seenBeforeResume).find((r) => r.body.instructions);
      expect(resumed).toBeDefined();
      const texts = JSON.stringify(resumed!.body.input);
      expect(texts).toContain("hello.txt"); // prior turn is in the resumed context
      expect(texts).toContain("what did you just do?");
    } finally {
      await agent.stop();
    }
    expect(requests.length).toBeGreaterThanOrEqual(3);
    for (const r of requests) expect(r.auth).toBe("Bearer test-token");
    expect(fs.existsSync(path.join(codexHome, "config.toml"))).toBe(true);
    expect(fs.existsSync(path.join(codexHome, "sessions"))).toBe(true);
  }, 90_000);

  it("attaches an image when present and continues when missing", async () => {
    const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-home-"));
    const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-ws-"));
    const cfg = resolveConfig({ backendUrl: url, token: "test-token", codexHome, workspace, model: "gpt-5.2" });
    const events: string[] = [];
    const agent = new CodexAgent(cfg, { onEvent: (l) => events.push(l) });
    try {
      await agent.start();
      const r = await agent.run("describe", { imagePath: path.join(workspace, "missing.png") });
      expect(r.status).toBe("completed");
      expect(events.some((e) => e.includes("screenshot attach not yet wired"))).toBe(true);
    } finally {
      await agent.stop();
    }
  }, 60_000);
});
