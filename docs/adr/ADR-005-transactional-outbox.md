# ADR-005: Transactional outbox

**Status:** Accepted · **Date:** 2026-11-07

## Context
Writing to the database and publishing to RabbitMQ are two systems ("dual write"). A crash between them loses messages or publishes events for rolled-back changes.

## Decision
- Aggregates raise domain events; `SaveChangesAsync` maps them to versioned integration events and inserts `outbox_messages` rows in the same transaction.
- The Worker claims pending rows with `FOR UPDATE SKIP LOCKED` (safe with several workers), publishes persistent, mandatory messages with **publisher confirms**, and marks them published only after the broker ack.
- `available_at` delays a message (used for the retry backoff of payment attempts). Publish failures increase `publish_attempts` and push `available_at` with exponential backoff.
- Each row keeps the W3C `traceparent` of the originating request, so the consumer continues the same trace.
- Metrics: pending count and oldest pending age, with an alert and a runbook.

## Consequences
- ✅ RabbitMQ outages never lose events and never block the API (payments still get 202).
- ⚠️ At-least-once publication (a crash after the confirm, before marking) → consumers must be idempotent (ADR-006).
