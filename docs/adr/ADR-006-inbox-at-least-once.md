# ADR-006: Inbox and at-least-once delivery

**Status:** Accepted · **Date:** 2026-11-07

## Context
RabbitMQ and the outbox deliver at least once. Duplicates must not apply effects twice.

## Decision
- Each consumer has a stable name. `inbox_messages (consumer_name, message_id)` is inserted with `ON CONFLICT DO NOTHING` **inside the transaction that applies the effect**; a duplicate inserts nothing and the effect is skipped.
- Business idempotency backs it up: aggregates ignore duplicate confirmations (e.g. `RecordSucceeded` on an already succeeded attempt) and reject backward transitions.
- Payment processing commits `Processing` before calling the provider (the payment can then no longer be cancelled), calls the provider without holding a transaction, then commits the result together with the inbox record. A redelivery re-sends the same provider idempotency key.
- Transport failures are retried through per-level retry queues (5 s, 30 s, 2 min) and then dead-lettered; non-retryable errors (malformed message, foreign tenant, state-machine violation) go straight to the DLQ.

## Consequences
- ✅ Exactly-once *effects* on top of at-least-once delivery, verified by tests (duplicate dispatch, redelivered payment request, forged tenant id).
- ⚠️ Inbox rows must be cleaned up (retention job, 7 days).
