# Session Handoff — QbAutopost

Written: 2026-09-17 (session 8) · M0–M5 done · M6 done except T-609 (ready-for-human, server) · Next step: **M7** (rules, logging, hardening), starting with **T-701**

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-39) and apply any
that change behaviour first. Then continue with the first todo task in M7 (T-701).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–8 commits (T-101…T-609) are **local only**. Push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 806 passing (Core 673, Api 133) on Windows, 3 consecutive green runs. **Not yet run on Linux.** |
| Milestones | M0–M5 done; M6 done except **T-609 = ready-for-human** (needs the QuickBooks server); M7–M8 todo |
| Packages | none added in M6 |
| Manual check | `scripts/qb-server-check.ps1` was run against a local host with `QuickBooks:Fake=true` (health, sync, undo 404, post guard). The COM path has **never run**: there is no QuickBooks on the dev box |

## 4. What M6 added (code map delta)

```
src/QbAutopost.QuickBooks/
  QbSession.cs      late-bound QBXMLRP2 (OpenConnection2 "",AppName,1 → BeginSession file,2 → ProcessRequest → EndSession/
                    CloseConnection); open failures → QuickBooksUnavailableException, ProcessRequest COM error →
                    QuickBooksCallException; [SupportedOSPlatform("windows")]
  QbGateway.cs      IQbGateway: one session per call on its own STA thread (QbGatewayOptions AppName, CompanyFile)
src/QbAutopost.Core/
  Abstractions/IQbGateway.cs      + QuickBooksBusyException, QuickBooksCallException(ErrorCode)
  Gateway/ResilientQbGateway.cs   one call at a time (SemaphoreSlim), busy timeout (abandoned call keeps the lock),
                                  one retry after RetryDelay only when repeating cannot double-post (Q-34, Q-37)
  Gates/LiveDuplicateGate.cs      G4 vs QuickBooks while posting; writes output/query-<n>.qbxml + .response.qbxml
  Gates/BackupGuard.cs            FR-11 backup age → "backup-too-old: …"
  QbXml/QbMessageSet.cs           shared writer for query/delete message sets (same layout as QbXmlBuilder)
  QbXml/QbListQuery.cs            Account/Vendor/Customer query (ActiveOnly) + parser → QbLists
  QbXml/QbTxnQuery.cs             Check/CreditCardCharge/CreditCardCredit/Deposit query + parser → ExistingTxn
  QbXml/QbTxnDelete.cs            TxnDelRq message set + parser (requestID = 1-based position)
  QbXml/QbHostQuery.cs            HostQueryRq + parser
  QbXml/QbStatus.cs               statusCode/statusMessage pair, QbStatusException (QuickBooks refused a read)
  QbXml/QbXmlRequests.cs          IsReadOnly(qbxml): every request is *QueryRq
  Pipeline/QbListSync.cs          FR-15 (writes qb-lists.json; audit in qb-audit/ next to it)
  Pipeline/BatchUndo.cs           FR-13 (ledger entries/batch undone)
  Pipeline/QbHealth.cs            FR-16
  Pipeline/JobPipeline.cs         PostAsync: backup guard → G4 (rewrites analysis.json/request.qbxml/sheets) → add → G5
  Pipeline/PipelineOptions.cs     + DuplicateWindowDays, BackupFolder, BackupMaxAgeHours
  Jobs/JobStatus.cs               + posting → failed (backup guard only)
  Mapping/Rules.cs                + AccountNames()
  Models/HoldReasons.cs           + quickbooks-busy, backup-too-old, duplicate-check-failed
  Output/OutputDocuments.cs       SkippedItem.Note (TxnID for already-in-quickbooks)
src/QbAutopost.Api/
  QuickBooks/QbConnection.cs      DI switch: Sdk (Windows) / Simulated (QuickBooks:Fake, Development|Testing only) /
                                  Unavailable; Program wraps QbConnection.Gateway in ResilientQbGateway
  QuickBooks/SimulatedQuickBooks.cs  in-memory company answering adds, G4 queries, lists, HostQuery, TxnDel
  QuickBooks/SimulatedQbGateway.cs   QuickBooks:Fake gateway (TxnIDs SIM-n)
  Endpoints/QuickBooksEndpoints.cs   POST /qb/sync-lists; QuickBooksProblem(ex) → 503 (unavailable/busy/COM) or 502
  Endpoints/BatchEndpoints.cs        POST /batches/{id}/undo (id has '#', send %23); job → undone
  Endpoints/HealthEndpoints.cs       + GET /health/quickbooks
  Jobs/JobQueue.cs, JobWorker.cs     JobAction.Exclusive + queue.RunExclusiveAsync(label, work, ct): undo and sync run
                                     on the single worker; errors go back to the caller, never change a job's status
  Configuration/AppSettings.cs       QuickBooks:Fake, QuickBooks:RetryDelaySeconds; BackupFolder resolved to absolute
scripts/qb-server-check.ps1          T-609 helper (ASCII only: Windows PowerShell 5.1 misreads UTF-8 scripts)
tests/  Core: Gateway/ResilientQbGatewayTests, Gates/{LiveDuplicateGate,BackupGuard}Tests, QbXml/{QbListQuery,QbTxnQuery,
        QbTxnDelete,QbXmlRequests}Tests, Pipeline/{QbListSync,BatchUndo,QbHealth}Tests
        Api: {QbConnection,SyncListsApi,DuplicateCheckApi,PostingSafetyApi,PostResultApi,UndoApi,QuickBooksHealthApi,
        QuickBooksFlowApi}Tests; JobWorkerTests (+exclusive work)
        fixtures/qbxml: list-query/txn-query-check/txn-query-deposit/txn-del/host-query *.golden.xml + response samples
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-39 all use the conservative choices. New in M6: Q-34 (fake/DI switch, gateway lock and busy timeout), Q-35 (sync-lists details), Q-36 (G4 details), Q-37 (retry only when it cannot double-post; `posting → failed` for the backup guard), Q-38 (undo details), Q-39 (health details). Q-27 (T2/T3/T4 run again at post time) is still open and matters before go-live.
2. **Behaviour changes in M6:**
   - Posting now sends one read-only query message set per (kind, account) **before** the add. Tests that counted `Gateway.Requests` now count `Gateway.Writes`. `Gateway.Queries` gives the read-only ones.
   - `FakeQbGateway` is backed by `SimulatedQuickBooks` (`Company` property). `Throw`, `Hang`, `RejectLine` and `FailWritesTimes` apply to write message sets; `QueryThrow` and `QueryHang` apply to read-only ones; `CompanyFileThrow` applies to `CurrentCompanyFileAsync`. Seed data with `Company.Accounts/Vendors/Customers` or `Company.AddExisting(...)`.
   - `ApiFactory` replaces `QbConnection`, not `IQbGateway`. The host always wraps the fake in `ResilientQbGateway`, and `QuickBooks:RetryDelaySeconds` is 0.01 in tests. `BusyTimeoutSeconds` is an int, so hang tests use `WithSetting("QuickBooks:BusyTimeoutSeconds", "1")`.
   - A stale backup makes the job `failed` (`backup-too-old: …`) from `posting`, with nothing sent and no ledger record.
   - A failed G4 check (gateway error) → `partial` "nothing posted: duplicate check (G4) failed", with no ledger record.
3. **M7 hints:**
   - **T-701** `POST /rules/alias`, `/rules/account`. `samples/rules.json` has `//` comments, which `JsonOptions` accepts on read, but a plain re-serialise drops them. Decide whether to keep comments (e.g. edit through `JsonNode` and accept the loss, or keep a comment-free file), and record a Q-row. Rules are already loaded fresh for each job (`Rules.Load` in `RunAnalysisAsync`), so "hot reload" is mostly done. Serialise writes with the worker (`queue.RunExclusiveAsync`) or a lock, and write atomically (`AtomicFile`). Validate `account` against `qb-lists.json` when lists exist (`QbListsStore`). `kind` is `vendor|customer` → `PayeeAliases` / `CustomerAliases`.
   - **T-702** Serilog: `Serilog.AspNetCore` and `Serilog.Sinks.File` are on the allowed list, but no Serilog package is referenced yet. The `JobWorker` already opens a `jobId` scope, but console output does not show scopes. Scrub `X-Api-Key`, `Authorization` and `Hermes:ApiKey`. `Paths.Logs` already exists in settings.
   - **T-703** The backup-age guard and `failed (backup-too-old)` were done in T-604 (`BackupGuard`, `PostingSafetyApiTests`). Confirm the config (`QuickBooks:BackupFolder`, `BackupMaxAgeHours`, relative path resolution) and close it.
   - **T-704** `GET /jobs?status=` was done in T-104 (`JobsApiTests.Should_ListJobsAndFilterByStatus_When_Asked`). Confirm it (including `undone`) and close it.
   - **T-705** `docs/runbook.md`: use `scripts/qb-server-check.ps1`, the T-609 checklist, the hold/skip codes in `HoldReasons.cs`, and the Q-rows for operator-facing behaviour (undo 3120 failures, "nothing posted" vs "run duplicates before re-post").
4. **Test helpers:** `ScriptedHermes` (Core) validates like the real client. `TestStatementReader`, `TestInvoiceExtractor` and `TestAccountChooser` build the pipeline's readers. In `JobPipelineTests`, `_accountAnswer`, `_invoiceAnswer` and `_rulesFile` (via `UseRules`) script a run. `PostResultApiTests.UseRulesThatResolveEverything()` makes all 10 sample lines postable (the job ends `posted`). `QuickBooksFlowApiTests.SimulatedHostFactory` shows how to host without the test gateway.
5. **Still true from earlier sessions:**
   - JSON enums are camelCase.
   - Read shared JSON with `AtomicFile.ReadAllText`.
   - `ApiFactory.WithSetting(key, value)` is not chainable. Use `WithWebHostBuilder` when you need several keys.
   - Console logs lack the `jobId` scope until T-702.
   - Sample output and `src/QbAutopost.Api/data/` are git-ignored.
   - `.gitattributes` marks binary files.
   - A golden mismatch in `AnalysisGoldenTests` writes `sample-analysis.actual.json` next to the test assembly.
6. **Running by hand:** `dotnet run` needs `ASPNETCORE_ENVIRONMENT=Development` (or a real `QBAUTOPOST__Api__ApiKey`). Add `QBAUTOPOST__QuickBooks__Fake=true` to simulate QuickBooks locally. A job still fails at T1 without a running Hermes (Q-22).
7. **Tooling:**
   - The GateGuard hook may block the first edit or creation of a file; retrying works.
   - Windows PowerShell 5.1 reads `.ps1` files as ANSI. Keep scripts ASCII only, and edit `docs/tracker.md` with the Edit tool, not a PowerShell script.

## 6. Next milestone: M7 (rules, logging, hardening)

See `docs/plan.md` T-701…T-705 and these spec sections: FR-14 (rules teaching), §14 (logging, secrets), FR-11 (backup guard), §6 (`GET /jobs`).

Open follow-ups:
- **T-609 on the server** (human): run the checklist in `docs/tracker.md` and record the bitness. It also confirms the COM constants, `DepositQuery` with `AccountFilter`, and real `*AddRs` / `TxnDelRs` answers.
- Run `dotnet test` on Linux (a CI workflow would cover this).
- Decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
- Push sessions 2–8.
- Get the owner's answer to Q-27 (re-running T2/T3/T4 at post time) before posting goes live.
