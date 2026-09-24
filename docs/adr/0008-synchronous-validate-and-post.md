# ADR-0008: Synchronous validate-and-post — refuse when busy, all-or-nothing on validation

**Date**: 2026-09-24
**Status**: proposed
**Deciders**: Braj (owner, requested it), Claude Code (drafted it)
**Relates to**: amends ADR-0003 for one route; rests on ADR-0006; specified by `docs/validate-and-post-transaction-data.md`

## Context

FR-A-8's `POST /api/v1/quickbooks/transactions` ships `dryRun: true` by default, enqueues the post on the single
worker, and answers `202` with a batch to poll when the work outlasts `Api:SyncPostTimeoutSeconds`. The owner wants
one route that does none of that: validate, report anything found, otherwise post — one request, one response.

Two constraints do not move. The QuickBooks SDK holds **one session at a time** (ADR-0006), so SDK calls must stay
serialised however the caller is answered. And G4 duplicate detection and undo (FR-13) both read the **ledger row**,
so a post cannot go unrecorded even if it is not a "batch".

## Decision

Add `POST /api/v1/quickbooks/transactions/validate-and-post`: no `dryRun` field, no `202`, no pollable batch. It takes
the exclusive SDK lock **only if it is free** and answers `409` immediately when it is not, and it posts **all rows or
none** — any held row ends the request at `422` before the SDK is touched.

## Alternatives Considered

### Alternative 1: Queue behind the worker and wait, as FR-A-8 does
- **Pros**: no request is ever refused; reuses `RunExclusiveAsync` unchanged.
- **Cons**: the caller waits an unbounded time in a line they cannot see or cancel; a 10-minute folder job makes a
  one-second post take 10 minutes, with nothing in the response explaining why.
- **Why not**: the owner asked for "not queued", and a hidden wait is the thing they are asking to remove. `409` with
  `Retry-After` puts the decision back with the caller, who knows whether to wait.

### Alternative 2: Drop serialisation entirely and post concurrently
- **Pros**: literally "not queued"; no `409` ever.
- **Cons**: concurrent `QBXMLRP2` sessions collide — the second call fails, or worse, interleaves with a folder job
  mid-post.
- **Why not**: ADR-0006. This is not a policy choice; the SDK does not support it.

### Alternative 3: Keep FR-A-8's per-row semantics (post the good rows, report the held ones)
- **Pros**: one bad payee does not block 133 good rows; it is the behaviour already built and tested.
- **Cons**: leaves a partially posted state the caller must reconcile, which is what `partial` means — and T-919
  showed how easily `partial` is misread.
- **Why not**: the owner's wording is "return required validation results **if any**, **then** post". All-or-nothing
  is the literal reading and it posts less, the direction CLAUDE.md rule 4 requires. Recorded as **Q-A**, because the
  cost of it lands on the owner.

### Alternative 4: A second worker, or a lock with a timeout
- **Pros**: fewer spurious `409`s.
- **Cons**: a second worker reintroduces exactly the collision ADR-0003 exists to prevent; a lock with a timeout is a
  queue with a small number on it.
- **Why not**: CLAUDE.md forbids background workers beyond the single `JobWorker`. A short bounded wait is recorded
  as **Q-D** rather than chosen, since it is a judgement about one operator's server.

## Consequences

### Positive
- A caller gets one answer to one question, with the `TxnID`s in the same response.
- Nothing posts by accident: `dryRun` cannot be requested, so it cannot be believed.
- No half-posted batch to reconcile, and no `partial` status to misread.
- `409` is honest about contention instead of hiding it as latency.
- FR-A-8, the folder path, and every existing caller are untouched — this is an addition.

### Negative
- **One unresolvable row blocks the whole request.** On a 134-row statement that is 133 good rows refused.
- The caller holds an HTTP connection for the length of a real SDK post — minutes for a large batch — and must set a
  client timeout to match (**Q-B**).
- Two routes now post, with different semantics. That is more surface to document and to keep honest.
- Callers must handle `409`, a status the rest of the API never returns.

### Risks
- **A retry after a lost response double-posts.** Mitigated by `Idempotency-Key`, and by G4 + the ledger row — which
  is precisely why "not batched" cannot mean "not recorded". Whether the header should be mandatory here is **Q-C**.
- **A client disconnect does not stop the post**, by design: abandoning a half-sent qbXML would leave QuickBooks in a
  state nobody recorded. The caller must reconcile with `externalId` rather than assume nothing happened.
- **`409` under normal use** if the operator runs folder jobs and direct posts together. Low on a single-operator
  server; the response says when to retry.
