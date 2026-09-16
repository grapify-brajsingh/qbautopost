# QbAutopost.Api — Specification

Version 1.0 · 2026-09-15 · Status: approved for implementation
Companion documents: `plan.md` (how it gets built), `tracker.md` (what is done), `CLAUDE.md` (rules for the coding agent).

This document is the contract. If code and this document disagree, the document wins until it is changed here. Sections marked **MUST** are acceptance criteria; sections marked *assumed* are decisions taken in the absence of an answer (see §16) and can be changed by editing this file.

---

## 1. Purpose

A single .NET 8 application that runs on the QuickBooks Desktop server and turns a **job folder** — a requirement text plus bank/credit-card statements and optional invoices — into posted QuickBooks transactions (Checks, Credit Card Charges/Credits, Deposits), using Hermes AI for the parts that need reading and judgment, and deterministic code for everything that involves money.

It replaces this manual procedure, which today arrives as an email:

```
Company.>Batch Enter Transactions

Transactions Type => Checks (Debit Entries)
Bank Account=From Statement (Last Four Digits)
Date-
Check =Check Number /ACH
Payee=Vendor if ACH – Blank if Check
Account = Account Type
Amount = take from Bank Statement
Memo = written from Bank Statement

Transactions Type => Credit Card
    Credit Card Charges & Credits= From Statement (Last Four)
    Date-
    Payee- ACH
    Account = Account Type
    Amount = take from Credit Card Statement
    Memo = written from Credit Card Statement

Transactions Type > Deposit
    Date =
    Received From = Customer
    Account From = Bank
    Memo = Written from Bank Statement
    Amount = take from Bank Statement
```

## 2. Non-goals (v1)

- No mailbox integration. Input is a folder path.
- No Bills / customer Invoices in QuickBooks (invoices are evidence for categorisation only — *assumed*, see Q1).
- No multi-company job. One folder = one company file.
- No UI. The API and the files in `output\` are the interface.
- No database. State is JSON files and the job folders.

## 3. Definitions

| Term | Meaning |
|---|---|
| Job | One folder processed once. Id = folder name. |
| Requirement | `requirement.txt`, the template above with real values. |
| Statement | A bank or card statement file: `.csv`, `.xlsx`, `.pdf`. |
| Line | One transaction row from a statement, normalised (`StatementLine`). |
| Mapped line | A line after mapping to QuickBooks objects (`MappedTxn`). |
| Batch | The set of mapped lines posted in one SDK message set; id = job id + attempt. |
| Gate | A hard validation that holds only what failed (G1–G5). |
| Held | A line/statement that will not be posted until a person acts. |
| Ledger | `ledger.json`: every posted line with its `TxnID` and fingerprint. |
| Hermes | The Hermes Agent container's OpenAI-compatible API on `127.0.0.1:8642`. |
| SDK | QuickBooks Desktop SDK, `QBXMLRP2.RequestProcessor` COM object, qbXML payloads. |

## 4. Deployment context

- Windows server hosting QuickBooks Desktop Pro/Premier/Accountant (qbXML 13.0 assumed; 16.0 on 2023+).
- The app runs in an **interactive session** (Task Scheduler at logon of an auto-logon account), never as a Windows Service. Process bitness matches QuickBooks (x64 for 2022+, x86 older).
- Hermes runs in Docker on the same server (WSL 2 + Docker Engine on Windows Server; Docker Desktop on Windows 10/11). Port 8642 published on `127.0.0.1` only; `API_SERVER_KEY` required.
- The app **MUST** build and its Core tests **MUST** run on any OS (Linux/macOS dev box, CI). Only the `QbAutopost.QuickBooks` project touches COM and is Windows-only at runtime.

## 5. Folder contract

```
<jobFolder>\
├── requirement.txt              REQUIRED. UTF-8 text.
├── statements\                  REQUIRED. ≥ 1 file: *.csv | *.xlsx | *.pdf
├── invoices\                    OPTIONAL. *.pdf | *.png | *.jpg
└── output\                      CREATED BY THE APP (never read as input)
    ├── status.json              { status, updatedUtc, error?, attempt }
    ├── spec.json                T1 result + validation
    ├── statements\<file>.rows.json
    ├── invoices\<file>.json
    ├── hermes\<task>-<n>.request.json / .response.json   audit copies of every model call
    ├── analysis.json
    ├── request.qbxml
    ├── response.qbxml
    ├── result.json
    └── batch-enter-checks.csv | batch-enter-creditcard.csv | batch-enter-deposits.csv
```

**MUST:**
- F1. `POST /jobs` returns 400 if `requirement.txt` is missing, `statements\` is missing or empty, or the path is not an absolute existing directory.
- F2. Files in `statements\` whose extension is not csv/xlsx/pdf are reported in `result.json.unreadable[]` with reason `unsupported-extension`; the job continues.
- F3. A job id already present in `ledger.json.jobs[]` is refused with 409 unless `force=true`.
- F4. Nothing under `output\` is ever treated as input, even on re-run.
- F5. Last-four detection: from the file name (`(?<!\d)(\d{4})(?!\d)`), else from the statement content (T2 `accountLast4`, or CSV layout `Last4`). No last-four → the statement is held with reason `unknown-account`.

## 6. API

Base: `http://<bind>:<port>` (default `127.0.0.1:5080`). Header `X-Api-Key: <key>` required on every route except `GET /health/*`. Errors use RFC 7807 problem details.

| Method & path | Request | Response | Rules |
|---|---|---|---|
| `POST /jobs` | `{ "folder": "C:\\qb-jobs\\2026-08-tropicana", "dryRun": true, "force": false }` | `202 { "jobId": "2026-08-tropicana", "status": "queued" }` | F1, F3. `dryRun` defaults to `Settings.DryRunDefault` (true). |
| `GET /jobs/{id}` | — | `200 JobView` (below) | 404 if unknown. |
| `POST /jobs/{id}/post` | — | `202` | Only from `ready`. 409 otherwise. |
| `GET /jobs` | `?status=` | `200 [JobSummary]` | In-memory + `status.json` scan of known folders. |
| `POST /batches/{id}/undo` | — | `200 { "deleted": n, "failed": [{ "txnId", "message" }] }` | `TxnDelRq` per ledger entry; marks entries `undone`. |
| `POST /rules/alias` | `{ "fragment": "UNKNOWN PLUMBER", "name": "Unknown Plumber LLC", "kind": "vendor|customer" }` | `200` | Writes `rules.json`. |
| `POST /rules/account` | `{ "vendor": "Unknown Plumber LLC", "account": "Repairs and Maintenance" }` | `200` | Writes `rules.json`. 400 if account not in `qb-lists.json` (when lists exist). |
| `POST /qb/sync-lists` | — | `200 { accounts, vendors, customers, missingInRules[] }` | Writes `qb-lists.json`. |
| `GET /health/quickbooks` | — | `200 { ok, companyFile, message }` | `HostQuery`. 503 when not ok. |
| `GET /health/hermes` | — | `200 { ok, model, latencyMs }` | One 5-token completion. 503 when not ok. |

`JobView`:
```json
{
  "jobId": "2026-08-tropicana",
  "status": "ready",                       // queued|analysing|ready|posting|posted|partial|failed
  "dryRun": true,
  "company": "Tropicana Properties LLC",
  "statements": [ { "file": "chase-checking-4521.pdf", "last4": "4521", "kind": "bank", "rows": 41, "reconcile": { "ok": true, "message": "…" } } ],
  "counts": { "toPost": 38, "held": 2, "skipped": 1 },
  "totals": { "toPost": 12480.55 },
  "held":    [ { "requestId": "…", "date": "2026-08-05", "amount": 420.00, "description": "…", "reason": "…", "candidates": ["…"] } ],
  "skipped": [ { "requestId": "…", "reason": "…" } ],
  "posted":  [ { "requestId": "…", "txnId": "…", "kind": "Check", "account": "…", "amount": 184.32 } ],
  "batchId": "2026-08-tropicana#1",
  "error": null
}
```

Job state machine (**MUST**): `queued → analysing → (failed | ready | posting)`; `ready → posting` only via `POST /jobs/{id}/post`; `posting → posted | partial`; `posted|partial → undone` via undo. `status.json` is written on every transition. On startup, any job found in `analysing` is set to `failed (interrupted)`; any job found in `posting` is set to `partial (interrupted — run duplicates before re-post)`.

## 7. Domain model (Core)

Ported from the POC (`poc/QbAutopost`), namespaces `QbAutopost.Core.*`.

- `SourceKind { Bank, Card }`, `Direction { Debit, Credit }`, `TxnKind { Check, CcCharge, CcCredit, Deposit, Skip }`, `Confidence { Rule, History, Invoice, Model, Holding, Hold }`.
- `StatementLine { SourceFile, Kind, Last4, LineNo, Date, Description, Direction, Amount (positive), CheckNo?, Balance?, Fingerprint }` — `Fingerprint = sha256(kind|last4|date|amount(0.00)|direction|checkNo|Normalize(description))`, lower-hex.
- `MappedTxn { Line, Kind, Account, Payee?, LineAccount, RefNumber, Confidence, Tier, Note?, Candidates[]?, InvoiceRef? }`; `RequestId = Fingerprint[..16]`; `Memo = Line.Description`.
- `JobSpec { Company, Kinds, BankLast4[], CardLast4[], FieldRules }`.
- `InvoiceFacts { File, Party, Role (vendor|customer), Number?, Date?, Total, CategoryHint?, MatchedRequestId? }`.
- `Batch { Id, JobId, Company, ToPost[], Held[], Skipped[], QbXml, Results[] }`, `PostResult { RequestId, StatusCode, StatusMessage, TxnId?, EditSequence?, Amount? }`.
- `Rules` (§11), `Ledger` (§13), `QbLists { SyncedUtc, Accounts[], Vendors[], Customers[] }`.

## 8. Pipeline — functional requirements

Each FR lists behaviour, then acceptance criteria. "Held" always means: removed from the postable set, recorded in `analysis.json`/`result.json` with `reason`, and never silently dropped.

### FR-1 Folder validation
See §5 F1–F5. Runs synchronously inside `POST /jobs`.

### FR-2 Requirement → job spec (Hermes T1) + gate G2
- Send `requirement.txt` to Hermes with prompt `spec`. Parse into `JobSpec` (schema §9.1).
- Placeholder handling: a company value of `Company` (the template's placeholder) is ignored and `Settings.Company.Name` is used.
- If T1 fails validation twice, fall back to the deterministic `RegexSpecParser` from the POC (types + last-four by regex) and record `spec.json.source = "regex-fallback"`.
- **G2 MUST hold the job (`failed`) when:** no kinds resolved; a stated last-four has no statement file; a statement file's last-four is not stated in the requirement *and* not registered in `rules.json` accounts.
- AC: on `samples/jobs/2026-08-tropicana`, `spec.json` has kinds `[Check, CcCharge, CcCredit, Deposit]`, bank `["4521"]`, card `["7788"]`.

### FR-3 Statement extraction
- `.csv` → `CsvStatementParser` (POC logic, layouts from `rules.json.CsvLayouts`; unknown layout → statement held, reason `unknown-csv-layout`).
- `.xlsx` → `XlsxStatementParser`: first worksheet → rows of strings → same layout logic as CSV.
- `.pdf` → `PdfText` (PdfPig). If extracted text per page < 40 characters → page is scanned → `Ocr` (Tesseract, `Settings.Ocr.Enabled`; if disabled → statement held, reason `scanned-pdf-ocr-disabled`). Text → Hermes T2 (schema §9.2) → rows.
- Every row becomes a `StatementLine`; `Amount` positive; `Direction` from sign/columns; `Date` parsed with the layout's format, else invariant `DateOnly.TryParse`.
- Writes `output/statements/<file>.rows.json` `{ file, last4, kind, layout|"hermes-t2", rows[], reconcile }`.
- AC: the POC sample CSVs produce 7 and 4 rows respectively; a fixture PDF text file produces the same rows as its expected JSON.

### FR-4 Gate G1 — statement reconciles
- Running-balance chain (POC logic, both orientations) when a balance column exists.
- For T2 output: `|opening + Σcredits − Σdebits − closing| ≤ 0.01`; `rows.length == transactionCount` when `transactionCount` is not null; all dates within `[periodStart, periodEnd]` when given.
- CSV/XLSX without balance and without totals: gate passes with message `not-verifiable` and the statement is flagged `reconcile.verified = false` (posting still allowed — *assumed*).
- **MUST:** a statement that fails G1 is held whole; other statements continue.

### FR-5 Invoices (Hermes T3) + matching
- Each file in `invoices\` → text (PdfText/Ocr; images → Ocr) → Hermes T3 → `InvoiceFacts` (schema §9.3).
- Matching: candidate lines with `|line.Amount − invoice.Total| ≤ 0.005` and `|days| ≤ Rules.InvoiceMatchDays` (default 5). One candidate → match. Several → prefer same-direction (vendor↔debit, customer↔credit), then highest payee similarity ≥ `Rules.FuzzyThreshold`; ties → no match, reason `ambiguous`.
- A matched invoice sets `MappedTxn.InvoiceRef` and supplies `Payee` (if the line has none) and Tier 3 account evidence.
- Unmatched invoices → `result.json.unmatchedInvoices[]`.

### FR-6 Mapping
Applies the requirement's field rules exactly (POC `Mapper`, extended):

| Situation | Kind | RefNumber | Payee | LineAccount |
|---|---|---|---|---|
| Card line matches `SkipPatterns` | Skip | — | — | — |
| Card debit | CcCharge | `ACH` | vendor | tiers |
| Card credit | CcCredit | `ACH` | vendor | tiers |
| Bank credit | Deposit | — | customer (required) | `Rules.DepositIncomeAccount` (*assumed*, Q2) |
| Bank debit matching `TransferPatterns` | Check | checkNo ?? `ACH` | none | the pattern's liability/bank account |
| Bank debit with check number | Check | checkNo | **none** | keyword rule ?? `HoldingExpenseAccount` (Confidence `Holding`) |
| Bank debit, no check number | Check | `ACH` | vendor | tiers |

`Account` (header) = `Rules.BankAccounts[last4]` or `Rules.CardAccounts[last4]`; missing → held `unknown-account`.

Payee resolution: alias table (substring on normalised description) → fuzzy against `QbLists` names (+ `Rules.VendorAccounts` keys + ledger payees) with score ≥ `FuzzyThreshold` → else none. Lines that require a payee and have none are held with reason `unknown-payee` and `candidates` = top-3 fuzzy names.

LineAccount tiers (first hit wins; `Tier` recorded):
1. `VendorAccounts[payee]` or `KeywordAccounts` match → Confidence `Rule`.
2. Ledger history: ≥ `HistoryMinCount` postings for the payee, top account ≥ 3× the runner-up → `History`.
3. Matched invoice with `CategoryHint` → Hermes T4 with the hint, accepted if confidence ≥ `ModelConfidenceThreshold` → `Invoice`.
4. Hermes T4 without invoice → `Model`.

### FR-7 Gate G3 — confidence
- **MUST post:** `Rule`, `History`, `Invoice` (≥ threshold), `Holding`.
- **MUST hold:** `Model` unless (confidence ≥ threshold **and** the payee has ≥ 1 prior posting to the same account); any `Hold`.
- Held lines carry `candidates` (top-3 accounts from T4 or fuzzy) so a one-line `POST /rules/…` resolves them.

### FR-8 Gate G4 — duplicates
- Ledger: same fingerprint → skipped, reason `already-posted`.
- QuickBooks (only when posting, not in dry run): per `(kind, account)` one query over `[minDate−W, maxDate+W]`, `W = Settings.QuickBooks.DuplicateWindowDays` (3). Same amount within W days: exact (same date and (same non-ACH RefNumber or same payee)) → skipped `already-in-quickbooks`; otherwise held `possible-duplicate`.

### FR-9 qbXML build
- POC `QbXml` builder, element order exactly: `CheckAdd(AccountRef, PayeeEntityRef?, RefNumber, TxnDate, Memo, IsToBePrinted=false, ExpenseLineAdd(AccountRef, Amount, Memo))`; `CreditCardChargeAdd|CreditCardCreditAdd(AccountRef, PayeeEntityRef?, TxnDate, RefNumber, Memo, ExpenseLineAdd)`; `DepositAdd(TxnDate, DepositToAccountRef, Memo, DepositLineAdd(EntityRef?, AccountRef, Memo, Amount))`.
- `requestID = RequestId`; `onError="continueOnError"`; XML-escaped; amounts `0.00` invariant; `RefNumber` ≤ 11 chars; `Memo` ≤ 4095.
- Writes `request.qbxml` and the three paste-ready CSVs even in dry run.

### FR-10 Dry run and post
- `dryRun=true` → after FR-9 the job is `ready`. `POST /jobs/{id}/post` re-runs FR-6…FR-9 (rules may have changed), then posts.
- `dryRun=false` → posts immediately after FR-9.

### FR-11 Posting through the SDK
- `IQbGateway.Process(qbxml)` via `QbSession` (POC): `OpenConnection2("", AppName, localQBD=1)`, `BeginSession(companyFile, DoNotCare=2)`, `ProcessRequest`, `EndSession`, `CloseConnection`. One session per call. Calls are serialised by the single worker.
- A `COMException` is retried once after 5 s. A call exceeding `Settings.QuickBooks.BusyTimeoutSeconds` (60) is abandoned and the job goes `partial` with reason `quickbooks-busy` (the request may or may not have been applied → G4 on next attempt handles it).
- Refuse to post when `Settings.QuickBooks.BackupFolder` is set and its newest `.QBB` is older than `BackupMaxAgeHours` (36): job `failed`, reason `backup-too-old`.

### FR-12 Gate G5 — verify, ledger, result
- Parse `*AddRs`: `statusCode`, `TxnID`, `EditSequence`, `Amount|DepositTotal`. A line is posted iff `statusCode==0`, `TxnID` present, and echoed amount (when present) equals the statement amount. Others → held with the SDK message.
- Ledger records every posted line (§13) and the batch; `result.json` written; `status` = `posted` if nothing held, else `partial`.

### FR-13 Undo
- `POST /batches/{id}/undo` → one message set of `TxnDelRq(TxnDelType, TxnID)` for all not-yet-undone entries; mark `undone` per successful `statusCode 0`; batch `undone` when all succeeded.

### FR-14 Rules teaching
- `POST /rules/alias` writes `PayeeAliases` or `CustomerAliases`; `POST /rules/account` writes `VendorAccounts`. Both re-load rules for subsequent jobs.

### FR-15 Sync lists
- `AccountQuery`, `VendorQuery`, `CustomerQuery` (ActiveOnly) → `qb-lists.json`; response lists every account named in `rules.json` that QuickBooks does not have.

### FR-16 Health
- `/health/hermes`: `POST /v1/chat/completions` with a 5-token prompt; ok iff 200 and non-empty content. `/health/quickbooks`: `HostQuery` status 0.

### FR-17 Audit
- Every Hermes request/response saved under `output/hermes/`; every SDK request/response saved as `request.qbxml`/`response.qbxml` (queries as `query-<n>.qbxml`). Secrets never written.

## 9. Hermes integration

Client: `IHermesClient.CompleteJsonAsync<T>(task, systemPrompt, userContent, schemaHint, ct)` →
- `POST {BaseUrl}/v1/chat/completions`, header `Authorization: Bearer {ApiKey}`, body `{ model, temperature: 0, messages: [system, user] }`, timeout `TimeoutSeconds` (120).
- Response content → strip code fences → `JsonSerializer.Deserialize<T>` → `Validate(T)`; on failure, one retry with the validation errors appended to the user message; second failure → `HermesValidationException` (caller decides: hold or fallback).
- Prompts are files under `Hermes/prompts/*.md` loaded at startup; `{{placeholders}}` filled by code.

### 9.1 T1 `spec` → `JobSpec`
```json
{ "company": "string|null", "kinds": ["Check","CreditCard","Deposit"], "bankLast4": ["4521"], "cardLast4": ["7788"],
  "fieldRules": { "check": {…}, "card": {…}, "deposit": {…} } }
```
Validation: kinds ⊆ known; every last-four is exactly 4 digits.

### 9.2 T2 `statement` → `StatementExtraction`
```json
{ "accountLast4": "4521", "kind": "bank|card", "periodStart": "2026-08-01", "periodEnd": "2026-08-31",
  "openingBalance": 13195.87, "closingBalance": 10230.45, "transactionCount": 7,
  "rows": [ { "date": "2026-08-22", "description": "HOME DEPOT #4521 NOIDA", "direction": "debit|credit", "amount": 184.32, "checkNo": null, "balance": 9980.45 } ] }
```
Validation: amounts > 0; dates ISO; then G1 (FR-4). Input is chunked per page group when text > 60 000 characters; chunks are merged by row order and the balance check runs on the merged set.

### 9.3 T3 `invoice` → `InvoiceFacts`
```json
{ "party": "Home Depot", "role": "vendor|customer", "number": "88213", "date": "2026-08-21", "total": 184.32, "categoryHint": "plumbing fittings, repair" }
```
Validation: total > 0; role ∈ set.

### 9.4 T4 `account` → `AccountChoice`
Input: line (date, description, amount, direction, kind), payee, invoice hint, and `accounts[]` = `QbLists.Accounts` filtered to expense/income/COGS/other-expense types when types are known, else all.
```json
{ "account": "Repairs and Maintenance", "confidence": 0.82, "reason": "…", "alternatives": ["…", "…"] }
```
Validation: `account ∈ accounts[]` (case-sensitive exact) else reject; `0 ≤ confidence ≤ 1`.

## 10. Output files

- `analysis.json`: `{ jobId, company, spec, lines: [ { requestId, file, lineNo, date, description, amount, direction, kind, account, payee, lineAccount, refNumber, tier, confidence, reason, candidates, invoiceRef, decision: "post|hold|skip" } ] }`.
- `result.json`: `{ jobId, batchId, status, dryRun, counts, totals, posted[], held[], skipped[], unreadable[], unmatchedInvoices[], reconcile: [per statement], startedUtc, finishedUtc }`.
- Paste-ready CSVs: columns as in POC `BatchEnterSheet`.

## 11. `rules.json`

POC schema plus: `InvoiceMatchDays` (5), `ModelConfidenceThreshold` (0.8), `CustomerAliases`, `TransferPatterns[] { Match, Account }`, `SkipPatterns[]`, `CsvLayouts{}`. A rule change takes effect on the next job or re-post.

## 12. Configuration (`appsettings.json`)

```json
{
  "Api": { "Bind": "http://127.0.0.1:5080", "ApiKey": "change-me" },
  "DryRunDefault": true,
  "Company": { "Name": "Tropicana Properties LLC", "FilePath": "C:\\...\\Tropicana.QBW", "RulesFile": "rules.json" },
  "QuickBooks": { "AppName": "QbAutopost", "QbXmlVersion": "13.0", "DuplicateWindowDays": 3, "BusyTimeoutSeconds": 60, "BackupFolder": "", "BackupMaxAgeHours": 36 },
  "Hermes": { "BaseUrl": "http://127.0.0.1:8642", "ApiKey": "…", "Model": "default", "TimeoutSeconds": 120 },
  "Ocr": { "Enabled": false, "TessDataPath": "" },
  "Paths": { "Ledger": "C:\\qb-autopost\\ledger.json", "QbLists": "C:\\qb-autopost\\qb-lists.json", "Logs": "C:\\qb-autopost\\logs" }
}
```
Secrets may also come from environment variables (`QBAUTOPOST__Hermes__ApiKey`).

## 13. Ledger (`ledger.json`)

`{ jobs: [ { jobId, batchId, postedUtc, posted, held, skipped, txnIds[], undone } ], posted: [ { batchId, jobId, fingerprint, txnId, editSequence, kind, account, payee, lineAccount, amount, date, refNumber, memo, sourceFile, lineNo, undone } ] }`. Written atomically (temp file + rename) after every batch and every undo.

## 14. Non-functional

- Single worker; a job with 300 lines and a cloud model completes in < 10 minutes.
- Logging: Serilog to console + rolling file under `Paths.Logs`; one correlation id per job in every line; no secrets, no full statement text at Information level.
- Reliability: every state transition persisted; startup recovery per §6.
- Security: loopback bind by default; API key; Hermes key only in config/env; no inbound port beyond the API.
- Cross-platform Core: no `System.Runtime.InteropServices.COM*` outside `QbAutopost.QuickBooks`.

## 15. Testing

- `tests/QbAutopost.Core.Tests` (xUnit): parsers (CSV/XLSX/PDF-text fixtures), fingerprint, mapper routing table (one test per row of the FR-6 table), tiers, G1/G3/G4/G5, qbXML builder (golden files), response parsers, ledger, invoice matcher.
- `tests/QbAutopost.Api.Tests`: `WebApplicationFactory` with `FakeHermesClient` (canned JSON from `tests/fixtures/hermes/`) and `FakeQbGateway` (records requests; returns synthetic `*AddRs` with sequential TxnIDs; can be told to fail line N or hang). Covers the full state machine, dry-run → post, undo, rules endpoints, startup recovery.
- **MUST:** no test contacts a real Hermes or QuickBooks. `dotnet test` passes on Linux.
- Manual verification on the server (tracked in `tracker.md`, M6/M8): `/health/*`, sync-lists, a dry run on a real folder, a post to a **copy** of the company file, undo.

## 16. Open questions and assumed answers

| # | Question | Assumed for v1 |
|---|---|---|
| Q1 | Are invoices only evidence, or also entered as Bills/Invoices? | Evidence only. |
| Q2 | Deposits: do customers have open invoices (→ ReceivePayment + Deposit)? | No; plain `DepositAdd` to `DepositIncomeAccount`. |
| Q3 | Any scanned statements? | No; OCR disabled by default, code path present. |
| Q4 | Windows edition → container runtime? | Windows Server 2022 → WSL 2 + Docker Engine. |
| Q5 | Model behind Hermes? | Cloud provider key configured in the container. |
| Q6 | One folder = one company? | Yes. |
