# The on-device rung: simple requests answered on this Mac

2026-10-27. Prasanth asked whether OpenClicky could use the Mac's own Apple Intelligence for the
most common tasks, and commissioned research on who else has done it
(`docs/research/reports/OS native on device AI assistants.md`). The answer was that nobody ships
the combination, and that the design which works at this model size is settled: narrow,
schema-pinned transformations chosen by entry point and input length, with deterministic fallback,
never a confidence score. This design applies that to the ask lane (⌃⌥) on macOS. Prasanth was
away while it was written; every choice below is the builder's call unless marked as his.

## Intent

A person holds ⌃⌥ and says "open Safari", "turn the volume down", "pause the music", or, with an
email highlighted, "make this shorter". Today every one of those waits 1.2 s, sends a screenshot to a
cloud model, and answers in three to six seconds, on a key or on the account's credits. With this
design, on a Mac that has Apple Intelligence, those requests are understood and done on the Mac
itself: no screenshot, nothing leaves the machine, an answer in about a second, no credits spent.
Everything the small model cannot do goes to the cloud exactly as before, and the person is told
which it was.

Why now: the dictation clean-up already runs on Apple's model (`AppleModelTakePolisher`, since
2026-10-12), so the framework, the weak-linking, the availability check and the "frame the words
so the model doesn't answer them" lesson are all in the codebase. The research found the triple
(OS model, desktop assistant, cloud fallback) unoccupied, and it is the project's own population:
older adults on free accounts whose credits run out on the simplest requests.

## Goals

- The six fast-lane actions (`open_app`, `open_url`, `create_folder`, `reveal_in_finder`,
  `set_volume`, `media_control`) are recognised on the Mac and performed without a screenshot or a
  cloud call, in under two seconds end to end on an M-series Mac.
- "Rephrase / shorten / fix / make it warmer" of text OpenClicky can already read (a highlight or
  the focused text box, `WritingTarget`) is rewritten on the Mac and written in place through the
  existing `WriteInPlace.replace`, with the same undo sentence.
- Everything else goes to the cloud with no measurable delay when the pre-send pause is on, and
  at most 1.2 s added when it is off.
- The person can see which tier answered (the island's "speaking · on this Mac") and can turn the
  rung off in Settings.
- The decision is logged for every ask (`local rung:` lines in app.log), so the next release can be
  judged on numbers: how many asks stayed local, how long each took, how often the local tier was
  wrong.

## Non-goals

- The Realtime lane (own OpenAI key, hands-free). It already has the fast-lane tools and speaks
  from the cloud; a local rung in front of a voice-to-voice socket is a different design.
- Open questions about the screen ("what is this?", "how do I…"), guided steps, pointing, the
  Teacher. The research is unambiguous that a 3 B model confabulates on these (69 % on
  false-premise questions, Apple's own model) and the accessibility literature trusts small
  models to describe and rewrite, never to reason about a screen.
- Summaries and questions over on-screen text. Possible later over OCR/AX text within the 4 k
  budget; not in this cut.
- Windows and Linux. The report's recommendation there is Foundry Local and llama-server, which
  are their own designs. This cut establishes the routing contract they will share.
- Private Cloud Compute, the `LanguageModel` protocol and image input (macOS 27). This Mac runs
  26.6; the second rung is a follow-up once a 27 machine is available.
- Non-English requests. The keyword gate below is English; other languages go to the cloud as
  today. (Apple's model covers 16 languages, none Indian, so the gain elsewhere is small.)
- LoRA adapters. One per model version, 160 MB each, toolkit already end-of-life for OS 27.

## Anti-goals (a failure even if it works)

- A cloud question gets slower. The rung must never hold a "how do I…" up while a local model
  thinks about it: the decision runs inside the pre-send pause, or under a hard budget.
- The model does something the person did not ask: "how do I open Safari?" must teach, not open.
  "open it" with nothing named must go to the cloud (or ask), never open an app called "it".
- The model answers the text instead of rewriting it ("can you send it tomorrow?" → "Sure, I
  can"). The polisher met this; the same framing and the same spoken-words check apply.
- A rewrite that drops or invents content. The output is checked for length and emptiness before
  anything is pasted; a bad one goes to the cloud, silently.
- Silent data egress. If the local tier falls back to the cloud, the ordinary pause and
  "sending your screen" caption run as they always did. The rung never removes a consent step.
- A Mac without Apple Intelligence (Intel, macOS 25, the toggle off, an unsupported Siri language)
  behaves exactly as today. Nothing crashes, nothing waits.

## Constraints

- Foundation Models on macOS 26: ~3 B parameters, text only, one 4,096-token budget per session
  covering instructions, the request, tool schemas and the answer. Guided generation
  (`@Generable`) is constrained decoding and is where the model's reliability comes from; use it
  for every output. No images. `supportsLocale` follows the Siri language, not the system
  language. Throttled when the process is in the background on battery, with the error surfacing
  as a guardrail violation, so a menu-bar agent must treat any error as "go to the cloud" and must
  prefer `respond` over streaming. `prewarm()` saves about half a second.
- The framework is weak-linked already so older macOS launches; every use stays behind
  `#if canImport(FoundationModels)` and `#available(macOS 26, *)`, as the polisher's does.
- Own keys first ([openclicky-own-keys-primary]) is unaffected: this rung sits before every
  route, local or cloud, and spends nothing.
- `MacActions.swift` is being extended on another branch today (`do-things-for-me` design:
  `minimize_all`, `web_search`, `play_music`, …). This design does not edit `MacActions.swift`.
  The local intent maps onto tool names and argument dictionaries and goes through
  `MacAction.parse(toolName:arguments:)`, so new actions can be added to the local vocabulary
  later by extending one enum, and the two branches merge without touching the same lines.
- The pre-send pause (`AskSendPause`, 1.2 s, Settings → Privacy, on by default) stays as the
  consent moment for the cloud path. Esc during it still cancels everything, local included.
- Unit tests run under `xcodebuild test` with no model available (CI is a plain runner): every
  decision step is pure or behind an injected model, and the Foundation Models call itself has a
  debug entry point for live checks on this Mac.

## How it works

### Where it sits

`CompanionManager.sendTranscriptToClaudeWithScreenshot` is the ask lane: pause → screenshot →
agent-lane gate → writing target → cloud model → tool calls → speak. The rung goes in first, before
the pause, as a step that takes the transcript (and the writing target, captured early for it) and
either **handles** the request or **declines**. Decline continues into the existing code
unchanged. Guided-step continuations ("what's next?") never enter the rung.

```
transcript
   │
   ├─ setting off / not on this Mac / not English / guide continuation ──────────────► cloud path (as today)
   │
   ├─ keyword gate says "not a command or a rewrite" ─────────────────────────────────► cloud path
   │
   └─ gate passes ──► Apple model: classify (guided generation, ≤ 1.2 s budget)
                          │
                          ├─ needsCloud / timeout / any error / unmapped ─────────────► cloud path
                          │
                          ├─ quick action ──► MacAction.parse ──► perform ──► speak outcome
                          │                                (bad args → cloud path)
                          │
                          └─ rewrite (writing target present) ──► Apple model: rewrite
                                                                     │
                                                                     ├─ sanity check fails ──► cloud path
                                                                     └─ WriteInPlace.replace ──► speak
```

When the pause is on, the classification runs **during** the pause (both start together); a local
decision cancels the pause and nothing is captured; a cloud decision lets the pause finish as
usual, so the cloud path costs nothing extra. When the pause is off, the classification runs under
its own 1.2 s budget and the cloud path starts the moment it declines or times out.

The caption during the pause stays "sending your screen · esc to cancel" from the first moment,
as today. It is the warning for what happens if nothing intervenes, and it is still shown for the
full pause before any capture; a local decision means the screen was never sent, which is less
than warned, never more. A neutral caption that switched to the warning only on a cloud decision
would shrink the visible warning to whatever was left of the pause, which is the one thing the
anti-goals forbid.

### The keyword gate (pure, `LocalAskGate`)

Runs before the model, the Home Assistant way: a cheap deterministic check that decides whether
the model is worth asking at all, so questions never pay the model's latency. The transcript must
be short (≤ 24 words) and contain at least one command word or rewrite word:

- command words: open, launch, start, go to, show (me), reveal, make/create/new … folder, volume,
  louder, quieter, mute, unmute, turn (it) up/down, play, pause, resume, stop the music, next
  (song/track), previous, skip, back
- rewrite words (only when a writing target exists): rephrase, rewrite, reword, shorten, shorter,
  longer, expand, fix, correct, polish, tidy, proofread, improve, make it/this (warmer, formal,
  friendlier, simpler, clearer, …), translate

and must not start like a question or a how-to ("how do i", "how can i", "how to", "what", "why",
"where is", "can you show me how", "teach me", "explain"). "How do I open Safari" fails the gate and
teaches; "open Safari" passes.

The gate is deliberately generous: its job is to keep the model off questions, not to be the
classifier. Unit-tested on a table of phrases both ways.

### The classification (Apple model, guided generation)

One `@Generable` struct, one session per request, `temperature 0`, instructions in the system
text, the request inside `<request>` tags with the line that it is never addressed to the model
(the polisher's lesson). The output:

```swift
@Generable
struct LocalAskIntent {
    @Guide(description: "What the person wants done. needsCloud for anything else, anything that
                         refers to the screen, something not named, or a question.")
    var kind: Kind          // openApp, openWebsite, createFolder, showFolder, setVolume,
                            // playback, rewriteText, needsCloud
    @Guide(description: "The app, folder or website named, exactly as said; empty when none")
    var name: String
    @Guide(description: "desktop, downloads, documents or home when a folder's place was said; else empty")
    var location: String
    @Guide(description: "0 to 100 when a volume level was said; -1 otherwise")
    var volumeLevel: Int
    @Guide(description: "play, pause, next or previous for playback; else empty")
    var playback: String
    @Guide(description: "For rewriteText: the instruction in a few words, e.g. shorter, warmer, fix grammar")
    var rewriteInstruction: String
}
```

A pure mapper (`LocalAskIntentMapper`) turns it into `(toolName, arguments)` and hands that to
`MacAction.parse`. The mapper is where the safety sits, and it is unit-tested with hand-built
intents:

- `openApp` with a name that is empty, a pronoun (it, that, this, them, the app) or more than four
  words → cloud. Otherwise the name is resolved against the apps installed on this Mac
  (`InstalledAppResolver`, pure matching over a cached list of `/Applications`,
  `/System/Applications`, `~/Applications` and the bundled utilities: exact name, then the spoken
  words as a prefix or as whole words of the app's name, "chrome" → "Google Chrome", "word" →
  "Microsoft Word"; plus a short alias table: email/mail → Mail, browser → the default browser,
  settings/preferences → System Settings, calculator, notes, photos, messages, music, calendar).
  A resolved name becomes `open_app {resolved name}`; no match → cloud, where today's model may
  know the app is not installed and say so. The fast lane's own finder needs the exact
  `<name>.app`, and "open chrome" spoken failing with "I couldn't find an app called chrome"
  would turn the most common request into a worse experience than today's.
- `openWebsite` → `open_url` with the name turned into an https URL when it looks like a host
  ("youtube", "bbc.co.uk"); a bare word that is not a host ("the news") → cloud.
- `createFolder` / `showFolder` need an item name (and `MacActionValidation.folderName` must accept
  it); location mapped through `MacActionLocation.named`, workspace when none. "Show me my
  downloads" names a place and no item: the fast lane has no open-a-folder action, so → cloud.
- An action that was performed and did not succeed (`didSomething == false`: the app really is
  missing, nothing to reveal) is **spoken**, as a cloud-chosen quick action's failure is today.
  It does not fall through to the cloud: the person would wait the local time, then the pause,
  then the cloud, for a request the resolver already did its best with.
- `setVolume` needs 0…100; "louder"/"quieter" with no number → cloud (the fast lane has no relative
  volume; adding one belongs with the other branch's action work).
- `playback` → `media_control {action}`; `play`/`pause` only if the fast lane accepts them (it takes
  `next`/`previous`/`playpause` today; the mapper translates play and pause to `playpause` only
  when something is already playing is unknown, so: play/pause → `playpause`).
- `rewriteText` with no writing target → cloud (the cloud prompt tells the person to highlight).
- Anything the parser returns as `.badArguments` or `.notAFastAction` → cloud.

Cloud means: the ordinary path, with its pause and screenshot, as if the rung did not exist.

### The rewrite (Apple model, second call)

Only with a writing target of ≤ 2,500 characters whose text is readable (`.accessibility` or
`.clipboard`; a `.screenOnly` target has no text for a text-only model and goes to the cloud).
A `@Generable` struct with a single `rewrittenText: String`. The system text is the dictation
polisher's voice rules plus the instruction; the original goes inside `<text>` tags with the line
that it is never addressed to the model and must not be answered or carried out.

Sanity check (pure, unit-tested, `LocalRewriteCheck`): the result is non-empty, between a third
and three times the original's length, not identical to the original, and does not start with
an answering phrase ("Sure", "Here is", "I can", "Certainly"). A failing result goes to the cloud,
silently, with a log line. A passing one goes through `WriteInPlace.replace(target, with:)`, whose
own same-text check and undo sentence apply.

### Speaking, history, the island

A local turn is spoken through the same `StreamingAnswerSpeaker` the cloud path uses
(`receiveWholeReply` with the outcome sentence), so the voice, the stop button and esc behave
identically. `answeringSourceName` is "this Mac" for the turn, so the island reads
"speaking · this Mac". The turn is added to `conversationHistory` ("open Safari" / "opened Safari")
so a cloud follow-up knows what happened. `lastSuccessfulQuickActionAt` is set as it is for a
cloud quick action (the first-run tour's proof). Analytics: the existing response event plus a
`local_rung` property naming the decision (local_action, local_rewrite, cloud_declined,
cloud_timeout, cloud_error, gate_skipped).

Esc during a local turn stops it like any other (`stopAskLane`); a model call already in flight
is simply ignored when it returns (the task is cancelled).

### Prewarm

When the ⌃⌥ press starts (the ask lane's `voiceState` goes to `.listening`, the same hook that
prepares Kokoro), a `LanguageModelSession` is created and `prewarm()`ed on a detached task,
only when the rung is on and available. The warmed session is used for the classification that
follows within 60 s; otherwise a fresh one is made. Cold start on this Mac measured 1.49 s for a
first answer; the target with prewarm is ≤ 0.8 s for the classification.

### Availability and the setting

`LocalAskRung.availability` reuses `AppleModelTakePolisherAvailability` (macOS 26, Apple
Intelligence on, model ready, `supportsLocale` for the reply language). Settings → Clicky →
Answers gets a switch:

> **Simple requests stay on this Mac** — Opening apps and websites, folders, volume, playback
> and rewrites are understood by Apple Intelligence on this Mac: no screenshot is sent and no
> answer credits are used.

The copy is exact on purpose. On a free account the ⌃⌥ audio is still transcribed through the
backend and the outcome sentence is still spoken by the account's voice (ElevenLabs) unless
Kokoro or the Mac voice is chosen, so "nothing leaves the Mac" would be false for the very people
this is for. The island's "speaking · this Mac" names who answered, as "speaking · Claude" does
today, not who speaks.

`openclicky.ask.onThisMac`, on by default. On a Mac where it is unavailable, the row's detail
says why in the polisher's words ("needs macOS 26", "apple intelligence is off", "apple's model
is still downloading", "this mac can't run apple intelligence") and the switch is disabled. For
a person whose ⌃⌥ goes to the Realtime lane (an own OpenAI key, `usesRealtimeVoice`), the row
stays live and its detail adds one line: "Not used while your OpenAI key answers out loud." The
row reads the credentials when it is drawn, as the "Answers come from" row does.

### Logging and measurement

Every ask logs one `local rung:` line: `gate skipped (question)`, `declined by model in 0.6 s`,
`timed out at 1.2 s`, `error <reason> → cloud`, `open_app in 0.7 s (spoken: opened Safari)`,
`rewrite 412→298 chars in 1.1 s`. `scripts/measure-actions.sh` gains a `local rung` section
(count of each outcome, p50/p95 of the local turns), since the fast lane's acceptance number
(p95 under 2 s) is the number this rung must beat.

Debug: `--openclicky-local-ask "<request>"` runs the gate, the classification and the mapping on
this Mac, prints each step and its time, performs nothing. `--openclicky-local-rewrite
"<instruction>" "<text>"` does the same for a rewrite. Both are how the prompts get tuned without
holding ⌃⌥ a hundred times.

## Decisions made for Prasanth (his to overturn)

- **Keyword gate before the model.** Chosen over "ask the model about everything" so that
  questions never pay for the model. Costs: a command phrased without any of the words goes to
  the cloud, which is today's behaviour, not a regression.
- **No relative volume, no "open it".** Both go to the cloud rather than guessing.
- **English only.** The gate's word list is English; the model's language coverage would allow
  more, at the cost of running it on every non-English ask without a gate.
- **On by default.** The research's accessibility rule is "small models may describe, rewrite and
  act on closed vocabularies"; every local action here is already in the fast lane with the same
  typed arguments, so nothing new can happen that a cloud model could not already ask for.
- **The island says "this Mac".** Making the tier visible is the one thing every shipped
  precedent (TalkBack, Narrator, Notepad) does.
- **Not touching `MacActions.swift`**, to merge cleanly with the do-things-for-me branch. When
  that branch lands, its new actions can be added to `LocalAskIntent.Kind` in one place.
- **App names resolved on the Mac, not by the model.** The builder check found that "open
  chrome" would fail out loud through the fast lane's exact-name finder. A deterministic resolver
  over the installed apps fixes the common case without asking a 3 B model to know product names.
- **A failed local action is spoken, not retried in the cloud**, and **the pause caption is
  unchanged**: both keep every cloud question exactly as slow as today and every consent step
  exactly as visible. (Builder check, 2026-10-27.)
- **The privacy copy promises only what is true**: no screenshot, no answer credits.
- **Realtime users see the row with a line saying it does not apply to them**, rather than a
  hidden or disabled row that would need to react to the credentials file.

## What is left to the builder

- Exact prompt wording for the classifier and the rewrite; tuned with the debug flags against a
  table of phrases (the gate's test table doubles as the live phrase list).
- Whether the writing target is captured before the pause for the rewrite path (it must be, since
  the rewrite needs its text) and whether that early capture changes anything for the cloud path
  (it should be reused, not captured twice).
- How `measure-actions.sh` presents the new section.
- A `CHANGELOG.md` entry (release procedure requires one) and the AGENTS.md key-files rows.

## Acceptance

- Unit tests: the gate table, the mapper (every `Kind` with good and bad fields), the rewrite check,
  the decision flow with a fake model (handles / declines / times out / throws, each landing on
  the right branch, and the pause-cancel interplay), the setting's default.
- Live on this Mac (macOS 26.6, M-series, Apple Intelligence on), through the debug flags and
  then by holding ⌃⌥: "open Safari", "open youtube", "make a folder called taxes on the desktop",
  "set the volume to thirty", "pause the music", "how do I open Safari" (must teach), "open it"
  (must go to the cloud), "what is this" (must go to the cloud with the pause), and a highlighted
  sentence with "make this shorter" (must rewrite in place with the undo sentence).
- p95 under 2 s for the local actions in `measure-actions.sh` over at least 20 asks.
- The full `OpenClickyTests` suite passes; CI's unsigned build passes.
