# Plan — M13 dashboard integration fixes

Contract: `docs/dashboard-integration-m13.md`. Tracker: `docs/tracker.md` › M13. One task at a time, tests first.

## Files

| File | Change | Task |
|---|---|---|
| `src/QbAutopost.Api/Endpoints/V2/UserViewsV2.cs` | `UserViewV2` gains `RowVersion` (ConcurrencyStamp) | T-1301 |
| `src/QbAutopost.Api/wwwroot/openapi-v2.json` | users list row: `rowVersion`; audit `detail` description | T-1301, T-1302 |
| `tests/QbAutopost.Data.Tests/Api/V2UsersTests.cs` | row version equals ETag; enable with it; stale → 412; v1 row has none | T-1301 |
| `src/QbAutopost.Api/Endpoints/V2/AuditEventEndpoints.cs` | resolve subject users with the actor batch; add `email`/`displayName` to user-event detail | T-1302 |
| `tests/QbAutopost.Api.Tests/SavedRequests/V2AuditEventsTests.cs` | subject name/email added; stored value kept; unknown id unchanged | T-1302 |
| `Dockerfile`, `.dockerignore`, `docs/runbook.md` | Linux image for the local integration stack; runbook note | T-1303 |
| `src/QbAutopost.Api/Logging/{ClientAbortMiddleware,RequestLog}.cs`, `Program.cs` | 499 for a client abort, logged at Information | T-1304 |
| `tests/QbAutopost.Api.Tests/Logging/ClientAbortTests.cs` | 499, rethrow when not aborted, rethrow other faults, level | T-1304 |
| `docs/tracker.md` | M13 milestone row, section, Q-116/Q-117, session log | T-1300…T-1303 |

## Tasks

| ID | Task | Depends on | Expected outcome |
|---|---|---|---|
| T-1300 | Tracker: M13 row and section, questions | — | Docs only; build and test green |
| T-1301 | R-15 `rowVersion` on v2 user rows | T-1300 | New `V2UsersTests` facts red, then green; v1 users unchanged; OpenAPI v2 drift test green |
| T-1302 | R-7.3 subject email and name on user events | T-1300 | New `V2AuditEventsTests` facts red, then green; viewers still see no user events |
| T-1303 | Dockerfile and runbook note | — | `docker build .` succeeds; integration compose stack healthy; dashboard `e2e-integration` green |
| T-1304 | Client abort answered 499 at Information (F-3) | — | `ClientAbortTests` red then green; no `responded 500` lines for aborted reads in the stack's log |

## Verification

```
dotnet build -warnaserror
dotnet test --filter "FullyQualifiedName~V2UsersTests|FullyQualifiedName~V2AuditEventsTests|FullyQualifiedName~OpenApi|FullyQualifiedName~V1ContractPin"
dotnet test
docker build -t qbautopost-api:dev .
# in D:\qb_post_react
docker compose -f docker-compose.integration.yml up -d --build
npx playwright test -c playwright.integration.config.ts
```

## Milestones

- [x] T-1300 tracker
- [x] T-1301 rowVersion on user rows
- [x] T-1302 user names on user events
- [x] T-1303 Docker image and runbook note
- [x] T-1304 client abort is 499, not 500
- [x] Full `dotnet test` green, results recorded below

## Results

2026-10-29:
- `dotnet build -warnaserror`: 0 warnings, 0 errors.
- `dotnet test`: Core 1231 passed; Api 1515 passed; Data 478 passed, 1 skipped (`PerfFact`, opt-in with
  `QBAUTOPOST_PERF=1`). New tests: `V2UsersTests` +4, `V2AuditEventsTests` +3, `ClientAbortTests` 4; each new behaviour
  was seen failing first.
- Integration: the dashboard's `e2e-integration` suite, 13 passed against this API in Docker
  (`D:\qb_post_react\docker-compose.integration.yml`). After T-1304 a full run left 0 `responded 5xx` and 24
  `responded 499` lines in the API log.
- No v1 change, no migration, no new package. Built as specified; §4a was added during the work (F-3 was found in the
  live log after the first spec draft).
