import { describe, it, expect } from "vitest";
import { SupabaseRest } from "../src/db.js";

describe("SupabaseRest.rpc", () => {
  it("returns undefined for a void function's empty 204 reply (oc_settle)", async () => {
    const db = new SupabaseRest("https://db.example", "service", async () => new Response(null, { status: 204 }));
    await expect(db.rpc("oc_settle", {})).resolves.toBeUndefined();
  });
  it("parses a JSON reply", async () => {
    const db = new SupabaseRest("https://db.example", "service", async () => new Response('{"ok":true}', { status: 200 }));
    await expect(db.rpc("oc_reserve", {})).resolves.toEqual({ ok: true });
  });
  it("still throws on an error status", async () => {
    const db = new SupabaseRest("https://db.example", "service", async () => new Response("nope", { status: 500 }));
    await expect(db.rpc("oc_reserve", {})).rejects.toThrow(/rpc oc_reserve failed \(500\)/);
  });
});

it("update PATCHes the matching rows", async () => {
  const calls: { url: string; init: any }[] = [];
  const db = new SupabaseRest("https://p.supabase.co", "sk", (async (url: any, init: any) => { calls.push({ url: String(url), init }); return new Response(null, { status: 204 }); }) as any);
  await db.update("oc_accounts", "user_id=eq.u1", { replaced_at: "2026-10-10T00:00:00Z" });
  expect(calls[0].url).toBe("https://p.supabase.co/rest/v1/oc_accounts?user_id=eq.u1");
  expect(calls[0].init.method).toBe("PATCH");
  expect(JSON.parse(calls[0].init.body)).toEqual({ replaced_at: "2026-10-10T00:00:00Z" });
});
