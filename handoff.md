# Session Handoff — QbAutopost

Written: 2026-09-23 (session 15) · **M9 (API v1): 10 of 14 tasks done, on branch `m9-api-v1`, not pushed, not
merged** · This session delivered the four that carry the money and the callers: **T-907, T-908, T-909, T-910**.
Next session: **T-911 (TLS, rate limits, input hardening)** then **T-912 (audit trail)**. T-913 is **blocked on
Q-46** — it needs a NuGet package the owner has not approved.

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/spec-api-v1.md (the M9 contract) and docs/tracker.md (M9 rows T-901…T-914,
questions Q-46…Q-61). You are on branch m9-api-v1; main is untouched.
Continue with T-911 (FR-A-14 TLS, FR-A-15 rate limits, FR-A-17 input hardening), then T-912 (FR-A-16 audit).
T-913 is blocked on Q-46 (the Scalar package) — do not add a package without an answer.
Work test-first: a RED you actually ran and pasted, then the fix, then the full suite twice, then the tracker row,
session-log line, docs/testing/T-91x.tdd.md and a commit per task. Read handoff.md §7 before writing any test —
it lists the traps this codebase has already sprung.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app that turns a job folder (`requirement.txt` + bank/card statements + optional
invoices) **or a JSON request** into QuickBooks Desktop transactions: Checks, credit-card charges and credits,
Deposits. Deterministic code does all money and mapping; Hermes (a local OpenAI-compatible model) only reads
PDFs/invoices and suggests accounts, and can be switched off entirely (`Hermes:Enabled=false`). The contract is
`docs/spec.md`; **M9's contract is `docs/spec-api-v1.md`**, which wins for `/api/v1/*`. Milestones `docs/plan.md`,
progress `docs/tracker.md`, operator guide `docs/runbook.md`, agent rules `CLAUDE.md`, per-task test evidence
`docs/testing/T-9xx.tdd.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch **`m9-api-v1`** (20 commits), `main` last at `33b96b8`. **Nothing pushed, nothing merged.** |
| Build | `dotnet build -warnaserror` → 0 warnings |
| Tests | **Core 820, Api 344** (1164), green twice in a row on Windows. Linux not re-run this session (CI covers it) |
| Milestones | M0–M7 done; M8 agent work done (T-802/T-803/T-804 `ready-for-human`); **M9 10/14** |
| Packages | none added. **Q-46 (Scalar) still unanswered, and it blocks T-913** |
| Owner answers | Q-1…Q-45 still unanswered except Q-0; **Q-46…Q-61 outstanding**, Q-61 needs telling before the next deploy |

## 4. What session 15 built

| Task | State | What it does |
|---|---|---|
| T-907 | done | `POST /api/v1/quickbooks/transactions/validate` — the offline dry run. `DirectPlanner` runs the rows through the same FR-6 mapper, duplicate checks and qbXML builder as a folder job and stops before the SDK. Zero QuickBooks/Hermes calls, asserted. 400 for a control-total mismatch, **422 with the same body** for a gate verdict |
| T-908 | done | `POST /api/v1/quickbooks/transactions` + `GET /api/v1/batches/{id}`. Evidence folder per batch; `request.json` written **before** the first qbXML leaves the process, proven by a test where QuickBooks throws. **No parallel store** — the ledger still records what posted, so `BatchUndo` needed no change and FR-13 works on a direct batch. 202 + poll past `Api:SyncPostTimeoutSeconds` |
| T-909 | done | `Idempotency-Key` (FR-A-12). Same request twice under one key → one batch, one TxnID, **one write to QuickBooks**. Different body → 409; still running → 409 + `Retry-After: 5`. Covers undo; deliberately not validate |
| T-910 | done | `clients.json`, per-caller keys, scopes, rotation, CIDR, `scripts/new-api-client.ps1`. Only salted hashes stored. Fail-closed startup. Closed T-907's `qb:debug` gap |

## 5. Read this before T-911 — two things this session changed under you

1. **Health routes are no longer all open.** api-v1 §2.2 narrows spec.md §6: only `/health` and `/health/ready` need
   no key. `/health/sdk`, `/health/quickbooks`, `/health/hermes` now require `health:read`. It moved 18 existing
   tests. **Q-61** is open, and the owner's monitoring needs telling before the next deploy.
2. **`ApiKeyMiddleware` is now identity, not a string compare.** It resolves a caller from `clients.json` (falling
   back to the shared key while `Api:AllowLegacyKey`), checks CIDR, then checks the route's scope from
   `ApiRoutes.ScopeFor`. Anything T-911 adds to the pipeline goes *after* it, so it can see the client id via
   `ApiKeyMiddleware.ClientOf(context)` — which is exactly what FR-A-15's per-client rate limits and FR-A-16's audit
   need.

## 6. Next: T-911, then T-912

**T-911 — FR-A-14 + FR-A-15 + FR-A-17.** Three separable pieces; consider three RED/GREEN cycles.
- *Transport*: Kestrel HTTPS from `Api:Tls:PfxPath` + `PfxPassword` (**environment only** — a value in
  `appsettings.json` warns and is accepted) or `StoreThumbprint`. **Startup refuses a non-loopback bind without TLS**
  unless `Api:AllowInsecureRemote=true`, which warns at startup *and* on every request. HSTS, `nosniff`,
  `Referrer-Policy: no-referrer`, no `Server` header. CORS off; `*` refused while any route needs a key.
- *Rate limits*: ASP.NET Core 8's built-in limiter (**shared framework, no package**), partitioned by client id, by
  IP for the unauthenticated health routes. `qb:post` 10/min, other authenticated 120/min, health 600/min per IP,
  failed auth 10/min per IP then 429 for 5 minutes. `429` carries `Retry-After`. Per-client overrides already exist
  on `ApiClient` (`PostPerMinute`, `DefaultPerMinute`) and are so far unused.
- *Input hardening*: body 2 MB (`Api:MaxRequestBodyBytes`), string bounds (`reference` 64, `memo` 4096, names 255),
  and **caller-supplied paths must be absolute and inside `Paths:AllowedJobRoots[]`** — the traversal fix now that
  callers are remote. Note T-909 buffers the whole body to hash it, so the 2 MB cap matters there too.

**T-912 — FR-A-16 audit.** One JSON line per authenticated mutating request to `Paths:Logs/audit-yyyyMMdd.jsonl`:
`utc, requestId, clientId, remoteIp, method, path, idempotencyKey, outcome, status, batchId, jobId, counts,
totalAmount`. Never the key, never the body, never statement text. Retention `Api:AuditRetentionDays` (400).
`requestId` (§2.6) **does not exist yet** — T-908 notes the gap; T-912 is the natural place to add it.

Then T-913 (OpenAPI + Scalar, **blocked on Q-46**) and T-914 (docs/POC moved to v1 paths).

## 7. Traps this codebase has already sprung — read before writing tests

1. **A GateGuard hook blocks the first Bash command and the first edit/create of every file** until you restate
   facts (callers, affected API, data shapes, the user's verbatim instruction). It is not a failure; answer and retry.
2. **Test namespaces shadow production ones.** `QbAutopost.Api.Tests.Endpoints` broke four files because inside
   `…Tests.Api` the name `Endpoints.JobView` resolved to the test namespace. Use a name production does not have.
3. **`ApiFactory` must substitute every external double** — `QbConnection`, `IHermesClient`, `IQbSdkProbe`.
4. **The test host inherits the shipped `appsettings.json`**, and this bit twice. T-908 added `Paths:ApiBatches`
   there and five tests promptly wrote batches into the real `C:\qb-autopost`. **Any new path setting must also be
   pinned in `ApiFactory`.** `Paths:Clients` and `Paths:ApiBatches` are pinned now; the next one will not be.
5. **`JsonNamingPolicy.CamelCase` lowercases only the first character.** Pin wire names with `JsonPropertyName`.
6. **`ChannelReader.Count` throws** on a `SingleReader` unbounded channel; `JobQueue.Depth` is an `Interlocked` counter.
7. **`CoreIsolationTests` scans Core sources for COM tokens**, including the literal `QBXMLRP2`.
8. **Windows-only types cannot be referenced from tests** (`CA1416` as an error).
9. `PipelineOptions` has **required** members; `QuickBooksCallException` takes `(message, errorCode)`.
10. PowerShell 5.1 only (no `pwsh`), no `python` on this box, scripts must be ASCII (`DeployScriptsTests` enforces it).
11. **Platform-dependent APIs fail on CI, not here.** T-908's batch-id guard used `Path.GetInvalidFileNameChars()`,
    which permits `\` and `:` on Linux, so `C:\windows\system32` would have passed. Prefer an explicit allow-list.
12. **A hanging test costs two minutes, not a failure.** T-909's in-progress test wedges the gateway; it pins a 2 s
    `Api:SyncPostTimeoutSeconds` so a regression fails fast instead of waiting out the default.
13. **`WithSetting(...)` returns `WebApplicationFactory<Program>`, not `ApiFactory`** — no `CreateAuthorizedClient()`
    on it; create the client and add the header.

## 8. Open questions (owner) — `docs/tracker.md › Questions`

Q-46…Q-57 from session 14, plus five raised this session:

| # | In one line |
|---|---|
| Q-46 | May we add `Scalar.AspNetCore`? **Blocks T-913** |
| Q-58 | The caller's payee is honoured on a numbered check, where FR-6 leaves it blank. Right? |
| Q-59 | FR-A-8 mentions posting a `ready` batch later; that route was not built (re-send with `dryRun:false` instead) |
| Q-60 | A non-2xx **releases** an idempotency key, so a corrected resend works. Right? |
| Q-61 | **Health routes now need a key** (api-v1 §2.2 over spec.md §6). The owner's monitoring must be told |

Q-1…Q-45 from earlier milestones are **still unanswered**; Q-27 and Q-37 matter before any real posting.

## 9. Waiting on the server (a person, not an agent)

- **T-903 checklist** (8 items, in the tracker) and the **T-904/T-905 confirmations**, unchanged from session 14.
- **New**: nothing in T-907…T-910 has met a real QuickBooks. The direct-post path needs its own server checklist
  before T-804 go-live — a dry run, then one real batch, then an undo of it.
- Still open from M8: T-802 dry run, T-803 shadow week, T-804 go-live, and finishing the POC post/undo.

## 10. Housekeeping

- `main` is untouched; `m9-api-v1` is **not pushed**. Twenty commits now sit only on this machine — worth deciding.
- Every M9 task has a `docs/testing/T-9xx.tdd.md` with a "what this does not prove" section. Keep it honest; it is
  the only record of which claims are actually tested.
- `docs/spec-api-v1.md` FR-A-4 was amended in session 14. §2.2 was **not** amended this session — the code follows it
  and `docs/spec.md` §6 is now the stale one. If the owner answers Q-61 the other way, the spec needs the edit, not
  a quiet code change.
