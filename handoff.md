# Session Handoff — QbAutopost

Written: 2026-09-22 (session 14) · **M9 (API v1) started: 5 of 14 tasks done, on branch `m9-api-v1`, not merged**
· Both session-13 server defects are now closed in code · Next session: **T-907 and T-908 — the JSON transactions
validate + post endpoints**, the biggest remaining pieces.

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/spec-api-v1.md (the M9 contract) and docs/tracker.md (M9 rows T-901…T-914,
questions Q-46…Q-57). You are on branch m9-api-v1 with 14 commits; main is untouched.
Continue with T-907 (POST /api/v1/quickbooks/transactions/validate), then T-908 (the post itself).
Work test-first: a RED you actually ran and pasted, then the fix, then the full suite twice, then the tracker row,
session-log line, docs/testing/T-90x.tdd.md and a commit per task. Read handoff.md §7 before writing any test —
it lists the traps this codebase has already sprung.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app that turns a job folder (`requirement.txt` + bank/card statements + optional
invoices) into QuickBooks Desktop transactions: Checks, credit-card charges and credits, Deposits. Deterministic code
does all money and mapping; Hermes (a local OpenAI-compatible model) only reads PDFs/invoices and suggests accounts,
and can be switched off entirely (`Hermes:Enabled=false`). The contract is `docs/spec.md`; **M9's contract is
`docs/spec-api-v1.md`**, which wins for `/api/v1/*`. Milestones `docs/plan.md`, progress `docs/tracker.md`, operator
guide `docs/runbook.md`, agent rules `CLAUDE.md`, per-task test evidence `docs/testing/T-90x.tdd.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch **`m9-api-v1`** (14 commits), `main` last at `33b96b8`. **Nothing pushed, nothing merged.** |
| Build | `dotnet build -warnaserror` → 0 warnings |
| Tests | **Core 757, Api 292** (1049), green twice in a row on Windows. Linux not re-run this session (CI covers it) |
| Milestones | M0–M7 done; M8 agent work done (T-802/T-803/T-804 `ready-for-human`); **M9 5/14 done** |
| Packages | none added this session. Q-46 (Scalar) still unanswered |
| Owner answers | Q-1…Q-45 still unanswered except Q-0; **Q-46…Q-57 new this session** |

## 4. What M9 is, and the four owner decisions behind it

The owner asked to "convert this to an API project". It already was one, so M9 is a restructure plus five new
capabilities. Decisions taken 2026-09-22 (recorded in `docs/tracker.md › Decisions`):

- **D-1** Keep the job-folder pipeline **and** add a direct JSON-rows post. One engine, two entrances.
- **D-2** Two validation endpoints: the QuickBooks **company file**, and the incoming **transaction data**.
- **D-3** `/api/v1` prefix + an OpenAPI document. Reference UI is **Scalar — the owner explicitly ruled out Swagger**.
- **D-4** **Remote callers**: TLS, per-caller keys with scopes, rate limits, a 400-day audit trail.

## 5. What was built in session 14 (all test-first, all committed)

| Task | State | What it does |
|---|---|---|
| T-901 | done | `/api/v1` route group; flat paths kept behind `Api:LegacyRoutes` (default true) and warned about once per route per hour; content root → `AppContext.BaseDirectory` (**server defect 1 closed**) |
| T-902 | done | `GET /api/v1/health` (liveness: version, bitness, gateway mode, worker) and `/health/ready` (settings, rules, qb-lists, log folder, worker). Zero QuickBooks/Hermes calls, asserted |
| T-903 | **ready-for-human** | `GET /api/v1/health/sdk` + the COM probe: registration, bitness match, running QuickBooks. **Never calls `BeginSession`**, so it answers with QuickBooks closed. The COM half has no automated test — checklist in the tracker |
| T-904 | done | `POST /api/v1/quickbooks/connection/test`: three timed steps, a slow step stays `ok:true` with a dialog hint, 180 s default timeout, and a "Waiting for QuickBooks" log line (**server defect 2 closed**) |
| T-905 | done | `POST /api/v1/quickbooks/company-file/validate`: 7 checks. A different company open is an **error**; a stale backup is a **warning** reusing FR-11's `BackupGuard` |
| T-906 | done | `Core/Api/DirectRequestReader`: JSON rows → the same `StatementLine`s a statement produces. Control total refuses the batch; over-cap/out-of-window/missing-account holds the row. Equivalence test pins same fingerprint + byte-identical qbXML from both paths |

Both defects from the owner's server (§5 of the previous handoff) are closed **in code**; neither is confirmed on the
server yet.

## 6. Next: T-907 and T-908 (the big ones)

Everything before this was plumbing. These two carry money.

**T-907 — `POST /api/v1/quickbooks/transactions/validate` (FR-A-6).** The offline dry run: take the same body as
T-908, run `DirectRequestReader` → the FR-6 mapper → G3 → G4 (duplicates, against `ledger.json`) → build qbXML, and
report what *would* post. **It must not contact QuickBooks at all** — names come from `qb-lists.json`, duplicates from
the ledger — so a caller can validate before the QuickBooks server is even up. Returns 200 when clean, **422 with the
same body** otherwise. Follow the `qbXml` count-only rule: the body is returned only with `?includeQbXml=true` and the
`qb:debug` scope (which does not exist until T-910 — gate it behind the scope check or leave a `// SPEC-GAP T-907`).

**T-908 — `POST /api/v1/quickbooks/transactions` (FR-A-8, FR-A-10, FR-A-11).** The post. Runs through
`JobQueue.RunExclusiveAsync` so it never overlaps a job; persists the batch under `Paths:ApiBatches/<batchId>/`
(`request.json`, `status.json`, `request.qbxml`, `response.qbxml`, `result.json`) **before the first qbXML leaves the
process** (rule 7); synchronous under `Api:SyncPostTimeoutSeconds` (120), else `202` + `GET /api/v1/batches/{id}`.
Batch id is `api-<reference>#1`. Undo (FR-13) must work on these batches too.

Watch for: the batch id feeds the ledger and undo, which today assume a job id per folder. Read `JobStore` and
`Ledger` before inventing a parallel store.

Then T-909 (idempotency), T-910 (`clients.json`, scopes), T-911 (TLS, rate limits, input hardening), T-912 (audit),
T-913 (OpenAPI + Scalar, pending Q-46), T-914 (docs/POC moved to v1 paths).

## 7. Traps this codebase has already sprung — read before writing tests

1. **A GateGuard hook blocks the first Bash command and the first edit/create of every file** until you restate
   facts (callers, affected API, data shapes, the user's verbatim instruction). It is not a failure; answer and retry.
2. **Test namespaces shadow production ones.** `QbAutopost.Api.Tests.Endpoints` broke four existing files, because
   inside `…Tests.Api` the name `Endpoints.JobView` then resolved to the test namespace. Use a name the production
   tree does not have (`…Tests.Routing`, `…Tests.HealthChecks`).
3. **`ApiFactory` must substitute every external double.** It now replaces `QbConnection`, `IHermesClient` **and
   `IQbSdkProbe`** — without the last one the host picks the real COM probe on a Windows dev box and the tests talk to
   the actual SDK (rule 1 violation).
4. **The test host inherits the shipped `appsettings.json`.** `Company:FilePath` and friends are *set* in tests unless
   you override them. A test that assumes "nothing configured" is wrong.
5. **`JsonNamingPolicy.CamelCase` lowercases only the first character.** `QuickBooksGateway` → `quickBooksGateway`,
   not `quickbooksGateway`. Pin wire names with `[property: JsonPropertyName(...)]`.
6. **`ChannelReader.Count` throws `NotSupportedException`** on a `SingleReader` unbounded channel. `JobQueue.Depth`
   is an `Interlocked` counter for that reason.
7. **`CoreIsolationTests` scans Core sources for COM tokens**, including the literal `QBXMLRP2`. A ProgId string in
   Core fails the build. Carry such values as data from `QbAutopost.QuickBooks`.
8. **Windows-only types cannot be referenced from tests** (`CA1416` as an error): `QbSession.ProgId` is
   `[SupportedOSPlatform("windows")]`, so tests use a literal.
9. `PipelineOptions` has **required** members; `QuickBooksCallException` takes `(message, errorCode)`.
10. PowerShell 5.1 only (no `pwsh`), no `python` on this box, scripts must be ASCII.

## 8. Open questions (owner) — `docs/tracker.md › Questions`

New this session, each with a conservative behaviour already implemented so nothing is blocked:

| # | In one line |
|---|---|
| Q-46 | May we add `Scalar.AspNetCore`? (Swagger is ruled out; the OpenAPI document itself needs no package) |
| Q-47 | Remote callers from where — LAN, VPN or the internet? Decides the TLS/proxy design |
| Q-48 | Who issues and revokes caller keys? |
| Q-49 | Is `controlTotal` an acceptable substitute for G1 on the direct path, and should a mismatch refuse the whole batch? (currently yes) |
| Q-50 | May a request name its own company file? (currently no) |
| Q-51 | May a direct post use Hermes for account choice? (currently no: needs a flag **and** a scope) |
| Q-52 | How long must batch evidence be kept? (400 days audit) |
| Q-53 | When may `Api:LegacyRoutes` default to false? |
| Q-54 | Does the folder model stay for good? |
| Q-55 | FR-A-3 wants supported qbXML versions, but the SDK only gives them from a session ticket, which the same rule forbids |
| Q-56 | FR-A-4's per-SDK-phase timings need a wider gateway interface — worth it? (the spec was amended to the 3 measurable steps) |
| Q-57 | FR-A-5's `companyName` check needs a `CompanyQueryRq` whose element names could not be verified — not implemented rather than guessed |

Q-1…Q-45 from earlier milestones are **still unanswered**; Q-27 and Q-37 matter before any real posting.

## 9. Waiting on the server (a person, not an agent)

- **T-903 checklist** (8 items, in the tracker): `/api/v1/health/sdk` with QuickBooks closed and open, bitness `x64`,
  no company file opened, two calls in a row.
- **T-904/T-905 confirmations** on the same visit: that the 180 s timeout really outlasts the certificate dialog, that
  the "Waiting for QuickBooks" line appears when a job holds the gateway, and that the SDK's spelling of the open
  company file path matches the configured one (a UNC vs mapped-drive difference would read as "a different file").
- Still open from M8: T-802 dry run, T-803 shadow week, T-804 go-live, and finishing the POC post/undo.

## 10. Housekeeping

- `main` is untouched; `m9-api-v1` is **not pushed**. Decide whether to push/merge or keep iterating.
- Every M9 task has a `docs/testing/T-90x.tdd.md` recording the RED output, the test specification and — importantly —
  **what is not verified**. Keep that section honest; it is the only record of which claims are proven.
- `docs/spec-api-v1.md` FR-A-4 was **amended** during T-904 (six illustrative steps → three measurable ones). If the
  spec and the code disagree again, change the spec deliberately and raise a question; do not quietly diverge.
