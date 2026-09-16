# QbAutopost.Api — Implementation Tracker

Updated: 2026-09-15 · Owner: Braj · Executor: Claude Code
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
| M0 | Bootstrap | 5 | 0 | todo |
| M1 | API host, lifecycle, CSV dry run | 7 | 0 | todo |
| M2 | Hermes client, T1, G2 | 5 | 0 | todo |
| M3 | XLSX, PDF, T2, G1 | 5 | 0 | todo |
| M4 | Invoices T3 + matcher | 4 | 0 | todo |
| M5 | Tiers 3–4, G3 | 4 | 0 | todo |
| M6 | QuickBooks gateway, post, undo | 9 | 0 | todo |
| M7 | Rules, logging, hardening | 5 | 0 | todo |
| M8 | Deploy, shadow, go-live | 4 | 0 | todo |

## M0 — Bootstrap

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-001 | Solution + 3 src projects + 2 test projects, Directory.Build.props, .editorconfig, .gitignore | §4, plan §1 | `QbAutopost.sln`, `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitignore`, `.gitattributes`, `src/*/*.csproj`, `src/QbAutopost.Api/Program.cs`, `tests/*/*.csproj`, `tests/QbAutopost.Api.Tests/HostSmokeTests.cs` | `dotnet build -warnaserror` (0 warnings); `HostSmokeTests` | done | CA1416 suppressed only in QbAutopost.QuickBooks. `global.json` pins SDK 8.0.x (SDK 10 also installed). Test packages added: Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.5 (see Q-8) |
| T-002 | Port POC into Core (Models, Text, Rules, Fuzzy, Mapper, Gates, QbXmlBuilder/Parser, Ledger, BatchEnterSheet, CsvStatementParser, RegexSpecParser) | §7, §9, §11, §13 | | build green | todo | source: `poc/QbAutopost` |
| T-003 | Fixtures: samples/jobs/2026-08-tropicana, tests/fixtures/hermes/*.json, tests/fixtures/qbxml/*.golden.xml | §5, §9 | | files present | todo | |
| T-004 | First tests: CSV parser, fingerprint, G1 both orientations, FR-6 routing table, qbXML golden | §8 FR-3/4/6/9 | | `dotnet test` | todo | |
| T-005 | scripts/build.ps1, test.ps1, run-api.ps1; CLAUDE.md at repo root | plan §4 | | scripts run | todo | |

## M1 — API host, lifecycle, CSV dry run

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-101 | FolderReader + folder validation F1–F5, JobInput | §5 | | Core tests | todo | |
| T-102 | JobRecord, JobStore (memory + status.json), JobQueue (Channel), JobWorker | §6 | | Api tests | todo | one job at a time |
| T-103 | Pipeline orchestrator (RunAnalysisAsync / RunPostAsync) with RegexSpecParser stand-in | §8 | | Api tests | todo | |
| T-104 | Endpoints POST /jobs, GET /jobs/{id}, GET /jobs, POST /jobs/{id}/post; problem details; API key; Kestrel bind | §6 | | Api tests | todo | |
| T-105 | Writers: analysis.json, result.json, status.json, paste-ready CSVs | §10 | | golden compare | todo | |
| T-106 | Startup recovery of interrupted jobs | §6 | | Api test | todo | |
| T-107 | Api tests: happy path to ready; 400/404/409; auth; recovery | §15 | | `dotnet test` | todo | FakeHermesClient, FakeQbGateway |

## M2 — Hermes client, T1, G2

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-201 | HermesClient: OpenAI-compatible call, auth, timeout, fence strip, retry-with-error, audit copies | §9 | | Core tests (HttpMessageHandler fake) | todo | |
| T-202 | Prompt spec.md + T1 JobSpec + validation + regex fallback | §9.1, FR-2 | | Core tests | todo | |
| T-203 | Gate G2 → failed with explanation | FR-2 | | Api test | todo | |
| T-204 | GET /health/hermes | FR-16 | | Api test | todo | |
| T-205 | Tests: fixtures, retry, fallback, G2 negatives | §15 | | `dotnet test` | todo | |

## M3 — XLSX, PDF, T2, G1

| ID | Task | Spec | Files | Verified by | Status | Notes |
|---|---|---|---|---|---|---|
| T-301 | XlsxStatementParser (ClosedXML → layout logic) | FR-3 | | Core tests | todo | |
| T-302 | PdfText (PdfPig), scanned detection, Ocr wrapper behind Ocr.Enabled | FR-3 | | Core tests | todo | Tesseract optional |
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
| T-605 | G5 verify + ledger (atomic) + result.json + posted/partial | FR-12, §13 | | Api test | todo | |
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
| T-704 | GET /jobs?status= | §6 | | Api test | todo | |
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
| | | | | |

## Decisions

| Date | Decision | Why |
|---|---|---|
| 2026-09-15 | Core is cross-platform; COM isolated in QbAutopost.QuickBooks | tests run anywhere; server only for M6/M8 |
| 2026-09-15 | Invoices are evidence only (Q1 assumed) | avoids a second posting pipeline in v1 |
| 2026-09-15 | Deposits post straight to income (Q2 assumed) | pending answer on open customer invoices |

## Blockers

| Date | Task | Blocker | Owner | Resolved |
|---|---|---|---|---|
| | | | | |

## Session log

| Date | Session | Tasks touched | Result |
|---|---|---|---|
| | | | |
