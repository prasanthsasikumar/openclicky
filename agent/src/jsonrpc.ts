import type { Readable, Writable } from "node:stream";

type Id = number | string;
type NotificationHandler = (method: string, params: any) => void;
type ServerRequestHandler = (id: Id, method: string, params: any) => void;

/** Split a buffer of newline-delimited JSON into parsed messages plus the unparsed tail. */
export function parseJsonLines(buffer: string): { messages: unknown[]; rest: string } {
  const messages: unknown[] = [];
  let rest = buffer;
  let i: number;
  while ((i = rest.indexOf("\n")) >= 0) {
    const line = rest.slice(0, i).trim();
    rest = rest.slice(i + 1);
    if (!line) continue;
    try {
      messages.push(JSON.parse(line));
    } catch {
      // Not JSON (stray log line) — ignore.
    }
  }
  return { messages, rest };
}

/**
 * Minimal JSON-RPC 2.0 client over newline-delimited stdio, matching `codex app-server --stdio`.
 * Handles: our requests → their responses; their notifications; their requests (approvals) → our responses.
 */
export class JsonRpcStdio {
  private nextId = 1;
  private pending = new Map<Id, { resolve: (v: any) => void; reject: (e: Error) => void }>();
  private notificationHandlers: NotificationHandler[] = [];
  private serverRequestHandlers: ServerRequestHandler[] = [];
  private buffer = "";
  private closed = false;

  constructor(
    private toPeer: Writable,
    fromPeer: Readable,
  ) {
    fromPeer.setEncoding?.("utf8");
    fromPeer.on("data", (chunk: string | Buffer) => this.onData(chunk.toString()));
    fromPeer.on("end", () => this.failAll(new Error("peer closed stdout")));
    fromPeer.on("error", (e) => this.failAll(e));
  }

  request<T = unknown>(method: string, params?: unknown): Promise<T> {
    if (this.closed) return Promise.reject(new Error("JSON-RPC connection closed"));
    const id = this.nextId++;
    const p = new Promise<T>((resolve, reject) => this.pending.set(id, { resolve, reject }));
    this.write({ jsonrpc: "2.0", id, method, params: params ?? {} });
    return p;
  }

  respond(id: Id, result: unknown): void {
    this.write({ jsonrpc: "2.0", id, result });
  }

  notify(method: string, params?: unknown): void {
    this.write({ jsonrpc: "2.0", method, params: params ?? {} });
  }

  onNotification(cb: NotificationHandler): () => void {
    this.notificationHandlers.push(cb);
    return () => {
      this.notificationHandlers = this.notificationHandlers.filter((h) => h !== cb);
    };
  }

  onServerRequest(cb: ServerRequestHandler): void {
    this.serverRequestHandlers.push(cb);
  }

  private write(msg: unknown) {
    this.toPeer.write(JSON.stringify(msg) + "\n");
  }

  private onData(chunk: string) {
    const { messages, rest } = parseJsonLines(this.buffer + chunk);
    this.buffer = rest;
    for (const raw of messages) this.dispatch(raw as any);
  }

  private dispatch(msg: any) {
    const hasId = msg.id !== undefined && msg.id !== null;
    if (hasId && msg.method) {
      for (const h of this.serverRequestHandlers) h(msg.id, msg.method, msg.params);
      return;
    }
    if (hasId && this.pending.has(msg.id)) {
      const { resolve, reject } = this.pending.get(msg.id)!;
      this.pending.delete(msg.id);
      if (msg.error) reject(new Error(`${msg.method ?? "rpc"} failed: ${msg.error.message ?? JSON.stringify(msg.error)}`));
      else resolve(msg.result);
      return;
    }
    if (msg.method) for (const h of this.notificationHandlers) h(msg.method, msg.params);
  }

  private failAll(err: Error) {
    this.closed = true;
    for (const { reject } of this.pending.values()) reject(err);
    this.pending.clear();
  }
}
