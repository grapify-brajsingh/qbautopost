# Spec — Dashboard API v2 (`/api/v2`): what the dashboard needs (R-1 … R-9)

**Status**: **draft for owner approval**, rewritten 2026-10-28 after the owner's rulings in §0
**Follows**: `docs/saved-requests-postgres.md` (ticket #2, M10), cited as `§n`. `VP §n` is
`docs/validate-and-post-transaction-data.md`.
**Input**: `D:\qb_post_react\docs\api-requirements-for-dashboard.md` (R-1 … R-9). The dashboard's MSW fake API
implements that document. **§14 lists every place this spec differs from it.**
**Branch**: `feature/dashboard-api-v2` (from `feature/2-postgres-saved-requests`) · **Milestone**: M11 · **Tasks**:
T-1101 … (plan to follow)
**Packages**: **none new.** Q-80's list is unchanged.

---

## 0. Owner rulings, 2026-10-28

| # | Ruling |
|---|---|
| O-1 | **No existing API changes.** Every route that exists today keeps its paths, request and response shapes, status codes, behaviour and OpenAPI entries **exactly**. That covers `/api/v1/**`, including validate-and-post, jobs, batches, quickbooks, health, and the M10 `saved-requests`, `users` and `auth` routes. |
| O-2 | **Everything the dashboard needs is a new route**, under **`/api/v2`**. Paths are the same as the v1 ones, relative to the prefix, so the dashboard switches by changing its base constant from `/api/v1` to `/api/v2`. n8n switches to `POST /api/v2/saved-requests`. |
| O-3 | **The old routes will be deprecated once this work is done.** Deprecation (headers, removal) is a later task and not part of this one. Until then, the old routes may still be called, and the risk this leaves is accepted (§13). |
| O-4 | **A "don't post" row is sent as `post: false`**, never rewritten to `kind: "Skip"`. `kind: Skip` is refused with a 400 by `DirectRequestReader.Malformed` (`DirectRequestReader.cs:101`). |

## 1. Anti-goals (carried forward; each is tested again where this work touches it)

- **Never post twice.** v2 posts go through the same claim (§10.2), the same `unknown` lock (§7.2, R31) and G4.
- **Never recalculate `controlTotal`.** A `post: false` row stays in the control-total sum. Nothing writes a derived
  total.
- **Never round amounts.** No new code formats, rounds or adds an amount.
- **Append-only history.** No new `UPDATE` or `DELETE` on `saved_request_versions`, `saved_request_events`,
  `post_attempts` (beyond the existing completion and resolution columns) or `user_audit`. The audit feed is a
  read-only view. The app role's grants on those tables are unchanged.
- **No existing API changes (O-1).** This is proven by the existing tests. **No test for a v1 route is edited or
  deleted** by this work (shared test infrastructure may gain helpers). The v1 `openapi.json` is left byte-identical,
  and a test pins its SHA-256.

## 2. Shape of the solution

```
 n8n ──X-Api-Key──► POST /api/v2/saved-requests ─┐
                                                 ├─► same PostgreSQL tables (+ new columns, + audit view)
 dashboard ──Bearer──► /api/v2/** ───────────────┘
                         │
                         └─► POST /api/v2/saved-requests/{id}/post
                               claim (unchanged) → SyncPostRunner.RunAsync(request, notPosted: {rows with post:false})
                                                    ↑ the only engine difference, and only on this route
 /api/v1/** ──────────────► unchanged handlers (to be deprecated, O-3)
```

- **Code.** New endpoint files live under `src/QbAutopost.Api/Endpoints/V2/`. They reuse the existing services
  (`SavedRequestIntake`, `SavedRequestPoster`, `SavedRequestValidator`, `ISavedRequestStore`, the auth services). Each
  service gains **opt-in** parameters for the v2 behaviour, and every default reproduces v1 exactly.
- **Data.** v1 and v2 read and write the **same tables**. A request saved through either version is visible through
  both. v1 responses do not show the new columns.
- **Middleware.** The caller resolver, scopes (`ApiRoutes.ScopeFor`), rate limiting, the audit trail (FR-A-16), the
  database gate (503) and logging context apply to `/api/v2` exactly as to `/api/v1`. Every v2 route is named
  explicitly in `ScopeFor`. The fall-through stays `admin`, and the security-trap test is repeated for every v2 route.
  Any code that matches on the `/api/v1/` prefix is extended to `/api/v2/` **without** changing how v1 paths are
  matched.

## 3. v2 route table

The scopes are §11.3's, unchanged. "Same as v1" means **the same handler and shape** as the v1 route, served at the
v2 path.

| Method & v2 path | Scope | Difference from the v1 route |
|---|---|---|
| `POST /saved-requests` | `requests:submit` | `email`, `pushId` (§4, §5); `row-<n>` ids (§6.6) |
| `GET /saved-requests` | `requests:read` | New row fields and query parameters (§9) |
| `GET /saved-requests/stats` | `requests:read` | New shape (§9.3) |
| `GET /saved-requests/{id}` | `requests:read` | New fields (§9.1, §10 4.9) |
| `GET /saved-requests/{id}/original` | `requests:read` | Same as v1 |
| `GET /saved-requests/{id}/versions`, `/versions/{n}` | `requests:read` | Same as v1 (change lists are stored, so they show what the editing version computed) |
| `PUT /saved-requests/{id}` | `requests:edit` | v2 hash and diff rules (§7); `edit<v>-<n>` ids (§6.6) |
| `POST /saved-requests/{id}/validate` | `requests:edit` | The snapshot honours `post: false` (§6) |
| `POST /saved-requests/{id}/post` | `requests:post` | Honours `post: false`; `400 nothing-to-post`; 409 extras (§10 4.4) |
| `GET /saved-requests/{id}/attempts`, `/attempts/{attemptId}` | `requests:read` | `ledgerBatch.posted[].externalId` (§10 4.8) |
| `GET /saved-requests/{id}/events` | `requests:read` | Same as v1 |
| `POST /saved-requests/{id}/discard`, `/restore`, `/resolve` | as v1 | `412` extras (§10 4.5) |
| `GET /audit-events`, `GET /audit-events.csv` | `requests:read` | New (§8) |
| `GET /users`, `POST /users`, `GET/PATCH /users/{id}`, `POST /users/{id}/reset-password`, `/unlock`, `GET /users/{id}/audit` | `users:manage` | `GET /users`: `status` filter and field (§12). The rest are the same as v1 |
| `POST /auth/login`, `/refresh`, `/logout`, `/change-password`; `GET /auth/me` | as v1 | Refresh cookie `Path=/api/v2/auth`; refresh reuse grace (§11) |
| `GET /quickbooks/lists` | `lists:read` | Same as v1 |
| `GET /health/database` | `requests:read` or `health:read` | Same as v1 |
| `GET /openapi.json` | as v1's `openapi.json` | The v2 document (§15) |

Every `412` across v2 (edit, validate, post, discard, restore, resolve) carries the §10 4.5 extras.

## 4. Database changes (one migration, `DashboardV2`)

### 4.1 New columns on `saved_requests`

Every new column is filled **only by v2 code**, except the denormalised counts (`skip_count` and `problem_count`).
The store keeps those whichever version wrote, because they are internal and no v1 response shows them.

| Column | Type | Filled | Changes after save? |
|---|---|---|---|
| `email_subject` | `text` null | `$.email.subject` from the **original body**, at a v2 save. Truncated to 998 characters, never splitting a surrogate pair. | Never |
| `email_received_utc` | `timestamptz` null | `$.email.receivedUtc`. Only an ISO-8601 instant with an offset or `Z`, otherwise null. | Never |
| `email_from` | `text` null | `$.email.from` (string, truncated at 998) | Never |
| `email_message_id` | `text` null | `$.email.messageId` (string, truncated at 998) | Never |
| `push_id` | `uuid` null | `$.pushId`, when it parses as a UUID (any version, any case, with or without dashes) | Never |
| `push_id_hex` | `text`, generated stored: `replace(push_id::text, '-', '')` | (derived) | (derived) |
| `metadata_errors` | `text[]` not null, default `{}` | Problems reading `pushId` (§5) | Never |
| `skip_count` | `int` not null, default 0 | Rows of the current version whose `post` is the JSON literal `false` | With the other denormalised columns, on every save or edit |
| `validation_v2` | `jsonb` null | The **v2** snapshot of the latest version (§6.4) | Whenever a snapshot is written, by either version |
| `needs_attention_v2` | `bool` not null, default false | The v2 rule (§5.3) | With `validation_v2` |
| `problem_count` | `int` not null, default 0 | `len(validation_v2.validation) + len(validation_v2.errors)` | With `validation_v2` |

### 4.2 Other changes

- **Backfill.** `validation_v2` copies `validation`, and `needs_attention_v2` copies `needs_attention`. The two rules
  agree on every stored payload, because none has a `post` field or a `pushId`. `problem_count` is computed from the
  copy. `skip_count` is computed from the current version's payload in SQL, which gives 0 everywhere. The [server]
  checklist confirms "no stored `post`/`pushId`" before migrating.
- **`refresh_tokens`.** New column `grace_successor_id bigint null` (§11).
- **New indexes:**
  - `ux_saved_requests_push_id`: unique on `push_id` where not null.
  - `ix_saved_requests_push_id_hex`: `text_pattern_ops`.
  - Descending time indexes for the audit feed: `saved_request_versions(created_utc)`,
    `saved_request_events(at_utc)` and `user_audit(at_utc)`.
- **New view `audit_events`** (§8). The app role gets `SELECT` on it. `deploy/migrations.sql` and
  `deploy/grants.sql` are regenerated, and the grants drift test covers the view.
- *Builder's call:* no index on `email_received_utc`. At about 10 saves a day, sorting on
  `coalesce(email_received_utc, created_utc)` stays cheap for years. This is the "measure first" rule of §6.5.

## 5. R-1 and R-5: email metadata and `pushId` (v2 save only)

### 5.1 `email`

The save body may carry an optional `"email": { "subject", "receivedUtc", "from", "messageId" }`, with every field
optional.

- **Source.** The columns come only from the original body, at a v2 save. An edit never changes them.
- **Malformed.** If `email` is not an object, or a field has the wrong type, the save still succeeds. That field's
  column stays null, and nothing is flagged.
- **Storage.** It stays in `original_body` and in the version-1 payload. Binding ignores it, so it never reaches the
  engine.
- **Output.** Returned as `"email": { subject, receivedUtc, from, messageId }`, or `null` when all four columns are
  null.

### 5.2 `pushId`

- **Valid UUID.** Stored in `push_id` and returned as `pushId`, lower-case with dashes (otherwise `null`).
- **Present but not a UUID.**
  - The save succeeds, and `push_id` stays null.
  - `metadata_errors` gets `$.pushId: not a UUID`. On the v2 detail it is listed **first in `readErrors`**, and it sets
    `needsAttention` in v2 responses.
  - The version stays readable and **postable**.
  - The flag never clears for that request (Q-95).
- **Repeated `pushId`.** The checks run in this order:
  1. `Idempotency-Key` first, unchanged.
  2. The same `pushId` with the same original-body SHA-256 → `200` with the existing record and
     `Idempotency-Replayed: true`.
  3. The same `pushId` with a **different** body → **`409 push-id-reused`**, naming `savedRequestId` and `publicRef`.
     Nothing is stored (Q-94).
  - **Order.** When an `Idempotency-Key` is sent and matches a record, the key's rules decide **alone**, exactly as
    today: the same body → `200` with that record; a different body → `409 idempotency-key-reused`. The `pushId`
    check runs only when no key matched. (If the key and body match record A, the `pushId` is inside that body, so the
    unique index means no other record can hold it.)
  - A race on the unique index re-reads the winner, inside the same save transaction as the key check.
- **Search.** When `q`, with its dashes removed, is 1–32 hex characters, it also matches
  `push_id_hex LIKE lower(q) || '%'`.

### 5.3 `needsAttention` in v2

- **Rule.** `needs_attention_v2` = the latest version is unreadable **under v2 reading** (§6.1), **or** the v2
  snapshot is `held` or `refused`, **or** `metadata_errors` is non-empty.
- **Where it applies.** It is the value v2 responses show, **and** the column the v2 `needsAttention` filter, the
  `hasErrors` filter and v2 stats use. So a badge and its tab always agree.
- **v1 is untouched.** v1's `needs_attention` column and v1 responses don't change.

## 6. R-6: per-row "don't post" (`post: false`)

### 6.1 How v2 reads it, without touching v1 binding

- **Row type unchanged.** `DirectRow` is **not** changed. v1 binding, where `post` is an unknown member and so
  ignored whatever its type, stays exactly as it is.
- **New reader.** v2 reads the flag from the stored payload with a new Core reader, `NotPostedRows.Read(JsonElement
  payload)`. It returns the indexes whose row object has `"post": false`, or a read error:
  - `$.transactions[i].post: must be true or false` for any other JSON type, which makes the version unreadable in v2.
  - A missing `post`, or `true`, means post.
- **Engine input.** `DirectRequest` gains `IReadOnlySet<int> NotPosted { get; init; } = empty`.
  - `SyncPostRequest.ToDirectRequest()` keeps its signature and still passes an empty set.
  - A new overload, `ToDirectRequest(IReadOnlySet<int> notPosted)`, is used only by v2.
  - `SyncPostRunner.RunAsync` gains an optional `notPosted` argument, default empty.
  - With an empty set, every line of the engine runs as today. That is what makes v1 provably unchanged.

### 6.2 What the engine does with a row in `NotPosted`

This is used by v2 `post`, v2 `validate`, and the snapshot at v2 save and edit.

1. **Malformed checks still apply**, with the row's real kind. A bad amount or a bad kind still refuses the whole
   request (400).
2. **It counts toward the control-total check**, because `ControlTotal()` sums every submitted row before any row is
   set aside.
3. **It is set aside before the per-row holds** (`account-missing`, `amount-over-limit`, `date-too-old`,
   `date-in-the-future`):
   - `DirectReadResult` gains `NotPosted`.
   - The row never becomes a `DirectLine`, so it never reaches the mapper, `ApplyBatchGates`, live G4, qbXML or the
     SDK.
4. **The planner reports it** as `Outcome = Skipped`, `Reason = "not-to-post"` (new constant
   `SkipReasons.NotToPost`), with its kind, account, amount and `externalId` as sent:
   - `DirectPlan.Ok` is unaffected.
   - It counts under `counts.skipped` and is listed under `skipped`.
   - It is never `held`, and never a finding.
5. **Every row not posted** → a request-level error,
   `nothing-to-post: every row is marked post: false; nothing would be sent`:
   - The snapshot is `refused`.
   - v2 `post` checks this **before the claim** and answers **`400 nothing-to-post`** (problem `type`
     `nothing-to-post`). No attempt is created.
6. **Ledger.** It is never a ledger entry; only the batch's `Skipped` count records it.
7. **Status.** It never makes a post `partial` or `failed`.
8. **Unreadable `post` value** (for example `"post": "no"`). v2 `post` treats this exactly as v1 treats an unbindable
   payload (§7.2, `SavedRequestPoster.cs:119-128`): the claim is taken, a `refused` attempt with HTTP `400` naming
   the path is recorded, and the request returns to `pending`. So it leaves an attempt and a `post-failed` audit
   event, like any other refusal.
9. **Duplicate detection is unchanged.** `row_fingerprints` and `overlapping-rows` include `post: false` rows, so a
   v1 save and a v2 save of the same body agree. *Builder's call:* this flags more, never less, and a flagged request
   only needs `confirmDuplicate`.

### 6.4 Two snapshots, one per version

The shared snapshot column (`validation`), `needs_attention`, and each version's `readable`/`read_errors` stay **v1's
judgement**, written by v1 logic exactly as today. That way a v1 response never claims "ok" for a request a v1 post
would hold.

- **Every snapshot write computes both.** At a save or edit through either version, and at either version's
  `validate`, the validator runs twice on the same inputs: once with an empty `NotPosted` (v1 → `validation`,
  `needs_attention`) and once with the v2 reading (v2 → `validation_v2`, `needs_attention_v2`, `problem_count`).
  The cost is one extra offline plan.
- **v2 reads its own judgement.** v2 responses use `validation_v2` and `needs_attention_v2`.
- **Version readability in v2.** A version's v2 readability is `readable` (v1) **and** `NotPostedRows.Read` finding
  no error. It is computed when the version is read, because version rows are append-only and get no new column.
- **Display.** In v2 responses, `readErrors` lists the `metadata_errors` first, then the v1 read errors, then the v2
  `post` errors.

### 6.3 Answers to R-6.2

| Question | Answer | Tests |
|---|---|---|
| (a) Counts toward `controlTotal`? | **Yes.** `controlTotal` minus the skipped rows is refused as `control-total-mismatch`. | `DirectRequestReaderTests.Should_CountNotPostedRowInControlTotal_When_RowIsNotPosted`, `Should_RefuseControlTotalMismatch_When_TotalExcludesNotPostedRows` |
| (b) Under `skipped`, never `held`? | **Yes**, with `reason: "not-to-post"`, even for a row that would otherwise be held. | `DirectPlannerTests.Should_ReportNotPostedRowAsSkipped_When_RowIsNotPosted`, `Should_NotHoldNotPostedRow_When_RowWouldBeHeldOtherwise`; Api `V2PostTests.Should_ListNotPostedRowUnderSkipped_When_PostedById` |
| (c) Never sent to QuickBooks? | **Yes.** It is absent from qbXML and from every gateway call. | `DirectPlannerTests.Should_LeaveNotPostedRowOutOfQbXml_When_Planned`; `V2PostTests.Should_NotSendNotPostedRowToGateway_When_PostedById` |
| All rows off | `400 nothing-to-post`, no attempt | `V2PostTests.Should_Answer400NothingToPost_When_EveryRowIsPostFalse` |
| v1 unchanged | A payload with `post: false` posted through **v1** `/saved-requests/{id}/post` and validate-and-post behaves as before this work: the row posts (§13). | `V1UnchangedTests.Should_IgnorePostFlag_When_PostedThroughV1` |

## 7. v2 canonical hash and change list

The v2 rules are opt-in options on `PayloadCanonicalizer` and `PayloadDiff`, named `PayloadRules.V2`. v1 calls them
with the default, which is unchanged.

| Key | Hash (v2) | Change list (v2) |
|---|---|---|
| top-level `email`, `pushId` | dropped | dropped |
| row `post: true` | dropped (absent ≡ `true`) | absent compared as `true`, so turning a row off is `replace $.transactions[i].post true → false` |
| everything else | as v1 | as v1 |

- **Existing hashes stay valid.** For a payload without those keys, the v2 hash equals the v1 hash. No stored payload
  has those keys today, so every stored `canonical_sha256` stays valid and duplicate detection works across versions.
- **Mixed versions.** A request **saved** through v2 and **edited** through v1 is hashed and diffed by v1's rules on
  that edit. Accepted until deprecation (§13).

### 7.1 R-6.4: `externalId` assignment (v2 only)

- **v2 save.** Before version 1 is written:
  - Each object row whose `externalId` is missing, `null` or `""` gets `row-<n>` (1-based position).
  - If `row-<n>` is already used by another row, `-2`, `-3`, … is appended until it is unique.
  - Rows that have an id keep it.
  - Nothing is assigned when `$.transactions` is not an array, or to elements that are not objects.
- **v2 edit (`PUT`).** A row without an id gets `edit<v>-<n>`, where `v` is the new version number. It can never
  collide with an earlier id. The dashboard sends `dash-<uuid>` anyway.
- **What stays exact.** `original_body`, `GET …/original` and `original_sha256` (used for idempotency and `pushId`
  replay) keep the exact bytes. This is the one exception to "version 1 equals what was received" (§8.1), and only for
  v2 saves.
- **Hash after assignment.** The canonical hash is computed after assignment, so two identical v2 pushes still match
  `same-payload`.
- **Old requests.** Requests saved before this change, or through v1, are never rewritten, and their diffs keep
  matching by index (`PayloadDiff.cs:43-49`).
- **Format.** The `externalId` rule is unchanged: any string of at most 255 characters. It is not part of the
  fingerprint, so G4 is unaffected.
- **Duplicate ids** that n8n sends itself are left as they are, and that request diffs by index. *Builder's call.*

## 8. R-7: audit events (`/api/v2/audit-events`)

### 8.1 Access and query

- **Scope.** `requests:read`. **User events are included only for callers holding `users:manage`**. They are silently
  filtered out for everyone else, including from `total`.
- **Query parameters:**
  - `from` (inclusive) and `to` (exclusive): ISO instants with an offset.
  - `actor`: a user id, `api-client:<id>` or `system`.
  - `requestId`: numeric or `SR-…`.
  - `reference`: exact match.
  - `pushId`: a full UUID.
  - `action`: repeatable.
  - `q`: case-insensitive contains, over the JSON text of `old` and `new`, `field`, `reference`, `publicRef` and the
    push id hex.
  - `page` ≥ 1; `pageSize` 1–200, default 50.
- **Errors.** A bad value → `400 invalid-request`.
- **Order.** `atUtc desc, id desc`.

### 8.2 Response

The response is R-7.3's shape: `{ items: [{ id, atUtc, actor{id,name,kind}, action,
request{id,publicRef,reference,pushId,emailSubject}|null, row{externalId,index}|null, field, old, new,
versionNumber, detail }], page, pageSize, total }`.

- **`actor.kind`** is `user`, `api-client`, **`system`** or **`anonymous`**. The last two are additions (Q-97).
- **`row.index`** is the index in the new version for add and replace, and in the previous version for remove.

### 8.3 View `audit_events`

The view is a `UNION ALL` over existing tables. Nothing new is written.

| Source | Action | `id` |
|---|---|---|
| event `created` | `received` | `e<id>` |
| event `flagged-duplicate` | **`flagged-duplicate`** (addition). `detail` = `{ of, reasons }` | `e<id>` |
| event `validated` | `validated` (`detail.state`) | `e<id>` |
| event `post-started` | `post-started` (`detail.attemptId`) | `e<id>` |
| event `post-finished`, outcome posted | `posted` (`detail.attemptId`, `postId`) | `e<id>` |
| event `post-finished`, any other outcome | `post-failed` (`detail.attemptId`, `outcome`, `status`, `postId`) | `e<id>` |
| events `discarded`, `restored`, `resolved` | same name, with the event's detail | `e<id>` |
| event `edited` | not emitted: its changes are; the edit `note` goes into each change's `detail.note` | — |
| version change `$.transactions[i]` add / remove | `row-added` / `row-deleted` (whole row in `new` / `old`) | `v<versionId>-<opIndex>` |
| version change `$.transactions[i].post` | `row-post-off` if new is `false`, otherwise `row-post-on` | same |
| version change `$.transactions[i].<field…>` | `row-edited`, `field` = path after the row | same |
| version change `$.controlTotal` | `control-total-changed` | same |
| any other version change path | **`request-edited`** (addition), `field` = path without `$.` | same |
| `user_audit` `created`, `role-changed`, `disabled`, `enabled`, `password-reset`, `unlocked`, `password-changed`, `login`, `login-failed`, `locked-out` | `user-created`, `user-role-changed`, `user-disabled`, `user-enabled`, `user-password-reset`, `user-unlocked`, `user-password-changed`, `user-signed-in`, `user-sign-in-failed`, `user-locked-out` | `u<id>` |
| `user_audit` `refresh-reuse` (new, §11) | **`user-session-revoked`** (addition, Q-97). `detail` = `{ familyId }` | `u<id>` |
| any other `user_audit` kind (one added later) | `user-other`, `detail` = `{ kind }`, so nothing is ever dropped from the feed | `u<id>` |

Notes on the view:
- Enum values inside stored event details are PascalCase today. The view outputs them kebab-case, and the stored rows
  are not changed.
- The fake store uses a Core `AuditEventMapper` with the same table. One contract suite runs against both stores.

### 8.4 CSV and `historyCount`

**CSV** (`/audit-events.csv`):
- Same filters. All matching rows, as `text/csv; charset=utf-8` with a BOM.
- More than 50 000 rows → `413 too-many-rows`.
- Columns: `time_utc, actor, action, reference, push_id, sr_ref, row, field, old, new`.
- RFC 4180 escaping. A value starting with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'`.

**`historyCount`**, on v2 list rows and the detail: the count of `audit_events` rows for the request, computed in the
query. *Builder's call:* if the page query exceeds 200 ms at the default page size, it becomes a stored count.

## 9. R-8: statements list (v2)

### 9.1 New fields on v2 list rows and detail

| Field | Rule |
|---|---|
| `pushId`, `email`, `historyCount` | §5, §8.4 |
| `validationSummary` | `{ state (snapshot state, or "none"), problemCount (problem_count + metadata_errors), validatedUtc, versionNumber }`. The existing `validation` field stays. |
| `postSummary` | From the latest attempt with outcome `posted` or `partial`: `{ postId, postedCount: counts.posted, postedUtc: finished_utc, postedBy: {id, name} }`. If the request was settled `posted` by resolve with no such attempt: `postedCount: null`, `postedUtc: resolved_utc`, `postedBy`: the resolver. Otherwise `null`. |
| `skipCount` | The `skip_count` column |
| `otherUnpostedPushes` | **Detail only.** Other requests with the same **non-empty** `reference` and status `pending`, `posting`, `partial` or `unknown`: `[{ id, publicRef, pushId, receivedUtc, status }]`, ordered by `receivedUtc` = `coalesce(email_received_utc, created_utc)`. |
| `ledgerBatch` | **Detail only.** §10 4.9 |

### 9.2 Query parameters

These are v1's parameters plus the following, all combined with AND:

| Parameter | Rule |
|---|---|
| `q` | Also matches `email_subject` and the push id prefix |
| `receivedFrom`, `receivedTo` | Dates, UTC, `receivedTo` inclusive, compared with `coalesce(email_received_utc, created_utc)` |
| `emailFrom` | Contains, case-insensitive |
| `reference` | Exact |
| `minTotal`, `maxTotal` | Invariant decimals, inclusive, compared with `control_total`. A null total is excluded when either is given. `min > max` → 400 |
| `hasErrors` | `problem_count > 0 OR needs_attention_v2` (`false` is the negation). The `needsAttention` filter and every stats count use `needs_attention_v2` (§5.3) |
| `sort` | Also `received`/`-received`, `total`/`-total` (alias of `controlTotal`, nulls last), `rows`/`-rows`. The `id` tie-break is kept |

### 9.3 v2 stats

```json
{ "total": 131,
  "byStatus": { "pending": 7, "posting": 0, "posted": 120, "partial": 0, "unknown": 1, "discarded": 3 },
  "needsAttention": { "pending": 2, "all": 4 },
  "duplicate": { "pending": 1, "all": 3 },
  "hasErrors": { "pending": 2, "all": 5 } }
```

A test pins that `status` combines with `needsAttention`, `duplicate` and `hasErrors` (AND).

## 10. R-4: contract items, as served by v2

| # | v2 behaviour | Test |
|---|---|---|
| 4.1 | `ETag: "<rowVersion>"` on the detail and every mutation reply. In OpenAPI. | `Should_ReturnEtag_When_DetailRead` |
| 4.2 | §9.3 | `Should_CountPendingOnlyFlags_When_StatsRead` |
| 4.3 | The snapshot carries `versionNumber`. | (shape test) |
| 4.4 | `409 already-posting` and `already-posted` carry `attemptId`, `startedBy {id,name}` and `startedUtc`. On `already-posted` these may be null when the request was settled by resolve. | `Should_NameWhoStarted_When_AlreadyPosting` |
| 4.5 | Every v2 `412 edited-by-someone-else` carries `rowVersion`, `updatedUtc` and `updatedBy {id,name}`. | `Should_IncludeUpdatedUtc_When_Stale` |
| 4.6 | `/api/v2/quickbooks/lists` uses `lists:read`, every role holds it, and its response schema is in the v2 document. | scope + shape |
| 4.7 | `TxnKind` is an enum in the v2 document. Its description says `skip` is refused on input and that `post: false` is how to skip a row. | drift test |
| 4.8 / 9.3 | The attempt detail's `result.posted[]` has `index`, `externalId` and `txnId`. In v2, `ledgerBatch.posted[]` also gains `externalId`, joined by `index`. | `Should_ListTxnIdPerRow_When_AttemptPosted` |
| 4.9 | The detail has `ledgerBatch: { postId, status, createdUtc, counts{submitted, posted} } \| null`. It is set when status is `partial` or `unknown`, the last attempt has a `postId`, and the ledger has that batch. | `Should_ShowLedgerBatch_When_Unknown` |
| 4.10 | §11 | |
| 4.11 | §13.2: **yes**, rarely | `Should_EndPartial_When_GatewayRejectsSomeRows` |

## 11. R-4.10: refresh reuse grace (v2 `/auth/refresh` only)

**"The same successor" cannot be returned**, because only token hashes are stored. Instead:

- A revoked token is accepted **once**, as a grace refresh, when all of these hold:
  - (a) It was revoked by rotation (`replaced_by_id` set, family not revoked).
  - (b) It was revoked ≤ `Auth:Jwt:RefreshReuseGraceSeconds` ago (default 30).
  - (c) Its successor is neither rotated nor revoked.
  - (d) The caller's address equals the successor's `created_ip`.
  - (e) `grace_successor_id` is null.
- **Then** a second successor is issued in the same family and recorded in `grace_successor_id`. Both successors stay
  valid.
- **Otherwise** the family is revoked → `401`, as v1 does. In v2 the refusal is also written to `user_audit` as kind
  `refresh-reuse`. That is an insert, so the table stays append-only.
- **Cookies.** v2 login and refresh set the cookie with `Path=/api/v2/auth`, so v1 and v2 sessions never see each
  other's cookie. v1 refresh is unchanged and has no grace.

## 12. R-9: users (v2)

- **Status filter.** `GET /api/v2/users` accepts `status` (repeatable). Values are OR'd within the parameter and
  combined with AND with `role`, `disabled` and `q`. An unknown value → 400. The statuses are mutually exclusive,
  evaluated in order:
  1. `disabled`
  2. `locked`: not disabled, and `lockout_end > now`
  3. `must-change-password`
  4. `active`
- **List rows** carry `mustChangePassword` and `lockedOutUntilUtc`, as v1 already does, plus a new `status` field.
- **Activity.** `GET /api/v2/audit-events?actor=<userId>` gives a user's activity.

## 13. Hosting, R-2 and R-3; old-route risk

### 13.1 Serving the dashboard (R-2)

**Folder and pipeline:**
- **Folder.** `Dashboard:Path`, default `C:\qb-autopost\dashboard`, and `Dashboard:Enabled`, default `true`.
- **Which paths belong to the dashboard.** `Api:LegacyRoutes` is on by default (`AppSettings.cs:49`,
  `appsettings.json:10`), and it maps every v1 route a second time at a flat path (`/health`, `/jobs`, `/batches`,
  `/quickbooks`, `/qb`, `/rules`; `Program.cs:440-445`). Those must keep answering exactly as today (O-1). So a
  dashboard path is one that:
  - (a) does not start with `/api/`, and
  - (b) does not start with any legacy flat prefix, **whether or not legacy routes are enabled**. The prefix list
    lives in `ApiRoutes` beside the flat surface it describes, and a test asserts it covers every flat route mapped.
  - The dashboard's own client routes (`/statements`, `/requests`, `/audit`, `/users`, `/login`) clash with none of
    them. A test pins that too.
- **Pipeline.** Placed before the caller resolver and the other API middleware, for dashboard paths only:
  - Existing files are served from a `PhysicalFileProvider`. Dotfiles and hidden files are excluded.
  - Any other `GET` or `HEAD` returns `index.html`.
  - Any other method gets `405`.
  - No authentication.
- **No effect on API paths.** Every `/api/v1`, `/api/v2` and legacy flat path keeps exactly today's handling.
- **Unknown API paths.** An unknown path under any API prefix answers as v1 does today: it falls through to the `admin`
  scope (`ApiRoutes.cs:232-234`), giving `401` without credentials, `403 scope not granted` to a non-admin
  (`CallerMiddleware.cs:63,172`), and a JSON `404` only to an admin. It **never returns HTML**.

**Headers:**
- `index.html`: `Cache-Control: no-cache`.
- `/assets/*`: `public, max-age=31536000, immutable`.
- HTML gets R-2's Content-Security-Policy, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.
- Assets get `nosniff`.

**Missing folder.** `404 text/plain` "dashboard not installed", plus one startup warning. The API keeps working.

**CORS.** No `Access-Control-*` headers unless `Api:Cors:AllowedOrigins` is set. The deploy checklist leaves it empty,
and a test asserts it.

**[server] HTTPS on the office network.** Runbook steps:
- `Api:Bind` = `https://0.0.0.0:5443` with `Api:Tls`.
- A certificate the accountant's PC trusts: internal CA, or a self-signed certificate imported into its Trusted Root
  store.
- A firewall rule for TCP 5443 on the Domain and Private profiles, `RemoteAddress LocalSubnet`.
- `deploy/install-dashboard.ps1 -Source <dist>` copies to `dashboard.new`, then renames.

### 13.2 Long post (R-3) and `partial` (R-4.11)

| Question | Answer, from the code |
|---|---|
| **Kestrel cut-off?** | **None.** `Program.cs:50-59` sets only `MaxRequestBodySize`, the server header and TLS. There is no `AddRequestTimeouts`. `MinResponseDataRate` applies only while a response body is being written, and `KeepAliveTimeout` only to idle connections. |
| **Proxy?** | **None.** The app is a bare Kestrel process started by a Task Scheduler task with no execution time limit (`deploy/install-task.ps1`). The runbook adds that any future proxy needs a timeout of at least 10 minutes. |
| **Does a disconnect cancel the post?** | **No.** The request token is used only before the claim (`SavedRequestPoster.cs:75`). The claim, the engine and the recording run under `CancellationToken.None` (`:93-95`, `:136`, `:337`). The v2 route uses the same poster. Pinned by `PostApiTests.Should_FinishAndRecord_When_ClientDisconnects` (v1) and a v2 twin. |
| **Server-side bound** | `QuickBooks:BusyTimeoutSeconds` (60) applies to each of: the SDK lock wait, each live-G4 query (one per kind and account group), and **the single add call that carries every row**. Worst case is about 60 + 60 × groups + 60 + 5 s, which is under 5 minutes for 1–2 groups. |
| **Risk** | A 500-row add that takes longer than 60 s holds every row `quickbooks-busy`, so the request locks as **`unknown`**. Nothing is posted twice. Nobody has measured how long such an add takes; the [server] checklist measures it (Q-96). |
| **Can a post by id end `partial`?** | **Yes, rarely.** The pre-send gate is all-or-nothing (`SyncPostRunner.cs:74-82`). `partial` happens only when some rows land and a check after the gate holds a row: live G4 (`possible-duplicate` or `duplicate-check-failed`), or QuickBooks rejecting rows (G5). See `DirectPostRunner.cs:211-214`. Skipped and `post: false` rows never cause it. |

### 13.3 Accepted risks from leaving v1 unchanged (O-1, O-3)

1. **v1 post ignores `post: false`.** A request whose current version has a `post: false` row (made in the dashboard),
   posted through **v1** `POST /saved-requests/{id}/post`, sends that row to QuickBooks.
   - **Who can do it:** only a signed-in admin or editor (`requests:post`; API keys never hold it, §11.3).
   - **Mitigation:** the dashboard calls v2 only.
   - Recorded as a deprecation prerequisite: retire the v1 post route first.
2. **v1 edit uses v1 hash and diff rules.** A v1 `PUT` on a v2-saved request diffs `email` and `pushId` like any other
   field, and does not assign ids.
3. **v1 responses don't show the new metadata.** v1 detail and list omit `email`, `pushId` and the other new fields,
   and v1 `needsAttention` ignores `metadata_errors`.

## 14. Differences from `api-requirements-for-dashboard.md` (the dashboard follows these)

| Req | The document says | The API does | Why |
|---|---|---|---|
| all | `/api/v1/...` | **`/api/v2/...`**, same relative paths. The refresh cookie path is `/api/v2/auth`. | O-1, O-2 |
| R-6.2 | Send `post: false` rows as `kind: "Skip"` | Send the payload unchanged. `post: false` is honoured, the row keeps its real kind, and the reason is `not-to-post`. | O-4 |
| R-5.2 | Repeated `pushId` → `200` | Same body → `200`. Different body → **`409 push-id-reused`**. | No silent loss (Q-94) |
| R-5.1 | Malformed → read error | Listed first in `readErrors` and sets `needsAttention`. Still postable. Never clears. | Q-95 |
| R-4.2 | stats shape | As suggested, plus `total` and `hasErrors` | — |
| R-4.4 | Who started | `attemptId`, `startedBy`, `startedUtc`. They may be null on `already-posted`. | — |
| R-4.10 | The same successor | One extra successor, once, from the same address only. | Hashes only (§11) |
| R-7.3 | `actor.kind` is user or api-client | Also `system` and `anonymous`. | Q-97 |
| R-7.4 | Action enum | Also `flagged-duplicate`, `request-edited`, `user-session-revoked` and `user-other`. No separate `edited`; its note goes in `detail.note`. | Q-97 |
| R-7.6 | Prefix `= + - @` | Also tab and CR. | OWASP |
| R-8.3 | `postedCount` is a number | May be `null` after resolve. | — |
| R-8 | — | `sort=total` is an alias of `controlTotal`. | — |
| R-9.1 | — | Rows also carry `status`. | Addition |
| R-1.5 | Received filter | It uses `coalesce(received, created)`, like the sort. | Consistency |
| R-2 | `GET /api/v1/nope` → JSON 404 | Unchanged v1 behaviour: `401` without credentials, `403` for a non-admin, and a JSON `404` only for an admin. Never HTML. The same applies under `/api/v2`. | O-1 |
| R-2 | Every non-`/api/` GET is the dashboard | Except the legacy flat prefixes (`/health`, `/jobs`, `/batches`, `/quickbooks`, `/qb`, `/rules`), which stay API. | O-1 |

## 15. OpenAPI

- **New document.** `src/QbAutopost.Api/wwwroot/openapi-v2.json`, served at `GET /api/v2/openapi.json` under the same
  rules as v1's. It documents every v2 route with the shapes above: the `AuditAction`, `AuditActorKind` and
  `UserStatus` enums, the new problem types (`nothing-to-post`, `push-id-reused`, `too-many-rows`), `ETag`, and the
  `post` row field.
- **Drift test.** `OpenApiDriftTests` is extended so the v1 document covers exactly the `/api/v1` endpoints and the v2
  document exactly the `/api/v2` ones. The v1 document's SHA-256 is pinned (§1).
- **Static routes.** Dashboard static routes are in neither document.

## 16. Questions (for `docs/tracker.md` › Questions)

| # | Question | Choice until answered |
|---|---|---|
| Q-94 | A repeated `pushId` with a **different** body: `200` existing (R-5.2), or `409`? | `409 push-id-reused` |
| Q-95 | Should an edit clear the malformed-`pushId` flag? | No, it stays on. Posting is not blocked. |
| Q-96 | Raise `QuickBooks:BusyTimeoutSeconds` (60) after measuring a 500-row add? | Unchanged until measured |
| Q-97 | Accept the audit additions (actions `flagged-duplicate`, `request-edited`, `user-session-revoked`, `user-other`; actor kinds `system` and `anonymous`)? | Added |
| Q-98 | Before v1 is deprecated, should the v1 post route be retired first, because it ignores `post: false` (§13.3)? | Recorded as the first deprecation step. Nothing changes in this work. |

## 17. Acceptance criteria

| # | Criterion | From |
|---|---|---|
| D-0 | No v1 route changes. Every existing test passes unedited, the v1 `openapi.json` hash is unchanged, and `V1UnchangedTests` pass. | O-1 |
| D-1 | A v2 save with `email` returns it on the detail and list. The subject is searchable. A save without it works. Edits never change the metadata, the hash or the change list because of `email`. `sort=received` and `receivedFrom`/`receivedTo` work. | R-1 |
| D-2 | `GET /`, `/requests/123` and `/assets/x.js` serve the dashboard with the stated headers and no authentication. `/api/...` never returns HTML. No CORS headers. A missing folder leaves the API working. | R-2 |
| D-3 | A v2 post outlasts Kestrel's defaults and survives a client disconnect. The runbook states the server-side bounds. | R-3 |
| D-4 | Every row of §10 is in the v2 OpenAPI document and pinned by a test. | R-4 |
| D-5 | A v2 save with `pushId` returns it. The same `pushId` with the same body returns the same request. The first 8 characters find it. A different body gets `409`. | R-5 |
| D-6 | A `post: false` row is versioned and diffed as `true → false`. It counts in `controlTotal`, is reported under `skipped`, is never held, and is never sent through v2. All rows off → `400 nothing-to-post`. A v2 save assigns `row-<n>`. | R-6 |
| D-7 | `GET /api/v2/audit-events` and `.csv` follow §8. User events are shown only to `users:manage`. The CSV is formula-safe and capped. `historyCount` matches the feed. The grants test proves nothing is updated or deleted. | R-7 |
| D-8 | v2 list rows carry the R-8 fields. The filters and sorts work and combine with AND. The v2 stats shape is as in §9.3. | R-8 |
| D-9 | `GET /api/v2/users?status=…` filters as in §12, and rows carry `status`. | R-9 |
| D-10 | `dotnet build -warnaserror` and `dotnet test` are green on Windows and Linux; the Data tests skip cleanly without Docker. No new package. The equivalence test passes for v2 (a v2 post without `post: false` rows equals validate-and-post), and v2 adds a `post: false` fixture. | — |
