# ADR-001: Modular monolith with a separate worker

**Status:** Accepted · **Date:** 2026-11-07

## Context
The business boundaries (tenants, catalog, orders, payments, refunds, reporting) are still being learned. Microservices would add network failure modes, distributed transactions, independent deployments and operational cost before the domain is stable.

## Decision
- One backend solution with one API deployable and one Worker deployable, sharing the code base and the PostgreSQL database.
- Modules own their domain rules and application services. Cross-module calls go through narrow, module-owned interfaces (e.g. `IOrderPaymentCoordinator`, `IProductCatalog`) or integration events.
- The single `DbContext` exposes one interface per module (`IOrdersDb`, `IPaymentsDb`, …); a module's code only receives its own interface, so it cannot compile queries against another module's tables.
- Module dependencies are acyclic (Payments → Orders, Orders → Catalog/Customers). Where Orders needs a Payments behaviour (cancel the in-flight payment), Orders owns a port that Payments implements.
- Architecture tests (NetArchTest) enforce layer and module rules in CI.

## Consequences
- ✅ Local transactions: a payment, its order update and its outbox message commit atomically.
- ✅ Simple debugging, testing and local setup; the Worker still scales independently.
- ✅ Clear seams to extract a module (or the Worker) into a service later.
- ⚠️ Module discipline relies on interfaces and tests, not process boundaries.
