# ADR-0001: Cross-platform Core; QuickBooks COM isolated in its own project

**Date**: 2026-09-15 (backfilled on 2026-09-16 from spec.md §4 and the tracker's Decisions table)
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

QuickBooks Desktop is only reachable through the Windows-only `QBXMLRP2.RequestProcessor` COM object, and only on the server. Most of the logic (parsing, mapping, gates, qbXML, ledger) does not need QuickBooks, and it must be developed and tested on any OS, including Linux CI.

## Decision

We use one .NET 8 solution with three source projects:
- `QbAutopost.Core` (net8.0, no COM)
- `QbAutopost.QuickBooks` (the only project that touches COM; marked `[SupportedOSPlatform("windows")]`)
- `QbAutopost.Api` (the web host)

Core depends on `IQbGateway`. The API uses `FakeQbGateway` when it is not running on Windows or when `QuickBooks:Fake=true`. `global.json` pins the SDK to 8.0.x, because SDK 10 is also installed on the dev box.

## Alternatives Considered

### Alternative 1: One Windows-only project (like the POC)
- **Pros**: Fewer projects and no abstraction layer.
- **Cons**: Tests need Windows and QuickBooks, so there is no Linux CI.
- **Why not**: Breaks the requirement that tests run on any OS.

### Alternative 2: Target net8.0-windows everywhere
- **Pros**: COM types are available in every project.
- **Cons**: Core can't be built or tested on Linux.
- **Why not**: Same reason as Alternative 1.

## Consequences

### Positive
- `dotnet test` runs on any OS. Server time is needed only for M6 and M8.
- The risk of wrong COM enum constants stays inside `QbSession`.

### Negative
- There is one more abstraction (`IQbGateway`) and a fake to maintain.

### Risks
- The fake may drift from real SDK responses. Mitigation: golden qbXML files, plus the manual T-609 check on a copy of the company file.
