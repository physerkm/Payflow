# Backlog status (spec §18)

✅ done · 🟡 partial / placeholder · ⬜ not started

| ID | Issue | Status | Where / evidence |
|---|---|---|---|
| PF-001 | Repository and project board | 🟡 | Repository layout ready; create the GitHub repo and board |
| PF-002 | .NET solution and projects | ✅ | `PayFlow.sln`, reference rules in csproj + architecture tests |
| PF-003 | Architecture decision records | ✅ | `docs/adr/` (ADR-001…010) |
| PF-004 | Local Docker Compose | ✅ | `docker-compose.yml` |
| PF-005 | PostgreSQL and EF Core | ✅ | `PayFlowDbContext`, health check |
| PF-006 | Initial migration | ✅ | `InitialCreate`, `RowLevelSecurity` |
| PF-007 | Tenant aggregate | ✅ | `Domain/Tenants`, unit tests |
| PF-008 | User authentication | ✅ | Identity, `AuthService`, `AuthTests` |
| PF-009 | Refresh token lifecycle | ✅ | Hashed, rotated, reuse detection, logout |
| PF-010 | TenantMembership | ✅ | Roles, status, last-owner protection |
| PF-011 | Active tenant context | ✅ | `activate-tenant`, `TenantResolutionMiddleware` |
| PF-012 | RBAC policies | ✅ | `Permissions`, `PermissionPolicyProvider`, matrix tests |
| PF-013 | Tenant isolation integration tests | ✅ | `TenantIsolationTests`, `RowLevelSecurityModeTests` |
| PF-014 | Customer CRUD | ✅ | `CustomerService`, soft delete |
| PF-015 | Product catalog | ✅ | SKU unique per tenant, price/currency validation |
| PF-016 | Order aggregate | ✅ | State machine + invariants |
| PF-017 | Order item price snapshot | ✅ | `OrderTests.Prices_come_from_the_catalog…` |
| PF-018 | Order creation use case | ✅ | Server-side pricing, per-tenant order numbers |
| PF-019 | Order pagination/filtering | ✅ | Allowlisted sort, filters |
| PF-020 | Payment aggregate | ✅ | `Payment`, unit tests |
| PF-021 | PaymentAttempt model | ✅ | One attempt per provider call, triggers |
| PF-022 | MockPaymentProvider | ✅ | 5 spec tokens + persistent error |
| PF-023 | Payment initiation API | ✅ | 202 + paymentId, amount from server |
| PF-024 | Idempotency storage | ✅ | Same-transaction records |
| PF-025 | Idempotency payload mismatch | ✅ | 409 `idempotency_key_reused` |
| PF-026 | Refund aggregate/use case | ✅ | Row lock + reservation + check constraint |
| PF-027 | RabbitMQ exchanges and queues | ✅ | Durable topology, retry queues, DLQs |
| PF-028 | Worker host | ✅ | Health, config, graceful shutdown |
| PF-029 | Transactional outbox | ✅ | Domain events → outbox in SaveChanges |
| PF-030 | Outbox publisher confirms | ✅ | `RabbitMqMessageBus` (confirm tracking, mandatory) |
| PF-031 | Inbox deduplication | ✅ | `inbox_messages`, duplicate tests |
| PF-032 | Payment consumer | ✅ | `ProcessPaymentAttemptHandler` |
| PF-033 | Retry and dead-letter handling | ✅ | Business retries (attempts) + transport retries/DLQ |
| PF-034 | Mock webhook endpoint | ✅ | HMAC signature, replay window, dedup |
| PF-035 | Reconciliation for unknown payments | ✅ | `ReconcilePaymentHandler`, job |
| PF-036 | Audit logging | ✅ | `AuditTrail`, `/audit-logs` |
| PF-037 | React shell and routing | ✅ | Login, tenant switcher, layout, guards |
| PF-038 | Customer/product screens | ✅ | Data grids, forms, all UI states |
| PF-039 | Order screens | ✅ | List, create, detail, cancel |
| PF-040 | Payment/refund screens | ✅ | Polling, attempts, refunds, retry |
| PF-041 | Dashboard and reports | ✅ | Per-currency metrics, charts, tables |
| PF-042 | Unit tests | ✅ | 91 tests |
| PF-043 | PostgreSQL integration tests | ✅ | 58 tests (real PostgreSQL) |
| PF-044 | RabbitMQ integration tests | 🟡 | Real broker: confirm/route/consume, unroutable, DLQ. Missing: retry-queue round trip on a real broker |
| PF-045 | E2E payment flow tests | ✅ | Playwright journey |
| PF-046 | Architecture tests | ✅ | 13 rules |
| PF-047 | GitHub Actions CI | ✅ | `.github/workflows/ci.yml` |
| PF-048 | OpenTelemetry instrumentation | ✅ | API → worker trace via outbox `traceparent` |
| PF-049 | Grafana dashboards | 🟡 | API, queue, worker, payment panels. Missing: DB panels (pool, slow query, lock wait), DLQ size, 5xx and unknown-rate panels |
| PF-050 | k6 baseline load tests | 🟡 | Script ready (`tests/load/k6/baseline.js`); not run yet, no p95/p99/error-rate report |
| PF-051 | Staging deployment | 🟡 | Gated job + runbook; target platform not chosen |
| PF-052 | Tenant-safe AI reporting tools | ✅ | `AssistantTools` |
| PF-053 | AI evaluation and prompt safety tests | ✅ | `AssistantSafetyTests`, `docs/ai/assistant.md` |
| PF-054 | README, ADRs and demo | 🟡 | README, ADRs, demo script done. Missing: seed data script, screenshots, demo video |

## Beyond the backlog

| Item | Status | Where |
|---|---|---|
| Platform administration (PlatformAdmin / SupportAgent, spec §2.1) | ✅ | `Application/Platform`, `/platform` page, `PlatformAdminTests` |
| PostgreSQL Row-Level Security for the app role | ✅ | migration `RowLevelSecurity`, `RowLevelSecurityModeTests` |
