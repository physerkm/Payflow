# Runbook: outbox backlog

**Alert:** `OutboxBacklogGrowing` — oldest pending outbox message older than 2 minutes.

## Impact
Payments are accepted (202) but not processed; customers see `Pending` longer. No data is lost — events are safe in PostgreSQL.

## Diagnose
1. Worker health: `docker compose ps worker` / `GET http://<worker>:8080/health/ready` (checks PostgreSQL, RabbitMQ, outbox heartbeat).
2. Worker logs: `Outbox publish failed for message …` (broker problem) or `Outbox publishing cycle failed` (database problem).
3. Backlog in SQL:
   ```sql
   SELECT event_type, count(*), min(created_at), max(publish_attempts), max(last_error)
   FROM outbox_messages WHERE status = 'Pending' GROUP BY event_type;
   ```
4. RabbitMQ: management UI → connections, `payflow.events` exchange, queue bindings.

## Mitigate
- **Broker down / unreachable:** restore RabbitMQ. The publisher retries with backoff (max 60 s between attempts per message); no action needed afterwards.
- **Topology mismatch** (`PRECONDITION_FAILED` in logs): queue arguments changed; delete and re-declare the affected queue during a quiet period (messages in it must be drained first).
- **Worker down:** restart / scale up. Several workers are safe (`FOR UPDATE SKIP LOCKED`).
- **Retry storm** (many messages with high `publish_attempts`): after fixing the cause, release them immediately:
  ```sql
  UPDATE outbox_messages SET available_at = now() WHERE status = 'Pending' AND publish_attempts > 0;
  ```

## Verify
`payflow_outbox_pending` drops to ~0 and pending payments resolve (`GET /payments?status=Pending`).
