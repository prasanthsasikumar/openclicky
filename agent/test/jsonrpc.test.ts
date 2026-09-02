import { describe, it, expect } from "vitest";
import { PassThrough } from "node:stream";
import { parseJsonLines, JsonRpcStdio } from "../src/jsonrpc.js";

describe("parseJsonLines", () => {
  it("splits complete lines and keeps the remainder", () => {
    const r = parseJsonLines('{"a":1}\n{"b":2}\n{"c"');
    expect(r.messages).toEqual([{ a: 1 }, { b: 2 }]);
    expect(r.rest).toBe('{"c"');
  });
  it("ignores blank and non-JSON lines", () => {
    expect(parseJsonLines('\nnot json\n{"ok":true}\n').messages).toEqual([{ ok: true }]);
  });
});

describe("JsonRpcStdio", () => {
  it("correlates responses, routes notifications and server requests", async () => {
    const toChild = new PassThrough();
    const fromChild = new PassThrough();
    const rpc = new JsonRpcStdio(toChild, fromChild);
    const notes: string[] = [];
    rpc.onNotification((m) => notes.push(m));
    rpc.onServerRequest((id, m) => rpc.respond(id, { decision: "accept", m }));
    let written = "";
    toChild.on("data", (d) => (written += d));

    const p = rpc.request<{ ok: boolean }>("initialize", { x: 1 });
    fromChild.write('{"jsonrpc":"2.0","method":"thread/started","params":{}}\n');
    fromChild.write('{"jsonrpc":"2.0","id":99,"method":"item/commandExecution/requestApproval","params":{}}\n');
    fromChild.write('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}\n');

    expect(await p).toEqual({ ok: true });
    expect(notes).toEqual(["thread/started"]);
    expect(written).toContain('"method":"initialize"');
    expect(written).toContain('"id":99,"result":{"decision":"accept"');
  });

  it("handles messages split across chunks", async () => {
    const toChild = new PassThrough();
    const fromChild = new PassThrough();
    const rpc = new JsonRpcStdio(toChild, fromChild);
    const p = rpc.request("x");
    fromChild.write('{"jsonrpc":"2.0","id":1,"res');
    fromChild.write('ult":{"v":1}}\n');
    expect(await p).toEqual({ v: 1 });
  });

  it("rejects on error responses and when the peer closes", async () => {
    const toChild = new PassThrough();
    const fromChild = new PassThrough();
    const rpc = new JsonRpcStdio(toChild, fromChild);
    const p = rpc.request("x");
    fromChild.write('{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"boom"}}\n');
    await expect(p).rejects.toThrow("boom");
    const q = rpc.request("y");
    fromChild.end();
    await expect(q).rejects.toThrow("peer closed");
  });
});
