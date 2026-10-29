# Spec — QuickBooks list sync in the database, on a schedule and from the dashboard (M14)

**Date**: 2026-10-29 · **Status**: draft for the owner's review · **Milestone**: M14 (tasks T-1400 …)
**Source**: owner request 2026-10-29: "sync button in the dashboard", "a background job to sync the list every
30 mins", "use database and worker-run sync". Found while testing against QuickBooks Enterprise 2024 locally
(`grapify-brajsingh/qbpost_dashboard#6`, comment 5: gaps A and B).
**Dashboard side**: `D:\qb_post_react\docs\superpowers\specs\2026-10-29-qb-list-sync-spec.md`, requirement **R-16** in
`D:\qb_post_react\docs\api-requirements-for-dashboard.md`.
**Builds on**: spec FR-15 (sync lists), `docs/spec-api-v1.md` §3 (T-914 lists routes), M10 (PostgreSQL, ADR-0009),
M11 (`/api/v2`, roles and scopes).

---

## 0. Decisions

| # | Decision | By |
|---|---|---|
| D-1 | The synced lists are stored in **PostgreSQL**, not only in `qb-lists.json`. | owner |
| D-2 | A **scheduled sync every 30 minutes**, run by the **existing single `JobWorker`**: no new hosted service, no `System.Threading.Timer`. | owner |
| D-3 | The dashboard gets a **Sync now** button (v2 route). | owner |
| D-4 | Names are **not** fetched from QuickBooks on demand (every drop-down or validate asking QuickBooks live). | owner, from the trade-off below |

### 0.1 Trade-off record: a stored copy vs asking QuickBooks every time

| | Stored copy, refreshed (chosen) | Live call when needed |
|---|---|---|
| Freshness | Up to one interval old (30 min), or until someone presses Sync now | Always current |
| Speed | Memory read | 3–7 s per call (measured: 71 accounts / 282 vendors ≈ 180 KB answer) |
| QuickBooks closed / showing a dialog | Pickers and validate still work | Every page fails, or waits up to ~2 min while QuickBooks shows a dialog |
| Effect on posting | None | The SDK allows one call at a time for the whole process, so every grid load would queue in front of posts |
| Consistency | Validate and post use the same list | The list can change between validate and post |
| Safety | Unchanged: QuickBooks still refuses a name it does not have when the post arrives | Unchanged |

Database rather than the file: the dashboard's data already lives there; it keeps a history of syncs (who, when,
result, error) for the "last synced" display; and the file and its audit copy no longer hold client data on disk.
Cost: the Core readers of `qb-lists.json` move behind an interface (§9).

## 1. Goals and anti-goals

**Goals**
1. One sync code path, started by: the schedule, the dashboard button, the v1 API route, the server script.
2. Every sync attempt is recorded (trigger, who, start, end, outcome, counts, error).
3. Readers (dashboard pickers, validate/post, folder jobs, teach-a-rule) see the newest successful lists without
   opening QuickBooks.
4. The dashboard can show when the lists were last synced, whether the last attempt failed, and when the next
   automatic sync is due.
5. QuickBooks returns only the fields the app uses (gap B).

**Anti-goals** (failure even if it works)
- The scheduled sync **must never start QuickBooks** or open the company file when nobody has it open (§6).
- The scheduled sync **must never run while a job or post is queued or running**, and never delays one by more than
  one sync.
- A failed sync **never** empties or replaces the last good lists.
- No v1 change: `/api/v1/**` responses and the v1 OpenAPI document stay byte-identical (`V1ContractPinTests`).
- No new NuGet package, no new hosted service or timer, no message broker.
- No secret, balance, address or account number stored or logged.

## 2. What exists today (read from the code)

- `QbListQuery.Build` sends `AccountQueryRq`, `VendorQueryRq`, `CustomerQueryRq` with `ActiveStatus=ActiveOnly` and no
  `IncludeRetElement`, so QuickBooks returns every field. `Parse` keeps account `FullName` + `AccountType`, vendor
  `Name`, customer `FullName`.
- `QbListSync.SyncAsync` writes `qb-audit/sync-lists.{request,response}.qbxml`, then `qb-lists.json` (atomic), and
  returns `ListSyncResult(Accounts, Vendors, Customers, MissingInRules)`. Nothing is written when the query fails.
- Routes: `POST /qb/sync-lists`, `POST /api/v1/quickbooks/lists/sync` (`qb:read`, API key), run with
  `JobQueue.RunExclusiveAsync("qb-sync-lists", …)`. `GET /api/v1|v2/quickbooks/lists` read the file (`lists:read`).
  v2 maps only the GET.
- Readers of `QbListsStore(file).Load()`: `JobPipeline`, `DirectPostRunner`, `QuickBooksEndpoints` (validate, GET
  lists), `RulesEditor.LoadListsIfSynced`, `Readiness` (file exists).
- `JobWorker` is a `BackgroundService` that reads `JobQueue`'s channel; exclusive work items run `Work` directly.
- `QbSession` opens `OpenConnection2("", AppName, localQBD)` → `BeginSession(file, DoNotCare)`: when QuickBooks is
  not running this **starts** it (seen 2026-10-29: a second `QBW.EXE` with "Can't Open QuickBooks").
- The Database is optional: with `Database:ConnectionString` empty the app runs and saved-request routes answer 503.

## 3. Storage

New table **`qb_list_syncs`** (migration `QbListSyncs`, `deploy/migrations.sql` regenerated, `deploy/grants.sql`):

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint` identity | PK |
| `trigger` | `text` not null | `schedule`, `dashboard`, `api` (v1 route or server script), `import` (§3.3) |
| `requested_by` | `text` null | user id for `dashboard`, API client name for `api`, null otherwise |
| `started_utc`, `finished_utc` | `timestamptz` not null | |
| `outcome` | `text` not null | `ok`, `failed`, `skipped` |
| `reason` | `text` null | `skipped`: `quickbooks-not-open`, `worker-busy`; `failed`: a short code (`quickbooks-unavailable`, `quickbooks-busy`, `qb-status`, `rules-unreadable`) |
| `error` | `text` null | operator text for `failed` (exception message, never a payload); ≤ 1000 chars |
| `accounts`, `vendors`, `customers` | `int` null | counts, `ok` only |
| `missing_in_rules` | `jsonb` null | string array, `ok` only |
| `lists` | `jsonb` null | the `QbLists` JSON (`syncedUtc`, `accounts[{name,type}]`, `vendors[]`, `customers[]`), `ok` only |

Index on `(outcome, id desc)`.

1. **Current lists** = `lists` of the newest `ok` row. **Last attempt** = the newest row of any outcome.
2. **Retention**: in the same transaction as each insert, delete rows beyond the newest **200**, never deleting the
   newest `ok` row. The app role gets `DELETE` on this table only for that (grants.sql comment). At 48 scheduled rows a
   day this is about four days of history. No timer.
3. **Import**: at startup, when the database has no `ok` row and `qb-lists.json` exists, insert it once as an `ok` row
   with `trigger = import` (counts from the file, `missing_in_rules` = null), so an upgraded server keeps its lists
   before the first sync.
4. With **no database configured**, everything works as today from `qb-lists.json` (file source, §9); sync attempts
   are not stored; `lastSync` is the last attempt since the API started (kept in memory), null before any (Q-124). The schedule still runs; its due time is kept in memory (startup +
   `FirstDelayMinutes`, then last attempt + interval).
5. When the database is configured, the app **no longer writes** `qb-lists.json` (it stays on disk, unused, after the
   import). SPEC-GAP T-1404 → Q-119.

## 4. The sync service (one path)

`QbListSync.SyncAsync(ListSyncTrigger trigger, string? requestedBy, CancellationToken ct)`:

1. Load `rules.json` (unreadable → `failed`/`rules-unreadable`, nothing sent).
2. Build the query (§10), write the audit request copy, send it through `IQbGateway`, write the response copy.
3. Parse; on success save through `IQbListsStore.SaveAsync(run, lists)` (DB: insert the row and prune in one
   transaction, then update the in-memory current lists; file: today's atomic write).
4. Any failure: record a `failed` row (DB), keep the current lists, rethrow to the caller as today (the v1/v2 routes
   map it to their problem answers).
5. Returns `ListSyncResult` (unchanged record) plus the recorded run.

Every caller runs it through `JobQueue`, so it never overlaps a job, a post, an undo or another sync.

## 5. Scheduled sync, run by `JobWorker`

Settings `QuickBooks:ListSync` (§11). `IntervalMinutes = 30`; `0` turns the schedule off.

1. `JobWorker`'s loop waits for **the next work item or the due time, whichever comes first** (a bounded wait inside
   the existing loop: `WaitToReadAsync` raced against the due time; no new hosted service, no `Timer`).
2. **Due time** = last attempt's `finished_utc` (any trigger, any outcome) + interval. At startup with no attempt
   recorded: startup + `FirstDelayMinutes` (2), so a server that starts at logon gives the operator time to open
   QuickBooks.
3. When due: if the channel has a waiting item, run the item instead and re-check afterwards (a job always goes
   first; the sync then runs as soon as the worker is idle). Record nothing for that deferral.
4. Otherwise run the guard (§6); not open → record `skipped`/`quickbooks-not-open` and set the next due time.
5. Then `QbListSync.SyncAsync(schedule, null, ct)` directly on the worker (it already is the single worker).
6. A failure is logged at **Warning** once per failure streak (the first failure after an `ok`), later failures in
   the same streak at **Information**; the next attempt is one interval later (no fast retry).
7. A dialog in QuickBooks can hold `BeginSession` for up to about 2 minutes (seen 2026-10-29). That is the worst
   delay a scheduled sync can add to a job submitted meanwhile; it is logged and documented, not hidden.
8. Host stopping cancels the wait; a sync in progress finishes or is cancelled like an exclusive item today.

## 6. Guard: never start QuickBooks

New `IQbPresence.IsCompanyOpen()` (Core abstraction):

- Windows implementation in `src/QbAutopost.QuickBooks`: true when a `QBW` or `QBW32` process runs in **the API's
  Windows session** (`Process.SessionId`). `[SupportedOSPlatform("windows")]`.
- Fake gateway / non-Windows: true (the simulated company is always "open").
- It cannot tell which file QuickBooks has open; `BeginSession` on `Company:FilePath` still decides that, as today.

Used by the **scheduled** sync and the **dashboard** sync (§7.2). The v1 route and the server script are unchanged
(they keep today's behaviour, including starting QuickBooks: v1 is pinned).

## 7. v2 API

### 7.1 `GET /api/v2/quickbooks/lists` (existing; `lists:read`, every role)

Unchanged fields (`syncedUtc`, `accounts[{name,type}]`, `vendors`, `customers`), plus, **v2 only**:

```json
"lastSync": {
  "trigger": "schedule",
  "outcome": "failed",
  "reason": "quickbooks-not-open",
  "startedUtc": "2026-10-29T15:00:02Z",
  "finishedUtc": "2026-10-29T15:00:02Z",
  "requestedBy": { "id": "…", "displayName": "…" },
  "message": "QuickBooks was not open; the lists from 14:30 are still used."
},
"nextSyncUtc": "2026-10-29T15:30:02Z"
```

- `lastSync`: newest attempt; null before any attempt. With no database it is the last attempt since the API started
  (kept in memory), null before any (Q-124). `requestedBy` only for `dashboard`
  (resolved with the existing user directory, as audit actors are). `message` is fixed operator-safe text per
  outcome/reason, never the raw exception.
- `nextSyncUtc`: due time of the next scheduled sync; null when the schedule is off.
- `syncedUtc` stays the time of the lists in use (newest `ok`), so "synced 12 min ago" and "last attempt failed" can
  both be shown.

### 7.2 `POST /api/v2/quickbooks/lists/sync` (new)

- **Scope `requests:edit`** (editors and admins; viewers 403). API keys: `qb:read`, like v1. SPEC-GAP → Q-120.
- Body: none. Rate limit: the **post** bucket (`Api:RateLimits:PostPerMinute`), as it opens a QuickBooks session.
- Guard (§6) first: not open → **`409 quickbooks-not-open`** (problem detail: "Open QuickBooks on the company file,
  then sync again."), a `skipped` row recorded.
- Then runs through `JobQueue.RunExclusiveAsync("qb-sync-lists", …)` and waits, bounded by
  `Api:SyncPostTimeoutSeconds` (120).
- If a sync is already queued or running, the request **joins it** and answers with its result (no second sync).
- **200**: the same body as §7.1 (fresh lists, `lastSync` = this run, `nextSyncUtc`) plus
  `"counts": { "accounts": 71, "vendors": 282, "customers": 6 }`. `missingInRules` is **not** returned on v2 (an
  operator concern; it stays in the v1 answer and the log).
- QuickBooks failures: the existing mapping of v1's `SyncLists` (`503 quickbooks-unavailable`, `503 quickbooks-busy`,
  `502 qb-status`), plus the `failed` row. Timeout of the wait → `202` is **not** used: answer `504 sync-timeout`; the
  sync continues and its row shows the result.
- Not an audit-feed event (not a money action); the `qb_list_syncs` row is its record. SPEC-GAP → Q-121.
- OpenAPI v2 documents both changes (drift test).

## 8. v1

`POST /api/v1/quickbooks/lists/sync`, `POST /qb/sync-lists`, `GET /api/v1/quickbooks/lists`: same routes, scopes and
bytes. Internally they use the same service (trigger `api`, `requestedBy` = API client name) and the same store, so a
v1 sync is recorded and resets the schedule.

## 9. Readers

New Core abstraction `IQbListsSource { QbLists Current { get; } }` (synchronous, memory read):

- **DB mode**: `DbQbListsSource` (Api) loads the newest `ok` row at startup (after the import of §3.3) and is updated
  by every successful save. A database error at startup → empty lists, logged at Error, readiness shows it; the next
  successful sync fills it.
- **File mode**: `FileQbListsSource` wraps today's `QbListsStore` (reads the file on each `Current`, as today).
- Switched to `IQbListsSource`: `JobPipeline`, `DirectPostRunner`, `QuickBooksEndpoints` (validate, GET lists),
  `RulesEditor` ("synced" = `Current.SyncedUtc is not null`), `Readiness`.
- Readiness `qb-lists`: ok when lists exist; Warning text names the age and a failed last attempt
  (`last sync failed 15:00 (quickbooks-not-open); lists from 14:30`).

## 10. Query fields and audit copies (gap B)

1. Each list query gains `IncludeRetElement` for the fields `Parse` reads, after `ActiveStatus` (qbXML order):
   accounts `FullName`, `Name`, `AccountType`; vendors `Name`; customers `FullName`, `Name`.
   `tests/fixtures/qbxml/list-query.golden.xml` is updated and the change noted in the tracker (rule 5 applies to
   list queries by the same golden-file discipline).
2. The audit copies stay (`qb-audit/sync-lists.*.qbxml`, replaced each sync) and now hold names and types only.
3. `Parse` is unchanged (it already ignores other elements); a test proves a full answer and a trimmed answer parse
   the same.

## 11. Settings

| Key | Default | Notes |
|---|---|---|
| `QuickBooks:ListSync:IntervalMinutes` | `30` | `0` = no scheduled sync. Minimum 5 (smaller → startup error) |
| `QuickBooks:ListSync:FirstDelayMinutes` | `2` | First scheduled attempt after startup when none is recorded |
| `QuickBooks:ListSync:HistoryRows` | `200` | Rows kept in `qb_list_syncs` (min 10) |

Startup log `QuickBooks:` line gains `list sync every 30 min (first after 2 min)` or `list sync schedule off`.

## 12. Logging

- Each attempt: one Information line `QuickBooks lists sync ({Trigger}, by {RequestedBy}): {Outcome} …` with counts or
  reason; Warning per §5.6. Never list contents.
- `missingInRules` stays a Warning on every `ok` sync whose rules name accounts QuickBooks lacks (as today).

## 13. Documentation

- **ADR-0012** "Lists in PostgreSQL, scheduled sync on the job worker" (D-1, D-2, D-4, the trade-off table).
- **CLAUDE.md** "Don't": "…or background timers beyond the single `JobWorker`" gains "(which also runs the scheduled
  list sync, M14)". docs/CLAUDE.md the same.
- **Runbook**: FR-15 sync section and §2.2 folders (`qb-lists.json` becomes the no-database fallback); new subsection
  "Scheduled list sync" (settings, the never-start-QuickBooks guard, the up-to-2-minute dialog delay, how to read
  `lastSync`, how to turn it off).
- **Tracker**: M14 row and section, Q-119…Q-121, Decisions rows for D-1…D-4, golden-file note (§10).

## 14. Questions (answered here as the builder's call; owner may overrule)

| Q | Question | Call |
|---|---|---|
| Q-119 | Keep writing `qb-lists.json` when the database is configured? | No: one source of truth; the file is the no-database fallback only |
| Q-120 | Who may press Sync now? | Editors and admins (`requests:edit`): the people who see "not in QuickBooks" and fix rows. Viewers see the status only |
| Q-121 | Should a dashboard sync be an audit-feed event? | No: it moves no money and changes no statement; `qb_list_syncs` keeps who and when |

## 15. Acceptance

- **A-1** Scheduled: with the fake gateway and `IntervalMinutes=5`, a sync row `schedule/ok` appears when due; a job
  queued at the due time runs first and the sync follows; nothing runs while the queue is busy.
- **A-2** Guard: with presence "not open", a due scheduled sync records `skipped/quickbooks-not-open` and sends
  nothing to the gateway; the dashboard route answers `409 quickbooks-not-open`.
- **A-3** Failure: a gateway failure records `failed` with a reason; `GET lists` still returns the previous lists and
  `syncedUtc`; `lastSync.outcome = failed`.
- **A-4** Dashboard route: editor → 200 with fresh lists and counts; viewer → 403; API key without `qb:read` → 403; two
  concurrent requests → one gateway call, both 200 with the same `lastSync`.
- **A-5** v1 bytes unchanged (`V1ContractPinTests`, v1 OpenAPI hash); a v1 sync is recorded with trigger `api`.
- **A-6** Retention keeps 200 rows and always the newest `ok`.
- **A-7** Import: DB empty + file present → one `import` row; readers see the file's lists.
- **A-8** No-database mode: everything behaves as before M14 (file source, `lastSync` only the attempts since startup (Q-124), schedule still runs and
  writes the file).
- **A-9** Golden list query includes the `IncludeRetElement` lines in qbXML order.
- **A-10** [server] On the QuickBooks machine: scheduled sync with QuickBooks open → `ok`; QuickBooks closed → `skipped`
  and **no QuickBooks process started**; a dialog open → the delay is ≤ ~2 min and logged; Sync now from the dashboard
  updates the pickers.
