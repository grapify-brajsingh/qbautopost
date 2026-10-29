# ADR-0012: QuickBooks lists in PostgreSQL, refreshed by a scheduled sync on the job worker

**Date**: 2026-10-29
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

The account, vendor and customer names pulled from QuickBooks (FR-15) lived only in `qb-lists.json` with an audit copy
of the raw answer, and were refreshed only when someone called the sync route. The dashboard needs a "Sync now" button
and a "last synced" display, the lists drive pickers and validation, and people see "not in QuickBooks" when the copy is
stale. Spec: `docs/qb-list-sync-m14.md` (found while testing against QuickBooks Enterprise 2024).

## Decision

1. **D-1**: the synced lists are stored in PostgreSQL (`qb_list_syncs`, one row per sync attempt with who, when,
   result and error), not only in `qb-lists.json`. The file remains the no-database fallback only.
2. **D-2**: a scheduled sync every 30 minutes is run by the existing single `JobWorker` (ADR-0003): no new hosted
   service and no `System.Threading.Timer`. A job in flight always finishes first, and the sync never starts
   QuickBooks if it is not already open.
3. **D-4**: names are not fetched from QuickBooks on demand. Pickers and validate read the stored copy.

(D-3, the Sync now button on the v2 route, follows from the same request and is specified in the spec only.)

### Trade-off: a stored copy vs asking QuickBooks every time

| | Stored copy, refreshed (chosen) | Live call when needed |
|---|---|---|
| Freshness | Up to one interval old (30 min), or until someone presses Sync now | Always current |
| Speed | Memory read | 3-7 s per call (measured: 71 accounts / 282 vendors, about 180 KB answer) |
| QuickBooks closed / showing a dialog | Pickers and validate still work | Every page fails, or waits up to about 2 min while QuickBooks shows a dialog |
| Effect on posting | None | The SDK allows one call at a time for the whole process, so every grid load would queue in front of posts |
| Consistency | Validate and post use the same list | The list can change between validate and post |
| Safety | Unchanged: QuickBooks still refuses a name it does not have when the post arrives | Unchanged |

## Alternatives Considered

### Alternative 1: Ask QuickBooks on every picker load or validate
- **Pros**: always current.
- **Cons**: see the table: slow, fails when QuickBooks is closed, queues in front of posts.
- **Why not**: owner decision D-4.

### Alternative 2: Keep `qb-lists.json` only, add a timer
- **Pros**: no schema change.
- **Cons**: no sync history for the dashboard, client names stay on disk, and a second timer breaks the one-worker
  rule (ADR-0003).
- **Why not**: owner decisions D-1 and D-2.

## Consequences

### Positive
- Pickers and validate keep working while QuickBooks is closed or busy.
- The dashboard can show who synced, when, and why a sync failed.
- Client names no longer sit in a file and its audit copy (the audit copy keeps names and types only).

### Negative
- Lists can be up to one interval old; the Core readers of `qb-lists.json` move behind an interface.
- The `JobWorker` loop owns a schedule as well as the queue (CLAUDE.md "Don't" wording updated).

### Risks
- A dialog open in QuickBooks can hold the worker for about 2 minutes while the SDK waits; the next attempt is a full
  interval later (no retry storm).
- Database down at startup: pickers are empty until the first successful sync; readiness says why.
