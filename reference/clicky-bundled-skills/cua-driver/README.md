# cua-driver — HeyClicky Computer Use skill

HeyClicky's bundled Computer Use skill teaches Codex agents to drive
native macOS apps through the local `computer-use` MCP server, backed
by [`cua-driver`](https://github.com/trycua/cua/tree/main/libs/cua-driver).
Agents snapshot an app's accessibility tree, act by `element_token`
with `delivery_mode: "background"`, and verify via the action's effect
fields plus re-snapshot. Backgrounded-first: no focus steal, no cursor
warp, no Space follow.

This copy is product-managed inside Clicky. Do not ask users to
install `CuaDriver.app`, run the standalone `cua-driver` CLI, or
change their browser profile. HeyClicky supervises an embedded
`cua-driver` daemon inside `Clicky.app` (the MCP entry is a thin stdio
proxy to it), inherits HeyClicky's Accessibility and Screen Recording
grants, and exposes a curated MCP tool subset.

## What the skill covers

- The snapshot-before-AND-after invariant that keeps the agent honest
  about whether an action actually landed.
- The backgrounded-click recipe (yabai focus-without-raise + stamped
  SLEventPostToPid) that lets synthetic clicks land on Chrome web
  content without raising the window or pulling the user across Spaces.
- Web-app quirks (`WEB_APPS.md`) — Chromium/WebKit/Electron/Tauri,
  including the minimized-Chrome keyboard-commit caveat and the
  non-omnibox `set_value` workaround for ordinary fields.
- Trajectory recording (`RECORDING.md`) — upstream reference only in
  this build. HeyClicky's default runtime does not expose recording or
  replay tools.
- Canvas/viewport apps (Blender, Unity, GHOST, Qt, wxWidgets) —
  window-scoped background pixel clicks are available as the
  escalation rung; some viewport event loops still drop synthetic
  events, in which case stop and explain the missing capability
  instead of guessing.

See `SKILL.md` for the main body.

## Runtime prerequisites

1. **macOS 14 or newer**.
2. **HeyClicky permissions**: Accessibility and Screen Recording granted
   to Clicky.app during onboarding.
3. **Bundled driver present**:
   `Clicky.app/Contents/Helpers/cua-driver`.

## Invoking the skill

Codex auto-invokes the skill when the user asks for macOS GUI
automation, browser use, or background app control — e.g. "open the
Downloads folder in Finder", "click the Save button in Numbers", or
"navigate to trycua.com in my browser".

## Files

- `SKILL.md` — the main skill body (~500 lines). Loaded on first
  invocation; stays in context for the session.
- `WEB_APPS.md` — browsers, Electron, Tauri (Chromium + WebKit). Loaded
  on demand when SKILL.md's pointer is followed.
- `RECORDING.md` — upstream trajectory recording / replay reference.
  HeyClicky's default runtime does not expose these tools.
- `TESTS.md` — manual test scripts for end-to-end skill verification.

## Troubleshooting

- Missing Computer Use tools → verify the bundled helper exists and
  the runtime config registers `[mcp_servers.computer-use]`.
- Stale `element_token` / `snapshot_id` error → the handle was
  reused across turns, or across different windows of the same app.
  Call `get_window_state({pid, window_id})` first in the same turn,
  with the same window you're about to act against.
- Tiny screenshot / empty tree → likely a stale window capture;
  re-snapshot, or run `health_report` for the full diagnostics
  picture.
- System-alert beep when pressing Return in a minimized browser →
  the keyboard-commit-on-minimized limitation. For URL navigation,
  use `launch_app({bundle_id, urls:[...]})`; for normal page forms,
  use `set_value` on the field or AX-click a Go/Submit button. See
  `WEB_APPS.md`.

## Updates

The skill evolves alongside the driver. In HeyClicky, update it through
the bundled runtime upgrade path: bump the pinned `CUA_DRIVER_VERSION`
+ sha256 in `scripts/bundle_codex_runtime.sh`, sync the upstream skill
docs, then re-apply HeyClicky's managed-runtime guardrails.

## License

MIT. Same license as the parent `trycua/cua` repo.
