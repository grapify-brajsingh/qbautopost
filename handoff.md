# Session Handoff — QbAutopost

**This is an unattended relay. The owner is away and will answer nothing mid-run.**
Written: 2026-09-23 (session 16) · **M9 (API v1): 11 of 14 done**, branch `m9-api-v1` · **Next task: T-912.**

> **If you are an agent starting fresh: read §0, do the one task named in §3 as "NEXT", then §5 before you finish.**
> Do not read ahead and do not do two tasks. The next agent has no memory of you — this file is the only thing
> that carries forward, so leaving it accurate is part of the task, not paperwork after it.

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
| Build | `dotnet build -warnaserror` → 0 warnings |
| Tests | **Core 834, Api 405** (1239), green twice on Windows |
| Milestones | M0–M7 done; M8 agent work done (`ready-for-human`); **M9 11/14** |
| Packages | 6 + `Scalar.AspNetCore` **approved** for T-913 (Q-46 answered 2026-09-23). T-911 added **no** package — the limiter is shared framework |
| Owner | **Away. Answers nothing.** Q-1…Q-45, Q-47…Q-63 outstanding; each already has a conservative behaviour |

## 3. The task queue

| Task | Status | One line |
|---|---|---|
| T-901…T-911 | **done** | Routes, health, SDK probe, connection test, company-file validate, direct model, validate, post, idempotency, clients+scopes, transport+limits+input hardening |
| **T-912** | **NEXT** | FR-A-16 audit trail (`audit-yyyyMMdd.jsonl`) + the `requestId` of §2.6, which does not exist yet — see §4 |
| T-913 | queued | FR-A-18 OpenAPI document + Scalar reference UI. **Unblocked**: the package is approved |
| T-914 | queued | Move docs, runbook and the POC package to the v1 routes and the new auth rules |
| *(then)* | **STOP** | Write the final report (§9). Do not start M10. Do not attempt the server tasks |

## 4. The next three tasks, in detail

*(T-911 is done. Its shape is worth one line before you read on: the startup rules live in
`Api/Security/TransportGuard.cs` as pure functions, not inline in `Program.cs`, so they are testable — copy that if
T-912 adds any startup decision of its own.)*

**T-912 — FR-A-16 audit.** One JSON line per authenticated mutating request to `Paths:Logs/audit-yyyyMMdd.jsonl`:
`utc, requestId, clientId, remoteIp, method, path, idempotencyKey, outcome, status, batchId, jobId, counts,
totalAmount`. **Never the key, never the body, never statement text.** Retention `Api:AuditRetentionDays` (400 —
longer than the 31-day operational log, because this is money evidence). §2.6's `requestId` does not exist yet;
this is the place to add it (26-char sortable, echoed as `X-Request-Id`, honoured inbound when it matches
`^[A-Za-z0-9_-]{8,64}$`).

**T-913 — FR-A-18.** Two separate things on purpose: the **document** (`GET /api/v1/openapi.json`, hand-authored,
with a drift test that fails when a route exists without a matching entry) and the **UI** (`GET /api/v1/reference`,
Scalar, behind `Api:Reference:Enabled` default **false**). Swagger/Swashbuckle stays ruled out. Add
`Scalar.AspNetCore` to `src/QbAutopost.Api/QbAutopost.Api.csproj` and note it in the tracker row.

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
18. **`Q-62` is an unexplained single test failure** — one run reported `Failed: 1, Passed: 404` and it did not
    recur in 11 more runs; the name was not captured. If you see a one-off failure, **capture the name** before
    re-running, and update Q-62 with it.

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

## 9. The final report (last agent only)

When T-914 is done, do not start anything else. Write `docs/M9-COMPLETE.md` with: what M9 delivered route by
route; the full list of open questions with the conservative behaviour each one currently has; **everything that
needs the owner or the server**, including the T-903 checklist, T-802/803/804, and a new direct-post server
checklist (dry run → one real batch → undo it); and an honest list of what is implemented but never run against a
real QuickBooks — which, today, is all of T-907…T-910.

## 10. What no agent can do

The server-verification work needs a person at the QuickBooks machine and **must not be attempted or claimed**:
T-903's 8-item SDK checklist; the T-904/T-905 confirmations; T-802 dry run, T-803 shadow week, T-804 go-live;
finishing the POC post/undo; and the direct-post checklist above. Leave them `ready-for-human` and describe
exactly what a person should do.
