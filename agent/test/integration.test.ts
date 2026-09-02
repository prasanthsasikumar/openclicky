/**
 * End-to-end test of the agent bridge against the REAL Codex CLI, with a fake OpenAI Responses
 * server standing in for backend+OpenAI. Proves: isolated CODEX_HOME, JSON-RPC handshake,
 * thread start, tool execution (exec_command writes a file), final message + artifact collection,
 * streamed deltas, bearer token forwarding, thread resume/list/read/archive, and approvals.
 *
 * Skipped when `codex` is not installed (`npm i -g @openai/codex`).
 */
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { CodexAgent, type ApprovalRequest } from "../src/codex.js";
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
    ev("response.output_item.added", { output_index: i, item: { ...item, status: "in_progress", ...(item.type === "message" ? { content: [] } : {}) } });
    if (item.type === "message") {
      const text: string = item.content[0].text;
      for (const word of text.split(/(?<= )/)) ev("response.output_text.delta", { item_id: item.id, output_index: i, content_index: 0, delta: word });
      ev("response.output_text.done", { item_id: item.id, output_index: i, content_index: 0, text });
    }
    ev("response.output_item.done", { output_index: i, item });
  });
  ev("response.completed", {
    response: { id, object: "response", status: "completed", output: items, usage: { input_tokens: 5, output_tokens: 5, total_tokens: 10, input_tokens_details: { cached_tokens: 0 }, output_tokens_details: { reasoning_tokens: 0 } } },
  });
  res.end();
}
const message = (text: string) => ({ id: `msg_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`, type: "message", role: "assistant", status: "completed", content: [{ type: "output_text", text, annotations: [] }] });
const call = (name: string, args: Record<string, unknown>) => ({ id: `fc_${Date.now()}`, type: "function_call", status: "completed", call_id: `call_${Date.now()}`, name, arguments: JSON.stringify(args) });

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
      const tail = input.slice(lastUserIdx + 1);
      const toolOutputs = tail.filter((i) => i.type === "function_call_output");
      const userTexts = input.filter((i) => i.type === "message" && i.role === "user").flatMap((i) => (Array.isArray(i.content) ? i.content : [])).map((c) => c.text ?? "");
      const last = userTexts.at(-1) ?? "";
      const canExec = tools.some((t) => t.name === "exec_command");
      if (last.includes("hello.txt") && canExec && !toolOutputs.length) {
        sse(res, [call("exec_command", { cmd: "printf hi > hello.txt" })]);
      } else if (last.includes("ESCALATE") && canExec && !toolOutputs.length) {
        // Ask Codex for an escalated (unsandboxed) command -> triggers item/commandExecution/requestApproval.
        sse(res, [call("exec_command", { cmd: "printf escalated > escalated.txt", sandbox_permissions: "require_escalated", justification: "needs to write outside the sandbox" })]);
      } else if (toolOutputs.length) {
        const out = JSON.stringify(toolOutputs.map((t) => t.output));
        sse(res, [message(last.includes("ESCALATE") ? `tool result: ${out.slice(0, 200)}` : "Created hello.txt containing hi.")]);
      } else {
        sse(res, [message(`ack: ${last}`)]);
      }
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  url = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
afterAll(() => server.close());

// gpt-5.2 uses classic function tools. Codex's default gpt-5.6-* models use "code mode"
// (an `additional_tools` input item + a JS `exec` tool), which passes through the backend
// unchanged but would need a much smarter fake model here.
const mk = () => {
  const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-home-"));
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "oc-it-ws-"));
  return { codexHome, workspace, cfg: resolveConfig({ backendUrl: url, token: "test-token", codexHome, workspace, model: "gpt-5.2" }) };
};

describe.skipIf(!hasCodex)("CodexAgent (real codex, fake model)", () => {
  it("runs a task that writes a file, streams deltas, then resumes/lists/reads/archives the thread", async () => {
    const { codexHome, workspace, cfg } = mk();
    const events: string[] = [];
    const deltas: string[] = [];
    const agent = new CodexAgent(cfg, { onEvent: (l) => events.push(l), onDelta: (d) => deltas.push(d) });
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
      expect(r1.finalMessage).toBe("Created hello.txt containing hi.");
      expect(deltas.join("")).toBe("Created hello.txt containing hi.");
      expect(r1.artifacts).toContain(path.join(workspace, "hello.txt"));
      expect(events.some((e) => e.startsWith("ran: "))).toBe(true);

      const seenBeforeResume = requests.length;
      const r2 = await agent.run("what did you just do?", { threadId: r1.threadId });
      expect(r2.threadId).toBe(r1.threadId);
      expect(r2.status).toBe("completed");
      expect(r2.finalMessage).toBe("ack: what did you just do?");
      const resumed = requests.slice(seenBeforeResume).find((r) => r.body.instructions)!;
      const texts = JSON.stringify(resumed.body.input);
      expect(texts).toContain("hello.txt"); // prior turn is in the resumed context
      expect(texts).toContain("what did you just do?");

      const list = await agent.listThreads(10);
      expect(list.map((t) => t.id)).toContain(r1.threadId);
      expect(list.find((t) => t.id === r1.threadId)!.cwd).toBe(workspace);

      const read = await agent.readThread(r1.threadId);
      expect(read.thread.id).toBe(r1.threadId);
      expect(read.turns.length).toBe(2);
      expect(read.turns[0].user).toEqual(["create a file called hello.txt containing 'hi'"]);
      expect(read.turns[0].commands.some((c) => c.includes("hello.txt"))).toBe(true);
      expect(read.turns[1].agent).toEqual(["ack: what did you just do?"]);

      await agent.archiveThread(r1.threadId);
      expect((await agent.listThreads(10)).map((t) => t.id)).not.toContain(r1.threadId);
    } finally {
      await agent.stop();
    }
    expect(requests.length).toBeGreaterThanOrEqual(3);
    for (const r of requests) expect(r.auth).toBe("Bearer test-token");
    expect(fs.existsSync(path.join(codexHome, "config.toml"))).toBe(true);
    expect(fs.existsSync(path.join(codexHome, "sessions"))).toBe(true);
  }, 90_000);

  it("routes escalated commands through the onApproval hook (accept, then decline)", async () => {
    const { workspace, cfg } = mk();
    const asked: ApprovalRequest[] = [];
    let decision: "accept" | "decline" = "accept";
    const agent = new CodexAgent(cfg, { onApproval: async (req) => (asked.push(req), decision) });
    try {
      await agent.start();
      const r1 = await agent.run("ESCALATE please");
      expect(r1.status).toBe("completed");
      expect(asked.length).toBe(1);
      expect(asked[0].kind).toBe("command");
      expect(asked[0].summary).toContain("escalated.txt");
      expect(fs.readFileSync(path.join(workspace, "escalated.txt"), "utf8")).toBe("escalated");
      expect(r1.artifacts).toContain(path.join(workspace, "escalated.txt"));

      decision = "decline";
      const r2 = await agent.run("ESCALATE again");
      expect(asked.length).toBe(2);
      expect(r2.status).toBe("completed");
      expect(r2.finalMessage.toLowerCase()).toMatch(/reject|declin|denied|not approved|approval/);
    } finally {
      await agent.stop();
    }
  }, 90_000);

  it("attaches an image when present and continues when missing", async () => {
    const { workspace, cfg } = mk();
    const events: string[] = [];
    const agent = new CodexAgent(cfg, { onEvent: (l) => events.push(l) });
    try {
      await agent.start();
      const r = await agent.run("describe", { imagePath: path.join(workspace, "missing.png") });
      expect(r.status).toBe("completed");
      expect(events.some((e) => e.includes("screenshot attach not yet wired"))).toBe(true);
      const png = path.join(workspace, "shot.png");
      // 1x1 transparent PNG
      fs.writeFileSync(png, Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64"));
      const before = requests.length;
      const r2 = await agent.run("describe this", { threadId: r.threadId, imagePath: png });
      expect(r2.status).toBe("completed");
      const req = requests.slice(before).find((x) => x.body.instructions)!;
      expect(JSON.stringify(req.body.input)).toContain("input_image");
    } finally {
      await agent.stop();
    }
  }, 60_000);
});
