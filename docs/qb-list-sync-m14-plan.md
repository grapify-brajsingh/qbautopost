# Plan — M14 QuickBooks list sync in the database, scheduled on the job worker, dashboard route

Contract: `docs/qb-list-sync-m14.md`. Tracker: `docs/tracker.md` › M14 (added by T-1400). One task at a time, tests
first for Core logic; build + test green before each commit (`T-14xx: <summary>`).

## Global constraints

- `/api/v1/**` byte-identical: `V1ContractPinTests` and the v1 OpenAPI hash stay green with no fixture change.
- No new NuGet package; no new hosted service, `Timer` or `PeriodicTimer`: the schedule lives in `JobWorker`'s loop.
- No test contacts a real QuickBooks or OpenRouter (`FakeQbGateway`, fake `IQbPresence`); `dotnet test` must pass on
  Linux (the Windows presence check is `[SupportedOSPlatform("windows")]`, tested through the interface).
- A failed sync never replaces the current lists. Nothing from a list answer except names and types is stored or logged.
- No-database mode keeps today's behaviour (file source).

## Files

| File | Change | Task |
|---|---|---|
| `docs/tracker.md`, `docs/adr/0012-qb-lists-in-postgres-scheduled-sync.md`, `CLAUDE.md`, `docs/CLAUDE.md` | M14 row/section, Q-119…Q-121, Decisions; ADR; timer rule wording | T-1400 |
| `src/QbAutopost.Core/QbXml/QbListQuery.cs`, `tests/fixtures/qbxml/list-query.golden.xml`, `tests/QbAutopost.Core.Tests/QbXml/QbListQueryTests.cs` | `IncludeRetElement` per query; golden updated; full vs trimmed answer parse equal | T-1401 |
| `src/QbAutopost.Core/Abstractions/{IQbListsSource,IQbListsStore,IQbPresence}.cs`, `src/QbAutopost.Core/Pipeline/{QbListSync,ListSyncRun}.cs`, `src/QbAutopost.Core/Store/FileQbLists.cs` | Interfaces; run record (trigger, requestedBy, outcome, reason, error, counts); sync takes trigger; file source + store | T-1402 |
| `tests/QbAutopost.Core.Tests/Pipeline/QbListSyncTests.cs` | ok/failed/rules-unreadable runs; failure keeps current lists; audit copies | T-1402 |
| `src/QbAutopost.Core/Pipeline/JobPipeline.cs`, `src/QbAutopost.Core/Mapping/RulesEditor.cs`, `src/QbAutopost.Api/Jobs/DirectPostRunner.cs`, `src/QbAutopost.Api/Endpoints/QuickBooksEndpoints.cs`, `src/QbAutopost.Api/Health/Readiness.cs` | read `IQbListsSource.Current` | T-1402 |
| `src/QbAutopost.Data/Entities/QbListSyncEntity.cs`, `src/QbAutopost.Data/Configurations/QbListSyncConfiguration.cs`, `src/QbAutopost.Data/QbAutopostDbContext.cs`, `src/QbAutopost.Data/EfQbListsStore.cs`, `src/QbAutopost.Data/Migrations/*_QbListSyncs.cs`, `deploy/migrations.sql`, `deploy/grants.sql` | table, store (insert + prune in one transaction, newest ok, newest attempt, import) | T-1403 |
| `tests/QbAutopost.Data.Tests/QbListSyncStoreTests.cs` | retention keeps 200 and the newest ok; newest ok/attempt; import once | T-1403 |
| `src/QbAutopost.Api/QuickBooks/DbQbListsSource.cs`, `src/QbAutopost.Api/Program.cs`, `src/QbAutopost.Api/Configuration/AppSettings.cs` (`ListSyncSettings`) | DB vs file wiring; startup import; settings + validation | T-1404 |
| `tests/QbAutopost.Api.Tests/QuickBooks/QbListsSourceWiringTests.cs` | DB mode / file mode / import / startup DB error | T-1404 |
| `src/QbAutopost.QuickBooks/QbPresence.cs`, `src/QbAutopost.Core/Gateway/FakeQbPresence.cs` (or the fake gateway's folder) | `QBW`/`QBW32` in the API's session; fake always open | T-1405 |
| `src/QbAutopost.Api/Jobs/{JobWorker,JobQueue,ListSyncSchedule}.cs` | due-time race in the loop, job-first, guard, skipped/failed rows, warning streak | T-1406 |
| `tests/QbAutopost.Api.Tests/Jobs/ScheduledListSyncTests.cs` | A-1, A-2 (scheduled), failure streak levels, interval 0 off, stop cancels the wait | T-1406 |
| `src/QbAutopost.Api/Endpoints/V2/QuickBooksListsV2Endpoints.cs`, `src/QbAutopost.Api/Endpoints/QuickBooksEndpoints.cs` (v2 GET shape), `src/QbAutopost.Api/Endpoints/ApiRoutes.cs` (scope, rate bucket), `src/QbAutopost.Api/wwwroot/openapi-v2.json` | `lastSync`, `nextSyncUtc`; `POST /api/v2/quickbooks/lists/sync`; join an in-flight sync | T-1407 |
| `tests/QbAutopost.Api.Tests/Api/V2QbListsSyncTests.cs`, `tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs`, `tests/QbAutopost.Api.Tests/Api/SyncListsApiTests.cs` | A-2 (route), A-3, A-4, A-5; v1 sync recorded with trigger `api` | T-1407 |
| `src/QbAutopost.Api/Logging/StartupLog.cs`, `docs/runbook.md` | startup line; runbook sections | T-1408 |
| `docs/testing/T-1409.server-checklist.md` | A-10 on the QuickBooks machine | T-1409 |

## Tasks

| ID | Task | Depends on | Expected outcome |
|---|---|---|---|
| T-1400 | Tracker M14, Q-119…Q-121, Decisions D-1…D-4, ADR-0012, CLAUDE.md timer wording | — | Docs only; `OperatorDocsTests` green |
| T-1401 | List query asks only for the fields used (spec §10) | T-1400 | Golden test red, then green with the new XML; parse-equality test green |
| T-1402 | Core: `IQbListsSource`/`IQbListsStore`/`IQbPresence`, `ListSyncRun`, `QbListSync.SyncAsync(trigger, requestedBy, ct)`, file implementations, readers switched | T-1401 | New `QbListSyncTests` red then green; every existing reader test green unchanged |
| T-1403 | Data: `qb_list_syncs`, `EfQbListsStore`, migration, `migrations.sql`, grants | T-1402 | `QbListSyncStoreTests` (Testcontainers) red then green; `RequireCurrentSchema` check green |
| T-1404 | Api wiring: DB source when `Database:ConnectionString` is set, else file; startup import; `QuickBooks:ListSync` settings | T-1403 | Wiring tests red then green; A-7, A-8 |
| T-1405 | `IQbPresence` Windows implementation + fake | T-1402 | Builds on Linux; interface tests use the fake; Windows-only code guarded |
| T-1406 | Scheduled sync inside `JobWorker` (spec §5) | T-1404, T-1405 | `ScheduledListSyncTests` red then green (fake clock + fake gateway); no new hosted service (`EndpointSurfaceTests`/architecture test asserts one `BackgroundService`) |
| T-1407 | v2 GET additions and `POST /api/v2/quickbooks/lists/sync` (spec §7); v1 recorded as `api` | T-1406 | `V2QbListsSyncTests` red then green; scope matrix; v1 pin and OpenAPI drift green |
| T-1408 | Startup log line, readiness text, runbook | T-1407 | Startup log test; `OperatorDocsTests` green |
| T-1409 | [server] checklist A-10 | T-1408 | `ready-for-human`; stop |

## Review focus

1. **QuickBooks closed on the server**: the scheduled sync must not start `QBW.EXE` (guard before any SDK call).
2. **A job submitted at the due time**: the job runs first; the sync never interleaves inside a job.
3. **A dialog open in QuickBooks**: the worker may be held ~2 min by `BeginSession`; logged, and the next attempt is a
   full interval later (no retry storm).
4. **Database down at startup**: pickers empty but the app starts; readiness says why; the first `ok` sync recovers.
5. **Two people press Sync now together**: one gateway call; both see the same result.

## Verification

```
dotnet build -warnaserror
dotnet test --filter "FullyQualifiedName~QbListQuery|FullyQualifiedName~QbListSync|FullyQualifiedName~ScheduledListSync|FullyQualifiedName~V2QbListsSync|FullyQualifiedName~V2ScopeMatrix|FullyQualifiedName~V1ContractPin|FullyQualifiedName~OpenApi"
dotnet test
dotnet tool run dotnet-ef migrations script --idempotent -p src/QbAutopost.Data -s src/QbAutopost.Data -o deploy/migrations.sql
```

Local manual check (Windows, QuickBooks Enterprise 2024, `IntervalMinutes=5`): QuickBooks open → `ok` row; QuickBooks
closed → `skipped` and no `QBW.EXE` appears; dashboard Sync now updates the pickers.

## Milestones

- [ ] T-1400 docs and decisions
- [ ] T-1401 trimmed list query
- [ ] T-1402 Core abstractions and readers
- [ ] T-1403 database table and store
- [ ] T-1404 wiring and import
- [ ] T-1405 QuickBooks presence
- [x] T-1406 scheduled sync in the worker
- [x] T-1407 v2 routes
- [ ] T-1408 log, readiness, runbook
- [ ] T-1409 server checklist (ready-for-human)
- [ ] Full `dotnet test` green on Windows; Linux run recorded

## Results

(filled in during implementation)
