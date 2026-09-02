# OpenClicky Shell (macOS, scaffold)

A menu-bar app (no Dock icon) with a floating panel that drives the `openclicky` CLI. This is the
first step toward HeyClicky's notch HUD: it does not yet capture audio itself, render agent cards,
or read the active document — it delegates everything to `agent/`.

```
cd macos/OpenClickyShell
swift build -c release            # or: swift run
.build/release/OpenClickyShell     # menu-bar icon appears; ⌥Space toggles the panel
.build/release/OpenClickyShell --smoke   # start, print wiring, exit (headless check)
```

Settings live in `~/.openclicky/shell.json` (created via the menu → Open Settings File):

```json
{
  "cliCommand": ["node", "/path/to/openclicky/agent/dist/cli.js"],
  "backendUrl": "http://localhost:8787",
  "token": "<supabase jwt or session token>",
  "workspace": "/Users/you/OpenClicky",
  "model": "",
  "voiceSeconds": 5
}
```

What the panel does:

- Text → `openclicky do "<text>"` (gate decides ask vs. agent); "Follow thread" resumes the last thread.
- 📷 → adds `--screenshot` to the next request (`screencapture`, needs Screen Recording permission for this app).
- 🎤 → `openclicky voice --seconds N` (ffmpeg microphone capture, needs Microphone permission).
- 〰 → `openclicky talk` (always-on OpenAI Realtime conversation; Stop sends SIGINT to hang up).
- Output streams into the panel; thread ids are picked up from the CLI's stderr.

Next steps for the shell: OpenAI Realtime voice (`POST /agent/realtime/session` already mints the
ephemeral secret), agent cards/timeline from `--json` output, active-document reading, permissions
onboarding, Sparkle updates, and a proper app bundle with `LSUIElement` + entitlements.
