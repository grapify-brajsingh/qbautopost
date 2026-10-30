# Spec — QuickBooks list sync history route and a seeded simulated company (M15)

**Date**: 2026-10-30 · **Status**: draft for the owner's review · **Milestone**: M15 (tasks T-1500 …)
**Source**: owner request 2026-10-30: a **Lists** tab in the dashboard sidebar that shows "the synced data from
QuickBooks, also … the history of sync runs and manual syncs" (`grapify-brajsingh/qbpost_dashboard#6`).
**Dashboard side**: `D:\qb_post_react\docs\superpowers\specs\2026-10-30-qb-lists-page-spec.md`, requirement **R-17**
in `D:\qb_post_react\docs\api-requirements-for-dashboard.md`.
**Builds on**: M14 (`docs/qb-list-sync-m14.md`): `qb_list_syncs`, `IQbListsStore`, `ListSyncMessages`,
`GET /api/v2/quickbooks/lists` with `lastSync`, `POST /api/v2/quickbooks/lists/sync`.

---

## 1. Goals and anti-goals

**Goals**
1. The dashboard can list every recorded sync attempt (automatic, Sync now, API, import), newest first, filtered and
   paged (R-17). The names themselves already come from `GET /api/v2/quickbooks/lists` (accounts with type), so no
   new route is needed for them.
2. In Development with `QuickBooks:Fake=true`, a sync returns real-looking names instead of empty lists, so the
   dashboard and the Docker test stack can exercise Sync now and the 30-minute schedule.

**Anti-goals**
- No v1 change: `/api/v1/**` and the v1 OpenAPI document byte-identical (`V1ContractPinTests`).
- The history never exposes `error` (exception text), `missing_in_rules` or the stored `lists` JSON.
- No new table, column, package, hosted service or timer. No write path.
- The simulated-company seed never applies outside Development/Testing (the simulated mode already refuses to start
  there) and never touches the real SDK gateway.

## 2. What exists today (read from the code)

- `qb_list_syncs` (M14 §3): one row per attempt, `id` identity, `trigger`, `requested_by`, `started_utc`,
  `finished_utc`, `outcome`, `reason`, `error`, counts, `missing_in_rules`, `lists` (ok only); index
  `(outcome, id desc)`; retention 200 rows keeping the newest ok.
- `IQbListsStore` (`src/QbAutopost.Core/Abstractions/IQbListsStore.cs`): `RecordAsync`, `LastAttemptAsync`.
  Implementations: `EfQbListsStore` (database), `FileQbLists` (no database; last attempt kept in memory, Q-124).
- `QuickBooksListsV2Endpoints`: `ListSyncView` (`trigger`, `outcome`, `reason`, times, `requestedBy` resolved for
  `dashboard` through the user directory, falling back to the id, `message` from `ListSyncMessages.For`).
- `ApiRoutes` scope map: `GET /quickbooks/lists` → `lists:read`; `IsV2ListSync` matches the POST path exactly.
- `AuditEventEndpoints`: the v2 paging and query-validation pattern (`page`, `pageSize`, unknown parameter → 400).
- `QbConnection.Create` → `new SimulatedQbGateway(new SimulatedQuickBooks())` for `QuickBooks:Fake=true`;
  `SimulatedQuickBooks.Accounts|Vendors|Customers` start empty and answer the list queries from those collections.
  Development reads `Paths:QbLists` = `data/qb-lists.json`; the Docker stack mounts its seed there.
- Result today: in the Docker stack every Sync now and every scheduled sync stores **empty** lists over the imported
  seed (found 2026-10-30; the stack turned the schedule off as a stopgap).

## 3. `GET /api/v2/quickbooks/lists/syncs` (R-17)

- **Scope** `lists:read` (every dashboard role; API keys with `qb:read`, as the GET lists route). Default (read) rate
  bucket. v2 only.
- **Query**: `trigger` and `outcome`, each a comma-separated set (`schedule|dashboard|api|import`,
  `ok|failed|skipped`, case-insensitive); `page` ≥ 1 (default 1); `pageSize` 1–100 (default 25). An unknown parameter
  or value → `400` problem (`type` `invalid-request`, title "Invalid query", as the audit feed). A page past the end → `200` with empty `items`.
- **Order**: `id` descending (newest attempt first).
- **Answer** `200`:

```json
{
  "items": [
    {
      "id": 42,
      "trigger": "dashboard",
      "outcome": "ok",
      "reason": null,
      "startedUtc": "2026-10-30T09:14:02Z",
      "finishedUtc": "2026-10-30T09:14:09Z",
      "requestedBy": { "id": "…", "displayName": "Asha Kapoor" },
      "message": "The lists were synced from QuickBooks.",
      "counts": { "accounts": 71, "vendors": 282, "customers": 6 },
      "current": true
    }
  ],
  "page": 1,
  "pageSize": 25,
  "total": 57
}
```

  - `trigger`, `outcome`, `reason`, times, `requestedBy`, `message`: exactly as `lastSync` (one shared mapping, so a
    run reads the same in both places). `requestedBy` only for `dashboard`.
  - `counts`: the row's counts for `ok`, else `null`.
  - `current`: true on the newest `ok` row (its lists are the ones in use); false on every other row.
  - Never: `error`, `missingInRules`, `lists`.
- **No database** (file mode): the attempts since the API started, kept in memory, newest first, capped at
  `QuickBooks:ListSync:HistoryRows`; `id` is a per-process sequence; `current` marks the newest `ok` in memory (an
  imported or pre-start file has no row). SPEC-GAP → Q-125.
- OpenAPI v2 documents the route and schema (`OpenApiV2DriftTests`).

### 3.1 Store

`IQbListsStore` gains

```csharp
/// <summary>Attempts newest first, filtered, one page; total counts the filtered set.</summary>
Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct);
```

`ListSyncQuery(IReadOnlySet<ListSyncTrigger>? Triggers, IReadOnlySet<ListSyncOutcome>? Outcomes, int Page, int PageSize)`;
`ListSyncPage(IReadOnlyList<ListSyncAttempt> Items, int Total, long? CurrentId)` where `ListSyncAttempt` is the
`ListSyncRun` plus its `Id` and counts. `EfQbListsStore` reads without the `lists` and `error` columns (projection),
`CurrentId` = max id of `ok` rows. `FileQbLists` keeps a bounded in-memory list.

## 4. Seeded simulated company (Development only)

- When the connection mode is **Simulated** and the `Paths:QbLists` file exists and parses, `QbConnection.Create`
  loads its accounts (name and type), vendors and customers into the new `SimulatedQuickBooks` before it is used. A
  missing or unreadable file leaves the company empty and logs one Warning (`simulated QuickBooks company has no
  lists: {reason}`); it never stops startup.
- Effect: in Development a sync returns the file's names; the import row and later sync rows agree.
- The SDK and unavailable modes are unchanged; `SimulatedQuickBooks` stays in the API project.
- Startup log `QuickBooks:` line in simulated mode gains `simulated company: 19 accounts, 15 vendors, 6 customers
  from {file}` (counts only).

## 5. Questions (builder's call; owner may overrule)

| Q | Question | Call |
|---|---|---|
| Q-125 | History without a database? | Attempts since start, in memory, same shape (as Q-124 for `lastSync`) |
| Q-126 | Show a failed attempt's `error` to admins? | No: exception text is operator data; `message` is enough for the dashboard, the log has the rest |
| Q-127 | Names added/removed per sync (diff of stored `lists`)? | Not in M15 (owner asked for history, not changes); possible later from the stored JSON |

## 6. Acceptance

- **A-1** After a scheduled ok, a dashboard ok by an editor and a skipped attempt: the route returns three items newest
  first; the dashboard row names the editor; the newest ok row has `current: true`, the others false.
- **A-2** `trigger=dashboard` → only the editor's row; `trigger=schedule,import` → those; `outcome=skipped` → the skip
  with `counts: null`; `total` counts the filtered set.
- **A-3** Viewer → 200; no token → 401; API key without `qb:read` → 403.
- **A-4** `pageSize=101`, `page=0`, `trigger=nope`, `foo=1` → 400 `invalid-request`.
- **A-5** No item ever carries `error`, `missingInRules` or `lists` (JSON property check).
- **A-6** File mode: two syncs since start → two items, newest first.
- **A-7** v1 bytes unchanged; OpenAPI v2 drift test green with the new route.
- **A-8** Simulated mode with a seed file of 19/15/6 names: `POST /api/v2/quickbooks/lists/sync` → 200 with counts
  19/15/6; without the file → 200 with empty lists and one Warning at startup.
