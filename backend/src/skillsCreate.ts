import type { Context } from "hono";
import { getEnv } from "./env.js";
import { mapModelName } from "./proxy.js";
import { parseSkillMarkdown, slugify } from "./skillMarkdown.js";
import { SKILLS_MANIFEST } from "./skillsManifest.js";

/**
 * HeyClicky's "Create a skill": the user types what a skill should do, the model drafts one SKILL.md.
 * The client (CLI `openclicky skills create`, or the app's HUD) saves it into ~/.openclicky/skills/library.
 */
const SYSTEM = `You write skills for OpenClicky, a Mac voice assistant. A skill is ONE Markdown file in this exact format:
---
name: <Short Title Case name>
description: <one sentence: what it does and when to use it>
surfaces: [talk, agent]
---
<body: ≤ 600 words. Headings: Use When, Steps/Rules, Voice or Style (if relevant), Do Not.>

surfaces: "talk" applies when the user is chatting or asking; "agent" applies when the agent does work. Use [talk] for styles and knowledge, [agent] for operational workflows, [talk, agent] when unsure. Do not add comments after the value.
Only rely on capabilities from this list: {{CAPS}}. Never invent tools or integrations. Output the file only, no commentary.`;

// Both fields are interpolated into a model call billed to the backend key: keep them bounded.
const MAX_REQUEST_CHARS = 2000;
const MAX_CAPABILITIES = 20;
const MAX_CAPABILITY_CHARS = 64;

export async function createSkill(c: Context): Promise<Response> {
  const env = getEnv(c);
  const body = (await c.req.json().catch(() => ({}))) as { request?: unknown; capabilities?: unknown };
  if (typeof body.request !== "string" || !body.request.trim()) return c.json({ error: "request (string) is required" }, 400);
  if (body.request.length > MAX_REQUEST_CHARS) return c.json({ error: `request is longer than ${MAX_REQUEST_CHARS} characters` }, 400);
  if (body.capabilities !== undefined) {
    const ok =
      Array.isArray(body.capabilities) &&
      body.capabilities.length <= MAX_CAPABILITIES &&
      body.capabilities.every((x) => typeof x === "string" && x.length <= MAX_CAPABILITY_CHARS);
    if (!ok) return c.json({ error: `capabilities must be at most ${MAX_CAPABILITIES} strings of ${MAX_CAPABILITY_CHARS} characters` }, 400);
  }
  const model = env.SKILL_CREATE_MODEL || env.OPENAI_MODEL;
  if (!env.OPENAI_API_KEY || !model) {
    return c.json({ error: "skill creation needs OPENAI_API_KEY and OPENAI_MODEL (or SKILL_CREATE_MODEL) on the backend" }, 503);
  }
  const caps = Array.isArray(body.capabilities) ? body.capabilities.filter((x): x is string => typeof x === "string") : [];
  const capList = [...SKILLS_MANIFEST.filter((s) => s.kind !== "app").map((s) => s.id), ...caps].join(", ") || "none";
  const base = (env.OPENAI_BASE_URL || "https://api.openai.com/v1").replace(/\/+$/, "");
  const upstream = await fetch(base + "/chat/completions", {
    method: "POST",
    headers: { authorization: `Bearer ${env.OPENAI_API_KEY}`, "content-type": "application/json" },
    body: JSON.stringify({
      model: mapModelName(model, env.MODEL_ALIASES, env.OPENAI_MODEL_PREFIX),
      stream: false,
      messages: [
        { role: "system", content: SYSTEM.replace("{{CAPS}}", capList) },
        { role: "user", content: `Skill request: ${body.request.trim()}` },
      ],
    }),
  });
  if (!upstream.ok) return c.json({ error: `upstream ${upstream.status}` }, 502);
  const j = (await upstream.json().catch(() => ({}))) as { choices?: { message?: { content?: string } }[] };
  const raw = (j.choices?.[0]?.message?.content ?? "").trim();
  const markdown = raw.replace(/^```[a-z]*\n?/i, "").replace(/\n?```\s*$/, "").trim() + "\n";
  const parsed = parseSkillMarkdown(markdown);
  if (!parsed) return c.json({ error: "the model did not return a valid SKILL.md" }, 502);
  return c.json({ id: slugify(parsed.name), name: parsed.name, description: parsed.description, markdown });
}
