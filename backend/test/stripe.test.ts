import { describe, it, expect, vi } from "vitest";
import { Hono } from "hono";
import { verifyStripeSignature, handleStripeWebhook, createCheckoutSession } from "../src/stripe.js";
import { MemoryBillingStore } from "../src/billing.js";
import type { Principal } from "../src/auth.js";

const secret = "whsec_test";
async function sign(payload: string, t: number) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${t}.${payload}`));
  return `t=${t},v1=${Array.from(new Uint8Array(sig))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("")}`;
}

describe("verifyStripeSignature", () => {
  it("accepts a fresh, correctly signed payload and rejects a tampered or stale one", async () => {
    const now = 1_700_000_000;
    const header = await sign("{}", now);
    expect(await verifyStripeSignature("{}", header, secret, now + 60)).toBe(true);
    expect(await verifyStripeSignature("{ }", header, secret, now + 60)).toBe(false);
    expect(await verifyStripeSignature("{}", header, secret, now + 600)).toBe(false);
    expect(await verifyStripeSignature("{}", "garbage", secret, now)).toBe(false);
  });
});

describe("webhook", () => {
  it("activates the plan from checkout.session.completed and updates it on subscription events", async () => {
    const store = new MemoryBillingStore();
    const app = new Hono();
    app.post("/billing/webhook", (c) => handleStripeWebhook(c, store));
    const env = { STRIPE_WEBHOOK_SECRET: secret, STRIPE_SECRET_KEY: "sk_test" };
    const fetchSub = vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
      if (String(url).includes("/v1/subscriptions/sub_1")) {
        return new Response(
          JSON.stringify({
            id: "sub_1",
            customer: "cus_1",
            status: "active",
            current_period_start: 1_700_000_000,
            current_period_end: 1_702_592_000,
            items: { data: [{ price: { id: "price_pro" } }] },
            metadata: { user_id: "user-1" },
          }),
        );
      }
      throw new Error("unexpected fetch " + url);
    });
    // The signature window is measured against the wall clock: sign with "now".
    const now = Math.floor(Date.now() / 1000);
    const completed = JSON.stringify({ type: "checkout.session.completed", data: { object: { client_reference_id: "user-1", customer: "cus_1", subscription: "sub_1" } } });
    let r = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": await sign(completed, now) }, body: completed }, env);
    expect(r.status).toBe(200);
    expect(store.subs.get("user-1")).toMatchObject({ plan_id: "pro", status: "active", stripe_customer_id: "cus_1", stripe_subscription_id: "sub_1" });

    const canceled = JSON.stringify({
      type: "customer.subscription.deleted",
      data: {
        object: {
          id: "sub_1",
          customer: "cus_1",
          status: "canceled",
          current_period_start: 1_700_000_000,
          current_period_end: 1_702_592_000,
          items: { data: [{ price: { id: "price_pro" } }] },
          metadata: { user_id: "user-1" },
        },
      },
    });
    r = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": await sign(canceled, now) }, body: canceled }, env);
    expect(r.status).toBe(200);
    expect(store.subs.get("user-1")?.status).toBe("canceled");

    const bad = await app.request("/billing/webhook", { method: "POST", headers: { "stripe-signature": "t=1,v1=00" }, body: completed }, env);
    expect(bad.status).toBe(400);
    fetchSub.mockRestore();
  });
});

describe("checkout", () => {
  it("creates a subscription checkout for the plan's price with the user as client_reference_id", async () => {
    const store = new MemoryBillingStore();
    const app = new Hono<{ Variables: { principal: Principal } }>();
    app.use("*", async (c, next) => {
      c.set("principal", { sub: "user-1", email: "dev@example.com", via: "session" });
      await next();
    });
    app.post("/billing/checkout", (c) => createCheckoutSession(c, store));
    let form = "";
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (_url, init) => {
      form = String(init?.body);
      return new Response(JSON.stringify({ url: "https://checkout.stripe.com/c/abc" }));
    });
    const r = await app.request(
      "/billing/checkout",
      { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ plan: "pro" }) },
      { STRIPE_SECRET_KEY: "sk_test", STRIPE_SUCCESS_URL: "https://x/ok", STRIPE_CANCEL_URL: "https://x/no" },
    );
    expect(r.status).toBe(200);
    expect(await r.json()).toEqual({ url: "https://checkout.stripe.com/c/abc" });
    const params = new URLSearchParams(form);
    expect(params.get("mode")).toBe("subscription");
    expect(params.get("line_items[0][price]")).toBe("price_pro");
    expect(params.get("client_reference_id")).toBe("user-1");
    expect(params.get("subscription_data[metadata][user_id]")).toBe("user-1");
    expect(params.get("customer_email")).toBe("dev@example.com");
    fetchSpy.mockRestore();
  });
});
