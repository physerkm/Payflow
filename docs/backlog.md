# Backlog status (spec §18)

✅ done · 🟡 partial / placeholder · ⬜ not started

| ID | Issue | Status | Where / evidence |
|---|---|---|---|
| PF-001 | Repository and project board | 🟡 | GitHub repository [physerkm/Payflow](https://github.com/physerkm/Payflow) with CI; branch protection and project board not set up yet |
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
| PF-019 | Order pagination/filtering | ✅ | Allowlisted sort, filters; plans at 100 000 orders in `docs/performance/baseline.md` |
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
| PF-039 | Order screens | ✅ | List, create, detail with history (`/orders/{id}/history`), cancel |
| PF-040 | Payment/refund screens | ✅ | Polling, attempts, refunds, retry |
| PF-041 | Dashboard and reports | ✅ | Per-currency metrics, charts, tables |
| PF-042 | Unit tests | ✅ | 91 .NET unit tests + 25 frontend tests |
| PF-043 | PostgreSQL integration tests | ✅ | 61 tests (real PostgreSQL) |
| PF-044 | RabbitMQ integration tests | ✅ | Real broker: confirm/route/consume, delayed retry queue round trip, dead-lettering, unroutable messages |
| PF-045 | E2E payment flow tests | ✅ | Playwright journey, platform admin, axe-core WCAG 2.1 AA scan |
| PF-046 | Architecture tests | ✅ | 13 rules |
| PF-047 | GitHub Actions CI | ✅ | `.github/workflows/ci.yml` |
| PF-048 | OpenTelemetry instrumentation | ✅ | API → worker trace via outbox `traceparent` |
| PF-049 | Grafana dashboards | ✅ | API, payments, outbox, queues incl. DLQ depth, worker, DB pool, sessions, lock waits, slow statements; 10 alert rules |
| PF-050 | k6 baseline load tests | ✅ | `docs/performance/baseline.md`: reads p95 23 ms / p99 47 ms, 0 % errors; SQL plans at volume |
| PF-051 | Staging deployment | 🟡 | Gated job + runbook; target platform not chosen |
| PF-052 | Tenant-safe AI reporting tools | ✅ | `AssistantTools` |
| PF-053 | AI evaluation and prompt safety tests | ✅ | `AssistantSafetyTests`, `docs/ai/assistant.md` |
| PF-054 | README, ADRs and demo | ✅ | README with screenshots and demo video, ADRs, `docs/demo.md`, `scripts/seed-demo.mjs` |

## Beyond the backlog

| Item | Status | Where |
|---|---|---|
| Platform administration (PlatformAdmin / SupportAgent, spec §2.1) | ✅ | `Application/Platform`, `/platform` page, `PlatformAdminTests` |
| PostgreSQL Row-Level Security for the app role | ✅ | migration `RowLevelSecurity`, `RowLevelSecurityModeTests` |
| Request time budget (30 s, assistant 120 s → 504) | ✅ | `ApiTimeoutOptions`, `RequestTimeoutTests` |
| Accessibility fixes (contrast, list semantics, label in name) | ✅ | `AccessibilityTests`, theme `contrastThreshold: 4.5` |
