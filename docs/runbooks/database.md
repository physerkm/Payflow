# Runbook: database pressure (pool, locks, long transactions, slow statements)

**Alerts:** `DbConnectionPoolSaturated`, `DatabaseLockWaits`, `LongRunningTransaction`.
**Dashboard:** Grafana → *PayFlow - Operations* → panels *DB connection pool*, *PostgreSQL sessions & lock waits*,
*Longest open transaction*, *Top statements by total execution time*.

Metrics come from Npgsql (pool usage per service, through OpenTelemetry) and from `postgres-exporter`
(sessions, lock waits, `pg_stat_statements`). The exporter connects as `payflow_monitor`, which has `pg_monitor`
(statistics views) and no access to business tables.

## 1. Find out what is waiting

```sql
-- Sessions waiting for a lock, and who blocks them
SELECT w.pid, w.wait_event_type, w.wait_event, now() - w.query_start AS waiting_for,
       pg_blocking_pids(w.pid) AS blocked_by, left(w.query, 120) AS query
FROM pg_stat_activity w
WHERE w.datname = 'payflow' AND w.wait_event_type = 'Lock';

-- Longest open transactions
SELECT pid, usename, state, now() - xact_start AS open_for, left(query, 120) AS query
FROM pg_stat_activity
WHERE datname = 'payflow' AND xact_start IS NOT NULL
ORDER BY xact_start
LIMIT 10;
```

Run them with `docker compose exec postgres psql -U payflow -d payflow` (or through your managed database console).

## 2. Typical causes in PayFlow

| Symptom | Likely cause | Action |
|---|---|---|
| Lock waits on `payments` | Many concurrent refunds for one payment (row lock `SELECT … FOR UPDATE`, by design) | Expected in bursts; investigate only if it lasts minutes |
| Lock waits on `outbox_messages` | Several publishers without `SKIP LOCKED` (should not happen) | Check the Worker version; the publisher uses `FOR UPDATE SKIP LOCKED` |
| Long transaction, state `idle in transaction` | A client opened a transaction and stopped (manual `psql` session, debugger) | Identify the `pid` and `SELECT pg_terminate_backend(pid);` |
| Pool saturated in the API | Slow statements or a traffic spike; requests now queue for a connection | Check the slow-statement table, then scale API replicas or raise `Maximum Pool Size` together with `max_connections` |
| Pool saturated in the Worker | Provider calls are slow and handlers hold connections | Provider calls run outside transactions; check the provider latency and the circuit breaker |

The 30 s request timeout (`RequestTimeouts:Default`) cancels long API requests, so their transactions roll back
instead of holding locks.

## 3. Slow statements

The table panel lists the top statements by total execution time (constants are replaced by `$n`, so no data
values are shown). For a candidate:

```sql
EXPLAIN (ANALYZE, BUFFERS) <statement with realistic parameters>;
```

Compare with the baseline plans in [docs/performance/baseline.md](../performance/baseline.md). Add or change an
index only with a migration and only after the plan shows the benefit; every index slows down writes.

Reset the statistics after a fix to see the new numbers: `SELECT pg_stat_statements_reset();`

## 4. Existing databases

`pg_stat_statements` and the monitoring role are created by
`infrastructure/docker/postgres/init/02-monitoring.sh` on a fresh volume. For a database created earlier:

```bash
docker compose exec postgres sh /docker-entrypoint-initdb.d/02-monitoring.sh
docker compose --profile observability restart postgres-exporter
```
