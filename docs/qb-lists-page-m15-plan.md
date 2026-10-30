# Plan — M15 QuickBooks list sync history route and a seeded simulated company

Contract: `docs/qb-lists-page-m15.md`. Tracker: `docs/tracker.md` › M15 (added by T-1500). One task at a time, tests
first; build + test green before each commit (`T-15xx: <summary>`). Branch: on top of `feature/m14-qb-list-sync`
(M14 is not merged yet).

## Global constraints

- `/api/v1/**` byte-identical (`V1ContractPinTests`, v1 OpenAPI hash).
- No new package, table, column, hosted service or timer; no migration.
- No test contacts a real QuickBooks or OpenRouter; `dotnet test` passes on Linux.
- The history never returns `error`, `missingInRules` or `lists`.

## Files

| File | Change | Task |
|---|---|---|
| `docs/tracker.md` | M15 row and section, Q-125…Q-127, Session log | T-1500 |
| `src/QbAutopost.Core/Abstractions/IQbListsStore.cs`, `src/QbAutopost.Core/Pipeline/{ListSyncQuery,ListSyncPage,ListSyncAttempt}.cs` | `ListAttemptsAsync`, records | T-1501 |
| `src/QbAutopost.Core/Store/FileQbLists.cs`, `tests/QbAutopost.Core.Tests/Store/FileQbListsHistoryTests.cs` | in-memory history, capped | T-1501 |
| `src/QbAutopost.Data/EfQbListsStore.cs`, `tests/QbAutopost.Data.Tests/QbListSyncStoreTests.cs` | projection without `lists`/`error`, filters, paging, `CurrentId` | T-1501 |
| test fakes implementing `IQbListsStore` (`tests/**/TestSupport/*`) | new member | T-1501 |
| `src/QbAutopost.Api/Endpoints/V2/QuickBooksListsV2Endpoints.cs` | `GET /quickbooks/lists/syncs`, query parsing, shared run view | T-1502 |
| `src/QbAutopost.Api/Endpoints/ApiRoutes.cs` | scope `lists:read` (+ `qb:read` key) for the new path | T-1502 |
| `src/QbAutopost.Api/wwwroot/openapi-v2.json` | route and `QbListSyncRunPage` schema | T-1502 |
| `tests/QbAutopost.Api.Tests/Api/V2QbListSyncHistoryTests.cs`, `tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs` | A-1…A-5, A-7 | T-1502 |
| `tests/QbAutopost.Data.Tests/Api/…` (Testcontainers host) | A-1 against PostgreSQL | T-1502 |
| `src/QbAutopost.Api/QuickBooks/{QbConnection,SimulatedListsSeed}.cs`, `src/QbAutopost.Api/Logging/StartupLog.cs` | seed from `Paths:QbLists` in simulated mode; log line | T-1503 |
| `tests/QbAutopost.Api.Tests/QuickBooks/SimulatedListsSeedTests.cs` | A-8 | T-1503 |
| `docs/runbook.md` (§13 Docker stack, list sync section) | history route; simulated company seeded | T-1504 |

## Tasks

| ID | Task | Depends on | Expected outcome |
|---|---|---|---|
| T-1500 | Tracker M15, Q-125…Q-127 | — | Docs only; `OperatorDocsTests` green |
| T-1501 | Store: `ListAttemptsAsync` (EF and file), tests first | T-1500 | Store tests red then green: order, trigger/outcome filters, paging, total, `CurrentId`, columns not read, file cap |
| T-1502 | Route, scope, OpenAPI, tests first | T-1501 | `V2QbListSyncHistoryTests` red (404) then green; scope matrix row; drift tests green; v1 pins green |
| T-1503 | Seed the simulated company from `Paths:QbLists`, tests first | — | `SimulatedListsSeedTests` red then green (A-8); startup log line |
| T-1504 | Runbook, Session log; Docker stack check with the dashboard | T-1502, T-1503 | Runbook updated; in the stack Sync now returns the seed counts and the history route lists it |

## Verification

```
dotnet build -warnaserror
dotnet test
docker compose -f D:\qb_post_react\docker-compose.integration.yml up -d --build api
```

Then from the dashboard repo: `npx playwright test -c playwright.integration.config.ts` (includes
`lists-page.spec.ts`).

## Milestones

- [x] Spec and plan written
- [x] T-1500 tracker
- [ ] T-1501 store
- [ ] T-1502 route
- [ ] T-1503 seeded simulated company
- [ ] T-1504 runbook, stack check
