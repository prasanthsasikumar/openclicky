# Free accounts on the grant: runbook

How to turn on OpenClicky's free accounts on the hosted backend, and how to look after them
afterwards. Design: `docs/superpowers/specs/2026-10-10-openclicky-email-first-accounts-design.md`
(email-first, which replaced the password sign-up of
`docs/superpowers/specs/2026-10-08-openclicky-accounts-design.md`). Plan:
`docs/superpowers/plans/2026-10-10-openclicky-email-first-accounts.md`.

## 0. Where the accounts live

OpenClicky has its **own Supabase Cloud project**: `openclicky`, ref `hcmnepxtfngtrlbknumu`, region
`ap-northeast-1` (Tokyo), URL `https://hcmnepxtfngtrlbknumu.supabase.co`. Its `auth.users`, Auth
settings and email sending belong to OpenClicky alone, so the steps below need no sign-off from
other products. It replaces the shared self-hosted Supabase at db.flowsxr.com, which OpenClicky no
longer uses: do not apply `oc_*` schema or Auth changes there.

Credentials (all git-ignored, mode 600, only in the main checkout):

- `backend/.supabase-cloud.secrets`: `SUPABASE_PROJECT_REF`, `SUPABASE_URL`, keys, `SUPABASE_DB_URL`,
  and `SUPABASE_ACCESS_TOKEN` (a personal Management API token, needed only for §4; revoke it after
  rollout, §7 step 7).
- `backend/.hosted.vars`: the hosted backend's env (§2).

Load either with `set -a; . <file>; set +a` in the same command that uses it; never echo them.

## 1. How sign-in works (email-first)

The app asks for an email and nothing else, and sends it with this Mac's device hash to
`POST /auth/start`:

- **New email:** the backend makes an anonymous Supabase user, attaches the email (Supabase mails a
  confirmation link, the "email change" template) and returns a session at once: a **guest**. A guest
  can spend `GUEST_TOTAL_USD` (default $1) and `GUEST_TTS_CHARS` spoken characters **per Mac**, shared
  by every guest that Mac has made, for `GUEST_DAYS`. Past that, metered calls answer
  `402 confirm_email`. Clicking the link flips the user's `is_anonymous` to false; from the next
  request on the account is confirmed and gets the normal monthly limit (no daily limit unless ACCOUNT_DAILY_USD sets one).
- **New email, but no room for another guest:** at most `max_guests_per_ip` (2) live guests per
  address (an IPv4 host, or an IPv6 /64) and `max_guests_per_net` (6) per network (an IPv4 /24, or
  an IPv6 /48), and `max_guests` in all. Guests already on the asking Mac don't count against its
  address. Past a cap no guest is made: Supabase mails a 6-digit sign-up code (the "confirm signup"
  template) and `/auth/start` answers `code_sent`, so newcomers still get in, but only with a mailbox
  they can read. (Before 2026-10-11 a full pool answered `accounts_full` and locked everyone out.)
  Rows keep keyed hashes of the address and network (`ip_hash`, `net_hash`), never the address.
- **Same email again on the same Mac, still unconfirmed:** a new guest replaces the old one (old
  row gets `replaced_at`, old auth user is deleted). The Mac's guest pool is not reset.
- **Email that already has a confirmed account:** Supabase mails a 6-digit code (the "magic link"
  template) and `/auth/start` answers `code_sent`; the app sends the code to `POST /auth/code`.
  The **first** code sign-in on an account signs out every other session on it (`code_signed_in_at`
  is stamped), so a stranger who typed the email and had the link clicked for them loses it.
  Later code sign-ins (a second Mac) sign out nothing. A row without an email gets it here.
- `POST /auth/resend` re-sends the confirmation mail for the signed-in guest.
- `POST /auth/confirm {code}` (guest session required): the guest types the code from the
  confirmation mail. Only someone with both the mailbox and that Mac can confirm. This needs the
  "email change" template to carry the code (§4) and an app that has the code box; until then
  the link confirms on its own, as before.
- Attempt limits per hour, kept in Postgres (`oc_rate_hits`, so a deploy doesn't reset them):
  `/auth/start` 30 per address and 5 per email; `/auth/code` 60 per address and 10 per email;
  `/auth/confirm` 10 and `/auth/resend` 3 per user. A class behind one address fits. Every 429
  carries `Retry-After`.
- If the caps can't be read (a database hiccup), `/auth/start` and `/auth/code` answer
  `503 accounts_unavailable` and `/auth/config` says `accountsOpen: null`, never "full".

There are no passwords and no reset page.

## 2. The hosted env file

The hosted backend's settings live in **`backend/.hosted.vars`**. They are kept apart from
`backend/.dev.vars`, which the local launchd backend on :8787 reads: the grant key and
`GRANT_ACCOUNTS=true` must never end up in the local backend.

```
# backend/.hosted.vars: accounts settings
ANTHROPIC_API_KEY=<the Anthropic grant key>
ANTHROPIC_BASE_URL=https://api.anthropic.com     # pinned so nothing in .dev.vars can redirect it
GRANT_ACCOUNTS=true
ACCOUNTS_OPEN=false
ACCOUNT_MONTHLY_USD=10
ACCOUNT_DAILY_USD=2
GLOBAL_MONTHLY_BUDGET_USD=1000
ACCOUNT_MONTHLY_TTS_CHARS=200000
MAX_ACCOUNTS=100
ACCOUNT_CONFIRM_REDIRECT_URL=https://api.openclicky.flowsxr.com/auth/confirmed
SUPABASE_URL=https://hcmnepxtfngtrlbknumu.supabase.co
SUPABASE_PUBLISHABLE_KEY=<sb_publishable_…>
SUPABASE_SERVICE_KEY=<sb_secret_…>
SUPABASE_JWT_SECRET=<project JWT secret>
# guests (all optional; defaults shown)
GUEST_TOTAL_USD=1            # pre-confirmation allowance per Mac, dollars
GUEST_DAYS=14                # days a guest may stay unconfirmed
GUEST_TTS_CHARS=30000        # spoken characters per Mac before confirming
MAX_ACCOUNTS_PER_DEVICE=2    # confirmed accounts one Mac may create
IP_HASH_SALT=<random 32+ chars>  # optional; keys the address hashes (default: the service key, so rotating that resets the per-address caps)
```

The guest allowance is the money one made-up guest can spend. With the per-address caps a farm
needs many addresses, but each still gets `GUEST_TOTAL_USD`; a smaller value (`0.25`) shrinks the
exposure without code changes, at the cost of a shorter try-out before confirming.

`ACCOUNT_RESET_REDIRECT_URL` is no longer read and `FREE_MONTHLY_CREDITS` is unused in grant mode (only
the legacy Stripe path reads it); delete them from the file. `/auth/config` no longer returns a `resetRedirectUrl`.

| Setting | Effect |
|---|---|
| `GRANT_ACCOUNTS=true` | Requests without the user's own key are metered by the spend ledger and limited to Claude and ElevenLabs. **Without it the backend is unmetered** (every capability, nothing counted) even when Supabase is configured, unless it also has `STRIPE_SECRET_KEY`: a backend that sells Stripe plans enforces their monthly credits (`402 credits_exhausted`) and records usage. This keeps the local launchd backend and any self-hosted backend as they were. With no service key (no ledger) metered requests get a 503 rather than being given away. |
| `ACCOUNTS_OPEN=true` | New emails can become guests. Closed: `/auth/start` for a new email answers `accounts_full`; existing confirmed accounts still get their code and keep working. |
| `oc_settings.max_accounts` | Cap on confirmed accounts (default 100, `admin max-accounts N`). |
| `oc_settings.max_guests` | Cap on live guests (unconfirmed, not replaced, younger than `GUEST_DAYS`; default 300). Past it newcomers get a sign-up code instead of a guest. Change it in SQL. |
| `oc_settings.max_guests_per_ip`, `max_guests_per_net` | Live guests per address (default 2) and per network (default 6); past them, a sign-up code. Change in SQL. |

Deploy it with:

```bash
export OPENCLICKY_SERVER=root@api.openclicky.flowsxr.com
DEPLOY_ENV_FILE=backend/.hosted.vars npm run deploy:backend -- --env
```

Plain `npm run deploy:backend -- --env` uploads `backend/.dev.vars`; never use it for the hosted
backend.

## 3. Apply the schema

`backend/supabase/schema.sql` is idempotent and only touches `oc_*` objects in `public` (it reads
`auth.users`). From the main checkout:

```bash
set -a; . backend/.supabase-cloud.secrets; set +a
PGCONNECT_TIMEOUT=15 psql "$SUPABASE_DB_URL" -v ON_ERROR_STOP=1 -f backend/supabase/schema.sql
psql "$SUPABASE_DB_URL" -c '\df public.oc_*'   # one row per function, no stale overloads
```

`PGCONNECT_TIMEOUT` matters: the direct `db.<ref>.supabase.co` connection from this Mac sometimes
hangs while connecting; with the timeout it fails fast and a retry goes through.

Function signatures change between versions (the schema drops the old ones), so a backend from
before a schema change may not be able to call the new functions: **apply the schema and deploy
the backend in the same sitting.** Since 2026-10-11 the backend needs `oc_signup_room`,
`oc_rate_hit` and the new `oc_accounts` columns (`ip_hash`, `net_hash`, `code_signed_in_at`):
apply the schema **first** (the older backend ignores them), then deploy.

## 4. Supabase Auth settings

Set through the Management API (`SUPABASE_ACCESS_TOKEN` from the secrets file):

```bash
curl -s -X PATCH "https://api.supabase.com/v1/projects/$SUPABASE_PROJECT_REF/config/auth" \
  -H "Authorization: Bearer $SUPABASE_ACCESS_TOKEN" -H 'content-type: application/json' \
  --data @auth-settings.json | jq -c '{external_anonymous_users_enabled, mailer_otp_length}'

# read back all nine fields
curl -s "https://api.supabase.com/v1/projects/$SUPABASE_PROJECT_REF/config/auth" \
  -H "Authorization: Bearer $SUPABASE_ACCESS_TOKEN" \
  | jq '{external_anonymous_users_enabled, rate_limit_anonymous_users, mailer_otp_length, mailer_otp_exp, mailer_subjects_email_change, mailer_templates_email_change_content, mailer_subjects_magic_link, mailer_templates_magic_link_content, mailer_secure_email_change_enabled}'
```

with `auth-settings.json`:

```json
{
  "external_anonymous_users_enabled": true,
  "rate_limit_anonymous_users": 300,
  "mailer_otp_length": 6,
  "mailer_otp_exp": 3600,
  "mailer_subjects_email_change": "Confirm your OpenClicky email",
  "mailer_templates_email_change_content": "<p>Hi! Confirm this email for your free OpenClicky account:</p><p><a href=\"{{ .ConfirmationURL }}\">Confirm my email</a></p><p>If you didn't ask for this, ignore this email.</p>",
  "mailer_subjects_magic_link": "Your OpenClicky code",
  "mailer_templates_magic_link_content": "<p>Your OpenClicky sign-in code is</p><h2>{{ .Token }}</h2><p>It works for an hour. If you didn't ask for it, ignore this email.</p>",
  "mailer_secure_email_change_enabled": false
}
```

Why: guests are anonymous users (anonymous sign-ins on, 300 per hour per IP); the confirmation
link is the email-change mail, and secure email change is off because an anonymous user has no
old address to confirm from; existing accounts sign in with a 6-digit code.
`mailer_otp_exp` is 3600 (1 hour): GoTrue uses one expiry for both the codes and the confirmation
links, so 15 minutes would also kill a link opened later that hour.

Two template changes for the 2026-10-11 sign-up guard (not applied yet):

- **Now, with the backend deploy:** the "confirm signup" template (`mailer_subjects_confirmation`,
  `mailer_templates_confirmation_content`) must show the code, because a newcomer past the guest
  caps signs up with one: subject "Your OpenClicky code", body as the magic-link one above
  (`{{ .Token }}`). Without it they get a link and no code to type.
- **Once an app with the guest code box has shipped and spread:** the "email change" template
  should carry the code instead of the link, so the link can no longer confirm whoever typed the
  email: `<p>Your OpenClicky confirmation code is</p><h2>{{ .Token }}</h2><p>Type it into
  openclicky. If you didn't ask for this, ignore this email.</p>`. Older apps can't type it, so
  their guests could not confirm: hold this until most people have updated.

Check the fields read back. Already in place and unchanged:
`mailer_autoconfirm=false`, `site_url=https://api.openclicky.flowsxr.com`, the `uri_allow_list`
containing `https://api.openclicky.flowsxr.com/auth/confirmed`, SMTP through Resend from
hello@flowsxr.com, `rate_limit_email_sent=30` per hour (raise it before opening sign-up widely: every
guest sends one email). The allow-list's old `/auth/reset` entry is unused and can go.

## 5. Accounts that exist before sign-up opens

`npm run admin -w backend -- add <email>` creates a confirmed account (and the Supabase login if
missing). That person then signs in on the Mac with the email and the emailed code. `admin list`
shows everyone with a row.

The admin script reads `backend/.dev.vars`, which is the local backend's. To run it against the
Cloud project, export the hosted values first (exported values win over the file):

```bash
set -a; . backend/.hosted.vars; set +a; npm run admin -w backend -- add <email>
```

Confirming a guest by hand (support case, link lost): through the Auth admin API,
`PUT /auth/v1/admin/users/<id>` with `{"email": "<their email>", "email_confirm": true}`.
`{"email_confirm": true}` alone is **not enough**: it stamps `email_confirmed_at` but leaves the
email pending and `is_anonymous` true, so OpenClicky still treats the user as a guest (seen live on
2026-10-10).

## 6. End-to-end check and test-user cleanup

Run against a **local** backend from the checkout being tested, with the hosted env and overrides
for that run only:

```bash
cd backend
set -a; . ./.hosted.vars; set +a
ACCOUNTS_OPEN=true GUEST_TOTAL_USD=0.002 PORT=8790 npx tsx src/node.ts
```

(`src/node.ts` backfills from `.dev.vars` and `../.env` but never overrides exported values.) Use a
test address on a domain you own (`prasanth+ocguest@flowsxr.com`) and check: `/auth/start` →
`signed_in`, `confirmed:false`; `/v1/polish` until `402 confirm_email`; `/auth/start` again replaces
the guest and the new one is still refused once the Mac's pool is spent; after confirming,
`/billing/me` says `confirmed:true` and polish works; the same email from another device hash →
`code_sent`.

Clean up afterwards: delete the test auth users (`DELETE /auth/v1/admin/users/<id>`) and their
`oc_accounts`, `oc_usage_events` and `oc_reservations` rows, then check the counts.
`admin remove <email>` removes only the `oc_accounts` row.

## 7. Rollout order

1. Apply the schema (§3).
2. Auth settings (§4).
3. Deploy the backend with `GRANT_ACCOUNTS=true`, `ACCOUNTS_OPEN=false` (§2).
4. `admin add <email>` for Prasanth and anyone who should have the grant from day one (§5).
5. Release the app, 0.8.0.
6. With Prasanth's go-ahead: `ACCOUNTS_OPEN=true` in `backend/.hosted.vars`, deploy, and check
   `curl -s https://api.openclicky.flowsxr.com/auth/config` → `"accountsOpen": true`.
7. Revoke `SUPABASE_ACCESS_TOKEN` (supabase.com → Account → Access Tokens) and remove it from
   `backend/.supabase-cloud.secrets`. Make a new one when Auth settings next need changing.

To close sign-up again, set `ACCOUNTS_OPEN=false` and deploy. Existing accounts keep working.

## 8. Every day, every week

```bash
set -a; . backend/.hosted.vars; set +a
npm run admin -w backend -- budget     # daily: month's spend vs GLOBAL_MONTHLY_BUDGET_USD, characters, accounts, top 10
npm run admin -w backend -- prune      # weekly: lists stale guest logins (replaced, or older than GUEST_DAYS), asks before deleting
                                       # (--yes skips the question)
```

Watch `budget` daily for the first two weeks after opening. Run `prune` **weekly**: replaced and
expired guests can't spend, but their anonymous logins stay in `auth.users` until pruned. Prune
deletes only anonymous users; a sign-up code that was never typed leaves an unconfirmed,
non-anonymous auth user with no `oc_accounts` row, which prune does not touch (harmless: it can't
spend). Per-person controls: `limit`, `daily`, `block` / `unblock`,
`max-accounts` (see the header of `backend/scripts/admin.mjs`).
