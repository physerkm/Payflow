# 5-minute demo

Start everything: `docker compose up --build` (optionally `--profile observability` with `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`). Web app: http://localhost:8080, API: http://localhost:5080/scalar.

## 1. Multi-tenancy (1 min)
1. Register `owner-a@demo.test`, create company **Acme**. In a private window register `owner-b@demo.test`, create **Globex**.
2. In Acme create a customer, a product (`PRO-PLAN`, `1500.75 TRY`) and an order.
3. Show that Globex sees none of it. With Globex's token call Acme's order directly:
   ```bash
   curl -i http://localhost:5080/api/v1/orders/<acme-order-id> -H "Authorization: Bearer <globex-token>"   # 404
   ```
   Adding `X-Tenant-Id: <acme-tenant-id>` changes nothing — the tenant comes from the verified membership.

## 2. Asynchronous payment and idempotency (1 min)
1. **Take payment** with *Success*: the API answers **202**, the payment page shows `Pending` and switches to `Succeeded`; the order becomes `Paid`.
2. Send the same request twice with one key (PayFlow.Api.http → *Start payment*): the second response carries `Idempotency-Replayed: true` and the same `paymentId`. Change the body with the same key → **409**.

## 3. Failure modes (1 min)
Create three orders and pay with *Decline*, *Timeout* and *Transient error*:
- Decline → `Failed (card_declined)`, one attempt, no retry.
- Timeout → `Unknown`, then reconciliation confirms `Succeeded` — **one** charge at the provider.
- Transient error → attempt 1 `Failed`, attempt 2 (automatic retry) `Succeeded`.

## 4. Outbox recovery (1 min)
```bash
docker compose stop rabbitmq
```
Create an order and pay: still **202**, payment stays `Pending`. Show the waiting row:
```sql
SELECT event_type, status, publish_attempts, last_error FROM outbox_messages ORDER BY created_at DESC LIMIT 3;
```
```bash
docker compose start rabbitmq
```
Within seconds the message is published and the payment becomes `Succeeded`.

## 5. Refund, reporting, observability (1 min)
1. Refund 1000 TRY on a succeeded payment → `Partially refunded`; dashboard net amount drops accordingly. Try a refund larger than the refundable amount → rejected.
2. Grafana → Explore → Tempo: open the trace of the payment request — HTTP → SQL → outbox publish → RabbitMQ → consumer → provider call in one trace. Dashboard *PayFlow - Operations* shows provider outcomes, outbox backlog, retries.
3. (If `ANTHROPIC_API_KEY` is set) AI assistant: *"Summarize last week's failed payments by reason."* — the answer cites the tool, metric and period.
