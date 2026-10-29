# QbAutopost — Operator Runbook

Version 1.0 · 2026-09-17 · Audience: the person who installs, runs and supports QbAutopost on the QuickBooks server.
Related: `spec.md` (the contract), `tracker.md` (open questions Q-1…Q-41 and the T-609 server checklist), `docs/adr/0011-openrouter-for-model-calls.md` (model provider). `deploy/README-hermes.md` is obsolete.

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
`output\` folder. A model at OpenRouter (an online AI service; statement and invoice text is sent to it) reads the requirement, PDF statements and invoices, and suggests accounts;
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
| Model | OpenRouter over outbound HTTPS (`openrouter.ai`, port 443) with the `OpenRouter__ApiKey` machine variable, see 2.4. Docker is no longer needed |
| Account | A Windows account with auto-logon. The app runs **in that interactive session** (Task Scheduler at logon), never as a Windows service, because QuickBooks' SDK needs a desktop session |

### 2.2 Folders

| Path | Holds |
|---|---|
| `C:\qb-autopost\app\` | the published program |
| `C:\qb-autopost\rules.json` | mapping rules (§3.3) |
| `C:\qb-autopost\ledger.json` | every posted line and batch (do not edit by hand) |
| `C:\qb-autopost\qb-lists.json` | cached QuickBooks accounts / vendors / customers, **only when no database is configured**. With PostgreSQL the lists live in the database (`qb_list_syncs`, §5.4); the file is then read once to import and never written again |
| `C:\qb-autopost\jobs.json` | index of known job folders |
| `C:\qb-autopost\clients.json` | API callers: id, name, scopes and a **salted hash** of each key, never a key (§3.4) |
| `C:\qb-autopost\api-batches\` | one folder per direct post: its request, state and qbXML, plus `idempotency.json` |
| `C:\qb-autopost\qb-audit\` | qbXML copies of list syncs and undos without a job folder. A list-sync copy holds names and types only (the query asks for nothing else), replaced at each sync |
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

1. Set up the model provider (OpenRouter), once, as an administrator:
   - Set `OpenRouter__ApiKey` as a **machine** environment variable the service account can read, and `OpenRouter__Enabled=true`
     (`[Environment]::SetEnvironmentVariable('OpenRouter__ApiKey', '<key>', 'Machine')`). Never put the key in `appsettings*.json`.
   - Allow outbound HTTPS (port 443) from this server to `openrouter.ai`.
   - If a Hermes container still exists from before, stop and disable it (`docker compose down` in `deploy\hermes`, then disable the Docker service or the WSL distribution's autostart). **Docker is no longer needed** on this server.
   - Restart the API afterwards so it reads the variables, then check `GET /api/v1/health/hermes` (it now pings OpenRouter; section 8.1).
2. `deploy/start-all.ps1` starts or verifies the PostgreSQL service, starts `C:\qb-autopost\app\QbAutopost.Api.exe` (`-AppExe`) unless it already runs, then waits for `GET /api/v1/health/ready` = 200. It no longer starts Docker or a model container. The script holds no key, so it can only call the two key-free routes (`/api/v1/health/hermes` needs `health:read` - use `scripts\qb-server-check.ps1 -Step health` for the dependency checks). Exit 0 = the API is ready; exit 1 = the API could not be started or never became ready. It writes `C:\qb-autopost\logs\start-all-yyyyMMdd.log` and never reads a key.
3. `deploy/install-task.ps1` (run once as the auto-logon account, in an elevated PowerShell) registers the task `QbAutopost`: at logon of that account, 60 s delay, **interactive** session, limited rights, no password stored, no time limit, a second start ignored. Pass start-all options with `-StartAllArguments '-SkipPostgres'`; remove it with `-Unregister`.
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
| `QuickBooks:ListSync:IntervalMinutes` | `30` | Minutes between scheduled list syncs. `0` = schedule off; otherwise 5 to 1440 (anything else stops startup). §5.4 |
| `QuickBooks:ListSync:FirstDelayMinutes` | `2` | First scheduled attempt this long after startup when no attempt is recorded; 0 to 1440 |
| `QuickBooks:ListSync:HistoryRows` | `200` | Rows kept in `qb_list_syncs` (at least 10) |
| `QuickBooks:BackupFolder` | empty | When set, posting is refused unless the newest `*.QBB` there is recent |
| `QuickBooks:BackupMaxAgeHours` | `36` | Age limit for that backup |
| `QuickBooks:Fake` | `false` | Test only: simulated company. Refused outside Development/Testing |
| `OpenRouter:Enabled` | `false` | `true` turns the model on. `false` = no AI at all (POC, `samples/poc/README-POC.md`): the requirement is read by the regex parser; PDF statements, invoices and lines no rule resolves are held |
| `OpenRouter:BaseUrl` / `Model` / `TimeoutSeconds` | `https://openrouter.ai/api/v1` / `~google/gemini-pro-latest` / `120` | `BaseUrl` must be `https://` unless the host is loopback, otherwise startup fails. Optional `Referer` and `Title` are sent as attribution headers |
| `OpenRouter:ApiKey` | empty | The OpenRouter key. Environment only (`OpenRouter__ApiKey`); never in the file. A leftover `Hermes` section is ignored with one startup warning |
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
[Environment]::SetEnvironmentVariable('OpenRouter__ApiKey', '<OpenRouter key>', 'Machine')   # administrator; readable by the service account
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
`X-Api-Key: <key>` on every request. API keys are sent only in `X-Api-Key`; an API key in `Authorization: Bearer` is *not* accepted by this build (tracker Q-67). `Authorization: Bearer` carries only a dashboard user's access token (§11).

| Scope | Lets the caller |
|---|---|
| `health:read` | `GET /api/v1/health/sdk`, `/api/v1/health/quickbooks`, `/api/v1/health/hermes`, and read the API reference (§3.6) |
| `qb:read` | Read-only QuickBooks work: the connection test, the company-file check, `POST …/transactions/validate`, `GET /api/v1/quickbooks/lists`, `POST …/lists/sync`, `GET /api/v1/batches/{id}` |
| `qb:post` | **Move money**: `POST /api/v1/quickbooks/transactions` and `POST /api/v1/batches/{id}/undo` |
| `qb:debug` | Get the qbXML body back from a validate (`?includeQbXml=true`). It contains every amount and name |
| `jobs:read` | `GET /api/v1/jobs`, `GET /api/v1/jobs/{id}`, `POST /api/v1/jobs/validate` |
| `jobs:write` | `POST /api/v1/jobs` and `POST /api/v1/jobs/{id}/post` — also money |
| `rules:write` | `POST /api/v1/rules/alias`, `POST /api/v1/rules/account` |
| `requests:submit` | `POST /api/v1/saved-requests` only: save a payload for review. This is all the n8n workflow needs (§10.6) |
| `requests:read` | `GET /api/v1/saved-requests...`: list and read saved requests, their versions, attempts and events |
| `lists:read` | Read the QuickBooks lists the dashboard offers (`qb:read` also satisfies it) |
| `admin` | Anything not mapped above. A new route is closed until someone decides otherwise |

Editing, validating, posting and discarding a saved request, and managing users, are **people-only** scopes (a signed-in
dashboard user, by role): no API key can hold them, `admin` included.

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
means the sync has not run yet. The lists also refresh by themselves every 30 minutes (§5.4).

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

### 5.4 Scheduled list sync (M14)

The account, vendor and customer names are refreshed from QuickBooks by the API itself, on the same single worker that
runs jobs, so a sync never overlaps a job or a post. Settings (§3.1; environment form
`QuickBooks__ListSync__IntervalMinutes`):

- `QuickBooks:ListSync:IntervalMinutes` (default 30): minutes between syncs, 5 to 1440. `0` turns the schedule off.
- `QuickBooks:ListSync:FirstDelayMinutes` (default 2): the first sync after the API starts, when no earlier attempt is known.
- `QuickBooks:ListSync:HistoryRows` (default 200): how many attempts `qb_list_syncs` keeps (at least 10; the newest
  successful row is always kept). At most about four days at the default (Sync now and skipped attempts count too).

The startup log's `QuickBooks:` line shows what is in force, for example `list sync every 30 min (first after 2 min),
lists in the database`, or `list sync schedule off`.

**It never starts QuickBooks.** Before each scheduled sync the API looks for a running QuickBooks in its own Windows
session. If there is none, the attempt is recorded as `skipped` with the reason `quickbooks-not-open` and nothing is
sent. So QuickBooks must be open **in the same Windows session as the API** (the signed-in desktop where
`start-all.ps1` runs it, not another user's session and not a service). Close QuickBooks at night and the lists simply
stay as they were; open it and the next due sync runs.

**A dialog delays the worker for up to about two minutes.** When QuickBooks is showing a dialog (or is busy), the sync
waits for it inside QuickBooks' session start, which can take up to about 2 minutes. The worker is occupied meanwhile,
so a job submitted in that time waits and then runs normally; nothing is lost. A post that does not wait
(`validate-and-post`, or posting a saved request) is answered `409` "session in use" while the sync holds QuickBooks,
with nothing sent: retry it after the sync ends. Close the dialog to release it. A sync
that fails this way is recorded as `failed` (reason `quickbooks-busy` or `quickbooks-unavailable`), the lists stay as
they were, and the next attempt is a full interval later.

**Reading the state.** `GET /api/v2/quickbooks/lists` with a dashboard user's token, or with an API key that holds
`lists:read` or `qb:read`:

```powershell
$token = "<access token from a dashboard sign-in>"
Invoke-RestMethod http://127.0.0.1:5080/api/v2/quickbooks/lists -Headers @{ Authorization = "Bearer $token" }
# or, with an API key ($key holds lists:read or qb:read):
Invoke-RestMethod http://127.0.0.1:5080/api/v2/quickbooks/lists -Headers @{ 'X-Api-Key' = $key }
```

Besides the names it returns:

- `lastSync`: the newest attempt of any kind, with its outcome (`ok`, `failed`, `skipped`), reason, time, who asked
  (`requestedBy` for a person's Sync now) and a fixed message. With a database it survives restarts; with **no database
  it is the last attempt since the API started** and is null before any.
- `nextSyncUtc`: when the next scheduled sync is due, or null when the schedule is off.

`GET /api/v1/health/ready` also reports it in the `qb-lists` check (a warning only), for example
`last sync failed 15:00 (quickbooks-not-open); lists from 14:30` (times UTC). If the lists are old and the last sync
failed or was skipped, start with the reason.

**History.** With PostgreSQL every attempt is a row in `qb_list_syncs` (who, when, trigger, outcome, reason, counts;
never the names):

```sql
SELECT * FROM qb_list_syncs ORDER BY id DESC LIMIT 20;
```

**Turning it off.** Set the machine variable `QuickBooks__ListSync__IntervalMinutes=0` and restart the API. The lists
then change only when somebody syncs by hand (`POST /api/v1/quickbooks/lists/sync`, or Sync now). Setting it back to 30
restores the schedule.

**Sync now.** The dashboard's lists panel has a Sync now button; it calls `POST /api/v2/quickbooks/lists/sync`:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:5080/api/v2/quickbooks/lists/sync -Headers @{ Authorization = "Bearer $token" }
# or, with an API key that holds qb:read:
Invoke-RestMethod -Method Post http://127.0.0.1:5080/api/v2/quickbooks/lists/sync -Headers @{ 'X-Api-Key' = $key }
```

Who may call it: a dashboard user with `requests:edit` (editors and admins; viewers do not see the button), or an API
key with `qb:read`. A key's sync is recorded with trigger `api` and the key's client name; a person's with trigger
`dashboard` and their user id. The route uses the **posting** rate-limit bucket. Two people pressing it together cause one
sync; a press while the scheduled sync runs joins it. Answers:

| Status | Code | Meaning |
|---|---|---|
| `200` | | Fresh lists, counts and `lastSync`; the pickers update |
| `403` | | A viewer, or a key without `qb:read` |
| `409` | `quickbooks-not-open` | QuickBooks is not running in the API's session; a `skipped` row is recorded |
| `503` | `quickbooks-unavailable` | QuickBooks could not be reached |
| `503` | `quickbooks-busy` | QuickBooks did not answer within `QuickBooks:BusyTimeoutSeconds` (a dialog, usually) |
| `503` | `api-stopping` | The API is shutting down |
| `502` | `qb-status` | QuickBooks answered with an error status |
| `504` | `sync-timeout` | Longer than `Api:SyncPostTimeoutSeconds`; the sync keeps running and its `qb_list_syncs` row shows the result |

## 6. Exceptions: held and skipped lines

| Reason | Meaning | What to do |
|---|---|---|
| `unknown-payee` | No vendor/customer found; `candidates` lists the closest names | Teach an alias (below), re-run |
| `no-account-rule`, `low-confidence`, `no-prior-posting` | No rule for the expense account, or the AI suggestion is not trusted yet; `candidates` has suggestions | Teach a vendor account (below) |
| `no-accounts` | The QuickBooks lists are empty (never synced) | Sync lists (§5.4) |
| `hermes-failed` | Hermes did not give a valid answer | Check `/api/v1/health/hermes` (pings OpenRouter), re-run |
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
| Job `failed`: `analysis failed: Hermes Spec call failed` | OpenRouter is not reachable or the key is missing; check `OpenRouter__ApiKey`, outbound HTTPS to `openrouter.ai` and `/api/v1/health/hermes`, submit again |
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
| Startup | `starting:` (version, **x64/x86 process**, Windows session, user), `Settings:` (API address, `DryRunDefault`, company, company file and rules file with `found`/`missing`), `Data files:`, `QuickBooks:` (qbXML version, busy timeout, backup folder), `OpenRouter:` (URL, model, key `set`/`not set`, timeout; or `disabled` when `OpenRouter:Enabled=false`), `QuickBooks gateway:`, `Startup recovery:`, `ready, listening on`. Warnings for a missing company or rules file and for Windows session 0. A startup failure ends with `[FTL] QbAutopost stopped:` |
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
The saved requests are **not** in that folder: they are in PostgreSQL and are backed up by §10.5.

## 10. PostgreSQL for saved requests (T-1016)

Saved requests, their history and the dashboard users live in PostgreSQL 16 **on this server**, bound to `127.0.0.1`
only (spec `docs/saved-requests-postgres.md` §5.4). Everything else (ledger, rules, lists, job status, idempotency, audit)
stays in files, so `POST /api/v1/quickbooks/transactions/validate-and-post` keeps working when the database is down.
The step-by-step with expected output is `docs/testing/T-1016.server-checklist.md`. Every script takes `-WhatIf`.

### 10.1 Roles and what they may do

| Role | Used by | Rights |
|---|---|---|
| `postgres` | you, at install only | Superuser. Its password is the one you pass to the installer script and is **not stored**: keep it in your password manager |
| `qbautopost_owner` | `apply-migrations.ps1`, `backup-postgres.ps1` | Owns the database; runs migrations; dumps |
| `qbautopost_app` | the API | `SELECT`, `INSERT`, `UPDATE` on the mutable tables; **only** `SELECT`, `INSERT` on `saved_request_versions`, `saved_request_events` and `user_audit`; `SELECT` on the migration history; `DELETE` on `user_roles` only (changing a user's role replaces the row); no DDL, no `TRUNCATE` |

The rights are `deploy\grants.sql`, re-applied after every migration. The two connection strings are **machine**
environment variables written at install, never to a file: `QBAUTOPOST__Database__ConnectionString` (app role, read by
the API) and `QBAUTOPOST_MIGRATION_CONNECTION` (owner role). `QBAUTOPOST__Auth__Jwt__SigningKey` is generated there too if
it is absent (at least 32 bytes; if you rotate it, every signed-in user must sign in again). Processes started **before**
the variables were set do not see them: restart the API and open a new PowerShell window.

### 10.2 Install

From an elevated PowerShell on the server, in the repository's `deploy` folder. Download the PostgreSQL 16 Windows x64
installer from the vendor page and copy its SHA-256 from that page; the script refuses a file that does not match.

```powershell
.\deploy\install-postgres.ps1 -InstallerPath D:\setup\postgresql-16.x-windows-x64.exe -InstallerSha256 <sha256 from the vendor page> `
    -SuperuserPassword (Read-Host -AsSecureString 'postgres superuser password') -BackupFolder D:\qb-backups -WhatIf
# read the plan, then run it again without -WhatIf
```

It installs the service `postgresql-x64-16`, sets `listen_addresses = '127.0.0.1'`, creates the database and both roles,
writes the machine variables, runs `apply-migrations.ps1` and registers the daily backup task (§10.5). It can be run
again: roles, database and variables are kept; `-RotatePasswords` issues new role passwords (restart the API after).
Use `-SkipInstall` if PostgreSQL 16 is already there.
The vendor installer is never given a password on its command line (any local user could read it in the process list):
the script writes its options to an option file (`--optionfile`) in a fresh folder under `%TEMP%` that only the account
running the script can read, holding a throwaway superuser password that is replaced before the script ends, and deletes
that folder as soon as the installer exits, even on failure.

### 10.3 Migrate (every release that has a new migration)

The app never migrates by itself. With the API **stopped** (the script refuses while it runs):

```powershell
.\deploy\apply-migrations.ps1
```

It runs `deploy\migrations.sql` (idempotent: migrations already applied are skipped) as the owner, re-applies
`grants.sql`, and checks every migration in the script is recorded. A start of the API against an older schema still
works: the database feature answers `503` `schema-out-of-date` and `GET /api/v1/health/ready` shows
`database: { ok: false, reason: "schema-out-of-date" }` (`start-all.ps1` prints a warning), until you run it.
Developers regenerate the script with
`dotnet tool run dotnet-ef migrations script --idempotent -p src/QbAutopost.Data -s src/QbAutopost.Data -o deploy/migrations.sql`
(a test fails if the checked-in file is stale); strip the byte order mark and use LF line ends, as `deploy\migrations.sql` does.

### 10.4 Start order and the bootstrap admin

`start-all.ps1` starts or verifies the PostgreSQL service and waits for `127.0.0.1:5432` **before** it starts the API.
That order matters: the bootstrap admin is created **once, at start**, and only when the database is Ready; if it is not,
the app logs a warning and skips it. **If the admin was not created because the database was down, restart the app once.**

To create the first dashboard admin, set these for the auto-logon account (a temporary password of 12 characters or more),
start the API, sign in, and change the password when asked (`mustChangePassword`):

```powershell
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Auth__BootstrapAdmin__Email', '<admin email>', 'User')
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Auth__BootstrapAdmin__Password', '<temporary password>', 'User')
```

It does nothing once any user exists (it warns and ignores the variables). Afterwards **delete the password variable**:
`[Environment]::SetEnvironmentVariable('QBAUTOPOST__Auth__BootstrapAdmin__Password', $null, 'User')`.

### 10.5 Back up and restore

`install-postgres.ps1` registers the Task Scheduler task `QbAutopostPostgresBackup` (daily 02:00, as `SYSTEM`, no password
stored; not an app timer). It runs `deploy\backup-postgres.ps1`: `pg_dump -Fc` to
`<QuickBooks:BackupFolder>\postgres\qbautopost-yyyyMMdd.dump`, checked with `pg_restore --list` before it is renamed into
place, the newest 30 kept. A backup folder on a network share needs another account: re-register the task by hand.
`scripts\qb-server-check.ps1 -Step postgres` checks the service is running, the database answers
`GET /api/v1/health/database`, and the newest dump is younger than 36 hours.

Restore (the API stopped; this **replaces** the database content with the dump's):

```powershell
. .\deploy\pg-common.ps1
$owner = ConvertFrom-NpgsqlConnection $env:QBAUTOPOST_MIGRATION_CONNECTION
Invoke-Pg -Exe 'C:\Program Files\PostgreSQL\16\bin\pg_restore.exe' -Connection $owner `
    -Arguments @('--clean', '--if-exists', '--no-owner', '-d', $owner.Database, 'D:\qb-backups\postgres\qbautopost-20261028.dump')
.\deploy\apply-migrations.ps1      # newer migrations, and the grants again
.\deploy\start-all.ps1
```

A restore can bring back a request as `pending` that was posted to QuickBooks after the dump. Posting it again is stopped
by the live duplicate check (G4) and the duplicate flag on the request; an admin resolves any request left `posting`
or `unknown` from the dashboard. Test a restore into a spare database before you need one.

### 10.6 Switch n8n to saved requests (tracker Q-81)

The old workflow calls `POST /api/v1/quickbooks/transactions/validate-and-post` with a key that holds `qb:post`. The new
one only **saves**; a person reviews and posts from the dashboard. Do it in this order, so the workflow is never without a route:

1. **Issue an n8n key with `requests:submit`** (add `requests:read` only if the workflow should read records back):
   `.\scripts\new-api-client.ps1 -Id n8n-requests -Name 'n8n mailbox' -Scopes requests:submit -ClientsFile C:\qb-autopost\clients.json`.
   Copy the key once into n8n's credential store.
2. **Change the n8n HTTP Request node**: URL from `/api/v1/quickbooks/transactions/validate-and-post` to
   `/api/v1/saved-requests`, the new key in `X-Api-Key`, and add the header `Idempotency-Key` (for example the email's
   Message-ID), so a retried delivery replays the same record instead of saving a second one. Send one test mail: the
   answer is `201` with a `Location` header, and a replay of the same mail is `200`.
3. **Withdraw the old key's `qb:post`** once step 2 is verified: edit the old client in `clients.json` and remove `qb:post`
   (and `jobs:write`) from its `scopes`, or set `"enabled": false`. It takes effect on the next request. A stale workflow
   then gets `403` naming the scope, and nothing is posted by it.

The shared `Api:ApiKey` still carries every scope while `Api:AllowLegacyKey` is true (§3.4): turn that off once every
caller has its own key.

### 10.7 Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Saved-request routes `503` `database-unavailable` / `not-configured` | The service is stopped, or `QBAUTOPOST__Database__ConnectionString` is not visible to the API (start the API from a new session). `GET /api/v1/health/database` (scope `health:read`) says which |
| `503` `schema-out-of-date` | Stop the API, run `apply-migrations.ps1`, start it (§10.3) |
| No admin can sign in after a first start | The database was down at start: restart the app once (§10.4) |
| `apply-migrations.ps1` says the API is running | Stop the API first; it never migrates under a live app |
| `permission denied for table ...` in the log | `grants.sql` was not re-applied after a migration: run `apply-migrations.ps1` again |
| Dump too old in the server check | Run the task (`Start-ScheduledTask -TaskName QbAutopostPostgresBackup`) and read `C:\qb-autopost\logs\backup-postgres-*.log` |

## 11. The dashboard on the office network (T-1117)

The dashboard is a set of static files that the API itself serves at `/`, beside the API. Nothing else runs: no web server,
no proxy. Spec: `docs/saved-requests-dashboard-v2.md` section 13. Checklist: `docs/testing/T-1117.server-checklist.md`.

### 11.1 Install the files

```powershell
.\deploy\install-dashboard.ps1 -Source <built dashboard dist folder> -WhatIf     # read the plan
.\deploy\install-dashboard.ps1 -Source <built dashboard dist folder>
```

It copies to `C:\qb-autopost\dashboard.new`, checks `index.html` is there, then renames: the live folder becomes
`dashboard.old`, and `dashboard.new` becomes `dashboard`. It touches no service, firewall rule, certificate or setting.

| Setting | Default | Meaning |
|---|---|---|
| `Dashboard:Path` | `C:\qb-autopost\dashboard` | Folder served at `/` (a relative path resolves against the program folder) |
| `Dashboard:Enabled` | `true` | `false` leaves `/` to the API exactly as before |

The folder is checked per request: installing into a folder that already exists is picked up on the next request. If
the folder was **missing when the service started** (the API logs one warning "dashboard not installed" and answers
`404` text), **restart the API after the first install** (stop it, then `start-all.ps1` or `Start-ScheduledTask`).

**Roll back:** `.\deploy\install-dashboard.ps1 -Rollback` renames `dashboard` to `dashboard.failed` and `dashboard.old` back
to `dashboard`. By hand, rename the two folders the same way. Only one previous version is kept.

### 11.2 HTTPS for the accountant's PC

Sign-in sends a password and a refresh cookie, so the office network gets HTTPS (a non-loopback bind without TLS stops
startup, section 3.5).

1. **Bind and certificate.** Set `Api:Bind` to `https://0.0.0.0:5443` and `Api:Tls` to a `.pfx` (`PfxPath`) or a
   machine-store certificate (`StoreThumbprint`). The pfx password is environment only:
   `QBAUTOPOST__Api__Tls__PfxPassword`. Set `Paths:AllowedJobRoots` too (section 3.5). The certificate's name must match
   the name the PC types (`https://<server>:5443/`).
2. **Trust.** The accountant's PC must trust the certificate: issue it from an internal CA the PC already trusts, or
   import a self-signed certificate (public part only) into that PC's **Trusted Root Certification Authorities** store.
3. **Firewall**, from an elevated PowerShell, once: TCP 5443, Domain and Private profiles only, same subnet only
   (`RemoteAddress LocalSubnet`). Never Public, and no router port forward.

   ```powershell
   New-NetFirewallRule -DisplayName 'QbAutopost dashboard 5443' -Direction Inbound -Protocol TCP -LocalPort 5443 `
       -Action Allow -Profile Domain,Private -RemoteAddress LocalSubnet
   ```
4. **URL:** `https://<server>:5443/`. Sign-in uses `/api/v2/auth`, and the refresh cookie path is `/api/v2/auth`, so a
   reload keeps the session.
5. **CORS stays off.** The dashboard and the API share one origin: leave `Api:Cors:AllowedOrigins` **empty**. No
   `Access-Control-*` header is sent.
6. **Existing callers are unchanged:** the API answers `GET /api/v1/health` and `GET /api/v1/jobs` as before, and so do the legacy flat paths while `Api:LegacyRoutes` is on.

### 11.3 Migrate for v2

Two migrations, `DashboardV2` and `AuditEventsView`, go through the normal flow of section 10.3 (API stopped, then
`.\deploy\apply-migrations.ps1`; it re-applies `grants.sql`, which now gives the app role `SELECT` on `audit_events`).
**Before migrating**, check that no saved version already uses the new field names:

```sql
SELECT count(*) FROM saved_request_versions WHERE payload::text ~ '"(post|pushId|email)"';
```

It must print `0`. If not, stop and ask.

### 11.4 Long posts and timeouts

Kestrel has no request cut-off, the app runs as a bare process (no proxy), and a client disconnect does not cancel a post.
The server-side bound is `QuickBooks:BusyTimeoutSeconds` (60) per QuickBooks step, including the one add call that
carries every row. **Any future proxy timeout must be at least 10 minutes.** Nobody has measured a 500-row add yet: the
checklist does (Q-96), and the setting stays unchanged until then. A post cut by the bound ends `unknown`, never posted twice.

### 11.5 Switch n8n to v2 saved requests

Same order as section 10.6 (key with `requests:submit`, verify, then withdraw `qb:post`). The differences:

- URL: `POST /api/v2/saved-requests`.
- The body adds `email`: `{ "subject": ..., "receivedUtc": ..., "from": ..., "messageId": ... }` and `pushId`, which must be
  **stable per email across retries**: derive it deterministically from the Message-ID (for example a UUID v5 of the
  Message-ID) or store it on the first run. A fresh `pushId` with the same `Idempotency-Key` makes the body differ and
  answers `409 idempotency-key-reused`.
- Keep the `Idempotency-Key` header equal to the email's Message-ID.
- A batch evidence `request.json` does not show `post: false` rows (the engine's set is internal); `result.json` lists
  them under `skipped` with reason `not-to-post`.
- Testing note: the 1000-request list performance test (`ListV2PerformanceTests`, trait `Perf`) is skipped unless
  `QBAUTOPOST_PERF=1` is set.

## 12. Statement upload (extraction) with OpenRouter (T-1215)

The dashboard's upload page sends a bank-statement PDF to the model at OpenRouter and shows the rows for review. The key,
the enable switch, outbound HTTPS and retiring Hermes are in sections 2.4 and 3.1; this section adds the upload itself.
Spec: `docs/dashboard-api-m12.md` sections 6, 8 and 10. Checklist: `docs/testing/T-1215.server-checklist.md`.

### 12.1 Switch it on

Uploads need all three of these, otherwise the route answers `503 extraction-unavailable`:

```powershell
# administrator; machine variables, readable by the service account (restart the API afterwards)
[Environment]::SetEnvironmentVariable('OpenRouter__Enabled', 'true',  'Machine')
[Environment]::SetEnvironmentVariable('OpenRouter__ApiKey',  '<key>', 'Machine')   # never in appsettings*.json
[Environment]::SetEnvironmentVariable('Extraction__Enabled', 'true',  'Machine')
```

The statement text goes to `openrouter.ai` over HTTPS (port 443). Nothing of the PDF is kept: no temp file, no copy under
`output\`, no log line with its text. Only the extracted rows are stored, in the extraction record.

### 12.2 `Extraction` settings

| Key | Default | Notes |
|---|---|---|
| `Extraction:Enabled` | `false` | Off until configured. Needs `OpenRouter:Enabled` and a key as well |
| `Extraction:Model` | `~google/gemini-pro-latest` | Model for uploads; may differ from `OpenRouter:Model` (jobs) |
| `Extraction:TimeoutSeconds` | `120` | |
| `Extraction:MaxBytes` | `10485760` | 10 MB per PDF |
| `Extraction:MaxPages` | `50` | More pages are refused before any text is sent |
| `Extraction:MaxPerUserPerHour` | `20` | Per signed-in user |
| `Extraction:RecordHours` | `24` | How long an extraction record waits to be saved; expired records are removed on the next use, no timer |
| `Extraction:StrictSchema` | `true` | Sends `strict: true` in the JSON schema. Set `Extraction__StrictSchema=false` only if OpenRouter rejects strict mode for the model (Q-108); the reply is still validated in code |

The prompt version lives in the prompt file, not in settings. A scanned PDF without a text layer is refused (`422 no-text`);
there is no OCR.

### 12.3 Check it is configured

The v2 health answer (needs a signed-in token with `requests:read` or `health:read`) has an `extraction` entry:
`disabled`, `configured`, or `misconfigured` (enabled but no key). It never calls OpenRouter, so it proves the settings, not
the key. The proof is one real upload (checklist section C).

### 12.4 Time zones need ICU (Q-112)

The dashboard summary takes IANA zone ids such as `Asia/Kolkata`. On Windows these resolve only through ICU, which ships
with Windows 10 1903+ and Windows Server 2019+. The project turns invariant globalization off for Windows builds, and the
project file decides by the **operating system of the machine that builds or publishes**, so **build and publish on
Windows** (a Linux-built package would be invariant and could not resolve the zone). If `Asia/Kolkata` cannot be resolved
at startup the log has one warning. An older server needs a Windows update, not a code change.

### 12.5 Troubleshooting

| Symptom | Likely cause |
|---|---|
| `503 extraction-unavailable` | One of `OpenRouter__Enabled`, `OpenRouter__ApiKey`, `Extraction__Enabled` is missing, or the API was not restarted |
| `502` / `504` on upload | OpenRouter unreachable or slow: check outbound HTTPS and the key; nothing was stored |
| `429` | The user passed `Extraction:MaxPerUserPerHour` |
| `413` / `422` | Over 10 MB or 50 pages, not a PDF, protected, no text layer, or not a bank statement |
| The save says the record expired | More than `Extraction:RecordHours` passed; upload again |

## 13. Local Docker stack for dashboard integration (T-1303)

**Not a deployment.** QuickBooks Desktop needs Windows COM, so production stays the Windows service of sections 1–12.
The `Dockerfile` at the repository root builds a Linux image that runs the **simulated QuickBooks company**
(`QuickBooks:Fake=true`, allowed only with `ASPNETCORE_ENVIRONMENT=Development`), for developing and testing the
dashboard against the real API.

- The stack is defined in the dashboard repository: `D:\qb_post_react\docker-compose.integration.yml` (PostgreSQL 16,
  a one-shot `psql` step that applies `deploy/migrations.sql`, this API, an OpenRouter stand-in on the API's loopback,
  and the dashboard behind nginx on `http://localhost:8080`).
- Every secret is a throwaway value in that compose file and reaches the API as a `QBAUTOPOST__*` variable; nothing is
  baked into the image.
- The QuickBooks name lists come from `docker/seed/qb-lists.json` in the dashboard repository, mounted read-only at
  `/data/qb-lists.json` (the simulated company starts with empty lists).
- To read real PDFs with the real model, set `OPENROUTER_BASE_URL=https://openrouter.ai/api/v1` and
  `OPENROUTER_API_KEY` before `docker compose up`.

```
cd D:\qb_post_react
docker compose -f docker-compose.integration.yml up -d --build
npx playwright test -c playwright.integration.config.ts
docker compose -f docker-compose.integration.yml down -v   # -v also drops the database volume
```
