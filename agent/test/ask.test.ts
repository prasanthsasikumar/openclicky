import { describe, it, expect, beforeAll, afterAll } from "vitest";
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { ask, parseSseStream, imageToDataUrl } from "../src/ask.js";
import { resolveConfig } from "../src/config.js";

let server: http.Server;
let url: string;
const seen: { auth?: string; body: any }[] = [];

beforeAll(async () => {
  server = http.createServer((req, res) => {
    let b = "";
    req.on("data", (d) => (b += d));
    req.on("end", () => {
      const body = JSON.parse(b);
      seen.push({ auth: req.headers.authorization, body });
      if (req.headers.authorization !== "Bearer good") {
        res.writeHead(401, { "content-type": "application/json" }).end(JSON.stringify({ error: "nope" }));
        return;
      }
      res.writeHead(200, { "content-type": "text/event-stream" });
      const chunk = (c: string) => `data: ${JSON.stringify({ choices: [{ delta: { content: c } }] })}\n\n`;
      res.write(chunk("Hel"));
      setTimeout(() => {
        res.write(chunk("lo ") + chunk("world"));
        res.write("data: [DONE]\n\n");
        res.end();
      }, 10);
    });
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  url = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
afterAll(() => server.close());

describe("parseSseStream", () => {
  it("joins deltas and stops at [DONE]", () => {
    const s = 'data: {"choices":[{"delta":{"content":"a"}}]}\n\ndata: {"choices":[{"delta":{"content":"b"}}]}\n\ndata: [DONE]\n\n';
    expect(parseSseStream(s)).toBe("ab");
  });
});

describe("ask", () => {
  it("streams an answer through the backend with the bearer token", async () => {
    const cfg = resolveConfig({ backendUrl: url, token: "good" });
    const deltas: string[] = [];
    const answer = await ask(cfg, "say hi", { onDelta: (d) => deltas.push(d) });
    expect(answer).toBe("Hello world");
    expect(deltas.join("")).toBe("Hello world");
    const last = seen.at(-1)!;
    expect(last.auth).toBe("Bearer good");
    expect(last.body.model).toBe("default");
    expect(last.body.stream).toBe(true);
    expect(last.body.messages.at(-1)).toEqual({ role: "user", content: "say hi" });
  });

  it("attaches an image as a data URL part", async () => {
    const img = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "oc-img-")), "shot.png");
    fs.writeFileSync(img, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    expect(imageToDataUrl(img)).toBe("data:image/png;base64,iVBORw==");
    const cfg = resolveConfig({ backendUrl: url, token: "good" });
    await ask(cfg, "what is this", { imagePath: img });
    const content = seen.at(-1)!.body.messages.at(-1).content;
    expect(content[0]).toEqual({ type: "text", text: "what is this" });
    expect(content[1].image_url.url).toBe("data:image/png;base64,iVBORw==");
  });

  it("throws on non-2xx with the body", async () => {
    const cfg = resolveConfig({ backendUrl: url, token: "bad" });
    await expect(ask(cfg, "x")).rejects.toThrow(/backend 401: .*nope/);
  });

  it("refuses to run without a token", async () => {
    await expect(ask(resolveConfig({ backendUrl: url, token: undefined }, {} as NodeJS.ProcessEnv), "x")).rejects.toThrow(/missing token/);
  });
});
