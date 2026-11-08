# ADR-002: Shared database, shared schema, tenant_id

**Status:** Accepted · **Date:** 2026-11-07

## Context
Options: database per tenant, schema per tenant, or a shared schema with a tenant column. The MVP targets many small tenants and must stay cheap to operate and migrate.

## Decision
All tenants share one database and one schema. Every business table has a non-null `tenant_id`; platform-global tables (users, tenants, the provider webhook log, the mock provider ledger) are explicit exceptions. Unique constraints and indexes are tenant-prefixed (`(tenant_id, sku)`, `(tenant_id, order_number)`, `(tenant_id, created_at DESC)`).

## Consequences
- ✅ One migration path, simple connection pooling, cheap tenant onboarding.
- ✅ Platform-wide reporting remains possible later.
- ⚠️ A missing tenant predicate would leak data → mitigated by ADR-003 (several independent layers).
- ⚠️ Noisy-neighbour risk at scale → partitioning or tenant sharding can be added later without changing the domain.
