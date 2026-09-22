# Session Handoff — QbAutopost

Written: 2026-09-21 (session 13) · M0–M8 agent work done · **The app talked to a real QuickBooks for the first time
(Enterprise 24, x64)** · Next session: **the owner brings a spec for an "API project"** — connection endpoints,
health checks, logging to a local folder. Read §6 before planning that: the app already *is* an ASP.NET Core API.

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/spec.md §6 and docs/tracker.md (rows T-805, T-806, questions Q-27, Q-37, Q-45).
The owner will paste a spec for the API conversion. Before planning: the app is already an ASP.NET Core
minimal-API host with 10 routes, Serilog file logging and /health/hermes + /health/quickbooks (handoff §3, §6).
Map each line of the new spec to "already there", "rename/move", or "new", and say so before writing code.
Tests first for Core logic, one task at a time, tracker row + session log + commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` + bank/card statements + optional
invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, Deposits —
the same things a person would type into *Batch Enter Transactions*. Deterministic code does all money and mapping;
Hermes (a local OpenAI-compatible model) only reads PDFs/invoices and suggests accounts, and **can now be switched
off entirely** (`Hermes:Enabled=false`, T-806) for a no-AI POC. The contract is `docs/spec.md`, milestones
`docs/plan.md`, progress `docs/tracker.md`, operator guide `docs/runbook.md`, POC guide `samples/poc/README-POC.md`
and `steps.md`, agent rules `CLAUDE.md`.

## 3. What exists today (the part that matters for the API conversion)

**Host:** `src/QbAutopost.Api` — ASP.NET Core 8 minimal API, `WebApplication`, Kestrel bound to
`Api:Bind` (default `http://127.0.0.1:5080`, loopback). No MVC controllers, no Swagger, no auth middleware beyond a
single API-key check.

**Routes** (`src/QbAutopost.Api/Endpoints/`):

| Route | File | Does |
|---|---|---|
| `POST /jobs` | JobEndpoints | validates the folder, queues an analysis; `dryRun`, `force` |
| `GET /jobs?status=` | JobEndpoints | list of job summaries |
| `GET /jobs/{id}` | JobEndpoints | `JobView`: status, statements, counts, held/skipped/posted lines |
| `POST /jobs/{id}/post` | JobEndpoints | re-analyses and posts a `ready` job |
| `GET /health/hermes` | HealthEndpoints | model ping; 503 when down or turned off (no API key needed) |
| `GET /health/quickbooks` | HealthEndpoints | `HostQuery` + open company file; 503 with the reason |
| `POST /qb/sync-lists` | QuickBooksEndpoints | reads accounts/vendors/customers into `qb-lists.json` |
| `POST /batches/{id}/undo` | BatchEndpoints | deletes a posted batch from QuickBooks (FR-13) |
| `POST /rules/alias`, `POST /rules/account` | RulesEndpoints | teach a payee alias / vendor account |

Cross-cutting: `Security/ApiKeyMiddleware` (`X-Api-Key` on everything except `/health/*`, constant-time compare),
RFC 7807 problem details, `System.Text.Json` with camelCase enums, one `JobWorker` background service consuming an
in-process queue (`JobQueue`), `JobStore` mirroring state to each job's `output/status.json` plus a `jobs.json` index.

**QuickBooks connection** (`src/QbAutopost.QuickBooks`, the only project allowed to touch COM):
`QbSession` → `QBXMLRP2.RequestProcessor` → `OpenConnection2` → `BeginSession(companyFile)` → `ProcessRequest(qbXML)`
→ `EndSession`/`CloseConnection`, each call on its own STA thread (`QbGateway`), wrapped by
`Core/Gateway/ResilientQbGateway` (one call at a time process-wide, busy timeout, one safe retry) and by
`Api/QuickBooks/LoggingQbGateway` (T-805). `QbConnection.Create` picks: real SDK on Windows, `SimulatedQbGateway`
with `QuickBooks:Fake=true` (Development/Testing only), otherwise `UnconfiguredQbGateway` (never sends anything).

**Logging (T-805, already "logs to a local folder"):** Serilog, console + daily rolling file
`Paths:Logs/qbautopost-yyyyMMdd.log` (31 kept, 1 GB roll, shared). Sinks are fixed in code so a configured sink
cannot bypass the secret scrubber; only `Serilog:MinimumLevel:Default` / `Override:<category>` are read. Every line
carries a `jobId`. Logged: startup summary (version, **x64/x86**, Windows session, every setting, files found/missing),
one line per HTTP request (`HTTP GET /jobs responded 200 in 12 ms`, 4xx warning, 5xx error, health polls at Debug),
API-key refusals without the key, job steps (requirement, statements, invoices, held/skipped lines, G4 changes,
posted TxnIDs, batch totals), every QuickBooks call with per-response status and each SDK session step, every Hermes
call. No statement text, prompts or qbXML bodies at Information (spec §14). `docs/runbook.md` §8.6 has the table.

**Config** (`appsettings.json`, env `QBAUTOPOST__Section__Key`): `Api:Bind/ApiKey`, `DryRunDefault`,
`Company:Name/FilePath/RulesFile`, `QuickBooks:AppName/QbXmlVersion/DuplicateWindowDays/BusyTimeoutSeconds/
BackupFolder/BackupMaxAgeHours/Fake/RetryDelaySeconds`, `Hermes:Enabled/BaseUrl/ApiKey/Model/TimeoutSeconds`,
`Ocr:Enabled/TessDataPath`, `Paths:Ledger/QbLists/Logs/JobIndex`, `Serilog:MinimumLevel`.

## 4. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git, **pushed through `252ce00`** |
| Build | `dotnet build -warnaserror` → 0 warnings |
| Tests | **Core 732, Api 210** (942), green twice in a row on Windows; Linux + Windows CI in `.github/workflows/ci.yml` |
| Milestones | M0–M7 done; M8: T-801/T-805/T-806 done, T-802/T-803/T-804 ready-for-human; T-609 partly verified on the server (see §5) |
| Packages | none added in sessions 12–13 |
| POC package | `scripts/build-poc-package.ps1` → `dist/qbautopost-poc-win-x64.zip` (self-contained, ~53 MB, git-ignored): `app\`, `poc\` (settings, rules, IIF lists, 2 sample jobs), `scripts\qb-server-check.ps1`, `README-POC.md`, `steps.md` |

## 5. What happened on the owner's server (2026-09-20, first real QuickBooks run)

Windows Server 2019, RDP session 9, user `accountexx-grapify`, **QuickBooks Enterprise Solutions 24.0 (34.0), x64**,
test company `Tropicana Properties LLC` at
`D:\Accountexx Data\Quickbook - Accounting File\Quickbook - Accounting File\18_Takoma Park Grocery Store Inc\`,
POC package unzipped under `…\18_Takoma Park Grocery Store Inc\QBAUTO_POST\qbautopost-poc-win-x64\`.

Verified working: `GET /health/quickbooks` → `ok: true` with the product string and company file (**T-609 bitness = x64,
recorded**); `POST /qb/sync-lists` → 37 accounts, 7 vendors, 2 customers, `missingInRules` empty (the
`tropicana-lists.iif` import works); dry run of `2026-09-tropicana` → `ready`, 15 to post, 0 held, 1 skipped,
bank balance chain reconciles. **The post step was not completed yet** — the owner hit a 409 (the job was no longer
`ready`); the fix is `-Step dryrun … -Force` then post, or use the second job.

Two defects found there, both still open:
1. **The exe reads `appsettings.json` from the working directory.** Started from `C:\Users\…`, the app ran with an
   empty `Company:Name` and Hermes enabled. Starting it from `app\` fixes it. Worth making the content root
   `AppContext.BaseDirectory` (check `WebApplicationFactory` tests still pass) — a natural item for the API work.
2. **The certificate dialog blocked `BeginSession` for 101 s**, past the 60 s busy timeout, so the first health call
   returned `quickbooks-busy` while the session itself succeeded. Also: **waiting for the gateway lock is not logged**
   (the log is silent while a request queues behind another QuickBooks call). Consider a "waiting for QuickBooks" log
   line and a longer first-call timeout.

## 6. Next session: "convert this app to an API project"

**Read this first: it is already an API project.** ASP.NET Core 8 minimal API, 10 routes, API-key auth, problem
details, health endpoints and file logging all exist (§3). So the work is almost certainly *restructuring and filling
gaps*, not a rewrite. Before writing code, map every line of the owner's spec onto one of:

- **already there** — e.g. "health checks" (`/health/hermes`, `/health/quickbooks`), "logging to a local folder"
  (`Paths:Logs`, daily rolling file), "connection" (`/qb/sync-lists`, the health probe, `QbConnection`).
- **rename / re-shape** — e.g. controllers instead of minimal APIs, `/api/v1/...` prefixes, a different response
  envelope, OpenAPI/Swagger UI, CORS, a non-loopback bind, Windows service or IIS hosting.
- **genuinely new** — e.g. connect/disconnect endpoints that open a session on demand, a "post transactions" endpoint
  that takes JSON rows instead of a job folder, per-request company file, status/progress polling, an endpoint to read
  the log, API keys per caller.

Things the spec should settle (ask if it does not):
1. **Does the caller send transactions as JSON**, or keep the job-folder model? Today everything hangs off a folder
   on disk (`FolderReader`, `output/*`), and the ledger/undo assume a job id per folder.
2. **Who holds the QuickBooks session?** Today: one call at a time, a fresh session per call, no "connect" state. A
   connect/disconnect API means keeping a session open across requests — that changes `ResilientQbGateway` and how
   the busy timeout behaves.
3. **Is the API still loopback-only and single-company?** Both are current assumptions (Q-44). A remote caller means
   real auth, TLS and a threat model, not just the shared `X-Api-Key`.
4. **Logging**: what beyond today's file sink — a `GET /logs` endpoint, correlation id per request (today it is
   `jobId`), JSON lines for ingestion, retention other than 31 days?
5. **Hermes**: does the API keep the no-AI mode as default (Q-45)?

Keep these rules while doing it (`CLAUDE.md`): COM only in `QbAutopost.QuickBooks`; the model never computes money;
hold rather than guess; qbXML element order is golden-file tested; no new NuGet packages without a tracker question;
every state transition persisted; `dotnet test` must stay green on Linux (no real QuickBooks or Hermes in tests).

## 7. Things a new session must know

1. **Owner answers**: still none for Q-1…Q-45 except Q-0. Q-27 (re-running T2/T3/T4 at post time) and Q-37 (COM retry,
   backup guard) matter before any real posting; Q-45 asks whether the no-AI mode stays.
2. **Tests**: `ApiFactory` boots the real host with `FakeQbGateway` + `FakeHermesClient` in a temp folder;
   `ApiFactory.WithSetting` is not chainable (use `WithWebHostBuilder` + `AddInMemoryCollection` for several
   settings, as `PocModeApiTests` does). `ListLogger<T>` (T-805) captures `ILogger` calls. Host tests replace
   `IHermesClient`, so the logging decorator around it is covered by unit tests only.
3. **The POC data** (`samples/poc/`): `rules.json` resolves every line by rule; `tropicana-lists.iif` creates the
   9 accounts / 7 vendors / 2 customers those rules name; `jobs/2026-09-tropicana` (CSV bank + card, 15 post,
   1 skip) and `jobs/2026-08-tropicana-xlsx` (one Excel bank statement, 9 post, 0 skip). A job id is the folder name,
   so a re-run of the same folder needs `force=true`.
4. **Still true**: JSON enums camelCase; read shared JSON with `AtomicFile.ReadAllText`; never teach rules against
   `Fixtures.SampleRules` (copy first); Serilog reads only `Serilog:MinimumLevel`; `src/QbAutopost.Api/data/` and
   `dist/` are git-ignored; PowerShell 5.1 on the dev box (no `pwsh`), scripts must be ASCII; the GateGuard hook
   blocks the first edit of each file until the facts are restated.
5. **Running by hand**: `dotnet run --project src/QbAutopost.Api` with `ASPNETCORE_ENVIRONMENT=Development`,
   `QBAUTOPOST__QuickBooks__Fake=true`, `QBAUTOPOST__Api__Bind=http://127.0.0.1:5099`. With `Hermes:Enabled=false`
   a full CSV job runs end to end with no model.

## 8. Open follow-ups

- Finish the POC on the server: post `2026-08-tropicana-xlsx` (or `-Force` the September job), check both registers,
  then undo. `steps.md` sections 7–10.
- The two defects in §5 (content root, gateway-lock logging / first-call timeout).
- T-802/T-803/T-804 remain `ready-for-human` (Hermes container, shadow week, go-live) and assume Hermes is on.
- Decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
