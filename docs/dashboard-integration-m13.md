# Spec — Dashboard integration fixes (M13): user row version, user names in the audit feed, local Docker stack

**Date**: 2026-10-29 · **Status**: approved by the owner's standing instruction for this integration ("implement spec
and plan, then start the implementation") · **Milestone**: M13 (tasks T-1301 …)
**Source**: `D:\qb_post_react\docs\api-requirements-for-dashboard.md` (updated 2026-10-29): new **R-15** and the
amended **R-7.3**, found by running the dashboard against this API end to end
(`D:\qb_post_react\docs\superpowers\specs\2026-10-29-api-integration-spec.md`).
**Builds on**: `docs/saved-requests-dashboard-v2.md` (M11) and `docs/dashboard-api-m12.md` (M12), assumed as written.

Everything here is **`/api/v2` only**. `/api/v1/**` and its OpenAPI document stay byte-identical (`V1ContractPinTests`).

---

## 1. What the integration run found

The dashboard was switched from its fake API to `/api/v2` and driven through every page in a Docker stack (PostgreSQL,
this API with the simulated QuickBooks, an OpenRouter stand-in, the dashboard behind nginx). Sign-in, the Statements
list and filters, the statement page (edit, don't post, save, validate, post, results with TxnIDs), discard and
restore, duplicate warnings, pushId replay, the audit trail and its CSV, Home, upload and review of a PDF, and user
management all worked. Two things did not:

| # | Found | Cause | Requirement |
|---|---|---|---|
| F-1 | **Enable** on a disabled user in the Users list always answers `412 edited-by-someone-else`. | `PATCH /api/v2/users/{id}` needs `If-Match` with the user's ETag (Identity's `ConcurrencyStamp`), and list rows carry no version, so the dashboard cannot send the one the admin saw. | R-15 |
| F-2 | The Audit trail shows user events as "Created user 8070e704-… as Admin". | `AuditEventMapper.FromUserAudit` adds only `detail.userId`; R-7.3 also asks for the subject's `email` and `displayName`. | R-7.3 |
| F-3 | Every page change in the dashboard writes `HTTP GET /api/v2/audit-events responded 500` at **Error**, with an `OperationCanceledException` stack. | The dashboard (React Query) aborts reads it no longer needs; `RequestAborted` reaches EF/Npgsql and the exception escapes as a 500. The client is gone, so nobody sees it, but the log fills with false errors. | Logging (no R-n) |
| F-4 | Running the dashboard quickly as one user hits `429` on reads. | `Api:RateLimits:DefaultPerMinute` is 120 per caller; Home alone makes about 22 calls per load (R-10.7). Normal use stays below it; the test stack raises the limit. | Q-118 |

Everything else the dashboard needed was a shape this API documents and the dashboard now follows (lists of versions and
attempts wrapped in `{ items }`, QuickBooks accounts as `{ name, type }`, the password change answering with a new access
token). Those are dashboard fixes, not API changes.

## 2. Anti-goals

- No v1 change (response bytes, OpenAPI hash, routes).
- No schema change and no migration: both fixes read data that already exists.
- The stored `user_audit.detail` is never changed; names are looked up when the feed is read.
- No new package.

## 3. R-15: `rowVersion` on v2 user rows

1. `UserViewV2` (the row of `GET /api/v2/users`) gains **`rowVersion`** (string): the user's `ConcurrencyStamp`, the
   exact value the `ETag` header of `GET /api/v2/users/{id}` carries, without the quotes. It is placed after `status`.
2. `If-Match: "<rowVersion>"` is checked by the existing `PATCH`/`reset-password`/`unlock` code, unchanged: equal stamp
   → applied, different → `412`. No new comparison code.
3. The single-user answers (`GET /users/{id}`, `PATCH`, `POST /users`, reset, unlock) keep `UserView` (shared with v1)
   and the `ETag` header. Not adding `rowVersion` there keeps v1 untouched; the dashboard reads the ETag header on those.
4. OpenAPI v2: the users list row schema documents `rowVersion` (string, required) with a description naming `If-Match`.

**Acceptance (E-1)**
- A list row's `rowVersion` equals the ETag of `GET /api/v2/users/{id}` without quotes.
- `PATCH` with `If-Match: "<rowVersion>"` from the list → `200`; after another change to that user, the old list
  `rowVersion` → `412`.
- `GET /api/v1/users` rows are unchanged (no `rowVersion`).

## 4. R-7.3: the subject user's name and email on user events

1. In `GET /api/v2/audit-events`, every **user event** (action `user-*`) whose `detail.userId` names a known user gets
   `detail.email` and `detail.displayName`, from the user table, **when the page is read**.
   - Looked up with the existing `IUserDirectory` in the same batched call that resolves actor names (one query per
     page, not one per row).
   - A value already present in the stored detail is kept (never overwritten).
   - Unknown user id (should not happen: users are disabled, never deleted) → the detail is left as stored.
   - `user-session-revoked` and `user-other` carry no `userId` and are unchanged.
2. Request events are unchanged.
3. The CSV is unchanged (its columns are fixed by R-7.6 and carry no detail).
4. OpenAPI v2: the audit event `detail` description lists `email` and `displayName` for user events.

**Acceptance (E-2)**
- A `user-created` event for a known user carries `detail.userId`, `detail.email`, `detail.displayName` and the stored
  `detail.role`.
- A stored detail that already holds `email` keeps it.
- A user id the directory does not know leaves the detail as stored.
- Viewers still never see user events (unchanged filter).

## 4a. A request the client abandoned is not a server error (F-3)

1. A middleware just inside the request log catches `OperationCanceledException` **only when**
   `HttpContext.RequestAborted` is cancelled. It answers **499** (nginx's "client closed request") if the response has
   not started, and does not rethrow.
2. `RequestLog.Level` writes a 499 at **Information** (the client left; nothing failed here).
3. Any other exception, and a cancellation the client did not cause (a timeout inside the server), still reaches the
   exception handler as today.
4. Posting is unaffected: the poster claims, posts and records under `CancellationToken.None` (v2 spec §13.2).

**Acceptance (E-5)**: an aborted request answers 499 and logs Information; a non-client cancellation and any other
exception still propagate; the existing "client disconnects during a post" tests stay green.

## 5. Local Docker image (development and integration only)

1. A `Dockerfile` at the repository root builds the API for Linux (`dotnet publish -c Release`) on
   `mcr.microsoft.com/dotnet/aspnet:8.0`, with `tzdata` (IANA zones for the dashboard's `tz` parameters) and `curl`
   (container health check).
2. It runs with `ASPNETCORE_ENVIRONMENT=Development` and `QuickBooks:Fake=true` (the simulated company, which the app
   refuses outside Development/Testing), binds `http://0.0.0.0:5080` with `Api:AllowInsecureRemote=true`, and keeps
   its data files under `/data` (with `Paths:AllowedJobRoots` = `/data/jobs`, which the transport guard requires for a
   remote bind). `Dashboard:Enabled=false`: the dashboard's own container serves the UI.
3. No secret is baked in: the database connection, API key, JWT signing key, bootstrap admin and OpenRouter key come
   from `QBAUTOPOST__*` environment variables set by the dashboard repository's `docker-compose.integration.yml`.
4. `.dockerignore` keeps `bin/`, `obj/`, tests, docs and `.git` out of the build context.
5. It is **not** a production deployment. Production stays the Windows service of `docs/runbook.md` (QuickBooks Desktop
   needs Windows COM). The runbook gains a short "Local Docker stack" note saying so.

**Acceptance (E-3)**: `docker build .` succeeds; with the integration compose file the API answers
`GET /api/v1/health` `200`, `GET /api/v2/health/database` reports `Ready` after the migration step, and the dashboard's
`e2e-integration` suite passes against it.

## 6. Questions

| # | Question | Choice until answered |
|---|---|---|
| Q-116 | Should the single-user v2 answers also carry `rowVersion`? | No: they carry the `ETag` header, and `UserView` is shared with v1 (§3.3). |
| Q-117 | Should the audit CSV gain a "subject user" column for user events? | No: R-7.6 fixes the columns. |
| Q-118 | Is `Api:RateLimits:DefaultPerMinute` = 120 enough for the dashboard (Home makes ~22 calls per load; R-11 can replace most of them)? | Unchanged; measure on the server. The dashboard's Home moves to `GET /dashboard/summary` (R-11), which cuts its load to a handful of calls. |

## 7. Acceptance criteria

| # | Criterion |
|---|---|
| E-0 | No v1 change: `V1ContractPinTests` green, existing tests unedited except where a v2 shape test lists fields. |
| E-1 | §3 acceptance, against PostgreSQL (`QbAutopost.Data.Tests`). |
| E-2 | §4 acceptance, over the fake store (`QbAutopost.Api.Tests`). |
| E-3 | §5 acceptance. |
| E-4 | `dotnet build -warnaserror` 0 warnings, `dotnet test` green; no new package. |
| E-5 | §4a acceptance. |
