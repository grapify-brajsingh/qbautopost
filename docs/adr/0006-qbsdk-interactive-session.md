# ADR-0006: Post via QuickBooks Desktop SDK (qbXML over COM) from an interactive session

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md §4 and FR-11)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

The target is QuickBooks Desktop, not QuickBooks Online. Its only supported way to write data from code is the Desktop SDK's `QBXMLRP2.RequestProcessor` COM object. The SDK needs a logged-on user session so its certificate dialog can appear, and the app's bitness must match QuickBooks.

## Decision

- The app sends qbXML (version 13.0 by default) through late-bound COM, with one session per call and `continueOnError`.
- It runs as a normal process started by Task Scheduler when an auto-logon account signs in, never as a Windows Service.
- The qbXML element order is fixed and protected by golden-file tests.

## Alternatives Considered

### Alternative 1: QuickBooks Web Connector (SOAP polling)
- **Pros**: Officially supports running unattended.
- **Cons**: It pulls work on a schedule, needs a hosted SOAP service, and gives slower feedback.
- **Why not**: Too many moving parts for a single-server setup.

### Alternative 2: Windows Service
- **Pros**: Starts without anyone logging on.
- **Cons**: The SDK is unreliable from session 0, where its UI dialogs can't appear.
- **Why not**: A known SDK limitation.

### Alternative 3: Third-party drivers (QODBC, CData)
- **Pros**: SQL-like access.
- **Cons**: License cost, and still COM underneath.
- **Why not**: Not needed.

## Consequences

### Positive
- Transactions are native QuickBooks entries. They can be undone with `TxnDelRq`, and posted amounts are checked against the amounts QuickBooks echoes back.

### Negative
- The server needs auto-logon, and bitness must be matched by hand.

### Risks
- The COM enum constants may be wrong. Mitigation: they live only in `QbSession`, and T-609 checks them first.
