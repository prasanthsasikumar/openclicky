# Free accounts on the grant: runbook

How to turn on OpenClicky's free accounts on the hosted backend, and how to look after them
afterwards. These are the ops steps of plan Tasks 9 and 16
(`docs/superpowers/plans/2026-10-08-openclicky-accounts.md`). Design:
`docs/superpowers/specs/2026-10-08-openclicky-accounts-design.md`.

> **The Supabase at db.flowsxr.com is shared by every FlowsXR product.** Its `auth.users`, its
> GoTrue settings and its email sending belong to all of them, not to OpenClicky. Every step
> below that touches Auth settings is marked **shared**: check with the operator (Prasanth) before
> changing it, and say which other products it affects. Infra runbook:
> `flowsxr-hq/infra/README.md`; quick values: `~/Documents/GitHub/SUPABASE.md`.

## 1. What switches the feature on

| Setting | Where | Effect |
|---|---|---|
| `GRANT_ACCOUNTS=true` | hosted backend env | Requests without the user's own key are metered by the spend ledger and limited to Claude and ElevenLabs. **Without it the backend is unmetered** (every capability, nothing counted) even when Supabase is configured. This is what keeps the local launchd backend and any self-hosted backend as they were. |
| `ACCOUNTS_OPEN=true` | hosted backend env | Opens self-serve sign-up (`POST /auth/signup`), and the app shows "create a free account". Closed: existing accounts keep working; sign-up answers `accounts_full`; the app hides the create option. |
| `oc_settings.max_accounts` | database (`admin max-accounts N`) | Cap on confirmed OpenClicky accounts (default 100). |

`GRANT_ACCOUNTS=true` with no Supabase service key (no ledger) refuses metered requests with a 503
rather than giving them away.

## 2. The hosted env file

The hosted backend's settings live in **`backend/.hosted.vars`** (git-ignored, keep it mode 600).
They are kept apart from `backend/.dev.vars`, which the local launchd backend on :8787 reads: the
grant key and `GRANT_ACCOUNTS=true` must never end up in the local backend.

```
# backend/.hosted.vars — in addition to the existing hosted settings
ANTHROPIC_API_KEY=<the Anthropic grant key>      # straight to api.anthropic.com: no ANTHROPIC_BASE_URL,
                                                 # ANTHROPIC_MODEL_PREFIX or OpenRouter MODEL_ALIASES
GRANT_ACCOUNTS=true
ACCOUNTS_OPEN=false
ACCOUNT_MONTHLY_USD=10
ACCOUNT_DAILY_USD=2
GLOBAL_MONTHLY_BUDGET_USD=1000
ACCOUNT_MONTHLY_TTS_CHARS=20000
MAX_ACCOUNTS=100
ACCOUNT_CONFIRM_REDIRECT_URL=https://api.openclicky.flowsxr.com/auth/confirmed
ACCOUNT_RESET_REDIRECT_URL=https://api.openclicky.flowsxr.com/auth/reset
```

`FREE_MONTHLY_CREDITS` is no longer read; drop it.

Deploy it with:

```bash
export OPENCLICKY_SERVER=root@api.openclicky.flowsxr.com
DEPLOY_ENV_FILE=backend/.hosted.vars npm run deploy:backend -- --env
```

Plain `npm run deploy:backend -- --env` still uploads `backend/.dev.vars`; do not use it for the
hosted backend any more.

## 3. Apply the schema (shared database)

`backend/supabase/schema.sql` is idempotent and only touches `oc_*` objects in `public`. Load it with
the procedure in `SUPABASE.md` ("load a schema file").

- `oc_settle` gained a `p_user` parameter: the schema drops the old signature and creates the new
  one. A backend from before that change cannot settle against the new function, so **apply the
  schema and deploy the backend in the same sitting**.
- Verify: `npm run admin -w backend -- budget` →
  `this month: $0.00 of $1000 · 0 spoken characters · accounts N of 100`.

## 4. Supabase Auth settings (shared — check first)

The self-hosted GoTrue serves every FlowsXR product, so each of these changes reaches all of them.

1. **Email confirmations on: `GOTRUE_MAILER_AUTOCONFIRM=false`.** If another product relies on
   autoconfirm (its users sign in straight after sign-up), turning it off breaks that product's
   sign-up. Ask first. If some product needs autoconfirm, confirmation cannot be switched on
   globally and has to be handled per product. OpenClicky's backend would then have to refuse
   unconfirmed accounts itself. That is a design change, so stop and raise it rather than improvise.
2. **Email rate limits** (`GOTRUE_RATE_LIMIT_EMAIL_SENT` and friends) are shared too: a burst of
   OpenClicky sign-ups can use up the hourly allowance for everyone's confirmation and reset
   emails. Agree a number with the operator before raising or relying on it.
3. **Redirect allow-list: add both pages to `GOTRUE_URI_ALLOW_LIST`:**
   - `https://api.openclicky.flowsxr.com/auth/confirmed`: the confirmation link lands here.
   - `https://api.openclicky.flowsxr.com/auth/reset`: the password-reset link lands here. The
     page reads the recovery token from the URL fragment and sets the new password with
     `PUT /auth/v1/user`.

   Without these, GoTrue falls back to its site URL (another product's page).
4. Restart the auth container (see the infra runbook).
5. Check with a throwaway sign-up through the backend (sign-up open only on a local run, see §6):
   the email arrives from hello@flowsxr.com, a password sign-in before the link fails with "Email
   not confirmed", the link lands on `/auth/confirmed`, and "forgot password" sends a link that
   lands on `/auth/reset` and changes the password.

## 5. Accounts that exist before sign-up opens

OpenClicky accounts are rows in `oc_accounts`. Being a Supabase user is not enough: a user of
another FlowsXR product has no row and is refused (`not_on_plan`). The app tells them "this login
isn't switched on for openclicky yet".

- Give Prasanth's own login, and any existing user who should have the grant, a row:
  `npm run admin -w backend -- add <email>`.
- `admin list` shows everyone with a row.

## 6. End-to-end check and test-user cleanup

Run the plan's Task 16 Step 1 against a **local** backend with `ACCOUNTS_OPEN=true` and
`GRANT_ACCOUNTS=true` set only for that run, with the app pointed at it. Then clean up:

- `npm run admin -w backend -- remove <test email>` removes **only the OpenClicky row**. It never
  deletes the auth user, because the auth users are shared.
- **Delete the test auth user by hand in the Supabase dashboard** (Authentication → Users) at
  db.flowsxr.com, after checking it is the test address and nothing else uses it.

## 7. Opening sign-up

With Prasanth's go-ahead:

1. Set `ACCOUNTS_OPEN=true` in `backend/.hosted.vars`.
2. `DEPLOY_ENV_FILE=backend/.hosted.vars npm run deploy:backend -- --env`
3. `curl -s https://api.openclicky.flowsxr.com/auth/config` → `"accountsOpen": true` and a
   `resetRedirectUrl`.

To close it again, set `ACCOUNTS_OPEN=false` and deploy. Existing accounts keep working.

## 8. Every day

```bash
npm run admin -w backend -- budget     # month's spend vs GLOBAL_MONTHLY_BUDGET_USD, characters, accounts, top 10
```

Watch it daily for the first two weeks after opening. Per-person controls: `limit`, `daily`,
`block` / `unblock`, `max-accounts` (see the header of `backend/scripts/admin.mjs`).
