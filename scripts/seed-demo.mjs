#!/usr/bin/env node
// Seeds demo data through the public API, exactly as the web app would (no database access).
//
//   node scripts/seed-demo.mjs                      # against http://localhost:8080 (docker compose)
//   PAYFLOW_URL=http://localhost:5080 node scripts/seed-demo.mjs
//
// Creates two companies for the multi-tenant demo (spec §19.3):
//   Tenant A  demo@payflow.local    / PayFlow2026  "Demo Mağaza"  customers, products, orders, payments in every
//                                                                 outcome, refunds and a pending team invitation
//   Tenant B  demo-b@payflow.local  / PayFlow2026  "Globex Ltd"   a few records of its own
// Re-running is safe: an existing demo company is left untouched.
// Environment: PAYFLOW_URL, DEMO_PREFIX (default "demo"), DEMO_PASSWORD (default "PayFlow2026").

const BASE = `${(process.env.PAYFLOW_URL ?? 'http://localhost:8080').replace(/\/$/, '')}/api/v1`;
const PREFIX = process.env.DEMO_PREFIX ?? 'demo';
const PASSWORD = process.env.DEMO_PASSWORD ?? 'PayFlow2026';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function call(method, path, body, { token, key } = {}) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (key) headers['Idempotency-Key'] = key;
    const response = await fetch(BASE + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
    if (response.status === 429) {
      const wait = Number(response.headers.get('retry-after') ?? 15);
      console.log(`  rate limited on ${path}, waiting ${wait}s`);
      await sleep(wait * 1000);
      continue;
    }
    const text = await response.text();
    const data = text ? JSON.parse(text) : null;
    if (!response.ok) {
      const error = new Error(`${method} ${path} -> ${response.status} ${data?.code ?? ''} ${data?.detail ?? text}`);
      error.status = response.status;
      throw error;
    }
    return data;
  }
  throw new Error(`Still rate limited: ${method} ${path}`);
}

/** Signs in to an existing demo account, or registers it on the first run. */
async function signIn(email, displayName) {
  try {
    return await call('POST', '/auth/login', { email, password: PASSWORD });
  } catch (error) {
    if (error.status !== 401) throw error;
    return call('POST', '/auth/register', { email, password: PASSWORD, displayName });
  }
}

/** Signs in, creates the company unless it exists, and returns a token bound to it (or null if it existed). */
async function companySession(email, displayName, company) {
  const session = await signIn(email, displayName);
  const memberships = await call('GET', '/auth/memberships', null, { token: session.accessToken });
  if (memberships.some((m) => m.tenantSlug === company.slug)) {
    console.log(`- ${company.name} (${email}) already exists, skipped`);
    return null;
  }
  const tenant = await call('POST', '/tenants', company, { token: session.accessToken });
  const activated = await call('POST', '/auth/activate-tenant', { tenantId: tenant.id }, { token: session.accessToken });
  return activated.accessToken;
}

async function waitForPayment(token, paymentId, done) {
  for (let i = 0; i < 60; i++) {
    const payment = await call('GET', `/payments/${paymentId}`, null, { token });
    if (done(payment.status)) return payment;
    await sleep(1000);
  }
  throw new Error(`Payment ${paymentId} did not settle in time`);
}

async function seedTenantA() {
  const email = `${PREFIX}@payflow.local`;
  const token = await companySession(email, 'Demo Kullanıcı', {
    name: 'Demo Mağaza', slug: `${PREFIX}-magaza`, defaultCurrency: 'TRY', timeZone: 'Europe/Istanbul',
  });
  if (!token) return;
  const t = { token };

  const customers = [];
  for (const [name, mail, phone] of [
    ['Ayşe Yılmaz', 'ayse@example.com', '+905321112233'],
    ['Mehmet Kaya', 'mehmet@example.com', null],
    ['Zeynep Demir', 'zeynep@example.com', '+905449998877'],
    ['Can Öztürk', 'can@example.com', null],
    ['Elif Şahin', 'elif@example.com', null],
  ]) customers.push((await call('POST', '/customers', { name, email: mail, phone }, t)).id);

  const products = [];
  for (const [sku, name, description, priceMinor, currency] of [
    ['PRO-PLAN', 'Pro Plan (aylık)', 'Küçük ekipler için', 149900, 'TRY'],
    ['TEAM-PLAN', 'Takım Planı (aylık)', '10 kullanıcıya kadar', 349900, 'TRY'],
    ['SETUP', 'Kurulum Hizmeti', 'Tek seferlik', 75000, 'TRY'],
    ['SUPPORT-HR', 'Destek (saat)', null, 50000, 'TRY'],
    ['INTL-PLAN', 'International Plan', 'Billed in USD', 4900, 'USD'],
  ]) products.push((await call('POST', '/products', { sku, name, description, priceMinor, currency }, t)).id);

  // [customer, [[product, quantity]], test token] - one order per provider scenario.
  const plan = [
    [0, [[0, 1], [2, 1]], 'pm_test_success'],
    [1, [[1, 2]], 'pm_test_success'],
    [2, [[0, 3]], 'pm_test_success'],
    [4, [[3, 2]], 'pm_test_error'], // transient error, retried automatically, then succeeds
    [3, [[3, 4]], 'pm_test_decline'],
    [0, [[1, 1]], 'pm_test_insufficient_funds'],
    [1, [[0, 1]], 'pm_test_timeout'], // unknown outcome, resolved by reconciliation without a second charge
    [2, [[4, 2]], 'pm_test_success'], // USD
    [4, [[0, 1]], 'pm_test_pending'], // completed later by a signed provider webhook
    [3, [[2, 1]], null], // left unpaid
  ];
  const payments = [];
  for (const [customer, items, paymentToken] of plan) {
    const order = await call('POST', '/orders', {
      customerId: customers[customer],
      items: items.map(([product, quantity]) => ({ productId: products[product], quantity })),
      notes: paymentToken ? null : 'Ödeme bekleniyor',
    }, t);
    if (paymentToken) {
      const accepted = await call('POST', `/orders/${order.id}/payments`, { paymentMethodToken: paymentToken }, { ...t, key: `seed-${order.id}` });
      payments.push(accepted.paymentId);
    }
  }

  // One partial and one full refund on succeeded payments.
  const partial = await waitForPayment(token, payments[1], (s) => s === 'Succeeded');
  await call('POST', `/payments/${partial.id}/refunds`, { amountMinor: 100000, reason: 'Kısmi iade - müşteri talebi' }, { ...t, key: `seed-refund-${partial.id}` });
  const full = await waitForPayment(token, payments[2], (s) => s === 'Succeeded');
  await call('POST', `/payments/${full.id}/refunds`, { amountMinor: full.amountMinor, reason: 'Sipariş iptali' }, { ...t, key: `seed-refund-${full.id}` });

  await call('POST', '/tenants/current/invitations', { email: `finance.${PREFIX}@payflow.local`, role: 'Finance' }, t);
  console.log(`- Demo Mağaza: ${customers.length} customers, ${products.length} products, ${plan.length} orders, ${payments.length} payments, 2 refunds, 1 invitation`);
}

async function seedTenantB() {
  const email = `${PREFIX}-b@payflow.local`;
  const token = await companySession(email, 'Globex Owner', {
    name: 'Globex Ltd', slug: `${PREFIX}-globex`, defaultCurrency: 'EUR', timeZone: 'Europe/Berlin',
  });
  if (!token) return;
  const t = { token };
  const customer = (await call('POST', '/customers', { name: 'Hans Becker', email: 'hans@example.com' }, t)).id;
  const product = (await call('POST', '/products', { sku: 'CONSULT-DAY', name: 'Consulting day', priceMinor: 90000, currency: 'EUR' }, t)).id;
  const order = await call('POST', '/orders', { customerId: customer, items: [{ productId: product, quantity: 2 }] }, t);
  await call('POST', `/orders/${order.id}/payments`, { paymentMethodToken: 'pm_test_success' }, { ...t, key: `seed-${order.id}` });
  console.log('- Globex Ltd: 1 customer, 1 product, 1 paid order');
}

console.log(`Seeding demo data at ${BASE}`);
await seedTenantA();
await seedTenantB();
console.log(`Done. Sign in at ${BASE.replace(/\/api\/v1$/, '')} with ${PREFIX}@payflow.local or ${PREFIX}-b@payflow.local / ${PASSWORD}`);
