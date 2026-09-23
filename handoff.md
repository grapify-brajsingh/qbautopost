# Session Handoff — QbAutopost

**This is an unattended relay. The owner is away and will answer nothing mid-run.**
Written: 2026-09-23 · **M9 (API v1): 14 of 14 done and the final report written**, branch `m9-api-v1`
· **There is nothing left for an agent. The relay is over.**

> **If you are an agent starting fresh: there is no task for you here.** §3 is empty. Everything that remains is in
> §10 — server verification on the QuickBooks machine — and it needs a person, not an agent. Read
> `docs/M9-COMPLETE.md` (§3 consolidates every server and owner item, §4 is the honest list of what has never run
> against a real QuickBooks) and then stop. Do **not** start M10; the owner decided the run stops at the end of M9.

---

## 0. The relay protocol

One agent does **one task**, then hands over to a fresh agent with empty context. State travels only through the
repository: `handoff.md`, `docs/tracker.md`, and the commits.

**Your loop, in order:**

1. **Read** `CLAUDE.md`, this file, `docs/spec-api-v1.md` (the M9 contract), and the tracker row for your task.
2. **Confirm the task.** §3 names exactly one as `NEXT`. That is yours. If it is marked `BLOCKED`, see §4.
3. **Work test-first.** Write the failing test, *run it*, paste the real RED output. Then implement. Then GREEN.
   A RED you did not actually run is a lie in the evidence file, and nobody is here to catch it.
4. **Verify**: `dotnet build -warnaserror` (0 warnings) and `dotnet test` **twice in a row**, both green.
5. **Write the evidence**: `docs/testing/T-9xx.tdd.md`, including its "what this does not prove" section. Be honest
   there. It is the only record of which claims are actually tested.
6. **Update `docs/tracker.md`**: the task row (Status, Files, Verified by, Notes), a Session-log line, and any new
   question in the Questions table.
7. **Update this file**: move `NEXT` to the following task, fill in §2 (state), add anything the next agent would
   be hurt by not knowing to §6 (traps) or §7 (what changed underneath you).
8. **Commit** `T-9xx: <summary>` and **push**: `git push`. The upstream is already set and credentials are cached
   (session 15 pushed successfully), so a push failure means something real — report it, do not retry blindly.
9. **Hand over.** Your last output is the handover note in §8's format, and nothing else. Then stop.

**Why one task per agent:** each handover is only as trustworthy as it is small. An agent that did four tasks
writes a summary nobody can check; an agent that did one writes a summary the next one can verify in a minute.

---

## 1. Hard rules for running unattended

The usual rules (`CLAUDE.md`) all still apply. These matter more when nobody is watching:

1. **Hold, don't guess** (`CLAUDE.md` rule 4) is now the primary safety mechanism. If the spec is silent or two
   specs disagree, choose the behaviour that **posts less / reveals less / refuses sooner**, mark it
   `// SPEC-GAP T-9xx`, and add a Questions row. Never pick the permissive reading because it unblocks you.
2. **Never weaken a test to make it pass.** If an existing test fails, read it and decide whether the *test* or the
   *code* is wrong, and write down which and why. Session 15 changed 18 tests deliberately and listed every one in
   `docs/testing/T-910.tdd.md` §5 — that is the standard.
3. **Never touch a real QuickBooks or Hermes.** `FakeQbGateway`, `FakeHermesClient`, `FakeQbSdkProbe`. If a task
   cannot be proven without the real SDK, mark it `ready-for-human` and say exactly what a person must check.
4. **Never add a NuGet package** beyond `CLAUDE.md`'s list (which now includes `Scalar.AspNetCore`). If you want
   another, stop the task and record the question.
5. **Never push to `main`, never open a PR, never merge.** Push `m9-api-v1` only.
6. **Never delete or overwrite anything outside the repo and the temp folders.** If a test writes outside its
   `TempDir`, that is a bug in the test — see trap #4.
7. **Never claim a verification you did not run.** Paste real output. "Tests pass" without a count is not evidence.
8. **If you are stuck twice on the same thing, stop.** Record the state honestly and hand over. A confused agent
   doing a third attempt unattended is how money logic gets broken.

---

## 2. State

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch **`m9-api-v1`**, tracking `origin/m9-api-v1` (pushed 2026-09-23; credentials cached, so `git push` works unattended) |
| Build | `dotnet build -warnaserror` → **Build succeeded. 0 Warning(s), 0 Error(s)** — measured 2026-09-23 after the final-report commit |
| Tests | `dotnet test` → **Core 866 passed · Api 492 passed** (1358 total) — measured 2026-09-23 on Windows after T-916, twice in a row |
| Milestones | M0–M7 done; M8 agent work done (`ready-for-human`); **M9 complete: 14/14 rows (13 `done`, T-903 `ready-for-human`) and `docs/M9-COMPLETE.md` written** |
| Packages | **7**: the six plus `Scalar.AspNetCore` **2.13.13**, added by T-913 under Q-46. That is the whole allowance, and nothing remaining needs a package |
| Owner | **Away. Answers nothing.** 69 questions outstanding — Q-1…Q-45, Q-47…Q-61, Q-63…Q-71 — each with a conservative behaviour, all listed with that behaviour in `docs/M9-COMPLETE.md` §2. **Answered: Q-0, Q-46 (owner, yes), Q-62 (by T-912)** |

## 3. The task queue

**Empty. There is no task here for an agent.**

| Task | Status | One line |
|---|---|---|
| **T-915** | **done** (after the close-out) | `allowModelAccounts` / `qb:post:ai` were documented, scoped and **read by nothing**. Now refused with 400 from `DirectRequestReader`, so validate and post agree. Deliberately *not* implemented — Q-51 is unanswered and a model must not choose where money lands. `docs/spec-api-v1.md` §6.1 is knowingly out of step now: **Q-71** |
| **T-916** | **done** (after the close-out) | The guard T-915 said was missing: no request field or scope may be decorative. `Core.Tests/Architecture/DecorativeSurfaceTests.cs`, no production file touched. A no-op now costs writing your name into `ReservedScopes` with a tracker question beside it |
| T-901…T-914 | **done** (T-903 `ready-for-human`) | Routes, health, SDK probe, connection test, company-file validate, direct model, validate, post, idempotency, clients+scopes, transport+limits+input hardening, audit trail + `requestId`, OpenAPI document + Scalar reference, the list/validate routes + the documentation catch-up |
| The final report | **done** | `docs/M9-COMPLETE.md` — routes, the open questions with today's behaviour (68 at the time of writing; T-915 later added Q-71), everything needing the owner or the server, and what has never met a real QuickBooks |
| *(nothing follows)* | **STOP** | Do not start M10 (owner decision, Decisions table). Do not attempt or claim the §10 server tasks |

## 4. What is left, and who does it

**The remaining work is the §10 server verification, and it needs a person at the QuickBooks machine.** No agent can
do it, and no agent may claim it. It is: T-903's 8-item SDK checklist, T-609's round trip on a copy of the company
file, T-802's deploy and auto-start dry run, T-803's shadow week, T-804's go-live, T-806's POC company and IIF
import, the new direct-post checklist (dry run → one real batch → undo it), and the TLS/transport checks T-911 could
not make (no certificate has ever been loaded, no HSTS header ever emitted, `AddServerHeader` unverified on a real
socket, no production rate limit has ever throttled anything).

All of it, with the owner decisions that have to land first, is consolidated in **`docs/M9-COMPLETE.md` §3**, and
what has never run against a real QuickBooks — all of T-907…T-914, plus T-903's COM probe — is **§4** of that file.
Start there rather than reassembling it from the tracker.

*(Route policy — `IsHealth`, `IsIdempotent`, `IsMutating`, `IsReference`, `ScopeFor` — all lives in
`Api/Endpoints/ApiRoutes.cs`; startup rules live in `Api/Security/TransportGuard.cs` as pure functions.)*

## 5. Before you finish — the checklist

- [ ] RED output pasted in `docs/testing/T-9xx.tdd.md`, from a run you actually did
- [ ] `dotnet build -warnaserror` → 0 warnings
- [ ] `dotnet test` twice, both green, counts recorded
- [ ] `ls C:\qb-autopost` is **empty** (trap #4 — tests must not write to the machine's data folder)
- [ ] Tracker: task row + session-log line + any new question
- [ ] This file: `NEXT` moved, §2 updated, traps added
- [ ] Commit `T-9xx: <summary>` with the Co-Authored-By trailer, then `git push`
- [ ] Handover note written in §8's format

## 6. Traps this codebase has already sprung

1. **A GateGuard hook blocks the first Bash command and the first edit/create of every file** until you restate
   facts (callers, affected API, data shapes, the user's verbatim instruction). Not a failure; answer and retry.
2. **Test namespaces shadow production ones.** `…Tests.Endpoints` broke four files because inside `…Tests.Api` the
   name `Endpoints.JobView` resolved to the test namespace. Use a name production does not have.
3. **`ApiFactory` must substitute every external double** — `QbConnection`, `IHermesClient`, `IQbSdkProbe`.
4. **The test host inherits the shipped `appsettings.json`**, and this has bitten twice. T-908 added
   `Paths:ApiBatches` there and five tests immediately wrote real batch folders into `C:\qb-autopost`. **Any new
   path setting must also be pinned in `ApiFactory`.** T-911 adds `Paths:AllowedJobRoots` — pin it.
5. **`JsonNamingPolicy.CamelCase` lowercases only the first character.** Pin wire names with `JsonPropertyName`.
6. **`ChannelReader.Count` throws** on a `SingleReader` unbounded channel; `JobQueue.Depth` is an `Interlocked` counter.
7. **`CoreIsolationTests` scans Core sources for COM tokens**, including the literal `QBXMLRP2`.
8. **Windows-only types cannot be referenced from tests** (`CA1416` as an error).
9. `PipelineOptions` has **required** members; `QuickBooksCallException` takes `(message, errorCode)`.
10. PowerShell 5.1 only (no `pwsh`), no `python`, scripts must be ASCII (`DeployScriptsTests` enforces it).
11. **Platform-dependent APIs fail on CI, not here.** T-908's batch-id guard used `Path.GetInvalidFileNameChars()`,
    which permits `\` and `:` on Linux, so `C:\windows\system32` would have passed. Prefer an explicit allow-list.
    **This matters for T-911's path allow-list too.**
12. **A hanging test costs two minutes, not a failure.** T-909's in-progress test wedges the gateway and pins a 2 s
    `Api:SyncPostTimeoutSeconds` so a regression fails fast. Do the same for anything that waits.
13. **`WithSetting(...)` returns `WebApplicationFactory<Program>`, not `ApiFactory`** — no `CreateAuthorizedClient()`
    on it; create the client and add the header.
14. **Rate limiting will break existing tests** (T-911). 1164 tests hammer these routes. ~~Expect to~~ **Done**: the
    limiter is `Api:RateLimits:Enabled`, default **true**, and `ApiFactory` pins it **false**. If you add a test that
    needs it, turn it on with `WithSettings` and use tiny limits. Never raise a shipped limit to make a test pass.
15. **`ApiFactory.WithSettings(IDictionary)` now exists** beside `WithSetting(key, value)` — use it when you need
    more than one override. Both still return `WebApplicationFactory<Program>`, so trap 13 is unchanged.
16. **Four middlewares now sit in front of `ApiKeyMiddleware`**, in this order: request logging,
    `SecurityHeadersMiddleware` (FR-A-14 headers, so a 401 carries them too), `RequestSizeMiddleware` (FR-A-17 body
    cap, before T-909's body buffering), then the key check, then `UseRateLimiter`, then `IdempotencyMiddleware`.
    **Anything that needs the caller's identity goes after the key check** — that is where the limiter had to go.
17. **A 401 never reaches the rate limiter**, because the key check short-circuits first. That is why the
    brute-force brake (`Api/Security/AuthBrake.cs`) is separate and lives inside `ApiKeyMiddleware`. If T-912's audit
    must record refused requests, remember it has the same problem: the audit middleware cannot sit behind a check
    that returns early.
18. ~~**`Q-62` is an unexplained single test failure**~~ **Answered in session 17** — it was teardown, not the
    application: `TempDir.Dispose` deleted its folder while Serilog still held `qbautopost-yyyyMMdd.log`, so a test
    that had already passed failed in its own `Dispose`. `TempDir.Dispose` now retries for a second. If you see a
    *different* one-off failure, still capture the name before re-running.
19. **The audit middleware sits in FRONT of `ApiKeyMiddleware`** (see trap 17 for why) and **buffers the response**
    of every mutating request so it can read `batchId`/`counts` back out of it. Two middlewares now buffer the
    response on a post: this one and `IdempotencyMiddleware` inside it. If you add a route that streams a body
    rather than returning JSON, it will be buffered — add it to `ApiRoutes.ReadOnlyPosts` or teach the audit to skip
    it, and say so in the tracker.
20. **The log output template now has three columns, not one**: `[{jobId}] [{requestId}] [{clientId}]`. A test that
    greps a whole bracketed column (`LoggingApiTests` does, for `[2026-08-tropicana]`) still works; a test that
    matched the old prefix character-for-character would not. Nothing in the suite did.
21. **`Paths:Logs` now holds two kinds of file**: `qbautopost-*.log` and `audit-*.jsonl`. Anything that enumerates
    that folder must filter — `AuditLog.Sweep` deletes only files it could have written itself, and `LoggingApiTests`
    filters by prefix.
22. **A new `/api/v1` route now breaks the build until it is documented.** `OpenApiDriftTests` walks
    `EndpointDataSource`; add a route and `wwwroot/openapi.json` needs a matching operation with a summary, a 2xx
    response schema and an `x-required-scope` equal to `ApiRoutes.ScopeFor`. This is deliberate (api-v1 §11.6), not
    an obstacle to route around: do not exclude your route from the test.
23. **The reference page cannot be opened in a plain browser.** The key travels in the `X-Api-Key` header and the
    page needs `health:read`, so a browser address bar gets a 401 — for the page *and* for its script. Read it with
    a client that sets the header (Q-66 records this and the one-line change that would relax it).
24. **`Prefer: respond-async`, `Idempotency-Key`, query parameters and the rest of the request surface are now
    written down** in `wwwroot/openapi.json`. When you are about to grep the endpoint files to find out what a route
    accepts, read that document first — and if it turns out to be wrong, the drift test did not catch it (it checks
    routes, scopes and shape, never a schema's field names: see §8.3 of `docs/testing/T-913.tdd.md`).

25. **Five operator files are now checked by a test.** `Core.Tests/Architecture/OperatorDocsTests.cs` reads
    `docs/runbook.md`, `steps.md`, `samples/poc/README-POC.md`, `scripts/qb-server-check.ps1` and
    `deploy/start-all.ps1` and fails when a route is named without `/api/v1` in front of it, when any of them
    contains "no key needed" / "No API key needed" / "needs no key for /health", or when one of seven M9 subjects
    is missing from the runbook. **If you write `/jobs` or `/health/…` in one of those files, the build goes red.**
    Write the versioned path, or word the sentence without a leading slash (the runbook does this once, on purpose,
    where it describes the legacy surface itself).
26. **`deploy/start-all.ps1` may never hold a key.** `DeployScriptsTests.Should_NotTouchSecrets_When_Reading…`
    forbids the string `ApiKey` (case-insensitive) anywhere in it, because it runs unattended at logon. That is why
    it waits on `/api/v1/health/ready` and not on `/api/v1/health/hermes`. Do not "fix" it by adding a parameter.

## 7d. What changed underneath you in session 19 (T-914)

1. **Three routes were added**: `GET /api/v1/quickbooks/lists` (reads `qb-lists.json`, never opens a session),
   `POST /api/v1/quickbooks/lists/sync` (the v1 spelling of `/qb/sync-lists`, **same handler**), and
   `POST /api/v1/jobs/validate` (FR-A-7). `ApiRoutes` needed no change at all: `ScopeFor` already mapped
   `/quickbooks/*` → `qb:read` and `/jobs/…/validate` → `jobs:read`, and `ReadOnlyPosts` already listed
   `/jobs/validate`. That is T-910's and T-912's "closed by default" design paying off — but it also means a new
   route can be live and scoped before anybody has thought about it, so check `ScopeFor` when you add one.
2. **`/qb/sync-lists` was kept, not renamed away.** Both spellings answer. The OpenAPI document marks the old one
   `deprecated` with operationId `syncListsFlat`. **Q-69** asks when it goes; it is a *versioned* path, so Q-53
   (the flat paths) does not cover it.
3. **`FolderReader.Read` has a second parameter**, `createOutput = true`. Only the validator passes `false`. If you
   add a caller that must not touch the folder, pass it.
4. **G1 moved out of `JobPipeline` into `Core/Pipeline/StatementCheck.cs`** (`Reconcile(parsed, file)`), verbatim.
   Both the job and the folder validate call it. **Change it and you change both** — that is deliberate, and the
   reason the validate can be trusted to predict the job.
5. **`docs/spec.md` §6 is no longer stale.** It now carries the v1 table with scopes, and a banner saying
   `docs/spec-api-v1.md` wins. §12 gained a paragraph listing the M9 settings and pointing at the runbook.
6. **`samples/poc/appsettings.json` gained every M9 setting** with the shipped defaults. `scripts/build-poc-package.ps1`
   copies it over `app\appsettings.json` on the server, so a value you put there reaches the POC machine.
7. **The runbook has three new sections** — §3.4 (keys and scopes, the scope table, turning `AllowLegacyKey` off),
   §3.5 (job roots, rate limits, the 429 and its `Retry-After`, the wrong-key brake, body/field caps) and §3.6 (the
   reference page) — plus §8.7 (the audit file). `OperatorDocsTests` keeps the subjects present; only a person can
   keep them true.

## 7c. What changed underneath you in session 18 (T-913)

1. **`ApiRoutes` has a fifth question, `IsReference`,** and `ScopeFor` asks it **before** it strips the `/api/v1`
   prefix — the documentation exists only on the versioned surface. It returns `health:read` for
   `/api/v1/openapi.json`, `/api/v1/reference` and everything Scalar serves under that prefix.
2. **Two routes exist only when `Api:Reference:Enabled` is true**, and the setting ships **false**
   (`appsettings.Development.json` sets it true). Disabled means **not mapped**: the answer is 404, not 403. If you
   add a test that reads the document, turn it on with `WithSetting("Api:Reference:Enabled", "true")` — and
   remember trap 13, that factory has no `CreateAuthorizedClient()`.
3. **`src/QbAutopost.Api/wwwroot/openapi.json` is hand-authored and load-bearing.** A route added without an entry
   there **fails the build** (`OpenApiDriftTests`), and so does an entry with no route, an operation with no summary
   or no 2xx response schema, and an `x-required-scope` that does not **equal** `ApiRoutes.ScopeFor`. That is the
   point of it: the document cannot quietly go stale. The csproj has a `Content Update` line that copies it beside
   the executable; if you move the file, move that line.
4. **`Authorization: Bearer` does not work and never did** (**Q-67**). api-v1 §2.2 calls it the preferred form;
   `ApiKeyMiddleware` reads `X-Api-Key` and nothing else, so a bearer request is a `401`. T-913 did **not** change
   that — it made the document say so. If T-914 writes `Bearer` into the runbook, the runbook will be wrong.
5. **New settings: `Api:Reference:{Enabled,AllowTryIt,UseCdn}`**, all false, all in `appsettings.json`. They are not
   paths, so trap 4 does not apply.
6. **`Scalar.AspNetCore` 2.13.13 is the seventh and last package.** Its `CdnUrl` property is `[Obsolete]`, which
   `-warnaserror` turns into a build failure — `BundleUrl` is the one to use. Its assets are embedded, so the page
   fetches nothing from the internet; `Telemetry` and `DefaultFonts` are turned **off** in code because both would.

## 7b. What changed underneath you in session 17 (T-912)

1. **Every request now has an id before anything else runs.** `RequestIdMiddleware` is the **first** middleware,
   ahead of `UseExceptionHandler`. It sets `X-Request-Id` through `OnStarting`, pushes `requestId` onto Serilog's
   `LogContext`, and `Program.cs`'s `AddProblemDetails` adds it to every problem detail. If you add a middleware in
   front of it, those three stop being true for whatever it answers.
2. **`ApiKeyMiddleware` pushes `clientId` onto `LogContext`** as soon as a caller resolves — before the CIDR and
   scope checks, so the two 403 log lines name the client. The per-request line gets it instead from
   `RequestLog.EnrichDiagnosticContext`, because that line is written after the push has gone.
3. **`ApiRoutes.IsMutating`** is the new route question, and `ApiRoutes.ReadOnlyPosts` is the list of POSTs that
   change nothing (`…/transactions/validate`, `…/connection/test`, `…/company-file/validate`, `/jobs/validate`). An
   unlisted route **is** audited — closed by default, like `ScopeFor`.
4. **New config: `Api:AuditRetentionDays`** (400) in `appsettings.json`. Zero or less means *keep everything*, not
   *keep nothing*. `AuditLog` is a singleton built from `Paths:Logs`, which `ApiFactory` already pins, so trap 4
   does not bite — but if you add a path setting of your own, pin it.
5. **`TempDir.Dispose` retries.** See trap 18. It is the only existing test file this session touched, and only its
   teardown.
6. **Two new SPEC-GAPs, Q-64 and Q-65**, both about what FR-A-16 leaves undefined: an unknown key is not audited,
   and `outcome`/`totalAmount` were given meanings (`totalAmount` = `totals.posted`, the money that moved).

## 7a. What changed underneath you in session 16 (T-911)

1. **The app can now refuse to start.** `TransportGuard.Require` throws on: a non-loopback bind without TLS (unless
   `Api:AllowInsecureRemote`), a CORS `*`, or a non-loopback bind with an empty `Paths:AllowedJobRoots`. The shipped
   bind is still `http://127.0.0.1:5080`, so nothing changes today — but **do not add a setting that makes the bind
   remote without reading §4 of `docs/testing/T-911.tdd.md` first.**
2. **`Paths:AllowedJobRoots` and `QuickBooks:AllowedCompanyFolders` exist and ship empty**, and empty means
   *unconfigured* = unrestricted (Q-63). `Core/Security/PathAllowList.IsInside` is the one place that decides;
   reuse it rather than writing a second path comparison.
3. **`DirectRequestReader` now rejects over-long fields** (`memo` 4096, names 255) as whole-batch `400`s. If you add
   a field to `DirectRow`, add it to that bounds list or it is unbounded.
4. **`ApiKeyMiddleware.InvokeAsync` gained an `AuthBrake` parameter** and now answers `429` before comparing a key
   from a blocked address. It records a *success* as soon as a key resolves — before the CIDR and scope checks — so
   a 403 never counts as a guess.
5. **New config, all in `appsettings.json` except one**: `Api:AllowInsecureRemote`, `Api:MaxRequestBodyBytes`,
   `Api:Tls:{PfxPath,StoreThumbprint}`, `Api:Cors:AllowedOrigins`, `Api:RateLimits:*`,
   `QuickBooks:AllowedCompanyFolders`, `Paths:AllowedJobRoots`. **`Api:Tls:PfxPassword` is deliberately absent from
   the file** — it is environment-only, and the guard warns (naming the file, never the value) if it is found there.

## 7. What changed underneath you in session 15

1. **Health routes are no longer all open.** api-v1 §2.2 narrows spec.md §6: only `/health` and `/health/ready`
   need no key; `/health/sdk`, `/health/quickbooks`, `/health/hermes` need `health:read`. This moved 18 tests.
   **Q-61** records that the owner's monitoring must be told. `docs/spec.md` §6 is now the stale one — if that is
   ever reversed, edit the spec, do not quietly change the code.
2. **`ApiKeyMiddleware` is identity, not a string compare.** It resolves a caller from `clients.json`, checks CIDR,
   then checks `ApiRoutes.ScopeFor`. Anything you add to the pipeline goes **after** it so you can read
   `ApiKeyMiddleware.ClientOf(context)` — which is what T-911's per-client limits and T-912's audit both need.
3. **`ApiRoutes` now owns three route questions**: `IsHealth`, `IsIdempotent`, `ScopeFor`. Add route policy there,
   not scattered through middleware.
4. **`Paths:ApiBatches` holds per-batch evidence and `idempotency.json`.** It grows without bound (Q-52).

## 8. Handover note format

End your session with exactly this, and nothing after it:

```
TASK DONE: T-9xx — <one line>
TESTS: Core <n>, Api <n>, green twice. Build: 0 warnings.
PUSHED: <commit sha> to origin/m9-api-v1
NEXT: T-9xx (see handoff.md §3)
BLOCKED/NEW QUESTIONS: <Q-nn one-liners, or "none">
FOR THE HUMAN: <anything the owner must decide or do on the server, or "nothing">
```

If you could **not** finish, say so plainly instead:

```
TASK NOT DONE: T-9xx — stopped at <what> because <why>
STATE: <what is committed, what is half-done, what is safe to delete>
NEXT AGENT SHOULD: <the single next action>
```

## 9. The final report — **DONE, 2026-09-23**

`docs/M9-COMPLETE.md` is written. It contains all four parts asked for below, plus the two session-19 findings
(Q-61 and Q-67) in its §3.6 "decisions the owner has to make before this build is deployed". Nothing here is
outstanding; the brief is kept for the record.

*(Original brief.)* T-914 is done, so this is the whole remaining job. Write `docs/M9-COMPLETE.md` with: what M9 delivered route by
route; the full list of open questions (**Q-1…Q-70**, minus the answered Q-46 and Q-62) with the conservative
behaviour each one currently has; **everything that needs the owner or the server**, including the T-903
checklist, T-802/803/804, and a new direct-post server checklist (dry run → one real batch → undo it); and an
honest list of what is implemented but never run against a real QuickBooks — which, today, is all of
T-907…T-914.

Two things session 19 found that belong in the report's "needs the owner" part, because they are decisions, not
bugs:

- **Q-61 is now urgent, not theoretical.** Any monitoring pointed at `/health/quickbooks`, `/health/sdk` or
  `/health/hermes` starts getting `401` on this upgrade. The runbook now tells the operator to point it at
  `/api/v1/health/ready` instead, but somebody has to actually do that before the upgrade.
- **Q-67**: `Authorization: Bearer` does not work and never did, though api-v1 §2.2 calls it preferred. Either the
  middleware grows a second header or the spec is corrected; the document and the runbook currently say what the
  code does.

This report is text only. Do not change code to make it tidier.

## 10. What no agent can do

The server-verification work needs a person at the QuickBooks machine and **must not be attempted or claimed**:
T-903's 8-item SDK checklist; the T-904/T-905 confirmations; T-802 dry run, T-803 shadow week, T-804 go-live;
finishing the POC post/undo; and the direct-post checklist above. Leave them `ready-for-human` and describe
exactly what a person should do.
