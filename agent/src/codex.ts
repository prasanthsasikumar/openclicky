import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import { JsonRpcStdio } from "./jsonrpc.js";
import { readVersion } from "./version.js";
import { ensureCodexHome } from "./codexHome.js";
import { snapshotWorkspace, diffSnapshots, artifactsFromItems } from "./artifacts.js";
import type { AgentConfig } from "./config.js";

export interface RunResult {
  threadId: string;
  turnId: string;
  status: "completed" | "failed" | "interrupted";
  finalMessage: string;
  artifacts: string[];
  error?: string;
}

export interface RunOptions {
  /** Resume an existing Codex thread instead of starting a new one. */
  threadId?: string;
  /** Local screenshot/image attached to the turn as `localImage`. */
  imagePath?: string;
  /** Override the run's timeout (ms) for this call only; see DEFAULT_RUN_TIMEOUT_MS. */
  timeoutMs?: number;
}

/**
 * Wall-clock cap on a single turn/run before CodexAgent.run() rejects instead of hanging forever
 * (see the timeout note on `run()` below). Ten minutes covers any real agent task; a genuinely
 * longer one should resume as a new call rather than hold a single run open indefinitely.
 */
export const DEFAULT_RUN_TIMEOUT_MS = 10 * 60 * 1000;

/**
 * Interactive-shell and runtime variables Codex's own tooling expects to find set, independent of
 * anything OpenClicky configures. Derived by reading what the app-server actually shells out for:
 * PATH/SHELL to run commands, HOME/USER for path expansion and ownership checks, TMPDIR for scratch
 * files, LANG/LC_ALL/TERM for locale-aware and interactive-looking subprocess output.
 */
const SHELL_ENV_ALLOWLIST = ["PATH", "HOME", "USER", "SHELL", "TMPDIR", "LANG", "LC_ALL", "TERM"];

/**
 * Proxy configuration, upper- and lowercase (different tools read one or the other), so Codex's own
 * network calls (and any it shells out for) still honor the operator's proxy setup.
 */
const PROXY_ENV_ALLOWLIST = ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy"];

/**
 * Build the Codex child's environment from an explicit allowlist instead of forwarding the whole
 * shell environment. `codex app-server` runs an agent that executes arbitrary shell commands on the
 * user's behalf, so anything left in `process.env` beyond what Codex genuinely needs — AWS keys,
 * GitHub tokens, whatever else lives in the operator's shell — would otherwise be readable by every
 * command that agent runs. This carries: the shell/runtime variables above; any `NODE_*` variable
 * (NODE_OPTIONS, NODE_EXTRA_CA_CERTS, etc. — Node's own tooling, including Codex's, reads these);
 * proxy configuration; and the OpenClicky variables config.toml actually references — set explicitly
 * from `cfg` rather than trusted from the shell: `CODEX_HOME`, `OPENCLICKY_SESSION_TOKEN` (env_key
 * for model_providers.openclicky), `OPENCLICKY_OPENAI_KEY` / `OPENCLICKY_ANTHROPIC_KEY`
 * (env_http_headers, bring-your-own-key), and `COMPOSIO_API_KEY` (mcp_servers.composio's consumer
 * key — see codexHome.ts). Provider keys (OPENAI_API_KEY, ANTHROPIC_API_KEY, …) are never on this
 * list: Codex must always go through the OpenClicky backend, never see them directly.
 */
export function buildChildEnv(cfg: AgentConfig, sourceEnv: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const key of [...SHELL_ENV_ALLOWLIST, ...PROXY_ENV_ALLOWLIST]) {
    if (sourceEnv[key] !== undefined) env[key] = sourceEnv[key];
  }
  for (const [key, value] of Object.entries(sourceEnv)) {
    if (key.startsWith("NODE_") && value !== undefined) env[key] = value;
  }
  env.CODEX_HOME = cfg.codexHome;
  env.OPENCLICKY_SESSION_TOKEN = cfg.token ?? "";
  // Bring your own key: the Codex config forwards these as headers (env_http_headers); unset = not sent.
  if (cfg.openaiApiKey) env.OPENCLICKY_OPENAI_KEY = cfg.openaiApiKey;
  if (cfg.anthropicApiKey) env.OPENCLICKY_ANTHROPIC_KEY = cfg.anthropicApiKey;
  if (cfg.composioApiKey) env.COMPOSIO_API_KEY = cfg.composioApiKey;
  return env;
}

export type ApprovalKind = "command" | "fileChange" | "permissions";
export type ApprovalDecision = "accept" | "acceptForSession" | "decline";
export interface ApprovalRequest {
  kind: ApprovalKind;
  method: string;
  /** Human-readable one-liner (command text, changed paths, or requested permissions). */
  summary: string;
  params: any;
}

export interface CodexAgentHooks {
  /** Milestone commentary (thread started, item completed, approvals). */
  onEvent?: (line: string) => void;
  /** Streamed agent text as it is generated. */
  onDelta?: (text: string) => void;
  /**
   * Decide approval requests interactively. When set, threads run with `approvalPolicy: "on-request"`;
   * when absent, the headless default applies: `approvalPolicy: "never"` and everything is accepted
   * (the user's instruction IS the approval — see skills/ModelInstructions.md).
   */
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

/**
 * Drives `codex app-server --stdio` the way HeyClicky's CodexProtocolClient does:
 * isolated CODEX_HOME, JSON-RPC over stdio, thread start/resume, turn start, approvals,
 * and collection of the final agent message + artifacts.
 */
export class CodexAgent {
  private child?: ChildProcess;
  private rpc?: JsonRpcStdio;
  private exited?: Promise<never>;

  constructor(
    private cfg: AgentConfig,
    private hooks: CodexAgentHooks = {},
  ) {}

  private log(line: string) {
    this.hooks.onEvent?.(line);
  }

  async start(): Promise<void> {
    const { configPath } = ensureCodexHome(this.cfg);
    this.log(`codex home ${this.cfg.codexHome} (config ${configPath})`);

    const env = buildChildEnv(this.cfg);

    const child = spawn(this.cfg.codexBin, ["app-server", "--stdio"], { env, stdio: ["pipe", "pipe", "pipe"] });
    this.child = child;
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (d: string) => {
      if (this.cfg.verbose) process.stderr.write(d.replace(/^/gm, "[codex] "));
    });
    this.exited = new Promise<never>((_, reject) => {
      child.once("error", (e) => reject(new Error(`failed to start ${this.cfg.codexBin}: ${e.message}`)));
      child.once("exit", (code, signal) => reject(new Error(`codex exited (code ${code ?? "null"}, signal ${signal ?? "none"})`)));
    });
    this.exited.catch(() => {}); // observed per-call via race()

    const rpc = new JsonRpcStdio(child.stdin, child.stdout);
    this.rpc = rpc;
    rpc.onServerRequest((id, method, params) => void this.handleServerRequest(id, method, params));

    await this.race(
      rpc.request("initialize", {
        clientInfo: { name: "openclicky", title: "OpenClicky", version: readVersion() },
        capabilities: { experimentalApi: true },
      }),
    );
  }

  private async handleServerRequest(id: number | string, method: string, params: any) {
    const rpc = this.rpc!;
    const approvalKind: ApprovalKind | undefined =
      method === "item/commandExecution/requestApproval"
        ? "command"
        : method === "item/fileChange/requestApproval"
          ? "fileChange"
          : method === "item/permissions/requestApproval"
            ? "permissions"
            : undefined;

    if (approvalKind) {
      let decision: ApprovalDecision = "accept";
      if (this.hooks.onApproval) {
        try {
          decision = await this.hooks.onApproval({ kind: approvalKind, method, summary: summarizeApproval(approvalKind, params), params });
        } catch (e) {
          this.log(`approval handler failed (${(e as Error).message}); declining`);
          decision = "decline";
        }
      }
      rpc.respond(id, { decision });
      this.log(`${this.hooks.onApproval ? "approval" : "auto-approval"} ${decision}: ${summarizeApproval(approvalKind, params)}`);
      return;
    }
    switch (method) {
      case "item/tool/requestUserInput":
        rpc.respond(id, { answers: {} });
        break;
      case "mcpServer/elicitation/request":
        rpc.respond(id, { action: "decline" });
        break;
      default:
        rpc.respond(id, {});
    }
    this.log(`auto-answered ${method}`);
  }

  private threadOptions(): Record<string, unknown> {
    return {
      cwd: this.cfg.workspace,
      approvalPolicy: this.hooks.onApproval ? "on-request" : "never",
      sandbox: "workspace-write",
      ...(this.cfg.model ? { model: this.cfg.model } : {}),
    };
  }

  async run(task: string, opts: RunOptions = {}): Promise<RunResult> {
    const rpc = this.rpc;
    if (!rpc) throw new Error("CodexAgent.start() must be called first");
    const cwd = this.cfg.workspace;

    const started = opts.threadId
      ? await this.race(rpc.request<any>("thread/resume", { threadId: opts.threadId, ...this.threadOptions() }))
      : await this.race(rpc.request<any>("thread/start", this.threadOptions()));
    const threadId: string = started.thread.id;
    this.log(`${opts.threadId ? "resumed" : "started"} thread ${threadId} (model ${started.model}, provider ${started.modelProvider})`);

    const input: Array<Record<string, unknown>> = [{ type: "text", text: task }];
    if (opts.imagePath) {
      if (fs.existsSync(opts.imagePath)) {
        input.push({ type: "localImage", path: opts.imagePath });
        this.log(`attached screenshot ${opts.imagePath}`);
      } else {
        this.log(`screenshot attach not yet wired: ${opts.imagePath} not found, continuing without it`);
      }
    }

    const before = snapshotWorkspace(cwd);
    const items: unknown[] = [];
    let finalMessage = "";
    let errorMessage: string | undefined;

    let off: () => void = () => {};
    const done = new Promise<any>((resolve) => {
      off = rpc.onNotification((method, params) => {
        if (params?.threadId && params.threadId !== threadId) return;
        switch (method) {
          case "item/agentMessage/delta":
            if (typeof params.delta === "string") this.hooks.onDelta?.(params.delta);
            break;
          case "item/completed": {
            const item = params.item;
            items.push(item);
            if (item?.type === "agentMessage" && typeof item.text === "string") finalMessage = item.text;
            this.log(describeItem(item));
            break;
          }
          case "error":
            errorMessage = params.error?.message ?? String(params.error);
            this.log(`error: ${errorMessage}${params.willRetry ? " (retrying)" : ""}`);
            break;
          case "turn/completed":
            resolve(params.turn);
            break;
        }
      });
    });

    const { turn } = await this.race(rpc.request<any>("turn/start", { threadId, input }));
    let completed: any;
    try {
      // Without a timeout, a Codex bug that reports an "error" notification but never follows up
      // with turn/completed (or any other terminal event) would hang this call forever — there is
      // nothing else here to reject the promise. The timeout is the backstop for that case; a clean
      // exit (including the child crashing, handled by `race`) always resolves well before it fires.
      completed = await this.race(this.withTimeout(done, opts.timeoutMs ?? this.cfg.runTimeoutMs, threadId));
    } finally {
      off(); // stop listening whether we resolved, errored, or timed out — a leaked listener would
      // keep matching threadId on every later run() call's notifications.
    }

    const artifacts = Array.from(new Set([...artifactsFromItems(items), ...diffSnapshots(before, snapshotWorkspace(cwd))])).sort();
    return {
      threadId,
      turnId: turn.id,
      status: completed.status,
      finalMessage,
      artifacts,
      // A retried "error" notification (willRetry: true) describes a transient hiccup Codex already
      // recovered from by the time the turn reports success; surfacing it on a completed result would
      // be a stale, misleading error on an otherwise-fine run. Trust the turn's own status/error first
      // and only fall back to a stray notification's message when the turn did not finish cleanly.
      error: completed.error?.message ?? (completed.status === "completed" ? undefined : errorMessage),
    };
  }

  /** Reject with a clear, named-timeout message if `p` does not settle within `timeoutMs`. */
  private withTimeout<T>(p: Promise<T>, timeoutMs: number, threadId: string): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new Error(`Codex run timed out after ${timeoutMs}ms waiting for the turn to finish (thread ${threadId})`)),
        timeoutMs,
      );
      p.then(
        (v) => {
          clearTimeout(timer);
          resolve(v);
        },
        (e) => {
          clearTimeout(timer);
          // Passing the original rejection reason straight through: wrapping it in an Error here
          // would hide whatever the caller actually threw.
          // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors
          reject(e);
        },
      );
    });
  }

  async listThreads(limit = 20): Promise<ThreadSummary[]> {
    const rpc = this.requireRpc();
    const res = await this.race(rpc.request<any>("thread/list", { limit, sortKey: "updated_at" }));
    return (res.data ?? []).map((t: any) => ({
      id: t.id,
      preview: t.preview ?? "",
      cwd: t.cwd ?? "",
      createdAt: t.createdAt ?? 0,
      updatedAt: t.updatedAt ?? t.createdAt ?? 0,
      status: t.status?.type ?? "unknown",
      modelProvider: t.modelProvider ?? "",
    }));
  }

  async readThread(threadId: string, limit = 50): Promise<{ thread: ThreadSummary; turns: TurnSummary[] }> {
    const rpc = this.requireRpc();
    const read = await this.race(rpc.request<any>("thread/read", { threadId, includeTurns: false }));
    const turns = await this.race(rpc.request<any>("thread/turns/list", { threadId, limit, sortDirection: "asc", itemsView: "full" }));
    const t = read.thread;
    return {
      thread: { id: t.id, preview: t.preview ?? "", cwd: t.cwd ?? "", createdAt: t.createdAt ?? 0, updatedAt: t.updatedAt ?? 0, status: t.status?.type ?? "unknown", modelProvider: t.modelProvider ?? "" },
      turns: (turns.data ?? []).map((turn: any) => ({
        id: turn.id,
        status: turn.status,
        startedAt: turn.startedAt ?? undefined,
        completedAt: turn.completedAt ?? undefined,
        user: (turn.items ?? []).filter((i: any) => i.type === "userMessage").flatMap((i: any) => (i.content ?? []).filter((c: any) => c.type === "text").map((c: any) => c.text)),
        agent: (turn.items ?? []).filter((i: any) => i.type === "agentMessage").map((i: any) => i.text ?? ""),
        commands: (turn.items ?? []).filter((i: any) => i.type === "commandExecution").map((i: any) => i.command ?? ""),
      })),
    };
  }

  async archiveThread(threadId: string): Promise<void> {
    await this.race(this.requireRpc().request("thread/archive", { threadId }));
  }

  async stop(): Promise<void> {
    if (!this.child || this.child.exitCode !== null) return;
    this.child.stdin?.end();
    const child = this.child;
    await new Promise<void>((resolve) => {
      const t = setTimeout(() => {
        child.kill("SIGKILL");
        resolve();
      }, 2000);
      child.once("exit", () => {
        clearTimeout(t);
        resolve();
      });
      child.kill();
    });
  }

  private requireRpc(): JsonRpcStdio {
    if (!this.rpc) throw new Error("CodexAgent.start() must be called first");
    return this.rpc;
  }

  private race<T>(p: Promise<T>): Promise<T> {
    return this.exited ? Promise.race([p, this.exited]) : p;
  }
}

export function summarizeApproval(kind: ApprovalKind, params: any): string {
  if (kind === "command") {
    const cmd = params?.command ?? params?.cmd ?? params?.item?.command;
    const cmdText = Array.isArray(cmd) ? cmd.join(" ") : typeof cmd === "string" ? cmd : undefined;
    return `run ${cmdText ?? "(command)"}${params?.reason ? ` — ${params.reason}` : ""}${params?.cwd ? ` [cwd ${params.cwd}]` : ""}`;
  }
  if (kind === "fileChange") {
    const changes = params?.changes ?? params?.item?.changes ?? [];
    const paths = Array.isArray(changes) ? changes.map((c: any) => c.path).filter(Boolean) : [];
    return `change ${paths.length ? paths.join(", ") : "(files)"}${params?.reason ? ` — ${params.reason}` : ""}`;
  }
  return `grant permissions ${JSON.stringify(params?.permissions ?? params?.additionalPermissions ?? {}).slice(0, 200)}`;
}

function describeItem(item: any): string {
  switch (item?.type) {
    case "commandExecution":
      return `ran: ${item.command ?? "(command)"}${item.exitCode !== undefined && item.exitCode !== null ? ` (exit ${item.exitCode})` : ""}`;
    case "fileChange":
      return `changed files: ${(item.changes ?? []).map((c: any) => c.path).join(", ")}`;
    case "agentMessage":
      return "agent message";
    case "reasoning":
      return "reasoning";
    case "mcpToolCall":
      return `mcp tool: ${item.tool ?? item.name ?? ""}`;
    default:
      return `item ${item?.type ?? "unknown"}`;
  }
}
