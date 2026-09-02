import { serve } from "@hono/node-server";
import { config as loadDotenv } from "dotenv";
import path from "node:path";
import { fileURLToPath } from "node:url";
import app from "./app.js";

// Load secrets the same way wrangler does (backend/.dev.vars), falling back to the repo-root .env.
const here = path.dirname(fileURLToPath(import.meta.url));
const backendRoot = path.resolve(here, "..");
loadDotenv({ path: path.join(backendRoot, ".dev.vars"), quiet: true });
loadDotenv({ path: path.join(backendRoot, "..", ".env"), quiet: true });

const port = Number(process.env.PORT ?? 8787);
serve({ fetch: app.fetch, port }, (info) => {
  console.log(`openclicky backend listening on http://localhost:${info.port}`);
});
