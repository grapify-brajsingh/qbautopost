# Plan — M17 Multiple QuickBooks company files

Contract: `docs/multi-company-m17.md`. Tracker: `docs/tracker.md` › M17 (added by T-1700). One task at a time, tests
first for Core logic, commits `T-17xx: <summary>`. Branch: `feature/9-addition-of-company-file`.
Step-by-step implementation plan: `docs/multi-company-m17-implementation-plan.md`.
Dashboard side: `D:\qbautopost\qb_post_react\docs\superpowers\plans\2026-11-03-multi-company-plan.md` (R-18…R-20).

## Global constraints

- `/api/v1/**` and the v1 OpenAPI document byte-identical (`V1ContractPinTests`); v1 routes use the default company.
- No new NuGet package, hosted service or timer (the single `JobWorker` runs the scheduled sweep). One new table
  (`companies`) and new nullable columns, via one EF migration — approved by this spec (record in Decisions).
- COM stays in `src/QbAutopost.QuickBooks`. No test contacts real QuickBooks or OpenRouter.
- One `ResilientQbGateway` and one lock for all companies.
- Hold, don't guess: anything unresolved posts less and gets a `// SPEC-GAP T-17xx` plus a Questions row.

## Files

| File | Change | Task |
|---|---|---|
| `docs/tracker.md` | M17 section, Q-133…, Q-44/Q-50 marked superseded, Decisions row | T-1700 |
| `docs/adr/0013-multiple-company-files.md` | new ADR | T-1700 |
| `src/QbAutopost.Core/Companies/CompanyNameMatcher.cs` (+ `CompanyMatch.cs`) | new, pure | T-1701 |
| `src/QbAutopost.Core/Companies/CompanyFolderScanner.cs`, `ICompanyFolderSource.cs`, `ScanResult.cs` | new | T-1702 |
| `src/QbAutopost.Core/Companies/Company.cs`, `ICompanyStore.cs`, `CompanyContext.cs`, `ICompanyResolver.cs` | new | T-1703 |
| `src/QbAutopost.Data/Entities/CompanyEntity.cs`, `Configurations/CompanyConfiguration.cs` | new | T-1703 |
| `src/QbAutopost.Data/Entities/{SavedRequest,StatementExtraction,QbListSync}Entity.cs` + configurations | `CompanyId` (+ `CompanySource`, match jsonb) | T-1703 |
| `src/QbAutopost.Data/EfCompanyStore.cs`, `Migrations/<ts>_MultiCompany.cs` | new | T-1703 |
| `src/QbAutopost.Api/Configuration/AppSettings.cs` (+ `CompaniesSettings`, validator), `appsettings.json` | `Companies` section | T-1704 |
| `src/QbAutopost.Api/Companies/CompanyBootstrap.cs`, `CompanyResolver.cs`, `CompanyDataFolder.cs` | new | T-1704 |
| `src/QbAutopost.Api/Endpoints/V2/CompanyEndpoints.cs`, `ApiRoutes.cs` | companies routes + scopes | T-1705, T-1710 |
| `src/QbAutopost.Core/Abstractions/IQbGateway.cs`, `Gateway/ResilientQbGateway.cs`, `src/QbAutopost.QuickBooks/QbGateway.cs`, `QbSession.cs`, `Api/QuickBooks/{LoggingQbGateway,SimulatedQbGateway,SimulatedQuickBooks,QbConnection,UnconfiguredQbGateway}.cs`, `tests/**/FakeQbGateway.cs` | per-call company file; wrong-company exception | T-1706 |
| `docs/testing/T-1707.server-checklist.md` | new | T-1707 |
| `Pipeline/PipelineOptions.cs`, `Jobs/DirectPostRunner.cs`, `Pipeline/QbListSync.cs`, `Pipeline/CompanyFileValidator.cs`, `Mapping/RulesEditor.cs` use, `SavedRequests/SavedRequestPoster.cs`, `Program.cs` | `CompanyContext` | T-1708 |
| `QuickBooks/DbQbListsSource.cs`, `Data/EfQbListsStore.cs`, `Abstractions/IQbListsStore.cs`, `Endpoints/V2/QuickBooksListsV2Endpoints.cs`, `Jobs/JobWorker.cs`, `Jobs/ListSyncSchedule.cs` | per-company lists and sweep | T-1709 |
| `Extraction/extraction-prompt.json`, `Extraction/StatementExtractionService.cs`, `Endpoints/V2/StatementExtractionEndpoints.cs`, prompt SHA test | company fields + match | T-1711 |
| `SavedRequests/SavedRequestIntake.cs`, `Endpoints/SavedRequestEndpoints*.cs`, `Data/EfSavedRequestStore*.cs`, `Core/SavedRequests/DuplicateDetector.cs` (callers), allowed-actions builder | company on statements, hold, set-company | T-1712 |
| dashboard summary + audit endpoints | `company` filter | T-1713 |
| `Health/Readiness.cs`, `Logging/StartupLog.cs`, `docs/runbook.md` | multi-company readiness and runbook | T-1714 |
| `docs/testing/T-1715.server-checklist.md` | new | T-1715 |

## Tasks

| ID | Task | Depends on | Expected outcome |
|---|---|---|---|
| T-1700 | Tracker M17 section, Questions from spec §6, ADR-0013, Q-44/Q-50 superseded | — | Docs only; tracker shows M17 todo rows |
| T-1701 | `CompanyNameMatcher`: normalise + exact match → `matched/none/ambiguous` | T-1700 | Unit tests incl. both sample header texts (A-4) |
| T-1702 | `CompanyFolderScanner` over an `ICompanyFolderSource` (real FS impl + temp-folder tests) | T-1700 | One-level scan, code/displayName parse, ambiguous, no-company-file, missing (A-1…A-3 at Core level) |
| T-1703 | Data: `companies` table, `company_id` columns, `MultiCompany` migration, `EfCompanyStore` (upsert-by-folder, row version) | T-1702 | Migration applies on Testcontainers; `deploy/migrations.sql` regenerated |
| T-1704 | `Companies` settings + validator; `CompanyBootstrap` (default company, rules/ledger copy, one-time backfill); `CompanyResolver`; `CompanyDataFolder` (copies `RulesTemplate` on create) | T-1703 | Upgrade test: old rows → default company (A-9); empty `RootFolder` = today's behaviour |
| T-1705 | `GET /companies`, `POST /companies/scan`, `PATCH /companies/{id}`; scope map; audit `companies-scanned`, `company-updated`; problem `company-root-unavailable` | T-1704 | API tests A-1…A-3; unmapped-route test passes |
| T-1706 | Gateway: target company file per call through `LoggingQbGateway` → `ResilientQbGateway` (one lock); `SimulatedQbGateway` per file; `FakeQbGateway` can simulate "other file open"; map SDK error → `QuickBooksWrongCompanyException` (HRESULT constant marked `// SPEC-GAP T-1707` until verified) | T-1704 | Existing gateway tests green; new test: wrong file → exception, nothing processed |
| T-1707 **[server]** | Verify on the server: HRESULT/message of `BeginSession(B)` while A is open; background open with QuickBooks closed; open time for the largest file; whether `GetCurrentCompanyFileName` can report the open file | T-1706 | Checklist filled; constant confirmed or corrected; `ready-for-human` |
| T-1708 | `CompanyContext` through `DirectPostRunner`, `QbListSync`, `CompanyFileValidator`, `SavedRequestPoster`, v2 rules use; v1 paths resolve the default company | T-1706 | Post uses the statement company's file, rules and ledger; 409 `company-file-open-elsewhere`, attempt `unavailable`, no retry (A-7) |
| T-1709 | Lists per company: store/source keyed by company; `company` param on lists, sync, syncs (default when absent); scheduled sweep per `ScheduledSync` with per-company skip rows | T-1708 | A-8; sweep test with one company "open elsewhere" → that one skipped, others synced |
| T-1710 | `POST /companies/{id}/check`: via `JobQueue`, open session, `CompanyQueryRq`, store access + QuickBooks names; audit `company-checked` | T-1708 | Fake-gateway tests for approved / wrong-file / unavailable |
| T-1711 | Prompt `2026-11-03.api-2` with `accountHolderName`, `dbaName`; service runs matcher, stores match on extraction, returns `company` | T-1701, T-1705 | Fake Hermes tests: matched / none / ambiguous; SHA pin test updated |
| T-1712 | Statements: `companyId` on upload save (n8n save body unchanged), `company` + `companySource` in views, `company=` filter, `needsCompany` stat, `set-company` action + `POST /saved-requests/{id}/company`, 409 `company-required` on validate/post, duplicate scoping | T-1709, T-1711 | A-5, A-6; duplicate tests for same-company vs cross-company |
| T-1713 | `company` filter on `/dashboard/summary` and `/audit-events`; `companyId` on audit events | T-1712 | Filter tests; no-parameter answers unchanged |
| T-1714 | Readiness and start-up log report root folder, company count, unapproved count; runbook section "Adding a company file" (certificate approval steps from spec §3) | T-1705 | Readiness test; runbook reviewed |
| T-1715 **[server]** | End-to-end on the server with two approved companies (A-11) | all | Checklist filled; `ready-for-human` |

## Tests

- Core: `CompanyNameMatcherTests` (normalisation table, both samples, alias, DBA, ambiguous, missing/disabled
  excluded), `CompanyFolderScannerTests` (temp folders: one/zero/two `.qbw`, `.tlg`/`.nd`/`.qbb` ignored, nested
  folders ignored, rename, disappear/reappear).
- Data: migration + backfill on Testcontainers; `EfCompanyStore` upsert and row-version conflict; lists store per
  company.
- Api: companies routes (scopes, problems, audit), extraction company block, statement hold and `set-company`,
  wrong-company post, scheduled sweep, v1 pin tests unchanged.

## Verification

```
dotnet build -warnaserror
dotnet test
dotnet ef migrations script --idempotent -p src/QbAutopost.Data -s src/QbAutopost.Api -o deploy/migrations.sql
```
Server tasks T-1707 and T-1715 are verified by a person with the checklists.

## Milestones

- [x] Spec and plan written (2026-11-03)
- [x] Spec and plan approved by the owner (2026-11-04)
- [ ] T-1700 tracker, ADR-0013
- [ ] T-1701…T-1703 matcher, scanner, data
- [ ] T-1704…T-1705 bootstrap and companies API
- [ ] T-1706…T-1707 gateway per company (+ server check)
- [ ] T-1708…T-1710 company context, lists, access check
- [ ] T-1711…T-1713 extraction match, statements, filters
- [ ] T-1714 readiness and runbook
- [ ] T-1715 server end-to-end

## Results

(filled in after implementation: actual build/test output, deviations, deferred items)
