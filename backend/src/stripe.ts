import type { Context } from "hono";
import { getEnv } from "./env.js";
import type { BillingStore, Subscription } from "./billing.js";
import type { Principal } from "./auth.js";

/**
 * Stripe subscriptions over the REST API with fetch (no SDK, so it runs on Workers too):
 * Checkout sells a plan, the customer portal manages it, and the webhook keeps oc_subscriptions
 * in step with Stripe. Plans map to Stripe prices through oc_plans.stripe_price_id.
 */

const STRIPE_API = "https://api.stripe.com";
const SIGNATURE_TOLERANCE_SECONDS = 300;

async function stripe(env: { STRIPE_SECRET_KEY?: string }, method: "GET" | "POST", path: string, form?: URLSearchParams): Promise<Response> {
  if (!env.STRIPE_SECRET_KEY) throw new Error("STRIPE_SECRET_KEY not configured");
  return fetch(STRIPE_API + path, {
    method,
    headers: { authorization: `Bearer ${env.STRIPE_SECRET_KEY}`, ...(form ? { "content-type": "application/x-www-form-urlencoded" } : {}) },
    body: form?.toString(),
  });
}

/** Stripe-Signature: `t=<unix>,v1=<hex hmac-sha256 of "<t>.<payload>">` (several v1 allowed during key rotation). */
export async function verifyStripeSignature(payload: string, header: string, secret: string, nowSeconds = Math.floor(Date.now() / 1000)): Promise<boolean> {
  const pairs = header.split(",").map((kv) => kv.trim().split("=") as [string, string]);
  const t = Number(pairs.find(([k]) => k === "t")?.[1]);
  const provided = pairs.filter(([k]) => k === "v1").map(([, v]) => v ?? "");
  if (!Number.isFinite(t) || provided.length === 0 || Math.abs(nowSeconds - t) > SIGNATURE_TOLERANCE_SECONDS) return false;
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${t}.${payload}`));
  const expected = Array.from(new Uint8Array(mac))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
  return provided.some((sig) => sig.length === expected.length && timingSafeEqual(sig, expected));
}

function timingSafeEqual(a: string, b: string): boolean {
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

interface StripeSubscription {
  id: string;
  customer: string;
  status: string;
  current_period_start: number;
  current_period_end: number;
  items: { data: { price: { id: string } }[] };
  metadata?: Record<string, string>;
}

async function subscriptionRow(store: BillingStore, sub: StripeSubscription, userId: string | undefined): Promise<Subscription | undefined> {
  const uid = userId ?? sub.metadata?.user_id;
  const priceId = sub.items?.data?.[0]?.price?.id;
  if (!uid || !priceId) return undefined;
  const plan = await store.planByPrice(priceId);
  if (!plan) return undefined;
  return {
    user_id: uid,
    plan_id: plan.id,
    status: sub.status,
    current_period_start: new Date(sub.current_period_start * 1000).toISOString(),
    current_period_end: new Date(sub.current_period_end * 1000).toISOString(),
    stripe_customer_id: sub.customer,
    stripe_subscription_id: sub.id,
  };
}

/** POST /billing/webhook — no bearer auth; the Stripe signature is the auth. */
export async function handleStripeWebhook(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  if (!env.STRIPE_WEBHOOK_SECRET) return c.json({ error: "STRIPE_WEBHOOK_SECRET not configured" }, 503);
  const payload = await c.req.text();
  const header = c.req.header("stripe-signature") ?? "";
  if (!(await verifyStripeSignature(payload, header, env.STRIPE_WEBHOOK_SECRET))) return c.json({ error: "bad signature" }, 400);
  const event = JSON.parse(payload) as { type: string; data: { object: Record<string, unknown> } };
  const object = event.data.object;
  if (event.type === "checkout.session.completed") {
    const userId = object.client_reference_id as string | undefined;
    const subId = object.subscription as string | undefined;
    if (userId && subId) {
      const res = await stripe(env, "GET", `/v1/subscriptions/${subId}`);
      if (res.ok) {
        const row = await subscriptionRow(store, (await res.json()) as StripeSubscription, userId);
        if (row) await store.upsertSubscription(row);
      }
    }
  } else if (event.type === "customer.subscription.updated" || event.type === "customer.subscription.deleted") {
    const row = await subscriptionRow(store, object as unknown as StripeSubscription, undefined);
    if (row) await store.upsertSubscription(row);
  }
  return c.json({ received: true });
}

/** POST /billing/checkout { plan } → { url }: a Stripe Checkout page for the plan's price. */
export async function createCheckoutSession(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const principal = c.get("principal") as Principal;
  const body = (await c.req.json().catch(() => ({}))) as { plan?: string };
  const plan = body.plan ? await store.plan(body.plan) : undefined;
  if (!plan?.stripe_price_id) return c.json({ error: "unknown plan or plan has no Stripe price" }, 400);
  const form = new URLSearchParams({
    mode: "subscription",
    "line_items[0][price]": plan.stripe_price_id,
    "line_items[0][quantity]": "1",
    client_reference_id: principal.sub,
    "subscription_data[metadata][user_id]": principal.sub,
    success_url: env.STRIPE_SUCCESS_URL ?? "https://openclicky.app/subscribed",
    cancel_url: env.STRIPE_CANCEL_URL ?? "https://openclicky.app/",
    ...(principal.email ? { customer_email: principal.email } : {}),
  });
  const existing = await store.subscription(principal.sub);
  if (existing?.stripe_customer_id) {
    form.delete("customer_email");
    form.set("customer", existing.stripe_customer_id);
  }
  const res = await stripe(env, "POST", "/v1/checkout/sessions", form);
  if (!res.ok) {
    // Stripe's error body can name the account and the key; log it, tell the client the status.
    console.error(`stripe checkout: upstream ${res.status}: ${(await res.text()).slice(0, 1000)}`);
    return c.json({ error: `stripe checkout failed (${res.status})` }, 502);
  }
  const { url } = (await res.json()) as { url: string };
  return c.json({ url });
}

/** POST /billing/portal → { url }: Stripe's customer portal (cancel, upgrade, invoices). */
export async function createPortalSession(c: Context, store: BillingStore): Promise<Response> {
  const env = getEnv(c);
  const principal = c.get("principal") as Principal;
  const existing = await store.subscription(principal.sub);
  if (!existing?.stripe_customer_id) return c.json({ error: "no subscription" }, 404);
  const form = new URLSearchParams({ customer: existing.stripe_customer_id, return_url: env.STRIPE_PORTAL_RETURN_URL ?? "https://openclicky.app/" });
  const res = await stripe(env, "POST", "/v1/billing_portal/sessions", form);
  if (!res.ok) return c.json({ error: `stripe ${res.status}` }, 502);
  const { url } = (await res.json()) as { url: string };
  return c.json({ url });
}
