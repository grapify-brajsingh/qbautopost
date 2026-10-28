# Spec — Saved requests in PostgreSQL, post by id, and dashboard sign-in

**Status**: **draft for owner approval**, written 2026-10-27
**Ticket**: [#2](https://github.com/grapify-brajsingh/qbautopost/issues/2) · **Branch**: `feature/2-postgres-saved-requests`
**Contract rank**: subordinate to `docs/spec.md` (FR-x), `docs/spec-api-v1.md` and
`docs/validate-and-post-transaction-data.md`, **except** where §2 below explicitly reverses them. It reuses
validate-and-post unchanged: everything that route decides about a payload, this one decides identically.
**Decision records**: ADR-0009 (PostgreSQL via EF Core, superseding ADR-0002) and ADR-0010 (dashboard users and JWT),
both written as part of M10 task T-1001.
**Companion**: `D:\qb_post_react\qbautopost-dashboard-brief.md`, which gives the React dashboard project the API it
will call.

---

## 1. Why this exists

Statements arrive by email. An **n8n** workflow watches the mailbox, extracts the transactions, and today calls
`POST /api/v1/quickbooks/transactions/validate-and-post`. That means a model's reading of an email goes straight
into a real QuickBooks company file and no person looks at it first.

The owner wants a person between the email and the ledger:

1. n8n **saves** the payload instead of posting it. It is stored in PostgreSQL.
2. A **React dashboard** (shadcn) lists saved requests. A person reviews them, **edits** a request if n8n read it
   wrongly, and keeps the original, with a record of every change and who made it.
3. The person clicks **Post**. The API reads the saved request by id and runs exactly the validate-and-post logic.
   The outcome (posted, validation errors, busy, QuickBooks down, anything else) is recorded against the request.
4. People sign in to the dashboard with their own accounts. Their roles are **admin**, **editor** and **viewer**.

**Success** means that nothing reaches QuickBooks unless a named person pressed Post on a version they could see. For
every request, the dashboard can show where it came from, what changed, who changed it, and what happened each time
it was posted.

## 2. What this reverses, and the owner's authority for it

The owner asked for this on 2026-10-27, ticket #2. It reverses three standing rules, deliberately:

| Rule today | Where | Becomes |
|---|---|---|
| "Don't introduce a database" | `CLAUDE.md` › Don't | PostgreSQL is allowed **for saved requests, their history and dashboard users only**. JSON files remain the store for everything they hold today (ledger, rules, lists, job status, idempotency, audit). |
| "No database. State is JSON files and the job folders." | `docs/spec.md` §2 | Amended as above. |
| ADR-0002 "JSON files … the only state store" | `docs/adr/0002-…` | Status → *superseded in part by ADR-0009*. |
| Out of scope: "a database, a UI" | `docs/spec-api-v1.md` §1 | A database for this feature; the UI lives in a **separate repository** (`D:\qb_post_react`) and this API only serves it. |
| NuGet whitelist | `CLAUDE.md` › Don't | Extended by §13 once the owner approves each package (Q-80). |

**The ledger does not move.** `ledger.json` is what G4 and undo read (validate-and-post §2.2). Moving it into
PostgreSQL would change two safety properties at once and is **not** part of this work. A saved request *points at*
the ledger row its post produced (`postId`); it does not replace it.

## 3. Goals, non-goals, anti-goals

**Goals**
- G-1 n8n can save the exact validate-and-post payload and always gets an id back. A payload that would fail
  validation is still saved and is marked as needing attention.
- G-2 Saved requests, every version of them, every post attempt and every event are stored in PostgreSQL through
  EF Core **code-first** migrations.
- G-3 Posting a saved request by id behaves exactly like validate-and-post with that payload, and its result is
  stored in full.
- G-4 Dashboard users sign in, and every change records the user who made it.
- G-5 The edge cases in §10 each have a defined, tested ending.

**Non-goals** (not in this work; may come later)
- Undo from the dashboard. `POST /api/v1/batches/{postId}/undo` keeps working with an API key; the saved request
  stores `postId`, so a later feature can add it.
- Moving the ledger, rules, lists, idempotency or audit stores into PostgreSQL.
- Email delivery (password reset links, notifications). There is no SMTP; admins hand out temporary passwords.
- Splitting a saved request into several, or merging requests.
- Changes to n8n's extraction.
- Single sign-on or external identity providers.

**Anti-goals** (would make this a failure even if it technically works)
- **A double post.** A retried click, a double click, two editors, a crash or a lost response must never send the
  same request to QuickBooks twice.
- **Silent loss.** A payload n8n sent must never be dropped because it was malformed or because validation failed.
- **Rewritten history.** The original payload and old versions must never change after they are written.
- **A dashboard outage that stops the existing API.** If PostgreSQL is down, the existing folder-job,
  validate-and-post and health routes keep working.
- **The database computing money.** Totals shown or stored come from the payload or from the post result. Nothing
  here adds, adjusts or "fixes" an amount (CLAUDE.md rule 3).

## 4. Shape of the solution

```
 n8n ──X-Api-Key──►  POST /api/v1/saved-requests ──► PostgreSQL (request v1 = original, flags, validation snapshot)
                                                        ▲
 React dashboard ──Bearer JWT──► list / view / edit ────┘  (each edit = new version + change list)
                                │
                                └─► POST /api/v1/saved-requests/{id}/post
                                        │  claim (status pending → posting, attempt row written, COMMIT)
                                        ▼
                                   SyncPostRunner.RunAsync(latest version)   ← unchanged validate-and-post engine
                                        │  (evidence folder, ledger row, SDK lock: all as today)
                                        ▼
                                   attempt row completed + request status updated (COMMIT)
```

### 4.1 Projects

| Project | Gets | Rule |
|---|---|---|
| `QbAutopost.Core` | the state machine (§7) as pure code, the duplicate rules (§9), the change-list differ (§8.3), DTOs | No EF, no ASP.NET, no COM (CLAUDE.md rule 2 unchanged). Unit-tested first. |
| **`QbAutopost.Data`** *(new)* | `QbAutopostDbContext`, entity configurations, migrations, `EfSavedRequestStore`, Identity stores | References Core. The only project that references Npgsql. |
| `QbAutopost.Api` | endpoints, JWT and Identity wiring, `SavedRequestPoster` (claim → run → record), startup recovery (§10.6) | Talks to the store through `ISavedRequestStore` (declared in Core), so API tests run against `FakeSavedRequestStore`. |
| `QbAutopost.QuickBooks` | nothing | COM stays here. |

### 4.2 Configuration added

```jsonc
{
  "Database": {
    "ConnectionString": "",            // environment only: QBAUTOPOST__Database__ConnectionString (rule 6)
    "CommandTimeoutSeconds": 30,
    "RequireCurrentSchema": true       // §5.4
  },
  "Auth": {
    "Jwt": {
      "Issuer": "qbautopost", "Audience": "qbautopost-dashboard",
      "SigningKey": "",                // environment only, ≥ 32 bytes
      "AccessTokenMinutes": 15,
      "RefreshTokenDays": 7
    },
    "Lockout": { "MaxFailedAttempts": 5, "LockoutMinutes": 15 },
    "Password": { "MinLength": 12 },
    "BootstrapAdmin": { "Email": "", "Password": "" }   // environment only; used only when the users table is empty
  },
  "SavedRequests": {
    "MaxListPageSize": 100,
    "DuplicateLookbackDays": 90
  }
}
```

A secret found in `appsettings.json` (connection string with a password, signing key, bootstrap password) logs a
warning naming the **key**, never the value, as api-v1 §12 does for the PFX password. No secret is ever logged,
returned, or written to `output/` or fixtures.

## 5. Database

### 5.1 Code-first, PostgreSQL, EF Core 8

- Provider `Npgsql.EntityFrameworkCore.PostgreSQL` 8.x (matches net8.0). Snake_case table and column names.
- All timestamps are `timestamptz`, stored and returned as UTC. Money is `numeric(18,2)` where it is a column; inside
  payloads it stays as the JSON number n8n sent.
- Payloads are stored as `jsonb`, and the **original body is also stored byte-for-byte as `text`** (§5.2,
  `original_body`), because `jsonb` reorders keys and drops duplicate keys, which is not "the original n8n request".
- Concurrency: every mutable row has an application-managed `row_version integer`, which is incremented on every
  update and used as the EF concurrency token. *Builder's call, recorded here:* Postgres `xmin` would also work, but
  an explicit column keeps the store testable on a provider that does not have `xmin`.

### 5.2 Tables

**`saved_requests`**, one per thing n8n (or a person) sent.

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint` identity | The id the dashboard shows and `/{id}/post` takes. |
| `public_ref` | `text` unique | `SR-000123` form, for humans and logs. |
| `status` | `text` | §7. |
| `source` | `text` | `api-client:<clientId>` (n8n) or `user:<userId>` (created in the dashboard). |
| `reference` | `text` null | Copied from the latest version's payload `reference`, for search. Never authoritative. |
| `control_total` | `numeric(18,2)` null | Copied from the latest payload, for display and sort. Never computed. |
| `transaction_count` | `int` | Copied from the latest payload. |
| `current_version` | `int` | Number of the latest version. |
| `original_body` | `text` | The exact bytes received, as UTF-8. Written once. |
| `original_sha256` | `char(64)` | Hash of `original_body`. |
| `canonical_sha256` | `char(64)` | Hash of the canonicalised latest payload (§9). |
| `idempotency_key` | `text` null | Unique per source when present (§10.1). |
| `needs_attention` | `bool` | True while the latest version fails to read or validate (§6.1). |
| `duplicate_of_id` | `bigint` null | §9. |
| `duplicate_reasons` | `text[]` | §9. Empty when not flagged. |
| `duplicate_acknowledged_by` / `_utc` | null | Set by a post with `confirmDuplicate: true`. |
| `validation` | `jsonb` null | Latest validation snapshot (§6.4). |
| `validated_utc` | `timestamptz` null | |
| `post_id` | `text` null | The ledger batch id once anything posted. |
| `discard_reason` | `text` null | |
| `created_utc` / `created_by` | | `created_by` = user id or `api-client:<id>`. |
| `updated_utc` / `updated_by` | | |
| `row_version` | `int` | Concurrency token. |

Indexes: `(status, created_utc desc)`, `(reference)`, `(canonical_sha256)`, `(source, idempotency_key)` unique
where not null.

**`saved_request_versions`**, append-only.

| Column | Notes |
|---|---|
| `id` bigint, `saved_request_id` FK, `number` int | unique `(saved_request_id, number)`. Version 1 is the original. |
| `payload` jsonb | The full payload at this version. |
| `readable` bool, `read_errors` text[] | Whether it binds to `SyncPostRequest` (§6.1). |
| `changes` jsonb | §8.3 change list against the previous version; `[]` for version 1. |
| `note` text null | Optional reason the editor typed. |
| `created_utc`, `created_by` | |

No update and no delete is ever issued against this table. The store exposes no method that could, and a test
asserts that.

**`post_attempts`**, one per press of Post that got past the claim (§10.2).

| Column | Notes |
|---|---|
| `id` bigint, `saved_request_id` FK, `version_number` int | The exact version that was sent. |
| `outcome` | `in-progress` · `posted` · `partial` · `failed` · `held` · `refused` · `busy` · `unavailable` · `error` · `unknown` (§7.2). |
| `http_status` int null | What validate-and-post would have answered. |
| `post_id` text null | Ledger batch id, when one was created. |
| `result` jsonb null | The full `SyncPostResult` (200) or the problem body (4xx/5xx), stored as returned. |
| `counts` jsonb null | `submitted / posted / held / skipped`, copied from the result. |
| `error` text null | A short message for `error` / `unknown`. Never a stack trace or a secret. |
| `started_utc`, `finished_utc`, `started_by` | |
| `resolved_by`, `resolved_utc`, `resolution_note` | §10.6. |

**`saved_request_events`**, append-only timeline: `created`, `flagged-duplicate`, `edited`, `validated`,
`post-started`, `post-finished`, `discarded`, `restored`, `resolved`. Columns: `id`, `saved_request_id`, `kind`,
`detail jsonb`, `at_utc`, `actor`. The dashboard's history view reads this table.

**Identity tables**: ASP.NET Core Identity (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, …) on the same
database, plus:

- `users` extension columns: `display_name`, `is_disabled`, `must_change_password`, `created_utc`, `created_by`,
  `last_login_utc`.
- **`refresh_tokens`**: `id`, `user_id`, `token_sha256`, `family_id`, `expires_utc`, `created_utc`,
  `revoked_utc`, `replaced_by_id`, `created_ip`. Only the hash of a token is ever stored.
- **`user_audit`**: `id`, `user_id` (subject), `actor`, `kind` (`created`, `role-changed`, `disabled`, `enabled`,
  `password-reset`, `password-changed`, `login-failed`, `locked-out`, `login`), `detail jsonb`, `at_utc`.

### 5.3 Who wrote it

`created_by` / `updated_by` / `actor` are always taken from the authenticated caller, never from the body:

- JWT caller → the user's id (`sub` claim). The API returns the user's display name and email beside the id.
- API key caller → `api-client:<clientId>` (e.g. `api-client:n8n-mailbox`).

A body that carries `createdBy` or `updatedBy` has those fields ignored. Users are **disabled, never deleted**, so an
id in history always resolves to a name.

### 5.4 Where PostgreSQL runs (owner, 2026-10-27)

PostgreSQL runs **on the QuickBooks server itself**, beside the app. So:

- **Install**: `deploy/` gains `install-postgres.ps1`. It installs a pinned PostgreSQL 16 for Windows as a service,
  bound to `127.0.0.1` only (no remote access, so no DB TLS is needed), and creates database `qbautopost` with two
  roles: `qbautopost_owner` (runs migrations) and `qbautopost_app` (DML only, with no DDL and no `DELETE` on the
  append-only tables). Passwords are generated at install and written to a machine-scoped environment variable,
  never to a file in the repo. *Builder's call:* the least-privileged app role makes the append-only rule (§5.2)
  hold even against a bug.
- **Backups**: `deploy/backup-postgres.ps1` runs `pg_dump -Fc` daily through Windows Task Scheduler (not an app
  timer, so CLAUDE.md's one-`JobWorker` rule is untouched). It writes beside the QuickBooks backups in
  `QuickBooks:BackupFolder\postgres\` and keeps 30 days. `scripts/qb-server-check.ps1` checks that the service is
  running, that the database is reachable, and that the newest dump is < 36 h old.
- **Verification on the server** is a **[server]** task (`ready-for-human`), like the QuickBooks ones.
- Development machines use Docker (`docker compose` file in `deploy/dev/`) for a local PostgreSQL, and the
  Testcontainers tests (§14) run locally as well as in CI.

### 5.5 Migrations and startup

- Migrations live in `QbAutopost.Data/Migrations` and are created with `dotnet ef migrations add`. The `dotnet-ef`
  tool and `Microsoft.EntityFrameworkCore.Design` are build-time only and never ship.
- **Deploy applies migrations**, not the app: `deploy/` gains a step that runs an idempotent script
  (`dotnet ef migrations script --idempotent`) or a migration bundle against the server's database. *Builder's call:*
  silently migrating a production schema on startup is the kind of thing that should post less.
- At startup the app compares applied migrations with the ones it was built with. If they differ and
  `Database:RequireCurrentSchema` is true, the database feature is **not ready**. `GET /api/v1/health/ready`
  reports `database: { ok: false, reason: "schema-out-of-date" }`, and every route in §6 answers `503`. The rest of
  the API starts normally.
- A new `GET /api/v1/health/database` (`health:read`) reports reachability, schema state and latency. It never
  includes the connection string.

## 6. API: saved requests

All routes are under `/api/v1`, use RFC 7807 problems, and are listed in `wwwroot/openapi.json` (the drift test
enforces it). Authorisation is by **scope** (§11.3). The role → scope table decides who holds each scope.

| Method & path | Scope | Purpose |
|---|---|---|
| `POST /saved-requests` | `requests:submit` | Save a payload (n8n). Returns `201` with the record. |
| `GET /saved-requests` | `requests:read` | List, filtered and paged (§6.5). |
| `GET /saved-requests/{id}` | `requests:read` | The record, latest payload, flags, validation snapshot, last attempt. |
| `GET /saved-requests/{id}/original` | `requests:read` | The original body exactly as received (`application/json`, byte-for-byte). |
| `GET /saved-requests/{id}/versions` | `requests:read` | Version list: number, who, when, note, change count. |
| `GET /saved-requests/{id}/versions/{n}` | `requests:read` | One version's payload and change list. |
| `PUT /saved-requests/{id}` | `requests:edit` | Replace the payload, creating a new version (§8). Requires `If-Match`. |
| `POST /saved-requests/{id}/validate` | `requests:edit` | Re-run offline validation of the latest version and store the snapshot. |
| `POST /saved-requests/{id}/post` | `requests:post` | Post the latest version (§10.2). |
| `GET /saved-requests/{id}/attempts` | `requests:read` | Every post attempt, newest first. |
| `GET /saved-requests/{id}/attempts/{attemptId}` | `requests:read` | One attempt with the full stored result. |
| `GET /saved-requests/{id}/events` | `requests:read` | Timeline (§5.2). |
| `POST /saved-requests/{id}/discard` | `requests:edit` | `{ "reason": "…" }`, required, 1–500 chars. |
| `POST /saved-requests/{id}/restore` | `requests:admin` | Discarded → pending. |
| `POST /saved-requests/{id}/resolve` | `requests:admin` | Settle an `unknown` or `partial` request (§10.6). |
| `GET /saved-requests/stats` | `requests:read` | Counts per status, needs-attention and duplicates, for the dashboard header. |

`{id}` is the numeric id. `SR-000123` is accepted too and resolves to the same record. An unknown id → `404`.

### 6.1 `POST /saved-requests`: save

**Body**: exactly the validate-and-post body (validate-and-post §4). Nothing is added and nothing is renamed, so the
only change n8n needs is the URL.

What is accepted, in order:

1. Body larger than `Api:MaxRequestBodyBytes` → `413`. Not stored, because there is nothing safe to store.
2. Not JSON, or JSON that is not an object → `400 not-a-json-object`. Not stored. n8n gets a clear error and its
   workflow's error branch fires.
3. Any JSON object → **stored** as version 1. Then it is read as `SyncPostRequest`:
   - It binds (types are right) → `readable: true`.
   - It does not bind (wrong type, bad enum such as `"kind":"Cheque"`, malformed date) → `readable: false`, and
     `read_errors` names each JSON path (`$.transactions[3].kind: 'Cheque' is not a TxnKind`).
     **It is still saved**, with `needs_attention: true`, so a person can fix it in the dashboard.
   - The validate-and-post refusals that are about the request rather than the rows (`dryRun` present,
     `allowModelAccounts` present, control-total mismatch, more than `MaxTransactionsPerRequest` rows) are recorded
     as validation findings and set `needs_attention`. They do not refuse the save.
4. The validation snapshot (§6.4) is computed. If validation itself cannot run (rules file missing, lists
   unreadable), the snapshot is `{ "state": "unavailable", "reason": … }`. **The save still succeeds.**
5. Duplicate rules run (§9). A match sets the flag. It never refuses the save.

**Response `201`**, with `Location: /api/v1/saved-requests/{id}`:

```json
{
  "id": 123, "publicRef": "SR-000123", "status": "pending",
  "needsAttention": false,
  "duplicate": { "flagged": true, "of": 117, "reasons": ["same-payload"] },
  "validation": { "state": "ok", "counts": { "submitted": 12, "postable": 12, "held": 0, "skipped": 0 } },
  "createdUtc": "2026-10-27T09:14:02Z"
}
```

n8n should treat any `201` as success. Flags are for people, not for the workflow.

**Idempotency**: an `Idempotency-Key` header is honoured per source (§10.1).

### 6.2 `GET /saved-requests/{id}`

```json
{
  "id": 123, "publicRef": "SR-000123", "status": "pending", "rowVersion": 4,
  "source": { "kind": "api-client", "id": "n8n-mailbox", "name": "n8n mailbox workflow" },
  "reference": "zemen-2019-12", "controlTotal": 4820.17, "transactionCount": 12,
  "currentVersion": 3, "needsAttention": false,
  "readErrors": [],
  "duplicate": { "flagged": false, "of": null, "reasons": [], "acknowledgedBy": null },
  "validation": { "state": "held", "validatedUtc": "…", "counts": {…}, "validation": [ …SyncPostFinding… ],
                  "unknownNames": {…} },
  "payload": { …latest version, as stored… },
  "lastAttempt": { "id": 9, "outcome": "held", "httpStatus": 422, "finishedUtc": "…" },
  "postId": null,
  "created": { "utc": "…", "by": { "id": "api-client:n8n-mailbox", "name": "n8n mailbox workflow" } },
  "updated": { "utc": "…", "by": { "id": "5b1…", "name": "Asha K", "email": "asha@…" } },
  "allowedActions": ["edit", "validate", "post", "discard"]
}
```

The response carries `ETag: "4"` (the `rowVersion`). `allowedActions` is computed from status **and** the caller's
scopes, so the dashboard does not have to duplicate the rules in §7.

### 6.3 `PUT /saved-requests/{id}`: edit

- Header `If-Match: "<rowVersion>"` is **required**. Missing → `428`. Stale → `412 edited-by-someone-else`, with
  the current `rowVersion` and who last changed it, so the dashboard can say "Asha changed this 2 minutes ago,
  reload".
- Body: `{ "payload": { …full validate-and-post body… }, "note": "fixed payee on row 4" }`. A full payload, not a
  patch. *Builder's call:* a full replace is simpler to validate and to diff, and the dashboard already holds the
  whole payload.
- Allowed only in status `pending` (§7). Otherwise `409 not-editable` with the status.
- A payload identical to the current version (after canonicalising, §9) → `200` with nothing changed and no new
  version.
- Otherwise: new version `n+1` with its change list, the denormalised columns are refreshed, validation and
  duplicate rules re-run, an `edited` event is written, and `200` returns the record (§6.2).
- The same body rules as save apply: an unreadable payload is stored (and keeps `needs_attention`). Over the size
  limit, or not an object → `400`/`413`, nothing stored.

### 6.4 Validation snapshot

This is the offline plan validate-and-post would make: `DirectPlanner` through `DirectPostRunner.Plan` plus the
§6.1 request-level refusals. It is stored on the request and shown in the dashboard **before** anyone presses Post.

```json
{ "state": "ok" | "held" | "refused" | "unreadable" | "unavailable",
  "validatedUtc": "…", "versionNumber": 3,
  "counts": { "submitted": 134, "postable": 131, "held": 3, "skipped": 0 },
  "validation": [ …SyncPostFinding, exactly as validate-and-post §6.2… ],
  "errors": [ "…request-level refusals or read errors…" ],
  "unknownNames": { "accounts": [], "vendors": [], "customers": [] } }
```

It is **advisory**. Rules, lists and the ledger change after it was taken, so Post always re-validates (§10.2). It
is refreshed on save, on edit, and on `POST /{id}/validate`, and the dashboard shows `validatedUtc`.

`needs_attention` = latest version unreadable, **or** snapshot state is `held` / `refused`. An `unavailable`
snapshot does not set it, because the request may be fine and only the check is down.

### 6.5 `GET /saved-requests`: list

Query: `status` (repeatable), `needsAttention`, `duplicate`, `q` (matches `reference`, `publicRef`, and payee or
memo text inside the payload), `createdFrom` / `createdTo` (dates, UTC), `source`, `sort`
(`created` | `updated` | `controlTotal`, default `-created`), `page` (≥1), `pageSize` (1–`MaxListPageSize`,
default 25). Unknown values → `400`.

Response: `{ "items": [ …summary rows… ], "page": 1, "pageSize": 25, "total": 312 }`. Summary rows hold everything
in §6.2 except `payload`, `validation.validation` and `readErrors`, to keep the list light.

Payload search uses a `jsonb` path query over `payee` and `memo`. *Builder's call:* if it turns out slow it gets a
GIN index, measured first.

## 7. Status

### 7.1 Request status

```
                 edit / validate (stays pending)
                       ┌──┐
   save ──► pending ◄──┘  │
              │  ▲        │
       post   │  │ held / refused / busy / unavailable / failed / error-before-send
              ▼  │
           posting ──── posted ──────────────► posted    (terminal, locked)
              │    ──── partial ─────────────► partial   (locked until resolved)
              │    ──── crash / unknown ─────► unknown   (locked until resolved)
              │
   pending ── discard ──► discarded ── restore (admin) ──► pending
   unknown / partial ── resolve (admin) ──► posted | pending
```

| Status | Meaning | Edit | Post | Discard |
|---|---|---|---|---|
| `pending` | Saved, not in QuickBooks. Waiting for a person. | ✓ | ✓ | ✓ |
| `posting` | An attempt holds the claim. | – | – (`409 already-posting`) | – |
| `posted` | Every row landed (or was skipped). | – | – (`409 already-posted`) | – |
| `partial` | Some rows landed and some did not (validate-and-post Q-78). | – | – | – |
| `unknown` | A post was started and its ending is not known (§10.6). | – | – | – |
| `discarded` | A person decided it should never post. | – | – | – |

"needs attention" and "possible duplicate" are **flags**, not statuses. They can be on a `pending` request at the
same time.

### 7.2 Attempt outcome → request status

| validate-and-post ending | Attempt `outcome` | `httpStatus` | Request goes to |
|---|---|---|---|
| unreadable payload (could not bind) | `refused` | 400 | `pending` |
| `400` (dryRun, control total, too many rows, …) | `refused` | 400 | `pending` |
| `422` held | `held` | 422 | `pending` |
| `409` worker busy | `busy` | 409 | `pending` |
| `503` backup stale / QB unreachable / rules missing | `unavailable` | 503 | `pending` |
| `200` with `status: posted` | `posted` | 200 | `posted` |
| `200` with `status: partial` | `partial` | 200 | `partial` |
| `200` with `status: failed` and **every** held row proving nothing was created: a pre-send gate reason (`unknown-account`, `unknown-payee`, `conflicting-last4`, `duplicate-line`, `no-account-rule`, `no-holding-account`, `no-deposit-income-account`, `refnumber-too-long`, `possible-duplicate`, `duplicate-check-failed`, `backup-too-old`, `quickbooks-unavailable`) or `quickbooks-rejected` with a non-zero QuickBooks status code and no TxnID | `failed` | 200 | `pending` |
| `200` with `status: failed` and any other held row: `quickbooks-no-response` (the qbXML was sent and QuickBooks did not answer; `DirectPostRunner` records it as "may or may not have been applied"), `quickbooks-busy`, `amount-mismatch` (a TxnID exists with another amount), `quickbooks-rejected` with status 0 but no TxnID, a missing status code, or a TxnID returned beside a warning, any reason not listed in the row above, or no held row at all | `unknown` | 200 | `unknown` |
| exception **before** the SDK lock was taken | `error` | 500 | `pending` |
| exception **after** the lock was taken, or the result could not be recorded | `unknown` | – | `unknown` |

*Builder's call, recorded:* the `quickbooks-no-response` row exists because `status: failed` alone cannot tell
"nothing landed" from "nothing confirmed". Returning such a request to `pending` would make a possible double post
one click away, which is the first anti-goal. It goes to `unknown`, and an admin resolves it (§10.6).

*Amended by controller ruling R31 (Q-93, M10 review):* the rule is an allow-list, not a deny-list. A failed post
returns to `pending` only when every held row's reason is on the list of reasons that prove nothing was sent
(`NothingSentReasons` in Core). G4's live check matches on amount, so re-posting a row QuickBooks holds under another
amount, or one it accepted without returning a TxnID, is a double post. A reason added later locks as `unknown` until
someone adds it to the list on purpose.

The post-by-id route binds the stored `jsonb` to `SyncPostRequest` **itself**, before calling the engine. An
unbindable version is answered with `400` problem details naming each JSON path, and is recorded as `refused`. That
avoids validate-and-post's bare-`400` defect (Q-79), so the equivalence test (§14.4) excludes unbindable payloads.

For every other case, the post-by-id route answers the caller with the **same status code and body** validate-and-post would have, plus
`savedRequestId`, `attemptId` and the request's new `status` in the problem extensions or result body. A client
written for validate-and-post can read it unchanged.

## 8. Editing and history

### 8.1 What is kept

- `original_body` (bytes) and version 1 (`jsonb`) are written once, at save.
- Every edit appends a version. Older versions are never updated.
- The current version is always the highest number.
- Every post attempt records the version number it sent, so "what exactly went to QuickBooks" is always answerable.

### 8.2 Who may edit what

Any field of the payload may be edited, including adding or removing rows and changing `controlTotal`.
**`controlTotal` is never recalculated by the API.** If an editor changes an amount, they must also change the
control total, or validation reports the mismatch (CLAUDE.md rule 3).

### 8.3 Change list

Stored on each version and returned with it. Computed in Core (`PayloadDiff`) by comparing the canonical forms of
the previous and new payload:

```json
[
  { "op": "replace", "path": "$.transactions[3].payee", "old": "ABC PLUMBING", "new": "ABC Plumbing LLC" },
  { "op": "replace", "path": "$.controlTotal", "old": 4820.17, "new": 4784.17 },
  { "op": "add",     "path": "$.transactions[12]", "new": { … } },
  { "op": "remove",  "path": "$.transactions[7]",  "old": { … } }
]
```

Rows are compared **by `externalId` when every row has one**, and by index otherwise. This means deleting row 2 does
not report every later row as changed. Numbers are compared as decimals, so `184.3` and `184.30` are equal.

## 9. Duplicates: flagged, never refused (owner, 2026-10-27)

At save and at every edit, the request is compared against every other saved request created within
`DuplicateLookbackDays` that is not `discarded`:

| Reason code | Rule |
|---|---|
| `same-payload` | `canonical_sha256` equal. Canonical form: keys sorted, insignificant whitespace removed, numbers normalised to invariant `0.00` where the field is money, strings unchanged. |
| `same-reference` | Same non-empty `reference`. |
| `overlapping-rows` | One or more row fingerprints (CLAUDE.md fingerprint) equal to a row in the other request. Computed only when the payload is readable. |

The **oldest** match is stored in `duplicate_of_id`, all matching reasons in `duplicate_reasons`, and a
`flagged-duplicate` event is written. The flag never refuses the save.

**Posting a flagged request** requires `{ "confirmDuplicate": true }` in the post body. Without it the answer is
`409 duplicate-unconfirmed`, naming the other request and its status, and no attempt is created. The confirming user
and time are stored. G4 still runs inside the post, so even a confirmed duplicate cannot repeat a row QuickBooks
already has. The confirmation only lets the person ask.

A discarded request is not a match, so discarding the copy clears nothing on the original. Re-checking happens on
edit, so fixing a duplicate by editing it clears the flag if it no longer matches.

## 10. Edge cases (real-life scenarios)

### 10.1 n8n retries a save
- **With `Idempotency-Key`** (n8n's HTTP node can send one, for example the email's Message-ID): the same key from
  the same API client returns `200` with the **existing** record and stores nothing new. The same key with a
  different body → `409 idempotency-key-reused`. *Recommended in the brief to the n8n workflow.*
- **Without a key**: a second record is saved and flagged `same-payload` (§9). Nothing is lost, and a person
  discards the copy.

### 10.2 Two people press Post, or one double-clicks
The **claim** is a single conditional update:
`UPDATE saved_requests SET status='posting', row_version=row_version+1 … WHERE id=@id AND status='pending'`. It is
done in the same transaction that inserts the `in-progress` attempt row, and it is **committed before the engine
runs** (CLAUDE.md rule 7). Exactly one caller wins. The other gets `409 already-posting` (or `already-posted`) with
the winning attempt's id. No second attempt row is created.

`POST /{id}/post` also takes an optional `If-Match`. When it is sent and stale, the answer is `412`, so a person never
posts a version they have not seen. *Builder's call:* the dashboard always sends it; n8n never calls this route.

### 10.3 Someone edits while a post is running
Edit is refused in `posting` (`409 not-editable`). Because the claim bumps `row_version`, an edit form opened before
the click gets `412` as well.

### 10.4 QuickBooks busy, down, or backup stale
The attempt is recorded (`busy` / `unavailable`) with the problem body, the request returns to `pending`, and the
`Retry-After` header is passed through. Nothing is retried automatically. A person presses Post again.

### 10.5 The browser closes or times out mid-post
The post continues to the end, as validate-and-post §7 requires. The result is recorded. The dashboard recovers by
reading `GET /{id}`, which shows `posting` until the attempt finishes. The brief tells the dashboard to poll while it
sees `posting`.

### 10.6 The server crashes, or the result cannot be written
- **At startup**, before the API serves the routes in §6, every request still in `posting` is moved to `unknown` and
  its `in-progress` attempt to `unknown`, with `error: "the service restarted while this post was running"`. Nothing
  is retried, because the post may have landed.
- **Result write fails** (database unavailable after QuickBooks answered): the result is written to the attempt's
  evidence folder (`api-batches/<postId>/saved-request.json`, holding the saved-request id and attempt id) and the
  write is retried three times with back-off. If it still fails, the request stays `posting` until startup recovery
  marks it `unknown`. The error is logged at `ERR` with `savedRequestId` and `postId`.
- **Resolving** (`POST /{id}/resolve`, admin): body
  `{ "outcome": "posted" | "not-posted", "note": "…required…" }`. The response shows the matching ledger batch when
  the attempt has a `postId`, so the admin can check QuickBooks before choosing. `posted` → `posted` (and copies
  `postId`). `not-posted` → `pending`. For `partial`, only `posted` is allowed, meaning "accepted as is". To post the
  remaining rows, a person creates a **new** saved request; removing rows that landed is undo's job, outside this
  work (§3). The resolver and the note are stored on the attempt and in events.

### 10.7 The database is down
- `POST /saved-requests` → `503 database-unavailable` with `Retry-After: 30`. n8n's retry setting re-sends it. With
  an Idempotency-Key that is always safe.
- All routes in §6 → `503`. Login → `503`.
- Existing routes (validate-and-post, jobs, health, lists, undo) are unaffected. A DbContext is never resolved on
  their path.
- `/health/ready` shows the database as not ready but still reports the rest. *Builder's call:* readiness is
  per-feature, so a load balancer does not take the whole service out for a dashboard outage. If that is wrong, it
  is Q-83.

### 10.8 The rules file is missing when saving
The save succeeds with snapshot `unavailable` (§6.4). Post still answers `503`, as T-920 does.

### 10.9 A huge or hostile payload
The limits are the same as validate-and-post: body ≤ 2 MB, `MaxTransactionsPerRequest` rows. Strings in the payload
are stored as given and **always rendered as text**. The brief tells the dashboard never to inject them as HTML.
`q` search uses parameters, never concatenated SQL.

### 10.10 A posted request needs a correction
Locked. The dashboard shows the `postId`. The correction is undo (existing route, API key) and then a new saved
request. The spec does not let `posted` go back to `pending`, because that is the path to a double post.

### 10.11 A user is disabled while signed in
Disabling revokes all their refresh tokens immediately and updates their Identity security stamp. Every JWT request
checks the user is enabled and the stamp matches (cached ≤ 30 s), so the longest a disabled user keeps access is
30 seconds. Their name stays on everything they did.

### 10.12 The last admin
Demoting or disabling the only enabled admin → `409 last-admin`. An admin cannot disable themselves.

### 10.13 Clock and time zones
All stored times are UTC. Dates **inside** a payload (`date`) are `DateOnly`, exactly as validate-and-post reads
them. The API never converts them.

### 10.14 Version skew between n8n and the API
Unknown extra fields in a payload are **kept** in the stored `jsonb` (so the original is preserved) and ignored by
binding, as validate-and-post ignores them today. A field the API needs that is missing is a validation finding.

## 11. Dashboard sign-in and users

### 11.1 Roles (owner, 2026-10-27)

| Capability | admin | editor | viewer |
|---|---|---|---|
| View saved requests, versions, attempts, events | ✓ | ✓ | ✓ |
| Read QuickBooks lists (`GET /quickbooks/lists` only) for edit dropdowns | ✓ | ✓ | ✓ |
| Edit, re-validate, discard | ✓ | ✓ | – |
| **Post to QuickBooks** | ✓ | ✓ | – |
| Restore a discarded request, resolve `unknown` / `partial` | ✓ | – | – |
| Manage users | ✓ | – | – |

*Builder's call, recorded:* restore and resolve are admin-only because both can lead to money moving after a person
already decided it should not, or before anyone knows what happened.

### 11.2 Auth routes

| Method & path | Who | Purpose |
|---|---|---|
| `POST /auth/login` | anyone | `{ email, password }` → `200 { accessToken, expiresUtc, user }` and the refresh cookie. Wrong credentials → `401 invalid-credentials` (same answer for unknown email and wrong password). Locked → `423 locked-out` with `Retry-After`. Disabled → `401` (same as wrong credentials). |
| `POST /auth/refresh` | refresh cookie | Rotates: the old token is revoked and a new one issued. Reusing a revoked token revokes the whole family (theft signal) → `401`. |
| `POST /auth/logout` | signed in | Revokes the current refresh token and clears the cookie. |
| `GET /auth/me` | signed in | `{ id, email, displayName, role, mustChangePassword, scopes }`. |
| `POST /auth/change-password` | signed in | `{ currentPassword, newPassword }`. Revokes the user's other refresh tokens. |

- **Access token**: JWT, HMAC-SHA256, `AccessTokenMinutes` (15). Claims: `sub`, `email`, `name`, `role`, `stamp`,
  `jti`. Sent as `Authorization: Bearer`.
- **Refresh token**: 256-bit random, stored hashed, `RefreshTokenDays` (7). Sent as an `HttpOnly; Secure;
  SameSite=Strict` cookie scoped to `Path=/api/v1/auth`. *Builder's call:* keeping it out of JavaScript means an XSS
  bug in the dashboard cannot steal a long-lived credential. This needs the dashboard served from the same site as
  the API (or a reverse proxy, or a dev proxy), and the brief says so. If they will be on different sites, this
  becomes `SameSite=None` plus CORS credentials, which is Q-82.
- `mustChangePassword: true` (new user, admin reset) → every route except `me`, `change-password` and `logout`
  answers `403 password-change-required`.
- Login is rate limited per address and per email using the existing `FailedAuthPerMinute` brake. Identity lockout
  (`MaxFailedAttempts`, `LockoutMinutes`) applies per user. Each failure is recorded in `user_audit`.
- Passwords are hashed by Identity's `PasswordHasher` (PBKDF2). Minimum length is `Password:MinLength` (12), and
  composition rules are off. *Builder's call:* length is what matters.

### 11.3 How JWT and API keys live together

`ApiKeyMiddleware` today authenticates every non-health request by `X-Api-Key`. It becomes the **caller resolver**:

1. `Authorization: Bearer …` present → validate the JWT, load the user (enabled + stamp, §10.11), and make the
   caller a user with the scopes of their role.
2. Else `X-Api-Key` → as today.
3. Both present → `400 ambiguous-credentials`. Neither → `401`.

`ApiRoutes.ScopeFor` maps the new routes **explicitly**, and its fall-through stays `admin`. The test from the
validate-and-post plan's "security trap" is repeated for every new route.

| New scope | Routes | Held by |
|---|---|---|
| `requests:submit` | `POST /saved-requests` | the n8n API client; admin and editor roles |
| `requests:read` | every `GET /saved-requests…` | all roles |
| `requests:edit` | `PUT`, `/validate`, `/discard` | admin, editor |
| `requests:post` | `/{id}/post` | admin, editor |
| `requests:admin` | `/restore`, `/resolve` | admin |
| `users:manage` | `/users…` | admin |
| `lists:read` | `GET /quickbooks/lists` **only** | all roles; API clients that hold `qb:read` also satisfy it |

`GET /health/database` takes `requests:read` **or** `health:read`, so the dashboard can show a status light.

**Roles receive only the scopes in this table.** None of them receives `qb:read`, `health:read`, `qb:post`, `jobs:*`,
`rules:write` or the API-key `admin` scope. `qb:read` opens list sync, connection tests and company-file
validation, which open QuickBooks sessions and can make a real post answer `busy`. A viewer must not be able to do
that. A dashboard user cannot reach the older routes, and can post only through a saved request, which is recorded.

**People only.** `requests:edit`, `requests:post`, `requests:admin` and `users:manage` are satisfied **only by a
signed-in user**. An API key never satisfies them, whatever its scopes. That includes the `admin` scope and the
legacy shared key. Today `ApiClient.Allows` treats `admin` as every scope, and it changes to mean "every scope
except these four" (with a test per scope). *Builder's call, recorded:* this is what §1's success measure, "a named
person pressed Post", requires. An API key may hold `requests:submit` and `requests:read`.

The n8n client is issued `requests:submit` only (via `new-api-client.ps1`), and its old `qb:post` should be
withdrawn once the workflow is switched (deploy checklist).

### 11.4 User management (admin)

| Method & path | Purpose |
|---|---|
| `GET /users` | List. Query `q`, `role`, `disabled`, paging as §6.5. |
| `POST /users` | `{ email, displayName, role }` → `201` with a **one-time temporary password** in the body (shown once, never stored in plain text). The user has `mustChangePassword: true`. Duplicate email → `409`. |
| `GET /users/{id}` | One user, with `lastLoginUtc`, `lockedOutUntilUtc`. |
| `PATCH /users/{id}` | `{ displayName?, role?, disabled? }`. Needs `If-Match`. Role or disable changes revoke refresh tokens and update the stamp. Last-admin rule §10.12. |
| `POST /users/{id}/reset-password` | Issues a new one-time temporary password, sets `mustChangePassword`, and revokes tokens. |
| `POST /users/{id}/unlock` | Clears lockout. |
| `GET /users/{id}/audit` | `user_audit` rows for that user. |

Email is case-insensitive and unique. Users are never deleted (§5.3).

**Bootstrap**: when the users table is empty at startup and `Auth:BootstrapAdmin:Email`/`Password` are set, that
admin is created with `mustChangePassword: true`, and the creation is logged (email only). When the table is not
empty, the settings are ignored and a warning asks that they be removed.

## 12. Logging and audit

- Every line from the §6 routes carries `savedRequestId` and, during a post, `attemptId` and `postId`, alongside the
  existing `requestId` and `clientId`. For a JWT caller, `clientId` is `user:<id>`.
- The existing per-caller audit trail (FR-A-16) also records the §6 and §11 mutating routes, unchanged.
- Never logged: passwords, tokens, the refresh cookie, the connection string, the signing key, or temporary
  passwords.

## 13. Packages (each needs owner approval: Q-80)

| Package | Project | Why |
|---|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` 8.x | Data | The provider. |
| `Microsoft.EntityFrameworkCore.Design` 8.x | Data, build-time only (`PrivateAssets=all`) | `dotnet ef migrations`. |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 8.x | Data | Users, roles, password hashing, lockout. |
| `Microsoft.AspNetCore.Authentication.JwtBearer` 8.x | Api | Bearer validation. |
| `Testcontainers.PostgreSql` | Data.Tests only | Runs a real PostgreSQL in Docker for the store tests (§14). |

Everything else (rate limiting, cookies, CORS, problem details) is in the shared framework.

## 14. Testing

The rules are unchanged: xUnit, `Should_<behaviour>_When_<condition>`, fixtures over inline blobs, no real
QuickBooks or Hermes, `dotnet test` green on Linux.

1. **Core unit tests, first**: the state machine (every row of §7, plus every forbidden transition), the
   attempt-outcome mapping (§7.2), the canonicaliser and duplicate rules (§9), and `PayloadDiff` (§8.3, including
   externalId vs index matching and decimal equality).
2. **API tests** run against `FakeSavedRequestStore` + `FakeQbGateway` with `WebApplicationFactory`: every route ×
   every documented status. They cover the auth matrix (no credentials, bad JWT, expired JWT, disabled user,
   must-change-password, each role, API key with and without each scope, both credentials), the claim race (two
   concurrent posts → exactly one attempt, one gateway session), startup recovery, and `If-Match` handling.
3. **Store tests** (`tests/QbAutopost.Data.Tests`) run against **real PostgreSQL via Testcontainers**: migrations
   apply from empty, the conditional claim under real concurrency, the unique idempotency index, jsonb
   round-tripping, `original_body` byte equality, and the append-only tables refusing updates through the store.
   **When Docker is not available, these tests are skipped with a reason and the run stays green.** CI
   (`.github/workflows/ci.yml`, ubuntu) has Docker, so they run there.
4. **Equivalence test**, the one that matters most. Unbindable payloads are excluded (§7.2). For each other payload in `tests/fixtures/sync-post/*`, saving and then
   posting by id produces the same status code, the same body (minus the added fields) and the same gateway calls as
   validate-and-post with the same payload.
5. **Migration drift test**: fails when the model has changes that no migration covers
   (`HasPendingModelChanges`).
6. **OpenAPI drift test**: extended to the new routes.

## 15. Questions (proposed for `docs/tracker.md` › Questions)

| # | Question | Conservative choice taken until answered |
|---|---|---|
| Q-80 | Approve the five packages in §13? | Nothing is added until approved. T-1002 stops if refused. |
| Q-81 | Should the n8n client keep `qb:post` for a transition period, so the old workflow still works? | Withdraw it at go-live (deploy checklist). The owner can keep it by not running that step. |
| Q-82 | Will the dashboard be served from the same site as the API? | Assume yes (same-site refresh cookie). If not, cross-site cookies plus credentialed CORS. |
| Q-83 | Should `/health/ready` fail as a whole when the database is down? | No. The database is reported per feature (§10.7). |
| Q-84 | Retention: are saved requests and versions kept forever? | Kept forever. No deletion route, no job. |

## 16. Acceptance criteria

| # | Criterion |
|---|---|
| SR-1 | n8n can switch from validate-and-post to `POST /saved-requests` by changing only the URL, and every JSON object it sends is saved (§6.1). |
| SR-2 | The original body is retrievable byte-for-byte, and version 1 never changes after any number of edits. |
| SR-3 | Every edit creates a version with its change list, author and time. A stale `If-Match` is refused. |
| SR-4 | Posting a saved request by id gives the same result as validate-and-post with that payload (equivalence test). |
| SR-5 | Every post attempt is stored with its outcome, its HTTP status, the version it sent and its full result. |
| SR-6 | Two concurrent posts of one request produce exactly one attempt and at most one QuickBooks session. |
| SR-7 | `posted`, `partial`, `unknown` and `discarded` requests cannot be edited or posted. |
| SR-8 | A restart during a post leaves the request `unknown`, never `pending`. |
| SR-9 | Duplicates are flagged, never refused, and posting one needs `confirmDuplicate`. |
| SR-10 | Viewers cannot change anything. Editors cannot manage users or resolve. No role reaches `qb:post` routes. |
| SR-11 | `createdBy`/`updatedBy` always come from the token or API key, never from the body. |
| SR-12 | With PostgreSQL stopped, validate-and-post, jobs and health still work, and the §6 routes answer `503`. |
| SR-13 | `dotnet build -warnaserror` and `dotnet test` are green on Windows and Linux, with the Data tests skipping cleanly where Docker is absent. |
| SR-14 | `CLAUDE.md`, `docs/spec.md` §2, `docs/spec-api-v1.md` §1 and ADR-0002 are amended, and ADR-0009/0010 written. |
