# Session Handoff — QbAutopost

Written: 2026-09-16 (session 2) · M0 and M1 done · Next step: **M2** (after the owner checks in on M1 and the tracker Questions)

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-20) and apply any
that change behaviour first. Then continue with the first todo task in M2 (T-201).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2 commits (T-101…T-107) are **local only** — push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 235 passing (Core 181, Api 54); 8 consecutive green runs on Windows. **Not yet run on Linux.** |
| Milestones | M0 done, M1 done; M2–M8 todo |
| Manual check | Development host: sample job → `ready` (8 post / 2 held / 1 skipped, total 4942.64); post → `partial` "nothing posted" (no gateway until M6) |

## 4. What M1 added (code map delta)

```
src/QbAutopost.Core/
  Abstractions/  IClock (+SystemClock), IQbGateway (+QuickBooksUnavailableException), IHermesClient (+HermesTask, IValidatable)
  Jobs/          FolderReader (F1/F2/F4/F5), JobInput, JobStatus (+JobStatusRules), JobRecord (MoveTo), IJobStore
  Pipeline/      JobPipeline (RunAnalysisAsync, BeginPosting, RunPostAsync, PostAsync), PipelineOptions,
                 ISpecReader (+RegexSpecReader, SpecSources), AnalysisResult / StatementSummary / PostOutcome
  Gates/         SpecGate (G2), PostVerifier (G5)
  Output/        JobOutputWriter (spec/rows/analysis/request+sheets/response/result), OutputDocuments
  Store/         AtomicFile (shared read + retry), QbListsStore
src/QbAutopost.Api/
  Program.cs     DI, options (+path resolution), problem details, API-key middleware, recovery, routes
  Configuration/ AppSettings (spec §12 + Paths.JobIndex)
  Jobs/          JobStore (status.json + jobs.json index), JobQueue, JobWorker, JobRunner, JobAdmission, StartupRecovery
  Endpoints/     JobEndpoints (POST/GET /jobs, GET /jobs/{id}, POST /jobs/{id}/post), JobView, JobSummary
  Security/      ApiKeyMiddleware        QuickBooks/ UnconfiguredQbGateway (TODO T-601)
tests/QbAutopost.Api.Tests/TestSupport/  ApiFactory, FakeQbGateway, FakeHermesClient, TempDir, Fixtures, FixedClock
tests/fixtures/output/sample-analysis.golden.json   (reviewed by hand)
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-11 (from M0) and Q-12…Q-20 (from M1) all carry conservative choices. The most important: Q-1 (real CSV columns), Q-5 (sheet columns), Q-11 (last-four in requirement), Q-15 (company name must match `Company.Name`), Q-18 (what a failed QuickBooks call does).
2. **G2 already exists** (`SpecGate`, T-103). M2's T-203 only has to wire it to the Hermes T1 path and add the explanation in `spec.json` for Hermes output. `ISpecReader` is the seam: add a Hermes reader with regex fallback (`SpecSources.RegexFallback`) and register it in `Program.cs` in place of `RegexSpecReader`.
3. **Hermes T1 fixture** `tests/fixtures/hermes/spec.json` uses kinds `Check`, `CreditCard`, `Deposit` (spec §9.1). `CreditCard` must map to `CcCharge` + `CcCredit` — `TxnKind` has no `CreditCard` member.
4. **Posting path exists already** (G5 + ledger, T-103). M6 adds the COM gateway, live G4 query, retry-once, busy timeout, backup guard and undo. `UnconfiguredQbGateway` throws `QuickBooksUnavailableException` → job `partial`, nothing recorded.
5. **JSON enums are camelCase** (`"ready"`, `"ccCharge"`) and read case-insensitively (tracker Decisions).
6. **Windows file sharing:** always read shared JSON through `AtomicFile.ReadAllText` (a polling reader can otherwise break the atomic replace).
7. **Test factory:** `ApiFactory.WithSetting(key, value)` overrides one setting; the host starts on first client, so files can be seeded into `factory.Dir` before that (see `RecoveryTests`).
8. **GateGuard hook** blocks the first write of every new file path and asks for facts; retrying succeeds. `ECC_GATEGUARD=off` avoids it (owner's choice).
9. **Console logging** does not print the `jobId` scope yet; Serilog with the correlation id is T-702.
10. **Sample output** goes to `samples/jobs/2026-08-tropicana/output/` and dev data to `src/QbAutopost.Api/data/`; both are git-ignored.

## 6. Next milestone — M2 (Hermes client, T1, G2)

| Task | Notes |
|---|---|
| T-201 HermesClient | `HttpClient` + `HttpMessageHandler` fake in tests; `Authorization: Bearer`, `temperature: 0`, timeout, strip code fences, one retry with validation errors appended, then `HermesValidationException`; audit copies to `output/hermes/<task>-<n>.request/response.json` **without the key**. |
| T-202 Prompt + T1 | `Hermes/prompts/spec.md` loaded at startup; `{{placeholders}}`; T1 DTO → `JobSpec` (company placeholder → null); fallback to `RegexSpecParser` after two failures. |
| T-203 G2 | Already implemented — wire and test with the Hermes path. |
| T-204 `/health/hermes` | No API key needed (middleware already exempts `/health`); 503 when not ok. |
| T-205 Tests | `FakeHermesClient` exists; add malformed-then-valid, fallback, G2 negatives via Hermes. |

Open follow-ups: run `dotnet test` on Linux (a CI workflow would do); decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy; push session-2 commits.
