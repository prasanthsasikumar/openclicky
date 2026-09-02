# OpenClicky Initial Cut Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A headless vertical slice of OpenClicky: a Hono backend that holds provider keys and verifies Supabase auth, a TypeScript bridge that drives Codex CLI over JSON-RPC stdio through that backend, and the ported skills/config that define agent behavior.

**Architecture:** `agent/` spawns `codex app-server --stdio` with an isolated `CODEX_HOME` whose `config.toml` points Codex's model provider at `backend/` (`wire_api = "responses"`, session token via `env_key`). `backend/` is a single Hono app that runs under Node (`@hono/node-server`) and Cloudflare Workers unchanged; it verifies Supabase JWTs, mints short-lived HS256 session tokens, and streams proxied OpenAI Responses / Chat Completions / Anthropic Messages calls. `skills/` + `config/` are static assets rendered into the Codex home at run time.

**Tech Stack:** TypeScript 5+, Node 22, Hono 4, `@hono/node-server`, `jose` (JWT), `commander` (CLI), `vitest`, `wrangler` (Workers dev), Codex CLI 0.152.1 (`npm i -g @openai/codex`).

**Spec:** `PROMPT-initial-cut.md` (task), `REVERSE-ENGINEERING.md` (master spec), `reference/clicky-model-instructions-verbatim.md`, `reference/codex-config.toml`, `reference/clicky-bundled-skills/`.

## Global Constraints

- Keys stay server-side: `agent/` never reads `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`; every model call goes through `backend/`.
- No secrets copied from reference material (no PostHog/Sentry/Supabase values).
- Skip the proprietary `powerpoint` skill (it is not in the reference set anyway).
- Do NOT implement: SwiftUI shell, voice/realtime, cua-driver wiring, Composio, paywall, PostHog, Sentry, Sparkle. Stub/TODO only.
- Rebrand `HeyClicky`/`Clicky` → `OpenClicky`; `clicky-*` skill names → `openclicky-*`; leave `cua-driver`, `doc`, `frontend-design`, `obsidian`, `pdf`, `spreadsheet`, `vercel-deploy` names as-is.
- Remove `clicky-crons` from the Codex config.
- Backend default port `8787`; agent default `--backend-url http://localhost:8787`.
- Codex 0.152.1 facts (verified by probe): `wire_api = "chat"` is rejected, so the backend MUST proxy `POST /v1/responses`; `thread/resume` needs the same `CODEX_HOME` across runs, so the isolated home is stable (`~/.openclicky/codex-home`), not per-run temp; shell tool is `exec_command` with `{cmd: string}`; server approval requests are `item/commandExecution/requestApproval` and `item/fileChange/requestApproval` answered with `{decision: "accept"}`.
- No git commits (harness rule: commit only when the user asks). Steps that say "Commit" are replaced by "Verify" checkpoints.

## File Structure

```
openclicky/
  package.json                 # npm workspaces: backend, agent; build/test/dev scripts
  tsconfig.base.json           # shared strict TS settings
  .env.example                 # every env var documented
  .gitignore
  README.md                    # architecture, setup, run, auth flow (+ reference index)
  backend/
    package.json, tsconfig.json, wrangler.toml, .dev.vars.example
    src/env.ts                 # Env type + getEnv(c)
    src/auth.ts                # Supabase JWT verify, session token issue/verify, requireAuth middleware
    src/proxy.ts               # streaming proxies to OpenAI + Anthropic
    src/app.ts                 # Hono app + routes (default export for Workers)
    src/node.ts                # Node entry, loads .dev.vars, listens on 8787
    scripts/mint-dev-jwt.mjs   # mints a Supabase-style HS256 JWT for local dev / verification
    test/auth.test.ts, test/app.test.ts
  agent/
    package.json, tsconfig.json
    src/config.ts              # resolve flags/env → AgentConfig
    src/codexHome.ts           # render config template → CODEX_HOME/config.toml
    src/jsonrpc.ts             # JSON-RPC over stdio client
    src/artifacts.ts           # workspace snapshot diff + fileChange extraction
    src/codex.ts               # CodexAgent: spawn, initialize, run/resume turn
    src/ask.ts                 # lightweight chat completion via backend (SSE)
    src/cli.ts                 # `openclicky run|ask|token`
    test/*.test.ts, test/integration.test.ts
  skills/                      # ModelInstructions.md + 15 ported skills + ATTRIBUTION.md
  config/codex-config.toml     # template with {{OPENCLICKY_ROOT}}, {{BACKEND_URL}}, {{WORKSPACE}}
  scripts/port-skills.mjs      # reproducible port of reference skills → skills/
```

---

### Task 1: Repo scaffolding (workspaces, shared TS config, env example)

**Files:**
- Create: `package.json`, `tsconfig.base.json`, `.gitignore`, `.env.example`

**Interfaces:**
- Produces: `npm install` at root installs both workspaces; `npm run build` builds both; `npm test` runs both test suites.

- [ ] **Step 1: Write root `package.json`**

```json
{
  "name": "openclicky",
  "private": true,
  "version": "0.1.0",
  "description": "Open-source macOS AI voice assistant (headless initial cut)",
  "workspaces": ["backend", "agent"],
  "scripts": {
    "build": "npm run build --workspaces",
    "test": "npm run test --workspaces",
    "dev:backend": "npm run dev --workspace backend",
    "port-skills": "node scripts/port-skills.mjs"
  },
  "engines": { "node": ">=22" }
}
```

- [ ] **Step 2: Write `tsconfig.base.json`**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "declaration": false,
    "sourceMap": true,
    "types": ["node"]
  }
}
```

- [ ] **Step 3: Write `.gitignore`** (`node_modules/`, `dist/`, `.env`, `.dev.vars`, `.wrangler/`, `*.log`)

- [ ] **Step 4: Write `.env.example`** documenting `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `SUPABASE_JWT_SECRET`, `OPENAI_API_KEY`, `OPENAI_BASE_URL`, `OPENAI_MODEL`, `ANTHROPIC_API_KEY`, `BACKEND_URL`, `SESSION_TOKEN_SECRET`, `SESSION_TOKEN_TTL_SECONDS`, `OPENCLICKY_TOKEN`, `OPENCLICKY_CODEX_HOME`, `OPENCLICKY_WORKSPACE`, `OPENCLICKY_MODEL`, with one comment line each saying which side reads it.

- [ ] **Step 5: Verify** — `node -e "JSON.parse(require('fs').readFileSync('package.json','utf8'))"` exits 0.

---

### Task 2: Backend env + auth (Supabase JWT verify, session tokens, middleware)

**Files:**
- Create: `backend/package.json`, `backend/tsconfig.json`, `backend/src/env.ts`, `backend/src/auth.ts`
- Test: `backend/test/auth.test.ts`

**Interfaces:**
- Produces:
  - `type Env = { OPENAI_API_KEY?: string; OPENAI_BASE_URL?: string; OPENAI_MODEL?: string; ANTHROPIC_API_KEY?: string; ANTHROPIC_BASE_URL?: string; SUPABASE_URL?: string; SUPABASE_JWT_SECRET?: string; SESSION_TOKEN_SECRET?: string; SESSION_TOKEN_TTL_SECONDS?: string }`
  - `getEnv(c: Context): Env`
  - `type Principal = { sub: string; email?: string; via: "supabase" | "session" }`
  - `verifySupabaseJwt(token: string, env: Env): Promise<Principal>`
  - `issueSessionToken(p: {sub: string; email?: string}, env: Env): Promise<{ token: string; expiresAt: number }>`
  - `verifySessionToken(token: string, env: Env): Promise<Principal>`
  - `authenticate(token: string, env: Env): Promise<Principal>` (session token first, then Supabase JWT)
  - `class AuthError extends Error { status = 401 }`
  - `requireAuth: MiddlewareHandler` (reads `Authorization: Bearer`, sets `c.set("principal", p)`, returns `401 {error}` otherwise)
  - `bearerFrom(header: string | undefined): string | undefined`

- [ ] **Step 1: Write `backend/package.json`**

```json
{
  "name": "@openclicky/backend",
  "version": "0.1.0",
  "private": true,
  "type": "module",
  "main": "dist/app.js",
  "scripts": {
    "dev": "tsx watch src/node.ts",
    "dev:worker": "wrangler dev",
    "build": "tsc -p tsconfig.json",
    "start": "node dist/node.js",
    "test": "vitest run",
    "mint-jwt": "node scripts/mint-dev-jwt.mjs"
  },
  "dependencies": {
    "@hono/node-server": "^2.1.1",
    "dotenv": "^17.4.2",
    "hono": "^4.13.5",
    "jose": "^6.2.10"
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "tsx": "^4.23.13",
    "typescript": "^5.9.0",
    "vitest": "^4.1.11",
    "wrangler": "^4.128.0"
  }
}
```

- [ ] **Step 2: Write `backend/tsconfig.json`** extending `../tsconfig.base.json`, `rootDir: src`, `outDir: dist`, `include: ["src"]`, `lib: ["ES2022", "DOM"]` (Workers/Fetch types).

- [ ] **Step 3: Write the failing test `backend/test/auth.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { SignJWT } from "jose";
import { verifySupabaseJwt, issueSessionToken, verifySessionToken, authenticate, AuthError, bearerFrom } from "../src/auth.js";
import type { Env } from "../src/env.js";

const env: Env = { SUPABASE_JWT_SECRET: "supabase-test-secret-supabase-test-secret", SESSION_TOKEN_SECRET: "session-secret-session-secret-session", SESSION_TOKEN_TTL_SECONDS: "60" };
const key = (s: string) => new TextEncoder().encode(s);
async function supabaseJwt(over: Record<string, unknown> = {}, secret = env.SUPABASE_JWT_SECRET!) {
  return new SignJWT({ role: "authenticated", email: "dev@example.com", ...over })
    .setProtectedHeader({ alg: "HS256" }).setSubject("user-123").setIssuedAt().setExpirationTime("10m").sign(key(secret));
}

describe("verifySupabaseJwt", () => {
  it("accepts a JWT signed with SUPABASE_JWT_SECRET", async () => {
    const p = await verifySupabaseJwt(await supabaseJwt(), env);
    expect(p).toMatchObject({ sub: "user-123", email: "dev@example.com", via: "supabase" });
  });
  it("rejects a JWT signed with another secret", async () => {
    await expect(verifySupabaseJwt(await supabaseJwt({}, "wrong-secret-wrong-secret-wrong-secret"), env)).rejects.toBeInstanceOf(AuthError);
  });
  it("rejects garbage", async () => {
    await expect(verifySupabaseJwt("not-a-jwt", env)).rejects.toBeInstanceOf(AuthError);
  });
});

describe("session tokens", () => {
  it("round-trips", async () => {
    const { token, expiresAt } = await issueSessionToken({ sub: "user-123", email: "dev@example.com" }, env);
    expect(expiresAt).toBeGreaterThan(Date.now() / 1000);
    const p = await verifySessionToken(token, env);
    expect(p).toMatchObject({ sub: "user-123", via: "session" });
  });
  it("is not accepted as a Supabase JWT and vice versa", async () => {
    const { token } = await issueSessionToken({ sub: "u" }, env);
    await expect(verifySupabaseJwt(token, env)).rejects.toBeInstanceOf(AuthError);
    await expect(verifySessionToken(await supabaseJwt(), env)).rejects.toBeInstanceOf(AuthError);
  });
});

describe("authenticate", () => {
  it("accepts either token kind", async () => {
    expect((await authenticate(await supabaseJwt(), env)).via).toBe("supabase");
    const { token } = await issueSessionToken({ sub: "u" }, env);
    expect((await authenticate(token, env)).via).toBe("session");
  });
  it("fails without secrets configured", async () => {
    await expect(authenticate(await supabaseJwt(), {})).rejects.toBeInstanceOf(AuthError);
  });
});

describe("bearerFrom", () => {
  it("parses Bearer header", () => {
    expect(bearerFrom("Bearer abc")).toBe("abc");
    expect(bearerFrom("bearer abc")).toBe("abc");
    expect(bearerFrom(undefined)).toBeUndefined();
    expect(bearerFrom("Basic x")).toBeUndefined();
  });
});
```

- [ ] **Step 4: Run test to verify it fails** — `cd backend && npx vitest run test/auth.test.ts` → FAIL (module not found).

- [ ] **Step 5: Write `backend/src/env.ts`**

```ts
import type { Context } from "hono";
import { env as honoEnv } from "hono/adapter";

export type Env = {
  OPENAI_API_KEY?: string; OPENAI_BASE_URL?: string; OPENAI_MODEL?: string;
  ANTHROPIC_API_KEY?: string; ANTHROPIC_BASE_URL?: string;
  SUPABASE_URL?: string; SUPABASE_JWT_SECRET?: string;
  SESSION_TOKEN_SECRET?: string; SESSION_TOKEN_TTL_SECONDS?: string;
};

export function getEnv(c: Context): Env {
  return honoEnv<Env>(c);
}
```

- [ ] **Step 6: Write `backend/src/auth.ts`**

```ts
import { SignJWT, jwtVerify, createRemoteJWKSet, decodeProtectedHeader } from "jose";
import type { MiddlewareHandler } from "hono";
import { getEnv, type Env } from "./env.js";

export class AuthError extends Error { status = 401; }
export type Principal = { sub: string; email?: string; via: "supabase" | "session" };
const SESSION_TYP = "openclicky-session";
const enc = (s: string) => new TextEncoder().encode(s);

export function bearerFrom(header: string | undefined): string | undefined {
  if (!header) return undefined;
  const m = /^Bearer\s+(.+)$/i.exec(header.trim());
  return m?.[1];
}

export async function verifySupabaseJwt(token: string, env: Env): Promise<Principal> {
  let header;
  try { header = decodeProtectedHeader(token); } catch { throw new AuthError("malformed token"); }
  if (header.typ === SESSION_TYP) throw new AuthError("session token is not a Supabase JWT");
  try {
    if (env.SUPABASE_JWT_SECRET) {
      const { payload } = await jwtVerify(token, enc(env.SUPABASE_JWT_SECRET), { algorithms: ["HS256"] });
      return { sub: String(payload.sub), email: payload.email as string | undefined, via: "supabase" };
    }
    if (env.SUPABASE_URL) {
      const jwks = createRemoteJWKSet(new URL("/auth/v1/.well-known/jwks.json", env.SUPABASE_URL));
      const { payload } = await jwtVerify(token, jwks);
      return { sub: String(payload.sub), email: payload.email as string | undefined, via: "supabase" };
    }
  } catch (e) {
    if (e instanceof AuthError) throw e;
    throw new AuthError(`invalid Supabase JWT: ${(e as Error).message}`);
  }
  throw new AuthError("backend has no SUPABASE_JWT_SECRET or SUPABASE_URL configured");
}

export async function issueSessionToken(p: { sub: string; email?: string }, env: Env) {
  if (!env.SESSION_TOKEN_SECRET) throw new AuthError("SESSION_TOKEN_SECRET not configured");
  const ttl = Number(env.SESSION_TOKEN_TTL_SECONDS ?? 3600);
  const expiresAt = Math.floor(Date.now() / 1000) + ttl;
  const token = await new SignJWT({ email: p.email })
    .setProtectedHeader({ alg: "HS256", typ: SESSION_TYP })
    .setSubject(p.sub).setIssuedAt().setExpirationTime(expiresAt).setIssuer("openclicky-backend")
    .sign(enc(env.SESSION_TOKEN_SECRET));
  return { token, expiresAt };
}

export async function verifySessionToken(token: string, env: Env): Promise<Principal> {
  if (!env.SESSION_TOKEN_SECRET) throw new AuthError("SESSION_TOKEN_SECRET not configured");
  let header;
  try { header = decodeProtectedHeader(token); } catch { throw new AuthError("malformed token"); }
  if (header.typ !== SESSION_TYP) throw new AuthError("not a session token");
  try {
    const { payload } = await jwtVerify(token, enc(env.SESSION_TOKEN_SECRET), { algorithms: ["HS256"], issuer: "openclicky-backend" });
    return { sub: String(payload.sub), email: payload.email as string | undefined, via: "session" };
  } catch (e) { throw new AuthError(`invalid session token: ${(e as Error).message}`); }
}

export async function authenticate(token: string, env: Env): Promise<Principal> {
  let header;
  try { header = decodeProtectedHeader(token); } catch { throw new AuthError("malformed token"); }
  return header.typ === SESSION_TYP ? verifySessionToken(token, env) : verifySupabaseJwt(token, env);
}

export const requireAuth: MiddlewareHandler<{ Variables: { principal: Principal } }> = async (c, next) => {
  const token = bearerFrom(c.req.header("authorization"));
  if (!token) return c.json({ error: "missing Authorization: Bearer <token>" }, 401);
  try { c.set("principal", await authenticate(token, getEnv(c))); }
  catch (e) { return c.json({ error: e instanceof Error ? e.message : "unauthorized" }, 401); }
  await next();
};
```

- [ ] **Step 7: Run test to verify it passes** — `cd backend && npx vitest run test/auth.test.ts` → PASS.

---

### Task 3: Backend app + streaming proxies + Node/Workers entry

**Files:**
- Create: `backend/src/proxy.ts`, `backend/src/app.ts`, `backend/src/node.ts`, `backend/wrangler.toml`, `backend/.dev.vars.example`, `backend/scripts/mint-dev-jwt.mjs`
- Test: `backend/test/app.test.ts`

**Interfaces:**
- Consumes: Task 2 (`requireAuth`, `authenticate`, `verifySupabaseJwt`, `issueSessionToken`, `getEnv`).
- Produces:
  - `createApp(): Hono` and `export default app`.
  - Routes: `GET /health` → `{ok:true}`; `POST /agent/session-token` (Supabase JWT only) → `{ token, expiresAt, sub }`; `POST /v1/chat/completions`, `POST /v1/responses` (OpenAI, streaming passthrough), `POST /v1/messages` (Anthropic); all `/v1/*` and `/agent/*` require auth.
  - `proxyOpenAI(c, upstreamPath: string): Promise<Response>`; `proxyAnthropic(c): Promise<Response>`; `applyModelDefault(body: Record<string, unknown>, model?: string): Record<string, unknown>` (sets `model` when missing or `"default"`).

- [ ] **Step 1: Write the failing test `backend/test/app.test.ts`**

```ts
import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import { SignJWT } from "jose";
import { createApp } from "../src/app.js";
import type { Env } from "../src/env.js";

let upstream: http.Server; let upstreamUrl: string; const seen: { url: string; auth?: string; apiKey?: string; body: any }[] = [];
beforeAll(async () => {
  upstream = http.createServer((req, res) => {
    let b = ""; req.on("data", (d) => (b += d)); req.on("end", () => {
      seen.push({ url: req.url!, auth: req.headers.authorization, apiKey: req.headers["x-api-key"] as string, body: JSON.parse(b) });
      res.writeHead(200, { "content-type": "text/event-stream" });
      res.write("data: {\"chunk\":1}\n\n"); setTimeout(() => { res.write("data: [DONE]\n\n"); res.end(); }, 10);
    });
  });
  await new Promise<void>((r) => upstream.listen(0, r));
  upstreamUrl = `http://127.0.0.1:${(upstream.address() as any).port}`;
});
afterAll(() => upstream.close());

const env: Env = { SUPABASE_JWT_SECRET: "supabase-test-secret-supabase-test-secret", SESSION_TOKEN_SECRET: "session-secret-session-secret-session", OPENAI_API_KEY: "sk-upstream", OPENAI_MODEL: "gpt-test", ANTHROPIC_API_KEY: "ak-upstream" };
const app = createApp();
const call = (path: string, init: RequestInit = {}) => app.request(path, init, { ...env, OPENAI_BASE_URL: upstreamUrl + "/v1", ANTHROPIC_BASE_URL: upstreamUrl });
const jwt = () => new SignJWT({ email: "dev@example.com" }).setProtectedHeader({ alg: "HS256" }).setSubject("user-1").setIssuedAt().setExpirationTime("5m").sign(new TextEncoder().encode(env.SUPABASE_JWT_SECRET!));

describe("app", () => {
  it("GET /health", async () => { const r = await call("/health"); expect(r.status).toBe(200); expect(await r.json()).toEqual({ ok: true }); });
  it("401 without auth", async () => {
    const r = await call("/v1/chat/completions", { method: "POST", body: "{}" });
    expect(r.status).toBe(401);
    expect((await call("/agent/session-token", { method: "POST" })).status).toBe(401);
  });
  it("exchanges Supabase JWT for session token, which then authorizes proxy calls", async () => {
    const r = await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await jwt()}` } });
    expect(r.status).toBe(200); const { token, sub } = await r.json(); expect(sub).toBe("user-1");
    const p = await call("/v1/chat/completions", { method: "POST", headers: { authorization: `Bearer ${token}`, "content-type": "application/json" }, body: JSON.stringify({ model: "default", messages: [{ role: "user", content: "hi" }], stream: true }) });
    expect(p.status).toBe(200); expect(await p.text()).toContain("[DONE]");
    const last = seen.at(-1)!; expect(last.url).toBe("/v1/chat/completions"); expect(last.auth).toBe("Bearer sk-upstream"); expect(last.body.model).toBe("gpt-test");
  });
  it("session token cannot be re-exchanged", async () => {
    const { token } = await (await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${await jwt()}` } })).json();
    expect((await call("/agent/session-token", { method: "POST", headers: { authorization: `Bearer ${token}` } })).status).toBe(401);
  });
  it("proxies /v1/responses keeping the caller's model", async () => {
    const r = await call("/v1/responses", { method: "POST", headers: { authorization: `Bearer ${await jwt()}`, "content-type": "application/json" }, body: JSON.stringify({ model: "gpt-5.6-luna", input: "x", stream: true }) });
    expect(r.status).toBe(200); await r.text();
    expect(seen.at(-1)!.body.model).toBe("gpt-5.6-luna"); expect(seen.at(-1)!.url).toBe("/v1/responses");
  });
  it("proxies /v1/messages to Anthropic with x-api-key", async () => {
    const r = await call("/v1/messages", { method: "POST", headers: { authorization: `Bearer ${await jwt()}`, "content-type": "application/json" }, body: JSON.stringify({ model: "claude", messages: [] }) });
    expect(r.status).toBe(200); await r.text();
    expect(seen.at(-1)!.url).toBe("/v1/messages"); expect(seen.at(-1)!.apiKey).toBe("ak-upstream");
  });
  it("502 when upstream key missing", async () => {
    const r = await app.request("/v1/chat/completions", { method: "POST", headers: { authorization: `Bearer ${await jwt()}`, "content-type": "application/json" }, body: "{}" }, { ...env, OPENAI_API_KEY: undefined });
    expect(r.status).toBe(502);
  });
});
```

- [ ] **Step 2: Run test to verify it fails** — `cd backend && npx vitest run test/app.test.ts` → FAIL.

- [ ] **Step 3: Write `backend/src/proxy.ts`**

```ts
import type { Context } from "hono";
import { getEnv } from "./env.js";

export function applyModelDefault(body: Record<string, unknown>, model?: string) {
  if (model && (body.model === undefined || body.model === "default")) return { ...body, model };
  return body;
}
async function readJson(c: Context): Promise<Record<string, unknown>> {
  const text = await c.req.text(); if (!text) return {};
  try { return JSON.parse(text); } catch { return {}; }
}
const HOP = new Set(["content-length", "connection", "keep-alive", "transfer-encoding", "content-encoding"]);
function passthrough(upstream: Response): Response {
  const headers = new Headers();
  upstream.headers.forEach((v, k) => { if (!HOP.has(k.toLowerCase())) headers.set(k, v); });
  return new Response(upstream.body, { status: upstream.status, headers });
}

export async function proxyOpenAI(c: Context, upstreamPath: string): Promise<Response> {
  const env = getEnv(c);
  if (!env.OPENAI_API_KEY) return c.json({ error: "backend missing OPENAI_API_KEY" }, 502);
  const base = (env.OPENAI_BASE_URL ?? "https://api.openai.com/v1").replace(/\/$/, "");
  const body = applyModelDefault(await readJson(c), env.OPENAI_MODEL);
  const upstream = await fetch(base + upstreamPath, {
    method: "POST",
    headers: { "content-type": "application/json", accept: c.req.header("accept") ?? "*/*", authorization: `Bearer ${env.OPENAI_API_KEY}` },
    body: JSON.stringify(body),
  });
  return passthrough(upstream);
}

export async function proxyAnthropic(c: Context): Promise<Response> {
  const env = getEnv(c);
  if (!env.ANTHROPIC_API_KEY) return c.json({ error: "backend missing ANTHROPIC_API_KEY" }, 502);
  const base = (env.ANTHROPIC_BASE_URL ?? "https://api.anthropic.com").replace(/\/$/, "");
  const upstream = await fetch(base + "/v1/messages", {
    method: "POST",
    headers: { "content-type": "application/json", accept: c.req.header("accept") ?? "*/*", "x-api-key": env.ANTHROPIC_API_KEY, "anthropic-version": c.req.header("anthropic-version") ?? "2023-06-01" },
    body: await c.req.text(),
  });
  return passthrough(upstream);
}
```

- [ ] **Step 4: Write `backend/src/app.ts`**

```ts
import { Hono } from "hono";
import { getEnv } from "./env.js";
import { requireAuth, verifySupabaseJwt, issueSessionToken, bearerFrom, AuthError, type Principal } from "./auth.js";
import { proxyOpenAI, proxyAnthropic } from "./proxy.js";

export function createApp() {
  const app = new Hono<{ Variables: { principal: Principal } }>();
  app.get("/health", (c) => c.json({ ok: true }));

  // Exchange a Supabase JWT for a short-lived session token. Only Supabase JWTs are accepted here.
  app.post("/agent/session-token", async (c) => {
    const env = getEnv(c);
    const token = bearerFrom(c.req.header("authorization"));
    if (!token) return c.json({ error: "missing Authorization: Bearer <supabase jwt>" }, 401);
    try {
      const p = await verifySupabaseJwt(token, env);
      const { token: session, expiresAt } = await issueSessionToken(p, env);
      return c.json({ token: session, expiresAt, sub: p.sub });
    } catch (e) {
      const status = e instanceof AuthError ? 401 : 500;
      return c.json({ error: (e as Error).message }, status);
    }
  });

  app.use("/agent/*", requireAuth);
  app.use("/v1/*", requireAuth);
  app.post("/v1/chat/completions", (c) => proxyOpenAI(c, "/chat/completions"));
  app.post("/v1/responses", (c) => proxyOpenAI(c, "/responses"));
  app.post("/v1/messages", (c) => proxyAnthropic(c));
  // TODO(next cut): /agent/realtime/*, /skills/*, /codex-thread-launch (see REVERSE-ENGINEERING.md §8)
  return app;
}
const app = createApp();
export default app;
```

- [ ] **Step 5: Write `backend/src/node.ts`**

```ts
import { serve } from "@hono/node-server";
import { config as loadDotenv } from "dotenv";
import path from "node:path";
import app from "./app.js";

loadDotenv({ path: path.resolve(process.cwd(), ".dev.vars"), quiet: true });
loadDotenv({ path: path.resolve(process.cwd(), "../.env"), quiet: true });
const port = Number(process.env.PORT ?? 8787);
serve({ fetch: app.fetch, port }, (info) => console.log(`openclicky backend listening on http://localhost:${info.port}`));
```

- [ ] **Step 6: Write `backend/wrangler.toml`**, `backend/.dev.vars.example`, `backend/scripts/mint-dev-jwt.mjs` (reads `.dev.vars` for `SUPABASE_JWT_SECRET`, prints an HS256 JWT with `sub=dev-user`, `role=authenticated`, `exp=+24h`; args `--sub`, `--email`).

- [ ] **Step 7: Run tests** — `cd backend && npx vitest run` → all PASS. `npm run build -w backend` → exits 0.

- [ ] **Step 8: Verify live** — start `npm run dev -w backend` in background; `curl -s localhost:8787/health` → `{"ok":true}`; `curl -s -X POST localhost:8787/v1/chat/completions` → 401 JSON.

---

### Task 4: Skills port + config template + port script

**Files:**
- Create: `scripts/port-skills.mjs`, `skills/**` (generated), `skills/ATTRIBUTION.md`, `config/codex-config.toml`

**Interfaces:**
- Produces: `skills/ModelInstructions.md`; 15 skill dirs (`openclicky-artifacts`, `openclicky-build-preview`, `openclicky-creative-studio`, `openclicky-dev-setup-doctor`, `openclicky-email-assistant`, `openclicky-google-workspace`, `openclicky-repo-operator`, `openclicky-research-report`, `cua-driver`, `doc`, `frontend-design`, `obsidian`, `pdf`, `spreadsheet`, `vercel-deploy`); `config/codex-config.toml` template with placeholders `{{OPENCLICKY_ROOT}}`, `{{BACKEND_URL}}`, `{{WORKSPACE}}`.

- [ ] **Step 1: Write `scripts/port-skills.mjs`**: copies each dir under `reference/clicky-bundled-skills/` to `skills/` (renaming `clicky-*` → `openclicky-*`), rewrites text files (`.md`, `.yaml`, `.sh`, `.py`) with rules: `HeyClicky` → `OpenClicky`; `\bClicky\b` → `OpenClicky`; `\bclicky-(artifacts|build-preview|creative-studio|dev-setup-doctor|email-assistant|google-workspace|repo-operator|research-report|crons|scheduled-crons)\b` → `openclicky-$1`; `.clicky-preview.log` → `.openclicky-preview.log`; frontmatter `name: clicky-x` → `name: openclicky-x`. Also writes `skills/ModelInstructions.md` from `reference/clicky-model-instructions-verbatim.md` with the same rules. Skips any dir named `powerpoint`. Binary files (`.png`, `.svg`) copied byte-for-byte.
- [ ] **Step 2: Run** `npm run port-skills`; verify `ls skills` shows 15 dirs + `ModelInstructions.md`; `grep -rIl 'HeyClicky\|clicky-' skills` returns nothing except `openclicky-*`.
- [ ] **Step 3: Write `skills/ATTRIBUTION.md`** (source: HeyClicky bundled skills reverse-engineered on 2026-09-02, rebranded; vercel-deploy keeps its own LICENSE.txt/ATTRIBUTION.md; `powerpoint` intentionally omitted).
- [ ] **Step 4: Write `config/codex-config.toml`**:

```toml
# OpenClicky Codex CLI config template.
# Rendered by agent/src/codexHome.ts into $OPENCLICKY_CODEX_HOME/config.toml.
# Placeholders: {{OPENCLICKY_ROOT}} {{BACKEND_URL}} {{WORKSPACE}} {{MODEL_LINE}}
{{MODEL_LINE}}
model_provider = "openclicky"
model_instructions_file = "{{OPENCLICKY_ROOT}}/skills/ModelInstructions.md"
js_repl = true
multi_agent = true

# Keys stay server-side: Codex talks to the OpenClicky backend, which holds the real provider key.
# Codex sends `Authorization: Bearer $OPENCLICKY_SESSION_TOKEN` (env_key) on every request.
[model_providers.openclicky]
name = "OpenClicky backend"
base_url = "{{BACKEND_URL}}/v1"
wire_api = "responses"          # Codex >= 0.15x rejects wire_api = "chat"
env_key = "OPENCLICKY_SESSION_TOKEN"
requires_openai_auth = false
supports_websockets = false

[mcp_servers.openaiDeveloperDocs]
url = "https://developers.openai.com/mcp"

[[skills.config]]
path = "{{OPENCLICKY_ROOT}}/skills"
enabled = true

# TODO(next cut): computer-use (cua-driver) and composio MCP servers — see reference/codex-config.toml.
# clicky-crons intentionally removed: Remote Tasks are out of scope for this cut.

[projects."{{WORKSPACE}}"]
trust_level = "trusted"
```

- [ ] **Step 5: Verify** the rendered config is accepted: render by hand with sed into a temp `CODEX_HOME`, run `CODEX_HOME=<tmp> codex app-server --strict-config --stdio` with an `initialize` line; expect an `initialize` result and no config error on stderr.

---

### Task 5: Agent config + Codex home rendering + JSON-RPC + artifacts (pure units)

**Files:**
- Create: `agent/package.json`, `agent/tsconfig.json`, `agent/src/config.ts`, `agent/src/codexHome.ts`, `agent/src/jsonrpc.ts`, `agent/src/artifacts.ts`
- Test: `agent/test/config.test.ts`, `agent/test/codexHome.test.ts`, `agent/test/jsonrpc.test.ts`, `agent/test/artifacts.test.ts`

**Interfaces:**
- Produces:
  - `interface AgentConfig { backendUrl: string; token?: string; codexHome: string; codexBin: string; workspace: string; model?: string; root: string; verbose: boolean }`
  - `resolveConfig(flags: Partial<AgentConfig>, env?: NodeJS.ProcessEnv): AgentConfig` — env: `BACKEND_URL`/`OPENCLICKY_BACKEND_URL`, `OPENCLICKY_TOKEN`, `OPENCLICKY_CODEX_HOME` (default `~/.openclicky/codex-home`), `OPENCLICKY_CODEX_BIN` (default `codex`), `OPENCLICKY_WORKSPACE` (default cwd), `OPENCLICKY_MODEL`; `root` = repo root (`path.resolve(__dirname, "../..")`).
  - `renderCodexConfig(template: string, v: { root: string; backendUrl: string; workspace: string; model?: string }): string`
  - `ensureCodexHome(cfg: AgentConfig): { configPath: string }` — writes `config.toml`, creates dir.
  - `parseJsonLines(buffer: string): { messages: unknown[]; rest: string }`
  - `class JsonRpcStdio { constructor(stdin: Writable, stdout: Readable); request<T>(method, params?): Promise<T>; respond(id, result): void; onNotification(cb: (method: string, params: any) => void): void; onServerRequest(cb: (id: number|string, method: string, params: any) => void): void; }`
  - `snapshotWorkspace(dir: string): Map<string, number>`; `diffSnapshots(before, after): string[]` (new or modified absolute paths, sorted); `artifactsFromItems(items: unknown[]): string[]` (paths from `fileChange` items' `changes[].path`).

- [ ] **Step 1: Write `agent/package.json`** (`name: @openclicky/agent`, `type: module`, `bin: { openclicky: "dist/cli.js" }`, scripts `build: tsc -p tsconfig.json`, `test: vitest run`, `dev: tsx src/cli.ts`; deps `commander ^15`; devDeps typescript, tsx, vitest, @types/node) and `agent/tsconfig.json` (extends base; `rootDir: src`, `outDir: dist`, `include: ["src"]`).

- [ ] **Step 2: Write failing tests**

`agent/test/config.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { resolveConfig } from "../src/config.js";
describe("resolveConfig", () => {
  it("applies defaults", () => {
    const c = resolveConfig({}, { HOME: "/home/u" } as any);
    expect(c.backendUrl).toBe("http://localhost:8787");
    expect(c.codexHome).toBe("/home/u/.openclicky/codex-home");
    expect(c.codexBin).toBe("codex"); expect(c.workspace).toBe(process.cwd()); expect(c.token).toBeUndefined();
  });
  it("env then flags win", () => {
    const c = resolveConfig({ token: "flag" }, { HOME: "/h", BACKEND_URL: "http://b:1/", OPENCLICKY_TOKEN: "env", OPENCLICKY_WORKSPACE: "/ws" } as any);
    expect(c.backendUrl).toBe("http://b:1"); expect(c.token).toBe("flag"); expect(c.workspace).toBe("/ws");
  });
});
```
`agent/test/codexHome.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { renderCodexConfig } from "../src/codexHome.js";
const tpl = `{{MODEL_LINE}}\nbase_url = "{{BACKEND_URL}}/v1"\npath = "{{OPENCLICKY_ROOT}}/skills"\n[projects."{{WORKSPACE}}"]\n`;
describe("renderCodexConfig", () => {
  it("fills placeholders and omits model line when unset", () => {
    const out = renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x", workspace: "/w" });
    expect(out).toContain('base_url = "http://x/v1"'); expect(out).toContain('path = "/r/skills"'); expect(out).toContain('[projects."/w"]'); expect(out).not.toContain("{{"); expect(out).not.toMatch(/^model = /m);
  });
  it("emits model line when set", () => {
    expect(renderCodexConfig(tpl, { root: "/r", backendUrl: "http://x", workspace: "/w", model: "gpt-5.6-luna" })).toMatch(/^model = "gpt-5.6-luna"/m);
  });
});
```
`agent/test/jsonrpc.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { PassThrough } from "node:stream";
import { parseJsonLines, JsonRpcStdio } from "../src/jsonrpc.js";
describe("parseJsonLines", () => {
  it("splits complete lines and keeps the remainder", () => {
    const r = parseJsonLines('{"a":1}\n{"b":2}\n{"c"');
    expect(r.messages).toEqual([{ a: 1 }, { b: 2 }]); expect(r.rest).toBe('{"c"');
  });
});
describe("JsonRpcStdio", () => {
  it("correlates responses, routes notifications and server requests", async () => {
    const toChild = new PassThrough(); const fromChild = new PassThrough();
    const rpc = new JsonRpcStdio(toChild, fromChild);
    const notes: string[] = []; rpc.onNotification((m) => notes.push(m));
    rpc.onServerRequest((id, m) => rpc.respond(id, { decision: "accept", m }));
    let written = ""; toChild.on("data", (d) => (written += d));
    const p = rpc.request<{ ok: boolean }>("initialize", { x: 1 });
    fromChild.write('{"jsonrpc":"2.0","method":"thread/started","params":{}}\n');
    fromChild.write('{"jsonrpc":"2.0","id":99,"method":"item/commandExecution/requestApproval","params":{}}\n');
    fromChild.write('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}\n');
    expect(await p).toEqual({ ok: true }); expect(notes).toEqual(["thread/started"]);
    expect(written).toContain('"method":"initialize"'); expect(written).toContain('"id":99,"result":{"decision":"accept"');
  });
  it("rejects on error responses", async () => {
    const toChild = new PassThrough(); const fromChild = new PassThrough(); const rpc = new JsonRpcStdio(toChild, fromChild);
    const p = rpc.request("x"); fromChild.write('{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"boom"}}\n');
    await expect(p).rejects.toThrow("boom");
  });
});
```
`agent/test/artifacts.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import fs from "node:fs"; import os from "node:os"; import path from "node:path";
import { snapshotWorkspace, diffSnapshots, artifactsFromItems } from "../src/artifacts.js";
describe("artifacts", () => {
  it("detects new and modified files, ignoring node_modules/.git", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-art-"));
    fs.writeFileSync(path.join(dir, "old.txt"), "a"); fs.mkdirSync(path.join(dir, "node_modules")); fs.writeFileSync(path.join(dir, "node_modules", "x.js"), "");
    const before = snapshotWorkspace(dir);
    fs.writeFileSync(path.join(dir, "new.txt"), "b"); fs.writeFileSync(path.join(dir, "old.txt"), "changed"); fs.utimesSync(path.join(dir, "old.txt"), new Date(), new Date(Date.now() + 5000)); fs.writeFileSync(path.join(dir, "node_modules", "y.js"), "");
    expect(diffSnapshots(before, snapshotWorkspace(dir))).toEqual([path.join(dir, "new.txt"), path.join(dir, "old.txt")]);
  });
  it("extracts fileChange paths", () => {
    expect(artifactsFromItems([{ type: "fileChange", changes: [{ path: "/a/b.txt", kind: "add" }] }, { type: "agentMessage" }])).toEqual(["/a/b.txt"]);
  });
});
```

- [ ] **Step 3: Run tests to verify they fail** — `cd agent && npx vitest run` → FAIL (modules missing).

- [ ] **Step 4: Implement `src/config.ts`, `src/codexHome.ts`, `src/jsonrpc.ts`, `src/artifacts.ts`** per the interfaces above. `snapshotWorkspace` walks recursively (max depth 6, skips `node_modules`, `.git`, `dist`, hidden dirs) and records `mtimeMs` + size; `diffSnapshots` returns paths whose entry is new or whose mtime/size changed.

- [ ] **Step 5: Run tests** — `cd agent && npx vitest run` → PASS.

---

### Task 6: CodexAgent bridge + ask lane + CLI + integration test

**Files:**
- Create: `agent/src/codex.ts`, `agent/src/ask.ts`, `agent/src/cli.ts`
- Test: `agent/test/ask.test.ts`, `agent/test/integration.test.ts`

**Interfaces:**
- Consumes: Task 5 units.
- Produces:
  - `interface RunResult { threadId: string; turnId: string; status: "completed" | "failed" | "interrupted"; finalMessage: string; artifacts: string[]; error?: string }`
  - `class CodexAgent { constructor(cfg: AgentConfig, hooks?: { onEvent?: (line: string) => void }); start(): Promise<void>; run(task: string, opts?: { threadId?: string; imagePath?: string }): Promise<RunResult>; stop(): Promise<void> }`
  - `ask(cfg: AgentConfig, question: string, opts?: { imagePath?: string; fetchImpl?: typeof fetch }): Promise<string>`; `parseSseStream(text: string): string` (joins `choices[0].delta.content`).
  - CLI `openclicky run <task> [--thread id] [--image path] [--backend-url url] [--token t] [--cwd dir] [--model m] [--json] [--verbose]`, `openclicky ask <question> [--image path] ...`, `openclicky token --email e --password p` (Supabase password grant with `SUPABASE_URL`/`SUPABASE_ANON_KEY`, then exchange at `/agent/session-token`; prints session token).

- [ ] **Step 1: Write `agent/test/ask.test.ts`** (fake backend `http.Server` returning SSE chunks; assert `ask` sends `Authorization: Bearer <token>`, `stream: true`, `model: "default"`, image becomes `image_url` data URL part; result equals joined deltas; non-200 throws with body).

- [ ] **Step 2: Write `agent/test/integration.test.ts`** — skipped unless `codex` is on PATH. Fake Responses server on an ephemeral port: request 1 (no `function_call_output` in input) → streams `function_call` item `{name:"exec_command", arguments: JSON.stringify({cmd:"printf hi > hello.txt"}), call_id:"call_1"}`; request 2 (has `function_call_output`) → streams message `"Created hello.txt"`. `CodexAgent` with `codexHome` temp, `backendUrl` = fake, `workspace` temp. Assert: `hello.txt` content `hi`; `finalMessage` contains `Created hello.txt`; `artifacts` includes `hello.txt`; `status === "completed"`. Then `run("again", {threadId})` resumes: fake sees the previous user message in `input` and result `threadId` equals the first. Also assert every request had `authorization: Bearer test-token`.

- [ ] **Step 3: Run to verify they fail.**

- [ ] **Step 4: Implement `agent/src/codex.ts`**

```ts
import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import { JsonRpcStdio } from "./jsonrpc.js";
import { ensureCodexHome } from "./codexHome.js";
import { snapshotWorkspace, diffSnapshots, artifactsFromItems } from "./artifacts.js";
import type { AgentConfig } from "./config.js";

export interface RunResult { threadId: string; turnId: string; status: "completed" | "failed" | "interrupted"; finalMessage: string; artifacts: string[]; error?: string }

export class CodexAgent {
  private child?: ChildProcess; private rpc?: JsonRpcStdio;
  constructor(private cfg: AgentConfig, private hooks: { onEvent?: (line: string) => void } = {}) {}
  private log(line: string) { this.hooks.onEvent?.(line); }

  async start() {
    ensureCodexHome(this.cfg);
    const env = { ...process.env, CODEX_HOME: this.cfg.codexHome, OPENCLICKY_SESSION_TOKEN: this.cfg.token ?? "" };
    delete env.OPENAI_API_KEY; delete env.ANTHROPIC_API_KEY; // keys never reach the agent engine
    this.child = spawn(this.cfg.codexBin, ["app-server", "--stdio"], { env, stdio: ["pipe", "pipe", "pipe"] });
    this.child.stderr!.on("data", (d) => { if (this.cfg.verbose) process.stderr.write(`[codex] ${d}`); });
    const exited = new Promise<never>((_, rej) => this.child!.once("exit", (code) => rej(new Error(`codex exited with code ${code}`))));
    this.rpc = new JsonRpcStdio(this.child.stdin!, this.child.stdout!);
    this.rpc.onServerRequest((id, method) => {
      // Headless first cut: the user's instruction IS the approval (ModelInstructions approval gate).
      if (method === "item/commandExecution/requestApproval" || method === "item/fileChange/requestApproval") this.rpc!.respond(id, { decision: "accept" });
      else if (method === "item/permissions/requestApproval") this.rpc!.respond(id, { decision: "accept" });
      else if (method === "item/tool/requestUserInput") this.rpc!.respond(id, { answers: {} });
      else if (method === "mcpServer/elicitation/request") this.rpc!.respond(id, { action: "decline" });
      else this.rpc!.respond(id, {});
      this.log(`approval auto-accepted: ${method}`);
    });
    await Promise.race([this.rpc.request("initialize", { clientInfo: { name: "openclicky", title: "OpenClicky", version: "0.1.0" }, capabilities: { experimentalApi: true } }), exited]);
  }

  async run(task: string, opts: { threadId?: string; imagePath?: string } = {}): Promise<RunResult> {
    const rpc = this.rpc!; const cwd = this.cfg.workspace;
    const threadOpts = { cwd, approvalPolicy: "never", sandbox: "workspace-write", ...(this.cfg.model ? { model: this.cfg.model } : {}) };
    const started = opts.threadId
      ? await rpc.request<any>("thread/resume", { threadId: opts.threadId, ...threadOpts })
      : await rpc.request<any>("thread/start", threadOpts);
    const threadId: string = started.thread.id;
    this.log(`${opts.threadId ? "resumed" : "started"} thread ${threadId} (model ${started.model}, provider ${started.modelProvider})`);

    const input: any[] = [{ type: "text", text: task }];
    if (opts.imagePath) {
      if (fs.existsSync(opts.imagePath)) input.push({ type: "localImage", path: opts.imagePath });
      else this.log(`screenshot attach not yet wired: ${opts.imagePath} not found`);
    }
    const before = snapshotWorkspace(cwd);
    let finalMessage = ""; const items: unknown[] = []; let error: string | undefined;
    const done = new Promise<any>((resolve) => rpc.onNotification((method, params) => {
      if (params?.threadId && params.threadId !== threadId) return;
      if (method === "item/completed") { items.push(params.item); if (params.item?.type === "agentMessage") finalMessage = params.item.text ?? finalMessage; this.log(`item ${params.item?.type}`); }
      if (method === "error") error = params.error?.message ?? String(params.error);
      if (method === "turn/completed") resolve(params.turn);
    }));
    const { turn } = await rpc.request<any>("turn/start", { threadId, input });
    const completed = await done;
    const artifacts = Array.from(new Set([...artifactsFromItems(items), ...diffSnapshots(before, snapshotWorkspace(cwd))])).sort();
    return { threadId, turnId: turn.id, status: completed.status, finalMessage, artifacts, error: completed.error?.message ?? error };
  }

  async stop() { this.child?.kill(); }
}
```

- [ ] **Step 5: Implement `agent/src/ask.ts`** (build messages with optional image data URL; POST `${backendUrl}/v1/chat/completions` with `{model:"default", stream:true, messages}`; on non-2xx throw `Error(\`backend ${status}: ${text}\`)`; read the SSE body and join deltas via `parseSseStream`).

- [ ] **Step 6: Implement `agent/src/cli.ts`** (commander; `run` prints `thread: <id>` then final message, then `artifacts:` list; exit 1 on `status !== "completed"` or thrown error; `--json` prints `RunResult`; `ask` prints the answer; `token` helper). Always call `agent.stop()` in `finally`.

- [ ] **Step 7: Run all agent tests** — `cd agent && npx vitest run` → PASS (integration included, since `codex` is installed). `npm run build -w agent` → exits 0.

- [ ] **Step 8: Live smoke** with fake upstream removed: run backend with real `.dev.vars` if a key exists; otherwise document blocker.

---

### Task 7: README + final verification against the definition of done

**Files:**
- Modify: `README.md` (replace with architecture/setup/run/auth-flow; keep old reference index as a final "Reference material" section)

- [ ] **Step 1: Write README** sections: What this is; Architecture (ASCII diagram of agent → codex → backend → OpenAI, Supabase auth); Prerequisites (Node 22, `npm i -g @openai/codex`); Setup (`npm install`, `.dev.vars`, `.env`); Run backend (`npm run dev -w backend`, `wrangler dev`); Run agent (`npx openclicky ...` or `node agent/dist/cli.js`, env vars); Auth flow (Supabase sign-in → JWT → `/agent/session-token` → session token → `OPENCLICKY_TOKEN`; local dev: `npm run mint-jwt -w backend`); Scope/not-implemented list; Next cut.
- [ ] **Step 2: Run the definition-of-done checklist** and record real outputs: health, 401, session-token exchange, `ask`, `run` creating `hello.txt`, `run --thread` twice. Where no real provider key exists, run the same commands against the fake upstream from the integration test and label the result clearly as fake-model.

## Self-Review

- Spec coverage: backend endpoints (Tasks 2–3), auth flow (2–3), agent bridge + CLI + two lanes + threads + screenshot (5–6), skills/config (4), scaffolding/env/README (1, 7), DoD verification (7). `/v1/responses` is an addition forced by Codex 0.152.1 and is documented.
- Type consistency: `AgentConfig` fields (`backendUrl, token, codexHome, codexBin, workspace, model, root, verbose`) are used identically in Tasks 5 and 6; `RunResult` shape matches CLI usage; `JsonRpcStdio` API (`request/respond/onNotification/onServerRequest`) matches its test and `codex.ts`.
