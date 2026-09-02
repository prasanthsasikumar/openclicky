import { spawn, type ChildProcess } from "node:child_process";
import type { AgentConfig } from "./config.js";

/**
 * The always-on / push-to-talk voice loop (HeyClicky's RealtimeVoiceClient):
 *   backend mints an ephemeral client secret → WebSocket to OpenAI Realtime →
 *   mic PCM16 in, spoken PCM16 out, server VAD for turns, barge-in flushes playback,
 *   and a `send_to_agent` tool that hands real work to a Codex thread.
 *
 * Audio I/O is delegated to ffmpeg (capture) and ffplay (playback) so this stays dependency-free.
 */

export const TALK_INSTRUCTIONS = `You are OpenClicky, a friendly, fast macOS voice assistant. Keep spoken replies short (one or two sentences).
Answer quick questions yourself. For anything that requires doing work on the computer — creating or editing files or code,
running commands, using apps or integrations, research with sources, multi-step tasks — call the send_to_agent tool with a clear,
self-contained task description, then tell the user in one sentence what happened. Never pretend work was done without the tool.
If the user is just chatting, chat. Do not read file paths aloud character by character; summarize them.`;

export const SEND_TO_AGENT_TOOL = {
  type: "function",
  name: "send_to_agent",
  description: "Hand a task that requires doing work (files, code, commands, apps, research) to the OpenClicky agent. Returns a short result summary.",
  parameters: {
    type: "object",
    properties: { task: { type: "string", description: "A clear, self-contained description of what to do." } },
    required: ["task"],
  },
};

export interface RealtimeSessionOptions {
  cfg: AgentConfig;
  /** WebSocket base URL (default wss://api.openai.com/v1/realtime). Tests point this at a fake. */
  realtimeUrl?: string;
  voice?: string;
  instructions?: string;
  /** Command producing raw PCM16 mono 24 kHz on stdout (default: ffmpeg avfoundation). */
  micCommand?: string[];
  /** Command consuming raw PCM16 mono 24 kHz on stdin (default: ffplay). */
  playerCommand?: string[];
  /** Bytes per input_audio_buffer.append (default 4800 = 100 ms). */
  frameBytes?: number;
  onTranscript?: (role: "user" | "assistant", text: string) => void;
  onEvent?: (line: string) => void;
  /** Runs the agent for a send_to_agent tool call and returns the summary spoken back. */
  onAgentTask?: (task: string) => Promise<string>;
  fetchImpl?: typeof fetch;
}

export function defaultMicCommand(ffmpegBin = "ffmpeg", device = "0"): string[] {
  return [ffmpegBin, "-hide_banner", "-loglevel", "error", "-f", "avfoundation", "-i", `:${device}`, "-ac", "1", "-ar", "24000", "-f", "s16le", "-"];
}

export function defaultPlayerCommand(ffplayBin = "ffplay"): string[] {
  return [ffplayBin, "-hide_banner", "-loglevel", "quiet", "-nodisp", "-autoexit", "-f", "s16le", "-ar", "24000", "-ch_layout", "mono", "-i", "-"];
}

/** Session config sent as `session.update` (GA Realtime API shape). */
export function sessionUpdate(opts: { voice?: string; instructions?: string }) {
  return {
    type: "session.update",
    session: {
      type: "realtime",
      instructions: opts.instructions ?? TALK_INSTRUCTIONS,
      tools: [SEND_TO_AGENT_TOOL],
      tool_choice: "auto",
      audio: {
        input: {
          format: { type: "audio/pcm", rate: 24000 },
          transcription: { model: "gpt-4o-mini-transcribe" },
          turn_detection: { type: "server_vad", silence_duration_ms: 600, create_response: true, interrupt_response: true },
        },
        output: { format: { type: "audio/pcm", rate: 24000 }, ...(opts.voice ? { voice: opts.voice } : {}) },
      },
    },
  };
}

export class RealtimeSession {
  private ws?: WebSocket;
  private mic?: ChildProcess;
  private player?: ChildProcess;
  private assistantBuffer = "";
  private closed = false;
  private closeWaiters: Array<() => void> = [];

  constructor(private opts: RealtimeSessionOptions) {}

  private log(line: string) {
    this.opts.onEvent?.(line);
  }

  /** Mint the ephemeral secret via the backend, connect, configure the session, start the mic. */
  async start(): Promise<void> {
    const { cfg } = this.opts;
    if (!cfg.token) throw new Error("missing token: set OPENCLICKY_TOKEN or pass --token");
    const f = this.opts.fetchImpl ?? fetch;
    const res = await f(`${cfg.backendUrl}/agent/realtime/session`, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bearer ${cfg.token}` },
      body: JSON.stringify({ voice: this.opts.voice, instructions: this.opts.instructions ?? TALK_INSTRUCTIONS }),
    });
    if (!res.ok) throw new Error(`backend ${res.status}: ${(await res.text()).slice(0, 300)}`);
    const secret = (await res.json()) as { value?: string; session?: { model?: string } };
    if (!secret.value) throw new Error("backend returned no client secret");
    const model = secret.session?.model ?? "gpt-realtime";
    const base = (this.opts.realtimeUrl ?? "wss://api.openai.com/v1/realtime").replace(/\/+$/, "");
    const url = `${base}?model=${encodeURIComponent(model)}`;
    this.log(`connecting ${url}`);

    // Browser-style auth: the ephemeral key travels in the subprotocol list (Node's WebSocket has no custom headers).
    const ws = new WebSocket(url, ["realtime", `openai-insecure-api-key.${secret.value}`, "openai-beta.realtime-v1"]);
    this.ws = ws;
    await new Promise<void>((resolve, reject) => {
      ws.addEventListener("open", () => resolve(), { once: true });
      ws.addEventListener("error", () => reject(new Error(`websocket connect failed: ${url}`)), { once: true });
    });
    ws.addEventListener("message", (ev) => void this.onServerEvent(String(ev.data)));
    ws.addEventListener("close", () => this.finish("websocket closed"));
    ws.addEventListener("error", () => this.finish("websocket error"));
    ws.send(JSON.stringify(sessionUpdate({ voice: this.opts.voice, instructions: this.opts.instructions })));
    this.startMic();
  }

  /** Resolves when the session ends (stop() or the socket closes). */
  waitForClose(): Promise<void> {
    if (this.closed) return Promise.resolve();
    return new Promise((r) => this.closeWaiters.push(r));
  }

  stop(): void {
    this.finish("stopped");
  }

  private finish(reason: string) {
    if (this.closed) return;
    this.closed = true;
    this.log(reason);
    this.mic?.kill();
    this.stopPlayer();
    try {
      this.ws?.close();
    } catch {
      /* ignore */
    }
    for (const w of this.closeWaiters) w();
    this.closeWaiters = [];
  }

  private startMic() {
    const cmd = this.opts.micCommand ?? defaultMicCommand(this.opts.cfg.ffmpegBin);
    const frame = this.opts.frameBytes ?? 4800;
    const mic = spawn(cmd[0], cmd.slice(1), { stdio: ["ignore", "pipe", "pipe"] });
    this.mic = mic;
    let pending = Buffer.alloc(0);
    mic.stdout!.on("data", (chunk: Buffer) => {
      pending = Buffer.concat([pending, chunk]);
      while (pending.length >= frame) {
        const f = pending.subarray(0, frame);
        pending = pending.subarray(frame);
        this.send({ type: "input_audio_buffer.append", audio: f.toString("base64") });
      }
    });
    mic.stderr!.on("data", (d: Buffer) => this.log(`mic: ${String(d).trim()}`));
    mic.on("exit", (code) => {
      if (!this.closed) this.log(`mic exited (code ${code}) — check Microphone permission for your terminal`);
    });
  }

  private ensurePlayer(): ChildProcess {
    if (this.player && this.player.exitCode === null) return this.player;
    const cmd = this.opts.playerCommand ?? defaultPlayerCommand();
    const p = spawn(cmd[0], cmd.slice(1), { stdio: ["pipe", "ignore", "pipe"] });
    p.stdin!.on("error", () => {});
    p.stderr!.on("data", (d: Buffer) => this.log(`player: ${String(d).trim()}`));
    this.player = p;
    return p;
  }

  /** Barge-in: drop queued audio by killing the player; the next delta respawns it. */
  private stopPlayer() {
    if (this.player && this.player.exitCode === null) {
      this.player.stdin?.end();
      this.player.kill();
    }
    this.player = undefined;
  }

  private send(msg: unknown) {
    if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(msg));
  }

  private async onServerEvent(raw: string) {
    let ev: any;
    try {
      ev = JSON.parse(raw);
    } catch {
      return;
    }
    switch (ev.type) {
      case "session.created":
      case "session.updated":
        this.log(ev.type);
        break;
      case "input_audio_buffer.speech_started":
        this.stopPlayer(); // barge-in
        this.log("listening…");
        break;
      case "conversation.item.input_audio_transcription.completed":
        if (ev.transcript) this.opts.onTranscript?.("user", String(ev.transcript).trim());
        break;
      case "response.output_audio_transcript.delta":
      case "response.audio_transcript.delta":
        this.assistantBuffer += ev.delta ?? "";
        break;
      case "response.output_audio_transcript.done":
      case "response.audio_transcript.done": {
        const text = (ev.transcript ?? this.assistantBuffer).trim();
        this.assistantBuffer = "";
        if (text) this.opts.onTranscript?.("assistant", text);
        break;
      }
      case "response.output_audio.delta":
      case "response.audio.delta":
        if (ev.delta) this.ensurePlayer().stdin!.write(Buffer.from(ev.delta, "base64"));
        break;
      case "response.function_call_arguments.done":
        await this.onToolCall(ev);
        break;
      case "response.done":
        if (this.assistantBuffer.trim()) {
          this.opts.onTranscript?.("assistant", this.assistantBuffer.trim());
          this.assistantBuffer = "";
        }
        break;
      case "error":
        this.log(`server error: ${ev.error?.message ?? JSON.stringify(ev.error ?? ev)}`);
        break;
    }
  }

  private async onToolCall(ev: any) {
    let output = "";
    if (ev.name === "send_to_agent") {
      let task = "";
      try {
        task = String(JSON.parse(ev.arguments ?? "{}").task ?? "");
      } catch {
        task = String(ev.arguments ?? "");
      }
      this.log(`agent task: ${task}`);
      try {
        output = this.opts.onAgentTask ? await this.opts.onAgentTask(task) : "agent lane is disabled in this session";
      } catch (e) {
        output = `agent failed: ${(e as Error).message}`;
      }
    } else {
      output = `unknown tool ${ev.name}`;
    }
    this.send({ type: "conversation.item.create", item: { type: "function_call_output", call_id: ev.call_id, output: output.slice(0, 4000) } });
    this.send({ type: "response.create" });
  }
}
