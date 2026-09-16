# QbAutopost.Api — Implementation Tracker

Updated: 2026-09-16 · Owner: Braj · Executor: Claude Code
Spec: `spec.md` · Plan: `plan.md`

**How to update (Claude Code does this after every task):**
1. Change the task's Status: `todo` → `doing` → `done` (or `blocked` with a note).
2. Fill *Files* (paths created/changed) and *Verified by* (test names or the command run).
3. Append one line to the Session log.
4. Never delete rows; never mark a **[server]** task `done` — leave it `ready-for-human` and the human flips it.

Status legend: `todo` · `doing` · `done` · `blocked` · `ready-for-human`

## Milestones

| M | Name | Tasks | Done | Status |
|---|---|---|---|---|
| M0 | Bootstrap | 5 | 5 | done (Windows; Linux run pending) |
| M1 | API host, lifecycle, CSV dry run | 7 | 7 | done (Windows; Linux run pending) |
| M2 | Hermes client, T1, G2 | 5 | 5 | done (Windows; Linux run pending) |
| M3 | XLSX, PDF, T2, G1 | 5 | 2 | doing |
| M4 | Invoices T3 + matcher | 4 | 0 | todo |
| M5 | Tiers 3–4, G3 | 4 | 0 | todo |
| M6 | QuickBooks gateway, post, undo | 9 | 0 | todo |
| M7 | Rules, logging, hardening | 5 | 0 | todo |
| M8 | Deploy, shadow, go-live | 4 | 0 | todo |

## M0 — Bootstrap

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-001 | Solution + 3 src projects + 2 test projects, Directory.Build.props, .editorconfig, .gitignore | §4, plan §1 | `QbAutopost.sln`, `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitignore`, `.gitattributes`, `src/*/*.csproj`, `src/QbAutopost.Api/Program.cs`, `tests/*/*.csproj`, `tests/QbAutopost.Api.Tests/HostSmokeTests.cs` | `dotnet build -warnaserror` (0 warnings); `HostSmokeTests` | done | CA1416 suppressed only in QbAutopost.QuickBooks. `global.json` pins SDK 8.0.x (SDK 10 also installed). Test packages added: Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.5 (see Q-8) |
| T-002 | Port POC into Core (Models, Text, Rules, Fuzzy, Mapper, Gates, QbXmlBuilder/Parser, Ledger, BatchEnterSheet, CsvStatementParser, RegexSpecParser) | §7, §9, §11, §13 | `src/QbAutopost.Core/{Models,Text,Mapping,Gates,QbXml,Store,Output,Extract}/*.cs` | build green; covered by T-004 tests | done | **POC unavailable → written from spec (ADR-0007).** Gaps marked `SPEC-GAP T-002`, listed in Questions Q-1…Q-9. Mapper covers tiers 1–2 only; lines needing tiers 3–4 are held `no-account-rule` until T-502. `CsvStatementParser` delegates to `StatementGridParser` (shared with T-301 XLSX) |
| T-003 | Fixtures: samples/jobs/2026-08-tropicana, tests/fixtures/hermes/*.json, tests/fixtures/qbxml/*.golden.xml | §5, §9 | `samples/jobs/2026-08-tropicana/{requirement.txt,statements/*.csv,invoices/home-depot-88213.txt}`, `samples/rules.json`, `tests/fixtures/hermes/{spec,statement,invoice,account}.json`, `tests/fixtures/qbxml/{check,creditcard,deposit}.golden.xml`, `tests/fixtures/qbxml/add-response.xml`, `tests/fixtures/statements/*.csv` | files present; used by T-004 tests | done | All data synthetic (no POC samples). Bank CSV 7 rows (newest-first, balances reconcile 13195.87 → 10230.45), card CSV 4 rows. Invoice is a `.txt` stand-in for a text PDF (plan allows). Golden requestIDs computed independently with PowerShell SHA-256, not by the code under test |
| T-004 | First tests: CSV parser, fingerprint, G1 both orientations, FR-6 routing table, qbXML golden | §8 FR-3/4/6/9 | `tests/QbAutopost.Core.Tests/{TestSupport,Text,Extract,Gates,Mapping,QbXml,Store,Output,Architecture}/*.cs`, `SampleJobTests.cs`; fix in `src/QbAutopost.Core/Text/TextNormalizer.cs` | `dotnet test` → Core 91 passed, Api 1 passed (Windows, SDK 8.0.420) | done | Covers: CSV 7/4 rows (FR-3 AC), fingerprint vs independent SHA-256, G1 file/reverse order + failure modes, all 7 FR-6 rows + holds, tier 2 history, qbXML 3 golden files, response parser, ledger atomic save, sheets, RegexSpecParser FR-2 AC, Core has no COM tokens. Tests found one bug: apostrophes in names ("Joe's") broke fuzzy matching — fixed. Tests written in the same session as T-002 rather than strictly first. Linux run still to be done (no Linux box here) |
| T-005 | scripts/build.ps1, test.ps1, run-api.ps1; CLAUDE.md at repo root | plan §4 | `scripts/{build,test,run-api}.ps1`, `CLAUDE.md` | build.ps1 → 0 errors; test.ps1 → 92 passed; run-api.ps1 → "Now listening on http://127.0.0.1:5080" | done | CLAUDE.md copied, not moved (see Decisions) |

## M1 — API host, lifecycle, CSV dry run

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-101 | FolderReader + folder validation F1–F5, JobInput | §5 | `src/QbAutopost.Core/Jobs/{FolderReader,JobInput,InvalidJobFolderException}.cs`, `HoldReasons.SubfolderIgnored`; `tests/QbAutopost.Core.Tests/Jobs/FolderReaderTests.cs`, `TestSupport/TempJobFolder.cs` | `FolderReaderTests` (22); `dotnet test` → Core 113, Api 1 | done | F1 validate, F2 unreadable, F4 top-level only / `output/` never listed, F5 file-name last-four (content fallback stays in the parsers). F3 is checked by the endpoint (T-104). Job-id charset and subfolder reporting are SPEC-GAPs (Q-12, Q-13) |
| T-102 | JobRecord, JobStore (memory + status.json), JobQueue (Channel), JobWorker | §6 | `src/QbAutopost.Core/Jobs/{JobStatus,JobRecord,IJobStore}.cs`, `Core/Abstractions/IClock.cs`, `Core/Text/JsonOptions.cs` (camelCase enums); `src/QbAutopost.Api/Configuration/AppSettings.cs`, `Api/Jobs/{JobStore,JobQueue,IJobProcessor,JobWorker}.cs`; `tests/QbAutopost.Api.Tests/{Jobs/JobStoreTests,Jobs/JobWorkerTests,TestSupport/*}.cs`, `Core.Tests/Jobs/JobRecordTests.cs`, `Core.Tests/Text/JsonOptionsTests.cs` | `JobStoreTests` (7), `JobWorkerTests` (4), `JobRecordTests`, `JsonOptionsTests`; `dotnet test` → Core 139, Api 12 | done | One job at a time (single reader, asserted). State machine enforced by `JobRecord.MoveTo`. status.json is written atomically, before memory. Worker fail-safe: if the processor crashes, the job goes to `failed` (from analysing) or `partial (interrupted …)` (from posting); cancellation at shutdown leaves the status for startup recovery. Job index file is a SPEC-GAP (Q-14). Worker DI registration is in T-104 |
| T-103 | Pipeline orchestrator (RunAnalysisAsync / RunPostAsync) with RegexSpecParser stand-in | §8 | `src/QbAutopost.Core/Pipeline/{JobPipeline,PipelineOptions,ISpecReader,AnalysisResult}.cs`, `Core/Gates/{SpecGate,PostVerifier}.cs`, `Core/Abstractions/{IQbGateway,IHermesClient}.cs`, `Core/Store/QbListsStore.cs`, `HoldReasons` (pipeline codes); `src/QbAutopost.Api/Jobs/JobRunner.cs`; tests `Core.Tests/Pipeline/JobPipelineTests.cs`, `Core.Tests/Gates/{SpecGateTests,PostVerifierTests}.cs` | `JobPipelineTests` (17), `SpecGateTests` (10), `PostVerifierTests` (7); Api `PostingApiTests` | done | Analysis = FR-1…FR-9 (CSV only; xlsx/pdf held `extractor-not-available` until M3) + G1 per statement + **G2 (logic landed here, see T-203)** + ledger G4 (`already-posted`). Post = re-analysis → one qbXML call → G5 → ledger → posted/partial. Gateway exception → all lines held, batch recorded with 0 posted (F3 then refuses a re-run: check QuickBooks first); `QuickBooksUnavailableException` (nothing sent) → no ledger record. M6 adds live G4, retry, busy timeout, backup guard and the COM gateway. SPEC-GAPs Q-15…Q-18 |
| T-104 | Endpoints POST /jobs, GET /jobs/{id}, GET /jobs, POST /jobs/{id}/post; problem details; API key; Kestrel bind | §6 | `src/QbAutopost.Api/{Program.cs,appsettings.json,appsettings.Development.json}`, `Api/Endpoints/{JobEndpoints,JobView}.cs`, `Api/Jobs/JobAdmission.cs`, `Api/Security/ApiKeyMiddleware.cs`, `Api/QuickBooks/UnconfiguredQbGateway.cs`, `Api/Configuration/AppSettings.cs` (`ResolvePaths`); `.gitignore` (`src/QbAutopost.Api/data/`) | `JobsApiTests` (19), `AuthTests` (8); manual run below | done | RFC 7807 for 400/401/404/409 (400 lists `errors[]`). Key compared via SHA-256 + fixed-time compare; empty key → host refuses to start; `change-me` refused outside Development (Q-19). Kestrel binds `Api:Bind`. Env prefix `QBAUTOPOST__`. Admission (create/post) is serialised; an active job → 409, ledger job → 409 unless `force`. `POST /jobs/{id}/post` moves the job to `posting` (with batch id) before queueing. `GET /jobs?status=` implemented here (T-704). Default gateway is `UnconfiguredQbGateway` until T-601. Manual (Windows, Development): sample → 202 → `ready` with 8/2/1 and totals 4942.64; post → `partial` "nothing posted: QuickBooks unavailable"; second post → 409; no key → 401; log has no secrets |
| T-105 | Writers: analysis.json, result.json, status.json, paste-ready CSVs | §10 | `src/QbAutopost.Core/Output/{JobOutputWriter,OutputDocuments}.cs`, `Core/Store/AtomicFile.cs` (shared read + retry); tests `Core.Tests/Output/{ResultDocumentTests,AnalysisGoldenTests}.cs`, `Core.Tests/Store/AtomicFileTests.cs`, `tests/fixtures/output/sample-analysis.golden.json` | `AnalysisGoldenTests` (golden reviewed by hand against the FR-6 table), `ResultDocumentTests` (4), `AtomicFileTests` (3) | done | Writes spec.json, statements/&lt;file&gt;.rows.json, analysis.json, request.qbxml + 3 sheets (also in dry run), response.qbxml, result.json. result.json adds `company`, `error`, `counts.posted`, `totals.posted` to the spec shape (Q-20). Found while testing: on Windows a polling `GET /jobs/{id}` and the atomic replace block each other (500 / failed rename) → readers share delete access and both sides retry with backoff (≈1.5 s) |
| T-106 | Startup recovery of interrupted jobs | §6 | `src/QbAutopost.Api/Jobs/StartupRecovery.cs`, `Program.cs` | `RecoveryTests` (6) | done | Runs after `Build()`, before the worker starts. analysing → `failed (interrupted)`; posting → `partial (interrupted — run duplicates before re-post)`; queued → `failed` (Q-14). result.json keeps the last completed run; status.json is the authority |
| T-107 | Api tests: happy path to ready; 400/404/409; auth; recovery | §15 | `tests/QbAutopost.Api.Tests/TestSupport/{ApiFactory,FakeQbGateway,FakeHermesClient,TempDir,Fixtures,FixedClock}.cs`, `Api/{JobsApiTests,PostingApiTests,AuthTests,RecoveryTests}.cs`; removed `HostSmokeTests.cs` (covered by `AuthTests`) | `dotnet test` → Core 181, Api 54, 8 consecutive green runs | done | Every data file lives in a per-test temp folder; no real QuickBooks/Hermes. `FakeQbGateway` records requests, answers with sequential `FAKE-n` TxnIDs and echoed amounts, can refuse line N, throw, or hang. `FakeHermesClient` serves `tests/fixtures/hermes/*.json` (not called until M2). Posting covered: full post → partial (2 held), ledger entries, forced re-run skips posted lines and uses batch #2, dryRun=false, refused line, unavailable vs failed gateway, double post → 409 |

## M2 — Hermes client, T1, G2

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-201 | HermesClient: OpenAI-compatible call, auth, timeout, fence strip, retry-with-error, audit copies | §9 | `src/QbAutopost.Core/Abstractions/IHermesClient.cs` (`HermesRequest`, `HermesException`, `HermesValidationException`, `HermesUnavailableException`), `src/QbAutopost.Core/Hermes/{HermesClient,HermesOptions,HermesAudit,JsonReply}.cs`, `src/QbAutopost.Api/Program.cs` (typed `HttpClient`, `HermesOptions`); `tests/QbAutopost.Core.Tests/Hermes/{HermesClientTests,JsonReplyTests}.cs`, `TestSupport/StubHttpHandler.cs`; `tests/QbAutopost.Api.Tests/TestSupport/FakeHermesClient.cs` | `HermesClientTests` (19), `JsonReplyTests` (16); `dotnet test` → Core 216, Api 54 | done | Interface now `CompleteJsonAsync<T>(HermesRequest, ct)` (spec §9 signature: task, system prompt, user content, schema hint + audit folder). Invalid answer (prose, bad JSON, empty, `Validate()` errors) → one retry with the errors appended → `HermesValidationException`. Non-2xx, timeout, connection error, malformed envelope → `HermesUnavailableException`, no retry (Q-21). Audit: `output/hermes/<task>-<n>.request/response.json`, one pair per HTTP attempt; key only in the header, and scrubbed from audit text when ≥ 8 chars. Timeout is per call (`HttpClient.Timeout` infinite). Empty key → no `Authorization` header (Q-21). Host DI wiring is exercised by `HealthApiTests` (T-204) |
| T-202 | Prompt spec.md + T1 JobSpec + validation + regex fallback | §9.1, FR-2 | `src/QbAutopost.Core/Hermes/{prompts/spec.md,PromptTemplate,PromptLibrary,SpecAnswer}.cs`, `src/QbAutopost.Core/Pipeline/HermesSpecReader.cs`, `Pipeline/ISpecReader.cs` (`SpecReadResult.Note`), `Output/{OutputDocuments,JobOutputWriter}.cs` (`spec.json.note`), `QbAutopost.Core.csproj` (prompts copied to `<app>/Hermes/prompts`), `src/QbAutopost.Api/Program.cs` (`PromptLibrary` loaded before start, `HermesSpecReader` replaces `RegexSpecReader`); tests `Core.Tests/Hermes/{SpecAnswerTests,PromptLibraryTests}.cs`, `Core.Tests/Pipeline/HermesSpecReaderTests.cs`; `Api.Tests/TestSupport/FakeHermesClient.cs` (`Respond`, validates answers), `Api.Tests/Api/JobsApiTests.cs` (3 tests script their T1 answer) | `SpecAnswerTests` (26), `PromptLibraryTests` (6), `HermesSpecReaderTests` (6, two through the real client + stub HTTP); `dotnet test` → Core 254, Api 54; prompt file present in Api and test output folders | done | `CreditCard` → `CcCharge` + `CcCredit`; kinds matched ignoring case, only `Check`/`CreditCard`/`Deposit` accepted; `kinds`, `bankLast4`, `cardLast4` must be present (may be empty); last-four = exactly 4 ASCII digits; company `Company`/blank → null. Two invalid answers → `RegexSpecParser`, `source = regex-fallback`, `note` = the validation errors. Hermes unreachable → job `failed` (Q-22). Fixture answer and regex parse agree on the sample (FR-2 AC). Unknown `{{placeholder}}` or missing `spec.md` → error at startup |
| T-203 | Gate G2 → failed with explanation | FR-2 | `tests/QbAutopost.Api.Tests/Api/SpecGateApiTests.cs`, `tests/fixtures/output/sample-spec.golden.json`; gate code unchanged (`SpecGate`, T-103), `spec.json.note` added in T-202 | `SpecGateApiTests` (9); `dotnet test` → Core 254, Api 63 | done | Through the Hermes path: sample `spec.json` equals the golden file (reviewed by hand against the FR-2 AC: source `hermes`, kinds check/ccCharge/ccCredit/deposit, bank 4521, card 7788, gate ok). G2 negatives: stated account without a statement, no kinds, statement account neither stated nor in rules.json, another company → `failed`, error starts `requirement check failed (G2)`, `spec.json.gate.errors` lists the reasons, no analysis.json / request.qbxml. Placeholder company → configured name. Invalid answer → `regex-fallback` + `note` (ready on the sample; failed when the regex finds nothing). Hermes unreachable → failed, no fallback (Q-22) |
| T-204 | GET /health/hermes | FR-16 | `src/QbAutopost.Core/Abstractions/IHermesClient.cs` (`PingAsync`, `HermesPing`), `Core/Hermes/HermesClient.cs` (`PingAsync`; shared `PostAsync` with timeout), `src/QbAutopost.Api/Endpoints/HealthEndpoints.cs`, `Program.cs`; tests `Core.Tests/Hermes/HermesClientTests.cs` (+9), `Api.Tests/Api/HealthApiTests.cs`, fakes (`FakeHermesClient.Ping`) | `HermesClientTests` ping cases, `HealthApiTests` (4, two through the host's real `HermesClient` with a stub handler); `dotnet test` → Core 263, Api 67 | done | One completion with `max_tokens: 5`, no audit copy; ok iff HTTP 200 with non-empty content. Body `{ ok, model, latencyMs, message }` — `message` (null when ok) is an addition to spec §6 so an operator sees why (e.g. `HTTP 500`, `timed out after 120 s`, `empty completion`). 503 when not ok; no API key (middleware exempts `/health`). The real-client tests also prove the DI wiring (config → `HermesOptions`, bearer key, base URL) and that the key does not appear in the 503 body |
| T-205 | Tests: fixtures, retry, fallback, G2 negatives | §15 | `tests/QbAutopost.Api.Tests/TestSupport/ApiFactory.cs` (`Hermes:BaseUrl` = `hermes.invalid`), `Api.Tests/Api/HealthApiTests.cs` (guard test); coverage landed with T-201…T-204 | `dotnet test` → Core 263, Api 68, 6 consecutive green runs; manual run below | done | Fixtures: `FakeHermesClient` serves `tests/fixtures/hermes/*.json` (and validates). Malformed-then-valid: `HermesClientTests` (prose, bad JSON, empty, invalid → retry), `HermesSpecReaderTests.Should_ReturnHermesSpec_When_RealClientSucceedsOnRetry`. Fallback: `HermesSpecReaderTests`, `SpecGateApiTests` (ready and failed variants). G2 negatives: `SpecGateTests` (Core) + `SpecGateApiTests` (4 via Hermes). M2 "done when": `spec.json` golden ✓, health 503 with the fake failing ✓. Rule 1 guard: the test host points Hermes at `.invalid`. Manual (Windows, Development, no Hermes running): `/health/hermes` → 503 "actively refused (127.0.0.1:8642)" in ~2 s; sample job → `failed` "analysis failed: Hermes Spec call failed: …" with `output/hermes/spec-1.*.json` written, no key in files or log |

## M3 — XLSX, PDF, T2, G1

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-301 | XlsxStatementParser (ClosedXML → layout logic) | FR-3 | `src/QbAutopost.Core/Extract/{XlsxGrid,XlsxStatementParser}.cs`, `Extract/StatementGridParser.cs` (`acceptIsoDates`), `Models/HoldReasons.cs` (`unreadable-statement`), `Pipeline/JobPipeline.cs` (`.xlsx` routed; only `.pdf` still `extractor-not-available`), `QbAutopost.Core.csproj` (ClosedXML 0.105.1); tests `Core.Tests/Extract/XlsxStatementParserTests.cs`, `TestSupport/XlsxBuilder.cs`, `Extract/CsvStatementParserTests.cs` (+1), `Pipeline/JobPipelineTests.cs` (+2, pdf hold); fixture `tests/fixtures/statements/chase-checking-4521.xlsx` | `XlsxStatementParserTests` (17), `JobPipelineTests.Should_PostSameLines_When_BankStatementIsAWorkbook`, `…_When_WorkbookIsCorrupt`; `dotnet test` → Core 283, Api 68 | done | First worksheet only, blank rows dropped, first non-blank row = header, same layouts as CSV. Cells read without display formats: numbers → invariant digits (double → decimal keeps 15 digits, so `0.1+0.2` → 0.3; > 2 decimals still a row error), real date cells → `yyyy-MM-dd` (time dropped), booleans/errors → text that fails parsing, formulas → the result saved in the file (never recalculated; none saved → blank → statement held). **Date decision (Q-1 kept, Q-23):** text dates must match the layout format, no invariant fallback; the XLSX parser also accepts exact `yyyy-MM-dd`. A workbook ClosedXML cannot open → `unreadable-statement`. Fixture generated by a throw-away ClosedXML program from the sample bank CSV (synthetic). ClosedXML never writes formula results, so `XlsxBuilder` adds `<v>` to the sheet XML to imitate Excel |
| T-302 | PdfText (PdfPig), scanned detection, Ocr wrapper behind Ocr.Enabled | FR-3 | `src/QbAutopost.Core/Abstractions/IOcr.cs` (`IOcr`, `DisabledOcr`), `Core/Extract/PdfText.cs` (`PdfText`, `PdfTextResult`, `PdfPageText`), `Core/Pipeline/JobPipeline.cs` (new `IOcr` constructor argument; statements read async; PDFs go through `PdfText`), `QbAutopost.Core.csproj` (PdfPig 0.1.16); `src/QbAutopost.Api/Ocr/TesseractOcr.cs`, `Api/Program.cs` (`IOcr` by `Ocr:Enabled`, resolved at startup), `Api/Configuration/AppSettings.cs` (relative `TessDataPath` resolved), `QbAutopost.Api.csproj` (Tesseract 5.2.0); tests `Core.Tests/Extract/PdfTextTests.cs`, `TestSupport/{PdfBuilder,TestImage,FakeOcr}.cs`, `Pipeline/JobPipelineTests.cs` (+3, pdf test now uses a real PDF), `Output/{AnalysisGoldenTests,ResultDocumentTests}.cs` (constructor), `Api.Tests/Api/OcrApiTests.cs` | `PdfTextTests` (12), `JobPipelineTests` PDF cases (4), `OcrApiTests` (3); `dotnet test` → Core 298, Api 71. Manual (Windows, scratch program, tessdata_fast `eng.traineddata`, not committed): real `TesseractOcr` read "08/15/2026 ACH DEBIT UNKNOWN PLUMBER SVC -3199.70" exactly, both from a PNG and from a one-image PDF through `PdfText` | done | Page text via `ContentOrderTextExtractor`; pages joined with `\n\f\n`. Scanned = fewer than 40 **non-whitespace** characters (Q-24). OCR disabled + any scanned page → whole file `scanned-pdf-ocr-disabled` (error names the pages). OCR enabled → each image on a scanned page is OCR'd (JPEG streams passed as-is, other images re-encoded to PNG by PdfPig; an image neither can give → `unreadable-statement`); OCR text is appended to the page's own text. Scanned page with no images → its short text is kept (Q-24). Damaged/encrypted/non-PDF file or OCR exception → `unreadable-statement`; cancellation propagates. Readable PDFs are still held `extractor-not-available` until T-303 (T2). `Ocr:Enabled` with a missing or empty `TessDataPath` stops the host at startup. The Tesseract package bundles Windows x86/x64 native libraries only; Linux needs system Tesseract 5 + Leptonica (OCR is off by default, spec Q3). Only `eng` is loaded |
| T-303 | StatementLlmExtractor T2: prompt, chunking, merge, validation | §9.2 | | Core tests | todo | |
| T-304 | G1 extended + rows.json writer | FR-4 | | Core tests | todo | |
| T-305 | Tests: fixture text → rows; corrupted → held; XLSX detection | §15 | | `dotnet test` | todo | |

## M4 — Invoices

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-401 | InvoiceExtractor T3 + validation; images via Ocr or unreadable | §9.3 | | Core tests | todo | |
| T-402 | InvoiceMatcher + invoices/<file>.json + unmatchedInvoices | FR-5 | | Core tests | todo | |
| T-403 | MappedTxn.InvoiceRef + payee supply | FR-5/6 | | Core tests | todo | |
| T-404 | Tests: unique/ambiguous/direction/unmatched | §15 | | `dotnet test` | todo | |

## M5 — Tiers 3–4, G3

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-501 | AccountChooser T4, list-constrained validation, candidates | §9.4 | | Core tests | todo | |
| T-502 | Mapper tiers Rule→History→Invoice→Model; G3 policy | FR-6/7 | | Core tests | todo | |
| T-503 | analysis.json complete (tier, confidence, candidates, decision) | §10 | | golden compare | todo | |
| T-504 | Tests: per-tier, thresholds, list violation, G3 matrix | §15 | | `dotnet test` | todo | |

## M6 — QuickBooks gateway

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-601 | QbSession (COM) + QbGateway with busy timeout; FakeQbGateway; DI switch | FR-11 | | build; Api tests | todo | Windows-only project |
| T-602 | POST /qb/sync-lists + missingInRules | FR-15 | | Api test | todo | |
| T-603 | G4 live duplicate query + query audit files | FR-8 | | Api test | todo | |
| T-604 | Posting: retry once, backup-age guard, posting transition, response.qbxml | FR-11 | | Api test | todo | |
| T-605 | G5 verify + ledger (atomic) + result.json + posted/partial | FR-12, §13 | | Api test | todo | Basic version exists since T-103 (`PostVerifier`, `JobPipeline.RecordInLedger`, `PostingApiTests`); review against the real SDK response in T-609 |
| T-606 | POST /batches/{id}/undo | FR-13 | | Api test | todo | |
| T-607 | GET /health/quickbooks | FR-16 | | Api test | todo | |
| T-608 | Api tests: full post, refused line → partial, hang → busy, undo, sync-lists | §15 | | `dotnet test` | todo | |
| T-609 | **[server]** Manual verification on a COPY of the company file: health → sync-lists → dry run → post → undo | §15 | | human checklist below | todo | |

T-609 checklist (human):
- [ ] `GET /health/quickbooks` → ok (certificate dialog answered "Yes, always"; user HermesBridge)
- [ ] `POST /qb/sync-lists` → counts plausible; `missingInRules` empty after fixing rules.json
- [ ] Dry run on a real folder → `ready`; `analysis.json` reviewed
- [ ] `POST /jobs/{id}/post` on the copy → `posted`; transactions visible in QuickBooks
- [ ] `POST /batches/{id}/undo` → all deleted
- [ ] Bitness confirmed (x64/x86) and recorded here: ______

## M7 — Rules, logging, hardening

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-701 | POST /rules/alias, /rules/account; validation vs qb-lists; hot reload | FR-14 | | Api test | todo | |
| T-702 | Serilog console + rolling file; correlation id; secret scrubbing | §14 | | manual + test | todo | |
| T-703 | Backup-age guard config + failed(backup-too-old) | FR-11 | | Api test | todo | |
| T-704 | GET /jobs?status= | §6 | | Api test | todo | Implemented in T-104 (`JobsApiTests.Should_ListJobsAndFilterByStatus_When_Asked`); confirm and close in M7 |
| T-705 | docs/runbook.md | plan M7 | | review | todo | |

## M8 — Deploy

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-801 | deploy/hermes docker-compose.yml, .env.example, README-hermes.md (WSL 2 + Docker Engine; Docker Desktop alt) | §4 | | compose config valid | todo | |
| T-802 | deploy/start-all.ps1, install-task.ps1 | §4 | | dry run on server | todo | |
| T-803 | **[server]** Shadow week: 5 real folders dry run, diff vs manual, rules added | plan M8 | | log below | todo | |
| T-804 | **[server]** Go-live one company, then all | plan M8 | | log below | todo | |

## Questions raised during implementation

| # | Task | Question | Conservative choice taken | Answer (human) |
|---|---|---|---|---|
| Q-0 | T-002 | The POC (`poc/QbAutopost`) is not available. Will it be supplied? | Core written from the spec (ADR-0007); every item below is a guess to confirm | Owner: build from spec (2026-09-16) |
| Q-1 | T-002 | What is the `rules.json.CsvLayouts` format, and what are the real bank/card CSV columns? | Layout = `HeaderContains[]` + column names + `DateFormat` + one signed `AmountColumn` (`PositiveIsDebit`) or `DebitColumn`/`CreditColumn`, plus optional check-number/balance/last-four columns. No layout or 2+ layouts match → statement held. A date that fails the layout format is a row error (no fallback). Sample layouts imitate Chase exports | |
| Q-2 | T-002 | What does `Normalize(description)` do? | Trim, collapse whitespace, upper-case invariant; digits and punctuation kept. Fuzzy matching also drops apostrophes and turns other punctuation into spaces | |
| Q-3 | T-002 | Fingerprint field spelling (kind, direction, missing check number)? | `bank`/`card`, `debit`/`credit`, missing check number = "" | |
| Q-4 | T-002 | G1 "both orientations" — row order, balance sign, or both? | Row order only (file order or reversed). The sign is fixed by kind: bank credits raise the balance, card charges raise it. A partly filled balance column fails G1 | |
| Q-5 | T-002 | Which columns do the paste-ready sheets have, and in what date format? | Checks: Bank, Date, Number, Payee, Account, Amount, Memo. Card: Card, Date, Number, Payee, Type, Account, Amount, Memo. Deposits: Date, Received From, From Account, Memo, Amount, Deposit To. ISO dates, UTF-8 BOM, CRLF; text cells starting with `= + - @` get a leading `'` | |
| Q-6 | T-002 | What is the base `rules.json` schema, and what are the defaults? | Keys as in `Rules.cs`; `FuzzyThreshold` 0.85, `HistoryMinCount` 3. Missing `HoldingExpenseAccount` → numbered checks held; missing `DepositIncomeAccount` → deposits held. Pattern `Match` is a plain substring, not a regex | |
| Q-7 | T-002 | What happens when a check number is over 11 characters, or several aliases / equal fuzzy scores match? | Line held (`refnumber-too-long` / `unknown-payee` with a note); never truncated or guessed. Memo over 4095 characters is truncated (no money impact) | |
| Q-8 | T-001 | CLAUDE.md's NuGet list doesn't include the xUnit runtime packages (`Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`) | Added — xUnit tests can't run without them | |
| Q-9 | T-002 | File name with several 4-digit groups (e.g. `2026-chase-4521.csv`)? | No last-four taken from the name; fall back to the content, else hold `unknown-account` | |
| Q-10 | T-002 | One unparseable row in a CSV: hold the row or the whole statement? | Whole statement held (`unparsable-rows`), because a missing row makes the statement incomplete | |
| Q-11 | T-003 | How should `requirement.txt` state real last-four values? | Assumed as 4-digit groups on the account lines ("Bank Account=… 4521"). Please share a real example | |
| Q-12 | T-101 | Which characters may a job folder name (= job id) contain? | Only letters, digits, `.`, `_`, `-`; anything else → 400. The id appears in URLs and in batch ids `<jobId>#<attempt>` | |
| Q-13 | T-101 | Subfolders inside `statements\` / `invoices\`? | Not read; each is listed in `result.json.unreadable[]` with reason `subfolder-ignored`. `.jpeg`/`.tif` invoices are `unsupported-extension` (spec lists only pdf/png/jpg) | |
| Q-14 | T-102 | Where are the "known job folders" (spec §6 `GET /jobs`, startup recovery) recorded? | New setting `Paths.JobIndex` (default `jobs.json`) maps job id → folder and is rewritten atomically when a job is first saved. A job found `queued` at startup is set `failed (interrupted)`, not re-queued, because it might be a non-dry run | |
| Q-15 | T-103 | Must the company named in `requirement.txt` match `Company.Name`? | Yes: a different name (compared ignoring case and punctuation) fails G2, because the app always posts to the configured company file. The `Company` placeholder or no name → the configured name is used. An empty `Company.Name` fails every job | |
| Q-16 | T-103 | Statement-level hold codes the spec does not name | `reconcile-failed` (G1), `extractor-not-available` (xlsx/pdf until M3), `kind-mismatch` (layout says bank but the requirement lists the last-four only as a card, or the reverse) | |
| Q-17 | T-103 | Line-level: identical lines in one job (same fingerprint → same requestID), and kinds the requirement did not ask for | Every copy of an identical line is held `duplicate-line` (never post one of them); a postable line whose kind is not in the spec's kinds is held `kind-not-requested`. Precedence: skip-pattern → duplicate-line → already-posted → kind-not-requested | |
| Q-18 | T-103 | What happens when the QuickBooks call itself fails? | Any exception → job `partial`, every sent line held `quickbooks-no-response`, and the batch is recorded in the ledger with 0 posted, so `POST /jobs` refuses a re-run until someone checks QuickBooks and uses `force`. `QuickBooksUnavailableException` (thrown only before anything is sent) → `partial` "nothing posted", no ledger record. A response line with status 0 but a different echoed amount is held `amount-mismatch`, and its note names the TxnID to delete | |
| Q-19 | T-104 | API key defaults | An empty `Api:ApiKey` stops the host at startup; the shipped `change-me` is accepted only in Development. `appsettings.Development.json` uses `dev` (plan §4 curl) and local `data/` paths | |
| Q-20 | T-105 | result.json fields beyond spec §10 | Adds `company`, `error`, `counts.posted`, `totals.posted`; held items carry `file`, `lineNo`, `kind`, `note`, `candidates`. `unmatchedInvoices` stays `[]` until M4. The sample's `invoices/home-depot-88213.txt` stand-in is reported `unsupported-extension` (invoices accept pdf/png/jpg only) | |
| Q-21 | T-201 | Should a Hermes transport failure (timeout, connection refused, HTTP 5xx, malformed envelope) be retried, and must the `Authorization` header be sent when `Hermes:ApiKey` is empty? | No retry: spec §9 names only the validation retry, and the caller already falls back (T1) or holds (T2–T4). Empty key → no header (a local Hermes may run without one). Audit copies are written for every HTTP attempt, including failed ones | |
| Q-22 | T-202 | FR-2 falls back to the regex parser when T1 fails validation twice. What if Hermes is unreachable (timeout, refused, HTTP 5xx)? | The job fails (`analysis failed: Hermes Spec call failed: …`) instead of falling back, because the regex reading was not what the operator expected and the rule is to post less when unsure. Re-submit once Hermes is back. Changing this to a fallback is one `catch` in `HermesSpecReader` | |
| Q-23 | T-301 | FR-3 says a date that fails the layout format falls back to invariant `DateOnly.TryParse`; Q-1 chose "row error, no fallback". Which wins, and how are XLSX date cells handled? A workbook that cannot be opened? | Q-1 kept (session 4, owner said go ahead with it): a text date must match the layout format, otherwise the row fails and the statement is held, because an invariant (US) parse can silently swap day and month. XLSX real date cells are read as their date (`yyyy-MM-dd`), so the XLSX parser also accepts an exact ISO date; CSV does not. A plain-number "date" is a row error. A formula with no saved result is blank → row error. A workbook ClosedXML cannot open → statement held `unreadable-statement`. Following the spec instead is one `\|\|` in `StatementGridParser.TryParseDate` | |
| Q-24 | T-302 | FR-3 "text per page < 40 characters → scanned": are spaces and line breaks counted? What if OCR is enabled but a scanned page has no images, or an image OCR cannot read? Which OCR languages? | Whitespace is not counted (a page of blanks is scanned). OCR disabled → any scanned page holds the whole file, even a near-blank last page. OCR enabled: a scanned page without images keeps its own short text (nothing to OCR; T2 and G1 still check the rows); an image that is neither JPEG nor decodable by PdfPig, or any OCR error → file held `unreadable-statement`. English (`eng`) only. `Ocr:Enabled` with missing language data stops the host at startup | |

## Decisions

| Date | Decision | Why |
|---|---|---|
| 2026-09-15 | Core is cross-platform; COM isolated in QbAutopost.QuickBooks | tests run anywhere; server only for M6/M8 |
| 2026-09-15 | Invoices are evidence only (Q1 assumed) | avoids a second posting pipeline in v1 |
| 2026-09-15 | Deposits post straight to income (Q2 assumed) | pending answer on open customer invoices |
| 2026-09-16 | Build Core from the spec; POC unavailable (ADR-0007) | owner chose to proceed without the POC; gaps listed in Questions |
| 2026-09-16 | ADRs live in `docs/adr/` (0001–0007) | architecture decisions recorded with their alternatives |
| 2026-09-16 | `global.json` pins SDK 8.0.x (`rollForward: latestFeature`) | SDK 10 is installed on the dev box and would otherwise be used |
| 2026-09-16 | JSON enums are written camelCase (`"ready"`, `"post"`, `"ccCharge"`) and read case-insensitively | spec §6/§10 show lowercase values; `rules.json` `"Kind": "Bank"` still loads; `ledger.json` `kind` becomes `"check"` (no real ledger exists yet) |
| 2026-09-16 | M1 already posts through `IQbGateway` (G5 + ledger), with an `UnconfiguredQbGateway` that sends nothing until M6 | T-103 names `RunPostAsync`; the state machine needs a real posting path to test; M6 tasks keep the COM, live-G4, retry, busy and backup work |
| 2026-09-16 | Repo root is `D:\qb_post` (plan calls it `qb-autopost/`); `CLAUDE.md` copied to the root, `docs/CLAUDE.md` kept | plan T-005; copies must be kept in sync until one is removed |

## Blockers

| Date | Task | Blocker | Owner | Resolved |
|---|---|---|---|---|
| | | | | |

## Session log

| Date | Session | Tasks touched | Result |
|---|---|---|---|
| 2026-09-16 | 1 | ADRs, T-001…T-005 | M0 done on Windows: build has 0 warnings; 92 tests pass (Core 91, Api 1); scripts run. Linux test run still open. Found and fixed a `.gitignore` rule that hid `Output/` sources |
| 2026-09-16 | 2 | T-101…T-107 | M1 done on Windows: 235 tests (Core 181, Api 54), 8 consecutive green runs; sample job reaches `ready` via HTTP in tests and by hand. No owner answers to Q-1…Q-11 yet, so the conservative choices stand. New gaps Q-12…Q-20. Found and fixed a Windows file-sharing race between polling readers and atomic replace. Linux test run still open |
| 2026-09-16 | 3 | T-201…T-205 | M2 done on Windows: 331 tests (Core 263, Api 68), 6 consecutive green runs. Hermes client (retry with errors, audit copies, ping), T1 prompt + `SpecAnswer`, `HermesSpecReader` with regex fallback, G2 via Hermes, `/health/hermes`, `spec.json` golden. No owner answers yet. New gaps Q-21 (no transport retry; empty key → no header) and Q-22 (Hermes unreachable → job fails, no fallback). **Behaviour change:** without a running Hermes the sample job now fails instead of reaching `ready`. Linux test run still open |
| 2026-09-16 | 4 | T-301, T-302 | XLSX statements parsed (ClosedXML → shared grid/layout logic) and reconciled like CSV (T-301, Core 283 / Api 68). PDF text per page with PdfPig, scanned-page detection, `IOcr` with a Tesseract engine behind `Ocr:Enabled` (T-302); 369 tests (Core 298, Api 71); real Tesseract checked by hand on Windows. New gap Q-24 (what counts as a character; scanned page without images). Still no owner answers in the tracker. Date rule decided: Q-1 kept, with ISO accepted for XLSX date cells (Q-23). New hold code `unreadable-statement`. Found: ClosedXML never saves formula results, so tests imitate Excel's saved `<v>`. Linux test run still open |
