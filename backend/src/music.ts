import type { Context } from "hono";
import { getEnv, type Env } from "./env.js";
import { ipOf } from "./accountAuth.js";

/**
 * `GET /music/search?q=…`: the song lookup behind the app's play_music action, for people with no
 * Spotify credentials of their own. Spotify's catalogue search needs an app's client credentials
 * (not a user login), so it runs here on OpenClicky's. The route is public: it carries only the
 * words, returns only catalogue data, and own-key people have no account token to send.
 */
export type MusicTrack = { uri: string; name: string; artists: string[]; album: string; durationMs: number };

const RESULT_LIMIT = 5;
const QUERY_MAX_CHARS = 200;
const QUERY_CACHE_MS = 60 * 60_000;
const QUERY_CACHE_MAX_ENTRIES = 500;
/** The token is re-minted this long before Spotify says it expires. */
const TOKEN_MARGIN_MS = 60_000;
const RATE_LIMIT_PER_MINUTE = 30;
/** Addresses remembered by the limiter at most; the oldest go first. */
const RATE_LIMIT_MAX_ADDRESSES = 1_000;

let token: { value: string; expiresAt: number } | undefined;
let tokenRequest: Promise<string> | null = null;
const queryCache = new Map<string, { at: number; tracks: MusicTrack[] }>();
const requestsByAddress = new Map<string, number[]>();

/** How many addresses the limiter remembers (tests check it stays bounded). */
export function musicRateLimiterEntries(): number {
  return requestsByAddress.size;
}

export function resetMusicCaches() {
  token = undefined;
  tokenRequest = null;
  queryCache.clear();
  requestsByAddress.clear();
}

const accountsBase = (env: Env) => (env.SPOTIFY_ACCOUNTS_BASE_URL || "https://accounts.spotify.com").replace(/\/$/, "");
const apiBase = (env: Env) => (env.SPOTIFY_BASE_URL || "https://api.spotify.com").replace(/\/$/, "");
const normalised = (query: string) => query.trim().toLowerCase().replace(/\s+/g, " ");

async function accessToken(env: Env, now: number): Promise<string> {
  if (token && now < token.expiresAt - TOKEN_MARGIN_MS) return token.value;
  // Lookups that arrive together share one token request instead of each minting its own.
  tokenRequest ??= requestToken(env, now).finally(() => {
    tokenRequest = null;
  });
  return tokenRequest;
}

async function requestToken(env: Env, now: number): Promise<string> {
  const basic = Buffer.from(`${env.SPOTIFY_CLIENT_ID}:${env.SPOTIFY_CLIENT_SECRET}`).toString("base64");
  const res = await fetch(`${accountsBase(env)}/api/token`, {
    method: "POST",
    headers: { authorization: `Basic ${basic}`, "content-type": "application/x-www-form-urlencoded" },
    body: "grant_type=client_credentials",
  });
  if (!res.ok) throw new Error(`spotify token: ${res.status}`);
  const body = (await res.json()) as { access_token: string; expires_in: number };
  token = { value: body.access_token, expiresAt: now + body.expires_in * 1000 };
  return token.value;
}

/** At most five tracks for `query`, from the cache when it was asked within the hour. */
export async function searchTracks(env: Env, query: string, now = Date.now()): Promise<MusicTrack[]> {
  const key = normalised(query);
  const hit = queryCache.get(key);
  if (hit && now - hit.at < QUERY_CACHE_MS) return hit.tracks;
  const params = new URLSearchParams({ q: key, type: "track", limit: String(RESULT_LIMIT), market: env.SPOTIFY_MARKET || "US" });
  const res = await fetch(`${apiBase(env)}/v1/search?${params}`, { headers: { authorization: `Bearer ${await accessToken(env, now)}` } });
  if (!res.ok) throw new Error(`spotify search: ${res.status}`);
  const body = (await res.json()) as { tracks?: { items?: { uri: string; name: string; duration_ms: number; artists?: { name: string }[]; album?: { name: string } }[] } };
  const tracks: MusicTrack[] = (body.tracks?.items ?? []).slice(0, RESULT_LIMIT).map((item) => ({
    uri: item.uri,
    name: item.name,
    artists: (item.artists ?? []).map((artist) => artist.name),
    album: item.album?.name ?? "",
    durationMs: item.duration_ms ?? 0,
  }));
  if (queryCache.size >= QUERY_CACHE_MAX_ENTRIES) queryCache.delete(queryCache.keys().next().value!);
  queryCache.set(key, { at: now, tracks });
  return tracks;
}

/** True when this address has made its thirty requests this minute. Windows that emptied are
 *  dropped and the map is capped, so a stream of made-up addresses cannot grow memory. */
function rateLimited(address: string, now: number): boolean {
  for (const [other, times] of requestsByAddress) {
    if (times.every((at) => now - at >= 60_000)) requestsByAddress.delete(other);
  }
  const recent = (requestsByAddress.get(address) ?? []).filter((at) => now - at < 60_000);
  if (recent.length >= RATE_LIMIT_PER_MINUTE) {
    requestsByAddress.set(address, recent);
    return true;
  }
  recent.push(now);
  requestsByAddress.delete(address);
  requestsByAddress.set(address, recent);
  while (requestsByAddress.size > RATE_LIMIT_MAX_ADDRESSES) requestsByAddress.delete(requestsByAddress.keys().next().value!);
  return false;
}

export async function musicSearchHandler(c: Context): Promise<Response> {
  const env = getEnv(c);
  const query = (c.req.query("q") ?? "").trim();
  if (!query || query.length > QUERY_MAX_CHARS) return c.json({ error: "q must be 1 to 200 characters" }, 400);
  if (!env.SPOTIFY_CLIENT_ID || !env.SPOTIFY_CLIENT_SECRET) return c.json({ error: "music search is not configured" }, 503);
  // The proxy's forwarded address (Caddy overwrites it), the same as the sign-up limiter; never
  // a header the client can set itself.
  if (rateLimited(ipOf(c), Date.now())) return c.json({ error: "too many searches; try again in a minute" }, 429);
  try {
    return c.json({ tracks: await searchTracks(env, query) });
  } catch (error) {
    console.error(`music: ${(error as Error).message}`);
    return c.json({ error: "the song lookup failed" }, 502);
  }
}
