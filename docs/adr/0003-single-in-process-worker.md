# ADR-0003: Single in-process job worker (Channel + BackgroundService)

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md FR-11 and §14, and CLAUDE.md)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

The QuickBooks SDK handles only one session at a time and blocks while QuickBooks is busy. Jobs arrive infrequently. The target is a 300-line job in under 10 minutes.

## Decision

`POST /jobs` validates the folder synchronously, then puts the job on a `Channel<JobRequest>`. One `JobWorker : BackgroundService` processes jobs one at a time, which also serialises every SDK call. The app has no other timers or background services.

## Alternatives Considered

### Alternative 1: A message broker (RabbitMQ, Azure Service Bus)
- **Pros**: Durable queue and scale-out.
- **Cons**: More infrastructure, and the SDK can't run calls in parallel anyway.
- **Why not**: No benefit on a single server, and CLAUDE.md forbids it.

### Alternative 2: Hangfire or Quartz
- **Pros**: Built-in retries and a dashboard.
- **Cons**: Needs a storage backend and a new package.
- **Why not**: Not on the approved package list, and it would add a database.

## Consequences

### Positive
- The design is simple, and SDK calls can't collide.

### Negative
- Queued jobs are lost on restart. Startup recovery covers this: `analysing` becomes `failed`, and `posting` becomes `partial`.

### Risks
- A hung SDK call blocks every job. Mitigation: after `BusyTimeoutSeconds` the call is abandoned and the job becomes `partial (quickbooks-busy)`.
