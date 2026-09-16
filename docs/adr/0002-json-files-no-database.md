# ADR-0002: JSON files and job folders as the only state store

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md §2 and §13)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

The app runs on a single QuickBooks server and handles only a few jobs per period. Its state is:
- each job's status
- the ledger of posted TxnIDs
- rules learned from people
- a cached copy of the QuickBooks lists

Operators should be able to inspect all of it by opening files.

## Decision

Each job's status lives in its `output/status.json`. Shared state lives in `ledger.json`, `rules.json` and `qb-lists.json`. Shared files are written atomically (write a temp file, then rename). Every state transition is saved before the next step starts.

## Alternatives Considered

### Alternative 1: SQLite
- **Pros**: Transactions and queries.
- **Cons**: One more dependency, operators can't read it directly, and it isn't on the approved NuGet list.
- **Why not**: The data volume doesn't justify it, and the spec forbids a database.

### Alternative 2: SQL Server or Postgres
- **Pros**: Robust multi-user storage.
- **Cons**: A heavy operations burden for a single server.
- **Why not**: Far more than the app needs.

## Consequences

### Positive
- No infrastructure to run. The audit trail is human-readable and sits next to the input files.

### Negative
- Only one process can write at a time, and `GET /jobs` has to scan folders.

### Risks
- A crash during a write could corrupt the ledger. Mitigation: atomic rename, and the single worker serialises all writes.
