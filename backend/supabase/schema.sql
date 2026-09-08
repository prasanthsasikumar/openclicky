-- OpenClicky billing tables (shared Supabase, "oc_" prefix in public). Idempotent.
-- Load: see SUPABASE.md "load a schema file". Only the service key touches these tables.
create table if not exists public.oc_plans (
  id text primary key,
  name text not null,
  monthly_credits integer not null,
  stripe_price_id text unique
);

create table if not exists public.oc_subscriptions (
  user_id text primary key,
  plan_id text not null references public.oc_plans(id),
  status text not null,                      -- active | trialing | past_due | canceled | unpaid
  current_period_start timestamptz not null,
  current_period_end timestamptz not null,
  stripe_customer_id text,
  stripe_subscription_id text unique,
  updated_at timestamptz not null default now()
);

create table if not exists public.oc_usage_events (
  id bigserial primary key,
  user_id text not null,
  ts timestamptz not null default now(),
  route text not null,
  model text,
  input_tokens integer not null default 0,
  output_tokens integer not null default 0,
  audio_seconds numeric not null default 0,
  characters integer not null default 0,
  credits numeric not null
);
create index if not exists oc_usage_events_user_ts on public.oc_usage_events (user_id, ts desc);

-- Users never read these directly; RLS with no policies means only the service key can.
alter table public.oc_plans enable row level security;
alter table public.oc_subscriptions enable row level security;
alter table public.oc_usage_events enable row level security;

insert into public.oc_plans (id, name, monthly_credits, stripe_price_id) values
  ('free', 'Free', 200, null),
  ('starter', 'Starter', 3000, null),
  ('pro', 'Pro', 12000, null)
on conflict (id) do nothing;

-- Invite-only accounts (2026-09-08): a per-user allowance that overrides the plan's monthly credits.
alter table public.oc_subscriptions add column if not exists monthly_credits_override integer;
-- Invitees get this plan with an override; it has no Stripe price.
insert into public.oc_plans (id, name, monthly_credits, stripe_price_id) values ('invite', 'Invite', 1000, null)
on conflict (id) do nothing;
