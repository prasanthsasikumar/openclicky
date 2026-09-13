# Windows reference clones

Shallow clones (`--depth 1`) of the Windows projects that overlap OpenClicky, pulled 2026-09-13.
The directories themselves are git-ignored (41 MB); this file is the tracked index. Re-fetch any of
them with `git clone --depth 1 <url> <dir>`.

The findings drawn from these are in [`../../docs/WINDOWS-REFERENCE.md`](../../docs/WINDOWS-REFERENCE.md).

| Directory | Upstream | Pinned | Stars / forks | Stack | Last upstream push |
|---|---|---|---|---|---|
| `clicky-windows-python` | [Bitshank-2338/clicky-windows](https://github.com/Bitshank-2338/clicky-windows) | `6e6dd52` | 204 / 44 | Python 3.11+, PyQt6 | 2026-09-12 (**alive**) |
| `clicky-windows-electron` | [tekram/clicky-windows](https://github.com/tekram/clicky-windows) | `c935b10` | 59 / 19 | Electron + TypeScript | 2026-04-23 (stalled) |
| `clicky-windows-dotnet` | [emreyilmaz46/clicky_windows](https://github.com/emreyilmaz46/clicky_windows) | `74dbbbe` | 19 / 5 | .NET 8 + WPF | 2026-04-10 (stalled) |
| `windows-mcp-cursortouch` | [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | `787385e` | 6,983 / 837 | Python, comtypes/UIA | 2026-09-12 (**alive**) |
| `mcp-windows-uia-csharp` | [sbroenne/mcp-windows](https://github.com/sbroenne/mcp-windows) | `b90485c` | 93 / 15 | C# / .NET, UIA | 2026-09-12 (**alive**) |
| `trope-cua-background` | [voctory/trope-cua](https://github.com/voctory/trope-cua) | `3a1fbf0` | small | C# (.NET) + Swift | 2026-07-20 |

All six are MIT except `trope-cua` (check its LICENSE before copying code).

## What each one is worth reading for

**`clicky-windows-python`** — the only living Clicky port. Read:
- `ai/hybrid_pointer.py` — three-tier pointing resolver (UIA → offline OCR → vision LLM) with the
  bounded UIA walk (`MAX_NODES=3500`, `MAX_DEPTH=40`) and a fuzzy name/role/AutomationId scorer.
- `ai/element_locator.py`, `ai/universal_locator.py` — Computer-Use→pixel conversion, and the
  provider-agnostic two-stage grid locator (12×8 grid, then a 6×6 pass inside the chosen cell).
- `ui/overlay.py:224` — the Qt flag set for a click-through overlay, and the deliberate 2 px gap at
  the bottom edge so a topmost window does not break an auto-hidden taskbar.
- `hotkey.py` — why modifier-only combos (`ctrl+win`) need a full keyboard hook plus sided-modifier
  aliases rather than `RegisterHotKey`.
- `requirements.txt` / README "Zero-Cost Setup" — the no-key offline path that appears to be why this
  port kept its users.

**`clicky-windows-electron`** — dead, but the best-written internals of the three. Read:
- `CLAUDE.md` §"Pointing pipeline — three coordinate spaces" and §"Overlay window" — the coordinate
  footguns and the exact Electron incantations for a transparent click-through always-on-top window.
- GitHub issue #10 (not in the clone; open it on the web) — an agent-mode proposal carrying three
  measured Windows constraints: keystroke coalescing floor, PowerShell stdin codepage, and the
  screenshot→DIP→physical coordinate hops.

**`clicky-windows-dotnet`** — dead and thin, but its README has the cleanest macOS→Windows API
mapping table (ScreenCaptureKit→GDI, AVAudioEngine→NAudio WASAPI, NSPanel→`WS_EX_LAYERED |
WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` + `HWND_TOPMOST`, CGEvent tap→`RegisterHotKey`).

**`windows-mcp-cursortouch`** — 2 M+ installs through Claude Desktop extensions; the de-facto
reference for a Windows action surface. Read `CLAUDE.md` (20-tool surface, tree budgets, screenshot
backends `dxcam|mss|pillow`, `WINDOWS_MCP_MAX_TREE_ELEMENTS=500`) and its open issues for the real
UIA failure modes.

**`mcp-windows-uia-csharp`** — the "semantic UI, never coordinates" position stated bluntly, plus a
known-limitations table for UAC and elevated windows. Useful as the C#/UIA implementation reference
if the Windows shell is written in .NET.

**`trope-cua-background`** — the most advanced idea in the set: *background-safe* automation with
per-action receipts (`background_safe`, `cursor_moved`, `foreground_changed`). Read
`docs/agent-routing.md`, `docs/capture.md`, `docs/child-session.md`,
`docs/appbroadcast-inputinjector.md`, `docs/limits.md`.

## Not cloned

- [bytedance/UI-TARS-desktop](https://github.com/bytedance/UI-TARS-desktop) — 38.9k★, Apache-2.0,
  193 MB. Cross-platform GUI-agent stack; clone separately if the agent lane needs it.
- [microsoft/OmniParser](https://github.com/microsoft/OmniParser) — 25.4k★, CC-BY-4.0, 52 MB.
  Screen-parsing model + `omnitool/` (drives a Windows 11 VM).
- [QwenLM/open-computer-use](https://github.com/QwenLM/open-computer-use) — 259★, MCP computer use
  for macOS/Linux/Windows.
- [Fluent](https://fluentforall.com/) — closed-source commercial Windows voice agent on the
  accessibility tree ($20–99/mo, agent runs metered, speech/dictation/navigation free). The nearest
  direct competitor to the Windows OpenClicky idea.
