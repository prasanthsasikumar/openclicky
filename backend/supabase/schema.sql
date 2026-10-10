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

-- Accounts on the Anthropic grant (2026-10-08). Money in micro-dollars; months/days in UTC.
alter table public.oc_usage_events add column if not exists cost_micro_usd bigint not null default 0;
alter table public.oc_usage_events add column if not exists cache_write_tokens integer not null default 0;
alter table public.oc_usage_events add column if not exists cache_read_tokens integer not null default 0;
create index if not exists oc_usage_events_ts on public.oc_usage_events (ts);

create table if not exists public.oc_accounts (
  user_id text primary key,
  monthly_limit_micro_usd bigint,
  daily_limit_micro_usd bigint,
  blocked boolean not null default false,
  created_at timestamptz not null default now()
);
create table if not exists public.oc_reservations (
  id uuid primary key default gen_random_uuid(),
  user_id text not null,
  created_at timestamptz not null default now(),
  estimate_micro_usd bigint not null
);
create index if not exists oc_reservations_user on public.oc_reservations (user_id, created_at);
create table if not exists public.oc_settings (
  id boolean primary key default true check (id),
  max_accounts integer not null default 100
);
insert into public.oc_settings (id) values (true) on conflict (id) do nothing;
alter table public.oc_accounts enable row level security;
alter table public.oc_reservations enable row level security;
alter table public.oc_settings enable row level security;

create or replace function public.oc_reserve(p_user text, p_estimate bigint, p_monthly bigint, p_daily bigint, p_global bigint)
returns json language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_day timestamptz := date_trunc('day', now() at time zone 'utc') at time zone 'utc';
  v_acct oc_accounts%rowtype;
  v_today bigint; v_month_spent bigint; v_everyone bigint; v_id uuid;
begin
  -- One reservation at a time across all users: the global pool is shared, so per-user locks are not enough.
  perform pg_advisory_xact_lock(hashtext('oc_reserve'));
  delete from oc_reservations where created_at < now() - interval '10 minutes';
  select * into v_acct from oc_accounts where user_id = p_user;
  if not found then
    return json_build_object('ok', false, 'error', 'not_on_plan', 'resetsAt', v_month + interval '1 month');
  end if;
  if v_acct.blocked then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  select coalesce(sum(cost_micro_usd), 0) into v_today from oc_usage_events where user_id = p_user and ts >= v_day;
  select coalesce(sum(cost_micro_usd), 0) into v_month_spent from oc_usage_events where user_id = p_user and ts >= v_month;
  select coalesce(sum(cost_micro_usd), 0) into v_everyone from oc_usage_events where ts >= v_month;
  v_today := v_today + coalesce((select sum(estimate_micro_usd) from oc_reservations where user_id = p_user and created_at >= v_day), 0);
  v_month_spent := v_month_spent + coalesce((select sum(estimate_micro_usd) from oc_reservations where user_id = p_user), 0);
  v_everyone := v_everyone + coalesce((select sum(estimate_micro_usd) from oc_reservations), 0);
  if v_today + p_estimate > coalesce(v_acct.daily_limit_micro_usd, p_daily) then
    return json_build_object('ok', false, 'error', 'daily_limit', 'resetsAt', v_day + interval '1 day');
  end if;
  if v_month_spent + p_estimate > coalesce(v_acct.monthly_limit_micro_usd, p_monthly) then
    return json_build_object('ok', false, 'error', 'personal_limit', 'resetsAt', v_month + interval '1 month');
  end if;
  if v_everyone + p_estimate > p_global then
    return json_build_object('ok', false, 'error', 'monthly_budget', 'resetsAt', v_month + interval '1 month');
  end if;
  insert into oc_reservations (user_id, estimate_micro_usd) values (p_user, p_estimate) returning id into v_id;
  return json_build_object('ok', true, 'reservationId', v_id);
end $$;

-- oc_settle gained p_user (2026-10-10): the old signature would linger as an overload.
drop function if exists public.oc_settle(uuid, bigint, text, text, integer, integer, integer, integer, integer);
create or replace function public.oc_settle(p_reservation uuid, p_user text, p_actual bigint, p_route text, p_model text,
  p_input integer, p_output integer, p_cache_write integer, p_cache_read integer, p_chars integer)
returns void language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare v_user text;
begin
  perform pg_advisory_xact_lock(hashtext('oc_reserve'));
  delete from oc_reservations where id = p_reservation returning user_id into v_user;
  -- Swept after 10 minutes: the hold has expired, but a reply that slow still cost money, so it is still billed.
  v_user := coalesce(v_user, p_user);
  insert into oc_usage_events (user_id, route, model, input_tokens, output_tokens, cache_write_tokens, cache_read_tokens, characters, credits, cost_micro_usd)
  values (v_user, p_route, p_model, p_input, p_output, p_cache_write, p_cache_read, p_chars, 0, p_actual);
end $$;

create or replace function public.oc_reserve_chars(p_user text, p_chars integer, p_limit integer, p_global_remaining integer)
returns json language plpgsql security definer set search_path = public set timezone = 'UTC' as $$
declare
  v_month timestamptz := date_trunc('month', now() at time zone 'utc') at time zone 'utc';
  v_used integer;
begin
  perform pg_advisory_xact_lock(hashtext('oc_reserve_chars'));
  if not exists (select 1 from oc_accounts where user_id = p_user) then
    return json_build_object('ok', false, 'error', 'not_on_plan', 'resetsAt', v_month + interval '1 month');
  end if;
  if exists (select 1 from oc_accounts where user_id = p_user and blocked) then
    return json_build_object('ok', false, 'error', 'blocked', 'resetsAt', v_month + interval '1 month');
  end if;
  select coalesce(sum(characters), 0) into v_used from oc_usage_events where user_id = p_user and route = '/tts' and ts >= v_month;
  if v_used + p_chars > p_limit then
    return json_build_object('ok', false, 'error', 'personal_limit', 'resetsAt', v_month + interval '1 month');
  end if;
  if p_chars > p_global_remaining then
    return json_build_object('ok', false, 'error', 'monthly_budget', 'resetsAt', v_month + interval '1 month');
  end if;
  insert into oc_usage_events (user_id, route, model, characters, credits, cost_micro_usd) values (p_user, '/tts', 'eleven_flash_v2_5', p_chars, 0, 0);
  return json_build_object('ok', true, 'reservationId', '');
end $$;

create or replace function public.oc_spend_summary(p_user text, p_monthly bigint, p_daily bigint, p_global bigint, p_tts integer)
returns json language sql security definer set search_path = public set timezone = 'UTC' as $$
  with b as (
    select date_trunc('month', now() at time zone 'utc') at time zone 'utc' as m,
           date_trunc('day', now() at time zone 'utc') at time zone 'utc' as d
  ), a as (select * from oc_accounts where user_id = p_user)
  select json_build_object(
    'spentMonthMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where user_id = p_user and ts >= b.m),
    'spentTodayMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where user_id = p_user and ts >= b.d),
    'monthlyLimitMicro', coalesce((select monthly_limit_micro_usd from a), p_monthly),
    'dailyLimitMicro', coalesce((select daily_limit_micro_usd from a), p_daily),
    'globalSpentMicro', (select coalesce(sum(cost_micro_usd), 0) from oc_usage_events, b where ts >= b.m),
    'globalLimitMicro', p_global,
    'ttsCharsMonth', (select coalesce(sum(characters), 0) from oc_usage_events, b where user_id = p_user and route = '/tts' and ts >= b.m),
    'ttsCharsLimit', p_tts,
    'monthEnd', (select m + interval '1 month' from b),
    'dayEnd', (select d + interval '1 day' from b),
    'blocked', coalesce((select blocked from a), false),
    'onPlan', exists(select 1 from oc_accounts where user_id = p_user));
$$;

create or replace function public.oc_accounts_open() returns boolean language sql security definer set search_path = public set timezone = 'UTC' as $$
  -- only confirmed accounts count, so unconfirmed sign-up spam cannot fill the cap
  select (select count(*) from oc_accounts a join auth.users u on u.id::text = a.user_id where u.email_confirmed_at is not null) < (select max_accounts from oc_settings);
$$;
drop trigger if exists oc_max_accounts on auth.users;
drop function if exists public.oc_enforce_max_accounts();
revoke all on function public.oc_reserve, public.oc_settle, public.oc_reserve_chars, public.oc_spend_summary, public.oc_accounts_open from public, anon, authenticated;
