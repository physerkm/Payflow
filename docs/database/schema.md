# Database schema

PostgreSQL 17, snake_case names, managed by EF Core migrations (`src/PayFlow.Infrastructure/Persistence/Migrations`). Migration history table: `__ef_migrations_history`.

## Tables

| Table | Key columns | Scope | Notes |
|---|---|---|---|
| `tenants` | id, name, slug, status, default_currency, time_zone | Global | unique `slug` |
| `users` (+ `user_claims`, `user_logins`, `user_tokens`) | id, email, normalized_email, password_hash, is_active, lockout fields | Global | ASP.NET Core Identity |
| `tenant_memberships` | id, tenant_id, user_id, role, status | Tenant + user | unique `(tenant_id, user_id)` |
| `tenant_invitations` | id, tenant_id, email_normalized, role, status, expires_at | Tenant | |
| `customers` | id, tenant_id, name, email, phone, status | Tenant (RLS) | `uq_customers_tenant_id_id`, `ix_customers_tenant_email` |
| `products` | id, tenant_id, sku, name, price_minor, currency, status | Tenant (RLS) | unique `(tenant_id, sku)`, `price_minor > 0` |
| `orders` | id, tenant_id, order_number, customer_id, status, total_minor, currency, row_version | Tenant (RLS) | `uq_orders_tenant_number`, `ix_orders_tenant_created`, `total_minor >= 0` |
| `order_items` | id, tenant_id, order_id, product_id, sku_snapshot, name_snapshot, quantity, unit_price_minor, line_total_minor | Tenant (RLS) | `line_total_minor = unit_price_minor * quantity` |
| `payments` | id, tenant_id, order_id, status, amount_minor, currency, refunded_amount_minor, pending_refund_amount_minor, row_version | Tenant (RLS) | `ux_payments_order_active` (≤1 live payment/order), refund-limit check |
| `payment_attempts` | id, tenant_id, payment_id, attempt_number, trigger, provider, provider_reference, status, failure_code, scheduled_at | Tenant (RLS) | unique `(provider, provider_reference)`, partial reconciliation index |
| `refunds` | id, tenant_id, payment_id, amount_minor, currency, status, provider_reference | Tenant (RLS) | `amount_minor > 0` |
| `notifications` | id, tenant_id, type, severity, title, message, resource | Tenant (RLS) | |
| `idempotency_records` | id, tenant_id, operation, key, request_hash, response_status, response_body, expires_at | Tenant (RLS) | unique `(tenant_id, operation, key)` |
| `outbox_messages` | id, message_id, tenant_id, event_type, payload (jsonb), status, available_at, published_at, publish_attempts, trace_parent | System | partial `ix_outbox_pending` on `(available_at, created_at) WHERE status = 'Pending'` |
| `inbox_messages` | consumer_name, message_id, processed_at | System | PK `(consumer_name, message_id)` |
| `webhook_events` | id, provider, provider_event_id, payload (jsonb), status, error | Provider-global | unique `(provider, provider_event_id)` |
| `audit_logs` | id, tenant_id, actor_user_id, action, resource_type, resource_id, correlation_id, metadata (jsonb) | Tenant / platform | append-only for the app role |
| `refresh_tokens` | id, user_id, token_hash, family_id, expires_at, revoked_at, replaced_by_id | User | unique `token_hash` |
| `tenant_sequences` | tenant_id, name, value | Tenant | atomic per-tenant order numbers |
| `mock_provider_transactions` | id, kind, idempotency_key, provider_reference, amount_minor, status, webhook_status | Simulated provider | stands in for the provider's own database |

## Tenant-safe foreign keys

Every tenant-owned parent has an alternate key `(tenant_id, id)`; children reference it with a composite FK, so a row can only point to a parent of the same tenant:

```sql
ALTER TABLE orders ADD CONSTRAINT fk_orders_customer_tenant
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id);
-- same pattern: order_items → orders/products, payments → orders, payment_attempts/refunds → payments
```

## Row-Level Security

Migration `RowLevelSecurity` enables RLS on the tenant tables marked above:

```sql
CREATE POLICY tenant_isolation ON payments
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

The owner role (migrations, system jobs) bypasses RLS; `payflow_app` (API and Worker business queries) is subject to it. `infrastructure/docker/postgres/init/01-app-role.sh` creates that role with default privileges; the migration grants privileges and makes `audit_logs` append-only for it.

## Money and time

- Money: `BIGINT` minor units + `CHAR(3)` currency; never summed across currencies.
- Time: `timestamptz` in UTC; reports convert with `AT TIME ZONE <tenant time zone>`.
- Concurrency: `row_version BIGINT` on aggregates, incremented on every update and checked by EF Core.

## Index rationale

Indexes follow the query patterns of the API (tenant-prefixed lists sorted by `created_at DESC`, status filters, the outbox poller, reconciliation scans, webhook lookups). Verify changes with `EXPLAIN (ANALYZE, BUFFERS)` against realistic data before adding more — every index has a write cost.
