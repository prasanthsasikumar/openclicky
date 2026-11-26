#!/usr/bin/env node
// A fake `opencode acp` for testing OpenCodeAgent (and the CLI built on it) without a real OpenCode
// binary. Speaks the Agent Client Protocol (newline-delimited JSON-RPC over stdio): initialize,
// initialized, session/new, session/load, session/prompt. Scenario from `${OC_FAKE_HOME}/fake-scenario.txt`.
import readline from "node:readline";
import fs from "node:fs";
import path from "node:path";

const home = process.cwd();
let scenario = "complete";
try { scenario = fs.readFileSync(path.join(home, "fake-scenario.txt"), "utf8").trim() || "complete"; } catch { /* default */ }
if (home) fs.writeFileSync(path.join(home, "fake-opencode.pid"), String(process.pid));

const write = (msg) => process.stdout.write(JSON.stringify(msg) + "\n");
const sessionId = "fake-session-1";

setInterval(() => {}, 1 << 30); // stay alive; only a signal stops us

const rl = readline.createInterface({ input: process.stdin });
rl.on("line", (line) => {
  let msg; try { msg = JSON.parse(line); } catch { return; }
  const { id, method, params } = msg;
  switch (method) {
    case "initialize": write({ jsonrpc: "2.0", id, result: { protocolVersion: 1, agentCapabilities: {} } }); break;
    case "initialized": break;
    case "session/new": write({ jsonrpc: "2.0", id, result: { sessionId } }); break;
    case "session/load": write({ jsonrpc: "2.0", id, result: {} }); break;
    case "session/set_config_option": write({ jsonrpc: "2.0", id, result: {} }); break;
    case "session/prompt": {
      if (scenario === "fail-turn") { write({ jsonrpc: "2.0", id, result: { stopReason: "cancelled", error: { message: "boom" } } }); return; }
      write({ jsonrpc: "2.0", method: "session/update", params: { sessionId: params?.sessionId ?? sessionId, update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "ok" } } } });
      write({ jsonrpc: "2.0", id, result: { stopReason: "end_turn" } });
      break;
    }
    default: if (id !== undefined) write({ jsonrpc: "2.0", id, result: {} });
  }
});
