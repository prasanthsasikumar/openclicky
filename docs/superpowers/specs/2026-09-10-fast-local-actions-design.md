# Fast local actions — design (2026-09-10)

Approved in chat on 2026-09-10: a fixed set of typed native actions on the Realtime session, a
short spoken confirmation for each, Computer Use actually attached, and the routing instructions
corrected to match what is really available. Goal: "open Spotify" and "create a folder on my
desktop" complete in about two seconds instead of twelve.

## 1. Where the twelve seconds go

Measured 2026-09-10 on this Mac, `create a folder named SpeedProbe on the desktop`, wall clock
12.05 s (Codex rollout `~/.openclicky/codex-home/sessions/2026/09/10/…01a08bf9…jsonl`):

| Segment | Time | What it is |
|---|---|---|
| CLI + `codex app-server` spawn, config render, MCP startup | 0.40 s | process startup |
| First model turn | 7.72 s | reading the request, reasoning, emitting the tool call |
| `mkdir` | 0.37 s | the work itself (0.1 s of it the filesystem) |
| Second model turn | 3.56 s | writing "Created **SpeedProbe** on your Desktop." |

Two findings decide this design:

- **Startup is already negligible.** A warm Codex process — the intuitive optimisation — would buy
  0.4 s of 12. The latency is two LLM round-trips; the only way to two seconds is to remove
  round-trips, not to speed them up.
- **Some runs pay for round-trips that do nothing.** Runs of the same request ranged 9.8 s to
  29.8 s today. The spread is the agent calling `list_mcp_resources` to hunt for a Computer Use
  path before falling back to the shell (5–9 s), which §4 explains and fixes.

For comparison, the same day's runs end to end: 10.9 s (in-app voice, Desktop), 13.9 s (workspace
folder), 18.2 s (Desktop, CLI), and 24–30 s for the two that failed before the sandbox fix.

## 2. Fast lane: typed native actions on the Realtime session

The `gpt-realtime` session is already connected and listening, and already calls a native tool —
`point_at`, dispatched in `RealtimeVoiceClient.handleToolCall`. Simple local actions take the same
route instead of `send_to_agent`, so nothing spawns and no Codex turn happens.

### 2.1 The tools

New file `macos/OpenClicky/OpenClicky/MacActions.swift`: the catalogue, the argument types, and the
executor. `RealtimeVoiceClient` gains the tool definitions in its `session.update` payload (next to
`send_to_agent` and `point_at`) and a `case` per tool in `handleToolCall`.

| Tool | Arguments | Executed by |
|---|---|---|
| `open_app` | `name: string` | LaunchServices lookup → `NSWorkspace.openApplication` |
| `open_url` | `url: string` (http/https only) | `NSWorkspace.open` |
| `create_folder` | `name: string`, `location: Location` | `FileManager.createDirectory` |
| `reveal_in_finder` | `name: string`, `location: Location` | `NSWorkspace.activateFileViewerSelecting` — errors when nothing of that name is in that location, rather than opening the folder itself |
| `set_volume` | `level: integer 0–100` | `osascript -e 'set volume output volume <level>'` |
| `media_control` | `action: playpause \| next \| previous` | HID media keys via `CGEvent` |

`Location` is a closed enum — `desktop`, `downloads`, `documents`, `workspace`, `home` — resolved in
Swift to a `URL`. Tools never take a path.

### 2.2 Why typed verbs and not one shell tool

A shell tool would cover every phrasing on day one and need no per-verb work. It also hands a
speech-to-text pipeline an unsandboxed shell: the Codex lane's protections (workspace sandbox,
`ModelInstructions.md`, the approval gate) all live on the other side of `send_to_agent`, and none
of them apply here. With typed arguments the worst case of a mishearing is a wrongly *named* folder
in a known location, never a wrong *command*. Requests outside the set keep falling through to
`send_to_agent` exactly as they do today.

Argument validation, all in `MacActions`:

- `name` — rejected if it contains `/`, `:`, a leading `.`, or any path traversal; trimmed; capped
  at 255 characters (the HFS+ limit).
- `location` — an unknown value is an error, never a fallback to `home`.
- `url` — scheme must be `http` or `https`; anything else is refused (no `file://`, no custom
  schemes that could launch a handler).
- `level` — clamped to 0–100.

### 2.3 What the user hears

Every tool returns one plain sentence, which is what the model speaks:

| Outcome | Returned |
|---|---|
| success | "Opened Spotify." / "Created Test on your Desktop." |
| app not found | "I couldn't find an app called Spotify." |
| folder exists | "Test is already on your Desktop." |
| permission refused | "macOS hasn't granted access to Documents yet — I asked for it." |
| bad argument | "That name has characters a folder can't have." |

The model speaks its confirmation from that string, which costs one short Realtime turn (~1 s) after
an action that itself takes milliseconds. Target: **1–2 s end to end**, against 12 today.

`RealtimeVoiceClient.defaultInstructions` gains one clause: these tools handle opening apps, files
and URLs, making folders, revealing files, and volume/media; `send_to_agent` stays for everything
that is real work. No other instruction changes in the voice lane.

### 2.4 macOS permission prompts

`create_folder` and `reveal_in_finder` in `desktop`, `documents` or `downloads` hit macOS's
per-folder TCC prompt the first time. This is an improvement on today: the prompt is attributed to
OpenClicky, which the user recognises and has already granted other permissions to, instead of to a
`node` subprocess. `workspace` (`~/OpenClicky`) and `home` need no prompt. The refusal path is a
spoken sentence, not silence — see the table above.

## 3. Reclaiming the closing turn in the agent lane

The 3.56 s tail is Codex taking a second model turn to write one sentence after the work already
succeeded. The safe way to reclaim it is a one-shot mode: for a request the gate classifies as a
single action, the run returns at the first command that exits 0, cancels the turn, and the spoken
line is built from the command event rather than from the model.

**Sequenced after §2, deliberately.** Section 2 removes almost every one-command task from the agent
lane; what remains there is genuinely multi-step, where waiting for a real summary is correct
behaviour rather than latency to be optimised away. Building one-shot first would optimise a
population of requests that §2 is about to empty. Revisit with the measurements from §5 in hand:
if single-command agent runs are still common after §2 ships, build it; if they are rare, the
complexity is not worth it.

## 4. Route hygiene: attach Computer Use, correct the instructions

`skills/ModelInstructions.md` tells the agent to prefer Cua/Computer Use for native macOS app
control, and explicitly forbids shell `open`, AppleScript, `osascript` and `cliclick` "when Computer
Use can perform the GUI action". But `cuaDriverBin` is unset in `~/.openclicky/shell.json`, so
`renderMcpServers` never emits `[mcp_servers.computer-use]` and the server is not attached. The
model is told a capability exists, spends turns looking for it, and only then falls back to the
shell. That is the 5–9 s of hunting.

The driver is installed: `~/.local/bin/cua-driver` → `/Applications/CuaDriver.app/Contents/MacOS/cua-driver`.

1. **Attach it.** `resolveConfig` defaults `cuaDriverBin` to the installed driver when the binary
   exists (checking `~/.local/bin/cua-driver`, then the app bundle path), so the config renders the
   `computer-use` server. An explicit `cuaDriverBin` in `shell.json` or `CUA_DRIVER_BIN` still wins,
   and an explicit empty string disables it.
2. **Say what is native now.** `ModelInstructions.md` gains a line: OpenClicky performs simple local
   actions itself before the agent is involved, so opening an app or making a folder does not reach
   the agent lane and should not be planned around.
3. **Forbid the hunting.** A second line: do not enumerate MCP resources to discover whether
   Computer Use exists — the attached tools are visible in the tool list; when the `computer-use`
   server is absent, use the shell for local file and app actions.

Note the interaction, stated so it is not a surprise: with Computer Use attached, an agent-lane
"open Spotify" gets *slower*, because Cua's snapshot-act-verify contract is more careful than
`open -a`. That is acceptable precisely because §2 intercepts those requests before the agent lane.

## 5. Instrumentation and how "two seconds" gets proved

`AppLog` records `realtime: agent task: …` when a run starts and nothing when it ends, which is why
these measurements had to come from Codex's rollout files.

- Log every fast action with its elapsed time: `mac action: open_app "Spotify" ok in 0.14 s`.
- Log agent-run completion with elapsed time and outcome, next to the existing start line.
- `scripts/measure-actions.sh`: replay a fixed list of phrases through the same entry point the
  voice lane uses, parse `app.log`, and report p50/p95 per verb. The acceptance number for §2 is
  **p95 under 2 s for `open_app` and `create_folder`**, measured, not asserted.

## 6. Testing

Unit (`OpenClickyTests`):

- name sanitising: separators, traversal, leading dot, over-length, empty after trimming
- `Location` → `URL` mapping, and that an unknown location is an error rather than a fallback
- URL scheme filtering: `https` accepted, `file://` and custom schemes refused
- app-name resolution: an installed app, an app that is not installed, an ambiguous prefix
- the spoken sentence produced for each outcome in §2.3

Unit (`agent/test`):

- `resolveConfig` picks up an installed cua-driver, an explicit setting wins, an explicit empty
  string disables it
- `renderMcpServers` emits `[mcp_servers.computer-use]` when it is configured and nothing when not

Manual, on the real app: "open Spotify" with Spotify closed, already open, and not installed;
"create a folder called Test on my desktop" twice in a row (second one already exists); a request
outside the set ("summarise this PDF") to confirm it still reaches the agent lane.

## 7. Sequencing

1. §2 fast lane with §5 instrumentation — the change that produces the two-second number.
2. §4 route hygiene — small, independent, and it makes the instructions honest either way.
3. §3 one-shot mode — only if §5's measurements show single-command agent runs are still common.
