# ADR-0010: Dashboard users, Identity and short-lived JWTs

**Date**: 2026-10-27
**Status**: accepted
**Deciders**: Braj (owner, ticket #2, 2026-10-27), Claude Code (drafted it)
**Relates to**: rests on ADR-0009; specified by `docs/saved-requests-postgres.md` §11

## Context

Until now every caller is a machine holding an API key. The dashboard has named people with roles (admin, editor,
viewer), and the success measure is that a named person pressed Post. The dashboard is a separate repository served
from the same site as the API (Q-82).

## Decision

Users live in ASP.NET Core Identity on PostgreSQL. Sign-in returns an HMAC-SHA256 JWT access token (15 minutes) and
sets a rotating refresh token in an `HttpOnly; Secure; SameSite=Strict` cookie scoped to `/api/v1/auth` (7 days;
rotated on use, reuse of a revoked token revokes the family). The caller resolver accepts `Authorization: Bearer`
or `X-Api-Key`, never both. Role scopes are people-only (spec §11.3): `requests:edit`, `requests:post`,
`requests:admin` and `users:manage` are satisfied only by a signed-in user; an API key never holds them, including
the `admin` scope and the legacy shared key.

## Alternatives Considered

### Alternative 1: Cookie session only
- **Pros**: Simple; server-side revocation.
- **Cons**: Needs CSRF protection on every write; poor fit for non-browser clients.
- **Why not**: Bearer tokens keep the API-key and user paths uniform in one resolver.

### Alternative 2: Long-lived JWT, no refresh
- **Pros**: Fewer moving parts.
- **Cons**: A stolen token works for days and cannot be revoked.
- **Why not**: Money moves on these routes; the refresh cookie stays out of JavaScript so an XSS bug cannot steal a
  long-lived credential.

### Alternative 3: External identity provider
- **Pros**: Offloads password handling.
- **Cons**: New dependency and infrastructure on a single-server deployment.
- **Why not**: Not warranted for a handful of users.

## Consequences

### Positive
- Every post by a person is attributable; API keys cannot impersonate that.
- A disabled or demoted user loses access within the access-token lifetime (stamp check on each request).

### Negative
- The dashboard must be served from the same site as the API (or via a reverse proxy); cross-site needs
  `SameSite=None` plus credentialed CORS (Q-82).
- `ApiClient.Allows` changes: `admin` now means every scope except the four people-only scopes.

### Risks
- A weak or leaked JWT signing key lets anyone mint tokens: the key comes from configuration/environment, is never
  logged, and the host refuses to start without it when the database feature is on.
