import { describe, it, expect, beforeAll, afterAll, beforeEach } from "vitest";
import http from "node:http";
import { createApp } from "../src/app.js";
import type { Env } from "../src/env.js";
import { resetMusicCaches } from "../src/music.js";

// A fake Spotify: the accounts host mints tokens, the API host answers searches.
let spotify: http.Server;
let spotifyUrl: string;
let tokenCalls = 0;
let searchCalls: string[] = [];

beforeAll(async () => {
  spotify = http.createServer((req, res) => {
    let body = "";
    req.on("data", (d) => (body += d));
    req.on("end", () => {
      if (req.url === "/api/token") {
        tokenCalls += 1;
        expect(req.headers.authorization).toBe("Basic " + Buffer.from("cid:csecret").toString("base64"));
        expect(body).toBe("grant_type=client_credentials");
        res.writeHead(200, { "content-type": "application/json" });
        return void res.end(JSON.stringify({ access_token: "tok", token_type: "Bearer", expires_in: 3600 }));
      }
      if (req.url!.startsWith("/v1/search")) {
        searchCalls.push(req.url!);
        expect(req.headers.authorization).toBe("Bearer tok");
        res.writeHead(200, { "content-type": "application/json" });
        const items = Array.from({ length: 7 }, (_, i) => ({
          uri: `spotify:track:${i}`, name: `Track ${i}`, duration_ms: 1000 * i,
          artists: [{ name: "Artist" }], album: { name: "Album" },
        }));
        return void res.end(JSON.stringify({ tracks: { items } }));
      }
      res.writeHead(404); res.end();
    });
  });
  await new Promise<void>((r) => spotify.listen(0, "127.0.0.1", r));
  spotifyUrl = `http://127.0.0.1:${(spotify.address() as { port: number }).port}`;
});
afterAll(() => spotify.close());
beforeEach(() => { resetMusicCaches(); tokenCalls = 0; searchCalls = []; });

const app = createApp({ log: null });
const env = (): Env => ({
  SESSION_TOKEN_SECRET: "session-secret-session-secret-session",
  SPOTIFY_CLIENT_ID: "cid", SPOTIFY_CLIENT_SECRET: "csecret",
  SPOTIFY_BASE_URL: spotifyUrl, SPOTIFY_ACCOUNTS_BASE_URL: spotifyUrl,
});
const search = (q: string, headers: Record<string, string> = {}, over: Partial<Env> = {}) =>
  app.request(`/music/search?q=${encodeURIComponent(q)}`, { headers }, { ...env(), ...over });

describe("GET /music/search", () => {
  it("searches on a client-credentials token and returns at most five tracks, no bearer needed", async () => {
    const res = await search("blinding lights");
    expect(res.status).toBe(200);
    const body = await res.json() as { tracks: { uri: string; name: string; artists: string[]; album: string; durationMs: number }[] };
    expect(body.tracks).toHaveLength(5);
    expect(body.tracks[0]).toEqual({ uri: "spotify:track:0", name: "Track 0", artists: ["Artist"], album: "Album", durationMs: 0 });
    expect(tokenCalls).toBe(1);
    expect(searchCalls[0]).toContain("q=blinding+lights");
    expect(searchCalls[0]).toContain("type=track");
    expect(searchCalls[0]).toContain("limit=5");
    expect(searchCalls[0]).toContain("market=US");
  });

  it("caches the token and the query", async () => {
    await search("Blinding  Lights");
    await search("blinding lights");
    await search("something else");
    expect(tokenCalls).toBe(1);
    expect(searchCalls).toHaveLength(2);
  });

  it("answers 503 without credentials and 400 for a missing or oversize q", async () => {
    expect((await search("x", {}, { SPOTIFY_CLIENT_ID: undefined, SPOTIFY_CLIENT_SECRET: undefined })).status).toBe(503);
    expect((await app.request("/music/search", {}, env())).status).toBe(400);
    expect((await search("")).status).toBe(400);
    expect((await search("x".repeat(201))).status).toBe(400);
  });

  it("rate-limits a single address at thirty a minute", async () => {
    for (let i = 0; i < 30; i++) expect((await search(`q${i}`, { "x-forwarded-for": "10.0.0.1" })).status).toBe(200);
    expect((await search("q31", { "x-forwarded-for": "10.0.0.1" })).status).toBe(429);
    expect((await search("q31", { "x-forwarded-for": "10.0.0.2" })).status).toBe(200);
  });
});
