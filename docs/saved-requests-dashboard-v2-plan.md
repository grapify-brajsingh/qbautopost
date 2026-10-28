# Dashboard API v2 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve everything the dashboard needs (R-1 … R-9) under a new `/api/v2`. The `/api/v1/**` routes and the
legacy flat routes stay exactly as they are.

**Architecture:**
- **Core.** Pure rules go in `QbAutopost.Core`: the `post: false` reader, the engine's `NotPosted` set, v2 hash and
  diff rules, `externalId` assignment, save metadata, and the audit-event mapper.
- **Data.** `QbAutopost.Data` gets one migration (`DashboardV2`) with the new columns, the `audit_events` view and the
  refresh-grace column, plus new store methods mirrored in `FakeSavedRequestStore`.
- **Api.** The API maps a second route group at `/api/v2` that reuses the existing handlers through an
  `ApiFlavor { V1, V2 }` parameter. The `V1` default reproduces today's behaviour line for line.

**Tech Stack:** .NET 8 minimal APIs, EF Core 8 + Npgsql, ASP.NET Core Identity/JwtBearer, xUnit,
WebApplicationFactory, Testcontainers.PostgreSql. No new packages.

**Spec:** `docs/saved-requests-dashboard-v2.md`, approved by the owner on 2026-10-28. Every `§` below refers to it.
`M10 §n` refers to `docs/saved-requests-postgres.md`.
**Branch:** `feature/dashboard-api-v2` · **Milestone:** M11 · **Tasks:** T-1101 … T-1117

---

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` are green on Windows and Linux after **every** task. Data tests skip
  cleanly without Docker.
- **O-1, no v1 change:**
  - No existing test file is edited or deleted, with one exception: `OpenApiDriftTests` may scope its route
    enumeration to non-`/api/v2` endpoints (T-1109), and nothing else in it changes.
  - `src/QbAutopost.Api/wwwroot/openapi.json` stays byte-identical. Its SHA-256 is pinned in T-1101.
  - Every new parameter on a shared service defaults to today's behaviour.
- **New tests go in new files.** Name them with `V2` or the feature, e.g. `DirectPlannerNotPostedTests.cs` and
  `V2PostTests.cs`.
- No test contacts a real QuickBooks or Hermes (`FakeQbGateway`, `FakeHermesClient`, `FakeSavedRequestStore`).
- COM stays in `QbAutopost.QuickBooks`. Core gets no EF, ASP.NET or Npgsql reference.
- Rule 3: never compute, round or adjust an amount or `controlTotal`. A `post: false` row stays in the control-total
  sum.
- Append-only: no new `UPDATE` or `DELETE` on `saved_request_versions`, `saved_request_events` or `user_audit`.
  `post_attempts` changes only through the existing complete and resolve paths. The app role gets `SELECT` on
  `audit_events` and nothing more.
- Every v2 route:
  - is named explicitly in `ApiRoutes.ScopeFor` (the fall-through stays `admin`);
  - is documented in `wwwroot/openapi-v2.json` in the same task that maps it;
  - is covered by the security-trap test.
- No new NuGet package. If a task seems to need one, stop and record a question (CLAUDE.md › Don't).
- Test names are `Should_<behaviour>_When_<condition>`, one behaviour per test. Fixtures live in
  `tests/fixtures/saved-requests-v2/`.
- Each task ends the same way:
  - update its `docs/tracker.md` row (Status, Files, Verified by);
  - add one Session-log line;
  - commit `T-11xx: <summary>` with the `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` trailer.

## Review Focus

1. **A row marked `post: false` that is otherwise invalid for holding** (too old, `account` missing, amount over
   the line limit). Expected: reported `skipped / not-to-post`, never `held`, and the batch still posts. Pinned in
   T-1102 (`Should_NotHoldNotPostedRow_When_RowWouldBeHeldOtherwise`).
2. **The dashboard removes the `post` key instead of sending `true`** when a row is turned back on. Expected: the
   change list shows `replace … false → true`, and the hash equals a payload that never had the key. Pinned in T-1103
   (`Should_ReportPostOn_When_PostKeyRemoved`).
3. **A legacy flat path such as `GET /jobs` or `GET /health` with a dashboard build installed.** Expected: the API
   answer, never `index.html`. Pinned in T-1116 (`Should_NotServeDashboard_When_PathIsLegacyFlatRoute`).
4. **An n8n retry that resends the same body with the same `pushId` but without `Idempotency-Key`.** Expected:
   `200` with the same record and no second row. Pinned in T-1110 (`Should_Replay_When_PushIdAndBodyRepeat`).
5. **An audit value starting with `=` or `-` exported to CSV** (for example a payee typed as `=HYPERLINK(...)`, or
   a memo starting `-`). Expected: the CSV cell starts with `'`. Pinned in T-1114
   (`Should_PrefixQuote_When_ValueStartsWithFormulaChar`).

---

## File structure

```
src/QbAutopost.Core/Api/
  DirectRequest.cs                (modify: DirectRequest.NotPosted, DirectReadResult.NotPosted)
  SkipReasons.cs                  (new)
  DirectRequestReader.cs          (modify: set aside NotPosted rows; nothing-to-post)
  DirectPlanner.cs                (modify: report NotPosted rows as Skipped)
  SyncPostRequest.cs              (modify: ToDirectRequest(IReadOnlySet<int>) overload)
src/QbAutopost.Core/SavedRequests/
  NotPostedRows.cs                (new)
  PayloadRules.cs                 (new: enum V1 | V2)
  PayloadCanonicalizer.cs, PayloadDiff.cs   (modify: optional PayloadRules)
  ExternalIdAssigner.cs           (new)
  SaveMetadata.cs                 (new: record + reader)
  SavedRequestColumns.cs          (modify: SkipCountOf)
  ValidationSnapshot.cs           (modify: ProblemCount)
  SavedRequestModels.cs           (modify: init-only v2 properties, CreateKind.PushIdReused, v2 query/stats records)
  ISavedRequestStore.cs           (modify: v2 methods)
  Audit/AuditEvent.cs, Audit/AuditActions.cs, Audit/AuditEventMapper.cs, Audit/AuditQuery.cs   (new)
src/QbAutopost.Data/
  Entities/SavedRequestEntity.cs, Entities/RefreshTokenEntity.cs   (modify)
  Entities/AuditEventRow.cs       (new, keyless, view)
  Configurations/*                (modify / new AuditEventRowConfiguration)
  Migrations/<ts>_DashboardV2.cs  (new)
  EfSavedRequestStore.cs          (modify) + EfSavedRequestStore.V2.cs (new partial: v2 list/stats/audit)
src/QbAutopost.Api/
  Endpoints/ApiRoutes.cs          (modify: V2Prefix, v2 scopes, dashboard-path test)
  Endpoints/ApiFlavor.cs          (new)
  Endpoints/SavedRequestEndpoints.cs, UserEndpoints.cs, AuthEndpoints.cs, QuickBooksEndpoints.cs, HealthEndpoints.cs,
    ReferenceEndpoints.cs         (modify: flavor parameter, default V1)
  Endpoints/V2/AuditEventEndpoints.cs, Endpoints/V2/AuditCsv.cs   (new)
  SavedRequests/SavedRequestViewsV2.cs, SavedRequests/V2QueryParser.cs   (new)
  SavedRequests/SavedRequestIntake.cs, SavedRequestValidator.cs, SavedRequestPoster.cs   (modify: flavor)
  Jobs/SyncPostRunner.cs          (modify: optional notPosted)
  Auth/RefreshTokenService.cs     (modify: RotateWithGraceAsync)
  Hosting/DashboardHosting.cs, Configuration/DashboardSettings.cs   (new)
  wwwroot/openapi-v2.json         (new)
  Program.cs                      (modify: v2 group, dashboard middleware)
deploy/ install-dashboard.ps1 (new), migrations.sql + grants.sql (regenerated)
docs/  runbook.md, tracker.md, testing/T-1117.server-checklist.md
```

---

### Task 1 (T-1101): Tracker, questions, and the v1 pin

**Files:**
- Modify: `docs/tracker.md`
  - M11 row in Milestones.
  - Section "M11 — Dashboard API v2" with rows T-1101…T-1117, all `todo`.
  - Questions Q-94…Q-98 copied from spec §16.
  - Decisions row: "2026-10-28 owner rulings O-1…O-4 (spec §0)".
- Create: `tests/QbAutopost.Api.Tests/Api/V1ContractPinTests.cs`

- [ ] **Step 1: Write the pin test.**
  `Should_KeepV1OpenApiDocumentByteIdentical_When_V2IsAdded` reads `src/QbAutopost.Api/wwwroot/openapi.json`
  through the same path helper `OpenApiDriftTests` uses, and asserts the lower-hex SHA-256 equals the constant
  `V1OpenApiSha256`.
- [ ] **Step 2: Set the constant from the current file.**
  Run `certutil -hashfile src/QbAutopost.Api/wwwroot/openapi.json SHA256` (or `sha256sum` on Linux), then paste the
  value into the constant.
- [ ] **Step 3: Run the test.**
  `dotnet test --filter FullyQualifiedName~V1ContractPinTests` → PASS.
- [ ] **Step 4: Commit.** `T-1101: M11 tracker, Q-94…Q-98, pin v1 OpenAPI document`

### Task 2 (T-1102): Engine honours `NotPosted` (Core)

**Files:**
- Create:
  - `src/QbAutopost.Core/Api/SkipReasons.cs`
  - `src/QbAutopost.Core/SavedRequests/NotPostedRows.cs`
- Modify:
  - `src/QbAutopost.Core/Api/DirectRequest.cs`
  - `DirectRequestReader.cs`
  - `DirectPlanner.cs` (`PlanRows`)
  - `SyncPostRequest.cs`
- Test (new):
  - `tests/QbAutopost.Core.Tests/Api/DirectRequestReaderNotPostedTests.cs`
  - `DirectPlannerNotPostedTests.cs`
  - `tests/QbAutopost.Core.Tests/SavedRequests/NotPostedRowsTests.cs`

**Interfaces:**
- Produces:
  - `SkipReasons.NotToPost = "not-to-post"`; `SkipReasons.NothingToPost = "nothing-to-post"`.
  - `DirectRequest.NotPosted : IReadOnlySet<int> { get; init; }`, default empty.
  - `DirectReadResult.NotPosted : IReadOnlyList<DirectRowIssue> { get; init; } = []`.
  - `SyncPostRequest.ToDirectRequest(IReadOnlySet<int> notPosted) : DirectRequest`. The existing parameterless
    overload is unchanged.
  - `NotPostedRows.Read(JsonElement payload) : NotPostedRead`, with
    `sealed record NotPostedRead(IReadOnlySet<int> Indexes, IReadOnlyList<string> Errors)`.

- [ ] **Step 1: Write failing reader tests.**
  - `Should_CountNotPostedRowInControlTotal_When_RowIsNotPosted`: rows 100.00 + 50.00, `controlTotal` 150.00,
    `NotPosted = {1}` → `Errors` empty, `Lines.Count == 1`, `NotPosted[0].Index == 1`.
  - `Should_RefuseControlTotalMismatch_When_TotalExcludesNotPostedRows`: same rows, `controlTotal` 100.00 → one error
    starting `control-total-mismatch`.
  - `Should_StillRefuseMalformedRow_When_RowIsNotPosted`: the not-posted row has `Amount = 0` → error
    `row 1: amount must be greater than zero`.
  - `Should_SetAsideBeforeHolds_When_NotPostedRowIsTooOld`: the not-posted row is dated `today - 800` days →
    `Held` empty, `NotPosted` has index 1.
  - `Should_RefuseRequest_When_EveryRowIsNotPosted`: `NotPosted = {0,1}` → one error starting `nothing-to-post:`.
  - `Should_BehaveAsBefore_When_NotPostedIsEmpty`: the existing reader fixtures, read with an empty set, give a
    result equal to today's (`DirectReadResult` compared field by field).
- [ ] **Step 2: Write failing planner tests.**
  - `Should_ReportNotPostedRowAsSkipped_When_RowIsNotPosted`: the row has `Outcome == Skipped`,
    `Reason == "not-to-post"`, `Kind`, `Amount` and `ExternalId` as sent, and `plan.Ok` is true.
  - `Should_NotHoldNotPostedRow_When_RowWouldBeHeldOtherwise`: a row with an unknown account and `NotPosted` →
    Skipped, `Held == 0`.
  - `Should_LeaveNotPostedRowOutOfQbXml_When_Planned`: `plan.QbXml` does not contain that row's amount or memo, and
    `QbXmlCounts` totals 1.
  - `Should_NotFlagDuplicateLine_When_IdenticalTwinIsNotPosted`: two identical rows, one not posted → the other
    is `WouldPost`.
- [ ] **Step 3: Write failing `NotPostedRows` tests.**
  - `Should_ReturnIndexes_When_PostIsFalse` → `{2}`.
  - `Should_TreatMissingOrTrueAsPost_When_Read` → empty.
  - `Should_ReportPathError_When_PostIsNotBoolean` (`"post":"no"` at row 3) → error
    `$.transactions[3].post: must be true or false`.
  - `Should_ReturnEmpty_When_TransactionsIsNotArray` → empty, no error.
- [ ] **Step 4: Run.** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~NotPosted"` → FAIL
  (missing members).
- [ ] **Step 5: Implement.**
  - In `DirectRequestReader.Read`, after `Malformed` and `ControlTotal`:
    - if `NotPosted.Count > 0 && NotPosted.Count >= rows.Count` (every index covered), add
      `"nothing-to-post: every row is marked post: false; nothing would be sent"`;
    - in the line loop, put `NotPosted` indexes into `DirectReadResult.NotPosted` **before** `Hold(row, today)`.
  - In `DirectPlanner.PlanRows`, emit `Outcome = Skipped`, `Reason = SkipReasons.NotToPost` for those indexes.
  - Leave every other line of both files untouched.
- [ ] **Step 6: Run** the filter above plus the whole Core suite → PASS (the existing `DirectPlannerTests` and
  `DirectRequestReaderTests` still pass unedited).
- [ ] **Step 7: Commit.** `T-1102: engine sets aside post:false rows (NotPosted), nothing-to-post`

### Task 3 (T-1103): v2 hash and diff rules (Core)

**Files:**
- Create: `src/QbAutopost.Core/SavedRequests/PayloadRules.cs`
- Modify: `PayloadCanonicalizer.cs`, `PayloadDiff.cs`
- Test (new): `tests/QbAutopost.Core.Tests/SavedRequests/PayloadRulesV2Tests.cs`

**Interfaces:**
- Produces:
  - `public enum PayloadRules { V1, V2 }`
  - `PayloadCanonicalizer.Canonicalize(JsonElement payload, PayloadRules rules = PayloadRules.V1)`
  - `PayloadCanonicalizer.Sha256(JsonElement payload, PayloadRules rules = PayloadRules.V1)`
  - `PayloadDiff.Compute(JsonElement? previous, JsonElement next, PayloadRules rules = PayloadRules.V1)`

- [ ] **Step 1: Write failing tests.**
  - `Should_HashEqual_When_OnlyEmailOrPushIdDiffer` (V2).
  - `Should_HashEqual_When_PostTrueVersusMissing` (V2).
  - `Should_HashEqualToV1_When_PayloadHasNoV2Keys`: a fixture from `tests/fixtures/sync-post/` gives
    `Sha256(p, V2) == Sha256(p)`.
  - `Should_ReportPostOff_When_PostSetFalse` → exactly
    `[replace $.transactions[2].post old=true new=false]`.
  - `Should_ReportPostOn_When_PostKeyRemoved` → `[replace … old=false new=true]`.
  - `Should_IgnoreEmailAndPushId_When_Diffed` → `[]`.
  - `Should_KeepV1Behaviour_When_RulesOmitted` → a `pushId` change is reported under V1 (`replace $.pushId`).
- [ ] **Step 2: Run** with filter `PayloadRulesV2` → FAIL.
- [ ] **Step 3: Implement.**
  - `Normalize` gains the rules. For V2:
    - drop top-level `email` and `pushId`;
    - in each `$.transactions[i]` object, drop `post` when it is `true`.
  - The hash uses that form.
  - The diff uses the same drop for the top-level keys. For rows, it compares a missing `post` as `true` by inserting
    `post: true` into both sides of each row object before diffing.
- [ ] **Step 4: Run** the filter and the existing `PayloadCanonicalizerTests` / `PayloadDiffTests` → PASS.
- [ ] **Step 5: Commit.** `T-1103: PayloadRules.V2 for hash and change list`

### Task 4 (T-1104): Save metadata, `externalId` assignment, skip and problem counts (Core)

**Files:**
- Create:
  - `src/QbAutopost.Core/SavedRequests/SaveMetadata.cs`
  - `ExternalIdAssigner.cs`
- Modify:
  - `SavedRequestColumns.cs`
  - `ValidationSnapshot.cs`
- Test (new):
  - `SaveMetadataTests.cs`
  - `ExternalIdAssignerTests.cs`
  - `SkipAndProblemCountTests.cs`

**Interfaces:**
- Produces:
  - `sealed record SaveMetadata(string? EmailSubject, DateTime? EmailReceivedUtc, string? EmailFrom,
    string? EmailMessageId, Guid? PushId, IReadOnlyList<string> MetadataErrors)`, with `static SaveMetadata None`
    and `bool HasEmail`.
  - `SaveMetadataReader.Read(JsonElement originalPayload) : SaveMetadata`. `SaveMetadataReader.MaxTextLength = 998`.
  - `ExternalIdAssigner.AssignOnSave(JsonElement payload) : JsonElement`
  - `ExternalIdAssigner.AssignOnEdit(JsonElement payload, int newVersion) : JsonElement`
  - `SavedRequestColumns.SkipCountOf(JsonElement payload) : int`
  - `ValidationSnapshot.ProblemCount : int` (`Validation.Count + Errors.Count`)

- [ ] **Step 1: Write failing tests.**
  - **Metadata:**
    - `Should_ReadAllFields_When_EmailIsComplete`
    - `Should_TruncateSubject_When_Over998Chars`, including a case where character 998 is a surrogate pair, so the
      result has 997 characters.
    - `Should_LeaveReceivedNull_When_NoOffset` (`"2026-10-27T07:12:44"`)
    - `Should_IgnoreEmail_When_NotObject`
    - `Should_ParsePushId_When_UuidWithoutDashes`
    - `Should_RecordError_When_PushIdNotUuid` → `MetadataErrors == ["$.pushId: not a UUID"]`, `PushId == null`.
    - `Should_RecordError_When_PushIdIsNumber`
  - **Assignment:**
    - `Should_AssignRowN_When_ExternalIdMissing` → `row-1`, `row-3`, with row 2's own id kept.
    - `Should_SuffixId_When_RowNAlreadyUsed` (row 2 has the id `row-1` → row 1 becomes `row-1-2`).
    - `Should_AssignEditIds_When_EditRowLacksId` → `edit4-2`.
    - `Should_LeaveDuplicateCallerIds_When_Assigning`
    - `Should_ReturnUnchanged_When_TransactionsNotArray`
  - **Counts:**
    - `Should_CountOnlyLiteralFalse_When_SkipCounted` (`false` → counted; `"false"` and `0` → not).
    - `Should_SumFindingsAndErrors_When_ProblemCounted`
- [ ] **Step 2: Run** the filters → FAIL.
- [ ] **Step 3: Implement.**
  - `ExternalIdAssigner` works on a `JsonNode` copy and returns `JsonElement` via `JsonSerializer.SerializeToElement`.
  - It treats an `externalId` that is missing, `null` or `""` as absent.
  - Its uniqueness set is seeded with every existing id in the payload.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit.** `T-1104: save metadata reader, externalId assignment, skip/problem counts`

### Task 5 (T-1105): Migration `DashboardV2` and the store's v2 columns

**Files:**
- Modify:
  - `src/QbAutopost.Data/Entities/SavedRequestEntity.cs`
  - `Entities/RefreshTokenEntity.cs`
  - `Configurations/SavedRequestConfiguration.cs`, `RefreshTokenConfiguration.cs`
  - `EfSavedRequestStore.cs` (create, append version, set validation, mapping)
  - `src/QbAutopost.Core/SavedRequests/SavedRequestModels.cs`
  - `ISavedRequestStore.cs` (`SetValidationAsync` gains `snapshotV2`)
  - `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.cs`
  - `src/QbAutopost.Api/Database/DisabledSavedRequestStore.cs`
  - `deploy/migrations.sql` (regenerated)
  - `deploy/grants.sql`
- Create: `src/QbAutopost.Data/Migrations/<ts>_DashboardV2.cs` (`dotnet ef migrations add DashboardV2`)
- Test (new): `tests/QbAutopost.Data.Tests/DashboardV2StoreContract.cs`, a contract suite run by both
  `EfSavedRequestStoreTests` and `FakeSavedRequestStoreTests` through new derived classes
  `EfDashboardV2StoreTests` and `FakeDashboardV2StoreTests` (new files).

**Interfaces:**
- Consumes: T-1104's `SaveMetadata`, `ProblemCount` and `SkipCountOf`.
- Produces:
  - **Init-only properties, defaults keep v1 callers compiling unchanged:**
    - `NewSavedRequest`: `{ SaveMetadata Metadata = SaveMetadata.None; ValidationSnapshot? SnapshotV2 = null }`.
    - `NewVersion`: `{ ValidationSnapshot? SnapshotV2 = null }`.
    - `SavedRequest`: `{ SaveMetadata Metadata; int SkipCount; ValidationSnapshot? ValidationV2;
      bool NeedsAttentionV2; int ProblemCount; PostSummary? PostSummary; int HistoryCount }`.
  - `sealed record PostSummary(string? PostId, int? PostedCount, DateTime? PostedUtc, string? PostedBy)`.
  - `SetValidationAsync(long id, ValidationSnapshot snapshot, ValidationSnapshot snapshotV2, DuplicateMatch duplicate,
    string actor, DateTime nowUtc, CancellationToken ct)`. Every existing caller is updated in T-1112.
  - **Store rules:**
    - `needs_attention_v2` = `SnapshotV2.NeedsAttention || Metadata.MetadataErrors.Count > 0`.
    - When `SnapshotV2` is null, the store copies the v1 snapshot into it.
    - `skip_count` is recomputed from the stored payload on create and append.

- [ ] **Step 1: Write failing contract tests.**
  - `Should_PersistMetadata_When_Created`
  - `Should_NeverChangeMetadata_When_VersionAppended`
  - `Should_StoreBothSnapshots_When_Created`
  - `Should_CopyV1Snapshot_When_SnapshotV2Null`
  - `Should_SetNeedsAttentionV2_When_MetadataErrors`
  - `Should_RefreshSkipCount_When_VersionAppended`
  - `Should_RefreshProblemCount_When_ValidationSet`
- [ ] **Step 2: Run.** `dotnet test tests/QbAutopost.Data.Tests --filter DashboardV2` → FAIL.
- [ ] **Step 3: Add the entity columns.**
  The columns and types are spec §4.1. `push_id_hex` is
  `HasComputedColumnSql("replace(push_id::text, '-', '')", stored: true)`. Indexes as spec §4.2, plus
  `refresh_tokens.grace_successor_id bigint null`.
  The view is added in T-1106, not here.
- [ ] **Step 4: Generate the migration.**
  ```
  dotnet ef migrations add DashboardV2 -p src/QbAutopost.Data -s src/QbAutopost.Data
  ```
  In `Up`, append the raw-SQL backfill:
  ```sql
  UPDATE saved_requests SET validation_v2 = validation, needs_attention_v2 = needs_attention,
    problem_count = coalesce(jsonb_array_length(validation->'validation'),0) + coalesce(jsonb_array_length(validation->'errors'),0);
  ```
- [ ] **Step 5: Implement the store and fake changes.**
- [ ] **Step 6: Regenerate and update the deploy scripts.**
  - Regenerate `deploy/migrations.sql` (`dotnet ef migrations script --idempotent`).
  - No new grants yet; the new columns live on tables the app role already updates.
- [ ] **Step 7: Run the Data suite and the whole solution.** → PASS, including `MigrationTests` (no pending model
  changes) and `MigrationScriptTests`.
- [ ] **Step 8: Commit.** `T-1105: DashboardV2 migration, v2 columns, both snapshots in the store`

### Task 6 (T-1106): Audit feed: Core mapper, `audit_events` view, store query

**Files:**
- Create:
  - `src/QbAutopost.Core/SavedRequests/Audit/{AuditEvent,AuditActions,AuditQuery,AuditEventMapper,UserAuditEntry}.cs`
  - `src/QbAutopost.Data/Entities/AuditEventRow.cs`
  - `Configurations/AuditEventRowConfiguration.cs` (`ToView("audit_events")`, keyless)
  - `Migrations/<ts>_AuditEventsView.cs` (raw-SQL `CREATE VIEW`)
  - `src/QbAutopost.Data/EfSavedRequestStore.Audit.cs` (partial)
- Modify:
  - `ISavedRequestStore.cs`
  - `FakeSavedRequestStore.cs` (adds `AddUserAudit(UserAuditEntry)`)
  - `DisabledSavedRequestStore.cs`
  - `deploy/grants.sql` (`GRANT SELECT ON audit_events TO qbautopost_app`)
  - `deploy/migrations.sql`
- Test (new):
  - `tests/QbAutopost.Core.Tests/SavedRequests/AuditEventMapperTests.cs`
  - `tests/QbAutopost.Data.Tests/AuditEventsStoreContract.cs`, run by the Ef and Fake derived classes

**Interfaces:**
- Produces:
  - `sealed record AuditEvent(string Id, DateTime AtUtc, string ActorId, string ActorKind, string Action,
    long? RequestId, int? RowIndex, string? RowExternalId, string? Field, JsonNode? Old, JsonNode? New,
    int? VersionNumber, JsonNode? Detail)`. Request reference fields are joined by the API view.
  - `AuditActions`: string constants for every action in spec §8.3, including `FlaggedDuplicate`, `RequestEdited`,
    `UserSessionRevoked` and `UserOther`.
  - `AuditActorKinds`: `User`, `ApiClient`, `System`, `Anonymous`.
  - `sealed record AuditQuery(DateTime? From, DateTime? To, string? Actor, long? RequestId, string? Reference,
    Guid? PushId, IReadOnlyList<string> Actions, string? Q, bool IncludeUserEvents, int Page, int PageSize)`.
  - `AuditEventMapper.FromEvent(SavedRequestEvent e) : AuditEvent?` (null for `edited`).
  - `AuditEventMapper.FromVersion(SavedRequestVersion v, SavedRequestVersion previous, string? editNote) :
    IReadOnlyList<AuditEvent>`.
  - `AuditEventMapper.FromUserAudit(UserAuditEntry u) : AuditEvent`.
  - `AuditEventMapper.ActorKindOf(string actor) : string`, which gives `system`, `anonymous`, `api-client` for the
    `api-client:` prefix, and `user` otherwise.
  - Store: `Task<PagedResult<AuditEvent>> AuditEventsAsync(AuditQuery query, CancellationToken ct)`;
    `Task<int> AuditEventCountAsync(AuditQuery query, CancellationToken ct)`.

- [ ] **Step 1: Write failing mapper tests**, one per row of spec §8.3. For example:
  - `Should_MapRowPostOff_When_ChangeSetsPostFalse` → action `row-post-off`, `Field == "post"`, `RowIndex == 2`.
  - `Should_UseNewVersionRowId_When_ReplaceOrAdd`
  - `Should_UsePreviousVersionRowId_When_Remove`
  - `Should_MapRequestEdited_When_PathIsReference`
  - `Should_KebabCaseOutcome_When_PostFinishedDetailIsPascal` (`"NotPosted"` → `"not-posted"`)
  - `Should_SkipEditedEvent_When_Mapped`
  - `Should_CopyEditNote_When_ChangeMapped`
  - `Should_MapUserOther_When_KindUnknown`
  - `Should_MapRefreshReuse_When_KindRefreshReuse` → `user-session-revoked`
  - Ids: `e<id>`, `v<versionId>-<opIndex>` (0-based), `u<id>`.
- [ ] **Step 2: Write failing store contract tests.**
  - `Should_OrderNewestFirst_When_Listed`, with ties broken by `id` descending as a string.
  - `Should_ExcludeUserEvents_When_NotIncluded` (including from `Total`)
  - `Should_FilterByActorAndAction_When_Given`
  - `Should_MatchQInOldNew_When_Searched`
  - `Should_KeepIdsStable_When_ReadTwice`
  - `Should_MatchMapperOutput_When_ViewRead`: the Ef store result equals the Core mapper applied to the same seeded
    rows. This is the drift guard between SQL and C#.
- [ ] **Step 3: Run** → FAIL.
- [ ] **Step 4: Implement the mapper, then the view.**
  - The view SQL uses `jsonb_array_elements(changes) WITH ORDINALITY` and regex path classification, with the same
    order of rules as the mapper. `old` and `new` are jsonb. The previous version is joined on `number - 1` for remove
    ops.
  - Add the view migration, update the grants, and regenerate the scripts.
  - `MigrationScriptTests`: covering the view needs no edit if that test enumerates relations from the script. If it
    doesn't, add a **new** test `Should_GrantSelectOnAuditView_When_ScriptsGenerated` in
    `AuditViewGrantTests.cs`.
- [ ] **Step 5: Run** the Core, Data and full suites → PASS. Also run the app-role leg:
  `QBAUTOPOST_TEST_AS_APP_ROLE=1 dotnet test tests/QbAutopost.Data.Tests` → PASS.
- [ ] **Step 6: Commit.** `T-1106: audit_events view, AuditEventMapper, store audit queries`

### Task 7 (T-1107): Store v2 list, stats, post summary, other pushes, history count

**Files:**
- Modify:
  - `SavedRequestModels.cs`
  - `ISavedRequestStore.cs`
  - `EfSavedRequestStore.V2.cs` (new partial)
  - `FakeSavedRequestStore.cs`
  - `DisabledSavedRequestStore.cs`
- Test (new): `tests/QbAutopost.Data.Tests/ListV2StoreContract.cs` (+ Ef/Fake derived)

**Interfaces:**
- Produces:
  - `sealed record SavedRequestQueryV2(SavedRequestQuery Base, DateOnly? ReceivedFrom, DateOnly? ReceivedTo,
    string? EmailFrom, string? Reference, decimal? MinTotal, decimal? MaxTotal, bool? HasErrors)`.
    - `Base.Sort` also accepts `received`, `-received`, `total`, `-total`, `rows`, `-rows`.
    - `Base.NeedsAttention` is evaluated against `needs_attention_v2`.
  - `sealed record FlagCounts(int Pending, int All)`.
  - `sealed record SavedRequestStatsV2(int Total, IReadOnlyDictionary<SavedRequestStatus,int> ByStatus,
    FlagCounts NeedsAttention, FlagCounts Duplicate, FlagCounts HasErrors)`.
  - `SavedRequestSummary` gains init-only properties `Metadata`, `SkipCount`, `ValidationV2State`,
    `ValidationV2Utc`, `ValidationV2Version`, `NeedsAttentionV2`, `ProblemCount`, `PostSummary` and `HistoryCount`.
  - `sealed record OtherPush(long Id, Guid? PushId, DateTime ReceivedUtc, SavedRequestStatus Status)`.
  - Store methods:
    - `Task<PagedResult<SavedRequestSummary>> ListV2Async(SavedRequestQueryV2 query, CancellationToken ct)`
    - `Task<SavedRequestStatsV2> StatsV2Async(CancellationToken ct)`
    - `Task<IReadOnlyList<OtherPush>> OtherUnpostedPushesAsync(long id, CancellationToken ct)`
    - `GetAsync` now also fills `PostSummary` and `HistoryCount` (init-only, so v1 views ignore them).

- [ ] **Step 1: Write failing contract tests**, one per rule in spec §5.2 search, §9.1, §9.2 and §9.3:
  - `Should_MatchSubject_When_QGiven`
  - `Should_MatchPushIdPrefix_When_QIsHexWithOrWithoutDashes`
  - `Should_SortByReceivedFallingBackToCreated_When_SortReceived`
  - `Should_FilterReceivedRangeInclusive_When_Given`
  - `Should_ExcludeNullTotal_When_MinTotalGiven`
  - `Should_UseNeedsAttentionV2_When_FilteringAndCounting`
  - `Should_CombineStatusAndFlags_When_BothGiven`
  - `Should_CountPendingAndAll_When_StatsV2`
  - `Should_FillPostSummaryFromPostedAttempt_When_Posted`
  - `Should_FillPostSummaryFromResolution_When_ResolvedPosted` (`PostedCount == null`)
  - `Should_ListOtherUnpostedPushes_When_SameReference` (excludes self, `posted` and `discarded`, and an empty
    reference)
  - `Should_CountHistory_When_EventsAndChanges` (equals `AuditEventCountAsync` for that request)
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**, with parameterised SQL only (`EF.Functions.ILike` with the
  existing escape helper). **Step 4: Run** → PASS.
- [ ] **Step 5: Measure.** Add the Data test `Should_ListPageUnder200ms_When_1000Requests` (Postgres only, marked
  `[Trait("Category","Perf")]`). If it fails, add the stored count described in spec §8.4 within this task.
- [ ] **Step 6: Commit.** `T-1107: v2 list, filters, sorts, stats, post summary, other pushes`

### Task 8 (T-1108): `pushId` replay and conflict in the store

**Files:**
- Modify: `SavedRequestModels.cs` (`CreateKind.PushIdReused`), `EfSavedRequestStore.cs` (`CreateAsync`),
  `FakeSavedRequestStore.cs`
- Test (new): `tests/QbAutopost.Data.Tests/PushIdStoreContract.cs` (+ derived)

- [ ] **Step 1: Write failing tests.**
  - `Should_ReplayExisting_When_SamePushIdSameBody` → `Replayed`, with one row in the database.
  - `Should_ReportPushIdReused_When_SamePushIdDifferentBody`
  - `Should_LetKeyDecideAlone_When_IdempotencyKeyMatches`: same key and body → `Replayed`; same key, different body
    → `KeyReused`, even when the `pushId` is new.
  - `Should_StoreOneRow_When_TwoConcurrentSavesShareAPushId` (Postgres, `Task.WhenAll`; the unique-violation path
    re-reads the winner).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**, in the same transaction and after the key lookup, mirroring the
  key path's unique-violation handling. **Step 4: Run** → PASS.
- [ ] **Step 5: Commit.** `T-1108: pushId replay, push-id-reused, race`

### Task 9 (T-1109): `/api/v2` routing frame, scopes, OpenAPI v2, shared routes

**Files:**
- Create:
  - `src/QbAutopost.Api/Endpoints/ApiFlavor.cs` (`public enum ApiFlavor { V1, V2 }`)
  - `src/QbAutopost.Api/wwwroot/openapi-v2.json`
- Modify:
  - `ApiRoutes.cs`: `V2Prefix = "/api/v2"`; `ScopeFor` strips `V2Prefix` **only** for paths in the explicit v2
    table; `IsHealth` unchanged.
  - `Program.cs`: `var v2 = app.MapGroup(ApiRoutes.V2Prefix)`, mapping the auth, user, saved-request, quickbooks
    lists, health/database and reference endpoints with `ApiFlavor.V2`.
  - Every `Map*Endpoints` method gains `ApiFlavor flavor = ApiFlavor.V1`.
  - `ReferenceEndpoints.cs`: serves `openapi-v2.json` at `/api/v2/openapi.json` under the same switch.
  - `CallerMiddleware.cs` / `DatabaseGate`: wherever they test the `/api/v1` prefix, add `/api/v2` alongside it.
  - `OpenApiDriftTests.cs`: the single permitted edit, which scopes `EndpointDataSource` enumeration to endpoints
    whose route does not start with `/api/v2`.
- Test (new):
  - `tests/QbAutopost.Api.Tests/Api/OpenApiV2DriftTests.cs`: the same checks against `openapi-v2.json` for
    `/api/v2` endpoints only.
  - `tests/QbAutopost.Api.Tests/Routing/V2RoutingTests.cs`
  - `tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs`
  - `tests/QbAutopost.Api.Tests/SavedRequests/V1UnchangedTests.cs`

**Interfaces:**
- Produces:
  - `ApiFlavor`.
  - `ApiRoutes.V2Prefix`.
  - `SavedRequestEndpoints.Location(ApiFlavor, long id) : string`, which gives a v2 `Location` for v2 saves.
- Routes in this task:
  - **Mapped identically:** `original`, `versions`, `events`, `attempts`, `quickbooks/lists`, `health/database`,
    `/users/{id}…`, `/users` (POST) and `auth/me`, `logout`, `change-password`.
  - **Flavor-aware routes are mapped here** with `V1` behaviour for now, and **each later task switches its route to
    V2 behaviour**.

- [ ] **Step 1: Write failing tests.**
  - `Should_ServeSameBody_When_V1AndV2OriginalRead`
  - `Should_RequireSameScope_When_V2RouteCalled` (a theory over every v2 route × every role and API-key case,
    reusing the `CallerMatrix` data)
  - `Should_Answer403_When_UnknownV2PathNonAdmin`, `Should_Answer404Json_When_UnknownV2PathAdmin`,
    `Should_Answer401_When_UnknownV2PathAnonymous`
  - `Should_Answer503_When_DatabaseDownOnV2`
  - `Should_DocumentEveryV2Route_When_DriftChecked`
  - `Should_IgnorePostFlag_When_PostedThroughV1`: a payload with `post: false` posted through
    `/api/v1/saved-requests/{id}/post` sends that row to `FakeQbGateway`, which shows v1 is unchanged.
  - `Should_KeepV1RefreshCookiePath_When_V2Mapped`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run the full suite.** Every existing test passes
  unedited (apart from the one `OpenApiDriftTests` scoping line), and `V1ContractPinTests` passes.
- [ ] **Step 5: Commit.** `T-1109: /api/v2 group, explicit scopes, openapi-v2.json, shared routes`

### Task 10 (T-1110): v2 save

**Files:**
- Modify:
  - `SavedRequestIntake.cs`: `SaveAsync(string rawBody, string? idempotencyKey, Caller caller, CancellationToken
    ct, ApiFlavor flavor = ApiFlavor.V1)`.
  - `SavedRequestValidator.cs`: `ValidateBoth(PayloadRead read, NotPostedRead notPosted, int versionNumber,
    DateTime nowUtc, long? id = null) : (ValidationSnapshot V1, ValidationSnapshot V2)`.
  - The `SavedRequestEndpoints.SaveAsync` handler: v2 answers.
  - `openapi-v2.json`.
- Test (new): `tests/QbAutopost.Api.Tests/SavedRequests/V2SaveTests.cs`; fixtures
  `tests/fixtures/saved-requests-v2/{with-email,bad-pushid,mixed-externalids,post-false}.json`

**Interfaces:**
- Consumes: T-1102 `NotPostedRows`, T-1103 `PayloadRules.V2`, T-1104 `SaveMetadataReader` and `ExternalIdAssigner`,
  T-1105 `NewSavedRequest.Metadata`/`SnapshotV2`, T-1108 `CreateKind.PushIdReused`.
- **v2 flow:**
  1. `payload' = AssignOnSave(payload)`.
  2. `metadata = SaveMetadataReader.Read(payload)` (the original, not the assigned payload).
  3. `read = SavedPayloadReader.Read(payload')`.
  4. `ValidateBoth`.
  5. `Sha256(payload', V2)`.
  6. Fingerprints and duplicates as today.
- **Every save, v1 and v2,** stores `SnapshotV2` from `ValidateBoth`. v1 responses don't show it.

- [ ] **Step 1: Write failing tests.**
  - `Should_ReturnEmail_When_SavedWithEmail`
  - `Should_SaveWithoutFlag_When_EmailMalformed`
  - `Should_ReturnPushId_When_Valid`
  - `Should_Replay_When_PushIdAndBodyRepeat` (200, `Idempotent-Replayed: true`)
  - `Should_Answer409PushIdReused_When_BodyDiffers` (problem `type` `push-id-reused`, with `savedRequestId` and
    `publicRef`)
  - `Should_FlagButStayPostable_When_PushIdMalformed`
  - `Should_AssignRowIds_When_ExternalIdsMissing` (version 1 has `row-1`…; `/original` holds the exact bytes)
  - `Should_DiffAddRemoveByExternalId_When_N8nRowsLackedIds`
  - `Should_SetV2LocationHeader_When_Created`
  - `Should_NotFillMetadata_When_SavedThroughV1` (v1 save of the `with-email` fixture → the v2 detail shows
    `email: null`)
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run the full suite** → PASS.
- [ ] **Step 5: Commit.** `T-1110: v2 save — email, pushId, row ids, v2 hash, both snapshots`

### Task 11 (T-1111): v2 detail, list and stats views

**Files:**
- Create:
  - `src/QbAutopost.Api/SavedRequests/SavedRequestViewsV2.cs`
  - `src/QbAutopost.Api/SavedRequests/V2QueryParser.cs`
- Modify: the `SavedRequestEndpoints` `ListAsync`, `GetAsync` and `StatsAsync` handlers (branch on flavor);
  `openapi-v2.json`
- Test (new): `tests/QbAutopost.Api.Tests/SavedRequests/V2ReadTests.cs`

**Interfaces:**
- Produces:
  - `V2QueryParser.TryParse(IQueryCollection q, int maxPageSize, out SavedRequestQueryV2? query, out string? error)`.
  - **View records:**
    - `SavedRequestSummaryV2View`: the v1 summary fields, plus `pushId`, `email`, `historyCount`,
      `validationSummary`, `postSummary` and `skipCount`. `needsAttention` is v2.
    - `SavedRequestDetailV2View`: the v1 detail fields, with `needsAttention` v2 and `readErrors` merged per spec
      §6.4, plus the summary additions and `otherUnpostedPushes` and `ledgerBatch`.
    - `StatsV2View`.
  - **`ledgerBatch`** is read from `ledger.json` through the reader `AttemptAsync` already uses
    (`SavedRequestViews.cs:156-168`). It is filled only for `partial`/`unknown` with a `postId` that exists.
    `createdUtc` = the batch's start time.

- [ ] **Step 1: Write failing tests.**
  - `Should_ReturnEtag_When_DetailRead`
  - `Should_ShowValidationSummaryNone_When_NeverValidated`, `Should_ShowProblemCount_When_Held`
  - `Should_ShowSkipCount_When_PostFalseRows`
  - `Should_ShowOtherUnpostedPushes_When_SameReference`
  - `Should_ShowLedgerBatch_When_Unknown`, `Should_OmitLedgerBatch_When_Pending`
  - `Should_Answer400_When_MinTotalAboveMaxTotal`, `Should_Answer400_When_UnknownSort`
  - `Should_CountPendingOnlyFlags_When_StatsRead`
  - `Should_ListMetadataErrorFirst_When_PushIdMalformed`
  - `Should_ListTxnIdPerRow_When_AttemptPosted`: v2 `GET …/attempts/{attemptId}` has `result.posted[]` with
    `index`, `externalId` and `txnId`, and `ledgerBatch.posted[]` also carries `externalId`, joined by `index` (R-4.8,
    R-9.3). The v2 attempt handler gets a flavor branch; v1's `AttemptDetailView` is untouched.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit.** `T-1111: v2 detail, list, stats and attempt detail`

### Task 12 (T-1112): v2 edit, validate, discard, restore, resolve

**Files:**
- Modify:
  - `SavedRequestIntake.EditAsync(..., ApiFlavor flavor = ApiFlavor.V1)` and `RevalidateAsync(..., ApiFlavor flavor
    = ApiFlavor.V1)`. Both flavors now compute both snapshots and call the new `SetValidationAsync`.
  - `SavedRequestEndpoints`: the mutation answer and the 412 builder (`MutationAnswerAsync`, `:652-663`) take the
    flavor, and v2 adds `updatedUtc`.
  - `openapi-v2.json`.
- Test (new): `tests/QbAutopost.Api.Tests/SavedRequests/V2EditTests.cs`

**v2 edit:**
- `AssignOnEdit(payload, current.CurrentVersion + 1)`.
- Diff with `PayloadRules.V2`; hash with `PayloadRules.V2`.
- Both snapshots.

- [ ] **Step 1: Write failing tests.**
  - `Should_RecordPostOffChange_When_RowTurnedOff` (`replace $.transactions[2].post true→false`)
  - `Should_Return200Unchanged_When_OnlyEmailChanged`
  - `Should_KeepEmailMetadata_When_EditDropsEmail`
  - `Should_AssignEditId_When_NewRowLacksId`
  - `Should_IncludeUpdatedUtc_When_Stale`, on each of edit, validate, discard, restore and resolve
  - `Should_SnapshotHonoursPostFalse_When_Revalidated` (v2 `validationSummary.state` is `ok` while v1 `validation`
    for the same request is `held`, because the off row would be held)
  - `Should_SetNeedsAttentionV2_When_PostValueInvalid` (`"post":"no"`)
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run the full suite.** The existing
  `EditApiTests`/`LifecycleApiTests` still pass unedited. **Step 5: Commit.**
  `T-1112: v2 edit/validate/discard/restore/resolve with v2 rules and 412 extras`

### Task 13 (T-1113): v2 post by id

**Files:**
- Modify:
  - `SyncPostRunner.RunAsync(SyncPostRequest request, CancellationToken ct, Action? onSending = null,
    IReadOnlySet<int>? notPosted = null)`. It calls `request.ToDirectRequest(notPosted ?? empty)`.
  - `SavedRequestPoster.PostAsync(..., ApiFlavor flavor = ApiFlavor.V1)`.
    - **V2, before the claim:** read `NotPostedRows`. If it has errors, take today's unbindable path (claim, record a
      `refused` attempt with 400, return to pending; spec §6.2 item 8). If every row is off → new
      `SavedPostKind.NothingToPost`, with no claim.
  - The post handler: `400 nothing-to-post` (problem `type` `nothing-to-post`). The v2 409s add `attemptId`,
    `startedBy {id,name}` and `startedUtc`.
  - `openapi-v2.json`.
- Test (new):
  - `tests/QbAutopost.Api.Tests/SavedRequests/V2PostTests.cs`
  - `V2EquivalenceTests.cs`

- [ ] **Step 1: Write failing tests.**
  - `Should_ListNotPostedRowUnderSkipped_When_PostedById` (`skipped[0].reason == "not-to-post"`,
    `counts.skipped == 1`, status `posted`)
  - `Should_NotSendNotPostedRowToGateway_When_PostedById` (asserts the `FakeQbGateway` qbXML bodies)
  - `Should_KeepControlTotal_When_RowsSkipped` (the stored payload's `controlTotal` is unchanged after the post)
  - `Should_Answer400NothingToPost_When_EveryRowIsPostFalse` (no attempt row)
  - `Should_RecordRefusedAttempt_When_PostValueInvalid`
  - `Should_NameWhoStarted_When_AlreadyPosting`, `Should_NameAttempt_When_AlreadyPosted`
  - `Should_EndPartial_When_GatewayRejectsSomeRows` (3 rows, the gateway rejects one → request `partial`,
    `postSummary.postedCount == 2`)
  - `Should_FinishAndRecord_When_ClientDisconnects` (v2 twin, same `AbortProbe`/`WriteGate`)
  - `Should_NotTimeOut_When_PostOutlastsKestrelDefaults` (hold `WriteGate` past 130 s of wall time, using a
    `[Trait("Category","Slow")]` test with the real Kestrel test server; then 200 and a recorded attempt)
  - `V2EquivalenceTests.Should_MatchValidateAndPost_When_NoRowIsOff`: a theory over every fixture in
    `tests/fixtures/sync-post/`, comparing the status, the body without the added fields, and the gateway calls with
    validate-and-post on the version-1 payload.
  - `Should_PostOnlyOnRows_When_PostFalseFixture`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run the full suite**, with `PostApiTests` and
  `EquivalenceTests` unedited and green.
- [ ] **Step 5: Commit.** `T-1113: v2 post honours post:false, nothing-to-post, 409 extras`

### Task 14 (T-1114): `GET /api/v2/audit-events` and `.csv`

**Files:**
- Create:
  - `src/QbAutopost.Api/Endpoints/V2/AuditEventEndpoints.cs`
  - `AuditCsv.cs`
- Modify:
  - `ApiRoutes.ScopeFor` (both routes → `requests:read`)
  - `Program.cs` (map under v2)
  - `openapi-v2.json`, with the `AuditAction` and `AuditActorKind` enums
- Test (new): `tests/QbAutopost.Api.Tests/SavedRequests/V2AuditEventsTests.cs`

**Interfaces:**
- Consumes: T-1106 `AuditEventsAsync`, `AuditEventCountAsync`, `AuditQuery`.
- Produces:
  - `AuditCsv.Escape(string? value) : string`
  - `AuditCsv.WriteAsync(Stream body, IAsyncEnumerable<AuditEvent> rows, CancellationToken ct)`
  - `AuditCsv.MaxRows = 50_000`
- **Behaviour:**
  - `IncludeUserEvents = caller.Allows(users:manage)`.
  - `actor.name` is resolved through `ActorDirectory` (existing).
  - `request{…}` is joined from the store.

- [ ] **Step 1: Write failing tests.**
  - `Should_ReturnFeedShape_When_Read` (every field in spec §8.2)
  - `Should_HideUserEvents_When_CallerLacksUsersManage` (the total excludes them)
  - `Should_Answer400_When_FromHasNoOffset`
  - `Should_Answer413_When_CsvOverCap` (fake count 50 001)
  - `Should_PrefixQuote_When_ValueStartsWithFormulaChar` (`=`, `+`, `-`, `@`, `\t`, `\r`)
  - `Should_WriteBomAndHeader_When_CsvRequested`
  - `Should_FilterByActor_When_UserPageAsks`
  - `Should_MatchHistoryCount_When_FeedFilteredByRequest`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. **Step 5: Commit.**
  `T-1114: audit-events feed and CSV`

### Task 15 (T-1115): v2 users status, v2 auth cookie, refresh grace

**Files:**
- Modify:
  - `UserEndpoints.ListAsync`: under V2, a `status` filter and a `status` field on `UserViewV2`.
  - `AuthEndpoints`: `CookiePathFor(ApiFlavor)` → `/api/v1/auth` | `/api/v2/auth`. The v1 constant stays.
  - `RefreshTokenService`: new `RotateWithGraceAsync(string rawToken, string? ip, TimeSpan grace, CancellationToken
    ct) : Task<RefreshResult>`. `RotateAsync` is unchanged.
  - `UserAuditWriter`: kind `refresh-reuse`.
  - `AppSettings`: `Auth:Jwt:RefreshReuseGraceSeconds` (default 30).
  - `openapi-v2.json`, with the `UserStatus` enum.
- Test (new):
  - `tests/QbAutopost.Api.Tests/Auth/V2RefreshGraceTests.cs`
  - `tests/QbAutopost.Api.Tests/Api/V2UsersTests.cs`
  - `tests/QbAutopost.Data.Tests/Api/RefreshGraceStoreTests.cs` (Postgres)

- [ ] **Step 1: Write failing tests.**
  - **Grace:**
    - `Should_IssueSecondSuccessor_When_JustRotatedTokenReusedWithinGrace`, with both successors able to refresh
      afterwards.
    - `Should_RevokeFamily_When_ReusedAfterGrace`
    - `Should_RevokeFamily_When_GraceAlreadyUsed`
    - `Should_RevokeFamily_When_AddressDiffers`
    - `Should_RevokeFamily_When_SuccessorAlreadyRotated`
    - `Should_WriteRefreshReuseAudit_When_FamilyRevokedOnV2`
    - `Should_KeepNoGrace_When_V1Refresh`
  - **Cookie:** `Should_SetCookiePathV2_When_V2Login`
  - **Users:**
    - `Should_FilterByStatus_When_Given` (one user per status)
    - `Should_OrStatuses_When_Repeated`
    - `Should_Answer400_When_StatusUnknown`
    - `Should_ReturnStatus_When_Listed`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** the grace check (spec §11 a–e) with a conditional
  `UPDATE refresh_tokens SET grace_successor_id = @new WHERE id = @old AND grace_successor_id IS NULL`, so two racing
  grace refreshes give exactly one second successor. **Step 4: Run** → PASS. **Step 5: Commit.**
  `T-1115: v2 users status, v2 cookie path, refresh reuse grace`

### Task 16 (T-1116): Serve the dashboard (R-2)

**Files:**
- Create:
  - `src/QbAutopost.Api/Configuration/DashboardSettings.cs` (`Path` default `C:\qb-autopost\dashboard`, `Enabled`
    default `true`)
  - `src/QbAutopost.Api/Hosting/DashboardHosting.cs`
- Modify:
  - `ApiRoutes.cs`: `LegacyFlatPrefixes = ["/health", "/jobs", "/batches", "/quickbooks", "/qb", "/rules"]` and
    `IsDashboardPath(PathString)`.
  - `Program.cs`: `app.UseDashboard()`, before `CallerMiddleware`.
  - `appsettings.json`: the `Dashboard` section. This is a new key, so no existing key changes.
- Test (new): `tests/QbAutopost.Api.Tests/Hosting/DashboardHostingTests.cs`, with a temp folder holding `index.html`,
  `assets/app-abc123.js` and `.env`.

**Interfaces:**
- Produces: `DashboardHosting.UseDashboard(this WebApplication app)`, and
  `ApiRoutes.IsDashboardPath(PathString path) : bool` (not `/api/`, and not under any `LegacyFlatPrefixes` entry).

- [ ] **Step 1: Write failing tests.**
  - `Should_ServeIndex_When_RootRequested` (`no-cache`, the CSP exactly as spec §13.1 / R-2.5, `nosniff`,
    `no-referrer`)
  - `Should_ServeIndex_When_DeepLinkRequested` (`/requests/123`)
  - `Should_CacheImmutable_When_AssetRequested`
  - `Should_NotServe_When_Dotfile` (`/.env` → `index.html` fallback, never the file contents)
  - `Should_Answer405_When_PostToDashboardPath`
  - `Should_NotServeDashboard_When_PathIsLegacyFlatRoute` (`GET /jobs` and `/health` give the API answers)
  - `Should_CoverEveryLegacyRoute_When_PrefixesChecked`: every mapped flat endpoint's route starts with a
    `LegacyFlatPrefixes` entry.
  - `Should_NotClash_When_DashboardClientRoutesChecked` (`/statements`, `/requests`, `/audit`, `/users`, `/login`)
  - `Should_NeverReturnHtml_When_UnknownApiPath`
  - `Should_Answer404PlainText_When_FolderMissing`, with the API still answering
  - `Should_EmitNoCorsHeaders_When_SameOriginCall`
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**, branching on `IsDashboardPath` with `StaticFileMiddleware` over
  a `PhysicalFileProvider` (its default `ExclusionFilters.Sensitive`) and an `index.html` fallback. **Step 4: Run the
  full suite** → PASS. **Step 5: Commit.** `T-1116: serve the dashboard with SPA fallback and headers`

### Task 17 (T-1117): Deploy, runbook, server checklist **[server]**

**Files:**
- Create:
  - `deploy/install-dashboard.ps1 -Source <dist>`: copies to `dashboard.new`, then renames, keeping the previous
    version as `dashboard.old`.
  - `docs/testing/T-1117.server-checklist.md`
- Modify:
  - `docs/runbook.md`: the HTTPS bind, the certificate trust on the accountant's PC, the firewall rule (TCP 5443,
    Domain/Private, `RemoteAddress LocalSubnet`), the dashboard URL, "any future proxy timeout ≥ 10 min", and
    `Api:Cors:AllowedOrigins` empty.
  - `tests/QbAutopost.Core.Tests/Architecture/DeployScriptsTests.cs` coverage, **only through a new** test file
    `DashboardDeployScriptTests.cs`.
- **Checklist items:**
  1. Before migrating, run
     `SELECT count(*) FROM saved_request_versions WHERE payload::text ~ '"(post|pushId|email)"'` and expect 0.
  2. Apply `DashboardV2` and `AuditEventsView`.
  3. Measure the time of a 500-row add against a test company file, for Q-96.
  4. Sign in from another PC over HTTPS; a reload keeps the session.
  5. `GET /jobs` still returns the API answer.

- [ ] **Step 1: Write the failing script test.** `Should_RenameAtomically_When_InstallingDashboard`, which statically
  checks the script for `dashboard.new` and `Rename-Item`, in the style of the existing deploy-script tests.
- [ ] **Step 2: Implement.** **Step 3: Run the full suite** → PASS.
- [ ] **Step 4:** Set the tracker row to `ready-for-human`, and stop.
- [ ] **Step 5: Commit.** `T-1117: dashboard deploy, HTTPS runbook, server checklist [server]`

---

## Acceptance (from spec §17; tick on completion)

- [ ] **D-0** v1 unchanged: existing tests pass unedited (apart from the one drift-test scoping line), the v1
  OpenAPI document's hash is pinned, and `V1UnchangedTests` pass. — T-1101, T-1109
- [ ] **D-1** email metadata. — T-1104, T-1110, T-1111
- [ ] **D-2** dashboard hosting. — T-1116, T-1117
- [ ] **D-3** long post and disconnect. — T-1113, T-1117
- [ ] **D-4** R-4 items. — T-1111, T-1112, T-1113, T-1115
- [ ] **D-5** pushId. — T-1104, T-1108, T-1110
- [ ] **D-6** `post: false`. — T-1102, T-1103, T-1112, T-1113
- [ ] **D-7** audit feed. — T-1106, T-1114
- [ ] **D-8** statements list. — T-1107, T-1111
- [ ] **D-9** users. — T-1115
- [ ] **D-10** green build, no packages, v2 equivalence. — every task, T-1113
