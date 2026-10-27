# M9 complete — API v1

**Written 2026-09-23 by the last agent of the unattended relay · branch `m9-api-v1` · 14 of 14 task rows closed
(13 `done`, T-903 `ready-for-human`)**

Verified for this document, on this machine, today:

```
dotnet build -warnaserror   → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test                 → Core  866 passed, 0 failed, 0 skipped
                              Api   492 passed, 0 failed, 0 skipped   (1358 total)
```

*(Counts re-measured 2026-09-23 after T-915 and T-916, both of which landed after this document was first written.
At the time of writing they were Core 859 / Api 487 / 1346.)*


Read §4 before you deploy anything. It is the section that says what has never been run.

---

## 1. What M9 delivered, route by route

Every route below is served under `/api/v1`. The old flat paths (`/jobs`, `/health/…`, `/qb/sync-lists`, …) are
**still mapped** because `Api:LegacyRoutes` ships `true` (Q-53); each flat hit is logged at Warning once per route
per hour. Scopes are decided in one place, `Api/Endpoints/ApiRoutes.cs › ScopeFor`, by *effect* rather than by URL,
and an unmapped route falls through to `admin` — a new endpoint is closed until someone opens it.

The OpenAPI document (`src/QbAutopost.Api/wwwroot/openapi.json`) describes **23 operations** and a build-breaking
drift test keeps it equal to what is actually mapped.

### 1.1 Health and readiness

| Route | Scope | What it does | Requirement | Built by |
|---|---|---|---|---|
| `GET /api/v1/health` | **none** | Liveness from in-process state only: version, environment, process bitness, uptime, Windows session, gateway mode (`sdk\|simulated\|unavailable\|test`), Hermes enabled/disabled, worker running + queue depth. Makes **zero** QuickBooks and Hermes calls (asserted against recording fakes). | FR-A-1 | T-902 |
| `GET /api/v1/health/ready` | **none** | Cheap local checks — settings, rules, log folder, worker, `qb-lists.json`. `ok:false` only when an **error**-severity check fails; a missing `qb-lists.json` is a **warning**, because a job every rule resolves still posts. 200 when ok, 503 when not. | FR-A-2 | T-902 |
| `GET /api/v1/health/sdk` | `health:read` | Probes the QuickBooks COM request processor: ProgID registered, `OpenConnection2` succeeds, process bitness vs the running `QBW.exe`, configured qbXML version echoed. **Never calls `BeginSession`**, so it answers with QuickBooks closed and raises no certificate dialog. `bitnessMatch:false` is an error, not a warning. | FR-A-3 | T-903 — **`ready-for-human`, the COM path has never run** |
| `GET /api/v1/health/quickbooks` | `health:read` | Unchanged M6 body `{ok, companyFile, message}`. Moved onto `/api/v1`; **its auth changed** — it used to need no key. | FR-16 | moved by T-901, closed by T-910 |
| `GET /api/v1/health/hermes` | `health:read` | Unchanged M2 body `{ok, model, latencyMs, message}`. Same auth change. | FR-16 | moved by T-901, closed by T-910 |

> **The auth change on the last three is the single most disruptive thing in M9 for an existing installation.**
> See Q-61 in §2 and the first item of §3.6.

### 1.2 QuickBooks connection and validation

| Route | Scope | What it does | Requirement | Built by |
|---|---|---|---|---|
| `POST /api/v1/quickbooks/connection/test` | `qb:read` | A real SDK round trip with three **measured** timings — `waitForGateway`, `hostQuery`, `companyFile` — and an explicit reason. A step past the busy timeout is reported `ok:true` with a `slow: a QuickBooks dialog…` hint instead of `quickbooks-busy`; this is the fix for the server defect where the certificate dialog held `BeginSession` for 101 s. A wait for the gateway lock over 1 s now logs `Waiting for QuickBooks: {WaitMs} ms behind another call`. A caller-supplied `companyFile` is refused `400` before any gateway call unless `QuickBooks:AllowCompanyFileOverride` is on (Q-50). | FR-A-4 | T-904 |
| `POST /api/v1/quickbooks/company-file/validate` | `qb:read` | Seven checks: `configured`, `pathShape`, `exists`, `readable`, **`openInQuickBooks`**, `backupFreshness`, `listsSynced`. A *different* company open in QuickBooks is an **error** — it is the one misconfiguration that silently posts into the wrong books. A stale backup is a **warning** and reuses FR-11's own `BackupGuard`, so validating and posting can never disagree. Read-only by construction. 200 or 422, same body. | FR-A-5 | T-905 |
| `POST /api/v1/quickbooks/transactions/validate` | `qb:read` | The offline dry run for direct rows: read → FR-6 map → G3 → G4 (ledger half) → qbXML, **never touching QuickBooks or Hermes** (asserted: `Gateway.Requests` and `Hermes.Calls` both stay empty). `wouldPost + held + skipped = submitted`. A control-total mismatch is `400`; a gate verdict is `422` with the same body as the `200`. `?includeQbXml=true` is gated on the `qb:debug` scope. | FR-A-6 | T-906 (model) + T-907 (endpoint) |
| `POST /api/v1/jobs/validate` | `jobs:read` | Folder validation without queueing: `FolderReader.Validate` (F1–F5) + statement read + **G1**, using the very same `Core/Pipeline/StatementCheck.Reconcile` the job pipeline uses (moved out of `JobPipeline` verbatim), so the validate and the job cannot reach different verdicts. Answers `422` where `POST /jobs` answers `400` for the same folder (Q-68). Honours `Paths:AllowedJobRoots`. Does **not** create the `output/` folder. | FR-A-7 | T-914 |

### 1.3 Posting (the money routes)

| Route | Scope | What it does | Requirement | Built by |
|---|---|---|---|---|
| `POST /api/v1/quickbooks/transactions` | `qb:post` | The direct JSON-rows post (owner decision D-1: one engine, two entrances). Rows become the **same `StatementLine`** a statement produces, so the FR-6 mapper, G4, qbXML (FR-9) and G5 are shared rather than reimplemented — a golden test pins that both entrances emit byte-identical qbXML for the same movement. `SourceKind`/`Direction` are derived from `kind`, never taken from the caller, so two callers describing one movement fingerprint alike and G4 still catches the repeat. **Refuse vs hold** is deliberate: control-total mismatch or missing, no rows, too many rows, non-positive or over-precise amount, unsafe reference, over-long field → **whole batch refused, nothing read out of it**; amount over cap, date outside window, missing account → **that row held**. Evidence lands in `Paths:ApiBatches/<batchId>/` (`request.json`, `status.json`, `request.qbxml`, `response.qbxml`, `result.json`), and `request.json` is written **before** the first qbXML leaves the process. Ledger rows are written exactly as a folder job writes them — there is **no parallel store**. `Prefer: respond-async` or a timeout gives `202` and the same work runs to completion. | FR-A-8, FR-A-9, FR-A-10 | T-906 + T-908 |
| `GET /api/v1/batches/{id}` | `qb:read` | The batch result; the poll target for an async post. | FR-A-11 | T-908 |
| `POST /api/v1/batches/{id}/undo` | `qb:post` | Unchanged M6 undo (FR-13). It needed **no change at all** to work on a direct batch, because direct posts write ordinary ledger rows. Idempotent under `Idempotency-Key` (two undos, one delete). | FR-13 | moved by T-901, covered by T-909 |
| `POST /api/v1/jobs` | `jobs:write` | Unchanged folder job creation, now with the `Paths:AllowedJobRoots` check in front of it (400 when outside). Draws on the **posting** rate bucket, not the default one, because it ends in money moving. | spec §6 | moved by T-901, hardened by T-911 |
| `GET /api/v1/jobs` · `GET /api/v1/jobs/{id}` | `jobs:read` | Unchanged list (with `?status=`) and detail. | spec §6 | moved by T-901 |
| `POST /api/v1/jobs/{id}/post` | `jobs:write` | Unchanged folder post. | FR-10..FR-12 | moved by T-901 |

### 1.4 Lists and rules

| Route | Scope | What it does | Requirement | Built by |
|---|---|---|---|---|
| `POST /api/v1/quickbooks/lists/sync` | `qb:read` | The v1 spelling of `POST /qb/sync-lists`, **same handler**: one read-only message set (accounts, vendors, customers) → `qb-lists.json` + `missingInRules`. Runs as exclusive work on the single job worker. | FR-15 | T-914 |
| `POST /api/v1/qb/sync-lists` | `qb:read` | The old spelling, kept and marked `deprecated` in the document (operationId `syncListsFlat`). **Both answer.** When it goes is **Q-69**. | FR-15 | T-914 |
| `GET /api/v1/quickbooks/lists` | `qb:read` | Reads the cached `qb-lists.json` so a caller can pick valid account, vendor and customer names. **Never opens a QuickBooks session.** A missing file is `200` with empty lists and `syncedUtc: null`. No paging, no filter (**Q-70**). | api-v1 §3 | T-914 |
| `POST /api/v1/rules/alias` · `POST /api/v1/rules/account` | `rules:write` | Unchanged M7 rule teaching (atomic rewrite under a process-wide lock, validated against `qb-lists.json`). | FR-14 | moved by T-901 |

### 1.5 Documentation

| Route | Scope | What it does | Requirement | Built by |
|---|---|---|---|---|
| `GET /api/v1/openapi.json` | `health:read` | The hand-authored OpenAPI 3.0 document, 23 operations. .NET 8 has no built-in generator and Swashbuckle is excluded by the owner, so it is checked in — and kept honest by `OpenApiDriftTests`, which walks `EndpointDataSource` and **fails the build** when a mapped `/api/v1` route has no operation, an operation has no route, or an operation lacks a summary, a 2xx response schema, or an `x-required-scope` **equal to** `ApiRoutes.ScopeFor`. | FR-A-18 | T-913 |
| `GET /api/v1/reference` | `health:read` | The Scalar reference UI (owner decision D-3: Scalar, **not** Swagger). Assets are served from the package, never a CDN; telemetry and remote default fonts are off unconditionally. "Try it" is off by default. **Both routes exist only when `Api:Reference:Enabled=true`, and that ships `false`** — disabled means *not mapped*, so the answer is `404`, not `403`. | FR-A-18 | T-913 |

### 1.6 What sits in front of every route

Ordered as the middleware actually runs:

1. **`RequestIdMiddleware`** (T-912, FR-A-16 / §2.6) — the first thing in the pipeline, ahead of the exception
   handler. Every request gets a 26-character ULID `requestId`, echoed as `X-Request-Id`, pushed onto Serilog's
   `LogContext`, and added to every problem detail. An inbound `X-Request-Id` is honoured only against
   `^[A-Za-z0-9_-]{8,64}$`.
2. **Request logging** (T-805/T-912) — the log template now carries three columns: `[{jobId}] [{requestId}] [{clientId}]`.
3. **`SecurityHeadersMiddleware`** (T-911, FR-A-14) — `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
   HSTS decision, no `Server` header. In front of the key check, so a `401` carries them too.
4. **`AuditMiddleware`** (T-912, FR-A-16) — appends one JSON line per **authenticated mutating** request to
   `Paths:Logs/audit-yyyyMMdd.jsonl`, 400-day retention. It sits *in front of* the key check on purpose, so a `403`
   for a missing scope and a `429` from the limiter are both audited under the caller who was refused. Only four
   fields are read back out of the response body — `batchId`, `jobId`, `counts`, `totals.posted` — never the key,
   the memo, the payee or the account name (asserted by grepping the written file for all four).
5. **`RequestSizeMiddleware`** (T-911, FR-A-17) — `Api:MaxRequestBodyBytes` (2 MB), before any body buffering.
6. **`ApiKeyMiddleware`** (T-910, FR-A-13) — resolves a **caller** from `clients.json` (salted SHA-256, constant-time
   compare, only the hash is ever stored), checks the per-client CIDR allow-list, then checks `ApiRoutes.ScopeFor`.
   Scopes: `health:read`, `qb:read`, `qb:post`, `qb:post:ai`, `qb:debug`, `jobs:read`, `jobs:write`, `rules:write`,
   `admin`. Unknown key → `401`; wrong scope → `403` naming the scope; revoked client → `401` on its *next* request,
   because the list is re-read per request. Rotation via `previousKeyHash` + window. The single `Api:ApiKey` still
   works as an implicit all-scopes client while `Api:AllowLegacyKey=true` (ships **true**, warned about at startup).
   Startup **refuses to start** when there is no enabled client and no usable shared key. Also holds `AuthBrake`
   (T-911): 10 unrecognised keys a minute per address, then `429` for five minutes — counted on **unrecognised keys
   only**, so a client polling a route it lacks the scope for cannot lock itself out.
7. **`UseRateLimiter`** (T-911, FR-A-15) — partitioned by client id (by IP for the key-free health routes):
   10/min for `qb:post` routes, 120/min for other authenticated routes, 600/min for health. `429` carries
   `Retry-After`. `Api:RateLimits:Enabled` ships **true**; `ApiFactory` pins it false for the test suite.
8. **`IdempotencyMiddleware`** (T-909, FR-A-12) — `Idempotency-Key` on mutating routes, scoped by
   (key, route, client). A replay returns the stored body with `Idempotency-Replayed: true` and its **original**
   status code, and **exactly one write reaches QuickBooks**. A different body under one key is
   `409 idempotency-key-reused`; a retry while the first is still running is `409 idempotency-in-progress` with
   `Retry-After: 5`. Validate is deliberately **not** idempotent — replaying it would hide the change in the rules or
   ledger that a validate exists to reveal.

**Startup can now refuse to run** (`Api/Security/TransportGuard.cs`, T-911): a non-loopback bind without TLS (unless
`Api:AllowInsecureRemote=true`), a CORS `*`, or a non-loopback bind with an empty `Paths:AllowedJobRoots`. The
shipped bind is still `http://127.0.0.1:5080`, so nothing changes for today's installation.

---

## 2. Open questions — 68 of them, with what the code does today

Answer any of these by editing the *Answer* column in `docs/tracker.md › Questions`. **Every one already has a
behaviour**, listed here so the owner can overrule it knowing exactly what happens now. The rule behind almost all of
them is `CLAUDE.md` rule 4: when the spec is silent, do the thing that posts less.

### 2.1 Already answered — do not re-litigate these

| # | Answer |
|---|---|
| **Q-0** | Owner, 2026-09-16: build Core from the spec; the POC will not be supplied. |
| **Q-46** | Owner, **2026-09-23**: yes, add `Scalar.AspNetCore` (2.13.13). It is the 7th and last package. Swagger/Swashbuckle stays ruled out. |
| **Q-62** | Answered by T-912, 2026-09-23: the unexplained single test failure was **teardown, not the application** — `TempDir.Dispose` deleted its folder while Serilog still held the day's log file. `TempDir.Dispose` now retries for a second. Measured 0/3 without T-912, 1/3 with it, 0/6 after the fix. No assertion was weakened. |

### 2.2 Reading and parsing (M0–M5) — Q-1 … Q-33

| # | Question, in one line | What the code does today |
|---|---|---|
| Q-1 | `rules.json.CsvLayouts` format and the real bank/card CSV columns | Layout = `HeaderContains[]` + column names + `DateFormat` + one signed `AmountColumn` (`PositiveIsDebit`) or `DebitColumn`/`CreditColumn`. No layout, or two, matching → statement held. A date that fails the layout format is a row error, with no fallback. Sample layouts imitate Chase exports. |
| Q-2 | What `Normalize(description)` does | Trim, collapse whitespace, upper-case invariant; digits and punctuation kept. Fuzzy matching additionally drops apostrophes and turns other punctuation into spaces. |
| Q-3 | Fingerprint field spelling | `bank`/`card`, `debit`/`credit`, missing check number = `""`. |
| Q-4 | G1 "both orientations" — row order, balance sign, or both? | Row order only (file order or reversed). The sign is fixed by kind. A partly filled balance column fails G1. |
| Q-5 | Paste-ready sheet columns and date format | Checks: Bank, Date, Number, Payee, Account, Amount, Memo. Card: + Type. Deposits: Date, Received From, From Account, Memo, Amount, Deposit To. ISO dates, UTF-8 BOM, CRLF; cells starting `= + - @` get a leading `'`. |
| Q-6 | Base `rules.json` schema and defaults | `FuzzyThreshold` 0.85, `HistoryMinCount` 3. Missing `HoldingExpenseAccount` → numbered checks held; missing `DepositIncomeAccount` → deposits held. `Match` is a plain substring, not a regex. |
| Q-7 | Over-long check number; several aliases or equal fuzzy scores | Line held (`refnumber-too-long` / `unknown-payee`), never truncated or guessed. A memo over 4095 characters is truncated (no money impact). |
| Q-8 | The xUnit runtime packages are not on `CLAUDE.md`'s list | Added — the tests cannot run without them. |
| Q-9 | A file name with several 4-digit groups | No last-four is taken from the name; fall back to the content, else hold `unknown-account`. |
| Q-10 | One unparseable CSV row: hold the row or the statement? | The **whole statement** (`unparsable-rows`) — a missing row makes the statement incomplete. |
| Q-11 | How `requirement.txt` states real last-four values | Assumed to be 4-digit groups on the account lines. **A real example would still help.** |
| Q-12 | Which characters a job folder name (= job id) may contain | Letters, digits, `.`, `_`, `-` only; anything else → 400. |
| Q-13 | Subfolders inside `statements\` / `invoices\` | Not read; listed in `result.json.unreadable[]` as `subfolder-ignored`. `.jpeg`/`.tif` invoices are `unsupported-extension`. |
| Q-14 | Where the "known job folders" are recorded | `Paths.JobIndex` (`jobs.json`), rewritten atomically. A job found `queued` at startup is set `failed (interrupted)`, never re-queued. |
| Q-15 | Must the company in `requirement.txt` match `Company:Name`? | Yes — a different name fails G2. A placeholder or no name → the configured name. An empty `Company:Name` fails every job. |
| Q-16 | Statement-level hold codes the spec does not name | `reconcile-failed`, `extractor-not-available`, `kind-mismatch`. |
| Q-17 | Identical lines in one job; kinds the requirement did not ask for | **Every** copy of an identical line is held `duplicate-line` (never post one of them); an unrequested kind → `kind-not-requested`. Precedence: skip-pattern → duplicate-line → already-posted → kind-not-requested. |
| Q-18 | What happens when the QuickBooks call itself fails | Any exception → job `partial`, every sent line held `quickbooks-no-response`, batch recorded with 0 posted, so a re-run needs `force`. `QuickBooksUnavailableException` (nothing sent) → `partial`, no ledger record. A status-0 line with a different echoed amount → `amount-mismatch`, note names the TxnID to delete. |
| Q-19 | API key defaults | Empty `Api:ApiKey` stops the host; the shipped `change-me` is accepted only in Development. |
| Q-20 | `result.json` fields beyond spec §10 | Adds `company`, `error`, `counts.posted`, `totals.posted`; held items carry `file`, `lineNo`, `kind`, `note`, `candidates`. |
| Q-21 | Retry a Hermes transport failure? Send an empty-key header? | **No transport retry** (only the one validation retry the spec names). Empty key → no `Authorization` header. Audit copies are written for every HTTP attempt, failed ones included. |
| Q-22 | Hermes unreachable at T1 — fall back to the regex reader? | **No.** The job `failed`s, because the regex reading is not what the operator expected. Changing this is one `catch` in `HermesSpecReader`. |
| Q-23 | Date parsing: layout format vs invariant fallback; XLSX date cells | Q-1's rule kept — a text date must match the layout format, because an invariant (US) parse can silently swap day and month. XLSX real date cells are read as dates, so the XLSX parser also accepts exact `yyyy-MM-dd`; CSV does not. |
| Q-24 | What counts as a character for scanned-page detection; OCR edge cases; languages | Whitespace not counted. OCR off + any scanned page → whole file held. OCR on: a scanned page with no images keeps its short text; an undecodable image or any OCR error → `unreadable-statement`. English only. |
| Q-25 | T2 reason codes; how chunk fields combine; which last-four wins; zero rows | `hermes-failed`, `extraction-conflict`. Every field must agree in every part that reports it. File-name last-four wins; a different one in the text → `conflicting-last4`. Zero rows accepted; G1 decides. |
| Q-26 | T2 reconcile formula for cards; no opening/closing balance; partial running balances | Card charges raise the balance. **No opening *and* closing → G1 fails `not-verifiable`, statement held** — model-read amounts are never posted unchecked. Partly printed running balances are checked between the printed ones. |
| Q-27 | Posting re-runs T2/T3/T4; the rows could differ from the dry run the operator reviewed | **Not decided; today the model is called again** (temperature 0) and the post uses whatever passes G1 then. Suggested fix: store the PDF's SHA-256 in `rows.json` and reuse when the hash matches. **This one is on the T-804 go-live checklist as a must-answer.** |
| Q-28 | T3 invoice hold codes, long invoices, vendor vs customer | `unreadable`, `scanned-pdf-ocr-disabled`, `hermes-failed`. One request per invoice, never chunked, so a total is never read from part of an invoice. The company name is sent as "Our company". |
| Q-29 | FR-5 matching details the spec leaves open | No invoice date → no match. No same-direction candidate → all candidates go to the similarity step. A line matched by two invoices → **none** match. Candidates = every line of a G1-passing statement. |
| Q-30 | Which value `InvoiceRef` is; may an unknown invoice party become a payee | `InvoiceRef` = the invoice file name. The party is used **only** when it resolves to a known vendor/customer; otherwise the line stays `unknown-payee`. Numbered checks and transfers never get one. |
| Q-31 | T4 account-list filtering, `OtherIncome`, unlisted alternatives, no accounts | When any account has a type, only Expense/Income/CostOfGoodsSold/OtherExpense are kept. An unlisted alternative is dropped from `candidates`. No accounts → line held `no-accounts`, Hermes not called. |
| Q-32 | Tier 3–4 and G3 details | A below-threshold tier-3 answer becomes the tier-4 answer (no second call). Only a matched **vendor** invoice with a hint is tier-3 evidence. Holds `low-confidence` (checked first) then `no-prior-posting`. |
| Q-33 | Is `analysis.json`'s `confidence` the category or the T4 score? | **Both are written**: `confidence` keeps the FR-6 category, and the score is a new `modelConfidence` (null when the model was not asked). |

### 2.3 QuickBooks, rules and deployment (M6–M8) — Q-34 … Q-45

| # | Question, in one line | What the code does today |
|---|---|---|
| Q-34 | Fake gateway safety; a call still running when the busy timeout fires | `QuickBooks:Fake=true` → an in-memory simulated company, allowed **only** in Development/Testing (startup fails otherwise). Not Windows and not faked → `UnconfiguredQbGateway`, nothing is ever sent. A call past `BusyTimeoutSeconds` is abandoned but keeps the lock until COM really ends. |
| Q-35 | FR-15 sync details | Accounts/customers store `FullName`, vendors `Name`. `missingInRules` compares **exactly** (ordinal), so a case difference is reported. Audit copies go to `qb-audit/sync-lists.*.qbxml`. Any non-0/1 status → 502 and `qb-lists.json` is untouched. |
| Q-36 | FR-8 G4 query filters, payee/RefNumber comparison, deposit payee, failed query | One message set per (kind, header account); results whose header account differs are ignored. Exact = same date **and** (same RefNumber other than `ACH`, or same payee). A refused query → those lines held `duplicate-check-failed`. A gateway failure → nothing added, job `partial`, no ledger record. |
| Q-37 | FR-11 COM retry, and a stale backup vs the §6 state machine | Retried once: a session that could not open, and a COM error on a **read-only** message set. **Never** retried: a COM error on an add or delete, a busy timeout, or a wait for an abandoned call. Stale backup → job `failed` `backup-too-old`, nothing sent, no ledger record (`posting → failed` added for this case only). **On the T-804 must-answer list.** |
| Q-38 | FR-13 undo details | Only `statusCode 0` with the requested `TxnID` counts. Anything else (including 3120 "cannot be found") lands in `failed[]` and the entry stays live — a person checks QuickBooks. A gateway failure → 503, nothing marked. |
| Q-39 | What `/health/quickbooks`'s `message` and `companyFile` hold; does it wait for a post? | `message` = product name and version, or the error plus process bitness. `companyFile` = `GetCurrentCompanyFileName` in a second session. It waits for the gateway lock, so during a post it may take up to `BusyTimeoutSeconds` and then 503. **It does not compare the open file with `Company:FilePath`** — that is what FR-A-5's `openInQuickBooks` is for. |
| Q-40 | FR-14 rules-teaching details | `//` comments are dropped on the first taught rule. With `qb-lists.json` present, the account, vendor and alias name must all be in it **exactly**; a case-only difference is refused with "Did you mean …". Fragments need ≥ 3 characters. A broken `rules.json` → 500, file untouched. |
| Q-41 | §14 logging details | `Paths:Logs/qbautopost-yyyyMMdd.log`, daily and at 1 GB, 31 kept, shared. Sinks are fixed in code so a configured sink cannot bypass scrubbing. Every event is scrubbed: configured key values, `X-Api-Key:`/`Bearer`/`apiKey=` values, and secret-named properties are masked `***`. |
| Q-42 | §4 never names the Hermes image, its container port/variables or data path | Nothing guessed: `HERMES_IMAGE`, `HERMES_CONTAINER_PORT`, `HERMES_DATA_PATH` and `API_SERVER_KEY` are **required** in `deploy/hermes/.env`, so compose refuses to start rather than pull a guessed image. **The owner must name the image.** |
| Q-43 | `start-all.ps1` cannot wait on `/health/hermes` before the API exists | It waits for Hermes' **own TCP port** (up to 300 s), starts the API, then waits for readiness. If Hermes stays down the API is still started and the script exits **2**; Docker/compose failures exit **1**. Limited rights, interactive logon, 60 s delay. |
| Q-44 | How several companies are run | **Nothing added: one installation = one company.** `start-all.ps1` starts at most one API process, so a second installation on the same server would be seen as already running. |
| Q-45 | What "no AI" means for the POC | `Hermes:Enabled=false` (default `true`): the requirement is read by the regex reader, **every other model call fails as unavailable** — a PDF statement is held, an invoice is unmatched, a line no rule resolves is held. Nothing is guessed. **Ask the owner whether this mode stays after the POC.** |

### 2.4 M9 — Q-47 … Q-61, Q-63 … Q-70

| # | Question, in one line | What the code does today |
|---|---|---|
| Q-47 | D-4 says remote callers — from *where*? Same LAN, VPN, or the public internet? | TLS required off-loopback (startup refuses without it unless `AllowInsecureRemote`), per-client CIDR allow-list, rate limits, **no CORS origins by default**. **This answer decides whether a reverse proxy and managed certificates are needed, so it is the first question in §3.6.** |
| Q-48 | Who issues and revokes caller keys, and where do keys live between issue and use? | `clients.json` beside `rules.json`, git-ignored, **hash + salt only**. Keys are generated by `scripts/new-api-client.ps1`, printed **once**, never logged or written to `output/`. Rotation via `previousKeyHash` + expiry. It is not a secret manager and does not pretend to be. |
| Q-49 | On the direct path there is no statement, so G1 cannot run — is `controlTotal` enough? | Refuse the whole batch, post nothing, `400 control-total-mismatch`. The API **never** adjusts an amount to make a total agree. |
| Q-50 | May a caller name the company file per request? | **No.** `QuickBooks:AllowCompanyFileOverride=false`; a supplied `companyFile` is `400` and **no gateway call is made**. When enabled it must be absolute, `.QBW`, and inside `AllowedCompanyFolders[]`. |
| Q-51 | May a direct post use Hermes to choose accounts (`allowModelAccounts`)? | Default `false`: an unresolved row is **held**, never guessed. `true` would need `Hermes:Enabled` **and** the `qb:post:ai` scope — but it is **carried and not implemented**: tiers 3–4 never run on the direct path at all. |
| Q-52 | How long must batch evidence, audit lines and idempotency records be kept? | 400 days for `audit-*.jsonl` (money evidence), 30 days for idempotency records, 31 days for the operational log. **`Paths:ApiBatches` has no retention at all and grows without bound**, and `ApiBatchStore.All()` reads every batch to count attempts — O(batches) per post. Fine for hundreds, wrong for a hundred thousand. |
| Q-53 | When may `Api:LegacyRoutes` default to false and the flat paths be deleted? | Stays **`true`**. Every flat hit is logged at Warning once per route per hour, so the log will tell you who still uses them. |
| Q-54 | Does the folder job model stay, or does the direct API replace it? | Both maintained, nothing removed. One engine, two entrances; a golden test keeps their qbXML byte-identical. |
| Q-55 | FR-A-3 wants the supported qbXML versions, but reading them needs a session ticket the same requirement forbids | Neither guessed: the probe reports the **configured** `QuickBooks:QbXmlVersion` and no `supported` list, so nobody is told a version works when it may not. Getting the list means giving the probe its own session, which stops it being safe to call while QuickBooks is busy. |
| Q-56 | FR-A-4 wants six timed steps, but one gateway call is one whole SDK session | The three **measurable** steps are reported (`waitForGateway`, `hostQuery`, `companyFile`); **no number is invented**. A slow phase still surfaces, because the whole `hostQuery` is marked slow and carries the dialog hint. The spec's example was amended to match. |
| Q-57 | FR-A-5's `companyName` check | **Not implemented rather than guessed.** It needs a `CompanyQueryRq` whose response element names cannot be verified from here. `openInQuickBooks` already answers the dangerous form by comparing the actual open file path, which is stricter than a display name. |
| Q-58 | A direct caller sends both a check number **and** a payee; FR-6 leaves a numbered check's payee blank | **The caller's payee is used.** Dropping a name they explicitly sent would silently discard an instruction. This is the **one place the two entrances decide differently**, so it is worth confirming. |
| Q-59 | FR-A-8 mentions `POST /api/v1/batches/{id}/post` for a `ready` batch; it is in no route table | **Not built.** A caller re-sends with `dryRun:false`, which re-runs every gate against the rules and ledger **as they are at posting time** — safer than posting a plan made earlier against rules that have since changed. |
| Q-60 | What becomes of an `Idempotency-Key` whose request **failed**? | **Released on any non-2xx.** Holding it would lock a caller out of their own key over a typo, and would block the very retry a `503` exists to invite. Safe because idempotency is the convenience layer while G4 and the ledger are the safety layer. |
| Q-61 | `spec.md` §6 leaves all of `/health/*` open; api-v1 §2.2 opens only `/health` and `/health/ready` | **The narrower rule, on both surfaces.** `/health/quickbooks` opens a QuickBooks session and `/health/sdk` probes COM; with remote callers, leaving them open lets a stranger make the server hammer QuickBooks. **Any monitoring pointed at `/health/quickbooks`, `/health/sdk` or `/health/hermes` starts getting `401` on this upgrade.** See §3.6. |
| Q-63 | FR-A-17 requires caller paths inside `Paths:AllowedJobRoots`, and §12 ships that list **empty** | **Empty means *unconfigured***: unrestricted while the bind is loopback, and **startup refuses a non-loopback bind while it is empty**. Today's loopback deployment is unaffected; exposing the port forces the owner to say where jobs live. Same rule for `QuickBooks:AllowedCompanyFolders`. |
| Q-64 | Is a request with an **unknown** key, or one stopped by the brute-force brake, audited? | **No line.** FR-A-16 says *authenticated*, and a stranger must not be able to grow the money-evidence file at will. The operational log still records every refusal with its address and reason. A **403 is** audited, because the caller is identified. |
| Q-65 | FR-A-16 lists `outcome` and `totalAmount` but never says what values they take | `outcome` ∈ `ok` (2xx) · `accepted` (202) · `replayed` · `refused` (4xx) · `failed` (5xx). `totalAmount` = the batch's `totals.posted`, **the money that actually moved**, so a dry run records `0`. Both are one-way doors for a reader of old files, so they are recorded rather than assumed. |
| Q-66 | FR-A-18 requires `health:read` on the reference "in Production" — does Development serve it to anyone? | **`health:read` in every environment.** `ScopeFor` is a pure function of method and path with no environment in it, and the page lists every route, scope and request shape. **The cost: because the key travels in a header, the page cannot be opened in a plain browser at all** — it needs a client that sets `X-Api-Key`. Relaxing it for Development is a one-line change. |
| Q-67 | api-v1 §2.2 calls `Authorization: Bearer` the **preferred** form, but the middleware reads only `X-Api-Key` | **Nothing was changed in the middleware** — accepting a second authentication header is a change to the security surface, not a documentation task. `openapi.json` declares both schemes and *offers* the one that works; the `bearerAuth` description says plainly that this build does not accept it. **Either the middleware grows a second header or §2.2 is corrected.** |
| Q-68 | `POST /api/v1/jobs/validate` answers **422** where `POST /jobs` answers **400** for the same folder | The validate answers `422` with the body, because FR-A-7 names only 200/422 and a validator that returns a body only on success is useless. A *malformed* request (no `folder` field) is still `400`, as is a folder outside the allow-list. `POST /jobs` was **not** changed. |
| Q-69 | api-v1 §3 marks `/api/v1/quickbooks/lists/sync` as a **rename** of `POST /qb/sync-lists` — is the old spelling deleted? | **Both are mapped.** The old one is marked `deprecated` in the document. Removing a working path a caller may still use is not a decision to take with nobody watching. Note that `/api/v1/qb/sync-lists` is *versioned*, so Q-53 does **not** cover it — it needs its own answer. |
| Q-70 | `GET /api/v1/quickbooks/lists` returns every account, vendor and customer in one response | **No paging.** It is a local read of a file this app wrote, so nothing is unbounded that was not already on disk. If the real chart of accounts makes it unwieldy, the answer is a `?q=` filter rather than paging — **but nobody has measured it**. |

---

## 3. Everything that needs the owner or the server

This is the consolidated list. **None of it can be done by an agent**, and none of it has been attempted or simulated.

### 3.1 T-903 — the QuickBooks SDK probe (`ready-for-human`)

Run with the app running on the QuickBooks machine:

- [ ] `GET /api/v1/health/sdk` with QuickBooks **closed** → answers (does not hang, does not raise the certificate
      dialog); `requestProcessor.registered` true, `canOpenConnection` true, `quickBooks.running` false
- [ ] Same call with QuickBooks **open** on the test company → `quickBooks.running` true, `name` `QBW.EXE`,
      `bitness` `x64`, `bitnessMatch` true, `ok` true
- [ ] `processBitness` reads `x64` (confirms T-609 from inside the app rather than from the install folder)
- [ ] The call opens **no** company file: the QuickBooks title bar does not change and no new certificate prompt appears
- [ ] Two calls in a row both answer within a second (the STA thread is created and released each time)
- [ ] With the x86 build of the package (if still to hand): `bitnessMatch` false, `ok` false, message names both bitnesses
- [ ] `qbXmlVersion` echoes `QuickBooks:QbXmlVersion` from `appsettings.json`
- [ ] Log line `QuickBooks SDK health: …` present; **no company file path and no key in it**

Specifically unproven until this is run: that `OpenConnection2` without `BeginSession` really does avoid the
certificate dialog; that `Process.GetProcessesByName("QBW")` finds QuickBooks in the operator's Windows session and
that `MainModule.FileName` is readable with the rights the app has; and that a second call in the same process
succeeds (COM apartment behaviour across repeated STA threads).

### 3.2 T-609 — the M6 QuickBooks round trip, on a **copy** of the company file (`ready-for-human`)

Use `scripts/qb-server-check.ps1`. `post` and `undo` need `-ConfirmCopy`.

- [ ] `Company:FilePath` points at a **copy**; QuickBooks is open on that copy in the same Windows session as the API;
      `QuickBooks:Fake` is not set
- [ ] `-Step health` → ok (certificate dialog answered "Yes, always"). "Not registered" means the bitness does not match
- [ ] `-Step sync` → counts plausible; `missingInRules` empty after fixing `rules.json`
- [ ] `-Step dryrun -Folder <real folder>` → `ready`; `analysis.json` reviewed
- [ ] `-Step post -JobId <id> -ConfirmCopy` → `posted`/`partial` as expected; transactions visible in QuickBooks with
      the right account, payee, date, amount and memo
- [ ] `query-*.response.qbxml` show status 0 or 1 — this is what confirms `DepositQuery` accepts `AccountFilter` (Q-36)
- [ ] `response.qbxml`: each `*AddRs` has `TxnID`, `EditSequence` and `Amount`/`DepositTotal` equal to the statement (G5)
- [ ] Re-run the same folder with `force=true` and post again → every line skipped `already-posted`
- [ ] **G4 live check**: enter one statement line by hand on the copy, then dry run and post a folder containing it →
      that line is skipped `already-in-quickbooks`; with the date moved 1–3 days it is held `possible-duplicate`
- [ ] `-Step undo -BatchId '<id>#<n>' -ConfirmCopy` → all deleted, status 0 per `TxnDelRs`, transactions gone
- [ ] With QuickBooks **closed**: `-Step health` → 503 with a clear message, and a post ends `partial` "nothing posted"
- [ ] COM constants confirmed (`OpenConnection2` local = 1, `BeginSession` DoNotCare = 2)
- [x] Bitness confirmed **x64**, on the server 2026-09-20 (`GET /health/quickbooks` answered
      `"Intuit QuickBooks Enterprise Solutions 24.0 34.0"`)

**One caution from T-914**: `qb-server-check.ps1 -Step health` used to call `/health/quickbooks` **without a key**
and would have stopped at step 1 with a `401` since session 15. It now sends the key — but the script itself has
never been executed against a live host.

**What has since been checked, 2026-09-23** (see §3.9): all nine operator scripts parse cleanly under Windows
PowerShell 5.1, and every command they invoke resolves there. That rules out a syntax error or a PowerShell 7-only
cmdlet stopping you on the first line. It does **not** mean any of them does the right thing.

### 3.3 T-802 — deploy and auto-start (`ready-for-human`)

On the server, as the auto-logon account:

- [ ] `deploy\hermes\.env` and `provider.env` filled; `docker compose config --quiet` prints nothing
- [ ] `.\deploy\start-all.ps1 -WhatIf` → exit 0, lists the steps, changes nothing
- [ ] `.\deploy\start-all.ps1` → exit 0; `docker port qbautopost-hermes` shows `127.0.0.1:8642` only;
      `logs\start-all-yyyyMMdd.log` written and **holds no key**
- [ ] Run it again → "The API already runs … not starting another", exit 0
- [ ] With Hermes kept down → exit 1 (compose refused) or 2 (API started, Hermes unhealthy) as documented
- [ ] `.\deploy\install-task.ps1 -WhatIf`, then elevated for real → Task Scheduler shows `QbAutopost`:
      "Run only when user is logged on", at logon, delay 1 minute, **not** "highest privileges"
- [ ] Sign out and in → the API and Hermes come up untouched; `/health/quickbooks` ok with QuickBooks open
- [ ] The API window is in the **same session** as QuickBooks

**Changed in T-914 and never run**: `start-all.ps1` used to wait on `/health/hermes` without a key, which has been a
`401` since session 15 — it would have logged "not healthy" for its whole 180 s budget on every boot and exited 2
with the API perfectly fine. It now waits on the key-free `/api/v1/health/ready`, and Hermes is proved by its own TCP
port before the API starts. **Neither PowerShell script has ever been executed.** Also: Q-42 — nobody has named the
Hermes image, and `docker compose` will refuse to start until somebody does.

### 3.4 T-803 — the shadow week (`ready-for-human`)

`DryRunDefault=true` throughout; nothing is posted.

- [ ] T-609 and T-802 done; `/api/v1/health/quickbooks` and `/api/v1/health/hermes` ok; lists synced,
      `missingInRules` empty
- [ ] Five real job folders (different banks/cards, at least one PDF statement and one with invoices) that a person is
      entering by hand as usual
- [ ] For each: `-Step dryrun -Folder <folder>` → `ready`, then export the same period from QuickBooks to
      `Date,Amount,Payee,Account[,Source]` and run `scripts\shadow-diff.ps1`
- [ ] For each disagreement decide: tool wrong → teach a rule or log a defect; person wrong → note it
- [ ] Record each run in the tracker's shadow log. **The week ends after two consecutive runs with zero disagreements.**
- [ ] Check `output\hermes\` and one job's logs hold no keys

### 3.5 T-804 — go-live (`ready-for-human`), and T-806 the POC

Go-live, only after T-803 shows two clean runs:

- [ ] **Owner answers recorded for at least Q-27 (re-running T2/T3/T4 at post time), Q-37 (COM retry and the backup
      guard) and Q-42 (the Hermes image)**
- [ ] A fresh QuickBooks backup exists in `QuickBooks:BackupFolder`, younger than `BackupMaxAgeHours`; the ledger and
      `rules.json` are backed up
- [ ] **First company only**: `DryRunDefault=false`, API restarted; other companies stay `true`
- [ ] First real folder: dry run → review `analysis.json` → post → check every transaction; keep the batch id
- [ ] Undo drill on one real batch if the owner agrees, then re-post
- [ ] Monitor a week: every job `posted` or `partial` with understood holds; no duplicates (G4); logs clean
- [ ] Then the remaining companies one at a time — remembering Q-44: **one installation = one company**

T-806 (the no-AI POC package) also still needs the server: create the test company, import
`samples/poc/tropicana-lists.iif`, and run `README-POC.md` steps 1–6. **The IIF import itself has never been done**,
and **the POC package has not been rebuilt since `samples/poc/appsettings.json` gained the M9 settings** — nothing
proves the packaged app starts with that file.

### 3.6 Decisions the owner has to make **before** this build is deployed

1. **Q-61 is urgent, not theoretical.** Any monitoring pointed at `/health/quickbooks`, `/health/sdk` or
   `/health/hermes` starts getting `401` the moment this build goes on. The runbook now tells the operator to point it
   at `/api/v1/health/ready` instead — **but somebody has to actually do that before the upgrade**, not after.
2. **Q-67 — `Authorization: Bearer` does not work and never did**, although api-v1 §2.2 calls it preferred. Either
   `ApiKeyMiddleware` grows a second header or §2.2 is corrected. The OpenAPI document and the runbook currently
   describe what the **code** does, so a caller following them will work; a caller following §2.2 gets a `401`.
3. **Q-47 — where do remote callers actually come from?** LAN, VPN or the internet decides whether the CIDR allow-list
   is enough or a reverse proxy and managed certificates are needed. Until it is answered the bind stays loopback.
4. **Q-42 — name the Hermes image.** `docker compose` refuses to start without it, deliberately.
5. **Q-27 and Q-37** are on the go-live checklist because they change what gets posted, not just how.
6. **Q-44** — if more than one company is ever wanted on one server, that is a design decision nobody has made.

### 3.7 The direct-post server checklist — **new, never run**

`POST /api/v1/quickbooks/transactions` has never sent a single qbXML element to a real QuickBooks. Before any caller
is allowed to use it, do this on a **copy** of the company file, in this order:

**Preconditions**

- [ ] T-609 and T-903 complete and green
- [ ] `Company:FilePath` points at a **copy**; QuickBooks open on that copy, same Windows session
- [ ] `POST /api/v1/quickbooks/lists/sync` run, `missingInRules` empty
- [ ] A client key issued with `scripts/new-api-client.ps1`, scopes `qb:read` + `qb:post` (add `qb:debug` only if you
      want the qbXML echoed back)
- [ ] `POST /api/v1/quickbooks/company-file/validate` → `200`, `openInQuickBooks` ok, `listsSynced` ok

**Step 1 — dry run (nothing should reach QuickBooks)**

- [ ] `POST /api/v1/quickbooks/transactions/validate` with **three rows** covering three kinds (one `Check` with a
      `checkNo`, one `CcCharge`, one `Deposit`) and the exact `controlTotal` → `200`,
      `wouldPost + held + skipped = submitted`
- [ ] Send the same body with the control total **off by one cent** → `400 control-total-mismatch`, and confirm from
      the log that **no** gateway call was made
- [ ] `POST /api/v1/quickbooks/transactions` with `dryRun: true` → a batch id and a `ready` result, and **nothing new
      in QuickBooks**
- [ ] Confirm `Paths:ApiBatches/<batchId>/request.json` exists and holds what you sent

**Step 2 — one real batch**

- [ ] Fresh QuickBooks backup in `QuickBooks:BackupFolder`, younger than `BackupMaxAgeHours`
- [ ] Repeat the same three rows with `dryRun: false` and an `Idempotency-Key` → `200`/`202` with a batch id
- [ ] **Open QuickBooks and check each of the three transactions by hand**: account, payee, date, amount, memo,
      check number. This is the step that cannot be skipped — the amounts and element order have only ever been
      compared against a fake
- [ ] `GET /api/v1/batches/{id}` matches what you see in QuickBooks
- [ ] `response.qbxml`: each `*AddRs` carries `TxnID`, `EditSequence` and an `Amount`/`DepositTotal` **equal to what
      was sent** (G5)
- [ ] The ledger (`ledger.json`) has one entry per posted row, written the same way a folder job writes them
- [ ] `audit-yyyyMMdd.jsonl` has **one line** for the post: right `clientId`, `outcome: ok`, `batchId`, and
      `totalAmount` equal to the money that moved. Grep that file for the key, the memo and the payee — **none of
      them should be there**
- [ ] **Send the identical request again with the same `Idempotency-Key`** → the stored body comes back with
      `Idempotency-Replayed: true` and **no second transaction appears in QuickBooks**
- [ ] Send it again **without** the key → G4 holds the rows and the batch comes back `partial`; still no duplicate in
      QuickBooks
- [ ] Send it with the same key but a changed amount → `409 idempotency-key-reused`

**Step 3 — undo it**

- [ ] `POST /api/v1/batches/{id}/undo` → every row deleted, status 0 per `TxnDelRs`
- [ ] **The three transactions are gone from QuickBooks**; the ledger entries are marked `undone`
- [ ] Undo the same batch again → `deleted: 0`, QuickBooks not called, nothing changed
- [ ] Re-post the same rows with a **new** `Idempotency-Key` → they post again (undone entries no longer count for G4)

**Step 4 — the failure paths (do these, they are the ones nobody tests in production)**

- [ ] Close QuickBooks and post → `503`, batch recorded `Unavailable`, **nothing partially applied**
- [ ] Delete/age out the backup so the guard fires → the post is refused before anything is sent
- [ ] Post with a client whose key lacks `qb:post` → `403` naming the scope, **and a line in the audit file** (a 403
      is audited; an unknown key is not — Q-64)
- [ ] Send 11 posts in a minute with the shipped limits on → the 11th is `429` with `Retry-After`.
      **This is the first time a production rate limit will ever have throttled anything** (see §4)

### 3.8 TLS and transport — what T-911 could **not** verify

T-911 shipped the transport requirements, and **not one of them has met a real socket.** All four need a person:

1. **No certificate has ever been loaded.** `TlsCertificate.Load` is executed by no test — the test host does not use
   Kestrel, and shipping a PFX and password into the repository was not acceptable. What is proven is the *decision*
   to require TLS, not that Kestrel then serves it. **A person must verify TLS with a real certificate before the
   bind is moved off loopback.**
2. **No HSTS header has ever been emitted.** `UseHsts` is asserted as a decision; the header itself only appears on an
   HTTPS request, and no test makes one.
3. **`AddServerHeader = false` is unverified on a real socket.** The test asserts the response carries no `Server`
   header — but TestServer never adds one, so that assertion would pass even with the Kestrel line deleted. Only
   `curl -I` against the real host proves it.
4. **No production rate limit has ever throttled anything.** `ApiFactory` pins `Api:RateLimits:Enabled=false`, and the
   host tests use limits of 1–2 per minute that they set themselves. That the shipped defaults are `Enabled=true` with
   10/120/600 is asserted on the **settings object**, not by watching a real minute pass.
5. **Startup refusal is tested as a function, not as a process.** `TransportGuard.Require` throwing is asserted
   directly; that `Program.cs` calls it before a socket opens is read from the code, not executed.

Three further limits worth knowing before you expose the port:

- **The limiter is a fixed window.** A caller may send 10 posts at 11:59:59 and 10 more at 12:00:00 — 20 through in
  two seconds, once a minute. That is the standard reading of "per minute", but it is not a sliding window.
- **The brute-force brake is per process and in memory.** A restart forgives every blocked address.
- **The CIDR check trusts `RemoteIpAddress`.** Behind a reverse proxy that is the **proxy**, not the caller.
  `X-Forwarded-For` handling was deliberately not guessed at, and getting it wrong would make the allow-list
  meaningless.

---

### 3.9 Operator scripts — what was checked without a server, 2026-09-23

Two of the scripts a person will run on the server had **never been executed**, and one of them
(`qb-server-check.ps1 -Step health`) was silently broken from session 15 to T-914: it called a route that had begun
requiring a key, so it would have stopped at step 1 with a `401`. T-914 fixed that by inspection. The existing
`DeployScriptsTests` check script *content* by string matching — ASCII only, `-WhatIf` supported, no secrets, no
service installed, loopback only — and **none of them would notice a syntax error**.

So the scripts were put through PowerShell's own parser and command resolver:

| Check | Result |
|---|---|
| All nine `.ps1` under `deploy/` and `scripts/` parse | **Clean**, 0 errors, Windows PowerShell 5.1 |
| Every command each script invokes resolves on 5.1 | **Clean** — no PowerShell 7-only cmdlet, no typo'd command name |

That is a real result and a narrow one. **It proves only that they start.** It does not prove any script does the
right thing, that its arguments are correct, that a path it names exists on the server, or that it behaves against a
live host — and the parser cannot see a wrong URL, a missing key or a mistaken parameter value, which is precisely
the class of bug T-914 had to fix by reading. There is also **no regression test** for this: `dotnet test` must pass
on Linux (`CLAUDE.md` rule 1) and these scripts only parse under Windows PowerShell, so the check was run by hand
and its result recorded here rather than pinned by a test. **Re-run it after editing any script**, with the parser
snippet in the session log for 2026-09-23 (session 23).

## 4. What is implemented but has **never** run against a real QuickBooks

**Read this section twice if you are deploying on a Friday afternoon.**

The honest summary: **the entire second half of M9 — T-907 through T-914 — has only ever run against
`FakeQbGateway`, `FakeHermesClient`, `FakeQbSdkProbe` and `SimulatedQuickBooks`.** So has T-903's COM probe. The one
thing that *has* touched a real QuickBooks is a single `GET /health/quickbooks` call on 2026-09-20, which returned
`ok: true` and confirmed the x64 bitness. Nothing has ever been posted to a real company file by any version of this
application.

That is not a rhetorical caution. Specifically:

| Task | What exists | What has never happened |
|---|---|---|
| **T-903** | `GET /api/v1/health/sdk`, the COM probe, PE bitness reader | The COM path has **no automated test and is not known to work**. `Type.GetTypeFromProgID`, `OpenConnection2` and `CloseConnection` have never been called on the server. |
| **T-904** | Connection test with three measured timings | No real certificate dialog has been met. The fake reproduces the *shape* of a slow call that succeeds, not QuickBooks. |
| **T-905** | Company-file validation, seven checks | Whether the SDK's spelling of the open company file path matches the configured one on the real server (UNC vs mapped drive) is unverified. |
| **T-907** | `transactions/validate`, the offline planner | Tiers 3–4 never run offline, so a plan can be **gloomier** than the post — safe, but it means "validate says held" is not proof the post would hold. A clean validate is **not** a guaranteed post: G4's QuickBooks half, the backup guard and G5 can all still change the answer. |
| **T-908** | `POST …/transactions`, batch evidence, `GET /batches/{id}` | **No real QuickBooks has seen any of it.** No direct-post server checklist existed until §3.7 of this document, and it has not been run. Evidence is never aged out (Q-52). Two concurrent posts of the same reference could pick the same attempt number. |
| **T-909** | Idempotency store and middleware | The lock is in-process, correct for one host and wrong for two. No real retry-after-timeout has ever been replayed. |
| **T-910** | Per-caller keys, scopes, rotation, `new-api-client.ps1` | No key issued by the script has ever authenticated against a server installation. The CIDR check has never been behind a proxy. `qb:post:ai` has **no effect at all**, because `allowModelAccounts` is unimplemented (Q-51). |
| **T-911** | TLS requirement, headers, CORS, rate limits, brake, body/field caps, path allow-lists | **No TLS handshake, no HSTS header, no `AddServerHeader` on a real socket, and no production rate limit has ever throttled anything** (see §3.8). `QuickBooks:AllowedCompanyFolders` is enforced but has no host-level test. |
| **T-912** | Audit trail, `requestId`, `clientId` on every log line | Every `batchId`, `counts` and `totalAmount` in an audit line has come from the **fake**. The 400-day retention has never elapsed. Concurrency is untested. A disk-full or read-only `Paths:Logs` is untested. **Nobody has ever opened `audit-*.jsonl` in whatever tool the owner will use to read it.** |
| **T-913** | `openapi.json` (23 operations), drift test, Scalar UI | **No browser has ever opened the reference page.** The schemas are checked for *existence*, not truth — a renamed C# field would pass the drift test. The documented failure statuses (400/409/502/503) are never asserted to be producible. The document has never been run through an OpenAPI validator. `Api:Reference:Enabled` is never true in a shipped configuration, so "Development turns it on" is read from the file, not observed. |
| **T-914** | `GET …/lists`, `POST …/lists/sync`, `POST /jobs/validate`, all operator docs moved to `/api/v1` | **The validate has never read a PDF statement** — both sample statements are CSV. **No test asserts the validate and a real job reach the same verdict end to end** (the G1 step is literally the same code, which is the argument, but nothing runs both entrances over one folder and compares). `OperatorDocsTests` checks that the documents say the right *words*, never that the sentences around them are *true* — only a person reading the runbook can establish that, and **that reading has not happened**. **Neither PowerShell script has been executed.** The POC package has not been rebuilt. `GET …/lists` has no cap and nobody has sent a large one. Rate limits are off in the tests, so neither new route has ever met the limiter. |

Two more facts that belong here rather than buried:

- **There is no coverage number anywhere in M9.** The solution references no coverage package and `CLAUDE.md` forbids
  adding one without an answered question. What is reported instead is the test count and, per task, that every branch
  of each new unit has at least one test. Nobody should read "1346 tests pass" as a coverage claim.
- **A published exe started from a directory other than its own** — the session-13 server defect — is fixed in
  *decision logic* (`ContentRoot.Select`) and unit-tested there. That the published exe actually finds its
  `appsettings.json` when started elsewhere has still never been observed on a server.

---

## 5. Where to look next

- `docs/spec-api-v1.md` — the M9 contract (route table §3, FR-A-1…FR-A-18).
- `docs/tracker.md` — task rows T-901…T-914, the Questions table (answer them there), Decisions, Session log.
- `docs/testing/T-9xx.tdd.md` — one evidence file per task; the last section of each is its own honest
  "what this does not prove". §4 above is the union of those sections.
- `docs/runbook.md` — the operator's document: §3.4 keys and scopes, §3.5 job roots, rate limits and the 429,
  §3.6 the reference page, §8.7 the audit file.
- `handoff.md` — §6 traps (26 of them), and §10, which is the same server list as §3 here.
