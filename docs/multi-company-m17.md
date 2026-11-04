# Spec — Multiple QuickBooks company files (M17)

**Date**: 2026-11-03 · **Status**: approved by the owner 2026-11-04 · **Milestone**: M17 (tasks T-1700 …)
**Plan**: `docs/multi-company-m17-plan.md`
**Source**: owner request 2026-11-03 (brainstorm, branch `feature/9-addition-of-company-file`): a **Company** page in
the dashboard; a folder of QuickBooks company files is listed and synced to the backend, with a Sync button for the
same location; an uploaded statement PDF is matched to its company from the **PDF content** and posted to that
company's file. Examples: `Bank statement 18 ; Takoma park grocery store inc.pdf` belongs to *Takoma Park Grocery
Store Inc*; `Credit card 440 ; S & A CONVENIENCE MARKET LLC.pdf` belongs to *S & A Convenience Market LLC*.
**Dashboard side**: `D:\qbautopost\qb_post_react\docs\superpowers\specs\2026-11-03-multi-company-spec.md`,
requirements **R-18, R-19, R-20** in `D:\qbautopost\qb_post_react\docs\api-requirements-for-dashboard.md`.
**Supersedes**: Q-44 ("one installation = one company") and, for v2 routes, Q-50 (company-file override off). A new
ADR-0013 records the decision.

---

## 1. Goals, non-goals, anti-goals

**Goals**
1. One installation serves many QuickBooks Desktop company files. They are found by scanning a server folder named
   in `appsettings.json`; the scan can be re-run from the dashboard ("Sync").
2. Every uploaded PDF is matched to a company from the business name **printed in the PDF**. One confident match
   assigns it; anything else holds the statement as *needs company* until a person picks one.
3. Validation, list sync, duplicate checks, rules and posting all run against the statement's own company.
4. The app never interferes with a person using QuickBooks: if a different company file is open, it refuses to post
   and says which file is open and which one is needed.

**Non-goals (this milestone)**
- The statement file name is **never** used for matching (owner, 2026-11-03).
- Credit-card row types (charges, payments, refunds). A card statement is matched to its company, but its rows are
  extracted exactly as today (`Deposit`/`Check`). Separate milestone.
- Multi-company for the v1 job-folder pipeline (`/jobs`, `JobPipeline`) and v1 `/quickbooks/*` routes: they keep
  using the *default company* (§4.4) and stay byte-identical (`V1ContractPinTests`).
- n8n / email intake changes (owner, 2026-11-04). Emailed statements are held as *needs company* when there are
  several companies.
- Per-company user permissions (every signed-in user sees every company).
- Browsing arbitrary server folders from the browser.

**Anti-goals (failure even if it works)**
- Posting a statement to a company it was not assigned to, or guessing a company the matcher was not sure of.
- Closing, switching or otherwise driving the QuickBooks UI; retrying a refused post automatically.
- Opening any `.qbw` outside `Companies:RootFolder` (and the existing default company file).
- The model deciding the company. The model only reads the printed name; code decides the match (ADR-0004 spirit).
- One company's rules, lists, ledger or duplicate fingerprints affecting another company.

## 2. What exists today (read from the code)

- `CompanySettings { Name, FilePath, RulesFile }` (`src/QbAutopost.Api/Configuration/AppSettings.cs:197`) — one
  company per process. `QuickBooksSettings.AllowCompanyFileOverride` (:236, false) and `AllowedCompanyFolders` (:239).
- `QbConnection.CreateSdkGateway` (`src/QbAutopost.Api/QuickBooks/QbConnection.cs:74`) fixes
  `QbGatewayOptions.CompanyFile = settings.Company.FilePath`. `QbGateway` opens one `QbSession` per call on its own
  STA thread (no persistent session), so the file can change per call cheaply.
- Gateway chain (singletons, `Program.cs:181-231`): `QbGateway` → `LoggingQbGateway` → `ResilientQbGateway`, which
  holds the one `SemaphoreSlim(1,1)` lock. `IQbGateway` has `ProcessAsync(qbxml)` and `CurrentCompanyFileAsync()` —
  no company parameter.
- `PipelineOptions` singleton (`Program.cs:237-247`) carries `CompanyName`, `RulesFile`, `LedgerFile`,
  `QbListsFile`. `Rules.Load(options.RulesFile)` is called in `JobPipeline:33`, `QbListSync:40`,
  `DirectPostRunner:35`, `JobEndpoints:74`, `QuickBooksEndpoints:188`. `RulesEditor(s.Company.RulesFile, …)`
  (`Program.cs:293`).
- List sync (M14/M15): `QbListSync`, `IQbListsStore`/`IQbListsSource`, `DbQbListsSource` (one in-memory current
  list), `EfQbListsStore`, table `qb_list_syncs` (no company column). One `ListSyncSchedule` for the app;
  `JobWorker.RunScheduledSyncAsync` claims the idle slot of `JobQueue`.
- Saved requests: `saved_requests` and `statement_extractions` have no company column. Duplicate checks:
  `DuplicateDetector` via `SavedRequestIntake.DetectAsync` and `EfSavedRequestStore.DuplicateCandidatesAsync`
  (app-wide); upload duplicates via `UploadDuplicateCandidatesAsync`; posting-time ledger `Paths:Ledger` and
  `LiveDuplicateGate` (app-wide file).
- Extraction: `StatementExtractionService.ExtractAsync`; embedded `extraction-prompt.json`, `promptVersion`
  `2026-10-28.api-1` (pinned by a SHA test); no company fields.
- Posting: `SavedRequestPoster` → `SyncPostRunner` (`queue.TryRunExclusiveAsync`) → `DirectPostRunner`.
- Scopes: `DashboardRoles` (viewer/editor/admin), route map `ApiRoutes.ScopeFor` (unmapped `/quickbooks/*` falls to
  `qb:read`, other unmapped paths to admin). Latest migration `20261029154406_QbListSyncs`.
- Sample PDFs (read 2026-11-03): the bank statement prints `TAKOMA PARK GROCERY STORE INC` / `DBA ZEMEN MARKET`; the
  card statement prints `SA CONVENIENCE MARKET` (no "&", no "LLC").

## 3. QuickBooks Desktop facts this design rests on

- **No per-company login like QuickBooks Online.** Each company file grants the app access **once**: a QuickBooks
  admin opens that file and accepts the *QbAutopost* certificate, choosing *"Yes, always; allow access even if
  QuickBooks is not running"* and a QuickBooks user to log in as. N company files = N one-time approvals. Until
  then `BeginSession` on that file fails or waits on a dialog.
- **One company file open at a time.** If QuickBooks has file A open, `BeginSession(B)` fails. If QuickBooks is
  closed, the SDK opens B in the background (only for files approved "even if QuickBooks is not running") — which
  can take tens of seconds for a large file.
- The exact COM error for "a different company file is open" and the background-open behaviour are verified on the
  server in T-1707 before anything depends on the error code.

## 4. Behaviour

### 4.1 Configuration

New section (`CompaniesSettings`), validated at start-up:

```json
"Companies": {
  "RootFolder": "D:\\Accountexx Data\\Quickbook - Accounting File\\Quickbook - Accounting File",
  "DataFolder": "data/companies",
  "RulesTemplate": "data/rules.json",
  "ScheduledSync": "waiting-only"
}
```

- `RootFolder`: absolute server path. Empty = multi-company off (the app behaves exactly as today, one default
  company; the Companies page shows "not configured").
- `DataFolder`: per-company files live in `<DataFolder>/<companyId>/` (`rules.json`, `ledger.json`, and in no-database
  mode `qb-lists.json`). Resolved like other `Paths`.
- `RulesTemplate`: copied to a new company's `rules.json` when the company is first created (owner: "each company gets
  a copy of rules"). Copied once; later edits to the template do not propagate.
- `ScheduledSync`: `waiting-only` (default) syncs lists on schedule only for companies that have pending statements or
  have never synced; `all` sweeps every approved company; `off`. *(builder call — opening 20 files every 30 minutes is
  costly.)*
- `RootFolder` is implicitly added to the allowed folders for v2 company operations; `AllowedCompanyFolders` and
  `AllowCompanyFileOverride` keep governing v1 routes only.

### 4.2 Folder scan

`CompanyFolderScanner` (Core, file-system access behind a small interface so tests use temp folders):

- Looks **one level down**: every immediate subfolder of `RootFolder`; inside it, `*.qbw` files directly in that
  subfolder (case-insensitive extension; not recursive). QuickBooks side files (`.qbw.tlg`, `.nd`, `.qbb`, `.qbm`,
  `.dsn`) never match because only the `.qbw` extension counts.
- Subfolder with **one** `.qbw` → a company. With **none** → reported as `no-company-file`, no company. With **more
  than one** → a company in state `ambiguous`, listing the candidate files; unusable until an admin chooses one
  (`PATCH`, §5.1).
- Identity key: the subfolder name (relative to the root). Re-scan updates the file path if the `.qbw` was renamed;
  a subfolder that disappeared marks its company `missing` (never deleted — statements point at it). It returns to
  `active` if it reappears.
- From the subfolder name `18_Takoma Park Grocery Store Inc`: `code` = leading digits before `_` (`18`, may be null),
  `displayName` = the rest (`Takoma Park Grocery Store Inc`).
- The scan touches only the file system — never QuickBooks — so it is fast and does not need the job queue.
- Root missing or unreadable → 503 problem `company-root-unavailable` (path in the detail; no partial updates).

### 4.3 Company record (`companies` table)

| Column | Notes |
|---|---|
| `id` | int identity; public id in the API |
| `folder_name`, `code`, `display_name` | from the scan |
| `file_path`, `candidate_files` (jsonb) | chosen `.qbw`; candidates when ambiguous |
| `state` | `active`, `ambiguous`, `missing`, `disabled` (admin) |
| `access` | `unchecked`, `approved`, `needs-approval`, `file-not-found`, `error` + `access_checked_utc`, `access_message` |
| `qb_company_name`, `qb_legal_name` | read by `CompanyQueryRq` during an access check |
| `match_names` (jsonb) | extra names an admin adds (aliases, DBA names) |
| `is_default` | exactly one row; v1 routes and legacy data use it |
| `created_utc`, `updated_utc`, `row_version` | |

### 4.4 Default company and migration of existing data

- Start-up bootstrap (`CompanyBootstrap`, idempotent): if `companies` is empty, create the **default company** from
  `Company:Name` / `Company:FilePath` (`is_default = true`; if it lies under `RootFolder` the later scan adopts it by
  folder name instead of duplicating it).
- **The default company keeps the original files** *(builder call, review finding 2)*: its rules file is
  `Company:RulesFile`, its ledger `Paths:Ledger` and its company file `Company:FilePath` — no copies — so v1 and v2
  share one rule set and one ledger and a v1 post is always seen by v2's duplicate check. If a scan adopts the
  default company's folder and finds a different `.qbw` there, the company becomes `ambiguous` until an admin
  resolves it (and `Company:FilePath` is updated by hand). Only companies created by a scan get
  `<DataFolder>/<id>/rules.json` (copied from `RulesTemplate`) and `<DataFolder>/<id>/ledger.json` (new, empty).
- The same bootstrap, only in the run that created the default company, sets `company_id` = default on every existing
  `saved_requests`, `statement_extractions` and `qb_list_syncs` row. Rows created later with no company stay null.
- With no database (`FileQbLists` mode) multi-company is off: the default company only.

### 4.5 Matching a PDF to a company

1. **Prompt** (`extraction-prompt.json`, new `promptVersion` `2026-11-03.api-2`, SHA test updated): two new
   optional top-level fields `accountHolderName` (the business name printed as the account holder in the statement
   header) and `dbaName` (a "DBA …" name if printed). Rule text: copy exactly as printed; omit if not printed; never
   take it from the file name. The file name is not sent to the model as a hint for this field.
2. **Matcher** (`CompanyNameMatcher`, Core, pure):
   - Normalise: upper-case; `&` and every non-letter/digit → space; drop the tokens `LLC L L C INC INCORPORATED CORP
     CORPORATION CO COMPANY LTD LIMITED THE DBA`; join remaining tokens **without spaces**.
     `S & A CONVENIENCE MARKET LLC` → `SACONVENIENCEMARKET` = `SA CONVENIENCE MARKET`.
   - A company's names: `display_name`, `qb_company_name`, `qb_legal_name`, each of `match_names`.
   - Printed names: `accountHolderName`, `dbaName`.
   - A company matches if any printed name equals any of its names after normalising. **Exact equality only** — no
     fuzzy distance. Only the consecutive tokens `L L C` (from `L.L.C.`) are dropped as a group, never a lone `L`
     or `C`. A name that normalises to nothing never matches. (A `DBA ZEMEN MARKET` match works once *Zemen Market* is an alias or the QuickBooks company name.)
   - Result: `matched` (exactly one active company), `none`, or `ambiguous` (more than one), plus the printed names
     and candidate ids. Companies in state `missing`/`disabled` never match; `ambiguous` (file) companies may match.
3. The match is stored on the extraction row and returned in the extraction response (§5.3).

### 4.6 Statements and the *needs company* hold

- `saved_requests.company_id` (nullable FK) and `company_source`: `matched`, `chosen`, `migrated`, `default`.
- `saved_requests.company_match` (jsonb, nullable): `{ match, printedName, dbaName, candidates }` copied from the
  extraction (upload) — so the statement page can show what was printed
  *(review finding 4)*.
- **Multi-company off** (`RootFolder` empty) **or exactly one active company**: every new statement gets that
  company automatically, source `default` *(builder call, review finding 1)* — so a single-company install behaves
  exactly as today. The rules below apply only when there are two or more active companies.
- On upload save the company is: the client's `companyId` if sent (source `chosen`), else the match if `matched`
  (source `matched`), else null.
- n8n / API saves: **unchanged this milestone** (owner, 2026-11-04: "leave the n8n for now"). They get the
  default company when multi-company is off or one company is active; otherwise null (*needs company*).
- **Needs company** = `company_id` is null. It is not a new status (the status stays `pending`); it is a hold:
  - `allowedActions` omits `validate` and `post` and includes the new `set-company` (for `requests:edit`, while
    `pending`).
  - `POST …/validate` and `POST …/post` answer **409** `company-required` regardless.
- `POST /saved-requests/{id}/company` sets or changes it (`If-Match`, only while `pending` and no attempt is
  `in-progress`): clears the stored validation (lists differ per company), re-runs duplicate detection, writes an
  audit event `company-assigned` (old → new, source `chosen`).
- Duplicate detection: `same-payload` stays app-wide; `same-reference` and `overlapping-rows` only compare
  statements of the same company (unassigned statements are compared with nothing for these two).

### 4.7 Company context everywhere

- A `CompanyContext` record (`Id`, `DisplayName`, `FilePath`, `RulesFile`, `LedgerFile`) replaces the single
  `PipelineOptions.CompanyName/RulesFile/LedgerFile` for v2 work. `ICompanyResolver` returns it by id (and the default
  for v1 routes). `DirectPostRunner`, `QbListSync`, `CompanyFileValidator`, `RulesEditor` (v2 use) and
  `SavedRequestPoster` take the context of the statement/company they act on.
- **Gateway**: `IQbGateway` calls carry the target company file (builder chooses the shape — a parameter or a
  per-call target — but there stays **one** `ResilientQbGateway` and one lock for all companies).
  `SimulatedQbGateway` keeps one `SimulatedQuickBooks` per company file.
- **Which companies may open a QuickBooks session** *(builder call, review finding 3)*: only state `active` with
  access `approved`. The one exception is the admin's explicit **Check access**, which is how a file becomes
  `approved`; it answers `needs-approval` when the existing busy timeout (`QuickBooks:BusyTimeoutSeconds`) passes. A
  COM call cannot be cancelled, so the gateway lock stays held until someone answers or closes the dialog on the
  server (FR-11; measured in T-1707, runbook tells the admin to answer it). The default company is created `active` +
  `approved`, so an upgraded install keeps posting. Validate, post and list sync of any other company
  answer **409** `company-not-ready` with `state` and `access` (no session opened); `allowedActions` omits
  `validate`/`post` for such a statement. The scheduled sweep only considers `active` + `approved` companies.
- **Wrong company open**: the SDK error from `BeginSession` when another file is open becomes
  `QuickBooksWrongCompanyException` → attempt outcome `unavailable`, problem **409** `company-file-open-elsewhere`
  with `openFile` (when the SDK can report it) and `neededFile`. No retry. If T-1707 shows the SDK error cannot be told apart from other session failures, the app does not guess:
  any `BeginSession` failure for a company becomes attempt `unavailable`, no retry, problem **503**
  `company-file-unavailable` carrying the SDK message, and `company-file-open-elsewhere` is used only when the cause
  is certain *(builder call, review finding 5)*. QuickBooks closed + file approved → the
  SDK opens it in the background (allowing for the longer open time within `SyncPostTimeoutSeconds`).
- **Lists**: `qb_list_syncs.company_id`; `DbQbListsSource` keeps current lists per company; `MissingInRules` uses
  that company's rules. Validation uses the statement company's lists and rules.
- **Scheduled sync** (one timer, the single `JobWorker`): each tick walks the companies selected by
  `ScheduledSync`, one at a time through `JobQueue`; a company whose file cannot be opened right now gets a
  `skipped` row with reason `company-file-open-elsewhere` or `quickbooks-not-open`; the sweep continues.

## 5. API (v2)

### 5.1 Companies (R-18)

| Route | Scope | Notes |
|---|---|---|
| `GET /api/v2/companies` | `lists:read` | `{ rootFolder, rootConfigured, rootReachable, lastScanUtc, items: Company[] }` |
| `POST /api/v2/companies/scan` | `requests:admin` | runs §4.2; `{ added, updated, missing, ambiguous, noCompanyFile: string[], items }`; audit `companies-scanned` |
| `POST /api/v2/companies/{id}/check` | `requests:admin` | through `JobQueue`: open a session on the file, `CompanyQueryRq`, record `access` and QuickBooks names; audit `company-checked`. 409 `company-file-open-elsewhere`, 503 `quickbooks-not-open`. |
| `PATCH /api/v2/companies/{id}` | `requests:admin` | `If-Match` row version; body any of `matchNames`, `filePath` (one of `candidateFiles`), `disabled`; audit `company-updated` |

`Company` = `{ id, code, displayName, folderName, fileName, filePath, state, candidateFiles, access,
accessCheckedUtc, accessMessage, qbCompanyName, qbLegalName, matchNames, isDefault, rowVersion, lists: { syncedUtc, outcome } |
null, waitingCount }` (`waitingCount` = pending statements of that company).

### 5.2 Statements (R-19)

- `RequestSummary` gains `company: { id, displayName, code } | null` and `companySource`; `RequestDetail` gains
  `companyMatch: { match, printedName, dbaName, candidates } | null`.
- `GET /saved-requests` and `GET /saved-requests/stats` accept `company=<id>` or `company=none`.
- `Stats` gains `needsCompany` (count of pending statements with no company).
- New action `set-company`; new route `POST /saved-requests/{id}/company` `{ companyId }` (`requests:edit`).
- `POST /saved-requests` (upload) accepts optional `upload.companyId` beside `upload.extractionId`. The n8n/API save body is unchanged.
- 409 `company-required` from validate and post when no company; 409 `company-not-ready` (`state`, `access`) when
  the company is not `active` + `approved`.

### 5.3 Extraction, lists, summary (R-20)

- `POST /statement-extractions` answer gains
  `company: { match: 'matched'|'none'|'ambiguous', printedName, dbaName, companyId|null, candidates: CompanyRef[] }`.
- `GET /quickbooks/lists`, `POST /quickbooks/lists/sync` and `GET /quickbooks/lists/syncs` take `company=<id>`
  (query; for sync also accepted in the body). Missing → the default company (keeps the existing dashboard working).
- `GET /dashboard/summary` and `GET /audit-events` accept `company=<id>`; audit events carry `companyId` when known.
- Every new route is mapped explicitly in `ApiRoutes.ScopeFor` (no fallback).

## 6. Questions (builder's call; owner may overrule)

| Q | Question | Call |
|---|---|---|
| a | Subfolder with several `.qbw` | `ambiguous` company; admin chooses the file |
| b | Who scans / checks / edits companies | `requests:admin` (admins); assigning a company to a statement is `requests:edit` |
| c | Fuzzy name matching | No — exact after normalising; aliases cover the rest |
| d | Scheduled list sync with many companies | `waiting-only` default |
| e | Same-reference / overlapping-rows duplicates across companies | Not flagged (same company only) |
| f | What a missing `company` query means on lists/summary | The default company for lists; all companies for summary/audit |
| g | Changing company after validation | Allowed while `pending`; clears validation |
| h | Company of new statements when multi-company is off or only one company is active | That company, source `default` |
| i | Default company's rules, ledger and file | The original configured files, never copies |
| j | Opening sessions for unapproved / missing / ambiguous companies | Refused (`company-not-ready`) except the admin's Check access |
| k | Wrong-company error not distinguishable on the server | Generic `company-file-unavailable`, no retry |

## 7. Acceptance

- **A-1** With `RootFolder` holding `18_Takoma Park Grocery Store Inc\Takoma Park Grocery Store Inc.qbw` and
  `440_S & A Convenience Market LLC\S & A Convenience Market LLC.qbw`, `POST /companies/scan` returns two active
  companies with codes `18` and `440`; a second scan returns `added: 0`.
- **A-2** Removing a subfolder and scanning marks that company `missing`; its statements keep their company.
- **A-3** A subfolder with two `.qbw` files gives one `ambiguous` company with both candidates; `PATCH filePath`
  makes it `active`.
- **A-4** The matcher maps `TAKOMA PARK GROCERY STORE INC` → Takoma and `SA CONVENIENCE MARKET` → S & A (unit tests
  with both sample header texts), and returns `none` for an unknown name and `ambiguous` when two companies share a
  normalised name.
- **A-5** An upload whose PDF header has no recognisable name saves with `company: null`; validate and post answer
  409 `company-required`; `allowedActions` has `set-company` but not `validate`/`post`.
- **A-6** Setting the company clears validation, writes `company-assigned`, and then validate uses that company's
  lists and rules (proved with two fake companies whose lists differ).
- **A-7** Posting a Takoma statement while the fake gateway reports S & A open → 409 `company-file-open-elsewhere`,
  attempt `unavailable`, nothing sent, no retry.
- **A-8** List sync for company 2 never changes company 1's current lists or sync history.
- **A-9** After upgrade, every pre-existing statement, extraction and sync row belongs to the default company; the
  existing dashboard (no `company` parameter) behaves as before.
- **A-12** A statement of a `needs-approval` company gets 409 `company-not-ready` on validate and post, and the
  scheduled sweep never opens that file.
- **A-13** With `RootFolder` empty, a new n8n statement gets the default company (source `default`) and posts as today.
- **A-10** `/api/v1/**` and the v1 OpenAPI document are byte-identical.
- **A-11** [server] On the QuickBooks server: a statement for each of two approved companies posts to the right file
  with QuickBooks closed; with the other company open, the post is refused with the open file named.
