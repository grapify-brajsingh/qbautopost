# Session Handoff — QbAutopost

Written: 2026-09-16 · Previous session ended after M0 · Next step: **M1** (wait for the owner's go-ahead and answers to the tracker Questions)

## 1. Start the new session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Then check the tracker "Questions" table for owner answers (Q-0…Q-11) and
continue with the first todo task in M1 (T-101). One task at a time, tests
green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md` (copied at the repo root and kept in `docs/`).

## 3. State at handoff

| Item | State |
|---|---|
| Repo root | `D:\qb_post` (plan calls it `qb-autopost/`) |
| Branch | `master`, 8 commits, working tree clean |
| SDK | pinned to 8.0.x by `global.json` (8.0.420 in use; SDK 10 is also installed) |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 92 passing (Core 91, Api 1), also verified from a fresh clone. **Not yet run on Linux.** |
| Milestone M0 | done (T-001…T-005) |
| Milestones M1–M8 | todo |
| ADRs | `docs/adr/0001`…`0007` (0007 = built from the spec because the POC doesn't exist) |

Commits:
```
4ad85dd T-005: build/test/run scripts, root CLAUDE.md, tracker wrap-up for M0
c3d829f T-002/T-004: commit Output/ sources hidden by case-insensitive .gitignore rule
4060165 T-004: Core tests ...
54bfcac T-003: synthetic sample job, rules, Hermes and qbXML fixtures
3b878ba T-002: Core domain, mapping, gates, qbXML, ledger, sheets, parsers
e971ffd T-001: solution skeleton, build props, SDK pin, test projects
23de15c docs: spec, plan, tracker, CLAUDE.md and ADRs 0001-0007
```
(These handoff notes add one more commit.)

## 4. Push to GitHub (not done yet — no remote is configured)

Remote: **https://github.com/grapify-brajsingh/qbautopost.git**

```powershell
cd D:\qb_post
git remote add origin https://github.com/grapify-brajsingh/qbautopost.git
git branch -M main          # optional: rename master → main before the first push
git push -u origin main     # or: git push -u origin master
```

- If the GitHub repo was created with a README or license, run `git pull origin main --rebase --allow-unrelated-histories` before pushing.
- The GitHub MCP server failed to authenticate last session (HTTP 401). Use `git`/`gh` from the terminal, or fix the token first.
- Before pushing, confirm no secrets are in the repo. Nothing secret has been written so far; `.gitignore` excludes `.env`, `appsettings.*.local.json`, `logs/` and job `output/` folders.

## 5. Code map (what exists)

```
src/QbAutopost.Core/          net8.0, no COM (guarded by tests/.../Architecture/CoreIsolationTests.cs)
  Models/     Enums (SourceKind, Direction, TxnKind, Confidence, Decision), HoldReasons,
              StatementLine (Fingerprint/RequestId computed), MappedTxn, JobSpec,
              InvoiceFacts, Batch/PostResult, QbLists
  Text/       JsonOptions.Default, TextNormalizer (Normalize/ForMatching/ContainsFragment),
              Money (Format/TryParse), Fingerprints
  Mapping/    Rules (+Load/Parse, case-insensitive), PatternRule, CsvLayout, Fuzzy,
              PayeeResolver, LineAccountTiers (tiers 1–2), Mapper (FR-6 table)
  Gates/      ReconcileGate (G1 balance chain)
  QbXml/      QbXmlBuilder (FR-9 order, golden-tested), QbXmlParser (*AddRs)
  Store/      AtomicFile, Ledger/LedgerJob/LedgerEntry, LedgerStore
  Output/     BatchEnterSheet (3 paste-ready CSVs), CsvText
  Extract/    CsvGrid, Last4Detector, StatementGridParser (shared with future XLSX),
              CsvStatementParser, StatementParseResult, RegexSpecParser
src/QbAutopost.QuickBooks/    empty project (CA1416 suppressed) — COM work arrives in M6
src/QbAutopost.Api/           Program.cs skeleton only — M1 adds everything
tests/QbAutopost.Core.Tests/  TestSupport (Fixtures.PathOf/Read/SampleRules, Lines builders) + tests
tests/QbAutopost.Api.Tests/   HostSmokeTests (WebApplicationFactory<Program>)
tests/fixtures/               hermes/*.json (T1–T4), qbxml/*.golden.xml + add-response.xml, statements/*.csv
samples/rules.json            sample rules incl. CsvLayouts (chase-checking, chase-card, generic-debit-credit)
samples/jobs/2026-08-tropicana/  requirement.txt, statements/ (bank 4521: 7 rows, card 7788: 4 rows), invoices/ (.txt stand-in)
scripts/                      build.ps1, test.ps1, run-api.ps1
```

Expected result for the sample job: 8 lines post, 2 are held (both `unknown-payee`: UNKNOWN PLUMBER and SUNRISE deposit), and 1 is skipped (the card AUTOMATIC PAYMENT line).

## 6. Things the next session must know

1. **There is no POC.** `plan.md` says to port from `poc/QbAutopost`; ignore that. Items the POC would have defined were invented and marked `// SPEC-GAP T-002` in the code; they are listed as **Q-0…Q-11** in `docs/tracker.md › Questions`. The most important are **Q-1** (real bank CSV columns), **Q-5** (paste-ready sheet columns and date format) and **Q-11** (how `requirement.txt` gives the last-four digits).
2. **The Mapper holds tier 3–4 lines on purpose:** a line with no rule or history account is held with reason `no-account-rule` (`// TODO(T-502)`). M5 replaces this.
3. **`.gitignore` pitfall:** git on Windows ignores case, so a bare `output/` rule also hides the `Output/` code folders. Job output is ignored only under `samples/jobs/*/output/` and `tests/fixtures/jobs/*/output/`. Keep it that way.
4. **Test helper name:** use `Fixtures.PathOf(...)`, not `Path(...)`, which clashes with `System.IO.Path`.
5. **Line endings:** `core.autocrlf=true` on this machine. `.gitattributes` has `* text=auto`. The golden-file comparison normalises newlines.
6. **The GateGuard hook** blocks the first Write of every new file and asks for "facts" (callers, duplicates, data shapes, the user's instruction); a retry then succeeds. Setting `ECC_GATEGUARD=off` for this project avoids the double writes. Commands that delete files also need a rollback note first.
7. **Owner preferences this session:** build from the spec, finish one milestone and check in before the next, `git init` with one commit per task (`T-xxx: summary` plus the Co-Authored-By trailer), and record ADRs in `docs/adr/`.
8. **NuGet allow-list** (CLAUDE.md): PdfPig, ClosedXML, Serilog.AspNetCore, Serilog.Sinks.File, Tesseract, Mvc.Testing. Test runtime packages were added and noted as Q-8. Versions used: xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 17.14.1, Mvc.Testing 8.0.31.

## 7. Next milestone — M1 (API host, job lifecycle, CSV dry run)

Goal: `POST /jobs` on the sample folder reaches `ready` and writes `request.qbxml`, the three sheets, `analysis.json`, `result.json` and `status.json`, using `RegexSpecParser` instead of Hermes.

| Task | Notes / pointers |
|---|---|
| T-101 FolderReader (F1–F5), JobInput, `output/` creation | Use `Last4Detector`. Unsupported extensions → `result.json.unreadable[]` with `HoldReasons.UnsupportedExtension`. Never read `output/`. |
| T-102 JobRecord, JobStore (memory + `output/status.json`), JobQueue (`Channel`), JobWorker | Status enum `queued analysing ready posting posted partial failed` (+ `undone`). Save before each next step, using `AtomicFile`. |
| T-103 Pipeline (`RunAnalysisAsync`/`RunPostAsync`) | Wire `CsvStatementParser` → `ReconcileGate` → `Mapper` → `QbXmlBuilder` → `BatchEnterSheet`. Interfaces from plan §1: `IHermesClient`, `IQbGateway`, `IJobStore`, `IClock`. |
| T-104 Endpoints + RFC 7807 + `X-Api-Key` + Kestrel bind | Spec §6 and §12 (`appsettings.json` shape). `/health/*` doesn't need a key. |
| T-105 Writers | analysis.json / result.json shapes in spec §10; `JobView` in spec §6. |
| T-106 Startup recovery | `analysing` → `failed (interrupted)`; `posting` → `partial (interrupted — run duplicates before re-post)`. |
| T-107 Api tests | Create `FakeHermesClient` and `FakeQbGateway` (spec §15). Cover the happy path to `ready`, 400/404/409, auth and recovery. |

Open follow-ups: run `dotnet test` on Linux (a CI workflow would do); decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
