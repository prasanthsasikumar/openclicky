#!/usr/bin/env node
// A fake `codex app-server --stdio` for testing CodexAgent (and the CLI built on it) without a real
// Codex binary installed. Speaks the same newline-delimited JSON-RPC protocol as jsonrpc.ts and
// implements just enough of the app-server surface (initialize, thread/start, thread/resume,
// turn/start) to drive the scenarios below.
//
// Codex's own child environment is now an explicit allowlist (see codex.ts buildChildEnv), so this
// script can't just read an arbitrary env var a test sets — it takes its scenario from a file next
// to config.toml instead: `${CODEX_HOME}/fake-scenario.txt` (CODEX_HOME is always set explicitly).
// It also always writes its own pid to `${CODEX_HOME}/fake-codex.pid`, so a test that only has the
// CLI's own (separate) process tree can still check whether this child was actually stopped.
//
//   complete            (default) turn/completed with status "completed" after one agentMessage.
//   hang                turn/start responds, but nothing else is ever sent — exercises run()'s timeout.
//   retry-then-complete an "error" notification with willRetry: true, then an agentMessage, then
//                       turn/completed status "completed" — exercises clearing a stale retried error.
//   fail-turn           turn/completed with status "failed" and a turn-level error, no agentMessage.
import readline from "node:readline";
import fs from "node:fs";
import path from "node:path";

const codexHome = process.env.CODEX_HOME;
let scenario = "complete";
try {
  scenario = fs.readFileSync(path.join(codexHome, "fake-scenario.txt"), "utf8").trim() || "complete";
} catch {
  // No scenario file: default to "complete".
}
if (codexHome) fs.writeFileSync(path.join(codexHome, "fake-codex.pid"), String(process.pid));

const write = (msg) => process.stdout.write(JSON.stringify(msg) + "\n");
const threadId = "fake-thread-1";
const turnId = "fake-turn-1";

function runTurn(tid) {
  switch (scenario) {
    case "hang":
      return; // never emit item/completed or turn/completed.
    case "retry-then-complete":
      write({ jsonrpc: "2.0", method: "error", params: { threadId: tid, error: { message: "transient glitch" }, willRetry: true } });
      write({ jsonrpc: "2.0", method: "item/completed", params: { threadId: tid, item: { type: "agentMessage", text: "done after retry" } } });
      write({ jsonrpc: "2.0", method: "turn/completed", params: { threadId: tid, turn: { id: turnId, status: "completed" } } });
      return;
    case "fail-turn":
      write({ jsonrpc: "2.0", method: "turn/completed", params: { threadId: tid, turn: { id: turnId, status: "failed", error: { message: "boom" } } } });
      return;
    default:
      write({ jsonrpc: "2.0", method: "item/completed", params: { threadId: tid, item: { type: "agentMessage", text: "ok" } } });
      write({ jsonrpc: "2.0", method: "turn/completed", params: { threadId: tid, turn: { id: turnId, status: "completed" } } });
  }
}

// Stay alive after stdin ends (parent called `stdin.end()`, or the parent process itself exited and
// the OS closed its end of the pipe) instead of exiting on EOF like a plain readline script would —
// only an actual signal (the SIGTERM `CodexAgent.stop()` sends) should end this process. Otherwise a
// test couldn't tell "properly stopped" apart from "orphaned, but the pipe closing looked similar".
setInterval(() => {}, 1 << 30);

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return; // stray non-JSON line; ignore like the real protocol reader does.
  }
  const { id, method, params } = msg;
  switch (method) {
    case "initialize":
      write({ jsonrpc: "2.0", id, result: {} });
      break;
    case "thread/start":
    case "thread/resume":
      write({ jsonrpc: "2.0", id, result: { thread: { id: threadId }, model: "fake-model", modelProvider: "openclicky" } });
      break;
    case "turn/start":
      write({ jsonrpc: "2.0", id, result: { turn: { id: turnId } } });
      runTurn(params?.threadId ?? threadId);
      break;
    default:
      write({ jsonrpc: "2.0", id, result: {} });
  }
});
