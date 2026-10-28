# Saved Requests in PostgreSQL — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** n8n saves validate-and-post payloads into PostgreSQL. Signed-in dashboard users review, edit (fully
versioned) and post them by id through the unchanged validate-and-post engine, and every outcome is recorded.

**Architecture:** Pure rules go in `QbAutopost.Core`: the state machine, outcome mapping, canonical form, diff,
duplicates, and the `ISavedRequestStore` contract. A new `QbAutopost.Data` project holds the EF Core 8 code-first
model (Npgsql + ASP.NET Core Identity), the migrations, and `EfSavedRequestStore`. `QbAutopost.Api` gains a
caller resolver (JWT or API key), the auth, user and saved-request routes, and `SavedRequestPoster`, which claims
→ runs `SyncPostRunner` → records. The ledger, rules, lists and SDK lock are untouched.

**Tech Stack:** .NET 8 minimal APIs, EF Core 8 + Npgsql, ASP.NET Core Identity, JwtBearer, xUnit,
WebApplicationFactory, Testcontainers.PostgreSql.

**Spec:** `docs/saved-requests-postgres.md` (approved by the owner 2026-10-27). Every `§` below refers to it.
**Ticket:** #2 · **Branch:** `feature/2-postgres-saved-requests` · **Milestone:** M10 · **Tasks:** T-1001…T-1016

---

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` green on Windows and Linux after **every** task (CLAUDE.md).
- No test contacts a real QuickBooks or Hermes. API tests use `FakeQbGateway`, `FakeHermesClient` and
  `FakeSavedRequestStore`. Only `tests/QbAutopost.Data.Tests` touches PostgreSQL, through Testcontainers, and it
  **skips** (with a reason) when Docker with Linux containers is unavailable. That includes the `windows-latest` CI leg.
- COM only in `QbAutopost.QuickBooks`. `QbAutopost.Core` gets no EF, ASP.NET or Npgsql reference.
- Npgsql is referenced only by `QbAutopost.Data`.
- Money: `decimal`, `0.00` invariant, `numeric(18,2)` columns. The API never computes or adjusts an amount or
  `controlTotal` (rule 3).
- Times: `timestamptz`, UTC. Payload `date` stays `DateOnly`.
- Secrets (`Database:ConnectionString` password, `Auth:Jwt:SigningKey`, `Auth:BootstrapAdmin:Password`, tokens,
  temporary passwords) never appear in logs, responses (except a temporary password, once, to the admin who made
  it), `output/`, `api-batches/` or fixtures. A secret found in `appsettings*.json` logs a warning naming the key.
- `saved_request_versions`, `saved_request_events` and `user_audit` are append-only. `original_body` is written once.
- Packages: only those in spec §13, at the pinned versions in T-1001 step 4, and only after Q-80 is answered `yes`.
- Every new `/api/v1` route is in `src/QbAutopost.Api/wwwroot/openapi.json` in the same task that maps it
  (`OpenApiDriftTests` enforces it).
- Every new route is named explicitly in `ApiRoutes.ScopeFor`, and the fall-through stays `admin`.
- Test names: `Should_<behaviour>_When_<condition>`. One behaviour per test. Fixtures go in `tests/fixtures/`.
- Each task ends with: update its `docs/tracker.md` row (Status, Files, Verified by), add one Session-log line, and
  commit `T-10xx: <summary>` ending with the `Co-Authored-By` trailer.

## Review Focus

1. **Payload JSON that is valid but hostile to binding.** For example `transactions: null`, `transactions: {}`, an
   amount of `1e40` (overflows `decimal`), or `date: "31/12/2026"`. Expected: saved with `readable: false` and a
   path-named error, never a 500. Pinned in T-1004 (`SavedPayloadReader`) and T-1012.
2. **Non-ASCII and very long strings** (emoji, CJK, a 4 000-char memo). Expected:
   `GET /{id}/original` returns the exact received bytes with `charset=utf-8`. Pinned in T-1007 (store) and T-1012.
3. **Bad `{id}` values** (`abc`, `-1`, `SR-`, `SR-99999999999999999999`, `0`). Expected: `404`, never `400`/`500`.
   Pinned in T-1012.
4. **PostgreSQL drops between the claim and the result write.** Expected: the post finishes, the result goes to
   `api-batches/<postId>/saved-request.json`, the request stays `posting`, and the next start marks it `unknown`.
   Pinned in T-1014.
5. **An edit that differs only in number formatting or key order** (`184.3` vs `184.30`, keys reordered). Expected:
   `200`, no new version. Pinned in T-1003 (canonicaliser) and T-1013.

---

## File structure

```
src/QbAutopost.Core/SavedRequests/
  SavedRequestStatus.cs          enum + AttemptOutcome enum
  SavedRequestRules.cs           state machine: AllowedActions, CanEdit/CanPost/…, Transition checks
  PostEnding.cs                  engine ending, as Core sees it
  AttemptOutcomes.cs             PostEnding → (AttemptOutcome, SavedRequestStatus, httpStatus)
  PayloadCanonicalizer.cs        canonical JSON + sha256
  PayloadDiff.cs                 PayloadChange record + Compute
  SavedPayloadReader.cs          JsonElement → SyncPostRequest with JSON-path errors
  DuplicateDetector.cs           DuplicateDigest, DuplicateMatch, Detect
  ValidationSnapshot.cs          snapshot record (§6.4)
  ISavedRequestStore.cs          the store contract
  SavedRequestModels.cs          SavedRequest, SavedRequestSummary, SavedRequestVersion, PostAttempt, SavedRequestEvent,
                                 NewSavedRequest, NewVersion, AttemptCompletion, SavedRequestQuery, Page<T>, results
src/QbAutopost.Core/Security/
  ApiClient.cs (modify)          new scopes; Allows(): admin ≠ people-only scopes
  Caller.cs                      Caller record: Kind (User|ApiClient), Id, Name, Email, Role, Scopes
  DashboardRoles.cs              admin/editor/viewer → scopes
src/QbAutopost.Data/             (new project)
  QbAutopostDbContext.cs, Entities/*.cs, Configurations/*.cs, Migrations/*
  EfSavedRequestStore.cs, DataServiceCollectionExtensions.cs, SchemaState.cs
src/QbAutopost.Api/
  Configuration/DatabaseSettings.cs, AuthSettings.cs, SavedRequestSettings.cs
  Database/DatabaseGate.cs           schema/reachability state + 503 endpoint filter
  Database/InFlightRecovery.cs       IHostedService, runs once at start (no timer)
  Auth/TokenService.cs, Auth/RefreshTokenService.cs, Auth/BootstrapAdmin.cs, Auth/UserGate.cs
  Security/CallerMiddleware.cs       replaces ApiKeyMiddleware (renamed + JWT branch)
  Endpoints/AuthEndpoints.cs, UserEndpoints.cs, SavedRequestEndpoints.cs
  SavedRequests/SavedRequestValidator.cs   snapshot via DirectPostRunner.Plan
  SavedRequests/SavedRequestIntake.cs      save + edit pipeline (read → snapshot → duplicates → store)
  SavedRequests/SavedRequestPoster.cs      claim → SyncPostRunner → complete
  SavedRequests/SavedRequestViews.cs       DTOs returned by the routes, incl. allowedActions
tests/QbAutopost.Core.Tests/SavedRequests/*Tests.cs
tests/QbAutopost.Data.Tests/   (new) SavedRequestStoreContract.cs, EfSavedRequestStoreTests.cs,
                               FakeSavedRequestStoreTests.cs, MigrationTests.cs, PostgresFixture.cs
tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.cs, FakeUsers.cs, ApiFactory.cs (modify)
tests/QbAutopost.Api.Tests/SavedRequests/*Tests.cs, Auth/*Tests.cs
tests/fixtures/saved-requests/*.json
deploy/install-postgres.ps1, deploy/backup-postgres.ps1, deploy/apply-migrations.ps1, deploy/dev/docker-compose.yml
```

---

### Task 1 (T-1001): Decisions, docs and package approval

**Files:**
- Create: `docs/adr/0009-postgresql-for-saved-requests.md`, `docs/adr/0010-dashboard-users-and-jwt.md`
- Modify: `docs/adr/0002-json-files-no-database.md` (Status line), `docs/adr/README.md`, `CLAUDE.md`
  (Don't › database bullet and package list), `docs/spec.md` §2 "No database" bullet, `docs/spec-api-v1.md` §1
  out-of-scope line, `docs/tracker.md` (M10 milestone row, M10 section with T-1001…T-1016, Q-80…Q-84,
  Decisions row)

**Interfaces:** Produces the tracker rows every later task updates.

- [ ] **Step 1:** Write ADR-0009 from the template. Decision: "PostgreSQL via EF Core 8 code-first holds saved
  requests, their versions, attempts, events and dashboard users. All other state stays in JSON files." Cover the
  alternatives (SQLite, JSON files) and the risks (DB outage → §10.7, ledger not moved → §2).
- [ ] **Step 2:** Write ADR-0010: Identity + HMAC JWT (15 min) + rotating refresh cookie (7 d). People-only scopes
  (§11.3).
- [ ] **Step 3:** Set ADR-0002's Status to `superseded in part by ADR-0009 (2026-10-27)`. Amend `CLAUDE.md`
  › Don't to: "Don't introduce a database **other than the PostgreSQL store of ADR-0009**, a message broker, or
  background timers beyond the single `JobWorker`." Amend spec.md §2 and spec-api-v1 §1 to point at ADR-0009.
- [ ] **Step 4:** Add Q-80…Q-84 from spec §15 to the tracker. Q-80's row lists the exact pins, using the newest
  `8.0.x` patch available on nuget.org at execution time for the three Microsoft packages, and the newest 4.x for
  Testcontainers: `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.x`,
  `Microsoft.EntityFrameworkCore.Design 8.0.x` (PrivateAssets=all),
  `Microsoft.AspNetCore.Identity.EntityFrameworkCore 8.0.x`, `Microsoft.AspNetCore.Authentication.JwtBearer 8.0.x`,
  `Testcontainers.PostgreSql 4.x`. Put the owner's answer in the Answer column. **If Q-80 is not answered `yes`,
  stop here and mark T-1002+ `blocked`.** Add the same list to `CLAUDE.md`'s package list with
  "(approved 2026-10-27 for M10, Q-80)".
- [ ] **Step 5:** Run `dotnet build -warnaserror` and `dotnet test`. Expected: green (docs only).
- [ ] **Step 6:** Commit `T-1001: ADR-0009/0010, CLAUDE.md and spec amendments, M10 in the tracker`.

---

### Task 2 (T-1002): State machine and outcome mapping (Core)

**Files:**
- Create: `src/QbAutopost.Core/SavedRequests/SavedRequestStatus.cs`, `SavedRequestRules.cs`, `PostEnding.cs`,
  `AttemptOutcomes.cs`
- Test: `tests/QbAutopost.Core.Tests/SavedRequests/SavedRequestRulesTests.cs`, `AttemptOutcomesTests.cs`

**Interfaces — Produces:**
```csharp
public enum SavedRequestStatus { Pending, Posting, Posted, Partial, Unknown, Discarded }   // JSON: kebab/lower
public enum AttemptOutcome { InProgress, Posted, Partial, Failed, Held, Refused, Busy, Unavailable, Error, Unknown }
[Flags] public enum SavedRequestAction { None=0, Edit=1, Validate=2, Post=4, Discard=8, Restore=16, Resolve=32 }
public static class SavedRequestRules {
  SavedRequestAction Allowed(SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser);
  IReadOnlyList<string> AllowedActionNames(SavedRequestStatus, IReadOnlySet<string>, bool isUser); // "edit","post",…
  bool CanResolveTo(SavedRequestStatus from, ResolveOutcome to);   // Unknown→Posted|NotPosted; Partial→Posted only
}
public enum ResolveOutcome { Posted, NotPosted }
public enum PostEndingKind { Unreadable, Refused, Held, Busy, Unavailable, Completed, ErrorBeforeSend, ErrorAfterSend }
public sealed record PostEnding(PostEndingKind Kind, JobStatus? Status, IReadOnlyList<string> HeldReasons);
public sealed record AttemptVerdict(AttemptOutcome Outcome, SavedRequestStatus NextStatus, int? HttpStatus);
public static class AttemptOutcomes { AttemptVerdict From(PostEnding ending); }
```
Scope constants (`requests:submit|read|edit|post|admin`, `users:manage`, `lists:read`) are added in T-1009. Here,
use string literals through a `SavedRequestScopes` static class created in this task and moved/aliased in T-1009:
`Submit="requests:submit"`, `Read="requests:read"`, `Edit="requests:edit"`, `Post="requests:post"`,
`Admin="requests:admin"`.

- [ ] **Step 1: Write the failing tests.** Use `[Theory]` tables over every spec §7.1 row:
  - `Should_AllowEditValidatePostDiscard_When_PendingAndEditor`: `Allowed(Pending, {read,edit,post}, true)` ==
    `Edit|Validate|Post|Discard`.
  - `Should_AllowNothing_When_StatusIs(Posting|Posted|Partial|Unknown)` for an editor, except `Resolve` for an
    admin on `Partial` and `Unknown`.
  - `Should_AllowOnlyRestore_When_DiscardedAndAdmin`.
  - `Should_AllowNothing_When_CallerIsApiClientEvenWithScopes` (`isUser:false` with every scope) → `None`.
  - `Should_AllowNothing_When_Viewer` (`{requests:read}`).
  - `Should_RefusePartialToNotPosted`: `CanResolveTo(Partial, NotPosted)` is false.
  - `AttemptOutcomesTests`, one case per spec §7.2 row:
    `Unreadable→(Refused,Pending,400)`, `Refused→(Refused,Pending,400)`, `Held→(Held,Pending,422)`,
    `Busy→(Busy,Pending,409)`, `Unavailable→(Unavailable,Pending,503)`,
    `Completed+Posted→(Posted,Posted,200)`, `Completed+Partial→(Partial,Partial,200)`,
    `Completed+Failed+[]→(Failed,Pending,200)`,
    `Completed+Failed+["quickbooks-no-response"]→(Unknown,Unknown,200)`,
    `Completed+Partial+["quickbooks-no-response"]→(Partial,Partial,200)` (partial already locks),
    `ErrorBeforeSend→(Error,Pending,500)`, `ErrorAfterSend→(Unknown,Unknown,null)`.
- [ ] **Step 2:** Run `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~SavedRequests"`.
  Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the four files. Match the no-response reason against `HoldReasons.QuickBooksNoResponse`
  (never a literal).
- [ ] **Step 4:** Run the same filter. Expected: PASS.
- [ ] **Step 5:** Commit `T-1002: saved-request state machine and attempt outcome mapping`.

---

### Task 3 (T-1003): Canonical form and change list (Core)

**Files:**
- Create: `src/QbAutopost.Core/SavedRequests/PayloadCanonicalizer.cs`, `PayloadDiff.cs`
- Test: `tests/QbAutopost.Core.Tests/SavedRequests/PayloadCanonicalizerTests.cs`, `PayloadDiffTests.cs`
- Fixtures: `tests/fixtures/saved-requests/diff-*.json` (pairs: `before`/`after`)

**Interfaces — Produces:**
```csharp
public static class PayloadCanonicalizer {
  string Canonicalize(JsonElement payload);   // sorted keys, no whitespace, money fields as 0.00
  string Sha256(JsonElement payload);         // lower-hex of UTF-8 canonical string
}
public sealed record PayloadChange(string Op, string Path, JsonNode? Old, JsonNode? New); // Op: add|remove|replace
public static class PayloadDiff { IReadOnlyList<PayloadChange> Compute(JsonElement? previous, JsonElement next); }
```
The money fields are `controlTotal` and `transactions[*].amount`. They are formatted only when the JSON number fits
`decimal`. Otherwise they are kept as the raw token. Every other number is kept as its raw token.

- [ ] **Step 1: Write the failing tests.**
  - `Should_ProduceSameHash_When_OnlyKeyOrderAndWhitespaceDiffer`
  - `Should_ProduceSameHash_When_AmountIs184_3Or184_30` (Review Focus 5)
  - `Should_ProduceDifferentHash_When_StringCaseDiffers` (`"ABC"` vs `"abc"`)
  - `Should_KeepRawToken_When_AmountOverflowsDecimal` (`1e40` does not throw)
  - `Should_ReturnEmpty_When_PreviousIsNull`, and `Should_ReturnEmpty_When_PayloadsCanonicallyEqual`
  - `Should_ReportReplace_When_PayeeChanged`: path `$.transactions[3].payee`, old `"ABC PLUMBING"`, new
    `"ABC Plumbing LLC"`
  - `Should_MatchRowsByExternalId_When_EveryRowHasOne`: deleting the second of three rows yields exactly one
    `remove` at `$.transactions[1]` and no replaces
  - `Should_MatchRowsByIndex_When_AnyRowLacksExternalId`
  - `Should_TreatAmountsAsDecimals_When_Comparing` (184.3 → 184.30 gives no change)
  - `Should_ReportAddAndRemove_When_TopLevelFieldAppearsOrDisappears` (`reference`)
- [ ] **Step 2:** Run the filter `~PayloadCanonicalizer|~PayloadDiff`. Expected: FAIL.
- [ ] **Step 3:** Implement. For rows, the diff recurses into matched rows field by field. Unmatched rows become
  whole-row `add`/`remove`. Paths use the index in the **new** payload for `add`/`replace` and the old index for
  `remove`.
- [ ] **Step 4:** Run the filter. Expected: PASS.
- [ ] **Step 5:** Commit `T-1003: payload canonical form and change list`.

---

### Task 4 (T-1004): Payload reader and duplicate detector (Core)

**Files:**
- Create: `src/QbAutopost.Core/SavedRequests/SavedPayloadReader.cs`, `DuplicateDetector.cs`, `ValidationSnapshot.cs`
- Test: `tests/QbAutopost.Core.Tests/SavedRequests/SavedPayloadReaderTests.cs`, `DuplicateDetectorTests.cs`
- Fixtures: `tests/fixtures/saved-requests/unreadable-*.json`

**Interfaces — Produces:**
```csharp
public sealed record PayloadRead(SyncPostRequest? Request, IReadOnlyList<string> Errors) { bool Readable => Request is not null; }
public static class SavedPayloadReader {
  PayloadRead Read(JsonElement payload);   // uses JsonOptions.Default; errors as "$.path: message"
}
public sealed record DuplicateDigest(long Id, string CanonicalSha256, string? Reference, IReadOnlySet<string> RowFingerprints, DateTime CreatedUtc);
public sealed record DuplicateMatch(long? Of, IReadOnlyList<string> Reasons) { bool Flagged => Reasons.Count > 0; }
public static class DuplicateReasons { const string SamePayload="same-payload", SameReference="same-reference", OverlappingRows="overlapping-rows"; }
public static class DuplicateDetector {
  DuplicateMatch Detect(string canonicalSha256, string? reference, IReadOnlySet<string> rowFingerprints, IEnumerable<DuplicateDigest> others);
  IReadOnlySet<string> RowFingerprints(SyncPostRequest request, DirectLimits limits, DateOnly today); // via DirectRequestReader.Read(...).Lines[].Line.Fingerprint
}
public enum ValidationState { Ok, Held, Refused, Unreadable, Unavailable }
public sealed record ValidationSnapshot(ValidationState State, DateTime ValidatedUtc, int VersionNumber,
  SyncPostFindingCounts? Counts, IReadOnlyList<SyncPostFinding> Validation, IReadOnlyList<string> Errors,
  DirectUnknownNames? UnknownNames) { bool NeedsAttention => State is ValidationState.Held or ValidationState.Refused or ValidationState.Unreadable; }
```

- [ ] **Step 1: Write the failing tests.**
  - The reader returns readable for `samples/validate-and-post-request.json`.
  - `Should_NamePath_When_KindIsUnknownEnum`: the error starts with `$.transactions[3].kind`.
  - `Should_BeUnreadable_When_TransactionsIsNull`, `…_When_TransactionsIsObject`, `…_When_AmountOverflowsDecimal`,
    `…_When_DateIsNotIso` (Review Focus 1). Each has one path-named error and never throws.
  - `Should_KeepDryRunForTheEngineToRefuse_When_Present`: readable, with `DryRun` non-null. The engine refuses it,
    not the reader.
  - Detector: `Should_FlagSamePayload_When_HashesEqual`, `Should_FlagSameReference_When_ReferenceEqualIgnoringCase`,
    `Should_NotFlagReference_When_Empty`, `Should_FlagOverlappingRows_When_AnyFingerprintShared`,
    `Should_ReportOldestMatchAndAllReasons_When_SeveralMatch`, `Should_ReturnNotFlagged_When_NoOthers`.
- [ ] **Step 2:** Run the filter `~SavedPayloadReader|~DuplicateDetector`. Expected: FAIL.
- [ ] **Step 3:** Implement. The reader catches `JsonException` and uses its `Path` property. Overflow surfaces as
  `JsonException` from the decimal converter.
- [ ] **Step 4:** Run the filter. Expected: PASS.
- [ ] **Step 5:** Commit `T-1004: saved payload reader and duplicate detector`.

---

### Task 5 (T-1005): Store contract, fake store, contract test suite

**Files:**
- Create: `src/QbAutopost.Core/SavedRequests/ISavedRequestStore.cs`, `SavedRequestModels.cs`
- Create: `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.cs`
- Create: the `tests/QbAutopost.Data.Tests/` project (xUnit, with a project reference to
  `tests/QbAutopost.Api.Tests`, so it gets `FakeSavedRequestStore` now and `ApiFactory` in T-1010). Add it to
  `QbAutopost.sln`.
- Test: `tests/QbAutopost.Data.Tests/SavedRequestStoreContract.cs` (abstract), `FakeSavedRequestStoreTests.cs`

**Interfaces — Produces** (every later store-touching task uses exactly these):
```csharp
public interface ISavedRequestStore {
  Task<CreateResult> CreateAsync(NewSavedRequest request, CancellationToken ct);
  Task<SavedRequest?> GetAsync(long id, CancellationToken ct);
  Task<Page<SavedRequestSummary>> ListAsync(SavedRequestQuery query, CancellationToken ct);
  Task<SavedRequestStats> StatsAsync(CancellationToken ct);
  Task<IReadOnlyList<DuplicateDigest>> DuplicateCandidatesAsync(DateTime sinceUtc, long? excludeId, CancellationToken ct);
  Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id, CancellationToken ct);
  Task<SavedRequestVersion?> VersionAsync(long id, int number, CancellationToken ct);
  Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id, CancellationToken ct);
  Task<PostAttempt?> AttemptAsync(long id, long attemptId, CancellationToken ct);
  Task<IReadOnlyList<SavedRequestEvent>> EventsAsync(long id, CancellationToken ct);
  Task<MutationResult> AppendVersionAsync(long id, int expectedRowVersion, NewVersion version, CancellationToken ct);
  Task<MutationResult> SetValidationAsync(long id, ValidationSnapshot snapshot, DuplicateMatch duplicate, string actor, DateTime nowUtc, CancellationToken ct);
  Task<MutationResult> ChangeStatusAsync(long id, int? expectedRowVersion, SavedRequestStatus from, SavedRequestStatus to, string actor, DateTime nowUtc, string eventKind, JsonNode? detail, CancellationToken ct);
  Task<ClaimResult> TryClaimAsync(long id, int? expectedRowVersion, string actor, DateTime nowUtc, CancellationToken ct);
  Task CompleteAttemptAsync(long attemptId, AttemptCompletion completion, CancellationToken ct);
  Task<MutationResult> ResolveAsync(long id, ResolveOutcome outcome, string note, string actor, DateTime nowUtc, CancellationToken ct);
  Task<int> RecoverInFlightAsync(DateTime nowUtc, CancellationToken ct);
}
// Results (records): CreateResult(CreateKind Kind, SavedRequest Record) — Kind: Created | Replayed | KeyReused
// MutationResult(MutationKind Kind, SavedRequest? Record) — Kind: Ok | NotFound | Stale | WrongStatus | Unchanged
// ClaimResult(ClaimKind Kind, long? AttemptId, int VersionNumber, SavedRequest? Record) — Kind: Claimed | NotFound | Stale | AlreadyPosting | AlreadyPosted | NotPostable
// NewSavedRequest(string Source, string CreatedBy, string OriginalBody, JsonElement Payload, PayloadRead Read,
//                 string CanonicalSha256, string? IdempotencyKey, ValidationSnapshot Snapshot, DuplicateMatch Duplicate, DateTime NowUtc)
// NewVersion(JsonElement Payload, PayloadRead Read, string CanonicalSha256, IReadOnlyList<PayloadChange> Changes,
//            string? Note, ValidationSnapshot Snapshot, DuplicateMatch Duplicate, string Actor, DateTime NowUtc)
// AttemptCompletion(AttemptOutcome Outcome, SavedRequestStatus NextStatus, int? HttpStatus, string? PostId,
//                   JsonNode? Result, JsonNode? Counts, string? Error, DateTime FinishedUtc)
// SavedRequestQuery(IReadOnlyList<SavedRequestStatus> Statuses, bool? NeedsAttention, bool? Duplicate, string? Q,
//                   DateOnly? CreatedFrom, DateOnly? CreatedTo, string? Source, string Sort, int Page, int PageSize)
// Page<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
// SavedRequest carries every §5.2 column (RowVersion included) plus CurrentPayload (JsonElement) and LastAttempt (PostAttempt?).
// PublicRef is $"SR-{Id:000000}".
```
Event kinds are the constants in `SavedRequestEvents`: `created`, `flagged-duplicate`, `edited`, `validated`,
`post-started`, `post-finished`, `discarded`, `restored`, `resolved`.

- [ ] **Step 1: Write the contract tests** in the abstract `SavedRequestStoreContract` (with
  `protected abstract ISavedRequestStore CreateStore()`):
  - `Should_StoreVersion1AndOriginalBody_When_Created`
  - `Should_ReplayExisting_When_SameSourceAndKeyAndBody`, `Should_ReportKeyReused_When_SameKeyDifferentBody`,
    `Should_CreateSeparately_When_SameKeyDifferentSource`
  - `Should_AppendVersionAndBumpRowVersion_When_ExpectedRowVersionMatches`
  - `Should_ReturnStale_When_ExpectedRowVersionIsOld`
  - `Should_ReturnWrongStatus_When_AppendingToNonPending`
  - `Should_ReturnUnchanged_When_CanonicalHashEqualsCurrent`
  - `Should_KeepVersion1Unchanged_When_EditedThreeTimes`
  - `Should_ClaimOnce_When_TwoClaimsRace` (two `Task.WhenAll` claims → exactly one `Claimed`, the other
    `AlreadyPosting`)
  - `Should_RefuseClaim_When_Posted`/`Discarded`/`Unknown` (with the matching `ClaimKind`)
  - `Should_ReturnStale_When_ClaimExpectedRowVersionIsOld`
  - `Should_RecordAttemptAndMoveStatus_When_Completed`
  - `Should_MarkUnknown_When_RecoveringInFlight` (the claimed request and its attempt both become `Unknown`, with
    error "the service restarted while this post was running")
  - `Should_FilterAndPage_When_Listing` (statuses, needsAttention, q over reference/payee/memo, total correct)
  - `Should_ExcludeDiscardedAndOld_When_ListingDuplicateCandidates`
  - `Should_ResolvePartialOnlyToPosted`
  - `Should_WriteOneEventPerMutation`
- [ ] **Step 2:** Add `FakeSavedRequestStoreTests : SavedRequestStoreContract`. Run
  `dotnet test tests/QbAutopost.Data.Tests`. Expected: FAIL (fake missing).
- [ ] **Step 3:** Implement `FakeSavedRequestStore`: in-memory, one `lock` around all state, ids from 1.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5:** Commit `T-1005: saved-request store contract and in-memory fake`.

---

### Task 6 (T-1006): `QbAutopost.Data`: DbContext, Identity, initial migration

**Files:**
- Create: `src/QbAutopost.Data/QbAutopost.Data.csproj` (refs Core; packages Npgsql provider, Identity.EF, Design
  with `PrivateAssets=all`; `<NoWarn>` for generated code is set on `Migrations/**` only via
  `<Compile Update="Migrations\**" ... />` if `-warnaserror` trips on it)
- Create: `QbAutopostDbContext.cs` (`IdentityDbContext<AppUser, IdentityRole, string>`), `Entities/SavedRequestEntity.cs`,
  `SavedRequestVersionEntity.cs`, `PostAttemptEntity.cs`, `SavedRequestEventEntity.cs`, `AppUser.cs`,
  `RefreshTokenEntity.cs`, `UserAuditEntity.cs`, `Configurations/*.cs`, `SchemaState.cs`, `DesignTimeFactory.cs`
- Create: `.config/dotnet-tools.json` with `dotnet-ef` pinned to the same 8.0.x
- Create: `Migrations/<timestamp>_Initial.cs` through `dotnet ef migrations add Initial -p src/QbAutopost.Data`
- Create: `deploy/dev/docker-compose.yml` (`postgres:16`, `127.0.0.1:5432`, db `qbautopost`)
- Test: `tests/QbAutopost.Data.Tests/PostgresFixture.cs`, `MigrationTests.cs`

**Interfaces — Produces:**
```csharp
public sealed class AppUser : IdentityUser { string DisplayName; bool IsDisabled; bool MustChangePassword; DateTime CreatedUtc; string CreatedBy; DateTime? LastLoginUtc; }
public static class SchemaState { Task<SchemaStatus> CheckAsync(QbAutopostDbContext db, CancellationToken ct); }
public sealed record SchemaStatus(bool Reachable, bool Current, IReadOnlyList<string> Pending, string? Reason); // Reason: "unreachable" | "schema-out-of-date"
public sealed class PostgresFixture : IAsyncLifetime { string ConnectionString; bool Available; string? SkipReason; }
```
Tables, columns and indexes are exactly those in §5.2. Use snake_case naming
(`UseSnakeCaseNamingConvention` is not in the approved packages, so a small `ModelBuilder` convention loop
applies it). Map Identity tables to `users`, `roles`, `user_roles` and so on. `row_version` is configured
`.IsConcurrencyToken()`. Payload columns are `jsonb`, `original_body` is `text`, and `duplicate_reasons` is
`text[]`. Indexes: `(status, created_utc desc)`, `(reference)`, `(canonical_sha256)`, a unique
`(source, idempotency_key)` filtered `idempotency_key IS NOT NULL`, and a unique `(saved_request_id, number)`.

- [ ] **Step 1: Write the failing tests.** Tests that need Docker use the custom `[PostgresFact]` attribute, which
  sets `Skip` from `PostgresFixture.SkipReason`. Docker unavailable on that platform counts as a skip.
  - `Should_ApplyAllMigrations_When_DatabaseIsEmpty`: then `SchemaState.CheckAsync` gives `Current == true`.
  - `Should_HaveNoPendingModelChanges`: `db.Database.HasPendingModelChanges()` is false (EF Core 8 API; the
    context is built with the Npgsql provider and a dummy connection string, so no Docker is needed).
  - `Should_ReportOutOfDate_When_LastMigrationNotApplied` (migrate to the previous one; initially, a fixture
    migration-less database) → `Reason == "schema-out-of-date"`.
  - `Should_ReportUnreachable_When_ConnectionRefused` (port 1). Needs no Docker.
- [ ] **Step 2:** Run `dotnet test tests/QbAutopost.Data.Tests`. Expected: FAIL (compile).
- [ ] **Step 3:** Implement the entities, configurations and `DesignTimeFactory` (reads `QBAUTOPOST_DESIGN_CONNECTION`,
  falling back to the docker-compose default). Generate `Initial`.
- [ ] **Step 4:** Run `docker compose -f deploy/dev/docker-compose.yml up -d`, then `dotnet test`. Expected: PASS.
  On a machine without Docker, the Docker tests are reported as skipped and the run is green.
- [ ] **Step 5:** Commit `T-1006: QbAutopost.Data with EF Core model, Identity tables and initial migration`.

---

### Task 7 (T-1007): `EfSavedRequestStore`

**Files:**
- Create: `src/QbAutopost.Data/EfSavedRequestStore.cs`, `DataServiceCollectionExtensions.cs`
- Test: `tests/QbAutopost.Data.Tests/EfSavedRequestStoreTests.cs` (`: SavedRequestStoreContract`, all `[PostgresFact]`)

**Interfaces:**
- Consumes: `ISavedRequestStore` and its models (T-1005), and `QbAutopostDbContext` (T-1006).
- Produces: `IServiceCollection AddQbAutopostData(this IServiceCollection, string connectionString, int commandTimeoutSeconds)`,
  which registers a pooled `DbContextFactory` and `ISavedRequestStore` → `EfSavedRequestStore` (singleton, so each
  method creates its own context).

- [ ] **Step 1: Write the extra failing tests** beyond the contract:
  - `Should_ReturnOriginalBodyByteForByte_When_ContainsEmojiAndCjkAnd4000CharMemo` (Review Focus 2)
  - `Should_ClaimOnce_When_TwentyConcurrentClaims` (real connections, `Parallel.ForEachAsync`)
  - `Should_ThrowFromStore_When_UpdatingAVersionRow`: the store has no such method, so this asserts via reflection
    that `ISavedRequestStore` has no member taking a version id. The DB-role half of the guarantee is T-1016.
  - `Should_MapUniqueViolationToReplayOrKeyReused_When_ConcurrentSavesShareKey`
- [ ] **Step 2:** Run `dotnet test tests/QbAutopost.Data.Tests --filter ~EfSavedRequestStore`. Expected: FAIL.
- [ ] **Step 3:** Implement. Claim = one transaction: `ExecuteUpdateAsync(... Where(Id==id && Status==Pending [&& RowVersion==expected]) .Set(Status=Posting, RowVersion+1, UpdatedBy, UpdatedUtc))`.
  If 0 rows changed, re-read to pick the `ClaimKind`. If 1 row changed, insert the attempt (`in-progress`,
  `version_number = current_version`) and the `post-started` event, then commit. Idempotency: catch
  `PostgresException` `23505` on the idempotency index, then re-read and compare `original_sha256`. The `q` search
  uses `EF.Functions.ILike` on `reference` and `public_ref`, plus a `jsonb_path_exists` raw-SQL predicate with
  parameters for payee/memo.
- [ ] **Step 4:** Run with Docker up. Expected: PASS (contract and extras). The fake still passes too.
- [ ] **Step 5:** Commit `T-1007: EF Core saved-request store`.

---

### Task 8 (T-1008): Database wiring in the API: settings, gate, health, recovery

**Files:**
- Create: `src/QbAutopost.Api/Configuration/DatabaseSettings.cs`, `SavedRequestSettings.cs`;
  `Database/DatabaseGate.cs`, `Database/InFlightRecovery.cs`
- Modify: `src/QbAutopost.Api/Program.cs` (bind sections; when `Database:ConnectionString` is non-empty call
  `AddQbAutopostData`, otherwise register a `DisabledSavedRequestStore` that the gate reports as `not-configured`),
  `Health/*` (readiness gains `database`; new `GET /health/database`), `Endpoints/ApiRoutes.cs`
  (`/health/database` scope), `appsettings.json` (§4.2 sections, empty secrets), `wwwroot/openapi.json`,
  `QbAutopost.Api.csproj` (ref Data)
- Modify: `tests/QbAutopost.Api.Tests/TestSupport/ApiFactory.cs`. It registers `FakeSavedRequestStore` (exposed as
  `Store`) and a `DatabaseGate` forced to `Ready` unless a test sets it.
- Test: `tests/QbAutopost.Api.Tests/Database/DatabaseGateTests.cs`, `InFlightRecoveryTests.cs`, `DatabaseHealthTests.cs`

**Interfaces — Produces:**
```csharp
public sealed class DatabaseGate { DatabaseState State { get; } Task RefreshAsync(CancellationToken ct); void Set(DatabaseState s); }
public enum DatabaseState { Ready, NotConfigured, Unreachable, SchemaOutOfDate }
public static class DatabaseGateFilter { RouteGroupBuilder RequireDatabase(this RouteGroupBuilder g); } // 503 database-unavailable + Retry-After: 30
```
`DatabaseGate` checks the schema once at start, and again whenever a store call throws `NpgsqlException` or
`TimeoutException`. This is lazy, with no timer (CLAUDE.md). `InFlightRecovery.StartAsync` runs
`RecoverInFlightAsync` once when the gate is `Ready`. When the gate is not ready, recovery runs on the first
successful `RefreshAsync`.

- [ ] **Step 1: Write the failing tests.**
  - `Should_Answer503WithRetryAfter30_When_GateNotReady`: a probe route in a `RequireDatabase` group → `503`,
    title `Database unavailable`, header `Retry-After: 30`.
  - `Should_KeepValidateAndPostWorking_When_GateUnreachable` (SR-12): `POST /api/v1/quickbooks/transactions/validate-and-post`
    with the sample body → `200`.
  - `Should_ReportDatabaseInReadiness_When_Unreachable`: `/health/ready` shows `database.ok == false` with reason
    `unreachable`, and the other checks are still reported.
  - `Should_NeverIncludeConnectionString_When_HealthDatabaseAnswers`: the body has no `Password=` or `Host=`.
  - `Should_LogKeyNotValue_When_ConnectionStringWithPasswordInAppSettings` (uses `ListLogger`)
  - `Should_MarkPostingRequestsUnknown_When_HostStarts`: seed `FakeSavedRequestStore` with a claimed request, start
    the factory, and the request is `Unknown`.
- [ ] **Step 2:** Run the filter `~Database`. Expected: FAIL.
- [ ] **Step 3:** Implement. The secret warning hooks into the existing configuration warning pass (see how
  `Api:Tls:PfxPassword` is handled) and adds `Database:ConnectionString` (only when it contains `Password=`),
  `Auth:Jwt:SigningKey` and `Auth:BootstrapAdmin:Password`.
- [ ] **Step 4:** Run the full `dotnet test`. Expected: PASS.
- [ ] **Step 5:** Commit `T-1008: database gate, health and in-flight recovery`.

---

### Task 9 (T-1009): Scopes and the caller resolver (JWT or API key)

**Files:**
- Create: `src/QbAutopost.Core/Security/Caller.cs`, `DashboardRoles.cs`
- Modify: `src/QbAutopost.Core/Security/ApiClient.cs` (new scope constants, `PeopleOnly` set, `Allows`),
  `src/QbAutopost.Core/SavedRequests/SavedRequestRules.cs` (use `ApiScopes` constants; delete the T-1002
  `SavedRequestScopes` literals)
- Rename/modify: `src/QbAutopost.Api/Security/ApiKeyMiddleware.cs` → `CallerMiddleware.cs` (keep `ClientOf`
  working for existing callers by returning the caller's `ApiClient` when it is one; add `CallerOf`)
- Create: `src/QbAutopost.Api/Auth/UserGate.cs` (interface `IUserGate` + impl over `UserManager<AppUser>` with a
  30 s `IMemoryCache`)
- Modify: `src/QbAutopost.Api/Endpoints/ApiRoutes.cs` (`ScopeFor` for every §6, §11.2, §11.4 route;
  `/quickbooks/lists` GET → `lists:read`), `Program.cs` (JwtBearer validation parameters only, without the auth
  middleware pipeline: validation is called from `CallerMiddleware` using `JsonWebTokenHandler`)
- Test: `tests/QbAutopost.Core.Tests/Security/ScopeTests.cs`, `tests/QbAutopost.Api.Tests/Auth/CallerMatrixTests.cs`,
  `tests/QbAutopost.Api.Tests/Hardening/EndpointSurfaceTests.cs` (extend)
- Test support: `tests/QbAutopost.Api.Tests/TestSupport/FakeUsers.cs` (in-memory `IUserGate`, plus
  `string TokenFor(string role, bool mustChange=false, bool disabled=false)` signed with the test key)

**Interfaces — Produces:**
```csharp
// ApiScopes additions
const string RequestsSubmit="requests:submit", RequestsRead="requests:read", RequestsEdit="requests:edit",
             RequestsPost="requests:post", RequestsAdmin="requests:admin", UsersManage="users:manage", ListsRead="lists:read";
static IReadOnlySet<string> PeopleOnly { get; } // RequestsEdit, RequestsPost, RequestsAdmin, UsersManage
// ApiClient.Allows(scope): PeopleOnly.Contains(scope) ? false : (Scopes has scope || Scopes has Admin || (scope==ListsRead && Scopes has QbRead))
public sealed record Caller(CallerKind Kind, string Id, string Name, string? Email, string? Role, IReadOnlySet<string> Scopes, bool MustChangePassword)
{ string Actor => Kind == CallerKind.User ? Id : $"api-client:{Id}"; bool Allows(string scope); }
public static class DashboardRoles { const string Admin="admin", Editor="editor", Viewer="viewer"; IReadOnlySet<string> ScopesOf(string role); }
// viewer: requests:read, lists:read
// editor: + requests:submit, requests:edit, requests:post
// admin:  + requests:admin, users:manage
public interface IUserGate { Task<Caller?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct); } // null = disabled or stamp mismatch
public static Caller? CallerMiddleware.CallerOf(HttpContext);
```

- [ ] **Step 1: Write the failing tests.**
  - Core: `Should_RefusePeopleOnlyScopes_When_ApiClientHoldsAdmin` (each of the 4 scopes),
    `Should_GrantListsRead_When_ApiClientHoldsQbRead`, and
    `Should_NotGrantQbReadOrHealthRead_When_AnyDashboardRole` (each role).
  - API `CallerMatrixTests`: a table over `{GET /saved-requests, PUT /saved-requests/1, POST /saved-requests/1/post, POST /saved-requests/1/resolve, GET /users, GET /quickbooks/lists, POST /quickbooks/lists/sync, POST /quickbooks/connection/test, POST /quickbooks/transactions/validate-and-post}`
    × `{no credentials → 401, bad JWT → 401, expired JWT → 401, disabled user → 401, must-change user → 403 password-change-required (except /auth/me), viewer, editor, admin, legacy key, n8n key (requests:submit), both headers → 400 ambiguous-credentials}`.
    Expected values come from §11.1 and §11.3. Routes that do not exist yet are asserted only up to the auth
    decision: anything other than 401/403/400 counts as "passed auth".
  - `Should_MapEveryNewRouteExplicitly`: the `ScopeFor` result for each new path is not `admin` unless it is
    `requests:admin`/`users:manage` by design. This repeats the validate-and-post "security trap" check.
  - `Should_RejectUser_Within30Seconds_When_Disabled` (advance `FixedClock`/cache past 30 s).
- [ ] **Step 2:** Run the filters `~Scope|~CallerMatrix|~EndpointSurface`. Expected: FAIL.
- [ ] **Step 3:** Implement. JWT validation: issuer, audience, lifetime, HS256 with `Auth:Jwt:SigningKey`. When the
  key is empty or shorter than 32 bytes, a Bearer request answers `503 auth-not-configured` while API keys keep
  working. The `FailedAuth` brake also counts bad bearer tokens. The log property `clientId` becomes `user:<id>`
  for users.
- [ ] **Step 4:** Run the full `dotnet test`. Expected: PASS. Every existing auth test is still green.
- [ ] **Step 5:** Commit `T-1009: caller resolver with JWT users, people-only scopes and lists:read`.

---

### Task 10 (T-1010): Auth routes: login, refresh, logout, me, change-password

**Files:**
- Create: `src/QbAutopost.Api/Configuration/AuthSettings.cs`, `Auth/TokenService.cs`, `Auth/RefreshTokenService.cs`,
  `Endpoints/AuthEndpoints.cs`
- Modify: `Program.cs` (`AddIdentityCore<AppUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<QbAutopostDbContext>()`,
  with password options `RequiredLength = Auth:Password:MinLength` and every `Require*` false, lockout options from
  `Auth:Lockout`), `ApiRoutes.cs` (`/auth/login` and `/auth/refresh` need no scope but pass through the brake;
  `/auth/me`, `/auth/logout` and `/auth/change-password` need any signed-in user), `openapi.json`
- Test: `tests/QbAutopost.Data.Tests/Api/AuthApiTests.cs` (`[PostgresFact]`, using
  `ApiFactory.WithPostgres(string connectionString)`, which this task adds). Identity needs a real store, and EF
  InMemory is not an approved package. The Docker-free assertions (503 without a signing key, the bearer path with
  `FakeUsers`) go in `tests/QbAutopost.Api.Tests/Auth/AuthConfigTests.cs`.

**Interfaces — Produces:**
```csharp
public sealed class TokenService { AccessToken Issue(AppUser user, string role, string securityStamp); } // AccessToken(string Token, DateTime ExpiresUtc)
public sealed class RefreshTokenService {
  Task<string> IssueAsync(string userId, Guid? familyId, string? ip, CancellationToken ct);       // returns raw token
  Task<RefreshResult> RotateAsync(string rawToken, string? ip, CancellationToken ct);              // Ok(userId, newRaw) | Invalid | ReuseDetected
  Task RevokeAllAsync(string userId, CancellationToken ct);
}
public const string RefreshCookie = "qbautopost_refresh"; // HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth
```
`RefreshTokenService` and the user audit writes go through `QbAutopostDbContext` directly. These are Identity
concerns, not `ISavedRequestStore`.

- [ ] **Step 1: Write the failing tests.**
  - `Should_Return200WithTokenAndCookie_When_CredentialsValid`: the cookie has `httponly`, `secure`,
    `samesite=strict` and `path=/api/v1/auth`. `user.role` is present. `user_audit` has `login`. `last_login_utc`
    is set.
  - `Should_Return401SameBody_When_EmailUnknownOrPasswordWrong` (the two bodies are byte-equal)
  - `Should_Return401_When_UserDisabled` (same body)
  - `Should_Return423WithRetryAfter_When_FiveFailures` (`MaxFailedAttempts=5`). `user_audit` has `locked-out`.
  - `Should_RotateRefresh_When_CookieValid`, and `Should_RevokeFamilyAnd401_When_RevokedTokenReused`
  - `Should_RevokeCurrentAndClearCookie_When_Logout`
  - `Should_RevokeOtherTokens_When_PasswordChanged`, `Should_Return400_When_NewPasswordShorterThan12`
  - `Should_ClearMustChange_When_PasswordChanged`
  - `Should_Answer503_When_DatabaseGateNotReady` (login)
  - `Should_NeverLogPasswordOrToken` (`ListLogger` lines contain neither the password nor any part of the token)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Refresh tokens are 32 random bytes, base64url. Only their SHA-256 is stored.
- [ ] **Step 4:** Run the full suite with Docker up, then once with Docker stopped (Data tests skip). Expected:
  green both times.
- [ ] **Step 5:** Commit `T-1010: dashboard sign-in with JWT and rotating refresh cookie`.

---

### Task 11 (T-1011): User management and bootstrap admin

**Files:**
- Create: `src/QbAutopost.Api/Endpoints/UserEndpoints.cs`, `Auth/BootstrapAdmin.cs` (`IHostedService`, runs once)
- Modify: `ApiRoutes.cs` (`/users…` → `users:manage`), `openapi.json`
- Test: `tests/QbAutopost.Data.Tests/Api/UserApiTests.cs` (`[PostgresFact]`)

**Interfaces — Produces:** routes per §11.4. The user view is
`UserView(string Id, string Email, string DisplayName, string Role, bool Disabled, bool MustChangePassword, DateTime CreatedUtc, DateTime? LastLoginUtc, DateTime? LockedOutUntilUtc, int RowVersion)`.
`PATCH` uses `If-Match` against `AppUser.ConcurrencyStamp`, exposed as an ETag. Temporary passwords: 16 characters
from an unambiguous alphabet (`ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789`), generated with
`RandomNumberGenerator`.

- [ ] **Step 1: Write the failing tests.**
  - `Should_Create201WithTemporaryPasswordOnce_When_AdminCreatesUser`: a later `GET /users/{id}` has no
    password field. The new user must change their password.
  - `Should_Return409_When_EmailExistsIgnoringCase`
  - `Should_Return409LastAdmin_When_DemotingOrDisablingOnlyAdmin`, and `Should_Return409_When_AdminDisablesSelf`
  - `Should_RevokeTokensAndChangeStamp_When_RoleChangedOrDisabled`
  - `Should_Return412_When_IfMatchStale`, and `Should_Return428_When_IfMatchMissing`
  - `Should_ResetPasswordAndRequireChange_When_Reset`
  - `Should_ClearLockout_When_Unlock`
  - `Should_ListAuditRows_When_GetAudit`
  - `Should_Return403_When_EditorCallsUsers`
  - Bootstrap: `Should_CreateAdminWithMustChange_When_TableEmptyAndSettingsPresent`, and
    `Should_IgnoreAndWarn_When_UsersExist`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `T-1011: user management and bootstrap admin`.

---

### Task 12 (T-1012): Save route and read routes

**Files:**
- Create: `src/QbAutopost.Api/SavedRequests/SavedRequestValidator.cs`, `SavedRequestIntake.cs`,
  `SavedRequestViews.cs`, `Endpoints/SavedRequestEndpoints.cs`
- Modify: `ApiRoutes.cs` (each route's scope. `POST /saved-requests` stays **out** of `IsIdempotent`, with a
  comment explaining why: the store handles `Idempotency-Key` per source, §10.1), `Program.cs` (map the group
  under `RequireDatabase()`), `openapi.json`
- Fixtures: `tests/fixtures/saved-requests/n8n-*.json` (good, unreadable kind, control total wrong, dryRun present,
  rules-missing case)
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/SaveApiTests.cs`, `ReadApiTests.cs`

**Interfaces:**
- Consumes: `SavedPayloadReader`, `PayloadCanonicalizer`, `DuplicateDetector`, `ISavedRequestStore`,
  `DirectPostRunner.Plan`, `SyncPostFindings.From`, `CallerMiddleware.CallerOf`.
- Produces:
```csharp
public sealed class SavedRequestValidator { ValidationSnapshot Validate(PayloadRead read, int versionNumber, DateTime nowUtc); }
// Unreadable → state Unreadable. Request-level refusals (dryRun, allowModelAccounts, the SyncPostRunner.Unsupported
// texts, plan.Errors) → Refused. !plan.Ok → Held with SyncPostFindings. A rules-file exception
// (the T-920 exception type) → Unavailable.
public sealed class SavedRequestIntake {
  Task<CreateResult> SaveAsync(string rawBody, string? idempotencyKey, Caller caller, CancellationToken ct);
  Task<MutationResult> EditAsync(long id, int expectedRowVersion, JsonElement payload, string? note, Caller caller, CancellationToken ct);
  Task<MutationResult> RevalidateAsync(long id, Caller caller, CancellationToken ct);
}
public static class SavedRequestViews { object Detail(SavedRequest r, Caller c); object Summary(SavedRequestSummary s); }
```
Move the two refusal texts out of `SyncPostRunner.Unsupported` into a `public static` method,
`SyncPostRunner.UnsupportedFields(SyncPostRequest)`, so that both callers share them. This is a pure refactor and
the existing `SyncPostApiTests` must stay green.

`{id}` binding: one helper, `SavedRequestId.TryParse(string, out long)`, accepts `^\d{1,18}$` with a value ≥ 1, or
`^SR-\d{1,18}$`. Anything else is `404` (Review Focus 3).

- [ ] **Step 1: Write the failing tests.**
  - Save: `Should_Return201WithLocation_When_N8nKeySavesGoodPayload`. The body has `status:"pending"` and
    `validation.state:"ok"`.
  - `Should_SaveWithNeedsAttention_When_KindUnknown` (201, `needsAttention:true`, error path `$.transactions[3].kind`)
  - `Should_SaveWithNeedsAttention_When_ControlTotalWrong`, and `…_When_DryRunPresent`
  - `Should_SaveWithUnavailableSnapshot_When_RulesFileMissing` (201, `validation.state:"unavailable"`,
    `needsAttention:false`)
  - `Should_Return400NotAJsonObject_When_BodyIsArrayOrText` (and nothing is stored)
  - `Should_Return413_When_BodyOverLimit`
  - `Should_Return200Existing_When_SameIdempotencyKeyAndBody`, and `Should_Return409_When_SameKeyDifferentBody`
  - `Should_FlagSamePayload_When_SavedTwiceWithoutKey`: the second has `duplicate.of` = the first's id
  - `Should_SetCreatedByFromKey_When_BodyCarriesCreatedBy` (SR-11)
  - `Should_Return503_When_DatabaseGateNotReady`
  - Read: `Should_ReturnDetailWithETagAndAllowedActions_When_EditorGets` (`ETag: "1"`,
    `allowedActions == ["edit","validate","post","discard"]`)
  - `Should_ReturnEmptyAllowedActions_When_ViewerGets`
  - `Should_ReturnExactBytes_When_GetOriginal` (Review Focus 2: an emoji/CJK fixture, `Content-Type`
    `application/json; charset=utf-8`)
  - `Should_Return404_When_IdIs(abc|-1|0|SR-|SR-99999999999999999999)` (Review Focus 3)
  - `Should_ResolveSameRecord_When_IdIsPublicRef`
  - `Should_FilterPageAndTotal_When_Listing`, and `Should_Return400_When_PageSizeOver100OrUnknownStatus`
  - `Should_ReturnCounts_When_Stats`
  - versions, attempts and events routes return their lists newest-first (one test each)
- [ ] **Step 2:** Run the filter `~SavedRequests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Read the raw body with `Request.EnableBuffering()` and a UTF-8 `StreamReader`, then
  parse with `JsonDocument.Parse` (max depth 64). `RequestSizeMiddleware` already enforces the 2 MB limit.
- [ ] **Step 4:** Run the full suite. Expected: PASS. `OpenApiDriftTests` is green.
- [ ] **Step 5:** Commit `T-1012: save and read routes for saved requests`.

---

### Task 13 (T-1013): Edit, re-validate, discard, restore

**Files:**
- Modify: `SavedRequestEndpoints.cs`, `openapi.json`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/EditApiTests.cs`, `LifecycleApiTests.cs`

**Interfaces:** Consumes `SavedRequestIntake.EditAsync`/`RevalidateAsync` and
`ISavedRequestStore.ChangeStatusAsync`.

- [ ] **Step 1: Write the failing tests.**
  - `Should_CreateVersion2WithChanges_When_EditorEditsPayee`: `changes[0].path == "$.transactions[3].payee"`,
    `updated.by.id` = the user id, and `ETag: "2"`.
  - `Should_Return428_When_IfMatchMissing`, and `Should_Return412WithLastEditor_When_IfMatchStale` (the body names
    `updatedBy` and `rowVersion`)
  - `Should_Return200NoNewVersion_When_OnlyNumberFormattingDiffers` (Review Focus 5)
  - `Should_Return409NotEditable_When_Posting` (claim it through the fake first)
  - `Should_KeepOriginalAndVersion1_When_EditedThreeTimes` (SR-2)
  - `Should_StoreUnreadableEdit_When_KindInvalid`, and `Should_Return400_When_EditPayloadNotObject`
  - `Should_NotRecomputeControlTotal_When_AmountEdited`: the snapshot reports the control-total mismatch, and the
    stored `controlTotal` is unchanged (rule 3)
  - `Should_ClearDuplicateFlag_When_EditMakesItUnique`
  - `Should_RefreshSnapshot_When_Revalidate` (`validatedUtc` advances)
  - `Should_RequireReason_When_Discard` (400 for empty or > 500 chars), and
    `Should_Discard_When_EditorWithReason`
  - `Should_Return403_When_EditorRestores`, and `Should_RestoreToPending_When_AdminRestores`
  - `Should_Return403_When_ViewerEditsOrDiscards`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `T-1013: edit, re-validate, discard and restore`.

---

### Task 14 (T-1014): Post by id

**Files:**
- Create: `src/QbAutopost.Api/SavedRequests/SavedRequestPoster.cs`
- Modify: `SavedRequestEndpoints.cs` (`POST /{id}/post`), `QuickBooksEndpoints.cs` (extract the status/body choice
  from `ValidateAndPostTransactions` into `internal static IResult SyncPostResponses.ToResult(SyncPost post, HttpContext http, IDictionary<string, object?>? extra)`
  so that both routes answer identically), `openapi.json`
- Fixtures: `tests/fixtures/sync-post/*.json`. Reuse the payloads the existing `SyncPostApiTests` use, moved into
  fixture files: posted, held, control-total refused, dryRun refused, partial, failed, and no-response.
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/PostApiTests.cs`, `EquivalenceTests.cs`

**Interfaces — Produces:**
```csharp
public sealed class SavedRequestPoster(ISavedRequestStore store, SyncPostRunner runner, IClock clock, AppSettings settings, ILogger<SavedRequestPoster> log) {
  Task<SavedPostOutcome> PostAsync(long id, int? expectedRowVersion, bool confirmDuplicate, Caller caller, CancellationToken ct);
}
public sealed record SavedPostOutcome(SavedPostKind Kind, SyncPost? Post, long? AttemptId, SavedRequest? Record, int? HttpStatus);
// Kind: Ran | NotFound | Stale | AlreadyPosting | AlreadyPosted | NotPostable | DuplicateUnconfirmed | Unreadable
```
Order inside `PostAsync`:
1. Load the record. If it is flagged as a duplicate and `confirmDuplicate` is false, return `DuplicateUnconfirmed`
   without claiming.
2. `TryClaimAsync`, committed.
3. `SavedPayloadReader.Read(current payload)`. If unreadable, complete the attempt with `Refused`/`Pending`/400 and
   return `Unreadable` with its path errors.
4. `runner.RunAsync(request, CancellationToken.None)`. The caller's token is not passed, as in validate-and-post
   §7. Wrap it in try/catch.
5. Map to a `PostEnding`. An exception before `TryRunExclusiveAsync` returned means `ErrorBeforeSend`. Detect this
   with a flag set by a wrapper around the exclusive work delegate: an exception thrown after the work delegate
   started means `ErrorAfterSend`.
6. `AttemptOutcomes.From` → `CompleteAttemptAsync`, with 3 tries at 0.5 s / 1 s / 2 s. If all three fail, write
   `{savedRequestId, attemptId, outcome, httpStatus, result}` to `<Paths:ApiBatches>/<postId>/saved-request.json`
   (or to `<ApiBatches>/saved-requests/<attemptId>.json` when there is no `postId`), and log `ERR` with
   `savedRequestId` and `postId`.
7. Store the duplicate acknowledgement (who/when) in the claim when `confirmDuplicate` is true.

Response: `SyncPostResponses.ToResult` with extra `{savedRequestId, attemptId, status}`. Not-ran kinds map to
`404`, `412`, `409 already-posting|already-posted|not-postable|duplicate-unconfirmed` (with `duplicate.of` and that
request's status), and `400` (unreadable, with `errors` as JSON paths).

- [ ] **Step 1: Write the failing tests.**
  - `EquivalenceTests` (SR-4, **the important one**). `[Theory]` over each readable fixture: one factory posts it
    through validate-and-post, a second fresh factory saves it and then posts by id. Assert an equal status code,
    an equal body after removing `savedRequestId`, `attemptId` and `status` (and normalising `postId`,
    `startedUtc` and `finishedUtc`), and an equal `FakeQbGateway` request list.
  - `Should_RecordPostedAttemptAndLock_When_Posted`: the attempt has `outcome: posted`, its `postId`, and
    `versionNumber`. A later edit gets `409`, and a later post gets `409 already-posted` (SR-7).
  - `Should_ReturnToPendingWithHeldAttempt_When_422`
  - `Should_PassRetryAfter_When_Busy` (hold the `JobQueue` lock from the test)
  - `Should_MarkUnknown_When_QuickBooksNoResponse` (the `FakeQbGateway` throws after the send)
  - `Should_MarkPartial_When_OneRowRejectedByG5`
  - `Should_PostOnlyOnce_When_TwoConcurrentPosts` (SR-6): exactly one attempt and one gateway session, and the
    other answer is `409 already-posting`.
  - `Should_Return409DuplicateUnconfirmed_When_FlaggedAndNoConfirm`. No attempt is created. With
    `confirmDuplicate:true`, the post goes through and records the acknowledgement.
  - `Should_Return412_When_IfMatchStale`
  - `Should_Return400WithPaths_When_CurrentVersionUnreadable`. An attempt with outcome `refused` is recorded.
  - `Should_FinishAndRecord_When_ClientDisconnects` (cancel the `HttpClient` call, then poll `GET` until it is
    not `posting`)
  - `Should_WriteEvidenceFileAndStayPosting_When_StoreFailsOnComplete` (Review Focus 4). Make
    `FakeSavedRequestStore.FailCompletes = 3`, post, and assert the file exists with the ids and the request is
    `posting`. Restarting the factory makes it `unknown`.
  - `Should_Return403_When_ViewerOrApiKeyPosts`
- [ ] **Step 2:** Run the filter `~PostApi|~Equivalence`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite **twice**. Expected: PASS both times (the concurrency tests must not be flaky).
- [ ] **Step 5:** Commit `T-1014: post a saved request by id through the validate-and-post engine`.

---

### Task 15 (T-1015): Resolve, and logging context

**Files:**
- Modify: `SavedRequestEndpoints.cs` (`POST /{id}/resolve`, `GET /{id}/attempts/{attemptId}` includes the ledger
  batch summary when `postId` is set, read through the existing batch store), `SavedRequestPoster.cs` and the
  endpoints (push `savedRequestId`/`attemptId`/`postId` with `LogContext`), `openapi.json`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/ResolveApiTests.cs`, `LoggingTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - `Should_MoveUnknownToPostedAndCopyPostId_When_AdminResolvesPosted`
  - `Should_MoveUnknownToPending_When_AdminResolvesNotPosted`
  - `Should_Return409_When_ResolvingPartialToNotPosted`
  - `Should_Return400_When_NoteMissing`, and `Should_Return403_When_EditorResolves`
  - `Should_StoreResolverAndNoteOnAttemptAndEvent`
  - `Should_ShowLedgerBatch_When_AttemptHasPostId`
  - `Should_CarrySavedRequestIdOnEveryLine_When_Posting` (`ListLogger`)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `T-1015: resolve unknown and partial posts; saved-request log context`.

---

### Task 16 (T-1016): Deploy, server checks, runbook **[server]**

**Files:**
- Create: `deploy/install-postgres.ps1` (installs pinned PostgreSQL 16 silently from a path or URL argument, as a
  service, `listen_addresses='127.0.0.1'`; creates the db `qbautopost` and roles `qbautopost_owner` and
  `qbautopost_app`; grants the app `SELECT, INSERT, UPDATE` on mutable tables, `SELECT, INSERT` only on
  `saved_request_versions`, `saved_request_events` and `user_audit`, and no `DELETE` anywhere; writes the generated
  passwords to machine environment variables `QBAUTOPOST__Database__ConnectionString` (app) and
  `QBAUTOPOST_MIGRATION_CONNECTION` (owner); generates `Auth:Jwt:SigningKey` into
  `QBAUTOPOST__Auth__Jwt__SigningKey` if it is absent)
- Create: `deploy/apply-migrations.ps1` (applies the idempotent script `deploy/migrations.sql` with `psql` and the
  owner connection; refuses when the app is running)
- Create: `deploy/backup-postgres.ps1` and the Task Scheduler registration in `install-postgres.ps1` (daily
  `pg_dump -Fc` to `<QuickBooks:BackupFolder>\postgres\qbautopost-yyyyMMdd.dump`, keeping 30)
- Modify: `deploy/start-all.ps1` (warn if the schema is not current), `scripts/qb-server-check.ps1` (service
  running, db reachable, newest dump < 36 h), `scripts/new-api-client.ps1` (`-Scopes requests:submit` documented
  for n8n), `docs/runbook.md` (install, migrate, back up, restore, bootstrap admin, switch n8n URL, withdraw n8n
  `qb:post` per Q-81)
- Create: `deploy/migrations.sql`, generated by
  `dotnet ef migrations script --idempotent -p src/QbAutopost.Data -o deploy/migrations.sql`
- Create: `docs/testing/T-1016.server-checklist.md`
- Test: `tests/QbAutopost.Data.Tests/MigrationScriptTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - `Should_MatchCheckedInScript_When_Regenerated` (regenerate in-process with `IMigrator.GenerateScript(idempotent: true)`
    and compare with `deploy/migrations.sql`, ignoring line endings)
  - `[PostgresFact] Should_RefuseUpdateOnVersions_When_ConnectedAsAppRole` (run the grant block of
    `install-postgres.ps1`, extracted to `deploy/grants.sql` and shared, then connect as `qbautopost_app`;
    `UPDATE saved_request_versions` throws `42501`)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Write the scripts and docs. Each server step goes in the checklist with its expected output.
- [ ] **Step 4:** Run `dotnet build -warnaserror` and `dotnet test`. Expected: green. Run
  `pwsh -NoProfile -Command "Invoke-ScriptAnalyzer deploy/*.ps1"` if it is installed. Otherwise, parse each script
  with `[scriptblock]::Create((Get-Content -Raw $f))`, which must not throw.
- [ ] **Step 5:** Set T-1016 to `ready-for-human` (never `done`), update the M10 milestone row, and post the final
  Session-log line.
- [ ] **Step 6:** Commit `T-1016: PostgreSQL install, migrations, backups and runbook for the server`.

---

## Acceptance (copied from spec §16; tick on completion)

- [ ] SR-1 · T-1012
- [ ] SR-2 · T-1005, T-1013
- [ ] SR-3 · T-1013
- [ ] SR-4 · T-1014
- [ ] SR-5 · T-1014
- [ ] SR-6 · T-1005, T-1007, T-1014
- [ ] SR-7 · T-1002, T-1014
- [ ] SR-8 · T-1008
- [ ] SR-9 · T-1004, T-1012, T-1014
- [ ] SR-10 · T-1009
- [ ] SR-11 · T-1012
- [ ] SR-12 · T-1008
- [ ] SR-13 · every task
- [ ] SR-14 · T-1001
