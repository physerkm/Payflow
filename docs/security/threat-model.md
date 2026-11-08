# Threat model (STRIDE, MVP scope)

Assets: tenant business data (customers, orders, payments), financial integrity (no double charge, refund limits), user credentials and sessions, provider webhook secret, audit trail.

Trust boundaries: browser ↔ API; API/Worker ↔ PostgreSQL; Worker ↔ RabbitMQ; Worker/API ↔ payment provider (webhooks inbound); API ↔ LLM provider (assistant).

| Threat | Example | Controls | Evidence |
|---|---|---|---|
| **Spoofing** | Stolen password, forged JWT | Identity password policy + lockout, generic errors, login rate limit; JWT signature/issuer/audience/lifetime validation, HS256 key ≥ 32 bytes | `AuthTests` |
| | Forged webhook | HMAC-SHA256 over `timestamp.body`, constant-time compare, 5-minute window | `WebhookSignatureTests`, `PaymentFlowTests` |
| **Tampering** | Client sends its own price/total | Prices read from the catalog, snapshots in order items, server-computed totals | `OrderTests` |
| | Mass assignment (`tenantId` in body) | Request DTOs only; tenant from server context | `TenantIsolationTests` |
| | Webhook with wrong amount | Amount/currency must match the payment | `PaymentFlowTests` |
| **Repudiation** | "I didn't refund that" | Append-only audit log (actor, tenant, action, resource, correlation id) | `audit_logs`, Settings page |
| **Information disclosure** | BOLA/IDOR across tenants | Tenant context + query filters + composite FKs + RLS + 404 | `TenantIsolationTests`, `DatabaseConstraintTests`, `RowLevelSecurityModeTests` |
| | Token theft via XSS | Access token in memory; refresh token HttpOnly/SameSite=Strict/path-scoped; CSP | ADR-010 |
| | Leaking internals in errors | RFC 7807 with generic 500 + correlation id | `GlobalExceptionHandler` |
| | Secrets/PII in logs, events, audit | No tokens/passwords/card data logged; events carry ids only; audit metadata minimal | `IntegrationEventMapperTests` |
| **Denial of service** | Request floods, huge bodies, unbounded lists | Global/auth/assistant rate limits, 1 MB body limit, page size ≤ 100, report range ≤ 366 days | `ApiServiceCollectionExtensions` |
| | Slow provider exhausting resources | Async payments, timeouts, circuit breaker | ADR-007/009 |
| **Elevation of privilege** | Admin grants themselves Owner; removing the last owner | Domain `MembershipPolicy`, tenant row lock | `MembershipPolicyTests`, `AuthorizationTests` |
| | Squatting a configured platform email | Platform roles are email-based without email verification → operators register those accounts before configuring them; roles re-checked live on every call | `PlatformAdminTests` |
| | Platform staff reading tenant data | Platform roles only expose tenant metadata; business endpoints still require a tenant membership | `PlatformAdminTests` |
| | Revoked member keeps access until token expiry | Membership re-checked on every request | `AuthorizationTests` |
| | LLM performs actions / reads other tenants | Allowlisted read-only tools, tenant from context, input validation, no SQL | `AssistantSafetyTests` |

## Financial integrity

| Risk | Control |
|---|---|
| Duplicate charge from retries | Idempotency key (same transaction), one live payment per order (partial unique index), provider idempotency key = attempt id |
| Charge after timeout | `Unknown` state + reconciliation; no blind retry |
| Refunds above captured amount | Row lock + reservation + `refunded + pending <= amount` check constraint |
| Lost events | Transactional outbox, publisher confirms, mandatory routing |
| Duplicate events | Inbox + aggregate idempotency |

## Out of scope / future work
Real card data (PCI DSS) — never handled; MFA; email verification; per-tenant encryption keys; WAF; automated dependency/container scanning beyond `dotnet list package --vulnerable`, `npm audit` and gitleaks in CI.
