# ADR-0009: PostgreSQL for saved requests and dashboard users

**Date**: 2026-10-27
**Status**: accepted
**Deciders**: Braj (owner, ticket #2, 2026-10-27), Claude Code (drafted it)
**Relates to**: supersedes ADR-0002 in part; specified by `docs/saved-requests-postgres.md`

## Context

The owner wants a dashboard where people see, edit, re-validate and post saved requests, with a history of every
version, attempt and event, and with named users and roles. That is relational, multi-user, concurrently edited
data with uniqueness and optimistic-concurrency rules. ADR-0002 chose JSON files because the app held only job
status, a ledger, rules and cached lists; it also said "the spec forbids a database". The owner has now reversed
that rule for this feature (spec §2). The ledger is not part of the change: G4 and undo read `ledger.json`, and
moving it would alter two safety properties at once.

## Decision

PostgreSQL via EF Core 8 code-first holds saved requests, their versions, attempts, events and dashboard users. All
other state stays in JSON files.

## Alternatives Considered

### Alternative 1: SQLite
- **Pros**: No server to run; one file; same EF Core model.
- **Cons**: One writer at a time with file locking over a network share risk; no row-level concurrency; not on the
  approved list; weaker fit for several simultaneous dashboard users and for backups with point-in-time recovery.
- **Why not**: The dashboard is multi-user and the data is the audit trail of who pressed Post. PostgreSQL is the
  owner's stated choice and handles concurrent editors properly.

### Alternative 2: More JSON files
- **Pros**: No new infrastructure; consistent with ADR-0002; readable by hand.
- **Cons**: No transactions across a request, its versions and its attempts; no unique constraints; no queries or
  paging; user and password storage would be hand-rolled.
- **Why not**: Optimistic concurrency, duplicate detection and Identity-grade password handling are exactly what
  files do badly.

## Consequences

### Positive
- Real transactions, constraints and concurrency control for the saved-request lifecycle.
- ASP.NET Core Identity gives lockout and password hashing without hand-rolled code.

### Negative
- A database to install, back up and monitor on the server (deploy task T-1016).
- A second store: operators can no longer see all state by opening files.
- Five new NuGet packages (approved: Q-80).

### Risks
- **Database outage.** Saved-request and login routes answer `503 database-unavailable`; every existing route is
  unaffected because a DbContext is never resolved on their path; `/health/ready` reports the database per feature
  (spec §10.7, Q-83).
- **Ledger not moved.** A saved request points at the ledger row its post produced (`postId`); it does not replace
  it, so G4 duplicate detection and undo behave as before (spec §2).
- **Two stores disagreeing** after a crash between a QuickBooks post and the database write: handled by the
  `unknown` state and admin-only resolve (spec §10).
