# Do things for me: minimize everything, search, email, Spotify by voice

2026-10-27. Hands-free now switches on and off reliably (the Globe key fix, the voice sign-off), and
the first thing Prasanth did with it was sit back and tell the Mac what to do: "minimize everything",
"open a webpage and search for xyz", "open Facebook", "check my email", "draft a new email to xyz",
"open Spotify and play xyz". Half of that already happens through the fast lane (`open_app`,
`open_url`, `media_control`, `set_volume`); the other half either turns into a how-to lesson with the
pointer, or goes to the Codex agent, which does files and commands, not apps. This design adds the
missing actions as the same kind of typed, sub-two-second fast-lane tools, and one mechanism they
need: taking a fresh look at the screen after an action.

## Intent

A person with hands-free on (or holding ⌃⌥) says what they want done and it happens, in a second
or two, with a short spoken confirmation and no lesson. The lane that teaches ("how do I…") stays
exactly as it is; a command ("open…", "play…", "minimize…", "check…", "draft…") does the thing.

## Goals

- Six new voice actions, each as exact and fast as `open_app` is today:
  `minimize_all`, `restore_windows`, `web_search`, `play_music`, `check_email`, `draft_email`;
  plus `pause` and `play` added to `media_control`.
- "Act, then look": after an action that changes the screen, the model sees a fresh screenshot
  before it speaks, so "check my email" in Gmail can read the unread list it just opened.
- The model does a command and teaches a question, and says which it is doing.
- Everything typed and bounded, as the fast lane already is: a misheard word can pick the wrong
  song or the wrong recipient, never run a command or send anything.

## Non-goals

- Operating arbitrary apps through the screen (a click/type loop on screenshots). That is the next
  stage, a separate design; this stage covers the stated list with scripted actions.
- Sending email by voice. Never, in this stage or a later one without its own design.
- Reading Gmail through any API or the browser's cookies. Gmail is read from the screen.
- Apple Music, YouTube Music, the Spotify web player. Spotify desktop only ("in my case a desktop
  app"); the action is shaped so another player can be added later.
- Windows and Linux clients.
- The ask lane getting "act, then look". There the outcome sentence is spoken as today.

## Anti-goals (a failure even if it works)

- An email goes out that the person did not read and send themselves.
- "Minimize everything" hides OpenClicky's own island, overlay or the orb, or windows on other
  Spaces the person was not looking at.
- "Play xyz" plays for ten seconds while a lookup runs, or plays something unrelated with no
  way to tell what it picked. The confirmation always names the track and artist it chose.
- A new permission prompt storm. Apple Mail and Spotify scripting each ask once ("OpenClicky
  wants to control Mail"), on first use, and a refusal is explained in one sentence.
- "How do I minimize this?" starts minimizing things.

## Constraints

- Own keys first ([openclicky-own-keys-primary]): nothing of the person's goes through the
  OpenClicky backend unless they have no key of their own for it. The one new backend call
  (Spotify catalogue search) carries only the search words, never account data, and a person who
  adds their own Spotify client credentials goes straight to Spotify.
- Tool calls are handled on the Realtime receive loop and must return quickly (the Teacher's
  rule); anything slow (a Mail AppleScript on a huge inbox, a web page loading) is capped.
- Apple Events from the hardened runtime: `com.apple.security.automation.apple-events` is already
  in the entitlements (`BrowserTabLocator`, the Media plugin); `NSAppleEventsUsageDescription` is
  in Info.plist. Each scripted app (Mail, Spotify) prompts once for Automation.
- The fast lane's acceptance number (p95 under 2 s, `scripts/measure-actions.sh`) applies to every
  new action except the two that wait on something else: `check_email` in Apple Mail (AppleScript,
  capped at 5 s) and anything that ends with a look at the screen (+ about 2 s).
- `MacActionRunner` stays testable without AppKit: every system call is an injected closure.

## What exists (2026-10-27)

- `MacActions.swift`: `MacAction` (typed enum), `MacActionValidation`, `MacActionOutcome` (one
  sentence each, `spokenSentence`), `MacActionRunner` (injected closures), the tool schemas
  (`realtimeToolDefinitions`, mapped to Anthropic shape by `anthropicToolDefinitions`, and to the
  plans through `PlanToolProtocol`), and `MacAction.parse(toolName:arguments:)`.
- `RealtimeVoiceClient.handleToolCall`: `.action` → `onMacAction` → `function_call_output` →
  `requestContinuation`. `attachScreenContext()` captures the pointer's display, builds the caption
  (writing target, selected text), sends it as a user item, and starts OCR for `point_at`.
- `CompanionManager.composeTalkInstructions` builds the Realtime instructions from
  `RealtimeVoiceClient.baseInstructions` + plugin lines + skills; the ask lane has its own prompt.
- The Media plugin (`Plugins/Media/AppleScriptMediaSource.swift`) already scripts Spotify and
  Music for play/pause/next/previous and the current track, under a 3 s timeout, one script at a
  time, never launching a player.
- The backend (`backend/src/app.ts`, Hono) has public routes (`/health`, `/auth/config`) and
  gated ones; env in `.dev.vars` locally and on the VPS.

## Design

### 1. The actions

All go in `MacActions.swift` beside the existing ones, same shape: a `MacAction` case, a
`MacActionOutcome` case with its sentence, a tool definition, a `parse` branch, a runner branch
with injected closures. Names and arguments:

| Tool | Arguments | What happens | Says |
|---|---|---|---|
| `minimize_all` | none | Every window that is on screen now (CGWindowList, `onScreenOnly`, layer 0) belonging to a regular app other than OpenClicky is minimized through Accessibility (`kAXMinimizedAttribute` = true on the matching `AXWindow`, matched by frame + pid). The list of windows it minimized is kept for `restore_windows`. Windows on other Spaces are left alone. | "minimized 6 windows" / "nothing to minimize" |
| `restore_windows` | none | Un-minimizes the windows `minimize_all` minimized this session, newest set first, and brings the previously front app back. Not every minimized window in the Dock, only ours. | "brought 6 windows back" / "I hadn't minimized anything" |
| `web_search` | `query` (string, 1–200 chars), `site` (enum: `web`, `youtube`, `wikipedia`, `maps`, `amazon`; default `web`) | Opens the search URL in the default browser: Google for `web` (DuckDuckGo if the person has set `searchEngine: "duckduckgo"` in shell.json — builder's call whether to add the setting now or leave the hook), and the site's own search URL otherwise. Query percent-encoded; refuses control characters. Ends with a look at the screen. | "searching for xyz" |
| `play_music` | `query` (string: a song, optionally with the artist, as said) | See §3. Spotify desktop launched if not running; the catalogue lookup picks a track; Spotify plays it by URI. | "playing Blinding Lights by The Weeknd" |
| `media_control` | `action` enum gains `pause` and `play` | `pause`/`play` go to the running player's own scripting (Spotify, then Music, whichever is running, via the Media plugin's source) so "stop that song" never *starts* playback the way the play/pause media key would; with no player running, the media key as today. | "paused" / "playing" |
| `check_email` | `app` (enum `mail`, `gmail`; optional) | See §4. Apple Mail: unread senders and subjects by AppleScript. Gmail: opens the inbox in the default browser and ends with a look at the screen. | Mail: the list, handed to the model as text. Gmail: "here's your inbox" + the model reads the screen |
| `draft_email` | `to` (string), `subject` (string, optional), `body` (string), `app` (optional) | See §4. A new, open, unsent message with the fields filled in. | "drafted an email to xyz; it's open for you to check" |

Validation lives in `MacActionValidation` like `folderName` and `webURL`: a query is trimmed,
capped, and refused if it contains control characters; `to` is either an email address (kept as
is) or a name (kept as said, at most 80 characters, no angle brackets, so it can only ever land in
a To field); `body` is capped at 2,000 characters (the Gmail compose URL's practical limit; Apple
Mail takes the same cap so both behave alike).

The existing `open_app` and `open_url` cover "open Facebook", "open Spotify", "open a webpage";
nothing changes there except that `open_url` ends with a look at the screen too.

### 2. Act, then look

`MacActionOutcome` gains `var thenLooksAtScreen: Bool` (true for `web_search`, `open_url`,
`check_email` on Gmail, `draft_email`; false for the rest — `minimize_all` leaves nothing worth
describing, and a Mail check already returns its text).

`handleToolCall` is awaited on the Realtime receive loop, so anything slow in it freezes playback
and barge-in (the Teacher's rule). Every action that can wait — the settle below, the Mail
script, a Spotify launch — therefore runs off the loop on one serial chain (`actionChain`, the
shape of `pointingChain`): the receive loop returns at once, the chain performs the action, waits,
attaches the screen, sends the `function_call_output` and asks for the continuation when done.
`requestContinuation` already tolerates an output that arrives after `response.done`. Instant
actions (`open_app`, `set_volume`, `minimize_all`) take the same chain so ordering stays simple.
The fake-socket test in §9 drives the chain and asserts the order of what goes out.

On the chain, after `perform`:

1. If `thenLooksAtScreen`, wait for the screen to settle: poll every 250 ms until the front app
   is not the one that was in front when the action began, or 1.5 s have passed; then a further
   1 s for a browser page to paint; 4 s at most in all. (Builder's call to tune, with the
   measurements in the log.)
2. Call `attachScreenContext()`: the fresh screenshot goes into the conversation as a user item
   with the usual caption, `lastScreenCapture` is replaced (so a `point_at` that follows points at
   the new screen), OCR starts.
3. Send the `function_call_output`, whose text says what happened and what to do with the screen,
   e.g. for Gmail: "Gmail's inbox is open and attached as the latest screenshot. Read out the
   unread messages briefly: sender and subject, newest first, at most five, then stop." For a
   search: "The results are on screen. Say what the first few are in a sentence." The tool output
   is the right place for this instruction: it is per-call and never spoken.
4. `requestContinuation(after:)` as today.

**Two texts per outcome.** `MacActionOutcome` splits into `spokenSentence` (short, product copy,
what the ask lane says verbatim as today) and `toolOutput` (what the Realtime model is told:
the Mail list and the reading instruction, the "screen attached, read the bold rows" line).
Existing outcomes keep one text for both.

**The ask lane and the plans** get every new tool through `anthropicToolDefinitions` as today,
without act-then-look (non-goal) and speaking `spokenSentence`: for a Mail check that sentence
carries the list itself, at most three ("3 unread: Ana about the invoice, Ben about Friday, and
one from GitHub"), for Gmail "here's your inbox", for a search "the results are up". Nothing is
withheld from those lanes; they just say less.

**Can the model read the inbox?** The capture is scaled to 1,280 px wide
(`captureCursorScreenContext`), where an inbox row is about 12 px tall and bold is a one-pixel
difference. The plan starts with a spike: a Gmail inbox capture attached to a Realtime turn with
the §2 instruction, five tries, counting how often sender + subject + unread come back right. If
that is unreliable, the look step for `check_email` and `web_search` attaches a second image, a
crop of the window at full resolution (the window frame from CGWindowList, JPEG, capped at
~1,600 px on the long side), and the tool output also carries the OCR lines of the window
(`lastScreenText` already runs; OCR cannot tell bold, the crop can). Reading pixels or OCR of the
screen is within the non-goal; only APIs and cookies are out. The extra image costs tokens on
those two actions only.

A second mechanism the Gmail path needs: knowing the page has loaded. The settle wait above is
the whole answer for this stage; a screenshot of a half-loaded inbox makes the model say "it's
still loading" and the person asks again. Noted as a known rough edge.

### 3. Spotify: finding the song

Spotify's desktop app plays a track by URI (`tell application "Spotify" to play track
"spotify:track:…"`) but offers no search. The lookup is Spotify's Web API `GET /v1/search`
(`type=track`, `limit=5`, `market=` the Mac's locale region, since a client-credentials token
has no user behind it), which needs an app's client credentials, not a user login.

Routes, own keys first:

- `spotifyClientId` + `spotifyClientSecret` in `~/.openclicky/shell.json` (Settings → Keys & Plans,
  a "Spotify" row with the link to the developer dashboard): the app mints the client-credentials
  token itself and searches directly. Logged `route: music lookup → spotify`.
- Otherwise the OpenClicky backend's new `GET /music/search?q=…` (§6), on OpenClicky's own Spotify
  app. No auth required: it carries only the words, returns only catalogue data, and own-key people
  have no account token to send. Logged `route: music lookup → backend`.
- Neither reachable (offline, backend down): fall back to opening `spotify:search:<query>` in the
  app and say "I opened Spotify's search for xyz; pick one" — the person is one click away rather
  than stuck.

Picking the track: the first result whose name or artist shares a word with the query; otherwise
the first result. The confirmation names what was picked ("playing Blinding Lights by The
Weeknd") so a wrong pick is heard at once and "no, the one by…" is the next turn. The album
artwork and the track also show in the island's Now Playing component if that plugin is on;
nothing new there.

If Spotify is not running, `open_app`'s launcher starts it and the runner waits up to 6 s for it
to answer a `player state` script before playing; "Spotify is starting, give it a moment" if it
does not.

### 4. Email: Apple Mail and Gmail

Which app, in order: the `app` argument when the person named one ("in Gmail"); else
`emailApp` in shell.json (Settings → Voice → "Doing things" → Email: Automatic / Apple Mail /
Gmail); Automatic means Apple Mail when Mail has accounts set up on this Mac (its data folder,
`~/Library/Mail/V*/MailData/Accounts.plist`, exists — no Automation prompt to find out), otherwise
Gmail in the default browser. Every Mac has Mail as its mailto handler out of the box, set up or
not, so the handler is not the signal. Decided with Prasanth 2026-10-27. Logged once per change.

**Apple Mail, check:** one AppleScript, newest first, at most 10:
`sender`, `subject`, `date received` of `messages of inbox whose read status is false`. Capped at
5 s (the Media plugin's script runner pattern, timeout and one-at-a-time); on timeout or a refusal
(-1743, Automation not allowed) the outcome says so and activates Mail instead, ending with a look
at the screen. The list is returned to the model as text; the tool output tells it to read out
sender and subject briefly, newest first, at most five, never the bodies (they are not fetched).

**Apple Mail, draft:** `make new outgoing message with properties {subject:…, content:…,
visible:true}`, then `make new to recipient at end of to recipients with properties
{address:…}` for an address, `{name:…}` for a name (Mail shows the name and offers its own
contact matches when the person clicks the field; OpenClicky never reads Contacts, so no Contacts
permission). Mail is activated so the window is in front. Never `send`.

**Gmail, check:** opens `https://mail.google.com/mail/u/0/#inbox` in the default browser and
ends with a look at the screen (§2). Unread rows are bold in the screenshot; the tool output asks
for sender and subject of the bold ones.

**Gmail, draft:** opens `https://mail.google.com/mail/?view=cm&fs=1&to=…&su=…&body=…`
(percent-encoded, body ≤ 2,000 characters) in the default browser; Gmail opens its compose window
pre-filled. Ends with a look at the screen so the model can confirm it is open.

**Never sent.** There is no tool that sends. The `draft_email` description and the instructions
say the person reads and sends it themselves; "send it" by voice is answered with "I don't send
email — it's open in front of you, press send when you're happy with it." The body the model
writes is short, in the reply language, with no invented facts; it says what the person said.

### 5. Command or question

One block in `RealtimeVoiceClient.baseInstructions` (and the matching line in the ask lane's
prompt, so the plans behave the same):

> When the user tells you to do something you have a tool for ("open…", "play…", "minimize…",
> "search for…", "check my email", "draft an email"), do it with the tool and confirm in a few
> words. Do not explain how they could do it themselves and do not point. When they ask how to
> do something ("how do I…", "where is…", "show me how"), teach: explain and point, and do not do
> it for them. If you are not sure which they meant, ask in one short question.

The pointing fixes from earlier today (window buttons, "this tab") are unrelated and stay.

### 6. Backend: `GET /music/search`

In `backend/src/`, a new `music.ts`: client-credentials token from Spotify (cached until 60 s
before expiry), `GET /music/search?q=<words>` → `{ tracks: [{ uri, name, artists: [..],
album, durationMs }] }`, at most 5. Public route (no auth middleware), rate-limited 30 requests a
minute per IP (the existing limiter pattern if there is one; otherwise a small in-memory one),
results cached 1 h per normalised query, `q` capped at 200 characters. Env: `SPOTIFY_CLIENT_ID`,
`SPOTIFY_CLIENT_SECRET` in `.dev.vars` and on the VPS (`flowsxr-hq/infra` runbook for env
changes). Without the env the route answers 503 and the app falls back to `spotify:search:`.

Prasanth registers the Spotify app (developer.spotify.com, any name, redirect URI unused for
client credentials) and puts the credentials in both places; this is the one step the builder
cannot do.

### 7. Settings and keys

- Settings → Voice → a new section "Doing things": Email (Automatic / Apple Mail / Gmail), with a
  one-line footer: "Nothing is ever sent by voice."
- Settings → Keys & Plans → "Spotify" row: client id and secret, a Test button (searches for
  "test" and reports the first track), the dashboard link. Shown for everyone; empty means the
  backend route.
- Both go through `SettingsCatalog` so search finds them.

### 8. Safety and control

- Every argument typed and capped (§1). No tool runs a command, deletes, sends or pays.
- `minimize_all` is reversible by `restore_windows` and by the Dock.
- Esc stops the reply as today; the actions themselves are sub-second (the Mail script is capped).
- The log line per action (`mac action: <name> <sentence> in <s> s`, as today) never includes
  the email body or the recipient, only sizes ("draft_email: 240-character body, address given").
- Permission refusals (Automation for Mail or Spotify) are one sentence naming System Settings ›
  Privacy & Security › Automation.

### 9. Tests

Unit (`MacActionTests`, the runner with injected closures; new files where a thing is its own):

- `parse` for every new tool: good arguments, missing, out of range, control characters, an
  unknown `site`/`app`, a name with angle brackets in `to`.
- Outcome sentences and `thenLooksAtScreen` per outcome.
- `minimize_all`: the window-picking rule as a pure function over `OnScreenWindow`-shaped input
  (on screen, layer 0, regular app, not OpenClicky); `restore_windows` restores only what was
  minimized, newest set first.
- `web_search` URL building per site, encoding, the cap.
- Track picking from a search result (shared word wins, else first) and the fallback URI.
- The Mail AppleScript sources as pure strings with quotes and backslashes escaped; the Gmail
  compose URL encoding with a 2,000-character body and a `+` in the address.
- The email-app choice: argument → setting → mailto handler mapping.
- Act-then-look on the fake Realtime socket: the screen item is sent before the
  `function_call_output`, and the continuation follows `response.done` as today.
- The backend route: a test with a fake Spotify (token + search), the cache, the 503 without env,
  the cap on `q`.

Live, by Prasanth, with hands-free on (checklist in the plan): each sentence from the opening
list; "stop that song" with nothing playing (must not start playback); "how do I minimize this?"
(must teach, not minimize); "send it" after a draft (must decline); "minimize everything" with a
window on another Space (must stay); Gmail check with the page slow to load.

### 10. Docs and release

- `macos/OpenClicky/AGENTS.md`: the `MacActions.swift` row (new actions, act-then-look, the
  Spotify route), the Settings rows, the backend route in the backend's own README.
- `CHANGELOG.md`: its own section after 0.9.4 (0.9.4 ships the fixes from today; this is a
  feature set and gets the next version — builder's call on 0.9.5 vs 0.10.0, Prasanth's to
  override).
- The website's feature list, if it lists what Clicky can do by voice, gets one line (separate
  repo, `openclicky-site`; not part of this plan beyond a note).

## Left to the builder

- The settle-wait numbers in §2, measured in the log and written down in AGENTS.md.
- Whether `web_search` gets a search-engine setting now or only the shell.json hook.
- The shape of the in-memory rate limiter and cache on the backend, if none exists to reuse.
- Where exactly the "Doing things" section sits on the Voice page and the row copy, following the
  settings revamp's rules (`docs/superpowers/specs/2026-10-12-settings-revamp.md`).
- The 0.9.5 vs 0.10.0 question.
