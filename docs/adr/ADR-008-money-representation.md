# ADR-008: Money representation

**Status:** Accepted · **Date:** 2026-11-07

## Decision
- Amounts are `BIGINT` minor units with a 3-letter ISO 4217 currency (`TRY 1500,75 → 150075`). No `float`/`double`/`decimal` in storage or arithmetic.
- A `Money` value object refuses to combine different currencies and uses checked arithmetic (overflow throws instead of wrapping).
- Minor-unit exponents come from currency metadata (TRY/USD 2, JPY 0, KWD 3). The UI converts user input with string arithmetic, never floating point.
- Reports group by currency; there is no FX conversion in the MVP.

## Consequences
- ✅ Exact sums and refund limits; database check constraints can express the invariants.
- ⚠️ Display formatting needs the currency exponent everywhere (shared helpers on both sides).
