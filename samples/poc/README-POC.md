# QbAutopost POC — post sample bank and card lines into QuickBooks Enterprise, no AI

What the POC shows: the .NET app reads two bank/card statements (CSV), maps each line to a QuickBooks transaction
by rules, and **posts them into QuickBooks through the QuickBooks SDK** — the same Checks, Credit Card Charges/Credits
and Deposits you would type into *Batch Enter Transactions*. No AI (Hermes) is used: `Hermes:Enabled` is `false`.

> The SDK cannot type into the *Batch Enter Transactions* window itself; it creates the transactions directly, and
> they appear in the registers exactly as if entered there. For a manual comparison the app also writes paste-ready
> sheets (`output\batch-enter-checks.csv`, `batch-enter-creditcard.csv`, `batch-enter-deposits.csv`).

## What is in the package

| Path | What |
|---|---|
| `app\` | The program, self-contained 64-bit (no .NET install needed). `QbAutopost.Api.exe` |
| `poc\appsettings.json` | Settings for the POC (copy over `app\appsettings.json`) |
| `poc\rules.json` | Mapping rules: every sample line resolves by rule |
| `poc\tropicana-lists.iif` | QuickBooks list import: 9 accounts, 7 vendors, 2 customers the rules use |
| `poc\jobs\2026-09-tropicana\` | The sample job: `requirement.txt` + `statements\chase-checking-4521.csv` (10 lines) and `chase-card-7788.csv` (6 lines) |
| `poc\jobs\2026-08-tropicana-xlsx\` | A second job, one **Excel** bank statement (`chase-checking-4521.xlsx`, 9 lines, August 2026): 7 Checks and 2 Deposits, nothing skipped. Run it like the first one; it is a separate job id, so no `-Force` |
| `scripts\qb-server-check.ps1` | Runs each step against the app: health, sync, dryrun, post, undo |

Expected result for the sample job: **15 transactions posted, 1 skipped** (the card's "AUTOMATIC PAYMENT"):
7 Checks and 3 Deposits on *Chase Checking 4521*, 4 Credit Card Charges and 1 Credit Card Credit on *Chase Sapphire 7788*.

## 1. Prepare QuickBooks (on the server, once)

1. Sign in to the server as the Windows user who will run the app (the app and QuickBooks must run in the **same
   Windows session**; do not run the app as a service).
2. QuickBooks Enterprise 24 → **File → New Company → Detailed Start** (or Express Start):
   company name **`Tropicana Properties LLC`** (exactly — the app checks it), industry *Other/None* is fine.
   Save it as `C:\qb-autopost\company\Tropicana Properties LLC.qbw` (create the folders first).
   Using a test company keeps your real company file untouched.
3. **File → Utilities → Import → IIF Files** → choose `poc\tropicana-lists.iif`.
   Check **Lists → Chart of Accounts**: *Chase Checking 4521* (Bank), *Chase Sapphire 7788* (Credit Card),
   *Rental Income*, *Repairs and Maintenance*, *Office Supplies*, *Utilities*, *Automobile Expense*,
   *Insurance Expense*, *Ask My Accountant*. Vendors: Home Depot, Amazon, Shell, Florida Power & Light,
   City Water Dept, Allstate Insurance, Staples. Customers: Palm Court Rentals LLC, Sunrise Property Mgmt.
   (If the import complains about one name, create it by hand with the same spelling and type.)
4. Leave QuickBooks **open on this company, logged in as Admin, in single-user mode** for the first connection.

## 2. Install the app (once)

```powershell
# unzip the package to C:\qb-autopost  (you get C:\qb-autopost\app, \poc, \scripts)
Copy-Item C:\qb-autopost\poc\appsettings.json C:\qb-autopost\app\appsettings.json -Force
Copy-Item C:\qb-autopost\poc\rules.json       C:\qb-autopost\rules.json -Force
New-Item -ItemType Directory -Force C:\qb-jobs | Out-Null
Copy-Item C:\qb-autopost\poc\jobs\2026-09-tropicana C:\qb-jobs\ -Recurse -Force

# an API key of your choice, for this Windows user (the app refuses to start without one)
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Api__ApiKey', 'poc-' + [guid]::NewGuid().ToString('N'), 'User')
```

Close PowerShell and open a new one so the key is visible. If you saved the company file elsewhere, edit
`Company:FilePath` in `C:\qb-autopost\app\appsettings.json`.

## 3. Run

Window 1 — start the app (keep it open; it logs to the console and to `C:\qb-autopost\logs\`):

```powershell
C:\qb-autopost\app\QbAutopost.Api.exe
```

Window 2 — drive it, step by step (`cd C:\qb-autopost`):

| Step | Command | Expect |
|---|---|---|
| 1. Connect | `.\scripts\qb-server-check.ps1 -Step health` | **First time QuickBooks shows the Application Certificate dialog for `QbAutopost`**: choose *Yes, always; allow access even if QuickBooks is not running*, pick the *Admin* user, *Continue*. Then `ok: true` and the company file path |
| 2. Lists | `.\scripts\qb-server-check.ps1 -Step sync` | account/vendor/customer counts; `missingInRules` **empty** |
| 3. Dry run | `.\scripts\qb-server-check.ps1 -Step dryrun -Folder C:\qb-jobs\2026-09-tropicana` | status `ready`, 15 to post, 1 skipped. Nothing written to QuickBooks yet. Review `C:\qb-jobs\2026-09-tropicana\output\` |
| 4. Post | `.\scripts\qb-server-check.ps1 -Step post -JobId 2026-09-tropicana -ConfirmCopy` | status `posted`, 15 TxnIDs, batch `2026-09-tropicana#1` |
| 5. Check | In QuickBooks: **Banking → Use Register → Chase Checking 4521** and **Chase Sapphire 7788** | the 15 transactions with payee, account, amount, memo |
| 6. Undo (optional) | `.\scripts\qb-server-check.ps1 -Step undo -BatchId '2026-09-tropicana#1' -ConfirmCopy` | all 15 deleted from QuickBooks |

`-ConfirmCopy` confirms you are posting to a test company, not the real one.

To run the same folder again: `-Step dryrun … -Force` (a job already in the ledger is refused without it). After an
undo the lines post again; without the undo a re-post finds every line already in QuickBooks and skips it
(duplicate protection).

## 4. If something goes wrong

Look at the last lines of `C:\qb-autopost\logs\qbautopost-<date>.log`.

| Log / message | Cause | Fix |
|---|---|---|
| `QBXMLRP2.RequestProcessor is not registered for this x64 process` | QuickBooks is 32-bit | Ask for the x86 build of the package |
| Log stops after `QuickBooks SDK: BeginSession on '…'` | QuickBooks is showing a dialog (certificate, login, single-user, update) | Answer it on the server's desktop |
| `could not open a QuickBooks session` | QuickBooks not open, other company open, or certificate refused | Open the file in `Company:FilePath`; re-allow the app in *Edit → Preferences → Integrated Applications → Company Preferences* |
| `requirement check (G2) failed … company` | Company name differs | Keep `Company:Name` = `Tropicana Properties LLC` (and in `requirement.txt`) |
| `missingInRules` not empty | A list name is missing or spelled differently | Create it in QuickBooks with exactly that name |
| `Api:ApiKey is not set` | No key | Step 2, open a new PowerShell |
| A line `held … hermes-failed` | A line no rule resolves (AI is off) | Add a rule to `C:\qb-autopost\rules.json` (see `docs/runbook.md` §3.3) |

Your own statements: put a folder like the sample under `C:\qb-jobs\` (a `requirement.txt` naming the company and
the last four digits of the accounts, CSV statements in `statements\`), and add payee/account rules for your
vendors to `rules.json`. PDF statements and invoices need the AI and are held while `Hermes:Enabled` is `false`.
