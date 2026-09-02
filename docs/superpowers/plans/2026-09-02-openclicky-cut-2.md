# OpenClicky Cut 2 Plan (autonomous block, 2026-09-02)

**Goal:** Move from "vertical slice" toward the product surface described in `REVERSE-ENGINEERING.md`,
prioritizing what can be built and verified headlessly without provider keys.

**Decisions taken without the user (all reversible, one commit each):**

1. Initialize git and commit the initial cut as the baseline.
2. Agent UX — stream agent text as it arrives; `--screenshot` captures the screen with macOS
   `screencapture -x` and attaches it; `openclicky threads list|show|archive`.
3. Two-tier routing — `openclicky do "<text>"` runs a cheap gate through `POST /v1/messages`
   (Claude, server-side `ANTHROPIC_MODEL`) that returns `{"lane":"ask"|"agent"}`; falls back to
   the agent lane when the gate is unavailable. `--lane` overrides.
4. Approvals — `CodexAgent` accepts an `onApproval` hook; with it, threads run with
   `approvalPolicy: "on-request"`. CLI `--approve` prompts on the terminal; default stays auto-accept.
5. Backend groundwork for voice + skills — `POST /agent/realtime/session` (ephemeral Realtime
   client secret), `POST /agent/transcribe` (JSON base64 audio → server-side multipart to
   `/audio/transcriptions`), `GET /skills/library` (manifest generated from `skills/` at build time).
6. Optional MCP servers — rendered into the Codex config only when `COMPOSIO_MCP_URL` /
   `CUA_DRIVER_BIN` are set.
7. Voice lane — `openclicky voice` records N seconds with `ffmpeg -f avfoundation`, transcribes via
   the backend, then routes through the gate.
8. Native shell scaffold — `macos/` SwiftUI menu-bar app that drives the CLI (compile-verified only).

9. Realtime voice loop — `openclicky talk`: backend-minted ephemeral secret, WebSocket to OpenAI
   Realtime (subprotocol auth), ffmpeg mic in / ffplay out, server VAD + barge-in, `send_to_agent`
   tool into a persistent Codex thread. Verified against a fake Realtime server.
10. `--events` JSON Lines output mode + black-box CLI tests; structured request log in the backend.

Verification standard: unit tests per module, the real-codex integration test for anything that
touches the bridge, and a live run through the built backend with the fake upstream.

**Status (end of block):** all ten delivered and committed (`git log`), 65 tests passing, both
workspaces and the Swift shell building. Everything model-dependent was verified against fakes; see
README "Verification notes".
