import type { MiddlewareHandler } from "hono";
import type { Principal } from "./auth.js";

export interface RequestLogEntry {
  ts: string;
  method: string;
  path: string;
  status: number;
  ms: number;
  /** Authenticated user id when the route was authenticated. */
  sub?: string;
  via?: Principal["via"];
}

export type LogSink = (entry: RequestLogEntry) => void;

/** One structured line per request (no bodies, no tokens) — enough to see who used which lane. */
export function requestLogger(sink: LogSink = (e) => console.log(JSON.stringify(e))): MiddlewareHandler<{ Variables: { principal: Principal } }> {
  return async (c, next) => {
    const started = Date.now();
    await next();
    const principal = c.get("principal");
    sink({
      ts: new Date(started).toISOString(),
      method: c.req.method,
      path: new URL(c.req.url).pathname,
      status: c.res.status,
      ms: Date.now() - started,
      ...(principal ? { sub: principal.sub, via: principal.via } : {}),
    });
  };
}
