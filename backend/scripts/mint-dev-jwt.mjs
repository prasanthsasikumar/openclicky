#!/usr/bin/env node
// Mint a Supabase-style user JWT (HS256, signed with SUPABASE_JWT_SECRET) for local development.
// This is exactly the token shape Supabase Auth issues with the legacy JWT secret, so it exercises
// the real backend verification path without needing a live Supabase project.
//
//   npm run mint-jwt -w backend -- [--sub user-id] [--email dev@example.com] [--ttl 86400]
import { SignJWT } from "jose";
import { config as loadDotenv } from "dotenv";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
loadDotenv({ path: path.resolve(here, "..", ".dev.vars"), quiet: true });
loadDotenv({ path: path.resolve(here, "..", "..", ".env"), quiet: true });

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};

const secret = process.env.SUPABASE_JWT_SECRET;
if (!secret) {
  console.error("SUPABASE_JWT_SECRET is not set (backend/.dev.vars or ../.env)");
  process.exit(1);
}
const sub = opt("sub", "dev-user");
const email = opt("email", "dev@example.com");
const ttl = Number(opt("ttl", "86400"));

const jwt = await new SignJWT({ role: "authenticated", email, aud: "authenticated" })
  .setProtectedHeader({ alg: "HS256", typ: "JWT" })
  .setSubject(sub)
  .setIssuedAt()
  .setExpirationTime(Math.floor(Date.now() / 1000) + ttl)
  .sign(new TextEncoder().encode(secret));
console.log(jwt);
