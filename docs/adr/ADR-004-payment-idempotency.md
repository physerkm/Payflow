# ADR-004: Payment idempotency

**Status:** Accepted · **Date:** 2026-11-07

## Context
Clients retry on timeouts and users double-click. A retried payment request must not create a second payment.

## Decision
- Command endpoints accept `Idempotency-Key`; it is **required** for payment initiation, retry and refunds.
- The key is scoped by tenant and operation (route template). The request hash covers method, path and body.
- The endpoint filter opens a transaction, inserts the key with `INSERT … ON CONFLICT DO NOTHING`, runs the use case, stores the response and commits — **all in one transaction**. A concurrent duplicate blocks on the unique index until the first commits, then replays the stored response (`Idempotency-Replayed: true`). Same key with a different hash → `409 idempotency_key_reused`. Failures roll back the key, so the client can retry.
- Independently of keys, a partial unique index allows only one live payment per order, and the provider receives the attempt id as its own idempotency key.
- Records expire after 24 hours (retention job).

## Consequences
- ✅ No window in which a key is "completed" without its effect, or the reverse.
- ✅ Concurrency covered by tests (8 parallel identical requests → one payment; 6 parallel requests with different keys → one payment).
- ⚠️ Duplicates of a long-running request wait instead of failing fast; acceptable for short transactions.
