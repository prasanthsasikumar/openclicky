#!/usr/bin/env node
import { Command } from "commander";
import path from "node:path";
import readline from "node:readline";
import { resolveConfig, type AgentConfig } from "./config.js";
import { readVersion } from "./version.js";
import { backendHeaders } from "./backendHeaders.js";
import { CodexAgent, type ApprovalRequest, type ApprovalDecision, type RunResult, buildChildEnv } from "./codex.js";
import { ensureCodexHome } from "./codexHome.js";
import { ask } from "./ask.js";
import { gate, type Lane } from "./gate.js";
import { captureScreen } from "./screenshot.js";
import { recordAudio, transcribe } from "./audio.js";
import { RealtimeSession, defaultMicCommand } from "./realtime.js";
import { listLibrary, setActive, createSkillFiles } from "./skillsLibrary.js";

const program = new Command();
program
  .name("openclicky")
  .description("OpenClicky headless agent: run tasks through Codex via the OpenClicky backend, or ask quick questions.")
  .version(readVersion());

/**
 * Output modes: human (text on stdout, milestones on stderr), --json (one result object), or
 * --events (JSON Lines on stdout: lane / event / delta / answer / result / error — for UIs).
 */
let eventsMode = false;
const emit = (obj: Record<string, unknown>) => process.stdout.write(JSON.stringify(obj) + "\n");

/**
 * Signals "report this and exit 1" without calling `process.exit` — every command wraps a
 * `CodexAgent` run in a `try { … } finally { await agent.stop(); }`, and `process.exit` inside that
 * try would skip the finally and leave the Codex child running. Thrown instead, it unwinds through
 * those finally blocks like any other exception; only the top-level handler at the bottom of this
 * file (reached after all cleanup has run) turns it into the actual exit.
 */
class CliFailure extends Error {}

const reportFailure = (msg: string) => {
  if (eventsMode) emit({ type: "error", message: msg });
  else process.stderr.write(`error: ${msg}\n`);
};

const fail = (msg: string): never => {
  reportFailure(msg);
  throw new CliFailure(msg);
};
const note = (line: string) => (eventsMode ? emit({ type: "event", line }) : process.stderr.write(`▸ ${line}\n`));

/**
 * Every command's outermost catch used to just call `fail(e.message)`. Now that `fail` throws
 * rather than exiting, doing that unconditionally would re-report and re-throw an inner `fail()`
 * call's `CliFailure` a second time (it already printed once, closer to the actual cause) — so a
 * `CliFailure` passes straight through, and only a genuinely new, not-yet-reported error gets
 * reported here.
 */
const propagate = (e: unknown): never => {
  if (e instanceof CliFailure) throw e;
  return fail((e as Error).message);
};

const withCommonOptions = (cmd: Command) =>
  cmd
    .option("--backend-url <url>", "OpenClicky backend URL (env BACKEND_URL; default http://localhost:8787)")
    .option("--token <token>", "Supabase JWT or session token (env OPENCLICKY_TOKEN)")
    .option("--image <path>", "attach a local screenshot/image")
    .option("--screenshot", "capture the screen now (macOS screencapture) and attach it")
    .option("--json", "print the result as JSON")
    .option("--events", "stream JSON Lines events to stdout (for UIs)")
    .option("--verbose", "show Codex stderr / request details")
    .hook("preAction", (_thisCommand, actionCommand) => {
      eventsMode = Boolean(actionCommand.opts().events);
    });

const withRunOptions = (cmd: Command) =>
  cmd
    .option("--thread <id>", "resume an existing thread")
    .option("--cwd <dir>", "working directory for the agent (env OPENCLICKY_WORKSPACE; default: cwd)")
    .option("--model <model>", "Codex model override (env OPENCLICKY_MODEL)")
    .option("--approve", "ask before running commands / changing files outside the sandbox (default: auto-accept)");

function configFrom(opts: Record<string, any>): AgentConfig {
  return resolveConfig({
    backendUrl: opts.backendUrl,
    token: opts.token,
    workspace: opts.cwd ? path.resolve(opts.cwd) : undefined,
    model: opts.model,
    verbose: opts.verbose ? true : undefined,
  });
}

function resolveImage(opts: Record<string, any>): string | undefined {
  if (opts.screenshot) {
    const p = captureScreen();
    note(`captured screenshot ${p}`);
    return p;
  }
  return opts.image;
}

/**
 * Prompt for a password on the terminal without echoing the typed characters back. `readline` has
 * no built-in "hidden input" mode, so muting its own echo of what it reads — after it has written
 * the prompt text itself — is the standard workaround for this (the same trick tools like npm's own
 * prompts use); it still reads and returns every keystroke, it just stops printing them.
 */
async function promptPassword(promptText: string): Promise<string> {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stderr });
    let echo = true;
    (rl as unknown as { _writeToOutput: (s: string) => void })._writeToOutput = (s) => {
      if (echo) process.stderr.write(s);
    };
    rl.question(promptText, (answer) => {
      rl.close();
      process.stderr.write("\n");
      resolve(answer);
    });
    echo = false; // prompt text above is already written; stop echoing from here on
  });
}

/**
 * `--password` puts the password in shell history and in `ps` output for every user on the machine
 * for as long as the process runs. `OPENCLICKY_PASSWORD` (an env var is still readable by other
 * processes of the same user, but not by `ps` or a shell's saved history) and an interactive,
 * echo-off prompt are the safe paths and are preferred in that order; the flag still works — some
 * non-interactive scripting setups have no other way to supply it — but warns loudly when used.
 */
async function resolvePassword(opts: Record<string, any>): Promise<string> {
  if (opts.password) {
    process.stderr.write("warning: --password is insecure (visible in shell history and `ps`); prefer OPENCLICKY_PASSWORD or the interactive prompt\n");
    return opts.password;
  }
  if (process.env.OPENCLICKY_PASSWORD) return process.env.OPENCLICKY_PASSWORD;
  if (process.stdin.isTTY) return promptPassword("password: ");
  return fail("password required: set OPENCLICKY_PASSWORD, pass --password, or run `token` from an interactive terminal");
}

/** Interactive approval prompt on the terminal (stderr/stdin), used with --approve. */
async function promptApproval(req: ApprovalRequest): Promise<ApprovalDecision> {
  const rl = readline.createInterface({ input: process.stdin, output: process.stderr });
  try {
    const answer = await new Promise<string>((resolve) => rl.question(`\n⚠ approval needed — ${req.summary}\n  [y]es / [a]ll for this session / [N]o: `, resolve));
    const a = answer.trim().toLowerCase();
    if (a === "y" || a === "yes") return "accept";
    if (a === "a" || a === "all") return "acceptForSession";
    return "decline";
  } finally {
    rl.close();
  }
}

async function runAgent(cfg: AgentConfig, task: string, opts: Record<string, any>): Promise<RunResult> {
  if (!cfg.token) fail("missing token: set OPENCLICKY_TOKEN or pass --token");
  let streamed = false;
  const agent = new CodexAgent(cfg, {
    onEvent: note,
    onDelta: opts.json
      ? undefined
      : opts.events
        ? (t) => emit({ type: "delta", text: t })
        : (t) => {
            streamed = true;
            process.stdout.write(t);
          },
    onApproval: opts.approve ? promptApproval : undefined,
  });
  try {
    await agent.start();
    const result = await agent.run(task, { threadId: opts.thread, imagePath: resolveImage(opts) });
    if (opts.events) {
      emit({ type: "result", ...result });
    } else if (opts.json) {
      process.stdout.write(JSON.stringify(result, null, 2) + "\n");
    } else {
      if (streamed) process.stdout.write("\n");
      else process.stdout.write((result.finalMessage || "(no final message)") + "\n");
      note(`thread: ${result.threadId}`);
      if (result.artifacts.length) process.stdout.write(`\nartifacts:\n${result.artifacts.map((a) => `  ${a}`).join("\n")}\n`);
    }
    if (result.status !== "completed") fail(`turn ${result.status}${result.error ? `: ${result.error}` : ""}`);
    return result;
  } finally {
    await agent.stop();
  }
}

async function runAsk(cfg: AgentConfig, question: string, opts: Record<string, any>): Promise<string> {
  let streamed = false;
  const answer = await ask(cfg, question, {
    imagePath: resolveImage(opts),
    onDelta: opts.json
      ? undefined
      : opts.events
        ? (t) => emit({ type: "delta", text: t })
        : (t) => {
            streamed = true;
            process.stdout.write(t);
          },
  });
  if (opts.events) emit({ type: "answer", text: answer });
  else if (opts.json) process.stdout.write(JSON.stringify({ answer }) + "\n");
  else process.stdout.write(streamed ? "\n" : answer + "\n");
  return answer;
}

withRunOptions(withCommonOptions(program.command("run").description("full agent run (spawns Codex on a thread)").argument("<task...>", "what to do"))).action(
  async (parts: string[], opts) => {
    try {
      await runAgent(configFrom(opts), parts.join(" "), opts);
    } catch (e) {
      propagate(e);
    }
  },
);

withCommonOptions(program.command("ask").description("quick answer through the backend (no agent spawn)").argument("<question...>", "the question")).action(
  async (parts: string[], opts) => {
    try {
      await runAsk(configFrom(opts), parts.join(" "), opts);
    } catch (e) {
      propagate(e);
    }
  },
);

withRunOptions(
  withCommonOptions(
    program
      .command("do")
      .description("auto-route: a cheap gate model decides between `ask` and `run` (HeyClicky's two-tier routing)")
      .argument("<text...>", "what you said")
      .option("--lane <lane>", "force a lane: ask | agent")
      .option("--gate-only", "only classify: print the lane and exit (for shells that run the lanes themselves)"),
  ),
).action(async (parts: string[], opts) => {
  const cfg = configFrom(opts);
  const text = parts.join(" ");
  try {
    if (opts.gateOnly) {
      const d = await gate(cfg, text);
      if (opts.events || opts.json) emit({ type: "lane", lane: d.lane, gated: d.gated, reason: d.reason });
      else process.stdout.write(`${d.lane}\n`);
      return;
    }
    let lane: Lane;
    if (opts.lane) {
      if (opts.lane !== "ask" && opts.lane !== "agent") fail("--lane must be ask or agent");
      lane = opts.lane;
      if (opts.events) emit({ type: "lane", lane, forced: true });
      else note(`lane: ${lane} (forced)`);
    } else {
      const d = await gate(cfg, text);
      lane = d.lane;
      if (opts.events) emit({ type: "lane", lane, gated: d.gated, reason: d.reason });
      else note(`lane: ${lane}${d.gated ? "" : " (fallback)"} — ${d.reason}`);
    }
    if (lane === "ask") await runAsk(cfg, text, opts);
    else await runAgent(cfg, text, opts);
  } catch (e) {
    propagate(e);
  }
});

withRunOptions(
  withCommonOptions(
    program
      .command("voice")
      .description("push-to-talk stand-in: record the microphone, transcribe through the backend, then route like `do`")
      .option("--seconds <n>", "how long to record", "5")
      .option("--file <path>", "transcribe an existing audio file instead of recording")
      .option("--device <index>", "AVFoundation audio device index", "0")
      .option("--language <code>", "ISO language hint for transcription")
      .option("--lane <lane>", "force a lane: ask | agent")
      .option("--transcribe-only", "print the transcript and stop"),
  ),
).action(async (opts) => {
  const cfg = configFrom(opts);
  try {
    let file: string = opts.file;
    if (!file) {
      note(`recording ${opts.seconds}s from microphone (device ${opts.device})…`);
      file = recordAudio({ seconds: Number(opts.seconds), device: opts.device, ffmpegBin: cfg.ffmpegBin });
      note(`recorded ${file}`);
    }
    const text = await transcribe(cfg, file, { language: opts.language });
    if (!text) fail("transcription came back empty");
    note(`you said: ${text}`);
    if (opts.transcribeOnly) return void process.stdout.write(text + "\n");
    let lane: Lane;
    if (opts.lane) {
      if (opts.lane !== "ask" && opts.lane !== "agent") fail("--lane must be ask or agent");
      lane = opts.lane;
    } else {
      const d = await gate(cfg, text);
      lane = d.lane;
      note(`lane: ${lane}${d.gated ? "" : " (fallback)"} — ${d.reason}`);
    }
    if (lane === "ask") await runAsk(cfg, text, opts);
    else await runAgent(cfg, text, opts);
  } catch (e) {
    propagate(e);
  }
});

program
  .command("talk")
  .description("always-on voice conversation via OpenAI Realtime (mic → speech in/out); work is handed to a Codex thread")
  .option("--backend-url <url>", "OpenClicky backend URL (env BACKEND_URL)")
  .option("--token <token>", "Supabase JWT or session token (env OPENCLICKY_TOKEN)")
  .option("--voice <name>", "Realtime voice (e.g. marin, cedar, alloy)")
  .option("--device <index>", "AVFoundation audio device index", "0")
  .option("--cwd <dir>", "working directory for agent tasks")
  .option("--model <model>", "Codex model override for agent tasks")
  .option("--seconds <n>", "hang up after N seconds (default: until Ctrl-C)")
  .option("--no-agent", "answer only; never hand work to the agent")
  .option("--full-duplex", "keep the mic open while OpenClicky speaks (use with a headset; enables barge-in)")
  .option("--verbose")
  .action(async (opts) => {
    const cfg = configFrom(opts);
    let agent: CodexAgent | undefined;
    let threadId: string | undefined;
    const session = new RealtimeSession({
      cfg,
      voice: opts.voice,
      fullDuplex: Boolean(opts.fullDuplex),
      micCommand: defaultMicCommand(cfg.ffmpegBin, opts.device),
      onEvent: note,
      onTranscript: (role, text) => process.stdout.write(`${role === "user" ? "you" : "openclicky"}: ${text}\n`),
      onAgentTask: opts.agent === false
        ? undefined
        : async (task) => {
            if (!agent) {
              agent = new CodexAgent(cfg, { onEvent: note });
              await agent.start();
            }
            const r = await agent.run(task, { threadId });
            threadId = r.threadId;
            const files = r.artifacts.length ? ` Files: ${r.artifacts.map((a) => path.basename(a)).join(", ")}.` : "";
            return `${r.status === "completed" ? "Done." : `Turn ${r.status}.`} ${r.finalMessage.slice(0, 600)}${files}`;
          },
    });
    const shutdown = async () => {
      session.stop();
      await agent?.stop();
      process.exit(0);
    };
    process.on("SIGINT", () => void shutdown());
    try {
      await session.start();
      note("talking — speak now, Ctrl-C to hang up");
      if (opts.seconds) setTimeout(() => void shutdown(), Number(opts.seconds) * 1000);
      await session.waitForClose();
      await agent?.stop();
    } catch (e) {
      await agent?.stop();
      propagate(e);
    }
  });

const threads = program.command("threads").description("inspect OpenClicky's Codex threads (isolated CODEX_HOME)");
const withThreadAgent = async <T>(opts: Record<string, any>, fn: (agent: CodexAgent) => Promise<T>): Promise<T> => {
  const agent = new CodexAgent(configFrom(opts));
  try {
    await agent.start();
    return await fn(agent);
  } finally {
    await agent.stop();
  }
};
const fmtTime = (s?: number) => (s ? new Date(s * 1000).toISOString().replace("T", " ").slice(0, 19) : "-");

threads
  .command("list")
  .description("list recent threads")
  .option("--limit <n>", "how many", "20")
  .option("--json", "print JSON")
  .option("--verbose")
  .action(async (opts) => {
    try {
      const list = await withThreadAgent(opts, (a) => a.listThreads(Number(opts.limit)));
      if (opts.json) return void process.stdout.write(JSON.stringify(list, null, 2) + "\n");
      if (!list.length) return void process.stdout.write("no threads yet\n");
      for (const t of list) process.stdout.write(`${t.id}  ${fmtTime(t.updatedAt)}  ${t.status.padEnd(9)}  ${t.cwd}\n    ${t.preview.slice(0, 100)}\n`);
    } catch (e) {
      propagate(e);
    }
  });

threads
  .command("show")
  .description("show a thread's turns")
  .argument("<id>")
  .option("--json", "print JSON")
  .option("--verbose")
  .action(async (id: string, opts) => {
    try {
      const r = await withThreadAgent(opts, (a) => a.readThread(id));
      if (opts.json) return void process.stdout.write(JSON.stringify(r, null, 2) + "\n");
      process.stdout.write(`thread ${r.thread.id}  cwd ${r.thread.cwd}  updated ${fmtTime(r.thread.updatedAt)}\n`);
      for (const t of r.turns) {
        process.stdout.write(`\n— turn ${t.id} (${t.status}, ${fmtTime(t.startedAt)})\n`);
        for (const u of t.user) process.stdout.write(`  you: ${u}\n`);
        for (const c of t.commands) process.stdout.write(`  ran: ${c}\n`);
        for (const a of t.agent) process.stdout.write(`  agent: ${a}\n`);
      }
    } catch (e) {
      propagate(e);
    }
  });

threads
  .command("archive")
  .description("archive a thread")
  .argument("<id>")
  .option("--verbose")
  .action(async (id: string, opts) => {
    try {
      await withThreadAgent(opts, (a) => a.archiveThread(id));
      process.stdout.write(`archived ${id}\n`);
    } catch (e) {
      propagate(e);
    }
  });

// Account integrations (Composio through Codex's MCP OAuth). The macOS app's "Connect <app>" card
// calls these: `login` once per machine, then the agent's composio tools can connect single apps.
const integrations = program.command("integrations").description("account integrations (Composio MCP through Codex OAuth)");

/** `codex mcp <args>` against OpenClicky's CODEX_HOME; the config is (re)rendered first. */
async function codexMcp(cfg: AgentConfig, args: string[], onLine?: (line: string) => void): Promise<{ code: number; output: string }> {
  ensureCodexHome(cfg);
  const { spawn } = await import("node:child_process");
  return new Promise((resolve, reject) => {
    // Same allowlist as the app-server child: `codex mcp` talks to third-party MCP servers, so it
    // has no business inheriting every credential in the user's shell either.
    const child = spawn(cfg.codexBin, ["mcp", ...args], { env: buildChildEnv(cfg), stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    let pending = "";
    const consume = (chunk: Buffer) => {
      output += chunk.toString();
      pending += chunk.toString();
      const lines = pending.split("\n");
      pending = lines.pop() ?? "";
      for (const line of lines) onLine?.(line);
    };
    child.stdout.on("data", consume);
    child.stderr.on("data", consume);
    child.once("error", (e) => reject(new Error(`failed to start ${cfg.codexBin}: ${e.message}`)));
    child.once("close", (code) => {
      if (pending) onLine?.(pending);
      resolve({ code: code ?? 1, output });
    });
  });
}

withCommonOptions(integrations.command("status").description("MCP servers and whether Composio is logged in")).action(
  async (opts) => {
    try {
      const cfg = configFrom(opts);
      const { code, output } = await codexMcp(cfg, ["list", "--json"]);
      if (code !== 0) fail(output.trim() || `codex mcp list exited ${code}`);
      const servers = JSON.parse(output) as Array<Record<string, any>>;
      const composio = servers.find((s) => s.name === "composio");
      const status = {
        configured: Boolean(cfg.composioMcpUrl),
        composio: composio ?? null,
        // Codex reports "OAuth" once `codex mcp login composio` has stored a token; a consumer key needs no login.
        loggedIn: Boolean(cfg.composioApiKey) || /o_?auth/i.test(String(composio?.auth_status ?? composio?.authStatus ?? "")),
      };
      if (opts.json) return void process.stdout.write(JSON.stringify(status, null, 2) + "\n");
      process.stdout.write(
        !status.configured ? "Composio: not configured (set COMPOSIO_MCP_URL)\n" : status.loggedIn ? "Composio: logged in\n" : "Composio: configured, not logged in (run `openclicky integrations login`)\n",
      );
    } catch (e) {
      propagate(e);
    }
  },
);

withCommonOptions(
  integrations
    .command("login")
    .description("log Codex into the Composio MCP server (opens Composio's authorization page)")
    .argument("[server]", "MCP server name", "composio")
    .option("--no-open", "print the authorization URL instead of opening the browser"),
).action(async (server: string, opts) => {
  try {
    const cfg = configFrom(opts);
    if (server === "composio" && !cfg.composioMcpUrl) fail("Composio is not configured: set COMPOSIO_MCP_URL (composioMcpUrl in shell.json)");
    const { code, output } = await codexMcp(cfg, ["login", server], (line) => {
      const url = line.match(/https?:\/\/\S+/)?.[0];
      if (url && opts.open !== false) {
        process.stdout.write(`opening ${url}\n`);
        // Codex only prints the URL; open it so the user lands on Composio's page.
        void import("node:child_process").then(({ spawn }) => spawn("open", [url], { stdio: "ignore" }).unref());
      } else if (line.trim()) {
        process.stdout.write(line + "\n");
      }
    });
    if (code !== 0) fail(output.trim() || `codex mcp login exited ${code}`);
  } catch (e) {
    propagate(e);
  }
});

const skills = program.command("skills").description("the user's skill library (~/.openclicky/skills): activated skills apply to talk and agent runs");

skills
  .command("list")
  .description("list library skills and whether they are active")
  .option("--json", "print JSON")
  .action((opts) => {
    const list = listLibrary(resolveConfig().userSkillsDir);
    if (opts.json) return void process.stdout.write(JSON.stringify(list, null, 2) + "\n");
    if (!list.length) return void process.stdout.write("no skills yet — try: openclicky skills create \"reply to emails in my voice\"\n");
    for (const s of list) process.stdout.write(`${s.active ? "[on] " : "[off]"} ${s.id.padEnd(28)} ${s.name} — ${s.description.slice(0, 90)}\n`);
  });

const toggleSkill = (id: string, on: boolean) => {
  const dir = resolveConfig().userSkillsDir;
  if (!listLibrary(dir).some((s) => s.id === id)) fail(`no skill "${id}" in ${dir}/library`);
  setActive(dir, id, on);
  process.stdout.write(`${on ? "activated" : "deactivated"} ${id}\n`);
};
skills.command("activate").description("activate a skill").argument("<id>").action((id: string) => toggleSkill(id, true));
skills.command("deactivate").description("deactivate a skill").argument("<id>").action((id: string) => toggleSkill(id, false));
skills.command("path").description("print the library directory").action(() => void process.stdout.write(resolveConfig().userSkillsDir + "\n"));

skills
  .command("create")
  .description("draft a SKILL.md from a one-line request via the backend, save and activate it")
  .argument("<request>", "what the skill should do")
  .option("--capability <name...>", "capabilities the skill may rely on (e.g. composio, computer-use)")
  .option("--backend-url <url>", "env BACKEND_URL")
  .option("--token <token>", "env OPENCLICKY_TOKEN")
  .action(async (request: string, opts) => {
    const cfg = configFrom(opts);
    if (!cfg.token) fail("missing token: set OPENCLICKY_TOKEN or pass --token");
    try {
      const r = await fetch(`${cfg.backendUrl}/skills/create`, {
        method: "POST",
        headers: backendHeaders(cfg, { "content-type": "application/json" }),
        body: JSON.stringify({ request, capabilities: opts.capability ?? [] }),
      });
      if (!r.ok) fail(`skill creation failed (${r.status}): ${(await r.text()).slice(0, 300)}`);
      const { markdown } = (await r.json()) as { markdown: string };
      const skill = createSkillFiles(cfg.userSkillsDir, markdown);
      note(`saved ${skill.path}/SKILL.md (active)`);
      process.stdout.write(skill.id + "\n");
    } catch (e) {
      propagate(e);
    }
  });

program
  .command("token")
  .description("sign in to Supabase with email/password and exchange the JWT for an OpenClicky session token")
  .requiredOption("--email <email>")
  .option("--password <password>", "insecure: visible in shell history and `ps`; prefer env OPENCLICKY_PASSWORD or the interactive prompt")
  .option("--supabase-url <url>", "env SUPABASE_URL")
  .option("--anon-key <key>", "env SUPABASE_ANON_KEY")
  .option("--backend-url <url>", "env BACKEND_URL")
  .option("--jwt-only", "print the Supabase JWT instead of exchanging it")
  .action(async (opts) => {
    const cfg = resolveConfig({ backendUrl: opts.backendUrl });
    let supabaseUrl = (opts.supabaseUrl ?? process.env.SUPABASE_URL ?? "").replace(/\/+$/, "");
    let anonKey = opts.anonKey ?? process.env.SUPABASE_ANON_KEY;
    try {
      const password = await resolvePassword(opts);
      if (!supabaseUrl || !anonKey) {
        // The backend publishes its sign-in details, so only the backend URL is needed.
        const configRes = await fetch(`${cfg.backendUrl}/auth/config`);
        if (!configRes.ok) fail(`SUPABASE_URL and SUPABASE_ANON_KEY are required (flags or env), and ${cfg.backendUrl}/auth/config is not available (${configRes.status})`);
        const authConfig = (await configRes.json()) as { supabaseUrl: string; publishableKey: string };
        supabaseUrl = supabaseUrl || authConfig.supabaseUrl;
        anonKey = anonKey || authConfig.publishableKey;
      }
      const signIn = await fetch(`${supabaseUrl}/auth/v1/token?grant_type=password`, {
        method: "POST",
        headers: { "content-type": "application/json", apikey: anonKey },
        body: JSON.stringify({ email: opts.email, password }),
      });
      if (!signIn.ok) fail(`supabase sign-in failed (${signIn.status}): ${(await signIn.text()).slice(0, 300)}`);
      const { access_token } = (await signIn.json()) as { access_token: string };
      if (opts.jwtOnly) return void process.stdout.write(access_token + "\n");
      const ex = await fetch(`${cfg.backendUrl}/agent/session-token`, { method: "POST", headers: { authorization: `Bearer ${access_token}` } });
      if (!ex.ok) fail(`session-token exchange failed (${ex.status}): ${(await ex.text()).slice(0, 300)}`);
      const { token, expiresAt } = (await ex.json()) as { token: string; expiresAt: number };
      process.stderr.write(`session token expires at ${new Date(expiresAt * 1000).toISOString()}\n`);
      process.stdout.write(token + "\n");
    } catch (e) {
      propagate(e);
    }
  });

// The only place that actually exits: reached only after every action's own try/finally (which
// stops any Codex child it started) has already run its course while the rejection unwound here.
program.parseAsync(process.argv).catch((e) => {
  if (!(e instanceof CliFailure)) reportFailure((e as Error).message);
  process.exit(1);
});
