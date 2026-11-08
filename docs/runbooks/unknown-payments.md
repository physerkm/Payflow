# Runbook: unknown payments and reconciliation

**Alert:** `ProviderTimeoutsAccumulating`, or payments staying in `Unknown`.

## Background
A provider timeout means *we do not know* whether money moved. PayFlow marks the attempt `Unknown` and never sends a new charge blindly. `PaymentReconciliationJob` (every 15 s) asks the provider for the status of:
- `Unknown` attempts older than 10 s,
- `ProviderPending` attempts without a webhook after 30 s,
- `Processing` attempts older than 2 min (worker crashed during the call).

Outcomes: `Succeeded`/`Declined` are applied; `NotFound` for an unknown/processing attempt means the request never reached the provider → a new attempt is scheduled; anything else is left for manual review.

## Diagnose
```sql
SELECT a.status, a.provider, count(*), min(a.updated_at)
FROM payment_attempts a
WHERE a.status IN ('Unknown', 'ProviderPending', 'Processing')
GROUP BY a.status, a.provider;
```
Worker logs: `Reconciled attempt … : <Result>` and `Reconciliation of attempt … failed`.

## Mitigate
- **Provider status API failing / circuit open:** wait for recovery; reconciliation retries every cycle. Do not mark payments failed manually.
- **Amount mismatch** (`Provider amount mismatch for payment …`): the provider reports a different amount — escalate to finance; never auto-accept.
- **Long provider outage:** consider pausing new payments at the edge (feature flag / maintenance page) to limit `Unknown` volume.

## Verify
`payflow_payments_reconciliations_total` shows `Succeeded`/`Declined` results and the query above returns no old rows.
