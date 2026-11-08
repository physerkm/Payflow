# Runbook: dead-letter queue

**Alert:** `DeadLetteredMessages` — a message was moved to `payflow.<queue>.dlq`.

## Why messages are dead-lettered
- Exhausted transport retries (5 s → 30 s → 2 min) for an exception during processing.
- Non-retryable errors: malformed JSON, missing tenant id, referenced entity not found *in the message's tenant* (possible forged or misrouted message), state-machine violation.

## Diagnose
1. Worker logs around the time of the alert: `Message <id> (<type>) dead-lettered` includes the exception.
2. Inspect the message in the RabbitMQ UI (`payflow.payments.dlq`, `payflow.refunds.dlq`, `payflow.notifications.dlq`) → *Get messages* (requeue: on). Headers show `x-payflow-retry`, `x-death`.
3. Correlate with the trace (`traceparent` header) and the audit log (`correlation_id`).

## Decide
| Cause | Action |
|---|---|
| Bug fixed / dependency restored | Shovel the messages back to `payflow.events` with their routing key (RabbitMQ *Move messages*); the inbox makes reprocessing safe |
| Foreign tenant / entity not found | Do **not** replay. Investigate as a potential security incident (who produced it?) |
| State-machine violation | Check the payment's attempts and audit log; usually a late duplicate — safe to discard after confirming the payment's final state |

## Payment-specific checks
- A payment stuck in `Pending` whose `PaymentRequested.v1` is in the DLQ: replay after the fix, or retry from the UI once the payment is `Failed`.
- Never retry a charge manually at the provider; always go through PayFlow so the idempotency key is reused.
