## What and why

<!-- Problem, scope, linked issue (PF-xxx) -->

## Definition of Done (spec §19.1)

- [ ] Builds without warnings; lint passes
- [ ] Unit and integration tests written and green (real PostgreSQL, no EF InMemory)
- [ ] Request validation and consistent problem-details errors
- [ ] Authentication, authorization and tenant scope verified (incl. a cross-tenant negative test where relevant)
- [ ] Database constraints / indexes / migration reviewed (backward compatible)
- [ ] No secrets or PII in logs, events or audit metadata; correlation id present
- [ ] OpenAPI / README / ADR updated if behaviour or decisions changed
- [ ] Reviewed; important comments resolved
