# ADR-007: Payment provider abstraction

**Status:** Accepted · **Date:** 2026-11-07

## Context
PayFlow must not depend on one provider and must not move real money in this phase.

## Decision
- `IPaymentProvider` (`ChargeAsync`, `RefundAsync`, `GetPaymentStatusAsync`) lives in the Application layer; adapters live in Infrastructure.
- `MockPaymentProvider` keeps its own ledger table that stands in for the provider's database (written outside PayFlow's transactions), honours idempotency keys, and simulates success, decline, timeout-with-hidden-success, transient errors and pending-then-webhook. A worker job resolves pending charges and delivers HMAC-signed webhooks.
- `ResilientPaymentProvider` decorates any adapter with a per-call timeout and a circuit breaker (Polly). Timeouts map to *Unknown* (never *Failed*); an open circuit maps to `CircuitOpen` without calling the provider.
- A real provider must be integrated only through its sandbox, hosted checkout and tokenization; PayFlow never accepts card data.

## Consequences
- ✅ Deterministic tests for every failure mode.
- ✅ Adding a provider = one adapter + one webhook endpoint; the payment state machine stays unchanged.
