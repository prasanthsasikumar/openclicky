# OpenClicky accounts on the Anthropic grant — design

Date: 2026-10-08 · Status: draft for review · Owner: Prasanth

## 1. Goal

Anyone — including people who have never heard of an API key, like a parent or grandparent — can
install OpenClicky, sign up with an email address, and immediately get **polished dictation** and
**"ask about my screen" with pointing**, paid for by FlowsXR's **$1,000 Anthropic grant**. Nothing
else is installed on their Mac; they never see a key, a model name or a provider.

Success looks like:

- A new user goes from download to a working polished take and a pointed-at answer in under three
  minutes, with no settings page visited.
- The grant cannot be overspent: at most **$2 per person per day**, **$10 per person per month**,
  **100 accounts**, and **$1,000 across everyone per month** — the whole grant is meant to be used
  in about a month (100 × $10 = the grant). Past 100 users we revisit.
- When a limit is reached, nothing breaks: dictation keeps working offline, and the app says
  plainly what happened and when it resets.
- Bring-your-own-key users keep working exactly as today. Invite-only accounts are retired: anyone
  can sign up.

## 2. What an account includes (v1)

| Feature | Hears / speaks | Thinks | Paid by |
|---|---|---|---|
| Dictation (hold fn) | Apple on-device recogniser | — | free, on the Mac |
| Dictation polish, Hey Clicky edits | — | **cheap model chosen by the server** (Claude Haiku 4.5 today) | grant |
| Ask about my screen + pointing (hold ⌃⌥, or ⌃×2 typed) | Apple recogniser hears; **ElevenLabs** speaks (the Mac's own voice when ElevenLabs' allowance is used up) | **Claude Sonnet 5.5** (pointing needs it) | Anthropic grant (thinking) + ElevenLabs grant (voice) |
| Quick actions — open an app or a site, make a folder, show in Finder, volume, play/pause | — | Claude picks the action as a tool call in the ask lane; the Mac performs it (`MacActions`) | Anthropic grant |
| Agent chores (folders via agent, email) | — | Codex | **not included** — own key only |
| Realtime hands-free voice | OpenAI realtime | — | **not included** — own key only |

Quick actions run on the realtime lane today; on an account they move into the ask lane as Claude
tools (§5.3a), with the same typed, closed set of actions — Claude can name an action, it can never
run a command.

## 3. The user's journey

1. **Download and open.** The walkthrough's last chapter, "use clicky's brain", offers three
   choices: *create a free account* (recommended), *sign in*, *use my own key*, plus *skip — keep
   everything on this Mac*.
2. **Create account.** Email + password in the app (no browser). The app says: "check your email —
   tap the link, then come back." Supabase sends the confirmation from hello@flowsxr.com via Resend.
3. **Confirmed.** The link opens a small "you're in — go back to OpenClicky" page. The app polls
   every 5 s while the sign-up sheet is open and signs in on its own once the email is confirmed.
4. **Use it.** Takes are polished; ⌃⌥ questions are answered out loud with pointing. Settings →
   Account shows "this month: $0.42 of $2.00 · resets 1 Nov" as a simple bar (people see a bar and
   "plenty left / running low", not dollars, unless they open details).
5. **Running low (80% personal).** One quiet notice in the HUD: "your free allowance is running low
   this month — dictation still works offline."
6. **At a limit.**
   - *Personal:* polish is skipped (takes paste with local cleanup only), ⌃⌥ answers "you've used
     this month's free questions — they come back on 1 Nov. Dictation still works." The account page
     offers "use your own key".
   - *Daily ($2):* same behaviour, wording "you've used today's free allowance — it comes back
     tomorrow."
   - *Everyone's ($1,000, i.e. the grant is spent):* same behaviour, wording "OpenClicky's free
     allowance is used up for now — you can keep going with your own key."
   - *Full (100 accounts):* the create-account sheet says "OpenClicky's free accounts are full right
     now" and offers "use your own key" instead.
7. **Never a dead end:** offline dictation, history, styles, dictionary and shortcuts never depend on
   the account.

## 4. Architecture

```
 Mac app (signed in: Supabase JWT in ~/.openclicky/shell.json)
   │  POST /v1/polish   {purpose: "polish"|"edit", system, text}
   │  POST /chat        (ask lane: screenshot + question; existing route)
   │  GET  /billing/me  (usage bar)
   ▼
 OpenClicky backend  api.openclicky.flowsxr.com  (Hono, Docker on the VPS)
   1. auth            Supabase JWT → principal (existing auth.ts)
   2. plan gate       account requests may use only Claude routes; OpenAI routes → 402 not_on_plan
   3. model policy    server picks the model per purpose; client "model" is ignored on the grant
   4. reserve         oc_reserve(user, estimate)  — atomic, checks personal + global limits
   5. forward         Anthropic Messages API with the grant key (prompt caching on)
   6. settle          oc_settle(reservation, real cost from response usage)
   ▼
 Supabase (db.flowsxr.com): oc_accounts, oc_reservations, oc_usage_events, oc_budget (new/changed)
```

Bring-your-own-key requests skip steps 2–4 and 6 entirely, as today.

## 5. Backend

### 5.1 Model policy (server decides)

A small table in `backend/src/modelPolicy.ts`, overridable by env:

| Purpose | Env var | Default | Why |
|---|---|---|---|
| `polish` (dictation cleanup) | `POLISH_MODEL` | `claude-haiku-4-5` | cheapest; the task is punctuation, numbers, style rules |
| `edit` (Hey Clicky) | `EDIT_MODEL` | `claude-haiku-4-5` | same |
| `ask` (screen question + pointing) | `ASK_MODEL` | `claude-sonnet-5-5` | coordinates must be accurate |
| lane gate (talk vs agent) | `GATE_MODEL` | `claude-haiku-4-5` | one-word classification |

For grant-funded requests the client's `model` field is **replaced**, never trusted. BYOK requests
keep the model they ask for. Changing to any other cheap model later is a config change (no app
release).

### 5.2 New route `POST /v1/polish`

Request `{ purpose: "polish" | "edit", system: string, text: string, language?: string }`, response
`{ text: string }`. Limits: `text` ≤ 8,000 characters, `system` ≤ 6,000, `max_tokens` = 1.5 × input
estimate (cap 2,048). The system prompt is sent with `cache_control` so repeated style rules cost a
tenth after the first take. Replaces the app's direct `/v1/chat/completions` call for account users
(and for BYOK users with an Anthropic key; BYOK OpenAI users keep the old path).

### 5.3 Ask lane on `/chat` (existing)

Unchanged contract (Anthropic Messages body, streamed). For grant requests the backend: forces
`ASK_MODEL`, caps `max_tokens` at 1,024, allows at most 2 images per request each ≤ 1,568 px on the
long edge (the app already downsizes), and adds `cache_control` to the system prompt.

### 5.3a Quick actions as Claude tools

The ask lane gains the six `MacActions` verbs as Anthropic tool definitions (`open_app`, `open_url`,
`create_folder`, `reveal_in_finder`, `set_volume`, `media_control`) with the same argument schemas
`MacAction.parse` already enforces (`strict: true`; `location` a closed enum; http(s) URLs only).
`point_at` stays a `[POINT:x,y:label]` tag in the text so pointing is unchanged. Flow:

1. App sends the question + screenshot + tools to `/chat` (streamed).
2. If Claude returns a `tool_use`, the app runs it locally through `MacAction.parse` → perform,
   which already returns one sentence ("Created Launch Ideas on your Desktop.").
3. That sentence is spoken (and sent back as the `tool_result` only if Claude needs to continue,
   e.g. "open Safari and point at the address bar" — at most 2 tool rounds per question).

Anything outside the six verbs is answered in words ("I can't do that on an account yet — it needs
the agent, which uses your own key"). The tool list is in the cached system prefix, so it costs
almost nothing per question.

### 5.3b Answer voice: ElevenLabs with a fallback

`/tts` is allowed for accounts and metered in **characters** against a separate ElevenLabs pool:

| Limit | Default | Env var |
|---|---|---|
| Per person per month | 20,000 characters (~100 spoken answers) | `ACCOUNT_MONTHLY_TTS_CHARS` |
| Everyone per month | the ElevenLabs plan's allowance, read live from `GET /v1/user/subscription` (cached 10 min), minus a 5% margin | `TTS_GLOBAL_MARGIN` |

Answers are trimmed for speech before sending (the app already strips `[POINT]` tags; long answers
are spoken as the first ~400 characters and shown in full). When either pool is used up, or
ElevenLabs errors, `/tts` answers `402 { error: "tts_budget" }` and the app speaks the answer with
`AVSpeechSynthesizer` instead — no silence, no message to the user. Voice: `ELEVENLABS_VOICE_ID`
(one warm voice for everyone in v1), model `eleven_flash_v2_5`.

### 5.4 Plan gate

| Route | BYOK | Account |
|---|---|---|
| `/v1/polish`, `/chat`, `/v1/messages`, `/tts`, `/billing/me` | ✓ | ✓ |
| `/agent/transcribe`, `/agent/realtime/session`, `/v1/chat/completions`, `/v1/responses`, `/transcribe-token`, `/skills/create` | ✓ | **402 `not_on_plan`** |

### 5.5 Metering in dollars

Price table in `backend/src/prices.ts` (USD per million tokens):

| Model | Input | Output | Cache write (5 min) | Cache read |
|---|---|---|---|---|
| claude-haiku-4-5 | 1.00 | 5.00 | 1.25 | 0.10 |
| claude-sonnet-5-5 | 2.00 | 10.00 | 2.50 | 0.20 |

Cost = Σ tokens × price, stored as **integer micro-dollars** (1 USD = 1,000,000) in
`oc_usage_events.cost_micro_usd`. Usage comes from the response's `usage` block (input, output,
cache creation, cache read — `parseUsage` is extended to read all four). A request that reports no
usage is charged its reservation in full. Unknown model → refused before forwarding (never billed
at a guess).

### 5.6 Reserve → settle (fixes the overspend race)

Two Postgres functions, called through PostgREST RPC with the service key:

- `oc_reserve(user_id, estimate_micro_usd) → reservation_id | error`
  In one transaction: lock the user's row and the month's `oc_budget` row; refuse with
  `personal_limit` if `spent + held + estimate > personal_limit`, or `monthly_budget` if the global
  equivalent is crossed; otherwise insert a hold.
- `oc_settle(reservation_id, actual_micro_usd, usage…)` converts the hold into a usage event and
  updates both running totals.
- Holds older than 10 minutes are released by a cheap sweep run at the start of every
  `oc_reserve` (no cron needed).

Estimate = (input tokens counted from the request body: text ≈ chars / 3.5, each image ≈ 1,600)
× input price + `max_tokens` × output price. Worst case is always ≥ the real cost, so a full hold
never lets a request through that the settle would push over.

### 5.7 Limits and abuse protection

| Limit | Default | Where set |
|---|---|---|
| Personal monthly | $10.00 | `ACCOUNT_MONTHLY_USD`; per-user override via admin script (e.g. your own account) |
| Personal daily | $2.00 | `ACCOUNT_DAILY_USD` — no one can spend their month in an afternoon |
| Global monthly | $1,000.00 | `GLOBAL_MONTHLY_BUDGET_USD` — the whole grant; a backstop, since 100 × $10 already equals it |
| Accounts | 100 | `MAX_ACCOUNTS` — sign-up refuses with `accounts_full` past this; raise it when we revisit |
| Requests per minute per user | 20 | in-memory token bucket (single container) |
| Sign-ups | Supabase's built-in rate limit (per IP per hour) | Supabase Auth settings |
| Email must be confirmed | required before any grant spend | Supabase refuses to sign in an unconfirmed email (confirmations on), so no token exists until the link is tapped |

Sign-up itself goes through Supabase, so the 100-account cap is enforced by a database trigger on
`auth.users` insert (counts confirmed-or-pending accounts; refuses past `MAX_ACCOUNTS`), and the
app shows `accounts_full` from `/auth/config`'s `accountsOpen: false` before anyone types an email.

### 5.8 Errors the app can rely on

`402 { error: "personal_limit" | "daily_limit" | "monthly_budget" | "not_on_plan" | "accounts_full", resets_at? }`,
`429 { error: "slow_down" }`. No upstream error text ever reaches the client (existing behaviour).

### 5.9 Schema changes (`backend/supabase/schema.sql`, idempotent)

- `oc_usage_events` + `cost_micro_usd bigint not null default 0`, `reservation_id uuid`.
- `oc_reservations (id uuid pk, user_id, created_at, estimate_micro_usd, settled boolean)`.
- `oc_budget (month date pk, limit_micro_usd bigint, spent_micro_usd bigint, held_micro_usd bigint)`.
- `oc_accounts (user_id pk, monthly_limit_micro_usd bigint null, daily_limit_micro_usd bigint null, blocked boolean default false, created_at)`
  — null limits mean the defaults; a row is created on first use. The invite plan,
  `monthly_credits_override` and the credit columns stop being read (left in place, unused, so old
  rows stay readable); existing invite users simply become accounts with the defaults.
- A trigger on `auth.users` insert enforcing `MAX_ACCOUNTS` (value kept in a one-row `oc_settings`
  table so the admin script can change it).
- The two functions above. RLS stays on with no policies (service key only).

### 5.10 Admin script additions (`npm run admin -w backend -- …`)

`budget` (this month: spent / held / limit, accounts used of 100, top 10 users),
`limit <email> --usd 20` and `daily <email> --usd 5` (per-person overrides, e.g. your own account),
`block <email>` / `unblock`, `remove <email>`, `max-accounts 150`. The `invite` command is removed:
anyone signs up in the app. A warning line is printed to the container log at 50%, 80% and 100% of
the global budget.

## 6. Mac app

### 6.1 Sign-up and sign-in

- New `AccountSheet` (Paper style) used by the walkthrough and Settings → Account: create account /
  sign in / forgot password (Supabase `recover`).
- `OpenClickyAuthSession` gains `signUp(email:password:)` (Supabase `/auth/v1/signup`) and a
  confirmation poll; tokens stored as today in `~/.openclicky/shell.json`.
- After sign-in the app switches to the **account profile** (6.2) automatically.

### 6.2 Account profile — what changes when signed in on a free account

| Area | Today | Free account |
|---|---|---|
| Polish | `BackendTakePolisher` → `/v1/chat/completions`, `gpt-4o-mini` | → `/v1/polish`, no model sent |
| "Also polish takes heard on this mac" | off by default | **on by default** for account users, with the existing "sends text out" tag; can be turned off |
| Hearing a ⌃⌥ question | backend OpenAI transcription | `AppleSpeechTranscriptionProvider` (on-device) |
| Answer voice | ElevenLabs via `/tts` | ElevenLabs via `/tts`; on `402 tts_budget` or any error, `AVSpeechSynthesizer` with the best installed system voice for the reply language |
| Quick actions | realtime tools | Claude tools in the ask lane (§5.3a), performed by `MacActions` |
| Realtime voice / hands-free | on | off; the settings row explains "needs your own OpenAI key" |
| Agent mode | Codex via backend | off; row explains "needs your own key" |
| Claude model picker | user-chosen | hidden (server chooses) |

The profile is decided in one place (`AccountCapabilities`, from `/billing/me`), so every lane asks
the same question instead of each checking keys on its own.

### 6.3 Usage and limits in the UI

- Settings → Account: bar + "plenty left / running low / used up · resets 1 Nov", details on click
  (dollars, polishes, questions this month).
- HUD settings tab summary: "account · 21% used".
- On a 402 the lane shows the exact message from §3.6 once per session and falls back silently
  after that (no repeated nagging).

### 6.4 Languages

Apple's on-device recogniser covers English and several Indian languages on macOS 26; the reply
language setting (`ReplyLanguage`) already pins it. Sarvam (eleven Indian languages) stays a
bring-your-own-key engine in v1 because the grant is Anthropic-only.

## 7. Windows

All of this lives in the backend, so a Windows client later signs in the same way and gets the same
limits and models; only §6 is per-platform.

## 8. Testing

- Backend (vitest): price maths per model incl. cache tokens; reserve refuses at each limit; two
  concurrent reservations cannot both pass the last cent (run against the real functions with a
  test schema); settle releases the hold; stale holds swept; plan gate per route; client `model`
  ignored on the grant; email-unconfirmed refused; `parseUsage` reads Anthropic cache fields.
- App (XCTest/Swift Testing): `AccountCapabilities` mapping from `/billing/me`; polisher picks
  `/v1/polish` when signed in; 402 → correct message + fallback; sign-up flow state machine.
- End to end on the hosted backend with a test account: sign up → confirm → polish → ask → force
  the personal limit with `limit --usd 0.01` → check behaviour → remove the account.

## 9. Rollout

1. Backend + schema behind `ACCOUNTS_OPEN=false` (sign-up closed, existing accounts work); deploy;
   run the e2e test.
2. App 0.8.0 with sign-up hidden behind the same flag from `/auth/config`.
3. Flip `ACCOUNTS_OPEN=true`; watch `admin budget` daily for the first two weeks.

## 10. Open questions for review

Decided 2026-10-08: $2/day and $10/month per person, 100 accounts, $1,000 global (the grant, used
in about a month), invites removed — anyone signs up.

Also decided 2026-10-08: quick actions are in v1 (as Claude tools); answers use ElevenLabs (FlowsXR
has an ElevenLabs grant) with the Mac's voice as the fallback.

1. **ElevenLabs allowance:** the key provided reports the *free* tier (10,000 characters/month,
   ~50 answers) — confirm where the grant's characters are; the fallback keeps things working
   either way.
2. **Grant terms:** confirm the Anthropic grant allows serving end users' requests.

## Out of scope (v1)

Payments (parked for UPI), agent chores on the grant (email, file sorting — anything beyond the six
quick actions), realtime voice on the grant, Sarvam on the grant, a web dashboard.
