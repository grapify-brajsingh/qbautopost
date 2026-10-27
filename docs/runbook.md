# QbAutopost — Operator Runbook

Version 1.0 · 2026-09-17 · Audience: the person who installs, runs and supports QbAutopost on the QuickBooks server.
Related: `spec.md` (the contract), `tracker.md` (open questions Q-1…Q-41 and the T-609 server checklist), `deploy/README-hermes.md` (Hermes container).

> **Status.** The QuickBooks (COM) path has only run against a simulated company so far. Do the T-609 checklist
> in `tracker.md` on a **copy** of the company file before the first real post.

---

## 1. What it does

QbAutopost is one Windows program with an HTTP API on `http://127.0.0.1:5080`. Every route is served under
**`/api/v1`** (`docs/spec-api-v1.md` is the route contract). The flat paths of the first release (`jobs`,
`health/…` without the prefix) still answer while `Api:LegacyRoutes` is `true`, and each use is logged as a warning; write new callers
against `/api/v1`. You give it a **job folder**:

```
C:\qb-jobs\2026-08-tropicana\
├── requirement.txt          the "Batch Enter Transactions" instructions, with real values
├── statements\              bank / card statements: *.csv, *.xlsx, *.pdf  (at least one)
└── invoices\                optional: *.pdf, *.png, *.jpg (used only as evidence)
```

It reads the files, decides every line, and either stops at **ready** (dry run, the default) so you can review, or posts
Checks, Credit Card Charges/Credits and Deposits to QuickBooks Desktop. Everything it produced is in the job's
`output\` folder. Hermes (a local AI service) reads the requirement, PDF statements and invoices, and suggests accounts;
it never decides an amount. Anything uncertain is **held**, never guessed.

Job states: `queued → analysing → ready → posting → posted | partial`, or `failed`. An undone batch makes the job `undone`.

## 2. Install

### 2.1 Server prerequisites

| Item | Requirement |
|---|---|
| Windows | The QuickBooks Desktop server (Windows Server 2022 or Windows 10/11) |
| QuickBooks | Desktop Pro/Premier/Accountant with the company file; SDK support (qbXML 13.0) |
| Bitness | The app **must** run with the same bitness as QuickBooks: **x64 for QuickBooks 2022 and later, x86 for older**. A mismatch shows as "QBXMLRP2.RequestProcessor is not registered for this x64 process" |
| .NET | .NET 8 ASP.NET Core Runtime of that bitness (x64 or x86) |
| Hermes | Docker (WSL 2 + Docker Engine on Windows Server; Docker Desktop on Windows 10/11), see `deploy/README-hermes.md` |
| Account | A Windows account with auto-logon. The app runs **in that interactive session** (Task Scheduler at logon), never as a Windows service, because QuickBooks' SDK needs a desktop session |

### 2.2 Folders

| Path | Holds |
|---|---|
| `C:\qb-autopost\app\` | the published program |
| `C:\qb-autopost\rules.json` | mapping rules (§3.3) |
| `C:\qb-autopost\ledger.json` | every posted line and batch (do not edit by hand) |
| `C:\qb-autopost\qb-lists.json` | cached QuickBooks accounts / vendors / customers |
| `C:\qb-autopost\jobs.json` | index of known job folders |
| `C:\qb-autopost\clients.json` | API callers: id, name, scopes and a **salted hash** of each key, never a key (§3.4) |
| `C:\qb-autopost\api-batches\` | one folder per direct post: its request, state and qbXML, plus `idempotency.json` |
| `C:\qb-autopost\qb-audit\` | qbXML copies of list syncs and undos without a job folder |
| `C:\qb-autopost\logs\` | `qbautopost-yyyyMMdd.log`, one per day, 31 kept, **and** `audit-yyyyMMdd.jsonl`, 400 days (§8.7) |
| `C:\qb-jobs\` | job folders (any location works; the API takes an absolute path — see `Paths:AllowedJobRoots`, §3.5) |

### 2.3 Publish the program

On a build machine with the .NET 8 SDK (repository root):

```powershell
dotnet publish src/QbAutopost.Api -c Release -r win-x64 --self-contained false -o C:\qb-autopost\app
# QuickBooks older than 2022 (32-bit):  -r win-x86
```

Copy `rules.json` (start from `samples/rules.json`) to `C:\qb-autopost\rules.json`.

### 2.4 Start at logon

1. Start Hermes and check it: `deploy/README-hermes.md`.
2. `deploy/start-all.ps1` starts Docker (`-DockerMode Wsl` on Windows Server, the default, with `-WslDistro Ubuntu`; `-DockerMode Desktop` on Windows 10/11), runs `docker compose up -d` in `deploy\hermes`, waits until Hermes accepts connections on `127.0.0.1:8642`, starts `C:\qb-autopost\app\QbAutopost.Api.exe` (`-AppExe`) unless it already runs, then waits for `GET /api/v1/health/ready` = 200. The script holds no key, so it can only call the two key-free routes; Hermes is proved by its own port, not through the API (`/api/v1/health/hermes` needs `health:read` — use `scripts\qb-server-check.ps1 -Step health` for the dependency checks). If Hermes stays down, the API is still started (undo and `/api/v1/health/quickbooks` work; new jobs fail at T1) and the script exits 2. Exit 1 = Docker, compose or the API could not be started, or it never became ready. It writes `C:\qb-autopost\logs\start-all-yyyyMMdd.log` and never reads a key.
3. `deploy/install-task.ps1` (run once as the auto-logon account, in an elevated PowerShell) registers the task `QbAutopost`: at logon of that account, 60 s delay, **interactive** session, limited rights, no password stored, no time limit, a second start ignored. Pass start-all options with `-StartAllArguments '-DockerMode Desktop'`; remove it with `-Unregister`.
4. Both scripts accept `-WhatIf` (prints what would happen, changes nothing). Test the task with `Start-ScheduledTask -TaskName QbAutopost`, then sign out and in once.

### 2.5 Going live and more than one company

- A new install posts nothing by itself: `DryRunDefault` is `true`. Go live by setting it to `false` only after the shadow week (tracker T-803/T-804 checklists). A request's own `dryRun` always wins.
- One installation serves **one** company (`Company:*` holds one name and one file) and `start-all.ps1` starts one API. Running several companies on one server is not supported yet (tracker Q-44); until the owner decides, switch companies one at a time.

## 3. Configure

### 3.1 `appsettings.json` (next to the program)

| Key | Default | Meaning |
|---|---|---|
| `Api:Bind` | `http://127.0.0.1:5080` | Keep it on loopback. A non-loopback bind without TLS **stops startup** unless `Api:AllowInsecureRemote` is on (§3.5) |
| `Api:ApiKey` | `change-me` | The shared all-scopes key. The host refuses to start when no caller can authenticate at all — see `Api:AllowLegacyKey` and §3.4 |
| `Api:AllowLegacyKey` | `true` | Accept `Api:ApiKey` as an implicit all-scopes caller. Set **false** once every caller has its own key (§3.4) |
| `Api:LegacyRoutes` | `true` | Also answer the pre-M9 flat paths beside `/api/v1/*`; each use logs a warning once per route per hour |
| `Api:Reference:Enabled` | `false` | Serve `GET /api/v1/openapi.json` and the reference page at `/api/v1/reference` (§3.6) |
| `Api:Reference:AllowTryIt` / `UseCdn` | `false` / `false` | Show the page's "send request" buttons; load its script from a public CDN instead of the package's own copy |
| `Api:RateLimits:*` | see §3.5 | Requests a minute per caller, and the brute-force brake on wrong keys |
| `Api:MaxRequestBodyBytes` | `2097152` | Largest request body (2 MB); over it is `413` |
| `Api:MaxTransactionsPerRequest` | `500` | Rows in one `POST /api/v1/quickbooks/transactions`; over it the **whole batch is refused**, never truncated |
| `Api:SyncPostTimeoutSeconds` | `120` | How long a direct post holds the caller's connection before it answers `202` and a batch to poll |
| `Api:IdempotencyRetentionDays` | `30` | How long an `Idempotency-Key` is remembered |
| `Api:AuditRetentionDays` | `400` | How long `audit-yyyyMMdd.jsonl` is kept; `0` or less keeps everything for ever (§8.7) |
| `Api:Tls:PfxPath` / `StoreThumbprint` | empty | Serve HTTPS from a `.pfx` or a certificate in the machine store. The password is **environment only**: `QBAUTOPOST__Api__Tls__PfxPassword` |
| `Api:Cors:AllowedOrigins` | `[]` | Browser origins allowed to call the API. `*` is refused at startup, not ignored |
| `DryRunDefault` | `true` | A job without `dryRun` stops at `ready` |
| `Company:Name` | | Must match the company named in `requirement.txt` (case and punctuation ignored), or the job fails G2 |
| `Company:FilePath` | | Full path of the `.QBW`. Empty → QuickBooks is treated as unavailable |
| `Company:RulesFile` | `rules.json` | |
| `QuickBooks:AppName` | `QbAutopost` | The name shown in QuickBooks' certificate dialog |
| `QuickBooks:QbXmlVersion` | `13.0` | `16.0` on QuickBooks 2023+ if needed |
| `QuickBooks:DuplicateWindowDays` | `3` | Days either side for the live duplicate check (G4) |
| `QuickBooks:BusyTimeoutSeconds` | `60` | A QuickBooks call longer than this is abandoned (`quickbooks-busy`) |
| `QuickBooks:BackupFolder` | empty | When set, posting is refused unless the newest `*.QBB` there is recent |
| `QuickBooks:BackupMaxAgeHours` | `36` | Age limit for that backup |
| `QuickBooks:Fake` | `false` | Test only: simulated company. Refused outside Development/Testing |
| `Hermes:Enabled` | `true` | `false` = no AI at all (POC, `samples/poc/README-POC.md`): the requirement is read by the regex parser; PDF statements, invoices and lines no rule resolves are held |
| `Hermes:BaseUrl` / `Model` / `TimeoutSeconds` | `http://127.0.0.1:8642` / `default` / `120` | |
| `Hermes:ApiKey` | empty | Hermes' `API_SERVER_KEY` |
| `Ocr:Enabled` / `TessDataPath` | `false` / empty | Scanned PDFs and invoice images need OCR (`eng.traineddata`) |
| `QuickBooks:MaxLineAmount` | `100000.00` | A bigger line on the direct path is **held**, not refused |
| `QuickBooks:AllowedDateWindow:MaxAgeDays` / `MaxFutureDays` | `730` / `1` | A direct line dated outside this is held |
| `QuickBooks:AllowCompanyFileOverride` | `false` | Let a request name its own company file. Keep it off: one installation serves one company |
| `QuickBooks:AllowedCompanyFolders` | `[]` | Folders an overridden company file must sit inside. Empty = unrestricted |
| `Paths:Ledger`, `QbLists`, `Logs`, `JobIndex`, `Clients`, `ApiBatches` | | See §2.2. Relative paths are relative to the program folder |
| `Paths:AllowedJobRoots` | `[]` | Folders a job may come from (§3.5). Empty = unrestricted, and a non-loopback bind then **refuses to start** |
| `Serilog:MinimumLevel:Default` | `Information` | Also `Serilog:MinimumLevel:Override:<category>` |

### 3.2 Secrets

Put keys in environment variables of the auto-logon account instead of the file:

```powershell
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Api__ApiKey', '<long random key>', 'User')
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Hermes__ApiKey', '<API_SERVER_KEY>', 'User')
```

Keys are masked (`***`) in logs and are never written to `output\`. Do not add log sinks in configuration; only levels are read from it.

### 3.3 `rules.json`

| Key | Use |
|---|---|
| `BankAccounts` / `CardAccounts` | last four digits → QuickBooks bank / credit-card account |
| `PayeeAliases` / `CustomerAliases` | description fragment → vendor / customer name |
| `VendorAccounts` | vendor → expense account |
| `KeywordAccounts` | `{ Match, Account }`, description keyword → account |
| `TransferPatterns` | `{ Match, Account }`, bank debits that are transfers (e.g. card payments) |
| `SkipPatterns` | card lines never posted (the card payment already booked from the bank) |
| `HoldingExpenseAccount` | account for numbered checks with no keyword rule (missing → such checks are held) |
| `DepositIncomeAccount` | account for deposits (missing → deposits are held) |
| `CsvLayouts` | how to read each bank's CSV/XLSX export |
| `FuzzyThreshold`, `HistoryMinCount`, `InvoiceMatchDays`, `ModelConfidenceThreshold` | 0.85, 3, 5, 0.8 |

A change applies to the next job or re-post. Prefer the teaching endpoints (§6); they validate names and write safely.
Note: the first taught rule rewrites the file **without its `//` comments**.

### 3.4 API callers: keys and scopes

Every caller has its own key. `clients.json` (`Paths:Clients`, §2.2) holds the id, name, scopes and a **salted hash**
of the key — never the key itself. Issue one with:

```powershell
.\scripts\new-api-client.ps1 -Id acme-erp -Name 'Acme ERP' -Scopes qb:read,qb:post -ClientsFile C:\qb-autopost\clients.json
```

It prints the key **once** and stores only the hash; it cannot be recovered, only replaced. The caller sends it as
`X-Api-Key: <key>` on every request. (`Authorization: Bearer` is *not* accepted by this build — tracker Q-67.)

| Scope | Lets the caller |
|---|---|
| `health:read` | `GET /api/v1/health/sdk`, `/api/v1/health/quickbooks`, `/api/v1/health/hermes`, and read the API reference (§3.6) |
| `qb:read` | Read-only QuickBooks work: the connection test, the company-file check, `POST …/transactions/validate`, `GET /api/v1/quickbooks/lists`, `POST …/lists/sync`, `GET /api/v1/batches/{id}` |
| `qb:post` | **Move money**: `POST /api/v1/quickbooks/transactions` and `POST /api/v1/batches/{id}/undo` |
| `qb:debug` | Get the qbXML body back from a validate (`?includeQbXml=true`). It contains every amount and name |
| `jobs:read` | `GET /api/v1/jobs`, `GET /api/v1/jobs/{id}`, `POST /api/v1/jobs/validate` |
| `jobs:write` | `POST /api/v1/jobs` and `POST /api/v1/jobs/{id}/post` — also money |
| `rules:write` | `POST /api/v1/rules/alias`, `POST /api/v1/rules/account` |
| `admin` | Anything not mapped above. A new route is closed until someone decides otherwise |

A key that is not recognised is `401`; a recognised key without the scope is `403`, and the answer names the scope.
Revoking a caller (`enabled: false`, or delete the entry) takes effect on their **next request** — the file is
re-read each time, no restart.

**Turning the shared key off.** `Api:AllowLegacyKey` is `true` while callers migrate, and the shared `Api:ApiKey`
then carries *every* scope — it outranks every decision in `clients.json`, and startup warns about it on every
boot. Once each caller has its own key: set `Api:AllowLegacyKey` to `false`, restart, and check the log says
`shared Api:ApiKey not accepted`. `scripts\qb-server-check.ps1` needs a key with `health:read, qb:read, jobs:read,
jobs:write, qb:post`.

### 3.5 Limits, refusals and where jobs may come from

- **`Paths:AllowedJobRoots`** — the folders a caller's `folder` may sit inside, for `POST /api/v1/jobs` and
  `POST /api/v1/jobs/validate`. Empty means *unconfigured* = unrestricted, which is only tolerable on a loopback
  bind: with a remote bind and this list empty the app **refuses to start**. Set it to `["C:\\qb-jobs"]` before
  anything outside the machine can reach the API.
- **Rate limits** (`Api:RateLimits`, on by default): `PostPerMinute` 10 for anything that writes to QuickBooks
  (including `POST /api/v1/jobs`), `DefaultPerMinute` 120 for every other authenticated route, `HealthPerMinute`
  600 per address for the two key-free health routes. Over the limit the answer is **`429` with a `Retry-After`
  header in seconds** — wait that long and retry; it is not an error to report. The limit is per caller, so one
  busy integration cannot starve another. **Never raise a limit to make a failing caller work** without deciding
  that the new number is safe.
- **Wrong keys**: more than `FailedAuthPerMinute` (10) unrecognised keys from one address and that address is
  refused outright for `FailedAuthBlockMinutes` (5). A `403` for a missing scope does **not** count, so a
  legitimate caller cannot lock itself out.
- **Body and field sizes**: over `Api:MaxRequestBodyBytes` is `413`; a `memo` over 4096 characters or a name over
  255 refuses the whole batch with `400`, because that is a broken mapping, not a caller meaning what they said.

### 3.6 The API reference

`Api:Reference:Enabled` (default **false**) serves two routes:

| Route | What |
|---|---|
| `GET /api/v1/openapi.json` | The OpenAPI 3.0 document: every route, its request and response shape, and the scope it needs |
| `GET /api/v1/reference` | The page that renders it |

Both need a key with `health:read`, which means **a browser address bar gets a 401** — the key travels in a header.
Read the document with a client that sets one:

```powershell
$h = @{ 'X-Api-Key' = $env:QBAUTOPOST__Api__ApiKey }
Invoke-RestMethod http://127.0.0.1:5080/api/v1/openapi.json -Headers $h | ConvertTo-Json -Depth 8 > openapi.json
```

Disabled means **not mapped**: the answer is `404`, not `403`. Leave it off on the QuickBooks server unless somebody
is actively integrating; a page that lists every way to move money is not something to leave open. The page pulls
nothing from the internet (`UseCdn` false) and its "send request" buttons are off (`AllowTryIt` false).

## 4. First run

1. Open QuickBooks on the server, in the auto-logon session, as the QuickBooks **Admin**, with the company file
   (for the first trial: a **copy**). Leave it open.
2. Start the API (`deploy/start-all.ps1`, or `scripts/run-api.ps1`).
3. `.\scripts\qb-server-check.ps1 -Step health`
   QuickBooks shows the **Application Certificate** dialog for `QbAutopost`: choose
   **"Yes, always; allow access even if QuickBooks is not running"**, pick the QuickBooks user the app logs in as, confirm.
   Expect `ok: true`, the company file and the QuickBooks version.
4. `.\scripts\qb-server-check.ps1 -Step sync` → counts of accounts/vendors/customers. `missingInRules` lists
   account names in `rules.json` that QuickBooks lacks: fix the spelling in `rules.json` (names must match exactly) and sync again until it is empty.
5. `.\scripts\qb-server-check.ps1 -Step dryrun -Folder C:\qb-jobs\<folder>` → `ready`. Review (§5.2).
6. Only on a copy: `-Step post -JobId <id> -ConfirmCopy`, check the transactions in QuickBooks, then
   `-Step undo -BatchId '<id>#1' -ConfirmCopy`.

The script reads the key from `QBAUTOPOST__Api__ApiKey` and never prints it.

## 5. Daily operation

### 5.1 Submit

```powershell
$h = @{ 'X-Api-Key' = $env:QBAUTOPOST__Api__ApiKey }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/jobs -Headers $h -ContentType 'application/json' `
  -Body '{ "folder": "C:\\qb-jobs\\2026-08-tropicana", "dryRun": true }'
Invoke-RestMethod http://127.0.0.1:5080/api/v1/jobs/2026-08-tropicana -Headers $h
Invoke-RestMethod 'http://127.0.0.1:5080/api/v1/jobs?status=ready' -Headers $h
```

The folder name is the job id (letters, digits, `.`, `_`, `-` only). A folder that was already posted is refused with
409; add `"force": true` only after checking QuickBooks (lines already in the ledger are still skipped).

To check a folder **without** taking a job id or writing anything into it — a missing `requirement.txt`, a statement
that will not reconcile — ask first:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/jobs/validate -Headers $h -ContentType 'application/json' `
  -Body '{ "folder": "C:\\qb-jobs\\2026-08-tropicana" }'
```

`200` means it would run, `422` means it would not, and the body is the same either way: `{ ok, errors[],
statements[{ file, last4, kind, rows, reconcile }], unreadable[] }`. It stops before the requirement, the mapper and
the duplicate check, so a folder that passes here can still hold lines when it runs.

The names the mapper will accept come from the cached lists:

```powershell
Invoke-RestMethod http://127.0.0.1:5080/api/v1/quickbooks/lists -Headers $h          # accounts, vendors, customers
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/quickbooks/lists/sync -Headers $h
```

The read never opens a QuickBooks session, so it answers while QuickBooks is shut down; a `syncedUtc` of `null`
means the sync has not run yet.

### 5.2 Review a dry run

In `output\`:

| File | Check |
|---|---|
| `result.json` | counts, totals, `held[]` with reasons, `skipped[]`, `unreadable[]`, `unmatchedInvoices[]` |
| `analysis.json` | every line with kind, account, payee, line account, tier and confidence |
| `statements\<file>.rows.json` | the rows read and the reconcile result |
| `batch-enter-*.csv` | paste-ready sheets, same as what will be posted |
| `request.qbxml` | what will be sent |
| `hermes\` | every AI request/answer |

Fix held lines (§6), then post. Posting re-reads the folder and re-applies the rules first.

### 5.3 Post

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/jobs/2026-08-tropicana/post -Headers $h
```

Only a `ready` job can be posted (409 otherwise). Before sending, the app checks the backup age (if configured) and
queries QuickBooks for duplicates. Result:

- `posted` — every postable line is in QuickBooks. `output\response.qbxml` has the answers; `ledger.json` has the TxnIDs.
- `partial` — some lines were held or refused; see `result.json`. The posted ones stay posted.
- `failed` with `backup-too-old` — nothing was sent. Make a backup and submit the folder again.

Only one job or QuickBooks operation runs at a time; others wait in the queue.

## 6. Exceptions: held and skipped lines

| Reason | Meaning | What to do |
|---|---|---|
| `unknown-payee` | No vendor/customer found; `candidates` lists the closest names | Teach an alias (below), re-run |
| `no-account-rule`, `low-confidence`, `no-prior-posting` | No rule for the expense account, or the AI suggestion is not trusted yet; `candidates` has suggestions | Teach a vendor account (below) |
| `no-accounts` | `qb-lists.json` empty | Sync lists |
| `hermes-failed` | Hermes did not give a valid answer | Check `/api/v1/health/hermes`, re-run |
| `unknown-account` | No `BankAccounts`/`CardAccounts` entry for the last four, or none detected | Add it to `rules.json`, or put the last four in the file name |
| `unknown-csv-layout`, `ambiguous-csv-layout`, `unparsable-rows` | The CSV/XLSX export does not match exactly one layout, or a row cannot be read | Fix `CsvLayouts` or the export |
| `reconcile-failed` | The statement's balances do not add up (G1) | Check the file is complete; the whole statement is held |
| `scanned-pdf-ocr-disabled`, `unreadable-statement`, `unreadable` | The file could not be read | Get a text PDF or CSV, or enable OCR |
| `conflicting-last4`, `kind-mismatch`, `extraction-conflict` | The file contradicts itself or the requirement | Check the file and `requirement.txt` |
| `kind-not-requested` | The requirement did not ask for this transaction type | Fix `requirement.txt` if it should post |
| `duplicate-line` | Two identical lines in the same job | Check the statement; post by hand if both are real |
| `refnumber-too-long` | Check number over 11 characters | Enter by hand |
| `no-holding-account`, `no-deposit-income-account` | Rule missing in `rules.json` | Add `HoldingExpenseAccount` / `DepositIncomeAccount` |
| `already-posted` (skipped) | Same line is in the ledger | Nothing |
| `already-in-quickbooks` (skipped) | Same transaction found in QuickBooks; `note` has its TxnID | Nothing |
| `possible-duplicate` | Same amount within the window but not an exact match | Check QuickBooks; enter by hand if it is new |
| `duplicate-check-failed` | QuickBooks refused the duplicate query for that account | Check the account in QuickBooks, re-post |
| `quickbooks-rejected` | QuickBooks refused the line; the note has its message | Fix the cause (usually a name or account), re-run |
| `amount-mismatch` | QuickBooks echoed a different amount; the note names the TxnID | **Delete that transaction in QuickBooks** and investigate |
| `quickbooks-busy`, `quickbooks-no-response` | QuickBooks did not answer; lines may or may not be posted | See §8.4 |
| `quickbooks-unavailable` / "nothing posted" | QuickBooks could not be reached; nothing was sent | See §8.2, then post again |

Unmatched invoices (`unmatchedInvoices[]`) never hold a line; they only mean no evidence was used.

### Teaching rules

```powershell
# alias: kind is "vendor" or "customer"; fragment is matched in the statement description (at least 3 characters)
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/rules/alias -Headers $h -ContentType 'application/json' `
  -Body '{ "fragment": "UNKNOWN PLUMBER", "name": "Unknown Plumber LLC", "kind": "vendor" }'
# vendor → expense account
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/api/v1/rules/account -Headers $h -ContentType 'application/json' `
  -Body '{ "vendor": "Unknown Plumber LLC", "account": "Repairs and Maintenance" }'
```

When lists have been synced, the vendor/customer and account must exist in QuickBooks with the **exact** spelling
(400 "Did you mean …" otherwise). Create a new vendor in QuickBooks first, then sync lists, then teach.
The answer shows the previous value when a rule was replaced. Then submit the folder again (or post a `ready` job).

## 7. Undo

```powershell
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5080/api/v1/batches/2026-08-tropicana%231/undo' -Headers $h
```

The batch id is `<job id>#<attempt>` (shown as `batchId` on the job); write `#` as `%23`. The answer is
`{ deleted, failed[] }`. Every transaction QuickBooks confirms deleted is marked `undone` in the ledger; when all are,
the job becomes `undone`. To post the folder again, submit it with `"force": true` (the undone lines no longer count as posted).

- A failed entry (for example `3120` "cannot be found" because someone deleted it by hand) stays live. Check it in
  QuickBooks; a later undo retries only the entries still live.
- 503: QuickBooks was not reachable; nothing was marked. Try again.
- 404: the batch is not in the ledger.
- Copies of the request/response are in the job's `output\undo-<batch id with # written as ->.request.qbxml` / `.response.qbxml`.

## 8. Troubleshooting

### 8.1 Health

```powershell
$h = @{ 'X-Api-Key' = $env:QBAUTOPOST__Api__ApiKey }
Invoke-RestMethod http://127.0.0.1:5080/api/v1/health               # liveness, no key
Invoke-RestMethod http://127.0.0.1:5080/api/v1/health/ready         # readiness, no key
Invoke-RestMethod http://127.0.0.1:5080/api/v1/health/quickbooks -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/api/v1/health/hermes -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/api/v1/health/sdk -Headers $h
```

Only `/api/v1/health` and `/api/v1/health/ready` answer without a key. The other three open a QuickBooks session or
probe the SDK, so they need a key with the `health:read` scope (§3.4). **Point your monitoring at
`/api/v1/health/ready`**, which needs nothing and does not touch QuickBooks.

### 8.2 QuickBooks not reachable (503, "nothing posted", `quickbooks-unavailable`)

| Message contains | Cause | Fix |
|---|---|---|
| `not registered for this x64 process` / `x86` | Bitness mismatch or SDK missing | Publish with the other `-r win-x86/win-x64` and install the matching .NET runtime |
| `could not be created` | SDK COM object missing or blocked | Repair QuickBooks / install the SDK |
| `could not open a QuickBooks session` | QuickBooks not open, a modal dialog open, wrong company file, or the certificate was refused | Open QuickBooks on `Company:FilePath`, close dialogs, re-answer the certificate dialog (QuickBooks: Edit → Preferences → Integrated Applications → Company Preferences) |
| `Company:FilePath` empty | Configuration | Set it |
| The app runs as a service or in another session | The SDK needs the logged-on desktop | Run through the logon task (§2.4) |

### 8.3 Dialogs

QuickBooks blocks SDK calls while a dialog is open (update prompts, "single-user mode", backup reminders, the
certificate dialog). Close it in the server's desktop session, then re-run. Keep QuickBooks updates on a schedule
outside working hours.

### 8.4 Busy / no response

`quickbooks-busy` means a call took longer than `BusyTimeoutSeconds`; `quickbooks-no-response` means QuickBooks failed
during the add. **The lines may have been posted.** The batch is recorded in the ledger, so the folder cannot simply be
re-submitted:

1. Look in QuickBooks for the job's transactions (dates and amounts in `result.json`).
2. Submit the folder again with `"force": true` as a dry run. The live duplicate check (G4) skips lines already in
   QuickBooks (`already-in-quickbooks`) and holds near matches (`possible-duplicate`) when you post.

A job found `posting` after a crash or restart becomes `partial` with
"interrupted — run duplicates before re-post": handle it the same way. A job found `analysing` or `queued` becomes
`failed (interrupted)` and can simply be submitted again.

### 8.5 Other failures

| Symptom | Fix |
|---|---|
| Job `failed`: `analysis failed: Hermes Spec call failed` | Hermes is down; start it (`deploy/README-hermes.md`), submit again |
| Job `failed`: company name / last four (G2) | `requirement.txt` names another company, lists a last four with no statement, or a statement's last four is not listed and not in `rules.json` |
| Job `failed`: `backup-too-old` | Make a QuickBooks backup (`.QBB`) into `QuickBooks:BackupFolder`, submit again |
| Job `failed`: rules | `rules.json` is not valid JSON or has a bad value (see the error); teaching returns 500 in that case and does not touch the file |
| 401 | Missing or unrecognised `X-Api-Key` (or the caller was disabled; the list is re-read per request) |
| 403 | The key is known but lacks the scope the route needs; the answer names it (§3.4) |
| 429 | Rate limited, or the address is in the wrong-key block. Wait the seconds in `Retry-After` (§3.5) |
| 413 | The body is over `Api:MaxRequestBodyBytes` |
| Host does not start: `No API caller can authenticate` | No enabled client in `clients.json` **and** no usable shared key. Issue one with `scripts\new-api-client.ps1`, or set `QBAUTOPOST__Api__ApiKey` with `Api:AllowLegacyKey` true |
| Host does not start: a transport refusal | A non-loopback `Api:Bind` without TLS (`Api:AllowInsecureRemote`), a CORS `*`, or a remote bind with `Paths:AllowedJobRoots` empty (§3.5) |
| 409 on `POST /api/v1/jobs` | The job is running, or already in the ledger (use `force` only after checking QuickBooks) |
| Host does not start | Read the console or the last `[FTL] QbAutopost stopped:` line in the log: empty API key, `QuickBooks:Fake` outside Development, missing OCR data, or a missing prompt file |

### 8.6 Logs

`C:\qb-autopost\logs\qbautopost-yyyyMMdd.log`. Each line shows the job id in brackets (`[-]` when not about a job):

```
2026-09-17 09:12:03.114 +00:00 [INF] [2026-08-tropicana] QbAutopost.Api.Jobs.JobWorker: Job 2026-08-tropicana: Post started
```

Search a job with `Select-String -Path C:\qb-autopost\logs\*.log -Pattern '\[2026-08-tropicana\]'`.
For more detail set `Serilog:MinimumLevel:Default` to `Debug` and restart.

What is logged (Information unless noted):

| When | Lines to look for |
|---|---|
| Startup | `starting:` (version, **x64/x86 process**, Windows session, user), `Settings:` (API address, `DryRunDefault`, company, company file and rules file with `found`/`missing`), `Data files:`, `QuickBooks:` (qbXML version, busy timeout, backup folder), `Hermes:` (URL, model, key `set`/`not set`), `QuickBooks gateway:`, `Startup recovery:`, `ready, listening on`. Warnings for a missing company or rules file and for Windows session 0. A startup failure ends with `[FTL] QbAutopost stopped:` |
| Every API request | `HTTP POST /api/v1/jobs responded 202 in 12 ms` (4xx Warning, 5xx Error; a healthy `/api/v1/health/*` poll is Debug). A wrong or missing key: `refused: invalid X-Api-Key header` (the key itself is never logged). Job, post, undo and sync-lists requests also log what was accepted or refused and why |
| A job | `accepted`, `analysing`, `requirement read`, one line per statement (rows, reconcile) or `held (reason)`, invoices, `N lines: … to post, … held (reason xN), … skipped`, one line per held or skipped line (`file:line date amount direction`, reason, note), `posting batch …`, the final status |
| QuickBooks | `QuickBooks SDK:` each session step (`creating …`, `OpenConnection2`, `BeginSession on '…'`, `session open`, `session closed`). **A log that stops after `BeginSession` means QuickBooks is showing a dialog** (certificate, login, single-user). `QuickBooks call N: sending CheckAddRq x5 …`, `answered in … ms with N responses: … ok, … with errors`, a Warning per refused request with its status code and message, `took … s, longer than the busy timeout` |
| Posting | `posted Check file:line … as TxnID …` per transaction, `not posted … reason` per rejected line, `batch <id>: N posted, M not posted`, `duplicate check (G4) changed …` |
| Hermes | `Hermes Spec: asking (N characters of input)`, `valid answer in … ms`, or a Warning `no answer` / `answer rejected twice` |

Statement text (descriptions, memos), prompts and qbXML bodies are not logged at Information (spec §14): lines are named
by file, line number, date and amount. At Debug each line's description and full mapping, and every QuickBooks response
with its TxnID, are added. The full qbXML and Hermes exchanges stay in the job's `output\` folder.

### 8.7 The audit trail

Beside the operational log, in the same folder, the app writes `audit-yyyyMMdd.jsonl` — **one JSON line per
request that changed something**, kept for `Api:AuditRetentionDays` (400 days; `0` or less keeps everything for
ever). It is not a Serilog sink: no level, no filter and no formatter can lose a line from it.

```powershell
Get-Content C:\qb-autopost\logs\audit-20260923.jsonl -Tail 5 | ForEach-Object { $_ | ConvertFrom-Json }
```

Each line carries the time, the `requestId`, the `clientId` who asked, the method and route, the status, and — for
a post — the `batchId`/`jobId`, the counts and the money that actually moved. It never carries a payee, a memo, an
account name or a key: those stay in the job's `output\` folder and in the ledger.

- A request that **read** something (any `GET`, and the validates, the connection test and the company-file check)
  leaves no line; only changes do.
- A `403` and a `429` **are** recorded, under the caller who was refused. A request with an **unknown** key is not
  (tracker Q-64): a stranger must not be able to grow this file.
- Quote the `requestId` when reporting a problem — the same id is in the `X-Request-Id` response header, on every
  log line of that request, and in the problem detail.

Back it up with the ledger (§9). This is the record that answers "who posted this, and when".

## 9. Backing up the app's own state

Back up `C:\qb-autopost\` (ledger, rules, lists, job index) together with the QuickBooks backup. Restoring an older
`ledger.json` makes already-posted lines look new: always run a dry run and rely on the live duplicate check after a restore.
