# QbAutopost.Api — Implementation Plan

Version 1.0 · 2026-09-15 · For execution with Claude Code
Reads: `spec.md` (the contract) · Updates: `tracker.md` (progress) · Rules: `CLAUDE.md`

## 0. How to work this plan with Claude Code

1. Start every session with: read `CLAUDE.md`, then `tracker.md` (find the next `todo` task in the lowest unfinished milestone), then the `spec.md` sections that task cites.
2. One task at a time. For each task: write or extend tests first where the task is Core logic → implement → `dotnet build` and `dotnet test` green → update the task row in `tracker.md` (status, files touched, how it was verified) → commit with message `T-xxx: <summary>`.
3. Never invent behaviour the spec does not state. If the spec is silent or contradictory, add a row to `tracker.md › Questions`, choose the most conservative behaviour (hold, don't post), note it in the code with `// SPEC-GAP T-xxx`, and continue.
4. Tasks tagged **[server]** can only be verified on the QuickBooks server by a person; Claude Code implements them against the fakes and leaves the verification checkbox for the human.
5. Milestones are sequential; tasks inside a milestone are ordered but may be parallelised where files don't overlap.

Suggested Claude Code prompts:

- Kick-off: `Read CLAUDE.md, docs/spec.md and docs/tracker.md. Start with the first todo task in M0. Work one task at a time, keep tests green, update the tracker after each task, commit per task.`
- Resume: `Read docs/tracker.md and continue from the first task that is not done. Re-read the spec sections that task cites before coding.`
- Review: `Compare docs/spec.md §8 against the implementation; list any FR not covered by a test, then add the missing tests.`

## 1. Solution layout

```
qb-autopost/
├── CLAUDE.md
├── QbAutopost.sln
├── docs/                          spec.md · spec-api-v1.md (M9) · plan.md · tracker.md · runbook.md (M8)
├── src/
│   ├── QbAutopost.Core/           net8.0, cross-platform, NO COM. Models, Extract, Mapping, Gates, QbXml, Store, Output, Hermes client + prompts.
│   ├── QbAutopost.QuickBooks/     net8.0, Windows-only at runtime. QbSession (COM), QbGateway : IQbGateway.
│   └── QbAutopost.Api/            net8.0 web. Endpoints, JobQueue, JobWorker, JobStore, config, logging, DI.
├── tests/
│   ├── QbAutopost.Core.Tests/     xUnit + fixtures
│   └── QbAutopost.Api.Tests/      xUnit + WebApplicationFactory + fakes
│   └── fixtures/                  jobs/, hermes/, qbxml/ (golden files)
├── samples/jobs/2026-08-tropicana/   requirement.txt + statements/ (from the POC samples) + invoices/
├── deploy/                        hermes/docker-compose.yml · hermes/.env.example · start-all.ps1 · install-task.ps1
└── poc/                           the earlier single-console POC, kept read-only as the porting source
```

Key interfaces (Core), so everything is testable without the server:

```csharp
public interface IHermesClient { Task<T> CompleteJsonAsync<T>(HermesTask task, string userContent, CancellationToken ct) where T : IValidatable; }
public interface IQbGateway    { Task<string> ProcessAsync(string qbxml, CancellationToken ct); Task<string> CurrentCompanyFileAsync(CancellationToken ct); }
public interface IJobStore     { JobRecord? Get(string id); void Save(JobRecord r); IEnumerable<JobRecord> All(); }
public interface IClock        { DateTime UtcNow { get; } DateOnly Today { get; } }
```

NuGet (only these): `PdfPig`, `ClosedXML`, `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Tesseract` (optional, M3, behind `Ocr.Enabled`), `Microsoft.AspNetCore.Mvc.Testing` (tests).
M9 asks for one more, `Scalar.AspNetCore`, for the reference UI only — **approved by the owner on 2026-09-23 (Q-46)** and referenced in `src/QbAutopost.Api/QbAutopost.Api.csproj` at version 2.13.13, and it buys nothing else: the OpenAPI document is hand-authored and drift-tested. `Swashbuckle.AspNetCore` is **excluded by the owner** (2026-09-22), as a UI and as a generator. Everything else M9 needs (rate limiting, TLS, CORS, static files) is in the ASP.NET Core 8 shared framework.

## 2. Milestones

Effort is in Claude Code sessions (≈ 1–2 h of agent work each) plus human verification time.

### M0 — Bootstrap (1 session)
Goal: solution builds, tests run on any OS, POC logic ported into Core.
- T-001 Create solution, three `src` projects, two test projects, `Directory.Build.props` (nullable, implicit usings, warnings as errors except CA1416 in QuickBooks project), `.editorconfig`, `.gitignore`.
- T-002 Port POC files into `QbAutopost.Core`: `Models.cs` (+ new enums/fields from spec §7), `Text`, `Mapping/Rules.cs` (+ new keys §11), `Mapping/Fuzzy.cs`, `Mapping/Mapper.cs` (unchanged logic for now), `Gates/Gates.cs`, `QuickBooks/QbXml.cs` → `QbXml/QbXmlBuilder.cs` + `QbXmlParser.cs`, `Store/Ledger.cs` (+ `jobs[]` shape §13, atomic save), `Output/BatchEnterSheet.cs`, `Extract/StatementParser.cs` → `Extract/CsvStatementParser.cs`, `Extract/EmailSpecParser.cs` → `Extract/RegexSpecParser.cs`.
- T-003 Fixtures: copy `poc/samples/inbox/2026-09-01-august-batch` into `samples/jobs/2026-08-tropicana` in the folder-contract shape (`requirement.txt`, `statements/`), add `invoices/` with one text-based PDF or a `.txt` stand-in, add `tests/fixtures/hermes/*.json` canned answers (T1–T4) and `tests/fixtures/qbxml/*.golden.xml`.
- T-004 First tests: CSV parser on both sample statements, fingerprint stability, G1 both orientations, mapper routing table (7 rows of spec FR-6), qbXML golden files. All green.
- T-005 `scripts/build.ps1`, `scripts/test.ps1`, `scripts/run-api.ps1`; `CLAUDE.md` committed (from `docs/`).
Done when: `dotnet test` green on Linux and Windows; tracker M0 rows ☑.

### M1 — API host, job lifecycle, dry run on CSV (2 sessions)
Goal: `POST /jobs` on the sample folder → `ready`, with `request.qbxml`, sheets, `analysis.json`, `result.json` written, using the regex spec parser (no Hermes yet).
- T-101 `FolderReader`: folder contract validation (F1–F5), `JobInput` model, `output/` creation.
- T-102 `JobRecord`, `JobStore` (in-memory + `output/status.json`), `JobQueue` (`Channel<JobRequest>`), `JobWorker : BackgroundService` (one at a time, correlation id, exception → `failed`).
- T-103 `Pipeline` orchestrator in Core (`RunAnalysisAsync`, `RunPostAsync`) with injected `IHermesClient`/`IQbGateway`; M1 wires `RegexSpecParser` as T1 stand-in.
- T-104 Endpoints: `POST /jobs`, `GET /jobs/{id}`, `GET /jobs`, `POST /jobs/{id}/post` (returns 409 outside `ready`); problem-details errors; API-key middleware; Kestrel bind from config.
- T-105 Output writers: `analysis.json`, `result.json`, `status.json` transitions, paste-ready CSVs.
- T-106 Startup recovery (§6): scan known job folders, fix interrupted states.
- T-107 Api tests: happy path to `ready`, 400/404/409 cases, API key, recovery. Uses `FakeHermesClient` (unused yet) and `FakeQbGateway`.
Done when: the sample job reaches `ready` via HTTP in tests and by hand; `GET /jobs/{id}` matches spec `JobView`.

### M2 — Hermes client + T1 + G2 (1 session)
- T-201 `HermesClient` (OpenAI-compatible; auth; timeout; fence-stripping; retry-with-validation-error; audit copies to `output/hermes/`).
- T-202 Prompt files `Hermes/prompts/spec.md`, T1 `JobSpec` deserialisation + validation; fallback to `RegexSpecParser` after two failures (`spec.json.source`).
- T-203 G2 gate (FR-2) and `failed` transition with `spec.json` explanation.
- T-204 `GET /health/hermes`.
- T-205 Tests: `FakeHermesClient` returns fixtures; malformed-then-valid retry; fallback path; G2 negative cases.
Done when: sample job's `spec.json` matches the expected fixture; health endpoint returns 503 with the fake told to fail.

### M3 — XLSX, PDF text, T2 rows, G1 extended (2 sessions)
- T-301 `XlsxStatementParser` (ClosedXML → string grid → CSV layout logic).
- T-302 `PdfText` (PdfPig per page; scanned detection; `Ocr` wrapper behind `Ocr.Enabled`).
- T-303 `StatementLlmExtractor` (T2): prompt, chunking > 60 k chars, merge, validation (§9.2).
- T-304 G1 extended (opening/closing/count/period) + `rows.json` writer with `reconcile` block.
- T-305 Tests: fixture PDF-text → expected rows; chunk merge; every G1 failure mode; XLSX layout detection.
Done when: a fixture statement text goes through T2 (fake) and reconciles; a corrupted fixture is held.

### M4 — Invoices: T3 + matcher (1 session)
- T-401 `InvoiceExtractor` (T3) with validation; images → `Ocr` when enabled, else `unreadable`.
- T-402 `InvoiceMatcher` (FR-5 rules incl. ambiguity), `invoices/<file>.json` writer, `result.json.unmatchedInvoices`.
- T-403 `MappedTxn.InvoiceRef` + payee supply from invoice.
- T-404 Tests: unique match, ambiguous → none, direction preference, unmatched reported.

### M5 — Tiers 3–4, G3 (1 session)
- T-501 `AccountChooser` (T4): candidate account list from `QbLists`, prompt, validation `account ∈ list`, `alternatives` → `candidates`.
- T-502 `Mapper` tiers per FR-6 (Rule → History → Invoice → Model), `Tier`/`Confidence` recorded; G3 policy per FR-7.
- T-503 `analysis.json` decision/candidates fields complete.
- T-504 Tests: one test per tier, threshold edge cases, list-violation rejection, G3 hold/post matrix.

### M6 — QuickBooks gateway: lists, duplicates, posting, verify, ledger, undo (2 sessions + server time)
- T-601 `QbAutopost.QuickBooks`: `QbSession` (from POC, late-bound COM), `QbGateway : IQbGateway` with busy timeout (FR-11), `[SupportedOSPlatform("windows")]`; DI picks `FakeQbGateway` when `QuickBooks:Fake=true` or not Windows.
- T-602 `POST /qb/sync-lists` + `missingInRules` (FR-15).
- T-603 G4 live duplicate query (FR-8) with query audit files.
- T-604 Posting: retry once on `COMException`, backup-age guard (FR-11), `posting` transition, `response.qbxml`.
- T-605 G5 verify + ledger record (atomic) + `result.json` + `posted|partial` (FR-12).
- T-606 `POST /batches/{id}/undo` (FR-13).
- T-607 `GET /health/quickbooks`.
- T-608 Api tests with `FakeQbGateway`: full post, one refused line → partial, hang → `quickbooks-busy`, undo, sync-lists.
- T-609 **[server]** Manual verification on a **copy** of the company file: health, sync-lists, dry run, post, undo. Record results in tracker.

### M7 — Rules, logging, hardening (1 session)
- T-701 `POST /rules/alias`, `POST /rules/account` (validate against `qb-lists.json` when present), rules hot-reload.
- T-702 Serilog console + rolling file, correlation id per job, secret scrubbing.
- T-703 Backup-age guard config + `failed (backup-too-old)`.
- T-704 `GET /jobs` list with status filter.
- T-705 `docs/runbook.md`: install, configure, first-run certificate dialog, daily operation, exceptions, undo, troubleshooting (dialogs, bitness, busy).

### M8 — Deploy (1 session + server time)
- T-801 `deploy/hermes/docker-compose.yml`, `.env.example` (API_SERVER_KEY, provider key), `deploy/README-hermes.md` with the WSL 2 + Docker Engine steps and the Windows 10/11 Docker Desktop alternative; `.wslconfig` memory cap note.
- T-802 `deploy/start-all.ps1` (WSL/Docker up → compose up → wait for `/health/hermes` → start API) and `deploy/install-task.ps1` (Task Scheduler at logon).
- T-803 **[server]** Shadow week: five real folders in dry run; diff against manual entry; log disagreements as rules.
- T-804 **[server]** Go-live: `DryRunDefault=false` for one company; monitor a week; then the rest.

### M9 — API v1 conversion (4 sessions + server time)
Reads `docs/spec-api-v1.md` (the contract for this milestone; `spec.md` §6 still describes the legacy flat routes until T-914).
Goal: the same engine behind a versioned, documented, authenticated API a remote caller can use — plus the two defects found on the server in session 13.

- T-901 `/api/v1` route group; legacy flat routes kept behind `Api:LegacyRoutes` (default true, warned once per route per hour); content root → `AppContext.BaseDirectory` so `appsettings.json` is read from the exe folder (server defect 1).
- T-902 `GET /api/v1/health` (liveness, version, bitness, gateway mode, worker) and `/health/ready` (settings, rules, lists, log folder, worker). No QuickBooks or Hermes call.
- T-903 `IQbSdkProbe` + `QbSdkProbe` (COM, `QbAutopost.QuickBooks` only) + `GET /api/v1/health/sdk`: registration, server path, bitness match, supported qbXML versions. Never opens a company file. **[server]** for the COM half.
- T-904 `POST /api/v1/quickbooks/connection/test` with per-step timings; "waiting for QuickBooks" log line; `ConnectionTestTimeoutSeconds` (180) so the first call of the day survives the certificate dialog (server defect 2).
- T-905 `POST /api/v1/quickbooks/company-file/validate`: exists, readable, open in QuickBooks, name matches `Company:Name`, backup freshness, lists synced.
- T-906 Direct-post request model in Core: rows → mapped transactions, fingerprint/RequestId per §7, control total, date window, amount cap, reuse of the FR-6 mapper. Tests first.
- T-907 `POST /api/v1/quickbooks/transactions/validate` — the offline dry run (no QuickBooks call; names from `qb-lists.json`, duplicates from the ledger).
- T-908 `POST /api/v1/quickbooks/transactions` + batch persistence under `Paths:ApiBatches` + `GET /api/v1/batches/{id}`; synchronous under `SyncPostTimeoutSeconds`, else 202 + poll.
- T-909 Idempotency store and middleware (`Idempotency-Key`, body hash, replay, in-progress).
- T-910 `clients.json`: per-caller keys (hash + salt only), scopes, expiry, rotation, CIDR; `scripts/new-api-client.ps1`; the legacy shared key as an implicit all-scopes client.
- T-911 TLS (refuse a non-loopback bind without it), HSTS/CORS/headers, rate limiting (framework, no package), body and field limits, path allow-lists.
- T-912 Audit trail `audit-yyyyMMdd.jsonl` (400 days) + `requestId`/`clientId` on every log line.
- T-913 Hand-authored `wwwroot/openapi.json` + drift test against `EndpointDataSource`; then the **Scalar** reference UI at `/api/v1/reference` (pending Q-46). **No Swagger/Swashbuckle** — owner decision 2026-09-22.
- T-914 `GET /api/v1/quickbooks/lists`, `POST /api/v1/jobs/validate`; runbook, POC package, `steps.md`, `qb-server-check.ps1`, `start-all.ps1` and `spec.md` §6 moved to v1 paths.

Order: T-901 first (everything lands in the new group); T-906 before T-907/T-908; T-910 before T-911.
Done when: both suites green twice in a row on Linux and Windows; the direct path and the folder path emit byte-identical qbXML for the same transaction; every v1 route has a contract test and an auth-matrix row.

## 3. Dependencies and order

```
M0 → M1 → M2 → M3 → M4 → M5 → M6 → M7 → M8 → M9
             └──── M3/M4/M5 can run on a dev box with the fake gateway; M6/M8 need the server
                                                     M9 needs the server for T-903 only
```
M2 before M3 because T2 uses the client; M5 after M4 because Tier 3 consumes invoice evidence.

## 4. Verification commands

```
dotnet build -warnaserror
dotnet test --logger "console;verbosity=minimal"
dotnet run --project src/QbAutopost.Api            # then:
curl -H "X-Api-Key: dev" -X POST http://127.0.0.1:5080/jobs -d '{"folder":"<abs path to samples/jobs/2026-08-tropicana>","dryRun":true}' -H "Content-Type: application/json"
curl -H "X-Api-Key: dev" http://127.0.0.1:5080/jobs/2026-08-tropicana
```

From M9 (v1 paths; the flat ones above keep working while `Api:LegacyRoutes` is true):

```
curl http://127.0.0.1:5080/api/v1/health                                   # no key needed
curl -H "Authorization: Bearer dev" http://127.0.0.1:5080/api/v1/health/sdk
curl -H "Authorization: Bearer dev" -X POST http://127.0.0.1:5080/api/v1/quickbooks/connection/test -d '{}' -H "Content-Type: application/json"
curl -H "Authorization: Bearer dev" -X POST http://127.0.0.1:5080/api/v1/quickbooks/transactions/validate -d @rows.json -H "Content-Type: application/json"
```

## 5. Risks and mitigations

| Risk | Mitigation |
|---|---|
| COM enum constants wrong (`localQBD`, `DoNotCare`) | T-609 verifies on the server first thing; constants isolated in `QbSession`. |
| Bitness mismatch | `PlatformTarget` documented in csproj; `/health/quickbooks` error text names it. |
| Hermes returns prose instead of JSON | Fence stripping + retry-with-error + fallback (T1) / hold (T2–T4). |
| Statement text too long for the model | Chunking (T-303) + balance check on the merged rows. |
| Double posting after a crash mid-post | `posting` interrupted → `partial`; G4 live query before any re-post. |
| Docker Desktop unsupported on Windows Server | WSL 2 + Docker Engine path documented (T-801). |
| Model chooses a non-existent account | T4 validation rejects any name not in `QbLists`. |
| **M9**: a remote caller can now reach a service that posts money | Per-caller keys and scopes (T-910); TLS required off-loopback; rate limits; path allow-lists; every mutating call audited (T-911/T-912). |
| **M9**: a caller retries and posts twice | `Idempotency-Key` replays the stored result (T-909); without one, G4 fingerprints hold the repeat rather than posting it. |
| **M9**: a caller's JSON has no statement, so G1 cannot check the arithmetic | A required `controlTotal` must equal the sum of rows to the cent; a mismatch refuses the whole batch. Code checks, never repairs (Q-49). |
| **M9**: the hand-authored `openapi.json` drifts from the routes | A drift test walks `EndpointDataSource` and fails the build when a route or an operation is undocumented (T-913). |

## 6. Definition of done (whole POC)

- All FRs in `spec.md §8` have at least one automated test; `dotnet test` green on Linux and Windows.
- A real folder posts to a copy of the company file and is undone cleanly (T-609).
- Shadow week shows zero mapping disagreements for two consecutive runs (T-803).
- `runbook.md` lets someone other than the author operate it.
