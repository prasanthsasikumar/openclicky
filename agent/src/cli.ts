#!/usr/bin/env node
import { Command } from "commander";
import path from "node:path";
import { resolveConfig, type AgentConfig } from "./config.js";
import { CodexAgent } from "./codex.js";
import { ask } from "./ask.js";

const program = new Command();
program
  .name("openclicky")
  .description("OpenClicky headless agent: run tasks through Codex via the OpenClicky backend, or ask quick questions.")
  .version("0.1.0");

const withCommonOptions = (cmd: Command) =>
  cmd
    .option("--backend-url <url>", "OpenClicky backend URL (env BACKEND_URL; default http://localhost:8787)")
    .option("--token <token>", "Supabase JWT or session token (env OPENCLICKY_TOKEN)")
    .option("--image <path>", "attach a local screenshot/image")
    .option("--json", "print the result as JSON")
    .option("--verbose", "show Codex stderr / request details");

function configFrom(opts: Record<string, any>): AgentConfig {
  return resolveConfig({
    backendUrl: opts.backendUrl,
    token: opts.token,
    workspace: opts.cwd ? path.resolve(opts.cwd) : undefined,
    model: opts.model,
    verbose: opts.verbose ? true : undefined,
  });
}

const fail = (msg: string): never => {
  process.stderr.write(`error: ${msg}\n`);
  process.exit(1);
};

withCommonOptions(
  program
    .command("run")
    .description("full agent run (spawns Codex on a thread)")
    .argument("<task...>", "what to do")
    .option("--thread <id>", "resume an existing thread")
    .option("--cwd <dir>", "working directory for the agent (env OPENCLICKY_WORKSPACE; default: cwd)")
    .option("--model <model>", "Codex model override (env OPENCLICKY_MODEL)"),
).action(async (taskParts: string[], opts) => {
  const cfg = configFrom(opts);
  if (!cfg.token) fail("missing token: set OPENCLICKY_TOKEN or pass --token");
  const task = taskParts.join(" ");
  const agent = new CodexAgent(cfg, { onEvent: (line) => process.stderr.write(`▸ ${line}\n`) });
  try {
    await agent.start();
    const result = await agent.run(task, { threadId: opts.thread, imagePath: opts.image });
    if (opts.json) {
      process.stdout.write(JSON.stringify(result, null, 2) + "\n");
    } else {
      process.stderr.write(`thread: ${result.threadId}\n`);
      process.stdout.write((result.finalMessage || "(no final message)") + "\n");
      if (result.artifacts.length) process.stdout.write(`\nartifacts:\n${result.artifacts.map((a) => `  ${a}`).join("\n")}\n`);
    }
    if (result.status !== "completed") fail(`turn ${result.status}${result.error ? `: ${result.error}` : ""}`);
  } catch (e) {
    fail((e as Error).message);
  } finally {
    await agent.stop();
  }
});

withCommonOptions(
  program.command("ask").description("quick answer through the backend (no agent spawn)").argument("<question...>", "the question"),
).action(async (parts: string[], opts) => {
  const cfg = configFrom(opts);
  try {
    let streamed = false;
    const answer = await ask(cfg, parts.join(" "), {
      imagePath: opts.image,
      onDelta: opts.json
        ? undefined
        : (t) => {
            streamed = true;
            process.stdout.write(t);
          },
    });
    if (opts.json) process.stdout.write(JSON.stringify({ answer }) + "\n");
    else process.stdout.write(streamed ? "\n" : answer + "\n");
  } catch (e) {
    fail((e as Error).message);
  }
});

program
  .command("token")
  .description("sign in to Supabase with email/password and exchange the JWT for an OpenClicky session token")
  .requiredOption("--email <email>")
  .requiredOption("--password <password>")
  .option("--supabase-url <url>", "env SUPABASE_URL")
  .option("--anon-key <key>", "env SUPABASE_ANON_KEY")
  .option("--backend-url <url>", "env BACKEND_URL")
  .option("--jwt-only", "print the Supabase JWT instead of exchanging it")
  .action(async (opts) => {
    const supabaseUrl = (opts.supabaseUrl ?? process.env.SUPABASE_URL ?? "").replace(/\/+$/, "");
    const anonKey = opts.anonKey ?? process.env.SUPABASE_ANON_KEY;
    if (!supabaseUrl || !anonKey) fail("SUPABASE_URL and SUPABASE_ANON_KEY are required (flags or env)");
    const cfg = resolveConfig({ backendUrl: opts.backendUrl });
    try {
      const signIn = await fetch(`${supabaseUrl}/auth/v1/token?grant_type=password`, {
        method: "POST",
        headers: { "content-type": "application/json", apikey: anonKey },
        body: JSON.stringify({ email: opts.email, password: opts.password }),
      });
      if (!signIn.ok) fail(`supabase sign-in failed (${signIn.status}): ${(await signIn.text()).slice(0, 300)}`);
      const { access_token } = (await signIn.json()) as { access_token: string };
      if (opts.jwtOnly) {
        process.stdout.write(access_token + "\n");
        return;
      }
      const ex = await fetch(`${cfg.backendUrl}/agent/session-token`, { method: "POST", headers: { authorization: `Bearer ${access_token}` } });
      if (!ex.ok) fail(`session-token exchange failed (${ex.status}): ${(await ex.text()).slice(0, 300)}`);
      const { token, expiresAt } = (await ex.json()) as { token: string; expiresAt: number };
      process.stderr.write(`session token expires at ${new Date(expiresAt * 1000).toISOString()}\n`);
      process.stdout.write(token + "\n");
    } catch (e) {
      fail((e as Error).message);
    }
  });

program.parseAsync(process.argv).catch((e) => fail((e as Error).message));
