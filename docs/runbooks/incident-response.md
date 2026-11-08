# Runbook: incident response (API errors / latency)

**Alerts:** `ApiHighErrorRate` (5xx > 2 %), `ApiLatencyP95High` (p95 > 500 ms).

## First five minutes
1. Acknowledge, open an incident channel, name an incident lead.
2. Grafana → *PayFlow - Operations*: which endpoints, since when, which status codes.
3. Pick a failing request: the response contains `correlationId`/`traceId` → open the trace in Tempo → logs in Loki (`correlation_id`).
4. Check dependencies: `GET /health/ready` (API), PostgreSQL connections/locks, RabbitMQ (only the Worker depends on it).

## Common causes
| Symptom | Likely cause | Action |
|---|---|---|
| 5xx with `NpgsqlException` | Database down, pool exhausted, failover | Check connections (`pg_stat_activity`), long transactions, lock waits |
| 409 spikes | Concurrency conflicts / idempotency in progress | Usually client retries; check for a hot row (same payment refunded in a loop) |
| Latency on list endpoints | Missing index / bad plan | `EXPLAIN (ANALYZE, BUFFERS)` the query from the trace |
| 429 spikes | Rate limits hit | Confirm abuse vs. legitimate load; adjust `RateLimiting:*` |
| Errors right after deploy | Regression | Roll back (see deploy runbook) |

## Security incidents
Suspected cross-tenant access, token theft (`auth.refresh_reuse_detected` in the audit log) or forged messages: preserve logs, revoke sessions (`refresh_tokens` family / user), rotate `Auth:Jwt:SigningKey` (invalidates all access tokens), notify the security owner.

## Close
Write a short post-incident review: timeline, impact (tenants, payments), root cause, follow-up issues.
