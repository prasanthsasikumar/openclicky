import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { WebSocketServer, type WebSocket as WsSocket } from "ws";
import { RealtimeSession, sessionUpdate, defaultMicCommand, defaultPlayerCommand } from "../src/realtime.js";
import { resolveConfig } from "../src/config.js";

// Fake backend (mints the secret) + fake Realtime server that scripts one voice turn with a tool call.
let backend: http.Server;
let backendUrl: string;
let wss: WebSocketServer;
let wsUrl: string;
const backendSeen: any[] = [];
const serverSeen: any[] = [];
let protocolsSeen: string[] = [];

beforeAll(async () => {
  backend = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      backendSeen.push({ url: req.url, auth: req.headers.authorization, body: JSON.parse(b) });
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ value: "ek_test_123", expires_at: 1, session: { type: "realtime", model: "gpt-realtime-test" } }));
    });
  });
  await new Promise<void>((r) => backend.listen(0, "127.0.0.1", r));
  backendUrl = `http://127.0.0.1:${(backend.address() as { port: number }).port}`;

  wss = new WebSocketServer({ port: 0, host: "127.0.0.1", handleProtocols: (protocols) => (protocolsSeen = [...protocols], "realtime") });
  await new Promise<void>((r) => wss.once("listening", () => r()));
  wsUrl = `ws://127.0.0.1:${(wss.address() as { port: number }).port}`;
  wss.on("connection", (ws: WsSocket, req) => {
    serverSeen.push({ url: req.url });
    const send = (o: unknown) => ws.send(JSON.stringify(o));
    send({ type: "session.created", session: {} });
    let appends = 0;
    let turnStarted = false;
    ws.on("message", (raw) => {
      const msg = JSON.parse(String(raw));
      serverSeen.push(msg);
      if (msg.type === "session.update") send({ type: "session.updated", session: msg.session });
      if (msg.type === "input_audio_buffer.append" && !turnStarted && ++appends >= 3) {
        turnStarted = true;
        send({ type: "input_audio_buffer.speech_started" });
        send({ type: "conversation.item.input_audio_transcription.completed", transcript: "create a file called realtime.txt containing 'voice' " });
        send({ type: "response.function_call_arguments.done", name: "send_to_agent", call_id: "call_1", arguments: JSON.stringify({ task: "create a file called realtime.txt containing 'voice'" }) });
      }
      if (msg.type === "response.create") {
        send({ type: "response.output_audio_transcript.delta", delta: "Done, " });
        send({ type: "response.output_audio_transcript.delta", delta: "I created realtime.txt." });
        send({ type: "response.output_audio.delta", delta: Buffer.alloc(2400).toString("base64") });
        send({ type: "response.output_audio_transcript.done", transcript: "Done, I created realtime.txt." });
        send({ type: "response.done" });
        setTimeout(() => ws.close(), 200);
      }
    });
  });
});
afterAll(() => {
  backend.close();
  wss.close();
});

describe("realtime", () => {
  it("builds ffmpeg/ffplay commands and the session.update payload", () => {
    expect(defaultMicCommand("ffmpeg", "1").join(" ")).toContain("-f avfoundation -i :1 -ac 1 -ar 24000 -f s16le -");
    expect(defaultPlayerCommand().join(" ")).toContain("-f s16le -ar 24000 -ch_layout mono -i -");
    const u = sessionUpdate({ voice: "marin" }) as any;
    expect(u.session.tools[0].name).toBe("send_to_agent");
    expect(u.session.audio.output.voice).toBe("marin");
    expect(u.session.audio.input.turn_detection.type).toBe("server_vad");
  });

  it("runs a full voice turn: secret → ws → mic → tool call → agent → spoken reply → playback", async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-rt-"));
    const playedFile = path.join(dir, "played.bin");
    const transcripts: string[] = [];
    const events: string[] = [];
    const tasks: string[] = [];
    const cfg = resolveConfig({ backendUrl, token: "tok" });
    const session = new RealtimeSession({
      cfg,
      realtimeUrl: wsUrl,
      voice: "marin",
      greet: false,
      fullDuplex: true,
      // fake mic: 4800-byte frames of silence every 30 ms; fake player: append stdin to a file
      micCommand: ["node", "-e", "setInterval(()=>process.stdout.write(Buffer.alloc(4800)),30)"],
      playerCommand: ["node", "-e", `process.stdin.on("data",d=>require("fs").appendFileSync(${JSON.stringify(playedFile)},d))`],
      onTranscript: (role, text) => transcripts.push(`${role}: ${text}`),
      onEvent: (l) => events.push(l),
      onAgentTask: async (task) => (tasks.push(task), "created realtime.txt"),
    });
    await session.start();
    await Promise.race([session.waitForClose(), new Promise((_, rej) => setTimeout(() => rej(new Error("timeout")), 10_000))]);

    expect(backendSeen[0]).toMatchObject({ url: "/agent/realtime/session", auth: "Bearer tok", body: { voice: "marin" } });
    expect(serverSeen[0].url).toBe("/?model=gpt-realtime-test");
    expect(protocolsSeen).toContain("openai-insecure-api-key.ek_test_123");
    expect(serverSeen.find((m) => m.type === "session.update").session.tools[0].name).toBe("send_to_agent");
    expect(serverSeen.filter((m) => m.type === "input_audio_buffer.append").length).toBeGreaterThanOrEqual(3);
    expect(tasks).toEqual(["create a file called realtime.txt containing 'voice'"]);
    const out = serverSeen.find((m) => m.type === "conversation.item.create");
    expect(out.item).toMatchObject({ type: "function_call_output", call_id: "call_1", output: "created realtime.txt" });
    expect(serverSeen.some((m) => m.type === "response.create")).toBe(true);
    expect(transcripts).toEqual(["user: create a file called realtime.txt containing 'voice'", "assistant: Done, I created realtime.txt."]);
    await new Promise((r) => setTimeout(r, 200));
    expect(fs.existsSync(playedFile) && fs.statSync(playedFile).size).toBe(2400);
    expect(events.some((e) => e === "listening…")).toBe(true);
  }, 20_000);

  it("fails clearly without a token or when the backend refuses", async () => {
    await expect(new RealtimeSession({ cfg: resolveConfig({ backendUrl, token: undefined }, {} as NodeJS.ProcessEnv) }).start()).rejects.toThrow(/missing token/);
  });
});
