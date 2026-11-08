# Runbook: deploy and rollback

## Principles
- Images are immutable and tagged with the commit SHA (`ghcr.io/<owner>/payflow-api:<sha>`); secrets come from the environment / secret store, never from images or the repository.
- Containers run as non-root and expose `/health/live` (process up) and `/health/ready` (dependencies reachable).
- **Migrations are backward compatible** (expand → migrate → contract): the old version must keep working with the new schema, so the API can be rolled back without a database rollback.

## Deploy
1. CI on `main` is green (build, unit, architecture, integration, E2E).
2. Run the migrator job with the owner connection string: `dotnet PayFlow.Api.dll --migrate` (container: `command: ["--migrate"]`). It must exit 0.
3. Roll out Worker, then API, then Web (rolling; readiness gates traffic).
4. Smoke test: `GET /health/ready`, sign in, create an order, pay with `pm_test_success`, check it reaches `Succeeded`.
5. Watch the dashboard for 15 minutes (5xx, p95, outbox backlog, dead letters).

## Rollback
- **Application regression:** redeploy the previous image tag (schema is backward compatible).
- **Bad migration:** prefer a forward fix (new migration). Only use `dotnet ef database update <previous>` if the migration's `Down` is known to be safe and no new data depends on it.
- **Message format change:** contracts are versioned (`*.v1`); deploy consumers that understand the new version before producers emit it.

## Configuration checklist (staging/production)
| Setting | Value |
|---|---|
| `ConnectionStrings__PayFlow` | app role (`payflow_app`) |
| `ConnectionStrings__PayFlowSystem` | owner/system role |
| `Database__EnableRowLevelSecurity` | `true` |
| `Auth__Jwt__SigningKey` | ≥ 32 random bytes from the secret store |
| `Auth__RefreshToken__SecureCookie` | `true` (TLS) |
| `MockProvider__WebhookSecret` | secret store |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | collector endpoint |
| `RateLimiting__*` | tuned to expected load |
