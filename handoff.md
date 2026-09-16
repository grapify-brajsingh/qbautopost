# Session Handoff — QbAutopost

Written: 2026-09-17 (session 9) · M0–M5 and M7 done · M6 done except T-609 (ready-for-human, server) · Next step: **M8** (deploy), starting with **T-801**

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-41) and apply any
that change behaviour first. Then continue with the first todo task in M8 (T-801).
One task at a time, tests green, tracker updated, one commit per task.
T-803 and T-804 are [server]: prepare them, set ready-for-human, never done.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, the operator guide is `docs/runbook.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–9 commits (T-101…T-705) are **local only**. Push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 886 passing (Core 703, Api 183) on Windows, 2 consecutive green runs. **Not yet run on Linux.** |
| Milestones | M0–M5, M7 done; M6 done except **T-609 = ready-for-human**; M8 todo (T-801, T-802 agent work; T-803, T-804 [server]) |
| Packages | M7 added `Serilog.AspNetCore` 8.0.1 and `Serilog.Sinks.File` 5.0.0 (both on the allowed list) |
| Manual check | `dotnet run` (Development, `QuickBooks:Fake=true`, a Hermes key in the environment) → console and `src/QbAutopost.Api/data/logs/qbautopost-yyyyMMdd.log` show `[jobId]`, and the key is masked. The COM path has **never run** (no QuickBooks on the dev box) |

## 4. What M7 added (code map delta)

```
src/QbAutopost.Core/
  Mapping/RulesEditor.cs      FR-14: SetAlias(fragment, name, AliasKind) / SetVendorAccount(vendor, account) → RuleChange
                              (Section, Key, Value, Previous). Edits rules.json through JsonNode under a process-wide lock,
                              validates old + new content with Rules.Parse, writes with AtomicFile. Names checked against
                              qb-lists.json (exact) only when the file exists; RuleValidationException → 400
  Mapping/Rules.cs            Load now reads with AtomicFile.ReadAllText (share-safe with the editor)
src/QbAutopost.Api/
  Endpoints/RulesEndpoints.cs POST /rules/alias {fragment,name,kind}, POST /rules/account {vendor,account};
                              400 validation, 500 when rules.json is missing/broken (file untouched)
  Logging/LoggingSetup.cs     builder.AddQbAutopostLogging(): Serilog console + file Paths:Logs/qbautopost-.log (daily,
                              31 kept, shared), levels from Serilog:MinimumLevel only; sinks fixed in code
  Logging/SecretScrubber.cs   masks configured secret values, labelled values (X-Api-Key:, Bearer, apiKey=…), secret-named
                              properties, and exception text (ScrubbedException)
  Logging/ScrubbingSink.cs    wrapper sink (LoggerSinkConfiguration.Wrap)
  Logging/JobIdEnricher.cs    jobId = worker scope, else {JobId} in the message, else "-"
  Program.cs                  AddQbAutopostLogging, RulesEditor singleton, MapRulesEndpoints
  appsettings.json            "Logging" section replaced by "Serilog": { "MinimumLevel": … }
docs/runbook.md               operator guide (T-705)
tests/  Core: Mapping/RulesEditorTests (26), Gates/BackupGuardTests (+4)
        Api: RulesApiTests (13), LoggingApiTests (5), Logging/SecretScrubberTests (19), Configuration/AppSettingsTests (5),
             PostingSafetyApiTests (+2), JobsApiTests (+3), RecoveryTests (+2), UndoApiTests (+1)
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-41 all use the conservative choices. New in M7: Q-40 (rules teaching: comments dropped on rewrite, names also checked against lists, 3-character fragments, 500 on a broken file, lock instead of the worker) and Q-41 (logging details). Q-27 (T2/T3/T4 run again at post time) is still open and matters before go-live.
2. **Behaviour changes in M7:**
   - Teaching a rule rewrites `rules.json` and **drops its `//` comments**. Tests must never teach against `Fixtures.SampleRules`: copy it into the factory's temp dir and use `WithSetting("Company:RulesFile", copy)` (see `RulesApiTests`).
   - Logging is Serilog now. MS `Logging:LogLevel` settings are ignored; use `Serilog:MinimumLevel`. Every test host writes a log file under its temp `data/logs` (shared mode, so a parent and a `WithSetting` host can both write).
   - T-703 and T-704 needed no production change (tests only).
3. **M8 hints:**
   - **T-801** `deploy/hermes/docker-compose.yml`, `.env.example`, `deploy/README-hermes.md`. Spec §4: port 8642 published on **127.0.0.1 only**, `API_SERVER_KEY` required, cloud provider key in the container (spec Q5). The spec never names the Hermes image: take it from an `.env` variable (e.g. `HERMES_IMAGE`) with no guessed default that could pull the wrong thing, and record a Q-row. `.env` is git-ignored; `.env.example` must hold placeholders only (rule 6). Docker 29.3 CLI is on the dev box, so `docker compose -f deploy/hermes/docker-compose.yml --env-file deploy/hermes/.env.example config` is the verification. Include the `.wslconfig` memory-cap note and the Windows 10/11 Docker Desktop alternative.
   - **T-802** `deploy/start-all.ps1` (WSL/Docker up → compose up → wait for `/health/hermes` → start the API) and `deploy/install-task.ps1` (Task Scheduler at logon, interactive, never a service). Keep scripts **ASCII only** (Windows PowerShell 5.1 reads them as ANSI). Add a `-WhatIf`/dry-run switch so they can be exercised on the dev box; a real run needs the server, so the row may need `ready-for-human` for the server part (plan says "dry run on server"). Parse-check with `powershell -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile(...)"`. `docs/runbook.md` §2.4 already describes both scripts; keep it in sync.
   - **T-803 / T-804 [server]**: add checklists to the tracker (shadow week: five real folders dry run, diff against manual entry, rules taught via `/rules/*`; go-live: `DryRunDefault=false` for one company, monitor a week). Set both `ready-for-human`.
4. **Test helpers:** `ScriptedHermes` (Core) validates like the real client. `TestStatementReader`, `TestInvoiceExtractor` and `TestAccountChooser` build the pipeline's readers. `PostResultApiTests.UseRulesThatResolveEverything()` makes all 10 sample lines postable; `RulesApiTests.Should_UseTaughtRulesInNextJob_When_RulesChangeBetweenJobs` does the same through the teaching endpoints. `QuickBooksFlowApiTests.SimulatedHostFactory` shows how to host without the test gateway. `RecoveryTests.SeedJob` writes a job index + `status.json` as a previous host would.
5. **Still true from earlier sessions:**
   - JSON enums are camelCase.
   - Read shared JSON with `AtomicFile.ReadAllText`.
   - `ApiFactory.WithSetting(key, value)` is not chainable. Use `WithWebHostBuilder` when you need several keys (see `PostingSafetyApiTests.Should_UseConfiguredMaxAge_When_BackupMaxAgeHoursIsSet`).
   - `FakeQbGateway` is backed by `SimulatedQuickBooks`; `Gateway.Writes` / `Gateway.Queries` split add/delete from read-only message sets.
   - Sample output and `src/QbAutopost.Api/data/` are git-ignored. `.gitattributes` marks binary files.
6. **Running by hand:** `dotnet run --project src/QbAutopost.Api` needs `ASPNETCORE_ENVIRONMENT=Development` (or a real `QBAUTOPOST__Api__ApiKey`). Add `QBAUTOPOST__QuickBooks__Fake=true` to simulate QuickBooks, and `QBAUTOPOST__Api__Bind=http://127.0.0.1:5099` to avoid a port clash. A job still fails at T1 without a running Hermes (Q-22).
7. **Tooling:**
   - The GateGuard hook may block the first edit or creation of a file; state the facts and retry.
   - Python is not installed. Edit `docs/tracker.md` with the Edit tool (or a bash heredoc append for the Session log), not a PowerShell script.

## 6. Next milestone: M8 (deploy)

See `docs/plan.md` T-801…T-804 and spec §4 (deployment context), §12 (configuration), §14 (security), plus `docs/runbook.md` §2.

Open follow-ups:
- **T-609 on the server** (human): run the checklist in `docs/tracker.md` and record the bitness. It also confirms the COM constants, `DepositQuery` with `AccountFilter`, and real `*AddRs` / `TxnDelRs` answers.
- A person should read `docs/runbook.md` once on the server (plan §6 definition of done).
- Run `dotnet test` on Linux (a CI workflow would cover this).
- Decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
- Push sessions 2–9.
- Get the owner's answer to Q-27 (re-running T2/T3/T4 at post time) before posting goes live.
