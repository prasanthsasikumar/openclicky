# External review, 2026-09-11 — what was fixed and what was not

An external reviewer went over the whole repo. This records what was acted on, what was
deliberately left, and what needs a decision that is not mine to make. Anything below marked
**needs your call** is blocked on you, not on effort.

Baseline when the review arrived: backend 61 tests, agent 73, both `tsc --noEmit` clean over `src`.
After this pass: backend 70, agent 93, macOS 130 (from 120), and `tsc` covers `test` too.

## Fixed

| Finding | Commit | Note |
|---|---|---|
| Supabase anon key authenticated as user "undefined" | `554ec79` | The worst finding in the review. Verified by reproducing it, fixed, pinned by 4 tests. |
| `/transcribe-token` mints an AssemblyAI credential outside the credits gate | `554ec79` | |
| Stream metering kept the first 512 kB while usage arrives last | `554ec79` | Long turns were billed the 2-credit fallback instead of ~1000. Tests fail against the old code. |
| A cancelled stream was never charged | `554ec79` | |
| Missing service key silently disabled metering for the isolate | `554ec79` | Now refuses metered routes; no Supabase at all is still a deliberate unmetered self-host. |
| `createRemoteJWKSet` rebuilt per request | `554ec79` | |
| Unbounded `atob` on the audio body | `554ec79` | 24 MB base64 ceiling, 413 past it. |
| Raw upstream error bodies and rate-limit headers echoed to clients | `554ec79` | Logged server-side instead; `openai-*`, `x-ratelimit-*`, `set-cookie` stripped. |
| CI reported success on a failed Swift build | `ac1d8b6` | `shell: bash` for pipefail. Every prior green macos-app run proved only that `tail` worked. |
| Test files never type-checked | `108c01e` | Immediately found a test importing `KeyLike`, which jose no longer exports. |
| CLI reported a version matching nothing | `698dba1` | Reads `macos/OpenClicky/VERSION`. |
| `admin remove` deleted an account with no confirmation | `70a0085` | Types the email back; `--yes` for scripts. |
| Runtime image shipped dev dependencies; no HEALTHCHECK | `70a0085` | **Unverified** — no Docker on this machine. |
| TOML injection from unquoted paths, Codex child inheriting every env secret, no run timeout, `--password` on the command line, `process.exit` orphaning the child | `698dba1` | Five agent-CLI findings, plus the same env fix for the `codex mcp` child. |
| `shell.json` written 0644 with tokens and BYOK keys, credential prefix logged, verbatim transcripts to PostHog, force-unwrapped URLs from server strings, unused camera entitlement | `1a509b8` | Five macOS findings. |

## Deferred, with reasons

### Needs your call

- **The backend default URL contradicts the docs.** `config.ts:62` defaults to the hosted
  invite-only backend; the CLI help, `.env.example`, CONTRIBUTING and the setup docs all say
  localhost. Both are defensible — the shipped app wants hosted, a contributor wants localhost —
  but they cannot both be the default. Decide which, then the other four places get corrected.
- **`schema.sql` seeds every plan with a null Stripe price**, so checkout and webhooks are no-ops in
  production while tests pass on the in-memory store's fake prices. Fixing this needs your real
  Stripe price IDs, which I will not invent.
- **Stripe API version is unpinned** and `stripe.ts:64-65` reads period fields Stripe moved in the
  2025 versions. Pinning means choosing a version and testing against it; there is also no webhook
  idempotency table, so a retried event is reprocessed. This is the largest correctness risk left in
  the backend and it needs a deliberate Stripe upgrade, not a patch.
- **Linter and formatter.** There is no ESLint, Prettier, SwiftLint, SwiftFormat or `.editorconfig`.
  Adding them is cheap; agreeing on the rules is the part that needs you, and a formatter's first
  run rewrites every file, which would bury the history of everything above.
- **Repo clutter.** `PROMPT-initial-cut.md` and `REVERSE-ENGINEERING.md` at the root, eight documents
  describing the Mac app. What to keep is an editorial decision.
- **Hard-coded infrastructure.** `scripts/deploy-backend.sh:12` embeds `root@` plus a raw IP, and the
  Apple Team ID appears in twelve places. The Team ID in `project.pbxproj` is normal for Xcode; the
  deploy target is not, and moving it to git-ignored config changes your deploy flow.

### Real, deferred on effort

- **Credit checks are read-then-spend with no reservation**, so parallel requests overspend. The
  honest fix is an atomic decrement in Postgres (a function, or `update … returning` against a
  balance row), not application-side locking. Worth doing before any paid launch.
- **`skillMarkdown.ts` is duplicated** between `agent/` and `backend/` and the copies' doc comments
  have already diverged. It should be a third workspace package.
- **macOS: no Keychain.** Tokens and BYOK keys live in `shell.json`. This pass made the file
  owner-only, which is the cheap 90%; moving to the Keychain is the right answer and is a real
  migration with a fallback path for existing installs.
- **macOS: main-actor state mutated from the CoreAudio render thread**
  (`BuddyDictationManager.swift:553-556`, `RealtimeVoiceClient.swift:1003-1038`). Genuine, and the
  project pins Swift 5 mode with a note telling maintainers to ignore the concurrency warnings that
  would have caught it. Fixing it properly means auditing that boundary, not silencing warnings.
- **macOS: ~1,500 lines of dead code** — unused `DesignSystem` tokens while the HUD hardcodes 46
  colour literals, `CompanionResponseOverlay.swift`, the onboarding video subsystem, Sparkle wired
  but commented out with a Settings button that does nothing, half of `WindowPositionManager`.
  Deleting it is safe but large, and it should be its own reviewable change.
- **macOS: timers and observers with no teardown** (`NotchHUD.swift:601-631`, a 60 Hz per-display
  overlay, an un-stored typewriter timer that cannot be invalidated). A real leak; needs care around
  the HUD's lifecycle.
- **macOS: `CompanionManager.swift` mixes eight responsibilities across 1,689 lines**, the
  Accessibility helpers are copy-pasted into four files with different nil semantics, and eight
  backend requests each reimplement URL building and auth. All true, all structural.
- **macOS: 103 `print()` calls** where `AppLog` exists and is used by two files.
- **agent: duplication** — the "missing token" error in five places, every command repeating the
  same try/catch/fail wrapper, lane-forcing duplicated between `do` and `voice`, `talk` and
  `skills create` re-declaring options instead of using `withCommonOptions`.
- **agent: `codexHome.ts:98` writes to stderr from library code**, bypassing `--events`.
- **`.dev.vars.example` documents 14 of about 30 variables the code reads.** Mechanical, but it
  needs someone to enumerate the reads carefully rather than guess.

### Judged not worth changing

- **`admin remove` "undocumented"** — it is listed in the command usage line; the missing piece was
  the confirmation, which is fixed.
- **`OpenClickyShell` defines a drifted second shape over `shell.json`** — true, but nothing builds
  or ships it, so the fix is to delete the target, which belongs with the dead-code pass.
- **The UI test target is Xcode boilerplate CI never runs** — same: delete it with the dead code.

## One thing the reviewer got wrong

Nothing material. The review is accurate everywhere I checked it, including the two findings I
reproduced directly (the anon-key bypass and the p95-shaped billing window). The pasted copy reached
me with a few garbled fragments — an "Overier flight engine", "OverlayWindse at 60 Hz", a sentence
ending "still says blueine behaviour" — so a handful of macOS structural findings are recorded above
from partial text and may be slightly mis-stated.
