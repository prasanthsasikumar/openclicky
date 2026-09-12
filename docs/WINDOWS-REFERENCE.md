# Windows: what to build, from what already worked

Research pass of 2026-09-13. The question behind it: OpenClicky is macOS-only; several people have
already tried to build this on Windows, so what did they learn, what is the platform good for now,
and what should a Windows OpenClicky actually be?

Nothing here has been run — no Windows machine was involved. Every claim is either a code/doc
citation from a cloned repo (`reference/windows/`, see its [README](../reference/windows/README.md))
or a cited source. Where a number is somebody's claim rather than a measurement, it says so.

**Reddit could not be checked.** Reddit blocks this session's fetch tool by domain policy and
returns 403 to direct requests; the two open mirrors tried were rate-limited or behind a
proof-of-work challenge. Community signal here comes from GitHub issue trackers (the richest source
by far), Hacker News, and project docs instead. If you want the Reddit pass, run the searches in
your browser and paste threads in — the queries worth running are at the bottom.

---

## 1. The landscape

Three separate people ported Farza's Clicky to Windows within days of its release (2026-04-07), in
three different stacks. All three are MIT. One is still alive.

| Project | Stack | Stars / forks | Status | Key requirement |
|---|---|---|---|---|
| [Bitshank-2338/clicky-windows](https://github.com/Bitshank-2338/clicky-windows) | Python 3.11+ / PyQt6 | **204 / 44** | pushed 2026-09-12, active | **none** — runs on Ollama/LM Studio |
| [tekram/clicky-windows](https://github.com/tekram/clicky-windows) | Electron + TypeScript | 59 / 19 | last push 2026-04-23 | Anthropic (+AssemblyAI, ElevenLabs) |
| [emreyilmaz46/clicky_windows](https://github.com/emreyilmaz46/clicky_windows) | .NET 8 + WPF | 19 / 5 | last push 2026-04-10 | Anthropic + AssemblyAI + ElevenLabs |
| [farzaa/clicky](https://github.com/farzaa/clicky) (upstream, macOS) | Swift | 7,535 / 1,484 | last push 2026-04-28 | OpenAI |

Adjacent, and more mature than any of the ports:

| Project | What it is | Scale |
|---|---|---|
| [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | MCP server: 20 tools over UIA + PowerShell; "2M+ users" via Claude Desktop extensions (their claim) | 6,983★ / 837 forks |
| [sbroenne/mcp-windows](https://github.com/sbroenne/mcp-windows) | C#/UIA MCP server, semantic-only ("by name, not coordinates") | 93★ |
| [voctory/trope-cua](https://github.com/voctory/trope-cua) | Background-safe computer use for Windows + macOS, with per-action safety receipts | small, Show HN 2026-04-29 |
| [bytedance/UI-TARS-desktop](https://github.com/bytedance/UI-TARS-desktop) | Cross-platform GUI-agent stack + VLM operators | 38,941★ |
| [Fluent](https://fluentforall.com/) | **Closed-source commercial**: Windows voice agent on the accessibility tree, explicitly "not a vision model — faster, cheaper, your screen stays on your machine". $20/$50/$99 per month, agent runs metered, speech + dictation + navigation free forever | HN 2026-07-31 |

Fluent is the closest thing to a direct competitor for a Windows OpenClicky, and its pitch is worth
reading as a market signal: accessibility-tree-first, privacy-as-speed, and a free tier for exactly
the parts (talk, dictate, navigate) that OpenClicky already gives away.

Also note where Microsoft itself is: Copilot Vision went from preview to a standard Windows 11
feature around May 2026 and can highlight where to click; Click to Do and an "Ask Copilot" taskbar
entry are slated for mid-2026. The OS is moving into this product category, which makes the
differentiators BYOK/self-host, offline, skills, the agent lane, and cross-platform parity — not
"AI that sees your screen".

---

## 2. Four things the evidence actually says

### 2.1 The port that survived is the one that needs no API key

The two ports that required Anthropic + AssemblyAI + ElevenLabs keys were both abandoned inside three
weeks. The one still shipping made a zero-key offline path the headline feature: Ollama or LM Studio
for the model, `faster-whisper`/`whisper.cpp` for STT, Edge TTS (free, 400+ voices) for speech,
DuckDuckGo scraping for search. Its issue tracker is mostly people asking for *more* of that —
LM Studio support, OpenAI-compatible third-party providers (DeepSeek, DashScope/Qwen, SiliconFlow),
free NVIDIA API models, GitHub Copilot for students — plus `num_ctx` tuning so 8 GB cards do not
blow their KV cache.

Three data points is not proof, and 204 stars is a small sample. But the direction is consistent with
who is on Windows: the audience skews toward people who will not attach a credit card to a desktop
toy. OpenClicky's hosted-invite + BYOK model is a *harder* sell there than on macOS, and a Windows
build that cannot do anything without a key is likely to stall the way two of three ports did.

### 2.2 Everyone independently converged on the same pointing stack — and OpenClicky already has it

Nobody who shipped kept "ask the model for pixel coordinates" as the primary path:

- **clicky-windows-python** (`ai/hybrid_pointer.py`) documents the failure first — *"vision models
  trained on natural images don't have pixel-precise spatial reasoning, so they'd often pick a
  neighboring cell or off-by-one row"* — then resolves in three tiers with their own claimed
  latencies: **UIA tree ~5 ms (pixel-perfect) → offline OCR via RapidOCR/ONNX ~300 ms (text-perfect)
  → vision-LLM grid fallback ~1–3 s (best effort)**.
- **clicky-windows-electron** kept the vision path but added a second pass: crop ~300 px around the
  first estimate *from the full-resolution source image*, re-ask with `max_tokens: 32`, run all crops
  in `Promise.all`. Their note: a single call is typically ±30 px on a 1568-wide image, which is not
  enough for adjacent like/dislike buttons or toolbar icons.
- **mcp-windows** put it bluntly in its README: *"Screenshot-based automation doesn't work reliably…
  We tried it (check the commit history) — it failed too often to be useful."*
- **trope-cua** refuses pixel actions by default and tells agents to use `element_index` actions,
  falling back to pixels only for canvas and non-accessibility surfaces.

The 2026 research agrees that crop-refinement is where the accuracy is: GUI-Owl-1.5-32B reports 72.9 %
on ScreenSpot-Pro rising to 80.3 % with a two-stage crop-tool refinement, and recent papers
(MEGA-GUI, UI-Zoomer) are explicitly multi-stage zoom-in grounders.

**This is the same conclusion OpenClicky reached on macOS**, which is the good news:
`AccessibleElementLocator.swift` (AX role + container titles, written precisely because OCR cannot
tell two identical "Edit" buttons apart), then `ScreenTextLocator.swift` (OCR snapping), then
`ScreenElementGrounder.swift` (Claude Haiku on the 1280 px screenshot, measured ≤ 8 px). The Windows
port of `point_at` is therefore a **substitution, not a redesign**: UIA in place of AX, Windows
Text-Recognition or RapidOCR in place of Vision.framework, same grounder behind it.

### 2.3 UIA is the moat and the bug farm

Windows-MCP's open issues are a free list of everything that goes wrong in a UIA-based product, and
all of it applies to `point_at`:

- **Electron/VS Code deadlock** — a full snapshot tree walk deadlocks Electron host apps and Windows
  kills the process; the fix they are converging on is process exclusion plus a per-window UIA budget.
- **Stale elements** — traversal retries elements after `UIA_E_ELEMENTNOTAVAILABLE`; dead nodes must
  be pruned during the walk.
- **Empty desktop under stdio** — identical code sees the desktop in-process but gets an empty tree
  when hosted as an MCP server (open, unresolved).
- **Event pump instability** — their UIA focus watchdog had to be made opt-in because the event pump
  crashes the server; a dead asyncio self-pipe once pinned a full CPU core after screen wake.
- **`comtypes` generated-wrapper races** when several instances start at once.
- **Unicode** — a UI tree containing emoji (surrogate pairs) crashed the snapshot.
- **Localisation** — they recommend English as the Windows display language or disabling the app tool.

Mitigations that already exist in the wild, worth copying verbatim:

| Mitigation | Where |
|---|---|
| Bounded BFS: `MAX_NODES=3500`, `MAX_DEPTH=40`, start at `GetForegroundControl()` not the desktop root | `clicky-windows-python/ai/hybrid_pointer.py` |
| Element budget (`WINDOWS_MCP_MAX_TREE_ELEMENTS=500`) that truncates and says so | Windows-MCP |
| Thread-pooled traversal + retries (`THREAD_MAX_RETRIES=3`) | Windows-MCP |
| Fuzzy match on name **and** role **and** `AutomationId`, with a bonus for interactive control types | both |
| `IAccessible2` fallback for Firefox (no `RootWebArea` over UIA); CDP for Chromium/Electron | Windows-MCP, trope-cua |
| Drive input through UIA coordinates so there is no DPI mismatch with `BoundingRectangle` | Windows-MCP |

And the hard boundary nobody can cross: **UAC prompts and elevated windows.** A non-elevated process
cannot see or act on them, full stop. `point_at` must detect and say so rather than fly the buddy at
a secure desktop.

### 2.4 Background-safe is a Windows-only superpower worth knowing about

trope-cua is the only project in the set treating "the agent must not steal my mouse or my focus" as
a first-class contract. Its shape is worth stealing even if the implementation is not:

- Every mutating action returns a **receipt** with `background_safe`, `cursor_moved`,
  `foreground_changed`. `ok=true` does not imply the user's foreground work survived.
- Preferred route order: UIA/MSAA element action → CDP for Chromium/Electron → refuse with a named
  blocker (`requires_cdp_or_child_session`, `requires_background_launch_lane`) rather than silently
  falling back to parent-session `SendInput`.
- Unsafe routes exist but are explicit human opt-ins (`allow_parent_cursor`, `allow_parent_sendinput`,
  `unsafe_allow_foreground`).
- The visible agent cursor is an **overlay that communicates intent** and is explicitly not the
  hardware cursor — which is exactly OpenClicky's buddy, and means the buddy can keep flying while
  the real work happens without touching the user's pointer.
- Two escape hatches for surfaces UIA cannot drive: a **child session**
  (`WTSGetChildSessionId` + an RDP ActiveX host with `ConnectToChildSession=true`, where hardware-style
  `SendInput` is allowed without touching the parent desktop) and **AppBroadcast**
  (`InputInjector.TryCreateForAppBroadcastOnly()`, which needs restricted capabilities ordinary
  desktop apps don't get). Their own probes show the AppBroadcast lane leaves the parent cursor and
  foreground alone but does not deliver input to Chrome without an active capture target — i.e. it is
  a research seam, not a shipping path.

macOS has no equivalent; if the Windows build wants a story the Mac cannot tell, this is it.

---

## 3. The traps, collected

Everything below cost somebody time. Copy this table into whatever plan comes out of this.

**Overlay / HUD**

| Trap | Detail | Source |
|---|---|---|
| `transparent: true` + `fullscreen: true` renders *nothing* on Windows | Size the window to explicit `display.bounds` instead | electron port `CLAUDE.md` |
| Always-on-top is dropped at show time | Re-apply `setAlwaysOnTop(true, "screen-saver")` **after** `showInactive()` | electron port |
| Click-through needs event forwarding | `setIgnoreMouseEvents(true, { forward: true })`; Win32 equivalent is `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE` + `HWND_TOPMOST` | electron + dotnet ports |
| `ready-to-show` sometimes never fires for transparent windows | Keep a `did-finish-load` fallback | electron port |
| A topmost full-screen overlay breaks an auto-hidden taskbar | Leave a 2 px gap at the bottom edge | python port `ui/overlay.py:389`, filed as a bug first |
| A transparent window that renders nothing looks exactly like a window that failed to open | Log "overlay shown" explicitly | electron port |
| Tray icon survives app close / doesn't render at some DPI scales | Both filed as bugs | python + electron ports |

**Coordinates and capture**

| Trap | Detail |
|---|---|
| Three coordinate spaces, always | screenshot px → DIP (display bounds) → physical px. Skipping the DIP hop silently misses on any display not at 100 % scaling. |
| Never send a pass-1 image above the model's cap | Above ~1568 px the API rescales server-side and returns coordinates in a space you cannot reconstruct. Anthropic's *computer toolset* is a different limit (2576 px) — the ports found that reusing 1568 there made taskbar/tray icons unidentifiable and sent the agent into zoom loops. |
| Never scale coordinates before the refinement pass | Refinement operates on raw model-space coordinates |
| Strip `[POINT:…]` tags before TTS | Otherwise the voice reads coordinates aloud |
| DRM'd content (Netflix, Disney+) captures black | By design; detect and explain |
| Multi-monitor: capture all, send the cursor's screen first | Both ports converged on this |

**Input**

| Trap | Detail |
|---|---|
| Keystroke delay is load-bearing | With no inter-character delay, characters after a word boundary get coalesced and arrive as the final character (`"abc defgh"` → `"abc hhhhh"`). Measured floor ~5 ms; 15 ms is free margin since Windows timer granularity is ~15.6 ms. |
| Text through a PowerShell bridge must cross as UTF-16 code units | PowerShell decodes redirected stdin with the OEM codepage and mangles non-ASCII |
| Verify the Start menu opened before typing into it | Otherwise text lands in whatever has focus |
| Abort paths must be redundant | Global hotkey + mouse-nudge + tray + UI button, all resolving to one `AbortSignal`, checked between every action; the nudge watcher must suppress itself while the executor is mid-action |
| Native input modules are a packaging tax | The electron port rejected nut.js (Electron ABI rebuilds, Windows build tools, community-fork npm distribution) in favour of `SendInput` P/Invoked from a long-lived PowerShell process: ~260 ms startup, ~3 ms/action |

**Hotkeys**

| Trap | Detail |
|---|---|
| Modifier-only combos cannot use `RegisterHotKey` | `ctrl+win` has no terminal key; you must hook all key events and track state — see `hotkey.py` |
| `is_pressed("ctrl")` only matches left-ctrl on some layouts | Probe sided aliases (`left ctrl`, `right ctrl`, …) |
| Hotkey library teardown can unhook hooks it does not own | Open bug on the python port (`unhook_all()`) |
| Default combos collide | `Ctrl+Shift+Space`, `Ctrl+Win`, `Ctrl+Alt+Space` all reported as conflicting with other apps; make it user-configurable from day one |

**Packaging, keys, distribution**

| Trap | Detail |
|---|---|
| Plaintext API keys in `%APPDATA%` | Both ports ship it and both acknowledge it; one had a "Critical security issues" report filed. OpenClicky already fixed this class on macOS (0600 `shell.json`, Keychain migration pending) — do not regress it on Windows. |
| MSIX sandboxing breaks child processes | Claude Desktop's Store build virtualises `%APPDATA%` and Electron in the MSIX sandbox does not inherit `PATH`, so absolute paths are required. Relevant if OpenClicky's Windows shell spawns the `openclicky` CLI or Codex. |
| SmartScreen | A new signed app still warns until reputation accrues (weeks, hundreds of clean installs). Reputation attaches to the verified publisher identity, so it carries across builds. |
| Local vision models OOM on 8 GB cards | KV-cache overflow needed an explicit `num_ctx`; a 7B vision model is a ~5 GB pull |
| Python distribution is heavy | PyInstaller + Inno Setup works (python port does it) but the install is large and the tracker shows the usual "`.env` changes not picked up", "stuck on Listening…" class of packaging bugs |

---

## 4. What the platform offers now (September 2026)

### Capture

| Option | When |
|---|---|
| **Windows.Graphics.Capture** (WGC) — `GraphicsCaptureItem` via `IGraphicsCaptureItemInterop::CreateForWindow`, D3D11 frame pool | The production path. Set `IsCursorCaptureEnabled = false` (don't capture your own buddy) and `IsBorderRequired = false` where permitted. Per-window, survives occlusion. |
| **DXGI Desktop Duplication** (`dxcam` in Python) | Full-screen, high-FPS; 240+ fps claimed. Overkill for one screenshot per question, useful for any always-watching mode. |
| `PrintWindow(PW_RENDERFULLCONTENT)` | Bring-up / fallback for occluded windows |
| GDI `CopyFromScreen`, `mss`, Pillow | Simplest, slowest; what the .NET and Python ports actually ship |

Both mature MCP servers cap screenshots (1920×1080 or a 1568 px longest side) and expose a scale
factor, because the real constraint is tokens and the host's payload limit, not capture speed.

### Accessibility / semantics

UIA (via `comtypes` in Python, first-class in .NET/WinRT) is the backbone; `IAccessible2` for Firefox;
CDP for Chromium and Electron. Windows-MCP's `use_dom=True` mode filters browser chrome out of the
tree for web pages — the same trick OpenClicky's `BrowserTabLocator.swift` does on macOS.

### The Windows agent platform (new, and the strategically important part)

- **MCP on Windows + the On-Device Agent Registry (ODR)**: an OS-level registry for discovering local
  and remote MCP servers, with per-server containment by default, user/admin (Intune) access control,
  logging, an `odr.exe` CLI, and in-box connectors (File Explorer, Settings). Docs are still marked
  pre-release. Consequence: a Windows OpenClicky should *speak MCP in both directions* — register its
  own capabilities and consume ODR servers — rather than invent a private action protocol.
- **App Actions on Windows** (shipping in the Windows SDK since 10.0.26100.4188): apps register units
  of behaviour that Windows and other apps can invoke, via URI activation or an `IActionProvider` COM
  interface. **Requires MSIX package identity.** This is the native version of OpenClicky's fast
  local-action lane, and it is a reason to consider MSIX packaging.
- **Microsoft Execution Containers / agent workspace, Entra agent identity, Windows 365 for Agents** —
  enterprise-shaped, Build-2026 announcements; not a dependency for a consumer tool, but they say
  where the sandboxing conversation is going.

### On-device AI

- **Windows AI APIs** (Windows App SDK, Copilot+ PCs): ready-made **Text Recognition (OCR)**, image
  description, text intelligence. Use this instead of bundling Tesseract (the Python port needs a
  user-installed Tesseract binary on `PATH`) or RapidOCR, when available; keep an ONNX OCR fallback
  for non-Copilot+ machines.
- **Phi Silica** — NPU-tuned SLM for the cheap classify/gate step (OpenClicky's router). Reported to
  be superseded by "Aion Instruct" rolling out Oct–Nov 2026; verify before depending on the name.
- **Foundry Local** (GA, SDKs for NuGet/pip/npm/Rust) and **Windows ML** for bring-your-own-model.

### Voice

OpenClicky's in-process OpenAI Realtime session ports conceptually, but the free/local lane that the
surviving Windows port leaned on is now good: **Parakeet TDT 0.6B v3** (CPU ONNX, very high RTFx) or
`faster-whisper` for STT; **Kokoro-82M** or Piper for TTS (Edge TTS if a free cloud voice is
acceptable); Porcupine/openWakeWord for a wake word. Audio itself is WASAPI — NAudio
(`WasapiCapture`/`WasapiOut`) in .NET, `sounddevice` in Python.

### Grounding models, if `point_at` ever needs a local vision fallback

Open weights on ScreenSpot-Pro: GUI-Owl-1.5-32B (72.9 %, 80.3 % with crop refinement), Rex-Omni (3B,
best in class at that size), UI-Venus-7B/72B, Qwen2.5-VL-7B as the general baseline. Anthropic's
computer toolset (`computer_toolset_20260801`) is GA and client-side — the model returns actions, your
code executes them, which is why the Windows work is mostly an input layer.

### Distribution

- **Azure Artifact Signing** (formerly Trusted Signing): ~$10/month, GA, FIPS 140-2 L3 HSM,
  short-lived certs, GitHub Actions integration — but restricted to US/CA/EU/UK businesses;
  self-employed individuals are now eligible without a 3-year history. **Check whether a NZ-based
  publisher qualifies before planning on it**; if not, the fallback is a conventional OV/EV cert.
- Installer: Inno Setup (python port) or Squirrel (electron port) for a plain exe; **MSIX** if App
  Actions, Store distribution, or ODR registration matter. WinGet for a `winget install` path.

---

## 5. What to build

### The reuse story is better than it looks

Only the Swift shell is macOS-bound. `backend/` (Hono, Node or Workers) is platform-neutral.
`agent/` has exactly one macOS dependency: `agent/src/screenshot.ts` shells out to `screencapture`
(6 references, plus one `process.platform` check in `config.ts`). Skills already live in a
platform-neutral `~/.openclicky/skills` layout shared by Swift and TS, and Codex runs on Windows.

So the Windows project is: **a native shell + a screenshot provider + a UIA locator**, talking to the
backend and agent that already exist. Not a second product.

### Stack recommendation: C# / .NET 8+ with a WPF (or WinUI 3) overlay

| Stack | For | Against |
|---|---|---|
| **C# / .NET** ✅ | UIA, WinRT, WGC, App Actions, Windows AI APIs, NAudio are all first-class and unwrapped; single self-contained exe; the only stack that reaches MSIX + App Actions + ODR cleanly; the two living Windows automation servers with the best UIA hygiene are Python-comtypes and C# | Nothing shared with the existing TS code; the one .NET Clicky port died (19★, but of neglect, not of technical failure — its README is a competent API map) |
| Electron + TS | Maximum code reuse with `agent/`; fastest to a working HUD | The transparent/click-through/topmost overlay is the flakiest of the three (see §3); native input needs a bridge (their answer was a long-lived PowerShell process); heavy install; that port is dead |
| Python + PyQt6 | The one that actually won an audience; `hybrid_pointer.py` is directly liftable | Runtime + PyInstaller + local-model bundle is a big install; the tracker is full of packaging bugs; nothing shared with OpenClicky's code |

Pragmatic middle path if the C# cost looks too high: **write the locator/actions layer in C# as a
small sidecar** (the thing that must be native — UIA, WGC, SendInput, WASAPI) and keep the HUD and
orchestration wherever it is cheapest. That is effectively what Windows-MCP and mcp-windows already
are, and it is testable headlessly.

### The HUD has no notch

Windows has no notch to live in, and that is OpenClicky's whole visual identity (512×232 island,
606×115 card). Options, in rough order of how native they feel: a top-centre floating pill that
docks to the screen edge (closest to the current design, and Windows 11's rounded corners make it
read as intentional); a taskbar-adjacent island near the system tray; tray icon + hotkey only, HUD
appears on demand; binding to the **Copilot key** on new keyboards. This needs a design decision
before any code — and remember the auto-hidden-taskbar bug above.

### Phasing

**P0 — talk, see, point.** Tray app + configurable push-to-talk (full keyboard hook, not
`RegisterHotKey`) → WGC capture (cursor capture off) → existing backend `/chat` → TTS → overlay buddy
with the bezier flight. `point_at` resolves UIA → OCR → grounder, reusing the macOS ordering.
Acceptance: the pointing accuracy bench that already exists for macOS (`point_at` within N px), run
per-tier, plus the auto-hide-taskbar and multi-monitor/mixed-DPI cases that broke others.

**P1 — dictation and the fast local-action lane.** `FrontAppTextInserter` equivalent (UIA
`ValuePattern` first, `SendInput` second, with the 15 ms keystroke delay). Local verbs (`open_app`,
`open_url`, `create_folder`, `reveal_in_explorer`, `set_volume`, `media_control`) — and unlike macOS,
consider App Actions/ODR for the app-specific ones. **Measure p95 this time**; the macOS fast lane
is still an unmeasured claim (see [HANDOVER-2026-09-13.md](HANDOVER-2026-09-13.md)).

**P2 — the agent lane, safely.** Codex over the existing JSON-RPC bridge, plus a Windows-only
differentiator: adopt trope-cua's receipt contract (`background_safe`, `cursor_moved`,
`foreground_changed`), prefer UIA element actions, use CDP for Chromium, and refuse with named
blockers instead of grabbing the user's mouse. Redundant abort from day one.

**P0.5, cheap and high-leverage: a zero-key path.** Ollama/LM Studio + `faster-whisper` or Parakeet +
Kokoro/Edge TTS behind the existing provider abstraction. This is the single clearest lesson from the
three ports, it doubles as the privacy story against Copilot Vision, and most of it is backend-side
work that macOS would inherit too.

### Do not change

The notch-style single-surface HUD idea, backend-holds-the-keys, skills in `~/.openclicky/skills`,
and the buddy-as-intent-overlay (never the hardware cursor — trope-cua independently arrived at the
same rule). And do not regress the secrets hygiene the macOS app just fixed.

---

## 6. Open questions this research cannot answer

1. **Windows shell stack** — C# sidecar + which HUD host? This is the one blocking decision.
2. **Where does the HUD live** on a notch-less OS?
3. **Zero-key lane: yes or no?** It contradicts the hosted-credits model but is the strongest survival
   signal in the data.
4. **MSIX or plain exe?** MSIX unlocks App Actions and ODR registration; it also sandboxes the child
   processes the agent lane needs (Codex, the CLI).
5. **Does a NZ publisher qualify for Azure Artifact Signing?** If not, budget for an OV/EV cert.
6. **Reddit pass** — worth doing in a browser: `site:reddit.com clicky windows`, and
   r/Windows11 / r/LocalLLaMA / r/SideProject / r/accessibility for "screen assistant", "voice
   control Windows", "UI automation agent". The accessibility subreddits are probably the highest-value
   unread source, since Fluent's free-tier framing suggests that is where the real demand is.

---

## Sources

Repos (cloned, pinned SHAs in [`reference/windows/README.md`](../reference/windows/README.md)):
[Bitshank-2338/clicky-windows](https://github.com/Bitshank-2338/clicky-windows) ·
[tekram/clicky-windows](https://github.com/tekram/clicky-windows) ·
[emreyilmaz46/clicky_windows](https://github.com/emreyilmaz46/clicky_windows) ·
[CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) ·
[sbroenne/mcp-windows](https://github.com/sbroenne/mcp-windows) ·
[voctory/trope-cua](https://github.com/voctory/trope-cua) ·
[bytedance/UI-TARS-desktop](https://github.com/bytedance/UI-TARS-desktop) ·
[microsoft/OmniParser](https://github.com/microsoft/OmniParser) ·
[QwenLM/open-computer-use](https://github.com/QwenLM/open-computer-use)

Platform docs: [MCP on Windows overview](https://learn.microsoft.com/en-us/windows/ai/mcp/overview) ·
[App Actions on Windows](https://learn.microsoft.com/en-us/windows/ai/app-actions/) ·
[Windows AI / Foundry on Windows](https://learn.microsoft.com/en-us/windows/ai/overview) ·
[Phi Silica](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica) ·
[Code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) ·
[SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) ·
[Azure Artifact Signing](https://azure.microsoft.com/en-us/products/artifact-signing) ·
[Build 2025 Windows AI platform](https://blogs.windows.com/windowsdeveloper/2025/05/19/advancing-windows-for-ai-development-new-platform-capabilities-and-tools-introduced-at-build-2025/) ·
[Build 2026 Windows agent platform analysis](https://zylos.ai/research/2026-06-24-windows-ai-agent-platform-build-2026/) ·
[App Actions announcement](https://www.windowscentral.com/software-apps/windows-11/windows-11-app-actions-api-announcement-build-2025)

Products / community: [Fluent](https://fluentforall.com/) ·
[HN: Fluent](https://news.ycombinator.com/item?id=49118239) ·
[HN: background computer use for Windows](https://news.ycombinator.com/item?id=47951937) ·
[HN: Cyberdesk](https://news.ycombinator.com/item?id=44901528) ·
[HN: "Is there a genuine space for the Desktop AI assistant?"](https://news.ycombinator.com/item?id=49645248) ·
[Copilot Vision coverage](https://www.tomsguide.com/ai/microsoft-is-hiding-windows-11s-eyes-heres-how-to-find-copilot-vision-and-fully-delete-it) ·
[Ask Copilot / Click to Do timing](https://www.windowslatest.com/2026/05/27/microsoft-confirms-ask-copilot-is-coming-to-the-windows-11-taskbar-in-mid-2026/)

Tech: [DXcam](https://github.com/ra1nty/DXcam) ·
[ScreenSpot-Pro](https://www.emergentmind.com/topics/screenspot-pro) ·
[UI-TARS paper](https://arxiv.org/pdf/2501.12326) ·
[MEGA-GUI](https://arxiv.org/pdf/2511.13087) ·
[UI-Zoomer](https://arxiv.org/pdf/2604.14113) ·
[open-source STT benchmarks 2026](https://northflank.com/blog/best-open-source-speech-to-text-stt-model-in-2026-benchmarks) ·
[local voice-AI model survey](https://d-central.tech/local-voice-ai-models/)
