# QbAutopost — Operator Runbook

Version 1.0 · 2026-09-17 · Audience: the person who installs, runs and supports QbAutopost on the QuickBooks server.
Related: `spec.md` (the contract), `tracker.md` (open questions Q-1…Q-41 and the T-609 server checklist), `deploy/README-hermes.md` (Hermes container).

> **Status.** The QuickBooks (COM) path has only run against a simulated company so far. Do the T-609 checklist
> in `tracker.md` on a **copy** of the company file before the first real post.

---

## 1. What it does

QbAutopost is one Windows program with an HTTP API on `http://127.0.0.1:5080`. You give it a **job folder**:

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
| `C:\qb-autopost\qb-audit\` | qbXML copies of list syncs and undos without a job folder |
| `C:\qb-autopost\logs\` | `qbautopost-yyyyMMdd.log`, one per day, 31 kept |
| `C:\qb-jobs\` | job folders (any location works; the API takes an absolute path) |

### 2.3 Publish the program

On a build machine with the .NET 8 SDK (repository root):

```powershell
dotnet publish src/QbAutopost.Api -c Release -r win-x64 --self-contained false -o C:\qb-autopost\app
# QuickBooks older than 2022 (32-bit):  -r win-x86
```

Copy `rules.json` (start from `samples/rules.json`) to `C:\qb-autopost\rules.json`.

### 2.4 Start at logon

1. Start Hermes and check it: `deploy/README-hermes.md`.
2. `deploy/start-all.ps1` starts Docker (`-DockerMode Wsl` on Windows Server, the default, with `-WslDistro Ubuntu`; `-DockerMode Desktop` on Windows 10/11), runs `docker compose up -d` in `deploy\hermes`, waits until Hermes accepts connections on `127.0.0.1:8642`, starts `C:\qb-autopost\app\QbAutopost.Api.exe` (`-AppExe`) unless it already runs, then waits for `GET /health/hermes` = 200. If Hermes stays down, the API is still started (undo and `/health/quickbooks` work; new jobs fail at T1) and the script exits 2. Exit 1 = Docker, compose or the API could not be started. It writes `C:\qb-autopost\logs\start-all-yyyyMMdd.log` and never reads a key.
3. `deploy/install-task.ps1` (run once as the auto-logon account, in an elevated PowerShell) registers the task `QbAutopost`: at logon of that account, 60 s delay, **interactive** session, limited rights, no password stored, no time limit, a second start ignored. Pass start-all options with `-StartAllArguments '-DockerMode Desktop'`; remove it with `-Unregister`.
4. Both scripts accept `-WhatIf` (prints what would happen, changes nothing). Test the task with `Start-ScheduledTask -TaskName QbAutopost`, then sign out and in once.

### 2.5 Going live and more than one company

- A new install posts nothing by itself: `DryRunDefault` is `true`. Go live by setting it to `false` only after the shadow week (tracker T-803/T-804 checklists). A request's own `dryRun` always wins.
- One installation serves **one** company (`Company:*` holds one name and one file) and `start-all.ps1` starts one API. Running several companies on one server is not supported yet (tracker Q-44); until the owner decides, switch companies one at a time.

## 3. Configure

### 3.1 `appsettings.json` (next to the program)

| Key | Default | Meaning |
|---|---|---|
| `Api:Bind` | `http://127.0.0.1:5080` | Keep it on loopback |
| `Api:ApiKey` | `change-me` | **Required.** The host refuses to start with an empty key, or with `change-me` outside Development |
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
| `Hermes:BaseUrl` / `Model` / `TimeoutSeconds` | `http://127.0.0.1:8642` / `default` / `120` | |
| `Hermes:ApiKey` | empty | Hermes' `API_SERVER_KEY` |
| `Ocr:Enabled` / `TessDataPath` | `false` / empty | Scanned PDFs and invoice images need OCR (`eng.traineddata`) |
| `Paths:Ledger`, `QbLists`, `Logs`, `JobIndex` | | See §2.2. Relative paths are relative to the program folder |
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
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/jobs -Headers $h -ContentType 'application/json' `
  -Body '{ "folder": "C:\\qb-jobs\\2026-08-tropicana", "dryRun": true }'
Invoke-RestMethod http://127.0.0.1:5080/jobs/2026-08-tropicana -Headers $h
Invoke-RestMethod 'http://127.0.0.1:5080/jobs?status=ready' -Headers $h
```

The folder name is the job id (letters, digits, `.`, `_`, `-` only). A folder that was already posted is refused with
409; add `"force": true` only after checking QuickBooks (lines already in the ledger are still skipped).

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
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/jobs/2026-08-tropicana/post -Headers $h
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
| `hermes-failed` | Hermes did not give a valid answer | Check `/health/hermes`, re-run |
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
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/rules/alias -Headers $h -ContentType 'application/json' `
  -Body '{ "fragment": "UNKNOWN PLUMBER", "name": "Unknown Plumber LLC", "kind": "vendor" }'
# vendor → expense account
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5080/rules/account -Headers $h -ContentType 'application/json' `
  -Body '{ "vendor": "Unknown Plumber LLC", "account": "Repairs and Maintenance" }'
```

When lists have been synced, the vendor/customer and account must exist in QuickBooks with the **exact** spelling
(400 "Did you mean …" otherwise). Create a new vendor in QuickBooks first, then sync lists, then teach.
The answer shows the previous value when a rule was replaced. Then submit the folder again (or post a `ready` job).

## 7. Undo

```powershell
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5080/batches/2026-08-tropicana%231/undo' -Headers $h
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
Invoke-RestMethod http://127.0.0.1:5080/health/quickbooks   # no key needed
Invoke-RestMethod http://127.0.0.1:5080/health/hermes
```

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
| 401 | Missing or wrong `X-Api-Key` |
| 409 on `POST /jobs` | The job is running, or already in the ledger (use `force` only after checking QuickBooks) |
| Host does not start | Read the console: empty API key, `QuickBooks:Fake` outside Development, missing OCR data, or a missing prompt file |

### 8.6 Logs

`C:\qb-autopost\logs\qbautopost-yyyyMMdd.log`. Each line shows the job id in brackets (`[-]` when not about a job):

```
2026-09-17 09:12:03.114 +00:00 [INF] [2026-08-tropicana] QbAutopost.Api.Jobs.JobWorker: Job 2026-08-tropicana: Post started
```

Search a job with `Select-String -Path C:\qb-autopost\logs\*.log -Pattern '\[2026-08-tropicana\]'`.
For more detail set `Serilog:MinimumLevel:Default` to `Debug` and restart.

## 9. Backing up the app's own state

Back up `C:\qb-autopost\` (ledger, rules, lists, job index) together with the QuickBooks backup. Restoring an older
`ledger.json` makes already-posted lines look new: always run a dry run and rely on the live duplicate check after a restore.
