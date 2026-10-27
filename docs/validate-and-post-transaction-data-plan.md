# Plan — Validate and post transaction data (synchronous)

**Source spec**: `docs/validate-and-post-transaction-data.md`
**Decision record**: `docs/adr/0008-synchronous-validate-and-post.md` (status: proposed)
**Branch**: `m9-api-v1`
**Complexity**: **Medium** — 4 tasks, ~8 files, one new queue primitive, **no new packages**
**Status**: **done** 2026-09-24 — all four tasks, as T-921…T-924. Q-A is still unanswered; Task 3 was built to the
behaviour §5.1 states, because the spec states it and it posts less (rule 4), and the question is recorded as Q-74
rather than closed. Evidence: `docs/testing/T-921-T-924.tdd.md`. Two items of the acceptance list below are **not**
ticked: ADR-0008 is still `proposed`, and the server verification is outstanding.

---

## Summary

Add `POST /api/v1/quickbooks/transactions/validate-and-post`: no `dryRun`, no `202`, no pollable batch. It validates
the whole payload, returns `422` with every finding if anything would be held, and otherwise posts all rows and
returns the `TxnID`s in the same response. It takes the exclusive SDK lock only if free and answers `409` when not.

Almost nothing here is new logic. `DirectPlanner` already does the validation, `DirectPostRunner` already does the
post, and both are reused whole. The genuinely new parts are a non-blocking lock attempt, a request type without
`dryRun`, and an all-or-nothing gate between the two.

---

## Read this before Task 1 — a security trap

`ApiRoutes.ScopeFor` (`src/QbAutopost.Api/Endpoints/ApiRoutes.cs:150-158`) matches the posting route by **exact
string**:

```csharp
if (p == "/quickbooks/transactions")
{
    return get ? ApiScopes.QbRead : ApiScopes.QbPost;
}

return p.StartsWith("/quickbooks/", Ordinal) || p == "/qb/sync-lists"
    ? ApiScopes.QbRead
    : ApiScopes.Admin;
```

A new path `/quickbooks/transactions/validate-and-post` does **not** equal `/quickbooks/transactions`, so it falls
through to the `StartsWith("/quickbooks/")` branch and resolves to **`ApiScopes.QbRead`**.

**A route that posts real money would be gated behind the read scope.** Any caller holding `qb:read` — issued for
looking at lists and validating offline — could post. This is silent: the route works, tests pass, nothing warns.

It must be handled explicitly in Task 4, with its own test, and that test is the most important one in this plan.

Two related lists to get right at the same time:

- **`ReadOnlyPosts`** (`ApiRoutes.cs:167-174`) — the new route must **not** be added. It mutates, and `IsMutating`
  drives the audit trail and the posting rate-limit bucket.
- The **`/validate` sibling** is already in `ReadOnlyPosts`. Do not let an `EndsWith("/validate")` style shortcut
  catch `validate-and-post` — the name ends in `post`, but a careless `Contains("validate")` would misfile it.

---

## Patterns to mirror

| Category | Source | Pattern |
|---|---|---|
| Route registration | `Endpoints/QuickBooksEndpoints.cs:29-35` | `app.MapPost("/quickbooks/...", Handler)` on the v1 group |
| Scope resolution | `Endpoints/ApiRoutes.cs:150` | exact-path branch returning `ApiScopes.QbPost` |
| Request DTO | `Core/Api/DirectRequest.cs:9,48` | `sealed record`, `required` on mandatory fields, XML doc naming the FR |
| Refusing a decorative field | `Core/Api/DirectRequestReader.cs:29-44` (T-915) | add to `errors`, refuse the batch, cite the question |
| Validation | `Core/Api/DirectPlanner.cs:26` | `Plan(request, rules, lists, ledger, today, qbXmlVersion)` — reuse, never reimplement |
| Posting | `Api/Jobs/DirectPostRunner.cs:74-190` | backup guard → G4 live half → qbXML → SDK → G5 → ledger |
| Status derivation | `Api/Jobs/DirectPostRunner.cs:195,211` (T-919) | derive once from counts, never per-branch |
| Problem details | `Endpoints/QuickBooksEndpoints.cs:60-77` | `Results.Problem(statusCode, title, detail, extensions)` |
| Typed error → status | `Api/Configuration/RulesFileExceptionHandler.cs` (T-920) | `IExceptionHandler`, return `false` for anything not ours |
| Exclusive work | `Api/Jobs/JobQueue.cs:62` | `RunExclusiveAsync(label, work, ct)` — **extend, do not copy** |
| Tests | `Api.Tests/Api/DirectPostApiTests.cs` | `ApiFactory` + `FakeQbGateway`; `Should_<behaviour>_When_<condition>` |
| Host overrides | `Api.Tests/TestSupport/ApiFactory.cs:52-57` | `WithSetting` / `WithSettings` |

No pattern exists for **a non-blocking attempt at the worker lock**. `JobQueue` only enqueues. Task 1 writes it.

---

## Files to change

| File | Action | Why |
|---|---|---|
| `src/QbAutopost.Api/Jobs/JobQueue.cs` | UPDATE | add `TryRunExclusiveAsync` |
| `src/QbAutopost.Core/Api/SyncPostRequest.cs` | CREATE | request record with **no** `dryRun` |
| `src/QbAutopost.Core/Api/SyncPostResult.cs` | CREATE | `200` body (§6.1) and the `422` findings (§6.2) |
| `src/QbAutopost.Api/Jobs/SyncPostRunner.cs` | CREATE | all-or-nothing gate, then reuse the post path |
| `src/QbAutopost.Api/Endpoints/QuickBooksEndpoints.cs` | UPDATE | the route + handler |
| `src/QbAutopost.Api/Endpoints/ApiRoutes.cs` | UPDATE | **`qb:post` for the new path** (see the trap above) |
| `src/QbAutopost.Api/wwwroot/openapi.json` | UPDATE | `OpenApiDriftTests` fails otherwise |
| `tests/QbAutopost.Api.Tests/Api/SyncPostApiTests.cs` | CREATE | VP-1…VP-9 |
| `tests/QbAutopost.Api.Tests/Jobs/JobQueueTryExclusiveTests.cs` | CREATE | the busy path |
| `tests/QbAutopost.Api.Tests/Routing/*` | UPDATE | scope assertion for the new route |
| `docs/tracker.md` | UPDATE | rows T-921…T-924, Q-A…Q-D, session log |

---

## Tasks

### Task 1 — `JobQueue.TryRunExclusiveAsync` *(suggested T-921)*

- **Action**: run the work immediately if the worker is idle; return "busy" **without enqueuing** if it is not.
  Signature along the lines of
  `Task<(bool Ran, T? Result)> TryRunExclusiveAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken ct)`.
  Reuse the existing `TaskCompletionSource` shape; the only new behaviour is the idle check and the early return.
- **Mirror**: `JobQueue.cs:62`.
- **Watch**: the idle check and the claim must be one atomic step. A check-then-enqueue lets two requests both see
  "idle" and both post — which is the exact collision ADR-0006 forbids. This is the subtlest code in the plan.
- **Validate**: a test where the lock is held by a slow first call and a second call returns busy while the gateway
  records exactly one session. `dotnet test --filter JobQueueTryExclusiveTests`.

### Task 2 — request and result types *(suggested T-922)*

- **Action**: `SyncPostRequest` = `DirectRequest` **minus** `DryRun`, plus `bool? DryRun` declared *only* so a caller
  sending it can be refused (`null` = absent → allowed; any value → `400 dry-run-not-supported`). Same for
  `AllowModelAccounts`, following T-915. `SyncPostResult` carries §6.1's shape.
- **Mirror**: `Core/Api/DirectRequest.cs`; the refusal wording of `DirectRequestReader.cs:29-44`.
- **Watch**: `DecorativeSurfaceTests` (T-916/T-917) scans caller-facing request records for fields nothing reads.
  A `DryRun` that exists only to be refused **is** read — by the refusal — so it should pass, but run that test
  early rather than discovering it at the end.
- **Validate**: `dotnet test --filter DecorativeSurfaceTests` plus a unit test for the refusal.

### Task 3 — `SyncPostRunner` *(suggested T-923)* — **blocked on Q-A**

- **Action**: `Plan` via `DirectPlanner`. If `plan.Errors` any → `400`. **If any row is not `WouldPost` → return the
  findings, post nothing, touch no SDK.** Otherwise persist the request (rule 7) and run the post half of
  `DirectPostRunner` unchanged. Write the ledger row.
- **Mirror**: `DirectPostRunner.cs:74-190`; derive status once as `DirectPostRunner.cs:211`.
- **Watch**: do not duplicate the gate logic. If reuse needs `DirectPostRunner.RunAsync` split into "plan" and
  "send" halves, do that refactor **in this task and with the existing `DirectPostApiTests` green**, so FR-A-8's
  behaviour is provably unchanged.
- **Validate**: a test asserting `FakeQbGateway` received **nothing** when one row of many is held (VP-2). That
  single assertion is the heart of the feature.

### Task 4 — endpoint, scope, OpenAPI, tests *(suggested T-924)*

- **Action**: map the route; add the **exact-path `qb:post` branch** to `ScopeFor`; update `openapi.json`; write
  VP-1…VP-9.
- **Mirror**: `QuickBooksEndpoints.cs:29-35`, `ApiRoutes.cs:150`.
- **Validate**: a test that a `qb:read`-only client gets **403** on this route — the trap above, pinned. Plus
  `dotnet test --filter OpenApiDriftTests`.

---

## Validation

```bash
dotnet build -warnaserror
dotnet test
dotnet test --filter "FullyQualifiedName~SyncPostApiTests"
dotnet test --filter "FullyQualifiedName~DirectPostApiTests"   # FR-A-8 must be untouched
dotnet test --filter "FullyQualifiedName~OpenApiDriftTests"
dotnet test --filter "FullyQualifiedName~DecorativeSurfaceTests"
```

Expected after Task 4: **Core 866 unchanged**, **Api 513 + ~12**. A drop in either number means something was
weakened rather than added.

Then, by hand on the QuickBooks server (`[server]`, so the row goes `ready-for-human`):

```bash
curl -i -X POST "http://127.0.0.1:5081/api/v1/quickbooks/transactions/validate-and-post" \
  -H "X-Api-Key: <key>" -H "Content-Type: application/json" \
  -H "Idempotency-Key: zemen-2019-12-attempt-1" \
  --data-binary @samples/request.json
```

---

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **New route inherits `qb:read`** and a read-only key can post money | **High** if unhandled — it is the default behaviour | Explicit `ScopeFor` branch + a 403 test (Task 4). Called out above because it is silent |
| Racy idle check lets two posts run at once | Medium | Atomic claim in Task 1; concurrency test with the gateway counting sessions |
| All-or-nothing refuses 133 good rows over one payee | **High** (it is the design) | Q-A; `/transactions/validate` finds them offline first |
| Splitting `DirectPostRunner` regresses FR-A-8 | Medium | Refactor only with `DirectPostApiTests` green before and after; no test edited |
| Long post holds the connection; client times out and retries | **High** in real use | `Idempotency-Key` (Q-C), G4, ledger row; documented in spec §8 |
| Client disconnects mid-post | Medium | Post runs to completion by design; caller reconciles by `externalId` |
| `openapi.json` drift | Low | `OpenApiDriftTests` catches it; update in the same task |

---

## Acceptance

- [ ] VP-1…VP-10 from the spec all covered by tests
- [ ] A `qb:read`-only client receives **403** on the new route
- [ ] `FakeQbGateway` receives **nothing** when any row is held
- [ ] Two concurrent requests: one posts, one gets `409`, gateway sees one session
- [ ] `DirectPostApiTests` unchanged and green — FR-A-8 untouched
- [ ] `dotnet build -warnaserror` 0 warnings; `dotnet test` green twice consecutively
- [ ] Tracker rows T-921…T-924 with Files and Verified by; Q-A…Q-D recorded; session-log line
- [ ] Evidence file per task under `docs/testing/`
- [ ] ADR-0008 moved `proposed` → `accepted` once Q-A and Q-D are answered
- [ ] Row set to `ready-for-human` for server verification — no test touches a real QuickBooks (rule 1)

---

## Open questions gating this plan

| # | Question | Blocks |
|---|---|---|
| **Q-A** | All-or-nothing, or FR-A-8's per-row holds? | **Task 3** — it is the core behaviour |
| **Q-B** | Documented client timeout for a full batch? | docs only; needs a real measurement |
| **Q-C** | Is `Idempotency-Key` mandatory here? | Task 4 if yes (a `400` branch) |
| **Q-D** | `409` immediately, or a short bounded wait? | Task 1 |

Nothing in Tasks 1, 2 or 4 depends on Q-A, so work can start before it is answered — but Task 3 should not.
