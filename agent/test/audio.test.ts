import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { ffmpegRecordArgs, recordAudio, transcribe, parseDshowAudioDevices, resolveMicDevice } from "../src/audio.js";
import { resolveConfig } from "../src/config.js";

describe("recordAudio", () => {
  it("builds avfoundation capture args", () => {
    const a = ffmpegRecordArgs({ seconds: 4, outPath: "/t/r.wav", platform: "darwin" });
    expect(a).toContain("avfoundation");
    expect(a.slice(a.indexOf("-i") + 1)[0]).toBe(":0");
    expect(a.slice(a.indexOf("-t") + 1)[0]).toBe("4");
    expect(a.at(-1)).toBe("/t/r.wav");
    expect(ffmpegRecordArgs({ seconds: 1, outPath: "x", device: "2", platform: "darwin" })).toContain(":2");
  });
  it("builds DirectShow capture args on Windows and reads the device list", () => {
    const a = ffmpegRecordArgs({ seconds: 4, outPath: "C:\\t\\r.wav", device: "Microphone (Realtek Audio)", platform: "win32" });
    expect(a.join(" ")).toContain("-f dshow -i audio=Microphone (Realtek Audio) -t 4");
    const listing = [
      '[dshow @ 000001] "Integrated Camera" (video)',
      '[dshow @ 000001]   Alternative name "@device_pnp_\\\\?\\usb#vid"',
      '[dshow @ 000001] "Microphone Array (Realtek(R) Audio)" (audio)',
      '[dshow @ 000001] "Headset Microphone (USB)" (audio)',
    ].join("\n");
    expect(parseDshowAudioDevices(listing)).toEqual(["Microphone Array (Realtek(R) Audio)", "Headset Microphone (USB)"]);
    // A name is used as given; other platforms keep their index.
    expect(resolveMicDevice("ffmpeg", "Headset Microphone (USB)", "win32")).toBe("Headset Microphone (USB)");
    expect(resolveMicDevice("ffmpeg", "1", "darwin")).toBe("1");
  });
  it("runs a fake ffmpeg and validates output", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "oc-aud-"));
    const bin = path.join(dir, "fakeffmpeg.sh");
    fs.writeFileSync(bin, '#!/bin/sh\nfor last; do :; done; printf RIFF > "$last"\n', { mode: 0o755 });
    const out = path.join(dir, "r.wav");
    expect(recordAudio({ seconds: 1, outPath: out, ffmpegBin: bin })).toBe(out);
    const bad = path.join(dir, "bad.sh");
    fs.writeFileSync(bad, "#!/bin/sh\necho 'Input/output error' >&2; exit 1\n", { mode: 0o755 });
    expect(() => recordAudio({ seconds: 1, outPath: path.join(dir, "n.wav"), ffmpegBin: bad })).toThrow(/Microphone permission/);
  });
});

describe("transcribe", () => {
  let server: http.Server;
  let url: string;
  const seen: any[] = [];
  beforeAll(async () => {
    server = http.createServer((req, res) => {
      let b = "";
      req.on("data", (d) => (b += d));
      req.on("end", () => {
        seen.push({ url: req.url, auth: req.headers.authorization, body: JSON.parse(b) });
        res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ text: "  create a file called hello.txt \n" }));
      });
    });
    await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
    url = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
  });
  afterAll(() => server.close());

  it("posts base64 audio to the backend and returns trimmed text", async () => {
    const wav = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "oc-aud-")), "a.wav");
    fs.writeFileSync(wav, "RIFFdata");
    const text = await transcribe(resolveConfig({ backendUrl: url, token: "tok" }), wav, { language: "en" });
    expect(text).toBe("create a file called hello.txt");
    expect(seen.at(-1)).toMatchObject({ url: "/agent/transcribe", auth: "Bearer tok", body: { audio: Buffer.from("RIFFdata").toString("base64"), mime: "audio/wav", language: "en" } });
  });
});
