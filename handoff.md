# Session Handoff — QbAutopost

**This is an unattended relay. The owner is away and will answer nothing mid-run.**
Written: 2026-09-23 (session 15) · **M9 (API v1): 10 of 14 done**, branch `m9-api-v1` · **Next task: T-911.**

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
8. **Commit** `T-9xx: <summary>` and **push**: `git push -u origin m9-api-v1`.
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
| Repo | `D:\qb_post`, branch **`m9-api-v1`**, remote `origin` = `github.com/grapify-brajsingh/qbautopost` |
| Build | `dotnet build -warnaserror` → 0 warnings |
| Tests | **Core 820, Api 344** (1164), green twice on Windows |
| Milestones | M0–M7 done; M8 agent work done (`ready-for-human`); **M9 10/14** |
| Packages | 6 + `Scalar.AspNetCore` **approved** for T-913 (Q-46 answered 2026-09-23) |
| Owner | **Away. Answers nothing.** Q-1…Q-45, Q-47…Q-61 outstanding; each already has a conservative behaviour |

## 3. The task queue

| Task | Status | One line |
|---|---|---|
| T-901…T-910 | **done** | Routes, health, SDK probe, connection test, company-file validate, direct model, validate, post, idempotency, clients+scopes |
| **T-911** | **NEXT** | FR-A-14 TLS + FR-A-15 rate limits + FR-A-17 input hardening — see §4 |
| T-912 | queued | FR-A-16 audit trail (`audit-yyyyMMdd.jsonl`) + the `requestId` of §2.6, which does not exist yet |
| T-913 | queued | FR-A-18 OpenAPI document + Scalar reference UI. **Unblocked**: the package is approved |
| T-914 | queued | Move docs, runbook and the POC package to the v1 routes and the new auth rules |
| *(then)* | **STOP** | Write the final report (§9). Do not start M10. Do not attempt the server tasks |

## 4. The next three tasks, in detail

**T-911 — three separable pieces; do three RED/GREEN cycles inside the one task.**
- *Transport (FR-A-14)*: Kestrel HTTPS from `Api:Tls:PfxPath` + `PfxPassword` (**environment only** — a value found
  in `appsettings.json` logs a warning naming the file, never the value, and is accepted). **Startup refuses a
  non-loopback bind without TLS** unless `Api:AllowInsecureRemote=true`, which warns at startup *and* on every
  request. HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, no `Server` header. CORS off by
  default; `*` refused while any route needs a key.
- *Rate limits (FR-A-15)*: ASP.NET Core 8's built-in limiter — **shared framework, no package**. Partition by client
  id (`ApiKeyMiddleware.ClientOf(context)`), by IP for the unauthenticated health routes. `qb:post` 10/min, other
  authenticated 120/min, health 600/min per IP, failed auth 10/min per IP then 429 for 5 minutes. `429` carries
  `Retry-After`. `ApiClient.PostPerMinute` / `DefaultPerMinute` already exist for per-client overrides and are unused.
- *Input hardening (FR-A-17)*: body 2 MB (`Api:MaxRequestBodyBytes`), string bounds (`reference` 64, `memo` 4096,
  names 255) → `400`, and **caller-supplied paths absolute and inside `Paths:AllowedJobRoots[]`**. Note T-909
  buffers the whole body to hash it, so the 2 MB cap protects that too.

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
- [ ] Commit `T-9xx: <summary>` with the Co-Authored-By trailer, then `git push -u origin m9-api-v1`
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
14. **Rate limiting will break existing tests** (T-911). 1164 tests hammer these routes. Expect to make the limits
    configurable and effectively off in `ApiFactory`, and say so in the evidence rather than quietly raising them.

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
