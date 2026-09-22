# QbAutopost — API v1 Specification (API conversion)

Status: **draft for owner approval** · Written 2026-09-22 (session 14) · Supplements `docs/spec.md`, does not replace it.
Where this document and `docs/spec.md` disagree about a route, **this one wins for `/api/v1/*`**; `docs/spec.md` §6 keeps
describing the legacy flat routes until they are removed (§9).

---

## 0. The one thing to read first

**QbAutopost is already an ASP.NET Core 8 API.** `src/QbAutopost.Api` is a minimal-API host with 10 routes, an
`X-Api-Key` check, RFC 7807 problem details, Serilog file logging and two health endpoints. This specification is
therefore a **restructure plus five new capabilities**, not a rewrite. Every requirement below is labelled:

- **[A] already there** — exists and works; may move to a new path.
- **[R] reshape** — existing behaviour, new contract (path, body, extra fields).
- **[N] new** — code that does not exist yet.

### 0.1 Request → existing code map

| Owner asked for | Verdict | Today | Becomes |
|---|---|---|---|
| Application health check | **[N]** | nothing; only per-dependency probes | `GET /api/v1/health`, `GET /api/v1/health/ready` |
| QuickBooks **SDK** health | **[N]** | never probed; a missing/mis-registered `QBXMLRP2` surfaces only as a failed call | `GET /api/v1/health/sdk` |
| QuickBooks **connection test** | **[R]** | `GET /health/quickbooks` → `QbHealth.CheckAsync` (HostQuery + company file) | `POST /api/v1/quickbooks/connection/test` (per-step timings, optional file), thin `GET /api/v1/health/quickbooks` kept |
| QuickBooks **file validation** | **[N]** ×2 | `FolderReader.Validate` validates a *job folder*; the company file is never validated | `POST /api/v1/quickbooks/company-file/validate` and `POST /api/v1/quickbooks/transactions/validate` |
| QuickBooks **post data** | **[R]+[N]** | `POST /jobs/{id}/post` posts a folder-derived batch | folder path kept at `/api/v1/jobs/{id}/post`; **new** `POST /api/v1/quickbooks/transactions` takes JSON rows |
| Versioning + API reference | **[R]** | flat paths, no OpenAPI document | `/api/v1/*` everywhere, `/api/v1/openapi.json`, Scalar UI at `/api/v1/reference` |
| Remote callers, real auth | **[N]** | one shared key, loopback bind | per-caller keys + scopes, TLS required off-loopback, rate limits, per-caller audit |
| Logging to a local folder | **[A]** | Serilog daily rolling file, secret scrubbing, `jobId` on every line | + `requestId` and `clientId` on every line (§8) |

### 0.2 Decisions the owner made (2026-09-22)

| # | Question | Answer |
|---|---|---|
| D-1 | What does the post API take? | **Both**: keep the job-folder pipeline *and* add a direct JSON-rows endpoint. Both end at the same mapping → G4 → qbXML → post → G5 → ledger path. |
| D-2 | Which file does "file validation" mean? | **Both, as two endpoints**: the QuickBooks company file, and the incoming transaction data. |
| D-3 | How much reshaping? | **`/api/v1` prefix + an OpenAPI document**, minimal APIs kept, current JSON bodies kept, legacy paths kept for one release. Reference UI is **Scalar, not Swagger** (owner, 2026-09-22). |
| D-4 | Who calls it? | **Remote callers**: TLS, per-caller keys, rate limits, per-caller audit trail. |

D-4 is the expensive one. It turns a loopback tool into a network-reachable service that can move money in a real
company file, so §7 is a hard requirement set, not advice.

---

## 1. Scope

In scope: the HTTP surface, authentication and authorization, the direct-post path, validation and health endpoints,
OpenAPI, hosting, and the logging/audit that D-4 forces.

Out of scope (unchanged from `docs/spec.md` §2): multi-company in one installation (Q-44), QuickBooks Online, a
database, a UI, background schedulers beyond the single `JobWorker`.

Unchanged invariants (`CLAUDE.md`): COM only in `QbAutopost.QuickBooks`; the model never computes money; hold rather
than guess; qbXML element order is golden-file tested; every state transition persisted before the next step;
`dotnet test` green on Linux with no real QuickBooks or Hermes.

---

## 2. Conventions for every `/api/v1` route

1. **Base**: `https://<host>:<port>/api/v1`. Plain HTTP is allowed **only** when the bind address is a loopback
   address (§7.2 / FR-A-14).
2. **Auth**: `Authorization: Bearer <key>` (preferred) or `X-Api-Key: <key>`. Required on every route except
   `GET /api/v1/health` and `GET /api/v1/health/ready`.
3. **Errors**: RFC 7807 `application/problem+json`, with `traceId` and, when the failure is per-item, an `errors[]`
   extension — the shape `JobEndpoints.Problem` already produces.
4. **Success**: the resource itself as JSON. **No `{success,data,error}` envelope** (D-3): problem details already
   carry failure, and an envelope would rewrite all 210 API tests for no behavioural gain.
5. **JSON**: `System.Text.Json`, `JsonOptions.Default`, camelCase property names, **enums as camelCase strings**,
   money as a JSON number with two decimals, dates as `yyyy-MM-dd`, timestamps as ISO-8601 UTC with `Z`.
6. **Correlation**: every request gets a `requestId` (26-character, sortable, URL-safe). Echoed as the `X-Request-Id`
   response header, present on every log line for that request, and included in every problem detail. An inbound
   `X-Request-Id` is honoured when it matches `^[A-Za-z0-9_-]{8,64}$`, otherwise a new one is generated.
7. **Idempotency**: every mutating route accepts `Idempotency-Key` (§6.5).
8. **Status codes**: `400` malformed or self-inconsistent request · `401` missing/unknown key · `403` key lacks the
   scope · `404` unknown id · `409` state conflict or idempotency replay with a different body · `422` the request was
   understood but a gate refused it · `429` rate limited (with `Retry-After`) · `502` QuickBooks answered with an
   error · `503` QuickBooks/Hermes unreachable, busy, or the app is not ready.

---

## 3. Route table (v1)

| Method & path | Scope | Verdict | Summary |
|---|---|---|---|
| `GET /api/v1/health` | *(none)* | **N** | Liveness + build/runtime facts. Never calls QuickBooks or Hermes. |
| `GET /api/v1/health/ready` | *(none)* | **N** | Readiness: config, prompts, worker, rules/lists readable. |
| `GET /api/v1/health/sdk` | `health:read` | **N** | QuickBooks SDK/COM registration, versions, bitness. No company file opened. |
| `GET /api/v1/health/quickbooks` | `health:read` | **A** | Unchanged body `{ok, companyFile, message}` (FR-16). |
| `GET /api/v1/health/hermes` | `health:read` | **A** | Unchanged body `{ok, model, latencyMs}` (FR-16). |
| `POST /api/v1/quickbooks/connection/test` | `qb:read` | **R** | Full session round trip with per-step timings. |
| `POST /api/v1/quickbooks/company-file/validate` | `qb:read` | **N** | Is *this* company file the right one, open, and backed up? |
| `POST /api/v1/quickbooks/transactions/validate` | `qb:read` | **N** | Would these rows post? Offline; never writes. |
| `POST /api/v1/quickbooks/transactions` | `qb:post` | **N** | Post JSON rows. Returns the batch result. |
| `GET /api/v1/batches/{id}` | `qb:read` | **N** | Batch result (poll target for an async post). |
| `POST /api/v1/batches/{id}/undo` | `qb:post` | **A** | Unchanged (FR-13). |
| `POST /api/v1/quickbooks/lists/sync` | `qb:read` | **R** | Was `POST /qb/sync-lists`; same body. |
| `GET /api/v1/quickbooks/lists` | `qb:read` | **N** | Reads `qb-lists.json` so a caller can pick valid accounts/vendors. |
| `POST /api/v1/jobs` | `jobs:write` | **A** | Unchanged. |
| `POST /api/v1/jobs/validate` | `jobs:read` | **N** | `FolderReader.Validate` + statement read + G1, without queueing. |
| `GET /api/v1/jobs` · `GET /api/v1/jobs/{id}` | `jobs:read` | **A** | Unchanged. |
| `POST /api/v1/jobs/{id}/post` | `jobs:write` | **A** | Unchanged. |
| `POST /api/v1/rules/alias` · `POST /api/v1/rules/account` | `rules:write` | **A** | Unchanged (FR-14). |
| `GET /api/v1/openapi.json` · `GET /api/v1/reference` | *(see FR-A-18)* | **N** | The OpenAPI document and the Scalar reference UI that renders it. |

---

## 4. Health and readiness

### FR-A-1 `GET /api/v1/health` — application health **[N]**

Answers from in-process state only. **Never** touches COM, the network, or a company file, so a monitor may poll it
every few seconds without queueing behind a post.

```json
{
  "ok": true,
  "status": "healthy",
  "version": "1.4.0+33b96b8",
  "environment": "Production",
  "process": { "bitness": "x64", "startedUtc": "2026-09-22T08:00:01Z", "uptimeSeconds": 4210,
               "windowsSession": 9 },
  "quickbooksGateway": "sdk",
  "hermes": "enabled",
  "worker": { "running": true, "activeJobId": null, "queueDepth": 0 }
}
```

- `200` when the host is running. `503` only while the host is shutting down.
- `quickbooksGateway` is `QbConnection.Mode` lower-cased (`sdk|simulated|unavailable|test`).
- **Acceptance**: with `QuickBooks:Fake=true` and Hermes disabled, the body reports `simulated` / `disabled`, and a
  test asserts that no `IQbGateway` or `IHermesClient` call was made while serving it.

### FR-A-2 `GET /api/v1/health/ready` — readiness **[N]**

Cheap local checks that decide whether the app can accept work: settings bound and paths resolvable; prompt library
loaded (or Hermes disabled); `rules.json` parses; `qb-lists.json` readable if present; log folder writable;
`JobWorker` running; startup recovery completed.

```json
{ "ok": false, "checks": [
  { "name": "settings",   "ok": true,  "message": "company 'Tropicana Properties LLC'" },
  { "name": "rules",      "ok": true,  "message": "42 aliases, 17 vendor accounts" },
  { "name": "qb-lists",   "ok": false, "message": "qb-lists.json not found; run lists/sync", "severity": "warning" },
  { "name": "logFolder",  "ok": true,  "message": "D:\\...\\logs writable" },
  { "name": "worker",     "ok": true,  "message": "idle" }
] }
```

`ok` is false iff a check with `severity: "error"` failed; a `warning` keeps `ok: true`. `200` when ok, `503` when not.

### FR-A-3 `GET /api/v1/health/sdk` — QuickBooks SDK health **[N]**

Distinct from the connection test: **it never opens a company file**, so it answers even when QuickBooks is closed,
another company is open, or a certificate dialog is pending. This is the check that explains "why did it fail before
it ever reached QuickBooks".

```json
{
  "ok": true,
  "requestProcessor": { "registered": true, "progId": "QBXMLRP2.RequestProcessor",
                        "serverPath": "C:\\Program Files\\Common Files\\Intuit\\QuickBooks\\QBXMLRP2.DLL",
                        "serverBitness": "x64", "version": "16.0" },
  "process": { "bitness": "x64" },
  "bitnessMatch": true,
  "qbXmlVersion": { "configured": "16.0", "supported": ["13.0", "14.0", "15.0", "16.0"], "ok": true },
  "quickBooksProcess": { "running": true, "name": "QBW.EXE" },
  "message": "QuickBooks Desktop SDK ready"
}
```

- Checks, in order, stopping at the first that fails: COM class registered for the ProgID → server path and bitness
  readable → `OpenConnection2` succeeds → supported qbXML versions readable → configured `QuickBooks:QbXmlVersion` is
  in that list → `CloseConnection`.
- `BeginSession` is **not** called. Nothing is posted, no company file is named.
- `bitnessMatch: false` is an error, not a warning: it is the exact failure T-609 chased on the server.
- `200` when `ok`, `503` otherwise, body identical either way.
- **COM boundary (`CLAUDE.md` rule 2)**: the probe lives in `src/QbAutopost.QuickBooks/QbSdkProbe.cs`, guarded by
  `[SupportedOSPlatform("windows")]`, behind a new Core abstraction `IQbSdkProbe`. On non-Windows (and in every test)
  a fake answers `ok: false, message: "not Windows"`. No `Type.GetTypeFromProgID` outside `QbAutopost.QuickBooks`.
- **Acceptance**: Linux test asserts `ok:false` with a "not Windows" message; a fake probe drives each failure branch
  to its own message; the `QbAutopost.Core` architecture test still finds no COM tokens.

---

## 5. Connection and file validation

### FR-A-4 `POST /api/v1/quickbooks/connection/test` — connection test **[R]**

Everything `GET /health/quickbooks` did, plus per-step timings and an explicit reason, because the handoff §5 defect
("the certificate dialog blocked `BeginSession` for 101 s") was invisible in the old boolean answer.

Request (all fields optional):
```json
{ "companyFile": null, "timeoutSeconds": 180, "includeCompanyInfo": true }
```

Response:
```json
{
  "ok": true,
  "companyFile": "D:\\...\\Tropicana.QBW",
  "companyName": "Tropicana Properties LLC",
  "product": "QuickBooks Enterprise Solutions 24.0",
  "qbXmlVersion": "16.0",
  "steps": [
    { "name": "waitForGateway", "ok": true, "ms": 0 },
    { "name": "openConnection", "ok": true, "ms": 41 },
    { "name": "beginSession",   "ok": true, "ms": 101420,
      "message": "slow: a QuickBooks dialog (certificate/permission) usually causes this" },
    { "name": "hostQuery",      "ok": true, "ms": 88 },
    { "name": "companyQuery",   "ok": true, "ms": 63 },
    { "name": "endSession",     "ok": true, "ms": 12 }
  ],
  "totalMs": 101624,
  "message": "connected"
}
```

- `timeoutSeconds` defaults to `QuickBooks:ConnectionTestTimeoutSeconds` (**new**, default **180**), deliberately
  longer than the 60 s busy timeout so the first call of the day — the one that raises the certificate dialog —
  completes instead of returning `quickbooks-busy` while the session succeeds behind it (handoff §5 defect 2). The cap
  is `Api:MaxConnectionTestTimeoutSeconds` (default 600); a larger value is a `400`.
- A step that exceeds `QuickBooks:BusyTimeoutSeconds` is still reported `ok: true` with the `slow:` message above.
- `waitForGateway` measures time queued behind another QuickBooks call and **is logged** at Information when it
  exceeds 1 s — the "waiting for QuickBooks" line the handoff asks for.
- `companyFile` is refused with `400` unless `QuickBooks:AllowCompanyFileOverride=true` (**new**, default **false**).
  One installation serves one company (Q-44); an override silently posting into another company file is exactly the
  failure "hold, don't guess" exists to prevent. When allowed, the path must be absolute, exist, end in `.QBW`, and be
  inside one of `QuickBooks:AllowedCompanyFolders[]` — otherwise `400`.
- `200` when `ok`, `503` when not; the failing step carries the reason.
- **Acceptance**: a fake gateway with an injected slow `beginSession` produces the `slow:` message, `ok:true`, and one
  "waiting for QuickBooks" log line; a fake that throws `QuickBooksUnavailableException` gives `503` with
  `openConnection.ok:false`; an override without the flag gives `400` and **no** gateway call.

### FR-A-5 `POST /api/v1/quickbooks/company-file/validate` — company file validation **[N]**

"Am I pointed at the right, usable, recently-backed-up company file?" Read-only; posts nothing.

Request: `{ "companyFile": null, "requireBackup": true }`

```json
{
  "ok": false,
  "companyFile": "D:\\...\\Tropicana.QBW",
  "source": "configuration",
  "checks": [
    { "name": "configured",       "ok": true,  "severity": "error",   "message": "Company:FilePath is set" },
    { "name": "pathShape",        "ok": true,  "severity": "error",   "message": "absolute path, .QBW" },
    { "name": "exists",           "ok": true,  "severity": "error",   "message": "file exists, 412.3 MB, modified 2026-09-21T18:04Z" },
    { "name": "readable",         "ok": true,  "severity": "error",   "message": "opened for read" },
    { "name": "openInQuickBooks", "ok": true,  "severity": "error",   "message": "QuickBooks has this file open" },
    { "name": "companyName",      "ok": false, "severity": "error",
      "message": "QuickBooks reports 'Tropicana Properties, LLC'; Company:Name is 'Tropicana Properties LLC'" },
    { "name": "backupFreshness",  "ok": false, "severity": "warning",
      "message": "newest .QBB is 52.4 h old; BackupMaxAgeHours is 36" },
    { "name": "listsSynced",      "ok": true,  "severity": "warning", "message": "qb-lists.json synced 2026-09-20T14:02Z" }
  ],
  "message": "1 error, 1 warning"
}
```

- `ok` is false iff any `severity: "error"` check failed. `200` when ok, `422` when an error check failed, `503` when
  QuickBooks could not be reached at all.
- `openInQuickBooks` compares `gateway.CurrentCompanyFileAsync` to the requested path, normalised (full path,
  case-insensitive on Windows, trailing separators trimmed). A different file open is an **error**, not a warning: it
  is the most dangerous misconfiguration this app has.
- `companyName` compares the SDK's company name with `Company:Name` after trimming and collapsing whitespace; a
  difference in punctuation is reported, never auto-corrected.
- `backupFreshness` reuses the FR-11 backup guard (`BackupFolder`, `BackupMaxAgeHours`). It is a **warning** here and
  stays a hard refusal at post time — validating must not become the thing that lets a stale-backup post through.
- **Acceptance**: each check has a test with a fake gateway and a temp folder; a company file open elsewhere yields
  `422` with `openInQuickBooks.ok:false`; on Linux (`UnconfiguredQbGateway`) the route answers `503` and never throws.

### FR-A-6 `POST /api/v1/quickbooks/transactions/validate` — data validation **[N]**

The dry run of §6. Same request body as `POST /api/v1/quickbooks/transactions` (§6.1) with `dryRun` ignored. It
**never** contacts QuickBooks: names are checked against `qb-lists.json`, duplicates against `ledger.json`. A caller
can therefore validate before the QuickBooks server is even up.

```json
{
  "ok": false,
  "counts": { "submitted": 12, "wouldPost": 9, "held": 2, "skipped": 1, "duplicates": 1 },
  "totals": { "submitted": 4820.17, "wouldPost": 3640.17, "controlTotal": 4820.17, "controlTotalOk": true },
  "rows": [
    { "index": 0, "externalId": "row-7", "requestId": "9f2c1ab4d0e5f678", "outcome": "wouldPost",
      "kind": "Check", "account": "Checking 4521", "lineAccount": "Repairs and Maintenance",
      "payee": "ABC Plumbing LLC", "amount": 184.32, "confidence": "rule" },
    { "index": 3, "externalId": "row-10", "requestId": "1c77...", "outcome": "held",
      "reason": "no-account-rule", "candidates": ["Repairs and Maintenance", "Utilities"] },
    { "index": 9, "externalId": "row-16", "requestId": "3ab0...", "outcome": "duplicate",
      "reason": "already posted 2026-09-18 as TxnID 8A1-...", "batchId": "2026-09-tropicana#1" }
  ],
  "unknownNames": { "accounts": ["Repairs & Maint."], "vendors": [], "customers": [] },
  "qbXml": { "requestCount": 9, "byType": { "CheckAdd": 5, "CreditCardChargeAdd": 3, "DepositAdd": 1 } }
}
```

- `200` when `ok` (nothing held, nothing invalid); `422` otherwise, with the same body — a validator that returns a
  body only on success is useless.
- The validation is **the same code path** as the post: mapping (FR-6) → G3 confidence → G4 duplicates → qbXML build
  (FR-9). Only the final `ProcessRequest` is skipped. A validate that passed followed by a post that holds a line is a
  bug, not a tolerance.
- `qbXml` reports counts only. The qbXML body is returned **only** when the caller sends `?includeQbXml=true` and the
  key has the `qb:debug` scope; it is never logged (spec §14).

### FR-A-7 `POST /api/v1/jobs/validate` — folder validation **[N, optional]**

`FolderReader.Validate` (F1) + statement parse + the G1 reconcile gate, with no job queued and no `output/` written
outside a temp copy. Returns `{ok, errors[], statements[{file,last4,kind,rows,reconcile}], unreadable[]}`;
`200` / `422`. Cheap (the logic exists); include it unless the owner says the folder path is being retired (Q-54).

---

## 6. Posting

Two entrances, one engine. `POST /api/v1/jobs/{id}/post` (folder, unchanged) and `POST /api/v1/quickbooks/transactions`
(JSON rows, new) both converge on: **map (FR-6) → G3 → G4 duplicates → qbXML (FR-9) → SDK post (FR-11) → G5 verify →
ledger (§13) → result**. Undo (FR-13) works on batches from either entrance.

### 6.1 FR-A-8 `POST /api/v1/quickbooks/transactions` — post data **[N]**

```json
{
  "reference": "payroll-2026-09",
  "dryRun": false,
  "controlTotal": 4820.17,
  "allowModelAccounts": false,
  "transactions": [
    {
      "externalId": "row-7",
      "kind": "Check",
      "date": "2026-08-05",
      "amount": 184.32,
      "account": "Checking 4521",
      "lineAccount": "Repairs and Maintenance",
      "payee": "ABC Plumbing LLC",
      "checkNo": "1042",
      "refNumber": "1042",
      "memo": "INV 88213 plumbing repair unit 4",
      "last4": "4521"
    }
  ]
}
```

| Field | Rules |
|---|---|
| `reference` | optional; `^[A-Za-z0-9._-]{1,64}$`. Becomes the batch's job id as `api-<reference>`; omitted → `api-<yyyyMMdd>-<requestId[..8]>`. Must be unique unless `Idempotency-Key` says otherwise (§6.5). |
| `dryRun` | defaults to `Settings.DryRunDefault` (**true**). `true` = FR-A-6 behaviour plus a persisted batch in `ready` state that `POST /api/v1/batches/{id}/post` can later post. |
| `controlTotal` | **required** when `transactions` is non-empty. Must equal the sum of `amount` to the cent. A mismatch is `400 control-total-mismatch` and **nothing is posted** — the API never adjusts an amount to make a total agree (`CLAUDE.md` rule 3). |
| `allowModelAccounts` | default `false`. `true` permits tier 3–4 (Hermes) account choice for rows without `lineAccount`; requires `Hermes:Enabled` and the `qb:post:ai` scope. `false` → an unresolved row is **held**, never guessed. |
| `kind` | `Check \| CcCharge \| CcCredit \| Deposit`. Required. |
| `date` | `yyyy-MM-dd`, required. Outside `QuickBooks:AllowedDateWindow` (**new**, default: not before 2 years ago, not after tomorrow) → row held `date-out-of-window`. |
| `amount` | positive decimal, ≤ 2 decimals, `> 0`, `≤ QuickBooks:MaxLineAmount` (**new**, default 100000.00; a larger value is held, not refused, so one big line cannot fail a whole batch). |
| `account` | the bank/credit-card account the money moves on. Required. Must exist in `qb-lists.json` (exact, ordinal — as Q-35) when lists exist. |
| `lineAccount` | the expense/income account. Optional; resolved by rules (tiers 1–2) when omitted. |
| `payee` | vendor (Check/CcCharge) or customer (Deposit). Optional; aliases from `rules.json` apply. |
| `checkNo`, `refNumber`, `memo`, `last4` | optional; `memo` defaults to a normalised description built from `payee` + `refNumber`. |
| `externalId` | optional caller key, echoed on every row of every response and stored in the ledger. Never used for matching. |

Limits: at most `Api:MaxTransactionsPerRequest` (**new**, default **500**) rows; request body ≤ 2 MB. Exceeding either
is `400`.

**Fingerprint and RequestId** are computed exactly as `docs/spec.md` §7:
`sha256(kind|last4|date|amount(0.00)|direction|checkNo|Normalize(description))`, `RequestId = fingerprint[..16]`.
`direction` is derived from `kind` (`Check`/`CcCharge` → debit, `CcCredit`/`Deposit` → credit), not supplied by the
caller, so two callers describing the same movement produce the same fingerprint and G4 catches the repeat.

Response `200` (or `202`, §6.3):

```json
{
  "batchId": "api-payroll-2026-09#1",
  "status": "posted",
  "dryRun": false,
  "companyFile": "D:\\...\\Tropicana.QBW",
  "counts": { "submitted": 12, "posted": 11, "held": 1, "skipped": 0 },
  "totals": { "submitted": 4820.17, "posted": 4635.85 },
  "posted":  [ { "index": 0, "externalId": "row-7", "requestId": "9f2c1ab4d0e5f678",
                 "txnId": "8A1-...", "editSequence": "...",
                 "kind": "Check", "account": "Repairs and Maintenance", "amount": 184.32 } ],
  "held":    [ { "index": 3, "externalId": "row-10", "requestId": "1c77...", "reason": "no-account-rule",
                 "candidates": ["Repairs and Maintenance", "Utilities"] } ],
  "skipped": [],
  "startedUtc": "2026-09-22T09:14:02Z", "finishedUtc": "2026-09-22T09:14:37Z"
}
```

`status` uses the existing enum: `posted` (nothing held) · `partial` (some held, or the batch was cut short) ·
`failed` (nothing posted) · `ready` (dry run).

### 6.2 FR-A-9 Gates on the direct path

| Gate | Folder path | Direct path |
|---|---|---|
| G1 reconcile | statement balance chain must reconcile | **not applicable** — there is no statement. Replaced by `controlTotal` (§6.1): an arithmetic check the caller can verify, computed by code, never "fixed". |
| G2 job spec | Hermes reads `requirement.txt` | **not applicable** — the request *is* the spec. |
| G3 confidence | held below the threshold | **same**; with `allowModelAccounts:false` tiers 3–4 never run, so an unresolved row is held. |
| G4 duplicates | ledger + `DuplicateWindowDays` | **same**, and this is the main safety net for a caller that retries. |
| G5 verify | statusCode 0 + TxnID + echoed amount equals the submitted amount | **same**. The submitted amount is the caller's number; a mismatch holds the row. |
| Backup guard (FR-11) | refuse when the newest `.QBB` is older than `BackupMaxAgeHours` | **same**: `503 backup-too-old`, nothing posted. |

### 6.3 FR-A-10 Synchronous or asynchronous

QuickBooks calls are serialised process-wide, so a post can wait. The route runs through `JobQueue.RunExclusiveAsync`
(never in parallel with a job) and:

- waits up to `Api:SyncPostTimeoutSeconds` (**new**, default **120**) for the batch to finish → `200` with the full
  result;
- if the wait elapses, returns `202` with `{ batchId, status, location: "/api/v1/batches/<id>" }` and the work
  continues; the caller polls `GET /api/v1/batches/{id}`;
- a caller that always wants the async shape sends `Prefer: respond-async` and gets `202` immediately.

The batch is persisted **before** the first qbXML leaves the process and on every transition (`CLAUDE.md` rule 7):
`Paths:ApiBatches/<batchId>/{request.json, status.json, request.qbxml, response.qbxml, result.json}` — the same audit
set FR-17 requires of a folder job, with `Paths:ApiBatches` defaulting to `<Paths:Ledger folder>/api-batches`.
`request.json` stores the submitted rows verbatim; it is the evidence for "who asked us to post this".

### 6.4 FR-A-11 `GET /api/v1/batches/{id}` **[N]**

Returns the §6.1 response body for any batch — direct or folder-derived — read from `result.json`/`status.json`.
`404` when unknown. A batch still running returns `status: "posting"` with the counts so far.

### 6.5 FR-A-12 Idempotency **[N]**

`Idempotency-Key` (1–128 characters, caller-chosen) on `POST /api/v1/quickbooks/transactions`, `POST /api/v1/jobs`,
`POST /api/v1/jobs/{id}/post` and `POST /api/v1/batches/{id}/undo`:

- first use → processed normally; the key, the SHA-256 of the request body, the client id and the resulting batch id
  are recorded in `Paths:ApiBatches/idempotency.json` (atomic write, kept `Api:IdempotencyRetentionDays`, default 30);
- replay with the **same** body hash → the stored response, `200`, header `Idempotency-Replayed: true`. Nothing is
  sent to QuickBooks;
- replay with a **different** body hash → `409 idempotency-key-reused`;
- replay while the first is still running → `409 idempotency-in-progress` with `Retry-After: 5`.

Without an `Idempotency-Key` a repeat is still caught by G4 (fingerprints) — but G4 *holds* the rows, which surfaces
as a `partial` batch. Callers that retry should send the key.

---

## 7. Security (D-4: remote callers)

Today: one shared key, loopback bind, no TLS. That is safe only because nothing outside the server can reach it. D-4
removes that, so the following are **requirements**, not advice. All are in-house code or framework features — only
OpenAPI needs a package (§10).

### FR-A-13 Per-caller keys and scopes **[N]**

- Callers live in `Paths:Clients` (default `clients.json`, git-ignored, beside `rules.json`):
  `{ "clients": [ { "id": "acme-erp", "name": "Acme ERP", "keyHash": "<base64 sha256>", "keySalt": "<base64>",
  "scopes": ["health:read","qb:read","qb:post"], "enabled": true, "createdUtc": "2026-09-22T00:00:00Z",
  "expiresUtc": null, "allowedCidrs": ["10.0.0.0/24"] } ] }`.
- **Only the hash is stored.** Keys are generated by `scripts/new-api-client.ps1` (**new**), printed once, and never
  written to a log, `output/`, a fixture or the audit trail (`CLAUDE.md` rule 6). Hash = SHA-256 over `salt || key`,
  compared in constant time — the comparison `ApiKeyMiddleware` already does correctly.
- Scopes: `health:read`, `qb:read`, `qb:post`, `qb:post:ai`, `qb:debug`, `jobs:read`, `jobs:write`, `rules:write`,
  `admin`. A missing scope is `403`, logged with the client id and the scope required, never the key.
- Rotation: a client may carry `previousKeyHash` + `previousExpiresUtc` so a caller can roll over without downtime.
- **Back-compat**: the single `Api:ApiKey` keeps working as an implicit client `legacy-shared` with all scopes while
  `Api:AllowLegacyKey=true` (default **true** for one release, warned about at startup, then default false).
- Fail closed, as today: no usable client list **and** no legacy key → startup refuses.

### FR-A-14 Transport **[N]**

- Kestrel serves HTTPS when `Api:Tls:PfxPath` + `Api:Tls:PfxPassword` (environment only) or `Api:Tls:StoreThumbprint`
  is set.
- **Startup refuses** a non-loopback bind without TLS unless `Api:AllowInsecureRemote=true` is set explicitly; that
  flag logs a warning at startup and on every request.
- HSTS on; `X-Content-Type-Options: nosniff`; `Referrer-Policy: no-referrer`; no `Server` header.
- CORS off by default. `Api:Cors:AllowedOrigins[]` opts specific origins in; `*` is refused while any route requires a
  key.

### FR-A-15 Rate limiting **[N]**

ASP.NET Core 8's built-in rate limiter (shared framework, **no package**), partitioned by client id — by remote IP for
the unauthenticated health routes:

| Bucket | Default |
|---|---|
| `qb:post` routes | 10 requests/minute, queue 0 |
| other authenticated routes | 120 requests/minute |
| `GET /api/v1/health*` | 600 requests/minute per IP |
| failed authentication | 10/minute per IP, then `429` for 5 minutes (brute-force brake) |

Per-client overrides live in `clients.json`. `429` carries `Retry-After` and is logged with the client id.

### FR-A-16 Audit trail **[N]**

Every authenticated mutating request appends one JSON line to `Paths:Logs/audit-yyyyMMdd.jsonl`:
`{ "utc", "requestId", "clientId", "remoteIp", "method", "path", "idempotencyKey", "outcome", "status", "batchId",
"jobId", "counts", "totalAmount" }`. Never the key, never the body, never statement text. Retention
`Api:AuditRetentionDays` (default 400 — this is money evidence, so longer than the 31-day operational log).

### FR-A-17 Input hardening **[N]**

Body limit 2 MB (`Api:MaxRequestBodyBytes`); `Api:MaxTransactionsPerRequest` rows; every string field bounded
(`reference` 64, `memo` 4096, names 255) and rejected with `400` when longer. Any path a caller supplies (`folder`,
`companyFile`) must be absolute and inside a configured allow-list (`Paths:AllowedJobRoots[]`,
`QuickBooks:AllowedCompanyFolders[]`) — the direct fix for path traversal now that callers are remote. `POST /jobs`
with a folder outside the allow-list is `400`, logged.

### FR-A-18 API reference exposure **[N]**

Two separate things, deliberately: the **document** (`GET /api/v1/openapi.json`) and the **reference UI**
(`GET /api/v1/reference`, rendered by **Scalar** — not Swagger UI, owner decision 2026-09-22).

- Both are served only when `Api:Reference:Enabled=true` (default **true** in Development, **false** in Production)
  and, in Production, require the `health:read` scope. An interactive API explorer on a machine that can post money
  into a real ledger is opt-in.
- Scalar's "send request" feature is configured **off by default** (`Api:Reference:AllowTryIt`, default `false`). A
  reader of the documentation must not be one click away from posting a live transaction; turning it on is a
  deliberate act on a machine where that is acceptable.
- The UI loads Scalar's assets **from the package, not a CDN** (`Api:Reference:UseCdn`, default `false`). The
  QuickBooks server may have no outbound internet, and a documentation page that silently fetches script from a third
  party is not something this app should do.
- The document declares the security scheme (`Authorization: Bearer`), and every operation declares the scope it
  needs, so the page is also the scope reference.

**Where the document comes from.** Scalar renders an OpenAPI document; it does not produce one, and .NET 8 has no
built-in generator (`AddOpenApi`/`MapOpenApi` are .NET 9; `global.json` pins SDK 8.0.x). Since Swashbuckle — which
would have generated it — is now excluded, the document is **hand-authored and checked in** at
`src/QbAutopost.Api/wwwroot/openapi.json`, served as a static file, and kept honest by a drift test (§11.6): the test
enumerates `EndpointDataSource` and fails when a mapped `/api/v1` route has no documented operation, when a documented
operation has no matching route, or when an operation is missing its summary, response schema or scope. A stale
document then breaks the build rather than misleading a caller.

If the owner later moves the solution to .NET 9, the generator becomes built-in and the hand-authored file is deleted
in the same task — a tracker row, not a silent change.

---

## 8. Logging (extends T-805, spec §14)

Unchanged: Serilog, console + daily rolling file under `Paths:Logs`, fixed sinks, secret scrubbing, `jobId` on every
line. Added:

- `requestId` and `clientId` on every line produced while serving a request (a Serilog `LogContext` scope pushed by
  the request-logging middleware). `jobId` stays; for a direct batch it is that batch's job id.
- One Information line when a QuickBooks call waits more than 1 s for the gateway lock:
  `Waiting for QuickBooks: {WaitMs} ms behind {Holder}` (handoff §5 defect 2).
- The per-request line gains the client: `HTTP POST /api/v1/quickbooks/transactions responded 200 in 34210 ms
  (client acme-erp, 12 rows, 11 posted)`.
- Authentication failures log the client id when known, the remote IP and the scope required — never the key or any
  prefix of it.
- The audit file (FR-A-16) is separate from the operational log and is not subject to `Serilog:MinimumLevel`.

---

## 9. Compatibility and migration

1. Every legacy flat route keeps working while `Api:LegacyRoutes=true` (**default true**). The handler is the same
   delegate; each legacy hit logs `Legacy route {Path} used by {ClientId}; use /api/v1{Path}` at Warning, at most once
   per route per hour.
2. `samples/poc/*`, `steps.md`, `docs/runbook.md`, `scripts/qb-server-check.ps1` and `deploy/start-all.ps1` move to
   `/api/v1` paths in the same task that adds them, so the POC package never depends on the legacy routes.
3. `Api:LegacyRoutes` flips to **false** one release after the owner confirms no caller uses the flat paths. A tracker
   row records the flip; the routes are deleted only after that.
4. `docs/spec.md` §6 gains a pointer to this document in the first task of M9 and is rewritten to the v1 table in the
   last one, so the two never describe the same route differently for longer than one milestone.

---

## 10. Packages

| Package | Why | Status |
|---|---|---|
| `Scalar.AspNetCore` | The reference UI at `/api/v1/reference` (D-3, owner: Scalar, **not Swagger**) | **needs owner approval — Q-46.** `CLAUDE.md` forbids new packages without a tracker question. |
| ~~`Swashbuckle.AspNetCore`~~ | ~~OpenAPI document + Swagger UI~~ | **Excluded by the owner (2026-09-22).** Not to be added, not as a UI and not as a document generator. |
| *(none)* | rate limiting, TLS, HSTS, CORS, auth, audit, idempotency, static file serving | all in the ASP.NET Core 8 shared framework |

The OpenAPI **document** costs no package: it is hand-authored and drift-tested (FR-A-18). `Scalar.AspNetCore` buys
only the rendering, and it must be pinned to a version that targets `net8.0` and ships its assets locally — a version
that only works on .NET 9, or only from a CDN, fails the requirements in FR-A-18 and is not acceptable.

Fallback if Q-46 is refused: keep `GET /api/v1/openapi.json` and the drift test, and ship no UI at all. The document
alone is enough for a caller to generate a client; the UI is a convenience. **No Swagger fallback exists** — if
Scalar cannot be used, the answer is no UI.

---

## 11. Testing

Unchanged rules: xUnit, `Should_<behaviour>_When_<condition>`, fixtures over inline blobs, **no test touches a real
QuickBooks or Hermes**, `dotnet test` green on Linux.

Added:

1. **Contract tests** — one per route: happy path, each documented failure status, and the exact problem-detail
   `title`. A route without a contract test fails a new architecture test.
2. **Auth matrix** — every route × {no key, unknown key, disabled client, expired client, wrong scope, right scope,
   legacy key}. Table-driven, one assertion per case.
3. **Direct-post tests** against `FakeQbGateway`: a control-total mismatch posts nothing; a duplicate row is held with
   the prior TxnID; `allowModelAccounts:false` holds instead of calling Hermes (asserted with a Hermes client that
   throws if called); a G5 amount mismatch holds; `request.json` exists before the first qbXML is sent.
4. **Idempotency tests**: a replay returns the stored body and makes zero gateway calls; a changed body → 409.
5. **SDK-probe tests** run on Linux against the fake and assert the "not Windows" answer; the COM probe itself stays
   `ready-for-human` (server).
6. **OpenAPI drift test**: enumerate `EndpointDataSource` and fail when a mapped `/api/v1` route has no documented
   operation, a documented operation has no route, or an operation lacks its summary, response schema or scope.
   Runs on Linux, needs no package and no browser. Plus: the reference UI answers `404` when
   `Api:Reference:Enabled=false`, and `403` without `health:read` in Production.
7. **Golden files**: qbXML golden tests unchanged — the direct path must produce byte-identical qbXML to the folder
   path for the same logical transaction. This is the test that proves "one engine, two entrances".

Target: both suites green two runs in a row; no test may take a QuickBooks lock.

---

## 12. Configuration added

```jsonc
{
  "Api": {
    "Bind": "https://0.0.0.0:5443",
    "ApiKey": "",                          // legacy shared key; blank once clients.json is used
    "AllowLegacyKey": true,
    "LegacyRoutes": true,
    "AllowInsecureRemote": false,
    "MaxTransactionsPerRequest": 500,
    "MaxRequestBodyBytes": 2097152,
    "SyncPostTimeoutSeconds": 120,
    "MaxConnectionTestTimeoutSeconds": 600,
    "IdempotencyRetentionDays": 30,
    "AuditRetentionDays": 400,
    "Tls":  { "PfxPath": "", "PfxPassword": "", "StoreThumbprint": "" },
    "Cors": { "AllowedOrigins": [] },
    "Reference": { "Enabled": false, "AllowTryIt": false, "UseCdn": false },
    "RateLimits": { "PostPerMinute": 10, "DefaultPerMinute": 120, "HealthPerMinute": 600 }
  },
  "QuickBooks": {
    "ConnectionTestTimeoutSeconds": 180,
    "AllowCompanyFileOverride": false,
    "AllowedCompanyFolders": [],
    "MaxLineAmount": 100000.00,
    "AllowedDateWindow": { "MaxAgeDays": 730, "MaxFutureDays": 1 }
  },
  "Paths": { "Clients": "clients.json", "ApiBatches": "api-batches", "AllowedJobRoots": [] }
}
```

All of these are readable from `QBAUTOPOST__Section__Key`. `Api:Tls:PfxPassword` is **environment only**; a value
found in `appsettings.json` logs a warning and is accepted (refusing would strand an operator mid-deploy) — the
warning names the file, never the value.

**Hosting fix (handoff §5 defect 1)**: the content root becomes `AppContext.BaseDirectory`, so `appsettings.json` is
read from the executable's folder no matter where the process was started. `WebApplicationFactory` tests must stay
green — the test host sets its own content root, so the change is skipped when the environment is `Testing` and is
covered by a test that starts the app from a different working directory.

---

## 13. Proposed milestone M9 (for `docs/plan.md` and `docs/tracker.md`)

| ID | Task | Spec | Notes |
|---|---|---|---|
| T-901 | `/api/v1` route group, legacy routes behind `Api:LegacyRoutes`, content-root fix | §9, §12 | No behaviour change; existing tests re-pointed |
| T-902 | `GET /api/v1/health` + `/health/ready` | FR-A-1, FR-A-2 | Pure in-process |
| T-903 | `IQbSdkProbe` + `QbSdkProbe` (COM) + `GET /api/v1/health/sdk` | FR-A-3 | **[server]** for the COM half |
| T-904 | `POST /quickbooks/connection/test`, gateway-wait logging, longer first-call timeout | FR-A-4 | Closes handoff §5 defect 2 |
| T-905 | `POST /quickbooks/company-file/validate` | FR-A-5 | |
| T-906 | Direct-post request model, fingerprints, control total, mapping reuse | FR-A-8, FR-A-9 | Core, tests first |
| T-907 | `POST /quickbooks/transactions/validate` | FR-A-6 | Shares T-906 |
| T-908 | `POST /quickbooks/transactions`, batch persistence, `GET /batches/{id}` | FR-A-8, FR-A-10, FR-A-11 | |
| T-909 | Idempotency store and middleware | FR-A-12 | |
| T-910 | `clients.json`, scopes, key rotation, `new-api-client.ps1` | FR-A-13 | |
| T-911 | TLS, HSTS, CORS, rate limiting, input hardening, path allow-lists | FR-A-14, FR-A-15, FR-A-17 | |
| T-912 | Audit trail + `requestId`/`clientId` log properties | FR-A-16, §8 | |
| T-913 | Hand-authored `openapi.json` + drift test, then the Scalar UI at `/api/v1/reference` (pending Q-46) | FR-A-18, §10 | Document first: it stands alone if Q-46 is refused |
| T-914 | `GET /quickbooks/lists`, `POST /jobs/validate`, docs/runbook/POC moved to v1 paths | §3, FR-A-7, §9 | |

Order matters: T-901 first (everything else lands in the new group), T-906 before T-907/T-908, T-910 before T-911.

---

## 14. Open questions (proposed for `docs/tracker.md` › Questions)

| # | Question | Conservative choice taken meanwhile |
|---|---|---|
| Q-46 | May we add `Scalar.AspNetCore` for the reference UI? The owner has ruled out Swagger/Swashbuckle entirely (2026-09-22), and `CLAUDE.md` forbids new packages without approval. The OpenAPI document itself needs no package (hand-authored + drift test). | Document shipped either way; the UI waits for the answer. No Swagger fallback (§10) |
| Q-47 | D-4 says remote callers. From where exactly — the same LAN, a VPN, or the public internet? The answer decides whether an IP allow-list suffices or a reverse proxy and managed certificates are needed. | TLS required, per-client CIDR allow-list, `AllowInsecureRemote=false` (§7) |
| Q-48 | Who issues and revokes caller keys, and where do they live between issue and use? `clients.json` on the server is the simplest thing; it is not a secret manager. | `clients.json`, hashes only, script-issued, printed once (FR-A-13) |
| Q-49 | On the direct path there is no statement, so G1 cannot run. Is `controlTotal` an acceptable substitute, and should a mismatch refuse the whole batch (current choice) or post the rows that are internally consistent? | Refuse the whole batch, post nothing (§6.1) |
| Q-50 | May a caller name the company file per request (multi-company by the back door, cf. Q-44)? | No: `AllowCompanyFileOverride=false`, `400` when supplied (FR-A-4) |
| Q-51 | May a direct post use Hermes for account choice (`allowModelAccounts`), or must every direct row carry `lineAccount` or match a rule? | Default `false`; model accounts need an explicit flag **and** the `qb:post:ai` scope (§6.1) |
| Q-52 | How long must batch evidence (`request.json`, qbXML, audit lines) be kept? 400 days is a guess. | 400 days audit, 30 days idempotency, 31 days operational log (§7, §12) |
| Q-53 | When may `Api:LegacyRoutes` default to false and the flat routes be deleted? | Stays `true` until the owner confirms no caller uses them (§9) |
| Q-54 | Does the folder-based job model stay for good, or is the direct API meant to replace it once callers migrate? It decides whether `/jobs/validate` and the folder documentation are worth maintaining. | Both maintained; nothing removed (§1) |

---

## 15. What this does not change

The pipeline, the gates, the qbXML builder, the ledger, undo, rules, Hermes prompts and the POC data are untouched by
this document. If implementing any requirement above appears to need a change to how a transaction is *decided*
rather than how it is *received*, that is a signal the requirement is wrong — stop and raise a tracker question
instead.
