# OpenClicky email-first accounts — design

Date: 2026-10-10. Amends `2026-10-08-openclicky-accounts-design.md` (§3 journey, §5.7 limits, §6.1
sign-up). Everything else there — the grant ledger, model policy, tools, ElevenLabs — stands.

## 1. Goal

Installing OpenClicky must not put a login wall in front of the first polished dictation. During
setup the person types **only their email**. The app gets a working account at once, a
confirmation link arrives in their inbox, and confirming raises the allowance. There are no
passwords anywhere; signing in on another Mac is email + a 6-digit code.

Decisions taken with Prasanth (2026-10-10):

- Email asked during setup, used as the account's identity. No password.
- Works immediately, capped at **$1 total until the email is confirmed**; confirmed = $2/day,
  $10/month as before.
- **One Mac, one guest**: a reinstall does not reset the pre-confirmation allowance.
- Codes only: password sign-in, the reset page and the recover flow are removed.

## 2. How it works (Supabase anonymous users)

An account starts as a Supabase **anonymous user** with the email attached as a pending change.
Clicking the link converts it in place to a permanent user with the **same user id**, so spend,
limits and the `oc_accounts` row carry over with no migration. Sign-in elsewhere uses GoTrue's
email OTP. The app never talks to GoTrue directly; the backend fronts every auth call so it can
enforce the caps.

Confirmation state comes from the user's JWT: GoTrue signs `is_anonymous: true` until the email is
confirmed. The backend trusts that claim (it is signed) and records the first time it sees
`false` as `oc_accounts.confirmed_at`.

## 3. The person's journey

1. Onboarding "account" step: one field, *your email — for your free openclicky account*, a
   *continue* button, and *use my own key instead*.
2. Continue → `POST /auth/start`. Typical answer: a session. The app stores it, shows *you're set
   — check your inbox to unlock your full free allowance*, and polish/answers work right away.
3. If that email already has a confirmed account (second Mac, reinstall after confirming), the
   answer is `{ status: "code_sent" }`; the app shows a 6-digit code field → `POST /auth/code`
   → session.
4. They click the link in the email → lands on the existing `/auth/confirmed` page. The app picks
   up the change on its next token refresh (see §5.3) and the HUD/settings drop the "unconfirmed"
   note.
5. If the $1 runs out before they confirm: `402 confirm_email`; the app says *confirm your email to
   keep going* with a *resend link* button.

## 4. Backend

### 4.1 `POST /auth/start { email, device }`

`device` is the app's device hash (§5.1), 64 hex chars; anything else → `400 bad_request`.
Per-IP limit: 5 starts per hour (the existing `signupAllowed` bucket, reused). Email lower-cased
and trimmed; invalid → `400 bad_email`.

In order:

1. **Confirmed account with this email exists** (`oc_accounts.email = email and confirmed_at is
   not null`): call GoTrue `POST /auth/v1/otp { email, create_user: false }` → `{ status:
   "code_sent" }`. Device limits do not apply (signing in is always allowed).
2. **Sign-up closed** (`ACCOUNTS_OPEN != "true"` or `oc_accounts_open()` false) → `402
   accounts_full`.
3. **Device already has 2 confirmed accounts** (other emails) → `402 device_limit`.
4. **Device has a live guest** (unconfirmed, not replaced): mark it `replaced_at = now()` and
   delete its auth user (admin API) so its old confirmation link dies. Its spend stays on the
   device (§4.4).
5. GoTrue `POST /auth/v1/signup {}` (anonymous) → session; then `PUT /auth/v1/user { email }` with
   that session's token and `redirect_to = ACCOUNT_CONFIRM_REDIRECT_URL` (GoTrue sends the
   confirmation link). If GoTrue says the email is taken (an unconfirmed-then-confirmed race),
   delete the new anonymous user and fall back to step 1.
6. Insert `oc_accounts { user_id, email, device_hash }` (retry once, as today). Return `{ status:
   "signed_in", session: { access_token, refresh_token, expires_at }, confirmed: false }`.

GoTrue failures → `502 auth_unavailable` with nothing upstream leaked (existing pattern).

### 4.2 `POST /auth/code { email, code }`

GoTrue `POST /auth/v1/verify { type: "email", email, token: code }`. Success → `{ status:
"signed_in", session, confirmed: true }`. Wrong/expired → `401 bad_code`. Per-IP limit 10 per
hour.

### 4.3 `POST /auth/resend` (Bearer token of a guest)

GoTrue `POST /auth/v1/resend { type: "email_change", email }` for the caller's pending email. 3 per
hour per user. A confirmed caller → `{ status: "already_confirmed" }`.

### 4.4 Limits

| Limit | Default | Setting |
|---|---|---|
| Before confirming, per Mac (lifetime) | $1.00 | `GUEST_TOTAL_USD` |
| Confirmed, daily / monthly | $2 / $10 | unchanged |
| Confirmed accounts | 100 | `MAX_ACCOUNTS` (now counts confirmed only) |
| Live guests | 300 | `MAX_GUESTS` (unconfirmed, not replaced, younger than 14 days) |
| Confirmed accounts per Mac | 2 | `MAX_ACCOUNTS_PER_DEVICE` |
| Guest lifetime | 14 days | `GUEST_DAYS`; after that a guest gets `402 confirm_email` until confirmed |
| Global monthly | $1,000 | unchanged |

**Pre-confirmation spend for a device** = the sum of `cost_micro_usd` over usage events of every
account with that `device_hash`, counting only events before that account's `confirmed_at` (all of
them if it never confirmed). `oc_reserve` gains `p_confirmed boolean` (from the JWT) and
`p_guest_total bigint`:

- If `p_confirmed` and the row's `confirmed_at` is null, set it to `now()` first.
- If the account is unconfirmed: refuse with `confirm_email` when device pre-confirmation spend +
  estimate > `p_guest_total`, or the account is older than `GUEST_DAYS`, or it is `replaced`.
  Daily/monthly personal limits still apply too.
- Confirmed: today's checks, unchanged.

`oc_accounts_open()` becomes: confirmed count < `max_accounts` **and** live guests < `max_guests`.
Confirmations can push the confirmed count past 100 by at most the live guests at that moment;
accepted — the global $1,000 budget is the hard backstop.

`/billing/me` adds `confirmed: boolean`, `guestLimitUsd`, `guestSpentUsd` (device total), and
`email` (masked as `p•••@flowsxr.com`, for the settings page).

### 4.5 Removed

`POST /auth/signup`, the `/auth/reset` page and `ACCOUNT_RESET_REDIRECT_URL`. The `/auth/confirmed`
page stays (copy: *your openclicky account is confirmed — you can close this tab*).

### 4.6 Schema (`backend/supabase/schema.sql`, idempotent)

- `oc_accounts` + `email text`, `device_hash text`, `confirmed_at timestamptz`, `replaced_at
  timestamptz`; index on `email`, index on `device_hash`.
- `oc_settings` + `max_guests int default 300`, `max_accounts_per_device int default 2`.
- `oc_reserve` / `oc_spend_summary` signatures as in §4.4 (drop-and-recreate; the backend and
  schema deploy together).
- Admin script: `admin prune` deletes auth users of guests that are expired or replaced (rows
  stay, as tombstones for device accounting); `admin add` now takes an email and creates a
  confirmed account directly.

### 4.7 Supabase project settings (Cloud project `hcmnepxtfngtrlbknumu`, dedicated)

- Enable anonymous sign-ins.
- `rate_limit_anonymous_users` raised to 300/hour: every anonymous sign-up comes from the
  backend's single IP, and the backend does the per-IP limiting itself.
- Email OTP length 6, expiry 15 minutes.
- Templates: *Change email address* becomes "Confirm your OpenClicky email" (link); *Magic link*
  becomes "Your OpenClicky code: {{ .Token }}" (code only, no link).

## 5. Mac app

### 5.1 Device hash

`SHA-256("openclicky-device-v1:" + IOPlatformUUID)` as lowercase hex, computed in a small
`DeviceIdentity` helper (IOKit `IOPlatformExpertDevice`). The raw UUID never leaves the Mac. Both
Apple-silicon and Intel Macs have one.

### 5.2 Onboarding and account sheet

- Onboarding `account` step: email field + continue; *use my own key instead* stays. States:
  idle → sending → signed in (unconfirmed) | code needed → signed in. Errors shown inline in
  plain words (`accounts_full` → *free accounts are full right now — you can still use your own
  key*; `device_limit`; `bad_email`; `bad_code`; network).
- `AccountSheet`: same email → (code) flow for signing in later from settings. Password fields
  and *forgot password* are removed.
- Settings account page: *signed in as p•••@…*, *unconfirmed — check your inbox* with *resend
  link*, guest allowance used, sign out.

### 5.3 Picking up confirmation

While `confirmed == false`, the app force-refreshes its session every 60 s for 15 minutes after
`/auth/start` or *resend*, and on every app activation; a refreshed token carries
`is_anonymous: false` once the link is clicked. `AccountProfileStore` refreshes `/billing/me`
after each refresh. `402 confirm_email` maps to the *confirm your email to keep going* message
with *resend link*.

### 5.4 Windows

The device hash on Windows will be `SHA-256("openclicky-device-v1:" + MachineGuid)`; the backend
contract is platform-neutral.

## 6. Testing

- Backend (vitest, fake GoTrue + fake db): each `/auth/start` branch (existing confirmed email →
  code; closed; device limit; live guest replaced and its auth user deleted; taken-email race;
  insert retry; bad email; bad device; per-IP limit); `/auth/code` success/bad code;
  `/auth/resend`; `p_confirmed` sets `confirmed_at` once; guest cap counted per device across a
  replaced guest; expired guest → `confirm_email`; `/billing/me` new fields.
- SQL: the schema applies twice cleanly; `oc_reserve` guest cap on the live Cloud project during
  the e2e run.
- Swift: `DeviceIdentity` is stable and 64 hex; onboarding state machine for each answer;
  `confirm_email` message; no password UI left (`AccountSheetTests` updated).
- Live e2e against the Cloud project with a `+tag` address: start → work unconfirmed → hit the $1
  cap (lowered via env for the test) → click link → refresh shows confirmed → second start with the
  same email → code → signed in. Then delete the test users and rows.

## 7. Rollout

1. Supabase settings (§4.7) and schema on the Cloud project.
2. Deploy the backend (`GRANT_ACCOUNTS=true`, `ACCOUNTS_OPEN=false`), `admin add` Prasanth's email.
3. Release 0.8.0 (universal), test on the 2019 Intel Mac.
4. `ACCOUNTS_OPEN=true`, redeploy. Revoke the Supabase access token.

## Out of scope

Passwords, social sign-in, account deletion from the app (admin only for now), Windows client
work, moving the pre-confirmation allowance between devices.
