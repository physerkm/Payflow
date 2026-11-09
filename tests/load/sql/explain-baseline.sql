-- Query-plan baseline at production-like volume, without leaving data behind: everything runs in one
-- transaction that is rolled back at the end.
--   docker compose exec -T postgres psql -U payflow -d payflow -f - < tests/load/sql/explain-baseline.sql
-- Needs one tenant created by the k6 baseline (slug 'load-…', with a customer and a product).
-- Volume added to that tenant: 100 000 orders + items + payments + attempts, and 150 000 outbox rows of which
-- 50 000 are pending (a broker outage backlog). The tenant queries run as payflow_app with Row-Level Security,
-- exactly like the API; the outbox query runs as the owner role, like the Worker's publisher.
\set ON_ERROR_STOP on
\timing off
BEGIN;

SELECT t.id AS tenant_id, c.id AS customer_id, p.id AS product_id
FROM tenants t
JOIN customers c ON c.tenant_id = t.id
JOIN products p ON p.tenant_id = t.id
WHERE t.slug LIKE 'load-%'
ORDER BY t.created_at DESC
LIMIT 1 \gset

INSERT INTO orders (id, tenant_id, order_number, customer_id, status, total_minor, currency, created_at, updated_at, row_version)
SELECT gen_random_uuid(), :'tenant_id', 'PERF-' || g, :'customer_id',
       CASE WHEN g % 10 = 0 THEN 'PaymentFailed' ELSE 'Paid' END, 9900, 'TRY',
       now() - g * interval '20 seconds', now() - g * interval '20 seconds', 1
FROM generate_series(1, 100000) g;

INSERT INTO order_items (id, tenant_id, order_id, product_id, sku_snapshot, name_snapshot, quantity, unit_price_minor, line_total_minor, currency)
SELECT gen_random_uuid(), o.tenant_id, o.id, :'product_id', 'PERF', 'Perf product', 1, 9900, 9900, 'TRY'
FROM orders o WHERE o.tenant_id = :'tenant_id' AND o.order_number LIKE 'PERF-%';

INSERT INTO payments (id, tenant_id, order_id, status, amount_minor, currency, provider, payment_method_token,
                      refunded_amount_minor, pending_refund_amount_minor, created_at, updated_at, succeeded_at, failed_at, row_version)
SELECT gen_random_uuid(), o.tenant_id, o.id, CASE WHEN o.status = 'Paid' THEN 'Succeeded' ELSE 'Failed' END,
       9900, 'TRY', 'mock', 'pm_test_success', 0, 0, o.created_at, o.created_at,
       CASE WHEN o.status = 'Paid' THEN o.created_at END, CASE WHEN o.status <> 'Paid' THEN o.created_at END, 1
FROM orders o WHERE o.tenant_id = :'tenant_id' AND o.order_number LIKE 'PERF-%';

INSERT INTO payment_attempts (id, tenant_id, payment_id, attempt_number, trigger, provider, status, scheduled_at, started_at, completed_at, created_at, updated_at)
SELECT gen_random_uuid(), p.tenant_id, p.id, 1, 'Initial', 'mock', CASE WHEN p.status = 'Succeeded' THEN 'Succeeded' ELSE 'Declined' END,
       p.created_at, p.created_at, p.created_at, p.created_at, p.created_at
FROM payments p WHERE p.tenant_id = :'tenant_id' AND p.payment_method_token = 'pm_test_success' AND p.provider = 'mock'
  AND NOT EXISTS (SELECT 1 FROM payment_attempts a WHERE a.payment_id = p.id);

INSERT INTO outbox_messages (id, message_id, tenant_id, event_type, payload, status, occurred_at, created_at, available_at, published_at, publish_attempts)
SELECT gen_random_uuid(), gen_random_uuid(), :'tenant_id', 'PaymentRequested.v1', '{}'::jsonb,
       CASE WHEN g <= 50000 THEN 'Pending' ELSE 'Published' END,
       now() - g * interval '1 second', now() - g * interval '1 second', now() - g * interval '1 second',
       CASE WHEN g > 50000 THEN now() - g * interval '1 second' END, 0
FROM generate_series(1, 150000) g;

ANALYZE orders, order_items, payments, payment_attempts, outbox_messages;

SELECT (SELECT count(*) FROM orders WHERE tenant_id = :'tenant_id') AS tenant_orders,
       (SELECT count(*) FROM payments WHERE tenant_id = :'tenant_id') AS tenant_payments,
       (SELECT count(*) FROM outbox_messages WHERE status = 'Pending') AS pending_outbox;

-- ---- As the API: application role, Row-Level Security, tenant context set for this transaction ----
SET LOCAL ROLE payflow_app;
SELECT set_config('app.tenant_id', :'tenant_id', true) IS NOT NULL AS tenant_context_set;

\echo '### Q1 orders list (GET /orders, first page)'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT o.id, o.order_number, o.customer_id, o.status, o.total_minor, o.currency,
       (SELECT count(*)::int FROM order_items AS o0 WHERE o0.tenant_id = :'tenant_id' AND o.tenant_id = o0.tenant_id AND o.id = o0.order_id) AS item_count,
       o.created_at
FROM orders AS o WHERE o.tenant_id = :'tenant_id' ORDER BY o.created_at DESC LIMIT 20 OFFSET 0;

\echo '### Q2 orders total count (pagination)'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT count(*) FROM orders AS o WHERE o.tenant_id = :'tenant_id';

\echo '### Q3 payments list with attempts (GET /payments, first page)'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT p1.*, p2.*
FROM (SELECT p.* FROM payments AS p WHERE p.tenant_id = :'tenant_id' ORDER BY p.created_at DESC LIMIT 20 OFFSET 0) AS p1
LEFT JOIN (SELECT p0.* FROM payment_attempts AS p0 WHERE p0.tenant_id = :'tenant_id') AS p2
  ON p1.tenant_id = p2.tenant_id AND p1.id = p2.payment_id
ORDER BY p1.created_at DESC, p1.id;

\echo '### Q4 payments filtered by status, deep page (GET /payments?status=Failed&page=50)'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT p.id, p.created_at FROM payments AS p
WHERE p.tenant_id = :'tenant_id' AND p.status = 'Failed'
ORDER BY p.created_at DESC LIMIT 20 OFFSET 980;

-- ---- As the Worker's outbox publisher: owner role, 50 000 pending messages ----
RESET ROLE;
\echo '### Q5 outbox poll with a 50 000 message backlog'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM outbox_messages
WHERE status = 'Pending' AND available_at <= now()
ORDER BY available_at, created_at
LIMIT 100
FOR UPDATE SKIP LOCKED;

ROLLBACK;
