// k6 baseline: read-heavy traffic plus payment initiation, against a running stack.
//   k6 run -e BASE_URL=http://localhost:5080 tests/load/k6/baseline.js
// Reports p95/p99 latency and error rate. The thresholds mirror the spec's NFR baseline (simple reads p95 < 500 ms);
// they are engineering targets for a local/staging baseline, not an SLA.
import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const BASE = `${__ENV.BASE_URL || 'http://localhost:5080'}/api/v1`;
const JSON_HEADERS = { 'Content-Type': 'application/json' };
const accepted = new Rate('payment_accepted');
const paymentLatency = new Trend('payment_initiation_ms', true);

export const options = {
  scenarios: {
    readers: { executor: 'constant-vus', vus: 20, duration: '1m', exec: 'readers' },
    payers: { executor: 'constant-arrival-rate', rate: 5, timeUnit: '1s', duration: '1m', preAllocatedVUs: 10, exec: 'payers' },
  },
  thresholds: {
    'http_req_failed{scenario:readers}': ['rate<0.01'],
    'http_req_duration{scenario:readers}': ['p(95)<500', 'p(99)<1000'],
    payment_accepted: ['rate>0.99'],
  },
};

function post(path, body, token, extraHeaders = {}) {
  return http.post(`${BASE}${path}`, JSON.stringify(body), {
    headers: { ...JSON_HEADERS, ...(token ? { Authorization: `Bearer ${token}` } : {}), ...extraHeaders },
  });
}

export function setup() {
  const suffix = Date.now();
  const register = post('/auth/register', { email: `k6+${suffix}@payflow.test`, password: 'Str0ngPassw0rd', displayName: 'k6' });
  let token = register.json('accessToken');
  const tenant = post('/tenants', { name: `Load ${suffix}`, slug: `load-${suffix}`, defaultCurrency: 'TRY', timeZone: 'Europe/Istanbul' }, token);
  token = post('/auth/activate-tenant', { tenantId: tenant.json('id') }, token).json('accessToken');
  const customerId = post('/customers', { name: 'Load customer' }, token).json('id');
  const productId = post('/products', { sku: `LOAD-${suffix}`, name: 'Load product', priceMinor: 9900, currency: 'TRY' }, token).json('id');
  return { token, customerId, productId };
}

export function readers(data) {
  const params = { headers: { Authorization: `Bearer ${data.token}` } };
  group('reads', () => {
    check(http.get(`${BASE}/orders?pageSize=20`, params), { 'orders 200': (r) => r.status === 200 });
    check(http.get(`${BASE}/payments?pageSize=20`, params), { 'payments 200': (r) => r.status === 200 });
    check(http.get(`${BASE}/customers?pageSize=20`, params), { 'customers 200': (r) => r.status === 200 });
  });
  sleep(0.5);
}

export function payers(data) {
  const order = post('/orders', { customerId: data.customerId, items: [{ productId: data.productId, quantity: 1 }] }, data.token);
  if (order.status !== 201) {
    accepted.add(false);
    return;
  }
  const key = `${__VU}-${__ITER}-${Date.now()}`;
  const payment = post(`/orders/${order.json('id')}/payments`, { paymentMethodToken: 'pm_test_success' }, data.token, { 'Idempotency-Key': key });
  paymentLatency.add(payment.timings.duration);
  accepted.add(payment.status === 202);
}
