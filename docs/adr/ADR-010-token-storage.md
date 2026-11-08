# ADR-010: Token storage for the SPA

**Status:** Accepted · **Date:** 2026-11-07

## Context
Tokens in `localStorage` are readable by any injected script (XSS). A full BFF adds another deployable.

## Decision
- Short-lived (15 min) JWT access token, returned in the response body and kept **in memory only**; renewed shortly before expiry.
- Refresh token in an **HttpOnly, Secure, SameSite=Strict** cookie scoped to `/api/v1/auth`, stored server-side only as a SHA-256 hash and **rotated on every use**. Presenting a revoked token revokes the whole token family (theft detection). Logout revokes the family.
- The SPA and API share one origin (Vite proxy in development, nginx in Docker), so no CORS credentials are needed.
- The active tenant is re-validated on every request, so role changes apply without waiting for token expiry.

## Consequences
- ✅ JavaScript can never read the long-lived credential; CSRF is mitigated by SameSite=Strict and path scoping.
- ⚠️ Two simultaneous refreshes from different tabs can race; the loser gets 401 and the SPA asks the user to sign in again.
- A BFF remains an option if the threat model requires keeping access tokens out of the browser entirely.
