# Drawing model comparison — 2026-11-14

Which models on an OpenCode Go key can answer with a 3D drawing, and how well. Open `index.html` for the side-by-side
page (every model, three drawings each, rendered by the Windows stage).

## How it was run

- **The app's real request.** `scripts/prompt-dump` asks OpenClicky.Core for the drawing system prompt
  (`DrawStageLesson.SystemPrompt`, 1280 × 720, Detailed, no pieces, no pictures) and the user prompt for each question
  (`system.txt`, `user-*.txt`, `maxtokens.txt` = 8192).
- **The same three questions to all 30 models** on `opencode.ai/inference/go/openai`: "make me a 3D model of a
  school", "show me a 3D model of a human skull", "make me a 3D model of an airplane". Each request carried the
  screen as an image (`screen.jpg`, plain dark 1280 × 720) as the app does, with `x-opencode-session`, 5 at a time,
  a 300 s limit, and one retry with 4× the tokens if a reply was cut off with nothing visible.
- **Rendered by the Windows stage** (`windows/src/OpenClicky.Windows/Assets/DrawStage`) in Chromium at 1280 × 720,
  with each line's voice start and end simulated, then screenshotted (`shots/`). Errors are what the stage reported.
- Raw data: `results.json` (status, seconds for the whole answer, tokens, errors), `renders.json`, `programs/`
  (each model's program, replayable on the stage).

## Findings

| | Models |
|---|---|
| Best overall | **gemini-4-argon** — best skull of all (still cartoonish), good school and airliner, ~30 s each |
| Strong but slow | kimi-k3.1-pro (2-2.5 min), qwen3.8-max (2-3 min), deepseek-v4-pro (3 min; skull timed out) |
| Good and fast | gpt-6-luna (30-45 s), qwen3.7-plus (23-38 s), gpt-5.6-luna and claude-haiku-5-5 (15-33 s, simpler shapes) |
| Fastest, basic | glm-5.3-flash (~13 s) |
| Broken programs | minimax-m3 (skull: syntax errors, blank), step-5-preview (overwrote `step`), hy3, kimi-k3, deepseek-v4.1-flash (skull) |
| Can't draw here | glm-5.2, glm-5.3, beam-free, iceman-contributor-free (no image input); grok-4.7, hy4-preview, longcat-2.0 (over 5 min) |

- **No model draws a medically usable skull.** Anatomy should come from the bundled atlas (Z-Anatomy), not from the
  model building shapes.
- **Recommendation:** gemini-4-argon as the drawing model (Settings → drawing model), any fast model for ordinary
  answers.
- The last few requests (hy4 and others) were refused: the run used up the key's OpenCode Go 5-hour limit. 90
  drawings at up to 8k output tokens is a lot for one window — run fewer models at a time next time.

## Re-running

```bash
dotnet run --project docs/model-comparison/2026-11-14-drawings/scripts/prompt-dump -- <folder>   # writes system.txt, user-*.txt, maxtokens.txt
node docs/model-comparison/2026-11-14-drawings/scripts/run.mjs                                     # asks every model (skips ones in results.json)
node docs/model-comparison/2026-11-14-drawings/scripts/build-page.mjs                              # rebuilds index.html from results.json, renders.json, shots/
```

Rendering the programs to `shots/` was done with Playwright against the stage page served locally (the page's CSP
allows fetching only its own origin, so the programs were served from the same origin).
