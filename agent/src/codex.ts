import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import { JsonRpcStdio } from "./jsonrpc.js";
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
}

export interface CodexAgentHooks {
  /** Milestone commentary (thread started, item completed, approvals). */
  onEvent?: (line: string) => void;
}

/**
 * Drives `codex app-server --stdio` the way HeyClicky's CodexProtocolClient does:
 * isolated CODEX_HOME, JSON-RPC over stdio, thread start/resume, turn start, auto-approvals,
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

    const env: NodeJS.ProcessEnv = {
      ...process.env,
      CODEX_HOME: this.cfg.codexHome,
      OPENCLICKY_SESSION_TOKEN: this.cfg.token ?? "",
    };
    // Keys stay server-side: the agent engine must never see provider keys, even if the shell has them.
    delete env.OPENAI_API_KEY;
    delete env.ANTHROPIC_API_KEY;

    const child = spawn(this.cfg.codexBin, ["app-server", "--stdio"], { env, stdio: ["pipe", "pipe", "pipe"] });
    this.child = child;
    child.stderr!.setEncoding("utf8");
    child.stderr!.on("data", (d: string) => {
      if (this.cfg.verbose) process.stderr.write(d.replace(/^/gm, "[codex] "));
    });
    this.exited = new Promise<never>((_, reject) => {
      child.once("error", (e) => reject(new Error(`failed to start ${this.cfg.codexBin}: ${e.message}`)));
      child.once("exit", (code, signal) => reject(new Error(`codex exited (code ${code ?? "null"}, signal ${signal ?? "none"})`)));
    });
    this.exited.catch(() => {}); // observed per-call via race()

    const rpc = new JsonRpcStdio(child.stdin!, child.stdout!);
    this.rpc = rpc;
    rpc.onServerRequest((id, method) => {
      // Headless first cut: the user's instruction IS the approval (see skills/ModelInstructions.md).
      switch (method) {
        case "item/commandExecution/requestApproval":
        case "item/fileChange/requestApproval":
        case "item/permissions/requestApproval":
          rpc.respond(id, { decision: "accept" });
          break;
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
    });

    await this.race(
      rpc.request("initialize", {
        clientInfo: { name: "openclicky", title: "OpenClicky", version: "0.1.0" },
        capabilities: { experimentalApi: true },
      }),
    );
  }

  async run(task: string, opts: RunOptions = {}): Promise<RunResult> {
    const rpc = this.rpc;
    if (!rpc) throw new Error("CodexAgent.start() must be called first");
    const cwd = this.cfg.workspace;

    const threadOpts: Record<string, unknown> = {
      cwd,
      approvalPolicy: "never",
      sandbox: "workspace-write",
      ...(this.cfg.model ? { model: this.cfg.model } : {}),
    };
    const started = opts.threadId
      ? await this.race(rpc.request<any>("thread/resume", { threadId: opts.threadId, ...threadOpts }))
      : await this.race(rpc.request<any>("thread/start", threadOpts));
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

    const done = new Promise<any>((resolve) => {
      const off = rpc.onNotification((method, params) => {
        if (params?.threadId && params.threadId !== threadId) return;
        switch (method) {
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
            off();
            resolve(params.turn);
            break;
        }
      });
    });

    const { turn } = await this.race(rpc.request<any>("turn/start", { threadId, input }));
    const completed = await this.race(done);

    const artifacts = Array.from(new Set([...artifactsFromItems(items), ...diffSnapshots(before, snapshotWorkspace(cwd))])).sort();
    return {
      threadId,
      turnId: turn.id,
      status: completed.status,
      finalMessage,
      artifacts,
      error: completed.error?.message ?? errorMessage,
    };
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

  private race<T>(p: Promise<T>): Promise<T> {
    return this.exited ? Promise.race([p, this.exited]) : p;
  }
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
