# ADR-009: Asynchronous payment API

**Status:** Accepted · **Date:** 2026-11-07

## Decision
`POST /orders/{id}/payments` validates the request, records the payment and its first attempt, and returns **202 Accepted** with `paymentId` and `statusUrl`. The provider is called by the Worker. Clients poll `GET /payments/{id}` (the SPA polls every 1.5 s while the outcome is not final, for at most 90 s) or rely on notifications.

## Consequences
- ✅ Slow or failing providers never hold HTTP requests or database connections.
- ✅ Retries, reconciliation and webhooks fit naturally.
- ⚠️ Clients must handle intermediate states (`Pending`, `Unknown`); the UI explains them.
