/**
 * Black-box tests of the `openclicky` command line: spawns src/cli.ts via tsx against a fake backend
 * (chat completions, gate, and a message-only Responses API for the real codex binary).
 */
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const cli = path.resolve(here, "..", "src", "cli.ts");
const tsx = path.resolve(here, "..", "..", "node_modules", ".bin", "tsx");
const hasCodex = spawnSync("codex", ["--version"], { encoding: "utf8" }).status === 0;
const fakeCodex = path.join(here, "fixtures", "fake-codex.mjs");

let server: http.Server;
let url: string;
const signInRequests: Array<{ email: string; password: string }> = [];

function runCli(args: string[], env: Record<string, string> = {}): Promise<{ code: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve) => {
    const p = spawn(tsx, [cli, ...args], { env: { ...process.env, BACKEND_URL: url, OPENCLICKY_TOKEN: "cli-token", OPENCLICKY_CODEX_HOME: fs.mkdtempSync(path.join(os.tmpdir(), "oc-cli-home-")), ...env } });
    let stdout = "";
    let stderr = "";
    p.stdout.on("data", (d) => (stdout += d));
    p.stderr.on("data", (d) => (stderr += d));
    p.on("close", (code) => resolve({ code, stdout, stderr }));
  });
}

beforeAll(async () => {
  server = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      const body = b ? JSON.parse(b) : {};
      // Fake Supabase sign-in + session-token exchange for the `token` command's password tests
      // (agent/src/cli.ts): neither uses the "cli-token" bearer the rest of this server requires.
      if (req.url?.startsWith("/auth/v1/token")) {
        signInRequests.push({ email: body.email, password: body.password });
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ access_token: "fake-access-token" }));
      }
      if (req.url === "/agent/session-token") {
        if (req.headers.authorization !== "Bearer fake-access-token") return void res.writeHead(401).end();
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ token: "exchanged-session-token", expiresAt: Math.floor(Date.now() / 1000) + 3600 }));
      }
      if (req.headers.authorization !== "Bearer cli-token") return void res.writeHead(401, { "content-type": "application/json" }).end('{"error":"bad token"}');
      if (req.url === "/v1/chat/completions") {
        res.writeHead(200, { "content-type": "text/event-stream" });
        for (const w of ["Hello ", "from ", "fake"]) res.write(`data: ${JSON.stringify({ choices: [{ delta: { content: w } }] })}\n\n`);
        return void res.end("data: [DONE]\n\n");
      }
      if (req.url === "/v1/messages") {
        const q: string = body.messages?.at(-1)?.content ?? "";
        const lane = /\bfile\b/.test(q) ? "agent" : "ask";
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ content: [{ type: "text", text: JSON.stringify({ lane, reason: "fake gate" }) }] }));
      }
      if (req.url === "/skills/create") {
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ id: "pirate-voice", name: "Pirate Voice", description: "talk like a pirate", markdown: `---\nname: Pirate Voice\ndescription: talk like a pirate (${body.request})\nsurfaces: [talk]\n---\n# Pirate\nArr.\n` }));
      }
      if (req.url === "/v1/responses") {
        const id = "resp_1";
        const item = { id: "msg_1", type: "message", role: "assistant", status: "completed", content: [{ type: "output_text", text: "agent says hi", annotations: [] }] };
        res.writeHead(200, { "content-type": "text/event-stream" });
        const ev = (type: string, data: Record<string, unknown>) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
        ev("response.created", { response: { id, object: "response", status: "in_progress", output: [] } });
        ev("response.output_item.added", { output_index: 0, item: { ...item, status: "in_progress", content: [] } });
        ev("response.output_text.delta", { item_id: "msg_1", output_index: 0, content_index: 0, delta: "agent says hi" });
        ev("response.output_item.done", { output_index: 0, item });
        ev("response.completed", { response: { id, object: "response", status: "completed", output: [item], usage: { input_tokens: 1, output_tokens: 1, total_tokens: 2, input_tokens_details: { cached_tokens: 0 }, output_tokens_details: { reasoning_tokens: 0 } } } });
        return void res.end();
      }
      res.writeHead(404).end();
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  url = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
afterAll(() => server.close());

describe("openclicky CLI", () => {
  it("skills create/list/activate/deactivate manage the user library", async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cli-skills-"));
    const env = { OPENCLICKY_USER_SKILLS_DIR: dir };
    const created = await runCli(["skills", "create", "talk like a pirate"], env);
    expect(created.code).toBe(0);
    expect(created.stdout.trim()).toBe("pirate-voice");
    expect(fs.readFileSync(path.join(dir, "library", "pirate-voice", "SKILL.md"), "utf8")).toContain("talk like a pirate");
    const list = await runCli(["skills", "list", "--json"], env);
    expect(JSON.parse(list.stdout)).toMatchObject([{ id: "pirate-voice", active: true, surfaces: ["talk"] }]);
    expect((await runCli(["skills", "deactivate", "pirate-voice"], env)).code).toBe(0);
    expect(fs.existsSync(path.join(dir, "active", "pirate-voice"))).toBe(false);
    expect((await runCli(["skills", "activate", "pirate-voice"], env)).code).toBe(0);
    expect(fs.lstatSync(path.join(dir, "active", "pirate-voice")).isSymbolicLink()).toBe(true);
    const human = await runCli(["skills", "list"], env);
    expect(human.stdout).toMatch(/\[on\]\s+pirate-voice/);
    expect((await runCli(["skills", "path"], env)).stdout.trim()).toBe(dir);
    expect((await runCli(["skills", "activate", "nope"], env)).code).toBe(1);
  });
  it("ask streams the answer to stdout and exits 0", async () => {
    const r = await runCli(["ask", "say", "hello"]);
    expect(r.code).toBe(0);
    expect(r.stdout).toBe("Hello from fake\n");
  });

  it("ask --events emits JSON Lines", async () => {
    const r = await runCli(["ask", "hi", "--events"]);
    expect(r.code).toBe(0);
    const lines = r.stdout.trim().split("\n").map((l) => JSON.parse(l));
    expect(lines.filter((l) => l.type === "delta").map((l) => l.text).join("")).toBe("Hello from fake");
    expect(lines.at(-1)).toEqual({ type: "answer", text: "Hello from fake" });
  });

  it("fails with a non-zero exit and an error line on a bad token", async () => {
    const r = await runCli(["ask", "hi"], { OPENCLICKY_TOKEN: "wrong" });
    expect(r.code).toBe(1);
    expect(r.stderr).toMatch(/error: backend 401/);
    const ev = await runCli(["ask", "hi", "--events"], { OPENCLICKY_TOKEN: "wrong" });
    expect(ev.code).toBe(1);
    expect(JSON.parse(ev.stdout.trim())).toMatchObject({ type: "error", message: expect.stringContaining("401") });
  });

  it("do routes through the gate and reports the lane", async () => {
    const r = await runCli(["do", "what is up", "--events"]);
    expect(r.code).toBe(0);
    const lines = r.stdout.trim().split("\n").map((l) => JSON.parse(l));
    expect(lines[0]).toEqual({ type: "lane", lane: "ask", gated: true, reason: "fake gate" });
    expect(lines.at(-1)).toEqual({ type: "answer", text: "Hello from fake" });
  });

  it("do --gate-only classifies without running a lane", async () => {
    const plain = await runCli(["do", "make a file", "--gate-only"]);
    expect(plain.code).toBe(0);
    expect(plain.stdout).toBe("agent\n");
    const ev = await runCli(["do", "what is up", "--gate-only", "--events"]);
    expect(JSON.parse(ev.stdout.trim())).toEqual({ type: "lane", lane: "ask", gated: true, reason: "fake gate" });
  });

  it.skipIf(!hasCodex)("run --events streams deltas and a result through the real codex binary", async () => {
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cli-ws-"));
    const r = await runCli(["run", "say hi", "--events", "--cwd", ws, "--model", "gpt-5.2"]);
    expect(r.code).toBe(0);
    const lines = r.stdout.trim().split("\n").map((l) => JSON.parse(l));
    expect(lines.some((l) => l.type === "event" && /started thread/.test(l.line))).toBe(true);
    expect(lines.filter((l) => l.type === "delta").map((l) => l.text).join("")).toBe("agent says hi");
    const result = lines.at(-1);
    expect(result).toMatchObject({ type: "result", status: "completed", finalMessage: "agent says hi", artifacts: [] });
    expect(typeof result.threadId).toBe("string");
  }, 60_000);

  it("stops the Codex child instead of orphaning it when a failed run exits the CLI (fake codex)", async () => {
    // `run`'s fail() call for a non-completed turn happens inside `try { … } finally { await
    // agent.stop(); }` (cli.ts runAgent). Before the fix, fail() called process.exit() there and
    // skipped that finally, leaving the Codex child running after the CLI process itself was gone.
    const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cli-fail-home-"));
    fs.writeFileSync(path.join(codexHome, "fake-scenario.txt"), "fail-turn");
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "oc-cli-fail-ws-"));
    const r = await runCli(["run", "do something", "--cwd", ws], {
      OPENCLICKY_CODEX_HOME: codexHome,
      OPENCLICKY_CODEX_BIN: fakeCodex,
    });
    expect(r.code).toBe(1); // exit code unchanged from before this fix
    expect(r.stderr).toMatch(/error: turn failed: boom/);

    const pid = Number(fs.readFileSync(path.join(codexHome, "fake-codex.pid"), "utf8"));
    const isAlive = () => {
      try {
        process.kill(pid, 0);
        return true;
      } catch {
        return false; // ESRCH: no such process
      }
    };
    // The CLI's own process has already exited (runCli awaited `close`); give a stubborn OS a brief
    // moment, but the grandchild should already be gone by the time agent.stop()'s own exit-wait
    // resolved inside the CLI process, well before that process exited.
    for (let i = 0; i < 20 && isAlive(); i++) await new Promise((r2) => setTimeout(r2, 50));
    expect(isAlive()).toBe(false);
  });

  describe("token", () => {
    // `url` is only assigned inside beforeAll, which runs after this describe body, so build the
    // args fresh per test rather than capturing `url` (still undefined here) in a shared constant.
    const tokenArgs = () => ["token", "--email", "a@b.com", "--supabase-url", url, "--anon-key", "anon-key"];

    it("--password works but warns to stderr that it is insecure", async () => {
      const before = signInRequests.length;
      const r = await runCli([...tokenArgs(), "--password", "hunter2"]);
      expect(r.code).toBe(0);
      expect(r.stdout.trim()).toBe("exchanged-session-token");
      expect(r.stderr).toMatch(/insecure/);
      expect(signInRequests.slice(before)).toEqual([{ email: "a@b.com", password: "hunter2" }]);
    });

    it("prefers OPENCLICKY_PASSWORD over prompting, without the insecurity warning", async () => {
      const before = signInRequests.length;
      const r = await runCli(tokenArgs(), { OPENCLICKY_PASSWORD: "from-env" });
      expect(r.code).toBe(0);
      expect(r.stdout.trim()).toBe("exchanged-session-token");
      expect(r.stderr).not.toMatch(/insecure/);
      expect(signInRequests.slice(before)).toEqual([{ email: "a@b.com", password: "from-env" }]);
    });

    it("fails clearly instead of hanging when there is no flag, no env var, and no TTY to prompt on", async () => {
      const r = await runCli(tokenArgs());
      expect(r.code).toBe(1);
      expect(r.stderr).toMatch(/password required/);
    });
  });
});
