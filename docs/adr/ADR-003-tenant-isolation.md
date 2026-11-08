# ADR-003: Tenant isolation in depth

**Status:** Accepted · **Date:** 2026-11-07

## Context
Cross-tenant data access has zero tolerance. A single control (remembering a `WHERE tenant_id = …`) is not enough.

## Decision
Independent layers, each able to stop a leak on its own:

1. **Server-side tenant context.** `POST /auth/activate-tenant` issues an access token with a `tenant_id` claim only for an active membership. Every request re-validates membership and tenant status in the database and takes the *current* role from there. Client headers and body fields are never trusted.
2. **Authorization policies** per endpoint (`perm:orders.write`, …) derived from the role matrix.
3. **EF Core global query filters** on all tenant tables, failing closed (no tenant → no rows), plus a SaveChanges guard that rejects writes for other tenants.
4. **Composite foreign keys** `(tenant_id, x_id) → parent (tenant_id, id)`, so the database rejects cross-tenant references.
5. **PostgreSQL RLS** (`USING / WITH CHECK tenant_id = current_setting('app.tenant_id')`) enforced for the non-owner application role. An EF interceptor sets the value before commands and re-applies it after reconnects and rollbacks; Npgsql resets session state when connections return to the pool. Cross-tenant system tasks (webhook routing, reconciliation scans, retention) use a separate owner connection and then process each item in a tenant-scoped context.
6. **404 for foreign resources** so existence is never disclosed.

## Consequences
- ✅ Verified by integration tests: tenant A/B negative tests, a composite-FK violation test, an RLS role test, and the whole application running under RLS (with a counter-test showing it fails when the interceptor is off).
- ⚠️ One extra round trip when the tenant value changes on a connection.
- ⚠️ RLS does not replace application authorization; it is the last line of defence.
