import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import { JsonRpcStdio } from "./jsonrpc.js";
import { readVersion } from "./version.js";
import { snapshotWorkspace, diffSnapshots } from "./artifacts.js";
import type { AgentConfig } from "./config.js";
import { spawnTarget } from "./winSpawn.js";

/**
 * OpenCode agent, replacing the OpenAI Codex CLI integration.
 *
 * Drives `opencode acp` (Agent Client Protocol, JSON-RPC over stdio) the way `CodexAgent` drove
 * `codex app-server --stdio`: session start/resume, prompt with optional image, streamed text,
 * tool-call milestones, permission (approval) requests, thread resume/list/read. OpenCode manages
 * its own model providers (free `opencode/*`, Google Gemini via GEMINI_API_KEY, or local Ollama);
 * no paid OpenAI/Anthropic key is required. See https://agentclientprotocol.com.
 */

export interface RunResult {
  threadId: string;
  turnId: string;
  status: "completed" | "failed" | "interrupted";
  finalMessage: string;
  artifacts: string[];
  error?: string;
}

export interface RunOptions {
  /** Resume an existing OpenCode session instead of starting a new one. */
  threadId?: string;
  /** Local screenshot/image attached to the prompt (ACP `image` prompt part). */
  imagePath?: string;
  /** Override the run's timeout (ms) for this call only; see DEFAULT_RUN_TIMEOUT_MS. */
  timeoutMs?: number;
}

/** Wall-clock cap on a single run before `run()` rejects instead of hanging forever. */
export const DEFAULT_RUN_TIMEOUT_MS = 10 * 60 * 1000;

/** Interactive-shell and runtime variables OpenCode's own tooling expects. */
const SHELL_ENV_ALLOWLIST = ["PATH", "HOME", "USER", "SHELL", "TMPDIR", "LANG", "LC_ALL", "TERM"];
const PROXY_ENV_ALLOWLIST = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"];
const WINDOWS_ENV_ALLOWLIST = [
  "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
  "USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
  "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "PSModulePath",
];

/**
 * Build the OpenCode child's environment from an explicit allowlist instead of forwarding the whole
 * shell. OpenCode runs an agent that executes arbitrary shell commands, so provider keys
 * (OPENAI_API_KEY, ANTHROPIC_API_KEY, …) in the operator's shell must not leak into every command
 * it runs. A free Gemini key for OpenCode itself is passed explicitly as GEMINI_API_KEY when
 * `cfg.geminiApiKey`/`OPENCLICKY_GEMINI_KEY` is set; local Ollama needs no key.
 */
export function buildChildEnv(cfg: AgentConfig, sourceEnv: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const key of [...SHELL_ENV_ALLOWLIST, ...PROXY_ENV_ALLOWLIST, ...(platform === "win32" ? WINDOWS_ENV_ALLOWLIST : [])]) {
    if (sourceEnv[key] !== undefined) env[key] = sourceEnv[key];
  }
  for (const [key, value] of Object.entries(sourceEnv)) {
    if (key.startsWith("NODE_") && value !== undefined) env[key] = value;
  }
  // OpenCode's own free model needs no key; a Gemini key opts the agent lane into Gemini. Never
  // forward OpenAI/Anthropic keys to the child.
  const geminiKey = cfg.geminiApiKey ?? sourceEnv.OPENCLICKY_GEMINI_KEY ?? sourceEnv.GEMINI_API_KEY;
  if (geminiKey) env.GEMINI_API_KEY = geminiKey;
  return env;
}

export type ApprovalKind = "command" | "fileChange" | "permissions";
export type ApprovalDecision = "accept" | "acceptForSession" | "decline";
export interface ApprovalRequest {
  kind: ApprovalKind;
  method: string;
  summary: string;
  params: any;
}

export interface OpenCodeAgentHooks {
  onEvent?: (line: string) => void;
  onDelta?: (text: string) => void;
  onApproval?: (req: ApprovalRequest) => Promise<ApprovalDecision>;
}

export interface ThreadSummary {
  id: string;
  preview: string;
  cwd: string;
  createdAt: number;
  updatedAt: number;
  status: string;
  modelProvider: string;
}

export interface TurnSummary {
  id: string;
  status: string;
  startedAt?: number;
  completedAt?: number;
  user: string[];
  agent: string[];
  commands: string[];
}

/** Drives `opencode acp` over JSON-RPC stdio (Agent Client Protocol). */
export class OpenCodeAgent {
  private child?: ChildProcess;
  private rpc?: JsonRpcStdio;
  private exited?: Promise<never>;

  constructor(
    private cfg: AgentConfig,
    private hooks: OpenCodeAgentHooks = {},
  ) {}

  private log(line: string) { this.hooks.onEvent?.(line); }

  async start(): Promise<void> {
    const env = buildChildEnv(this.cfg);
    const target = spawnTarget(this.cfg.opencodeBin ?? "opencode", ["acp"], env);
    const child = spawn(target.command, target.args, { env, cwd: this.cfg.workspace, stdio: ["pipe", "pipe", "pipe"], shell: target.shell });
    this.child = child;
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (d: string) => { if (this.cfg.verbose) process.stderr.write(d.replace(/^/gm, "[opencode] ")); });
    this.exited = new Promise<never>((_, reject) => {
      child.once("error", (e) => reject(new Error(`failed to start ${this.cfg.opencodeBin ?? "opencode"}: ${e.message}`)));
      child.once("exit", (code, signal) => reject(new Error(`opencode exited (code ${code ?? "null"}, signal ${signal ?? "none"})`)));
    });
    this.exited.catch(() => {});

    const rpc = new JsonRpcStdio(child.stdin, child.stdout);
    this.rpc = rpc;
    rpc.onServerRequest((id, method, params) => void this.handleServerRequest(id, method, params));

    await this.race(rpc.request("initialize", {
      protocolVersion: 1,
      clientInfo: { name: "atmiya-ai", title: "ATMIYA AI", version: readVersion() },
      clientCapabilities: { fs: { readTextFile: false, writeTextFile: false } },
    }));
    rpc.notify("initialized", {});
    this.log(`opencode acp ready (workspace ${this.cfg.workspace})`);
  }

  /** Map an ACP `session/request_permission` to our approval hooks. */
  private async handleServerRequest(id: number | string, method: string, params: any) {
    const rpc = this.rpc!;
    if (method !== "session/request_permission") { rpc.respond(id, {}); return; }
    const kind = classifyPermission(params);
    const options: Array<{ optionId: string; kind?: string; name?: string }> = params?.options ?? [];
    let decision: ApprovalDecision = "accept";
    if (this.hooks.onApproval) {
      try { decision = await this.hooks.onApproval({ kind, method, summary: summarizeApproval(kind, params), params }); }
      catch { decision = "decline"; }
    }
    const wanted = decision === "accept" ? "allow_once" : decision === "acceptForSession" ? "allow_always" : "reject_once";
    const pick = options.find((o) => o.kind === wanted)
      ?? options.find((o) => (decision === "decline" ? /reject|deny|no/i.test(o.kind ?? o.name ?? "") : /allow|accept|yes/i.test(o.kind ?? o.name ?? "")))
      ?? options[0];
    if (decision === "decline" && !options.some((o) => /reject|deny|no/i.test(o.kind ?? o.name ?? ""))) {
      rpc.respond(id, { outcome: { outcome: "cancelled" } });
    } else {
      rpc.respond(id, { outcome: { outcome: "selected", optionId: pick?.optionId ?? wanted } });
    }
    this.log(`${this.hooks.onApproval ? "approval" : "auto-approval"} ${decision}: ${summarizeApproval(kind, params)}`);
  }

  async run(task: string, opts: RunOptions = {}): Promise<RunResult> {
    const rpc = this.rpc;
    if (!rpc) throw new Error("OpenCodeAgent.start() must be called first");
    const cwd = this.cfg.workspace;
    const mcpServers: unknown[] = [];

    let sessionId: string;
    if (opts.threadId) {
      await this.race(rpc.request("session/load", { sessionId: opts.threadId, cwd, mcpServers })).catch(async () => {
        // Fall back to a fresh session if the old one cannot be loaded.
        return null;
      });
      sessionId = opts.threadId;
    } else {
      const created = await this.race(rpc.request<any>("session/new", { cwd, mcpServers }));
      sessionId = created.sessionId ?? created.session?.id ?? created.id;
    }
    if (this.cfg.model) {
      await this.race(rpc.request("session/set_config_option", { sessionId, configId: "model", value: this.cfg.model })).catch(() => null);
    }
    this.log(`${opts.threadId ? "resumed" : "started"} session ${sessionId}`);

    const prompt: Array<Record<string, unknown>> = [{ type: "text", text: task }];
    if (opts.imagePath) {
      if (fs.existsSync(opts.imagePath)) {
        const data = fs.readFileSync(opts.imagePath).toString("base64");
        const mimeType = opts.imagePath.toLowerCase().endsWith(".png") ? "image/png" : "image/jpeg";
        prompt.push({ type: "image", data, mimeType });
        this.log(`attached screenshot ${opts.imagePath}`);
      } else {
        this.log(`screenshot attach skipped: ${opts.imagePath} not found, continuing without it`);
      }
    }

    const before = snapshotWorkspace(cwd);
    const commands: string[] = [];
    let finalMessage = "";
    let errorMessage: string | undefined;

    const off = rpc.onNotification((method, params) => {
      if (method !== "session/update") return;
      if (params?.sessionId && params.sessionId !== sessionId) return;
      const u = params?.update ?? {};
      switch (u.sessionUpdate) {
        case "agent_message_chunk":
          if (u.content?.type === "text" && typeof u.content.text === "string") { this.hooks.onDelta?.(u.content.text); finalMessage += u.content.text; }
          break;
        case "tool_call": {
          const name = u.name ?? u.title ?? "tool";
          commands.push(name);
          this.log(`tool: ${name}${u.kind ? ` (${u.kind})` : ""}`);
          break;
        }
        case "tool_call_update":
          if (u.status === "completed" || u.status === "failed") this.log(`tool ${u.status}: ${u.title ?? u.toolCallId ?? ""}`);
          break;
      }
    });

    let stopReason = "end_turn";
    try {
      const result = await this.race(this.withTimeout(
        rpc.request<any>("session/prompt", { sessionId, prompt }),
        opts.timeoutMs ?? this.cfg.runTimeoutMs ?? DEFAULT_RUN_TIMEOUT_MS,
        sessionId,
      ));
      stopReason = result?.stopReason ?? "end_turn";
      if (result?.error) errorMessage = String(result.error?.message ?? result.error);
    } catch (e) {
      errorMessage = (e as Error).message;
      stopReason = "cancelled";
    } finally { off(); }

    const trimmedFinal = finalMessage.trim();
    const artifacts = Array.from(new Set([...diffSnapshots(before, snapshotWorkspace(cwd))])).sort();
    const status: RunResult["status"] = stopReason === "end_turn" && !errorMessage ? "completed" : stopReason === "cancelled" ? "interrupted" : "failed";
    return { threadId: sessionId, turnId: sessionId, status, finalMessage: trimmedFinal, artifacts, error: errorMessage };
  }

  private withTimeout<T>(p: Promise<T>, timeoutMs: number, sessionId: string): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`OpenCode run timed out after ${timeoutMs}ms (session ${sessionId})`)), timeoutMs);
      p.then((v) => { clearTimeout(timer); resolve(v); }, (e) => { clearTimeout(timer); reject(e); });
    });
  }

  async listThreads(limit = 20): Promise<ThreadSummary[]> {
    const { spawn } = await import("node:child_process");
    return new Promise((resolve) => {
      const env = buildChildEnv(this.cfg);
      const target = spawnTarget(this.cfg.opencodeBin ?? "opencode", ["session", "list", "--format", "json", "--max-count", String(limit)], env);
      const child = spawn(target.command, target.args, { env, cwd: this.cfg.workspace, stdio: ["ignore", "pipe", "pipe"], shell: target.shell });
      let out = ""; child.stdout.on("data", (d) => (out += d));
      child.once("error", () => resolve([]));
      child.once("close", () => {
        try {
          const arr = JSON.parse(out) as any[];
          resolve(arr.map((t) => { const created = Number(t.createdAt ?? t.created ?? 0); const updated = Number(t.updatedAt ?? t.updated ?? created); return { id: t.id ?? t.sessionID ?? t.sessionId, preview: t.title ?? t.preview ?? "", cwd: t.cwd ?? t.directory ?? "", createdAt: created > 1e12 ? Math.floor(created / 1000) : created, updatedAt: updated > 1e12 ? Math.floor(updated / 1000) : updated, status: t.status ?? "unknown", modelProvider: t.modelProvider ?? "" }; }));
        } catch { resolve([]); }
      });
    });
  }

  async readThread(threadId: string): Promise<{ thread: ThreadSummary; turns: TurnSummary[] }> {
    const { spawn } = await import("node:child_process");
    return new Promise((resolve, reject) => {
      const env = buildChildEnv(this.cfg);
      const target = spawnTarget(this.cfg.opencodeBin ?? "opencode", ["session", "export", threadId], env);
      const child = spawn(target.command, target.args, { env, cwd: this.cfg.workspace, stdio: ["ignore", "pipe", "pipe"], shell: target.shell });
      let out = ""; child.stdout.on("data", (d) => (out += d));
      child.once("error", reject);
      child.once("close", () => {
        try {
          const data = JSON.parse(out);
          const info = data.info ?? data.session ?? data;
          const messages: any[] = data.messages ?? [];
          const turns: TurnSummary[] = [];
          let current: TurnSummary | undefined;
          for (const m of messages) {
            const role = m.info?.role ?? m.role;
            const parts: any[] = m.parts ?? [];
            const text = parts.filter((p) => p.type === "text").map((p) => p.text ?? "").join("\n");
            if (role === "user") { current = { id: m.info?.id ?? m.id ?? `t${turns.length}`, status: "completed", user: [text], agent: [], commands: [] }; turns.push(current); }
            else if (role === "assistant" && current) { current.agent.push(text); for (const p of parts) if (p.type === "tool" || p.type === "tool-invocation") current.commands.push(p.tool ?? p.name ?? ""); }
          }
          resolve({ thread: { id: threadId, preview: info.title ?? "", cwd: info.directory ?? info.cwd ?? "", createdAt: info.time?.created ?? 0, updatedAt: info.time?.updated ?? 0, status: "unknown", modelProvider: info.model?.providerID ?? "" }, turns });
        } catch (e) { reject(e); }
      });
    });
  }

  /** OpenCode has no separate "archive"; we treat archiving as removing from active lists. */
  async archiveThread(threadId: string): Promise<void> {
    const { spawn } = await import("node:child_process");
    await new Promise<void>((resolve) => {
      const env = buildChildEnv(this.cfg);
      const target = spawnTarget(this.cfg.opencodeBin ?? "opencode", ["session", "delete", threadId], env);
      const child = spawn(target.command, target.args, { env, cwd: this.cfg.workspace, stdio: "ignore", shell: target.shell });
      child.once("error", () => resolve());
      child.once("close", () => resolve());
    });
  }

  async stop(): Promise<void> {
    if (!this.child || this.child.exitCode !== null) return;
    const child = this.child;
    try { child.stdin?.end(); } catch { /* ignore */ }
    await new Promise<void>((resolve) => {
      const t = setTimeout(() => { child.kill("SIGKILL"); resolve(); }, 2000);
      child.once("exit", () => { clearTimeout(t); resolve(); });
      child.kill();
    });
  }

  private race<T>(p: Promise<T>): Promise<T> { return this.exited ? Promise.race([p, this.exited]) : p; }
}

function classifyPermission(params: any): ApprovalKind {
  const kind = String(params?.toolCall?.kind ?? params?.kind ?? "").toLowerCase();
  if (kind.includes("edit") || kind.includes("file") || kind.includes("write")) return "fileChange";
  if (kind.includes("permission")) return "permissions";
  return "command";
}

export function summarizeApproval(kind: ApprovalKind, params: any): string {
  const tc = params?.toolCall ?? params ?? {};
  const title = tc.title ?? tc.name ?? "";
  const locations = (tc.locations ?? []).map((l: any) => l?.path).filter(Boolean);
  if (kind === "fileChange") return `change ${locations.length ? locations.join(", ") : title || "(files)"}`;
  if (kind === "permissions") return `grant permissions ${title || ""}`.trim();
  return `run ${title || "(command)"}`;
}
