# Linux desktops: on-device language models and the de facto alternatives (as of 2026-10-27)

Scope: whether any Linux distribution or desktop environment ships a first-party on-device LLM API comparable to Apple Foundation Models or Windows Phi Silica; what the practical routes are for a Python + GTK 3 app on X11 to discover and use a local model with no API key; small-model behaviour on CPU-only laptops; local-first/cloud-fallback assistants; accessibility tooling paired with local LLMs.

Method note: ~35 web searches/fetches on 2026-10-27. Primary sources (project docs, GitHub, vendor blogs, Fedora/KDE/GNOME community posts) were preferred; where only secondary blogs were available this is flagged. Several "tokens/sec" figures below are self-described *estimates*, not measurements — marked as such.

---

## Key Question 1: Does any Linux desktop ship a first-party on-device model API today (2026)? If not, what is the closest thing and what has been announced?

### Takeaway
No Linux distribution or desktop environment ships a system-wide, key-free on-device LLM API that third-party apps can call the way Apple Foundation Models or Phi Silica work. The closest shipped thing is Canonical's "inference snaps" (public beta since 2025-10-23): confined snaps that bundle a model plus a hardware-matched runtime and expose a "local and standard" inference endpoint — a developer-facing packaging mechanism, not a desktop assistant or a stable OS API. GNOME and KDE have spent 2025–2026 debating AI *contribution* policies rather than shipping AI features; the only "AI-native desktop" proposal in KDE (Kadai, Akademy, Sept 2026) is explicitly not a roadmap item.

### Cited Findings

**Canonical / Ubuntu (shipped: inference snaps; announced: careful on-device strategy)**
- On 2025-10-23 Canonical announced "inference snaps": "optimized inference snaps, a new way to deploy AI models on Ubuntu devices, with automatic selection of optimized engines, quantizations and architectures based on the specific silicon of the device." One command detects hardware "and uses OpenVINO ... to deploy a recommended model variant onto the most suitable device." Launch beta models: DeepSeek R1 and Qwen 2.5 VL (`sudo snap install deepseek-r1 --beta`, `sudo snap install qwen-vl --beta`), with Intel- and Ampere-optimised builds. Developers get "local and standard inference endpoints"; the post gives no port or protocol and makes no statement about desktop integration — [Canonical blog, 2025-10-23](https://canonical.com/blog/canonical-releases-inference-snaps)
- The `canonical/inference-snaps` GitHub repo states the model "runs entirely on your machine once installed" and that the snaps "expose local standard APIs"; its README (as of the 2026-10-27 search) lists snaps for DeepSeek R1, Gemma 3 and 4, GLM 4.7 Flash, Qwen 3.6 and 3.8, and SmolLM2 (`snap install smollm2`), "more open-source models are coming" — [canonical/inference-snaps](https://github.com/canonical/inference-snaps)
- InfoQ (2026-05-16), citing the Ubuntu Discourse post "The future of AI in Ubuntu" by Canonical engineer Jon Seager: Ubuntu will prioritise "on-device intelligence, modular design, and user control", favour open-weight models, and split AI into "implicit" (e.g. speech-to-text improving existing features) and "explicit" (user-facing agentic workflows such as document authoring and automated troubleshooting). Inference snaps are the delivery vehicle (example: `nemotron-3-nano`), subject to snap confinement. Seager says there will likely be no "global AI killswitch"; users remove AI features by uninstalling the corresponding snap. The article names **no system-wide AI API and no named assistant** and no Ubuntu version — [InfoQ, 2026-05-16](https://infoq.com/news/2026/05/ubuntu-on-device-ai)
- A secondary 2026 round-up states Canonical has distributed models as inference snaps since October 2025 and reports no Canonical AI *contribution* policy — [pasqualepillitteri.it, 2026](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)
- A HowToGeek article's reader comment mentions Canonical testing "an AI dictation app for Ubuntu"; this is unverified user comment, not reporting — [HowToGeek, 2026](https://www.howtogeek.com/kde-is-fighting-over-whether-ai-belongs-in-linux-heres-whats-at-stake/)

**GNOME (nothing shipped; contribution-policy debate)**
- GNOME contributor Jordan Petridis ("alatiera") posted "The GNOME LLM Policy That I Want" on 2026-09-23 proposing that "LLMs ('AI') can not be used to create or modify anything submitted to GNOME, or hosted on GNOME infrastructure" — explicitly a personal draft, not adopted policy — [blogs.gnome.org/alatiera, 2026-09-23](https://blogs.gnome.org/alatiera/2026/09/23/the-gnome-llm-policy-that-i-want/)
- Diego Escalante Urrelo (2026-09-25) notes GNOME "has not proposed anything official, but some teams (gnome-calendar, loupe, libadwaita, gnome-software, Circle, among others) already have strong policies in place" — [diegoe.be, 2026-09-25](https://diegoe.be/2026/09/25/llm-policies-progress-at-all-costs/)
- Michael Catanzaro (2026-06-08) argued against banning AI-assisted *issue reports* — illustrating that the GNOME debate is about contributions, not features — [blogs.gnome.org/mcatanzaro, 2026-06-08](https://blogs.gnome.org/mcatanzaro/2026/06/08/please-do-not-ban-ai-assisted-issue-reports/)
- GNOME Circle rejects AI-generated applications; "the use of autocomplete is permitted, but developers must fully understand and justify their application code" (secondary report) — [ubunlog](https://en.ubunlog.com/GNOME-Circle-bans-artificial-intelligence-applications/)
- GNOME Extensions review rejects "obviously generated code" per a 2025-12-06 post by reviewer Javad Rahmatzadeh (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)
- No search result mentioned any GNOME Foundation or GNOME Shell initiative to ship a local model, a system assistant, or a D-Bus/portal LLM API.

**KDE (nothing shipped; Kadai proposal; policy draft; Alpaka client unreleased)**
- At Akademy (September 2026) Eva Brucherseifer and Jan Muehlig presented "A lovable, sovereign, AI-native KDE", proposing "Kadai" — AI workspaces/workflows integrated into KDE with approval gates and controls over what an agent may access; slides offered "a choice of local AI, other models, or no AI". HowToGeek: "This isn't a KDE roadmap or anything, it's just a conference proposal." — [HowToGeek, 2026](https://www.howtogeek.com/kde-is-fighting-over-whether-ai-belongs-in-linux-heres-whats-at-stake/)
- Nate Graham's September 2026 kde-devel suggestions (not adopted): human-in-the-loop, output "functionally indistinguishable" from unassisted work, "don't be lazy"; open disclosure of LLM use discouraged. The Discourse/GitLab discussion was locked after a heated clash — [HowToGeek](https://www.howtogeek.com/kde-is-fighting-over-whether-ai-belongs-in-linux-heres-whats-at-stake/); [XDA, 2026](https://www.xda-developers.com/kdes-proposed-llm-policy-discussion-gets-locked-after-a-fiery-community-clash/)
- KDE wiki update of 2026-05-25 asks contributors to declare AI tools in merge requests but excludes Co-authored-by/Assisted-by tags from commits (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)
- **Alpaka** (apps.kde.org): "a client for the Ollama AI service", Utilities category, GPL-3.0-or-later, "still in development and not yet released by the KDE community"; no version or date — [apps.kde.org/alpaka](https://apps.kde.org/alpaka/)
- **krunner-llm** (TibixDev, third-party): KRunner plugin that "answers natural-language questions from your launcher by calling any OpenAI-compatible chat completions API", including "local Ollama, LM Studio, etc." Not a KDE feature — [github.com/TibixDev/krunner-llm](https://github.com/TibixDev/krunner-llm)
- daily.dev summary: "KDE has not announced KRunner as a universal local-AI assistant" — [daily.dev](https://daily.dev/posts/no-kde-isn-t-turning-into-an-ai-desktop-s9j3caouz)

**Fedora (policy adopted; SIG projects, no OS API)**
- Fedora Council decision of 2025-10-22: AI assistance must be declared with an `Assisted-by` tag; the contributor is responsible; AI may not be the sole reviewer (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)
- Fedora AI/ML SIG 2026 activity is RamaLama-based Outreachy projects: `packaging-guide-ramalama` approved April/May 2026 ("improving RPM packaging using RamaLama RAG"); `editorial-guide-ramalama` v1.0.0 released 2026-07-13, announced on the Community Blog August 2026 as running "locally, using open-weight models, requiring no API keys" — [Fedora Discussion, 2026-04-23 meeting](https://discussion.fedoraproject.org/t/fedora-ai-ml-sig-meeting-2026-04-23/190761); [Fedora Forge releases](https://forge.fedoraproject.org/ai-ml/editorial-guide-ramalama/releases); [Fedora Community Blog, Aug 2026](https://communityblog.fedoraproject.org/meet-editorial-guide-ramalama-an-ai-assistant-that-checks-your-fedora-commblog-and-magazine-articles-against-editorial-guidelines/)
- A March 2026 Fedora Discussion user reports RamaLama RPMs exist but lag upstream — [Fedora Discussion](https://discussion.fedoraproject.org/t/ramalama-rag/184105)

**SUSE / openSUSE**
- SLES 16 (available since 2025-11-04) includes an MCP host and server as a technology preview — enterprise, not openSUSE desktop, and MCP is a tool-connection protocol, not a model (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)

**Debian**
- GR vote 2026-08-15 to 2026-08-28 chose "Option 5: Responsible Use of Generative AI": neither promotes nor bans; declaring AI use encouraged, not mandatory (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)

**Pop!_OS / COSMIC, Zorin, elementary**
- COSMIC 1.4.0 "Epoch" released 2026-07-23; Pop!_OS 24.04 LTS shipped COSMIC Epoch 1 on 2025-12-11 per LWN/Wikipedia (TurboGeek gives 2026-03-03; conflicting). No AI feature appears in any COSMIC/Pop!_OS release coverage — [UbuntuHandbook, 2026-07](https://ubuntuhandbook.org/index.php/2026/07/cosmic-desktop-1-4-0-enhanced-system-monitor/); [LWN](https://lwn.net/Articles/1050268/)
- Zorin OS 18 (released October 2025, Ubuntu 24.04 base) highlights tiling, panels, web apps — no AI feature. A Zorin forum thread "Do the devs plan on adding AI to Zorin?" contains only user opinions ("AI integration into Zorin OS is very unnecessary"); one user runs Alpaca as a third-party app — [Linuxiac](https://linuxiac.com/zorin-os-18-released-with-refreshed-ui-and-smart-window-tiling/); [Zorin forum](https://forum.zorin.com/t/do-the-devs-plan-on-adding-ai-to-zorin/44782)

**Mozilla / Firefox (shipped in-browser on-device AI, not an OS API)**
- Firefox link previews generate "AI key points" on-device using wllama (WebAssembly llama.cpp) running SmolLM2-360M; Firefox 142 made it optional, limited to English locales in US/CA/UK/AU and to machines with >3 GB available RAM; models are managed under about:addons "On-Device AI" — [Mozilla blog](https://blog.mozilla.org/en/mozilla/ai/ai-tech/ai-link-previews-firefox/); [Mozilla Connect, Firefox 142](https://connect.mozilla.org/t5/discussions/try-link-previews-in-firefox-142-see-what-s-behind-a-link-before/m-p/103496)
- Firefox 142 also lets extensions use wllama for LLM features — [OMG! Ubuntu, 2025-04](https://www.omgubuntu.co.uk/2025/04/firefox-nightly-introduces-experimental-link-previews-with-ai-summary)
- Firefox on-device translation is Project Bergamot, full-page in 20+ languages — [ApexGear summary](https://apexgear.blog/does-firefox-use-ai)
- Mozilla's own testing: first key point within ~4 s for most users; an early tester reported ~10 s — [Mozilla blog](https://blog.mozilla.org/en/mozilla/ai/ai-tech/ai-link-previews-firefox/); [It's FOSS](https://news.itsfoss.com/firefox-ai-link-previews)

**Kernel context**
- Since 2025-12-23 the kernel bars AI assistants from adding Signed-off-by; `Assisted-by:` tag shortened to "Assisted-by: LLM [TOOL...]" on 2026-07-01 (secondary report) — [pasqualepillitteri.it](https://pasqualepillitteri.it/en/news/17291/linux-ai-2026-what-kernel-and-distros-decided)

### Inferences
- The Linux desktop ecosystem's institutional energy in 2025–2026 went into *governing AI-generated contributions*, not into shipping assistant features. For an app vendor this means there is no platform API to target and none plausibly arriving within 12 months; the burden of discovering/shipping a runtime falls on the app.
- Canonical's inference snaps are the only distro-level mechanism that could evolve into a "system model" story, and Canonical's stated direction ("implicit" AI improving speech-to-text etc.) is the closest analogue to Apple/Microsoft's platform approach. But as of 2026-10 it is a packaging format with per-model snaps, confined, developer-targeted, and the endpoint contract is undocumented in the sources found.
- GNOME's project-level bans on LLM-generated *code* do not prohibit apps that *use* LLMs, but a GTK app that wants GNOME Circle or Flathub-verified status should expect scrutiny; Newelle and Alpaca show LLM-client apps coexist with GNOME.

### Gaps
- Could not find the actual API contract (port, OpenAI compatibility, discovery method) of inference snaps; `documentation.ubuntu.com/inference-snaps` was not fetched.
- No primary source found for GNOME Circle's AI policy wording (only ubunlog).
- Nothing found on elementary OS and AI; nothing on openSUSE desktop (only SLES).
- Alpaka's version, release status in KDE Gear/incubator, and whether it supports structured output are unknown.
- Mozilla.ai (the Mozilla subsidiary): no search result connected it to Firefox on-device features or Linux desktop; treat as unresearched.
- No 2026-dated source on Firefox on-device AI changes was found; the newest is Firefox 142 (Aug 2025).

---

## Key Question 2: For a Python + GTK 3 app, what is the least-friction way to detect and use a locally running model with no key (Ollama, llama.cpp, llamafile, Foundry Local)? Download sizes, RAM, tokens/sec on an 8–16 GB no-GPU laptop, cold start.

### Takeaway
Ollama is the de facto Linux "model service": one install command, a systemd unit (`ollama serve`) on `http://localhost:11434`, an OpenAI-compatible `/v1` surface, and a stable `/api/chat` with JSON-schema structured output and tool calling — detection is an HTTP GET to that port. llama.cpp's `llama-server` (default `127.0.0.1:9931`, `/health`, `-hf` auto-download, grammar/JSON-schema sampling) is the right choice when the app wants to *bundle* a runtime and a 2–3 GB GGUF itself. llamafile (0.10.x, 2026) is a viable single-file fallback. Foundry Local on Linux is a tarball CLI preview (`cli-preview-0.10.0`, tested only on Ubuntu 26.04 x64) and not yet a dependable target. Realistic CPU throughput for 3–4B Q4 models on dual-channel laptop RAM is ~15–30 tok/s; on single-channel DDR4 older laptops 5–10 tok/s (estimates).

### Cited Findings

**Ollama**
- Linux install: `curl -fsSL https://ollama.com/install.sh | sh`. Manual install: `ollama-linux-amd64.tar.zst` (x86-64), `ollama-linux-amd64-rocm.tar.zst` (AMD GPU add-on), `ollama-linux-arm64.tar.zst`, extracted into `/usr`. Service: create `ollama` user/group, `/etc/systemd/system/ollama.service` with `ExecStart=/usr/bin/ollama serve`, `Restart=always`, `WantedBy=multi-user.target`; verify with `systemctl status ollama`, logs via `journalctl -e -u ollama`; pin versions via `OLLAMA_VERSION=0.5.7`; models live in `/usr/share/ollama`. Tarball sizes are not stated on the page — [docs.ollama.com/linux](https://docs.ollama.com/linux)
- API: local server at `http://localhost:11434`; native paths under `/api` (e.g. `/api/chat`); OpenAI-compatible base `http://localhost:11434/v1`; Anthropic-compatible base `http://localhost:11434` (client appends `/v1/messages`). "Ollama's API isn't strictly versioned"; deprecations announced in release notes — [docs.ollama.com/api](https://docs.ollama.com/api)
- Structured outputs: pass a JSON schema in the `format` parameter of a chat request (or `"json"` for unstructured JSON); Python example uses pydantic `model_json_schema()` and `model_validate_json()`; also available via OpenAI-compatible `response_format`; recommended to also put the schema in the prompt and set temperature 0; vision models accept the same `format`. The page does not describe the decoding mechanism — [docs.ollama.com/capabilities/structured-outputs](https://docs.ollama.com/capabilities/structured-outputs)
- Tool calling: `tools` list of `{type: "function", function: {name, description, parameters}}`; model replies with `tool_calls`; return results as `role: "tool"` messages; parallel tool calls and streaming tool-call accumulation documented; Python SDK (`pip install ollama`) builds schemas from Python functions' type hints/docstrings; examples use `qwen3` with `think=True`. The page lists no per-model support matrix — [docs.ollama.com/capabilities/tool-calling](https://docs.ollama.com/capabilities/tool-calling)
- Defaults relevant to CPU users (secondary): Ollama unloads a model after 5 minutes idle by default (`OLLAMA_KEEP_ALIVE=30m` avoids reloads); default context 4096 unless `OLLAMA_CONTEXT_LENGTH` set; "a very long window can take minutes to fill before the first token appears" on CPU — [PromptQuorum, updated 2026-10-23](https://www.promptquorum.com/local-llms/best-cpu-only-llm)
- Ollama issue #14579 reports Qwen3.5 running "much slower" in Ollama than in llama.cpp (performance parity is not guaranteed between the two) — [ollama/ollama#14579](https://github.com/ollama/ollama/issues/14579)

**llama.cpp server**
- `llama-server` "will start a server that by default listens on `127.0.0.1:9931`" (`--port`, default 9931). `GET /health` (also `/v1/health`) needs no API key; returns 503 while loading, 200 `{"status":"ok"}` when ready. "OpenAI API compatible chat completions, responses, and embeddings routes"; `/v1/models` lists loaded models. `--grammar` ("BNF-like grammar to constrain generations"), `-j, --json-schema` and per-request `grammar`/`json_schema` fields ("Set a JSON schema for grammar-based sampling"). "Function calling / tool use for ~any model" (docs/function-calling.md). `--jinja` enabled by default; `--chat-template` built-ins include llama3, chatml, mistral-v7, gemma, deepseek. `-hf <user>/<model>[:quant]` downloads from Hugging Face, default quant Q4_K_M — [ggml-org/llama.cpp tools/server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Note: the default port 9931 in the current README differs from the long-standing 8080 documented in older material; check the version you ship (inference from the README text).

**llamafile (Mozilla-AI)**
- 0.10.0 (19 Mar 2026) introduced a new build system to track recent llama.cpp more closely; "may lack some features from the earlier 'classic' experience". 0.10.1 (1 May): Vulkan dylibs, CUDA/ROCm/Vulkan as .so/.dll, fixed Linux `GLIBCXX_3.4.32` CUDA error, synced llama.cpp for Gemma 4/Qwen 3.6. 0.10.2 (29 May): GPU probe, embedded web UI. 0.10.3 (2 Jun): fixed crash on GPU init failure, restored CPU fallback. 0.10.4 (16 Jul): HTTPS, HF download by ID, pledge/SECCOMP sandboxing back. 0.10.5 (3 Aug), 0.10.6 (15 Sep, latest; llama.cpp b10441) — [mozilla-ai/llamafile releases](https://github.com/mozilla-ai/llamafile/releases); [Help Net Security, 2026-03-20](https://www.helpnetsecurity.com/2026/03/20/llamafile-0-10-0-released/)

**Foundry Local (Microsoft)**
- Official quickstart (ms.date 2026-06-04, updated 2026-10-19): cross-platform SDKs (`pip install foundry-local-sdk openai`, Python 3.11+; also C#, JS, Rust) with a native chat-completions API; Windows package integrates Windows ML; "For cross platform builds there is no dynamic EP download"; example model alias `qwen2.5-0.5b`; no OS support matrix or Linux install instructions on this page — [Microsoft Learn: Foundry Local get started](https://learn.microsoft.com/en-us/azure/foundry-local/get-started)
- GitHub discussion #61: accepted answer (2026-06-17) points to "CLI for Linux" release `cli-preview-0.10.0`; a user installed `foundry-0.10.0-linux-x64.tar.gz` on Ubuntu 26.04 x64, ran `foundry server start`, and got phi-4-mini working "with tool calling on CPU"; the Node SDK does not bundle `Microsoft.AI.Foundry.Local.Core.so` and expects the CLI installed; install steps "not yet in the official docs"; Debian/Fedora support requested (2026-02-12) but unconfirmed; a user cited an April 9 (2026) GA post calling the product cross-platform and asked where the Linux CLI was — [microsoft/foundry-local discussion #61](https://github.com/microsoft/foundry-local/discussions/61)
- GA date conflict: a Miraheze wiki page says GA 2026-04-09 with v1.0.0 and lists 2.0.1 (expanded Linux/ARM64 support) as latest through Sept 2026; a news piece says GA at Build 2026. Neither verified against Microsoft's repo — [ai.miraheze.org](https://ai.miraheze.org/wiki/Foundry_Local); [krasa.ai](https://www.krasa.ai/news/microsoft-foundry-local-general-availability-on-device-ai-build-2026)

**NPU paths on Linux (for completeness; not recommended for the target)**
- Intel NPU driver mainline since kernel 6.3, used through OpenVINO; Arch packages `intel-npu-driver 1.38.0` / `openvino-intel-npu-plugin 2026.4.1`; LLMs must be exported to INT4 OpenVINO IR with static-shape limits (secondary) — [markaicode](https://markaicode.com/offload-llm-inference-cpu-to-integrated-npu/); [localaimaster](https://localaimaster.com/blog/run-llm-on-npu)
- AMD `amdxdna` driver mainline since kernel 6.14; practical LLM use arrived March 2026 via Lemonade 10.0 + FastFlowLM, requiring kernel 7.0 or backports; XDNA 1 (Ryzen 7000/8000) unsupported, XDNA 2 (Strix, Strix Halo, Kraken, Gorgon Point) supported — [lemonade-server.ai FLM Linux](https://lemonade-server.ai/flm_npu_linux.html); [agent-wars, 2026-03-14](https://agent-wars.com/news/2026-03-14-amd-ryzen-ai-npu-linux-lemonade-10-fastflowlm)

**Sizes and CPU throughput (estimates unless stated)**
- PromptQuorum (updated 2026-10-23, "estimates, not lab measurements", derived from bandwidth): Phi-4 Mini 3.8B Q4 file 2.5 GB, min RAM 3 GB, comfortable 6 GB, 15–20 tok/s dual-channel DDR4-3200, 25–32 DDR5-5600, **7–10 single-channel DDR4-3200**; Llama 3.2 3B file 2.0 GB, 18–30 tok/s; Gemma 4 E2B file 4.6 GB, 8–14 tok/s ("least certain"); Qwen3 8B 5.2 GB, 8–15 tok/s. Ceiling formula: tok/s ≤ memory bandwidth ÷ file size (51.2 GB/s dual-channel DDR4-3200; 89.6 GB/s DDR5-5600; 25.6 GB/s single-channel DDR4-3200); real speed 75–100% of ceiling. Older 8–10 GB single-channel laptops: 5–10 tok/s for Phi-4 Mini / Llama 3.2 3B — [PromptQuorum best CPU-only LLM](https://www.promptquorum.com/local-llms/best-cpu-only-llm)
- Same site's low-end guide estimates Qwen3 1.7B Q4_K_M at 25–40 tok/s on 4–8 GB CPU-only PCs — [PromptQuorum low-end PCs](https://www.promptquorum.com/local-llms/fastest-local-llms-low-end-pcs)
- Self-reported measurements: the Janas project (its own numbers) reports Qwen3-4B at 27.9 tok/s (Janas) vs 17.9 tok/s (llama.cpp) on a 20-thread laptop CPU — [prabanta-dev/janas](https://github.com/prabanta-dev/janas). A llama.cpp discussion log shows Qwen3-4B-Instruct-2507 Q4_K_M at 61.9 tok/s on a 32 GB laptop, but whether GPU offload was involved is unclear — [llama.cpp discussion #21112](https://github.com/ggml-org/llama.cpp/discussions/21112)
- Gemma 3n on Ollama: `gemma3n:e2b` download 7.8 GB, `gemma3n:e4b` 9.8 GB (effective 2B/4B params, 32K context, text+image) — notably larger downloads than their "effective" size suggests — [ollama.com/library/gemma3n](https://ollama.com/library/gemma3n)
- Firefox's bar for on-device summarisation is >3 GB available RAM with a 360M model — a useful floor for "works on old hardware" — [Mozilla Connect](https://connect.mozilla.org/t5/discussions/try-link-previews-in-firefox-142-see-what-s-behind-a-link-before/m-p/103496)

### Inferences
- Detection recipe for the Python app, in order: (1) `GET http://127.0.0.1:11434/api/tags` (Ollama; also honour `OLLAMA_HOST`); (2) `GET http://127.0.0.1:9931/health` and `:8080/health` (llama-server, new and old defaults); (3) `GET http://127.0.0.1:1234/v1/models` (LM Studio, cited by OrcaVision as a common OpenAI-compatible local endpoint); (4) optional `foundry` CLI presence. All of these speak the OpenAI chat-completions shape, so one client class suffices. (Inference from the endpoint facts above.)
- If the app must guarantee a model exists, bundling or downloading a ~2–2.5 GB Q4 GGUF (Llama 3.2 3B / Phi-4-mini / Qwen3-4B) and launching `llama-server -hf ... --port <ephemeral>` is lower-friction than requiring the user to install Ollama; `-hf` handles the download and `/health` gives a readiness signal. llamafile 0.10.x gives the same with a single executable but a less predictable feature set.
- Expect a cold start of seconds-to-tens-of-seconds for a 2–3 GB file on an older laptop with a spinning or slow SSD (no sourced figure; see Gaps) plus the Ollama 5-minute unload default, so the app should keep the server warm (`OLLAMA_KEEP_ALIVE`) or own the process.
- For older users' machines (8 GB, often single-channel), 7–10 tok/s with a 3–4B model means a 60-token rewrite takes ~6–9 s after prompt processing — acceptable for rewrite/classify, marginal for chat. A 1.7B model roughly doubles that.

### Gaps
- No source gives Ollama's Linux tarball size or exact disk footprint of its bundled GPU libraries.
- No independent, dated `llama-bench` CPU-only measurement for Qwen3-4B, Qwen3-1.7B, Gemma 3n, or SmolLM3 on an 8–16 GB laptop was found; all figures are estimates or self-reports.
- No sourced cold-start (model load) times in seconds.
- Foundry Local's official Linux support statement, distro list, and current version could not be verified from Microsoft's own pages; the discussion thread is the best evidence.
- llamafile's known Linux `binfmt_misc`/APE execution issues were not covered by the release page fetched.

---

## Key Question 3: Which small models are good at short rewrite/classify/extract-JSON tasks on CPU, and which support structured output (JSON schema / grammar-constrained generation) and tool calling?

### Takeaway
Structured output on Linux is a property of the *runtime*, not the model: both Ollama (`format` = JSON schema) and llama.cpp (`json_schema`/`grammar` sampling) enforce schemas for any GGUF model, and llama.cpp claims function calling "for ~any model". Among 1–4B candidates, Phi-4-mini (3.8B) has the strongest published accuracy and was demonstrated with tool calling on CPU under Foundry Local; Qwen3 is the model Ollama's own tool-calling docs use; the one structured-output benchmark found (Apr 2026) shows Gemma 3 4B at 100% JSON-parse but only 87% schema compliance *without* constrained decoding, which argues for always using runtime-enforced schemas.

### Cited Findings
- Ollama structured outputs: any JSON schema via `format`; "Setting `format` to `"json"` alone returns JSON without enforcing a particular structure"; also `response_format` on `/v1`; examples use `gpt-oss` and `gemma4`; Ollama cloud does not support structured outputs (local does) — [docs.ollama.com/capabilities/structured-outputs](https://docs.ollama.com/capabilities/structured-outputs)
- Ollama tool calling documented with `qwen3` examples; parallel and streaming tool calls supported; "first call only" handling recommended only "for models which only return a single tool call" — [docs.ollama.com/capabilities/tool-calling](https://docs.ollama.com/capabilities/tool-calling)
- llama.cpp server: `json_schema` and `grammar` request fields for "grammar-based sampling"; "Function calling / tool use for ~any model"; Jinja chat templates on by default — [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Foundry Local on Linux: phi-4-mini "working with tool calling on CPU" (user report, 2026-06-17) — [foundry-local discussion #61](https://github.com/microsoft/foundry-local/discussions/61)
- Accuracy comparison (secondary, 2026): Phi-4-mini 3.8B — 67.3% MMLU, 88.6% GSM8K; SmolLM3-3B — 44.1% MMLU, 67.6% GSM8K; no Qwen3-1.7B or Gemma 3n figures — [tech-insider.org](https://tech-insider.org/phi-4-mini-vs-gemma-3-vs-llama-3-2-2026/)
- AscentCore small-LLM benchmark (2026-04-01): `gemma3:4b-q4_K_M` 100% JSON parse rate, 87% schema compliance; Gemma 3 1B much lower schema compliance (covers Gemma 3 and Phi-3, not Gemma 3n/SmolLM3/Qwen3) — [AscentCore, 2026-04-01](https://ascentcore.com/2026/04/01/small-llm-performance-benchmark/)
- Canonical's inference-snaps README lists SmolLM2 and Gemma 4 as snap-packaged models, i.e. Canonical considers them deployable on commodity Ubuntu hardware — [canonical/inference-snaps](https://github.com/canonical/inference-snaps)
- Firefox uses SmolLM2-360M for 3-bullet summaries, demonstrating that sub-1B models suffice for constrained extractive tasks — [Mozilla blog](https://blog.mozilla.org/en/mozilla/ai/ai-tech/ai-link-previews-firefox/)
- Gemma 3n on Ollama is text+image, 32K context; tool support not stated on the library page — [ollama.com/library/gemma3n](https://ollama.com/library/gemma3n)

### Inferences
- For rewrite/classify/extract-JSON on CPU, the pragmatic shortlist is Qwen3-1.7B (speed), Qwen3-4B or Phi-4-mini (quality), Llama 3.2 3B (small file, broad template support). Gemma 3n's 7.8–9.8 GB downloads make it a poor fit for 8 GB machines despite its "effective 2B/4B" branding.
- Always request schema-constrained output from the runtime rather than trusting prompt-only JSON; the 87% schema compliance figure for an unconstrained 4B model is the kind of failure rate that breaks an accessibility assistant.
- Tool calling on 1–2B models is less dependable than on 4B; where the app only needs "pick one of N actions", a JSON-schema enum is more robust than function calling (inference from the docs' caveats about multi-call handling and the lack of a small-model support matrix).

### Gaps
- No per-model tool-calling reliability benchmark for Qwen3-1.7B/4B, Llama 3.2 3B, SmolLM3, or Gemma 3n on CPU.
- Ollama publishes a "tools" capability filter on ollama.com but the docs pages fetched did not enumerate models; not verified here.
- No source on Qwen3 thinking-mode overhead for short tasks on CPU (it matters: `think=True` adds tokens).

---

## Key Question 4: Which open-source Linux desktop assistants implement local-first with cloud fallback, and how do they detect availability and route?

### Takeaway
Several GNOME/GTK assistants support *both* local (Ollama/llama.cpp) and cloud providers — Newelle (GTK4/libadwaita, downloads local models and runs them via llama.cpp or Ollama), Alpaca (GTK4 Ollama client with third-party providers alongside), Bavarder (libadwaita, tagged local-first/LiteRT-LM/Gemma/Qwen) — but none of the sources describe an automatic "local first, cloud fallback" router; provider choice is a user setting. OpenJarvis (CLI framework) is the only project found whose installer explicitly auto-detects an existing Ollama install.

### Cited Findings
- **Newelle** (qwersyk): "Your Ultimate Virtual Assistant" for GNOME; GTK4/libadwaita (per topics); "multiple AI models and providers"; "Download local models directly from Newelle, and run them using Llama.cpp or Ollama"; extensions (`newelle-extension` tag); "Execute commands suggested by the AI on the fly"; supports "skills from Claude, Codex..."; Telegram and API interfaces; mini-window mode (Ctrl+G); Flathub, Nix, GNOME Builder; GPL-3.0; 1,687 commits, 1.5k stars. The README does not explain how Ollama/llama.cpp are detected or any fallback logic — [github.com/qwersyk/Newelle](https://github.com/qwersyk/Newelle)
- **Alpaca** (Jeffser): "An Ollama client made with GTK4 and Adwaita"; multi-model conversations, pull/delete models, image recognition; GPL-3.0; the Debian ITP (June 2025) notes "third party AI providers such as Gemini, ChatGPT and more can be used alongside it" — [Debian ITP #1108419, 2025-06](https://www.mail-archive.com/debian-wnpp@lists.debian.org/msg271487.html); [Alpaca README mirror](https://gitea.kosmos.org/hueso/Alpaca/src/commit/ba66ac40a35d3bfca95998c24bfd6441f034ac9e/README.md); [UbuntuHandbook, 2024-07](https://ubuntuhandbook.org/index.php/2024/07/alpaca-gtk4-app-chat-ai-offline/)
- **Bavarder**: libadwaita/GNOME app, GPL-3.0, topics `local-ai`, `local-first`, `litert-lm`, `gemma`, `qwen`; 1,337 commits; no releases listed; backends not named in README text fetched — [github.com/Bavarder/Bavarder](https://github.com/Bavarder/Bavarder)
- **Alpaka** (KDE): Ollama-only client, unreleased — [apps.kde.org/alpaka](https://apps.kde.org/alpaka/)
- **krunner-llm**: any OpenAI-compatible endpoint including local Ollama/LM Studio — [TibixDev/krunner-llm](https://github.com/TibixDev/krunner-llm)
- **OpenJarvis**: install script "auto-detects your existing Ollama installation", "local-first, and the cloud is optional"; run from terminal as `jarvis` — [Ollama blog: OpenJarvis](https://ollama.com/blog/openjarvis)
- **PyGPT**: Python desktop assistant for Windows/Linux/macOS supporting cloud providers and local Ollama; auto-detection/local-default not stated — [pygpt.net](https://pygpt.net/)
- **Goose**: native desktop app for macOS/Linux/Windows plus CLI, works with Ollama among 15+ providers (listicle, unverified) — [Vellum](https://www.vellum.ai/blog/best-local-ai-assistants)
- **OrcaVision** (accessibility, see Q5) supports OpenAI, Claude, Gemini, a local Ollama server, or any OpenAI-compatible server (OpenRouter, Groq, Mistral, LM Studio `http://localhost:1234/v1`, llama.cpp, vLLM) — provider is a setting — [whoknowsla/orcavision](https://github.com/whoknowsla/orcavision)
- Fedora's editorial-guide-ramalama runs "locally, using open-weight models, requiring no API keys" — local-only, no cloud path — [Fedora Community Blog](https://communityblog.fedoraproject.org/meet-editorial-guide-ramalama-an-ai-assistant-that-checks-your-fedora-commblog-and-magazine-articles-against-editorial-guidelines/)

### Inferences
- "Local first, cloud fallback" with automatic routing is not an established pattern in Linux desktop assistants; the norm is a provider picker where Ollama is one option. An app that probes `:11434`/`:9931` at startup and only shows a cloud/account path when nothing answers would be ahead of the field, and consistent with Canonical's "no key, uninstall the snap to remove AI" stance.
- The GTK apps found are all GTK4/libadwaita; a GTK 3/X11 app cannot reuse their UI code but can reuse their provider-client logic (Python, HTTP to Ollama).

### Gaps
- Could not confirm from primary sources how Newelle or Alpaca detect a running Ollama (likely a GET to `:11434` but not verified).
- Bavarder's current backends and whether it still accepts cloud providers are unverified from the README text obtained.
- No evidence of a GNOME Shell extension or KDE applet that routes between local and cloud automatically.

---

## Key Question 5: What accessibility tooling on Linux (Orca, AT-SPI2) is being paired with local LLMs, if any?

### Takeaway
Official Orca/AT-SPI2 have no LLM features. The one concrete pairing found is OrcaVision, a third-party Orca "user extension" (requires Orca 51+) that screenshots the screen/active window/focused object or clipboard image, sends it to a vision model — including a local Ollama server or any OpenAI-compatible local server — and speaks/brailles the description; it works on X11 (libwnck 3 for the active window) and Wayland (grim/spectacle/gnome-screenshot/xdg portal).

### Cited Findings
- Orca "works with applications and toolkits that support the Assistive Technology Service Provider Interface (AT-SPI)", covering GTK, Qt, Java Swing, LibreOffice, Gecko, WebKitGTK, Chromium — [GNOME Orca help](https://help.gnome.org/users/orca/stable/introduction.html.en); [orca.gnome.org](https://orca.gnome.org/)
- **OrcaVision**: "Orca screen reader extension that describes your screen, active window, focused object or clipboard image with AI vision models (OpenAI, Claude, Gemini, Ollama, OpenAI-compatible). Works on X11 and Wayland." Requires "Orca 51 or later"; needs only the Python stdlib and the GObject libs Orca already uses; `install.sh` copies it into Orca's extensions directory and approves it (Orca re-requires approval if contents change); settings in Orca Preferences > User Extensions, stored in dconf; API keys moved into the keyring. Keybindings: Orca+Ctrl+D active window, Orca+Ctrl+Shift+D whole desktop, Orca+Ctrl+Alt+D focused object, Orca+Ctrl+Alt+K key management. X11: direct screen capture, active window via libwnck 3 or Orca's own view. Wayland: grim+swaymsg/hyprctl, grim (wlroots), spectacle (Plasma), gnome-screenshot (GNOME), xdg-desktop-portal fallback; if no active-window capture, full screen is captured and the model is asked to describe only that window. Only 4 commits on `main`, no releases, no license shown — [github.com/whoknowsla/orcavision](https://github.com/whoknowsla/orcavision)
- Orca's own repo and the Newton (Wayland-native accessibility stack) experimental fork mention nothing about LLMs — [GNOME/orca GitLab](https://gitlab.gnome.org/GNOME/orca); [WaylandNewton/orca on Codeberg](https://codeberg.org/WaylandNewton/orca)
- chrys87/orca-plugin exists as an Orca plugin project; no LLM relevance found in the search snippet — [github.com/chrys87/orca-plugin](https://github.com/chrys87/orca-plugin)

### Inferences
- Orca 51's "User Extensions" mechanism (Python, dconf settings, approval step) is the sanctioned way to attach LLM behaviour to the screen reader; a GTK 3 assistant could integrate with Orca by shipping such an extension rather than by replacing speech output.
- OrcaVision's capture cascade is a ready-made reference for X11-vs-Wayland screenshot handling in a Python accessibility app.

### Gaps
- No evidence of AT-SPI2 tree + local LLM (text-only, no screenshots) projects on Linux; everything found is vision-model based.
- OrcaVision's maturity (4 commits, no release, no license) and the Orca 51 release date were not verified.
- No GNOME or KDE accessibility-team statement on LLM use in Orca/KDE accessibility was found.
