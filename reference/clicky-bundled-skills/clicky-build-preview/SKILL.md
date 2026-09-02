---
name: clicky-build-preview
description: Build, modify, launch, preview, and iterate websites, web apps, dashboards, landing pages, HTML files, React/Next apps, and frontend UI. Use when the user wants a visible working thing, not only code.
---

# HeyClicky Build Preview

Build the thing, launch it when appropriate, show or report where it is, and iterate. This workflow absorbs frontend design, polish, and animation guidance without exposing those raw skills separately.

## Use When
- The user asks to build a website, app, dashboard, landing page, HTML file, frontend component, or local preview.
- The user asks to open, preview, or revise a generated site/app.
- The request includes UI polish, animation, responsiveness, or visual design fixes.

## Do Not Use When
- The task is primarily GitHub/PR/CI/repo workflow; use `clicky-repo-operator`.
- The task is primarily localhost/toolchain failure; use `clicky-dev-setup-doctor`.
- The task is only finding/opening an existing generated file; use `clicky-artifacts`.

## Primary Path
1. Decide the delivery format first. If the result can be one self-contained HTML file (with inline or CDN CSS/JS), build it as a static file and hand back the absolute path via `clicky-artifacts`. Do not start a server for something a `file://` open can show.
2. Only reach for a dev/preview server when the project genuinely needs a bundler, framework runtime, or HMR (React/Next/Vite/SvelteKit projects, multi-route apps, JSX/TSX sources, server components, etc.).
3. Detect the stack and package manager.
4. Make scoped code changes using existing project patterns.
5. Use high-quality frontend rules: strong hierarchy, responsive layout, accessible controls, tasteful motion, no generic filler.
6. Verify with normal local checks first: file existence, server response, build output, and targeted browser/page checks when available.

## If a dev/preview server is required
A foregrounded `npm run dev`/`vite preview` shell call dies when the tool call returns, so the URL you report is already dead by the time the user opens it. Always:

1. Start it detached and logged:
   `nohup npm run preview -- --host 127.0.0.1 --port 4173 > .clicky-preview.log 2>&1 &`
   then `disown` (or use `setsid` / `(cmd &)` — the goal is "survives this shell call").
2. Poll until it actually answers before reporting the URL:
   `for i in {1..20}; do curl -fsS http://127.0.0.1:4173/ >/dev/null && break; sleep 0.5; done`
3. If the poll never succeeds, `tail -n 40 .clicky-preview.log` and report the real error — do not hand back a URL you have not seen respond.
4. Reuse an existing listener on the port (`lsof -i :4173`) instead of spawning a duplicate.
5. Prefer explicit `--host 127.0.0.1` + a fixed `--port` so the URL you report matches what's actually bound.

## Fallbacks
- For one-off pages, create a single HTML/CSS/JS file and open/reveal it via `clicky-artifacts`. This is the default, not the fallback.
- If the request turns into operating an existing app or logged-in browser UI, stop this build workflow and route to the visible GUI workflow instead of treating it as preview verification.
- If dependencies fail, route to `clicky-dev-setup-doctor`.

## Safety
- Do not refactor unrelated code.
- Do not overwrite user files unless asked.
- Avoid foregrounding or hijacking the user's browser; show the URL/path instead when enough.
- Do not use browser-specific shell launches such as `open -a Google Chrome` as a preview loop. If the user asked to see a finished local file, use the artifact/open path late and deliberately.
- Never report a `http://127.0.0.1:<port>/` URL you have not verified is currently responding.

## Artifacts
- End with the local URL or absolute file path.
- For generated static pages, save under a stable project or `output/builds/<slug>/` path.
- Use `clicky-artifacts` for open/reveal requests.

## Verification
- Run the project's relevant checks when reasonable.
- For frontend work, verify the page loads and key UI states render using local
  server responses, page/browser test tools, screenshots, or generated files
  before handing the result back.
- If visual inspection is not possible, say what was verified and what remains.
