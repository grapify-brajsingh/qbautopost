# steps.md — testing QbAutopost on the QuickBooks server

A tick-box run through the POC on the Windows server where QuickBooks Enterprise 24 is installed.
Work top to bottom; every step says what you should see. `README-POC.md` (in the package) explains the same
steps in prose, `docs/runbook.md` is the full operator guide.

Nothing here touches a real company file: you create a separate test company.
Fill in the result table at the end and send the failed rows back with the log file named in step 0.5.

---

## 0. Before you start

- [ ] **0.1** You are signed in to the server as the Windows user that will run the app (Remote Desktop is fine).
- [ ] **0.2** QuickBooks Enterprise 24 is installed and you can open a company file.
- [ ] **0.3** You have `qbautopost-poc-win-x64.zip` on the server (from `D:\qb_post\dist\` on the build machine).
- [ ] **0.4** Check the bitness, with QuickBooks running:

  ```powershell
  $p = 'C:\Program Files\Intuit\QuickBooks Enterprise Solutions 24.0\qbw.exe'
  $f = [IO.File]::OpenRead($p); $b = New-Object byte[] 4096; [void]$f.Read($b,0,4096); $f.Close()
  $pe = [BitConverter]::ToInt32($b,0x3C)
  switch ([BitConverter]::ToUInt16($b,$pe+4)) { 0x8664 {'x64'} 0x14C {'x86'} default {'other'} }
  ```

  **Expect `x64`.** If it prints `x86`, stop and ask for the `win-x86` package; the app must match QuickBooks.
- [ ] **0.5** Note where the log will be: `C:\qb-autopost\logs\qbautopost-<yyyyMMdd>.log`. Every step below is logged there.

## 1. Create the test company (QuickBooks)

- [ ] **1.1** QuickBooks → **File → New Company → Detailed Start** (Express Start is fine too).
- [ ] **1.2** Company name exactly: `Tropicana Properties LLC` — the app refuses the job if the name differs.
      Industry *Other/None*, any tax form.
- [ ] **1.3** Save it anywhere on the server. The packaged settings point at
      `D:\Accountexx Data\Quickbook - Accounting File\Quickbook - Accounting File\18_Takoma Park Grocery Store Inc\Tropicana Properties LLC.qbw`;
      a different location means editing `Company:FilePath` in `app\appsettings.json` (step 2.3).
- [ ] **1.4** **File → Utilities → Import → IIF Files** → `poc\tropicana-lists.iif` from the package.
      Expect "your data has been imported".
- [ ] **1.5** Check **Lists → Chart of Accounts**: *Chase Checking 4521* (Bank), *Chase Sapphire 7788* (Credit Card),
      *Rental Income*, *Repairs and Maintenance*, *Office Supplies*, *Utilities*, *Automobile Expense*,
      *Insurance Expense*, *Ask My Accountant*.
      **Vendors** (Vendor Center): Home Depot, Amazon, Shell, Florida Power & Light, City Water Dept,
      Allstate Insurance, Staples. **Customers**: Palm Court Rentals LLC, Sunrise Property Mgmt.
      Anything missing: create it by hand with exactly that name and type.
- [ ] **1.6** Stay **signed in as Admin, single-user mode** (the File menu shows "Switch to Multi-user Mode"),
      with this company open. Needed only for the first connection.

## 2. Install the app

```powershell
# unzip the package to C:\qb-autopost   -> C:\qb-autopost\app, \poc, \scripts, README-POC.md
Copy-Item C:\qb-autopost\poc\appsettings.json C:\qb-autopost\app\appsettings.json -Force
Copy-Item C:\qb-autopost\poc\rules.json       C:\qb-autopost\rules.json -Force
New-Item -ItemType Directory -Force C:\qb-jobs | Out-Null
Copy-Item C:\qb-autopost\poc\jobs\2026-09-tropicana C:\qb-jobs\ -Recurse -Force
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Api__ApiKey', 'poc-' + [guid]::NewGuid().ToString('N'), 'User')
```

- [ ] **2.1** The four copies ran without error.
- [ ] **2.2** **Close PowerShell and open a new window** (so the key is in the environment).
      Check with `$env:QBAUTOPOST__Api__ApiKey` — it must print a value.
- [ ] **2.3** If you saved the company file elsewhere, edit `Company:FilePath` in `C:\qb-autopost\app\appsettings.json`.

## 3. Start the app

- [ ] **3.1** In **window 1** (leave it open for the whole test):

  ```powershell
  C:\qb-autopost\app\QbAutopost.Api.exe
  ```

- [ ] **3.2** The console shows, in this order: `QbAutopost … starting: … x64 process`, `Settings: …`,
      `QuickBooks: app name QbAutopost …`, `Hermes: disabled …`, `QuickBooks gateway: Sdk`,
      `QbAutopost ready, listening on http://127.0.0.1:5080`.
- [ ] **3.3** `Company file … (found)` — if it says `missing`, fix `Company:FilePath` and restart.
- [ ] **3.4** It does **not** exit. `Api:ApiKey is not set` means step 2.2 did not take effect.

Open **window 2** for the rest: `cd C:\qb-autopost`.

## 4. First connection to QuickBooks (the important one)

- [ ] **4.1** Run:

  ```powershell
  .\scripts\qb-server-check.ps1 -Step health
  ```

- [ ] **4.2** **Switch to the QuickBooks window.** It shows *Application Certificate* for `QbAutopost`:
      choose **"Yes, always; allow access even if QuickBooks is not running"**, select the **Admin** user,
      **Continue** → **Done**. (The command waits meanwhile.)
- [ ] **4.3** Back in window 2, expect:

  ```json
  { "ok": true, "companyFile": "C:\\qb-autopost\\company\\Tropicana Properties LLC.qbw",
    "message": "QuickBooks Enterprise Solutions 24.0 …" }
  ```

- [ ] **4.4** Record the product line from `message` here: ________________________________________
- [ ] **4.5** If it fails, match the log against this table and stop:

  | Log line | Meaning | Do |
  |---|---|---|
  | `not registered for this x64 process` | bitness mismatch | get the `win-x86` package |
  | last line is `QuickBooks SDK: BeginSession on '…'` | QuickBooks is waiting on a dialog | answer it on the server's desktop, run 4.1 again |
  | `could not open a QuickBooks session` | QuickBooks closed, another company open, or the certificate was refused | open the right company; re-allow under *Edit → Preferences → Integrated Applications → Company Preferences* |

## 5. Read the QuickBooks lists

- [ ] **5.1** `.\scripts\qb-server-check.ps1 -Step sync`
- [ ] **5.2** Expect counts above zero and **`missingInRules` empty**.
      Any name listed there is missing in QuickBooks (step 1.5) — create it, run 5.1 again.

## 6. Dry run (nothing is written to QuickBooks)

- [ ] **6.1** `.\scripts\qb-server-check.ps1 -Step dryrun -Folder C:\qb-jobs\2026-09-tropicana`
- [ ] **6.2** Expect `"status": "ready"`, `"toPost": 15`, `"held": 0`, `"skipped": 1`
      (the skipped line is the card's AUTOMATIC PAYMENT).
- [ ] **6.3** Look in `C:\qb-jobs\2026-09-tropicana\output\`:
      `analysis.json` (every line with its account and payee), `request.qbxml` (exactly what will be sent),
      `batch-enter-checks.csv` / `batch-enter-creditcard.csv` / `batch-enter-deposits.csv`
      (the same lines in *Batch Enter Transactions* layout, if you want to compare by hand).
- [ ] **6.4** Check in QuickBooks that **nothing** has been created yet.

## 7. Post

- [ ] **7.1** `.\scripts\qb-server-check.ps1 -Step post -JobId 2026-09-tropicana -ConfirmCopy`
- [ ] **7.2** Expect `"status": "posted"`, `"posted": 15`, `"held": 0`, batch id `2026-09-tropicana#1`.
- [ ] **7.3** Write the batch id here (needed for the undo): ______________________________
- [ ] **7.4** Verify in QuickBooks — **Banking → Use Register**:

  | Register | Expect |
  |---|---|
  | Chase Checking 4521 | **10** entries: 7 Checks (2026-09-02 transfer 1,200.00; 09-04 check no. 1044 650.00; 09-08 FPL 298.17; 09-09 Home Depot 212.64; 09-11 City Water Dept 86.40; 09-15 Allstate 415.00; 09-16 Home Depot 96.18) and 3 Deposits (09-01 2,400.00; 09-12 1,850.00; 09-17 600.00) |
  | Chase Sapphire 7788 | **5** entries: charges 09-03 Amazon 54.99, 09-07 Shell 61.20, 09-10 Staples 38.45, 09-16 Shell 58.90, and a credit 09-12 Amazon 22.50 |

- [ ] **7.5** Open one check (e.g. FPL): payee *Florida Power & Light*, expense account *Utilities*,
      memo = the statement text. The check no. 1044 line has **no payee** and account *Ask My Accountant* by design.
- [ ] **7.6** The deposits are posted to *Rental Income* with the customer in *Received From*.

## 8. Duplicate protection

- [ ] **8.1** Run the same folder again, forced:
      `.\scripts\qb-server-check.ps1 -Step dryrun -Folder C:\qb-jobs\2026-09-tropicana -Force`
- [ ] **8.2** Expect `"toPost": 0` and `"skipped": 16` — 15 lines `already-posted` (the app knows it posted them)
      plus the AUTOMATIC PAYMENT line as before.
- [ ] **8.3** Optional, the live check: in QuickBooks delete one posted check by hand, then repeat 8.1.
      That one line comes back as `toPost: 1`; the rest stay skipped.

## 9. Undo

- [ ] **9.1** `.\scripts\qb-server-check.ps1 -Step undo -BatchId '2026-09-tropicana#1' -ConfirmCopy`
- [ ] **9.2** Expect `"deleted": 15`, `"failed": []`.
- [ ] **9.3** Both registers in QuickBooks are empty again (except anything you entered by hand).

## 10. Behaviour when QuickBooks is away

- [ ] **10.1** Close QuickBooks completely.
- [ ] **10.2** `.\scripts\qb-server-check.ps1 -Step health` → fails with a clear message, and the app keeps running.
- [ ] **10.3** Reopen QuickBooks on the test company; `-Step health` → `ok: true` again.
      (No certificate dialog this time.)

## 11. Finish

- [ ] **11.1** Stop the app in window 1 with Ctrl+C.
- [ ] **11.2** Keep `C:\qb-autopost\logs\qbautopost-<date>.log` and the job's `output\` folder.
- [ ] **11.3** Result:

  | Step | Worked? | Note |
  |---|---|---|
  | 4 connect | | |
  | 5 lists | | |
  | 6 dry run | | |
  | 7 post + registers | | |
  | 8 duplicates | | |
  | 9 undo | | |
  | 10 QuickBooks closed | | |

**If a step fails:** send the last ~50 lines of the log
(`Get-Content C:\qb-autopost\logs\qbautopost-*.log -Tail 50`), plus `output\response.qbxml` if the post failed.
The log never contains keys.

## Next, with your own data

Copy a folder like the sample into `C:\qb-jobs\`: a `requirement.txt` naming the company and the last four digits
of each account, and CSV statements in `statements\`. Add rules for your vendors in `C:\qb-autopost\rules.json`
(`docs/runbook.md` §3.3). Lines no rule covers are held, never guessed. PDF statements and invoices need the AI
part, which is off in this POC (`Hermes:Enabled: false`).
