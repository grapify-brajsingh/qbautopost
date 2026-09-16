# Session Handoff — QbAutopost

Written: 2026-09-17 (session 10) · M0–M7 done · M8 agent work done (T-801 done; T-802, T-803, T-804 ready-for-human) · T-609 ready-for-human · Next step: **no agent task left**. Next is the **human server work**, starting with **T-609**, then T-802 → T-803 → T-804

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-44) and apply any
that change behaviour first (tests first, one commit per change, tracker updated).
Every plan task is done or ready-for-human; do not mark a [server] or
ready-for-human row done. If the owner has recorded server results in the
T-609/T-802/T-803/T-804 checklists or logs, fix what they found, one task at a time.
Otherwise work the open follow-ups in section 6 (Linux test run / CI) only if asked.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, the operator guide is `docs/runbook.md`, Hermes setup is `deploy/README-hermes.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–10 commits (T-101…T-804) are **local only**. Push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 917 passing (Core 730, Api 187) on Windows, 2 consecutive green runs. **Not yet run on Linux.** |
| Milestones | M0–M7 done (M6's T-609 ready-for-human); M8: T-801 done, T-802/T-803/T-804 **ready-for-human** |
| Packages | none added in M8 |
| Manual checks | `docker compose -f deploy/hermes/docker-compose.yml --env-file deploy/hermes/.env.example config` → valid, loopback-only port. `deploy/start-all.ps1 -WhatIf` and `deploy/install-task.ps1 -WhatIf` → exit 0, nothing changed. `scripts/shadow-diff.ps1` run on the golden sample. No container has been started (no image known), no task registered, the COM path has **never run** |

## 4. What M8 added (code map delta)

```
deploy/
  hermes/docker-compose.yml     Hermes service: ports "127.0.0.1:8642:${HERMES_CONTAINER_PORT}", required (:?) HERMES_IMAGE,
                                HERMES_CONTAINER_PORT, HERMES_DATA_PATH, API_SERVER_KEY; API_SERVER_ENABLED/HOST/PORT set;
                                optional env_file provider.env (required: false); volume hermes-data; mem_limit
                                HERMES_MEMORY_LIMIT (4g); json-file logs 10m x 3
  hermes/.env.example           placeholders only (secret-named values start with "replace-with-")
  hermes/provider.env.example   placeholder provider key (name depends on provider/image)
  README-hermes.md              WSL 2 + Docker Engine (Server), Docker Desktop (10/11), .wslconfig cap, configure, key
                                hand-over to QBAUTOPOST__Hermes__ApiKey, checks, operate
  start-all.ps1                 Docker up (-DockerMode Wsl|Desktop, -WslDistro) -> compose up -d -> wait TCP 127.0.0.1:8642
                                -> Start-Api (once, by exe name, -AppExe) -> wait GET /health/hermes 200.
                                exit 0 ok / 1 docker|compose|api failed / 2 api up but Hermes unhealthy; -WhatIf;
                                log C:\qb-autopost\logs\start-all-yyyyMMdd.log; loopback-only -ApiBaseUrl; no keys
  install-task.ps1              Register-ScheduledTask "QbAutopost": -AtLogOn (+60 s), -LogonType Interactive,
                                -RunLevel Limited, no time limit, IgnoreNew; -StartAllArguments, -Unregister, -WhatIf
scripts/shadow-diff.ps1         T-803: analysis.json vs manual CSV (Date,Amount,Payee,Account[,Source]) -> shadow-diff.csv;
                                exit 0 / 3 disagreements / 1 bad input; offline, read-only
.gitignore                      + deploy/hermes/provider.env
docs/runbook.md                 §2.4 rewritten for the scripts; §2.5 going live, one company per installation
docs/tracker.md                 T-802, T-803, T-804 checklists; T-803 shadow log and T-804 go-live log tables; Q-42…Q-44
tests/  Core: Architecture/DeployFilesTests (10), Architecture/DeployScriptsTests (17), TestSupport/RepoRoot.cs
        Api:  Api/GoLiveConfigApiTests (4)
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-44 all use the conservative choices. New in M8:
   - Q-42: the Hermes image, in-container port, variable names and data path are unknown, so they are required in `.env` with no defaults.
   - Q-43: `/health/hermes` is served by the API, so `start-all.ps1` waits for the Hermes port first. If Hermes stays down, it still starts the API and exits 2.
   - Q-44: one installation serves one company, so running several companies needs an owner decision.

   Q-27 (T2/T3/T4 run again at post time) and Q-37 (COM retry) still matter before go-live.
2. **Behaviour changes in M8:** none in the program. Only deploy files, scripts, docs and tests were added.
3. **The deploy files are guarded by text tests** (`DeployFilesTests`, `DeployScriptsTests`):
   - loopback-only port, and no `${HERMES_IMAGE:-` default;
   - placeholders only in the `*.example` files;
   - ASCII-only scripts (Windows PowerShell 5.1 reads BOM-less files as ANSI), with `SupportsShouldProcess`;
   - no `ApiKey`/`API_SERVER_KEY` in the scripts;
   - no `New-Service`, `sc.exe` or `S4U`;
   - `Wait-HermesPort -` called before `Start-Api -`.

   Keep these tests passing when you edit the files.
4. **PowerShell 5.1 gotcha:** `$PSScriptRoot` is empty inside `param()` defaults when a script runs with `-File`. Resolve path defaults in the body from `$MyInvocation.MyCommand.Path` (see `start-all.ps1`). Parse-check with `[System.Management.Automation.Language.Parser]::ParseFile(...)`. `pwsh` is not installed on the dev box.
5. **Test helpers:**
   - `RepoRoot.Find()` (Core tests) locates `QbAutopost.sln`.
   - `GoLiveConfigApiTests.SubmitWithoutDryRunAsync` sends a job with `dryRun` null. `RunToEndAsync` always sends `dryRun` (default true).
   - Earlier helpers are still valid: `ScriptedHermes`, `PostResultApiTests.UseRulesThatResolveEverything()`, `QuickBooksFlowApiTests.SimulatedHostFactory`, `RecoveryTests.SeedJob`.
6. **Still true from earlier sessions:**
   - JSON enums are camelCase.
   - Read shared JSON with `AtomicFile.ReadAllText`.
   - `ApiFactory.WithSetting` is not chainable.
   - Never teach rules against `Fixtures.SampleRules`; copy it first.
   - Logging is Serilog and only `Serilog:MinimumLevel` is read.
   - Sample output and `src/QbAutopost.Api/data/` are git-ignored.
7. **Running by hand:** `dotnet run --project src/QbAutopost.Api` needs these environment variables:
   - `ASPNETCORE_ENVIRONMENT=Development`, or a real `QBAUTOPOST__Api__ApiKey`;
   - `QBAUTOPOST__QuickBooks__Fake=true` to simulate QuickBooks;
   - `QBAUTOPOST__Api__Bind=http://127.0.0.1:5099` to avoid a port clash.

   A job still fails at T1 without a running Hermes.
8. **Tooling:**
   - The GateGuard hook may block the first edit of a file. State the facts and retry.
   - Python is not installed.
   - With `sed`, do not use `#` as the delimiter when the replacement contains `#`.

## 6. Next: human server work (no agent milestone left)

Order on the QuickBooks server: follow the checklists in `docs/tracker.md` in this order.
1. **T-609**: a COPY of the company file, and record the bitness.
2. **T-802**: Hermes `.env` / `provider.env`, `start-all.ps1`, `install-task.ps1`, then sign out and in.
3. **T-803**: the shadow week with `scripts/shadow-diff.ps1`. It ends after two clean runs in a row.
4. **T-804**: go-live for one company with `DryRunDefault=false`, then monitor for a week.

Open follow-ups:
- Owner answers, above all:
  - Q-42: name the Hermes image and its variable names.
  - Q-27, Q-37: needed before posting.
  - Q-44: how several companies should run.
- A person should read `docs/runbook.md` and `deploy/README-hermes.md` once on the server (plan §6).
- Run `dotnet test` on Linux; a CI workflow would cover this. Plan §6 requires green on Linux and Windows.
- Decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
- Push sessions 2–10.
