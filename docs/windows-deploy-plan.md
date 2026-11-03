# Windows Server Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One command on the Windows server releases the current API and dashboard, and the first install is a written checklist.

**Architecture:** A new `deploy/deploy.ps1` orchestrates the existing deploy scripts. The API gains one opt-in setting so it sees real client addresses behind nginx on the same server. Nothing else changes.

**Tech Stack:** Windows PowerShell 5.1, .NET 8 (`Microsoft.AspNetCore.HttpOverrides`, shared framework), xUnit, npm.

**Spec:** `docs/windows-deploy.md`

## Global Constraints

- Work on a new branch `feature/m16-windows-deploy` from the current HEAD. Commits are `T-xxx: <summary>` (repo CLAUDE.md).
- No new NuGet package. `dotnet build -warnaserror` and `dotnet test` stay green, on Linux too.
- `Api:TrustForwardedHeaders` defaults to `false`.
- `deploy.ps1` matches `deploy\*.ps1`: `#Requires -Version 5.1`, ASCII only, `SupportsShouldProcess`, strict mode, `$ErrorActionPreference = 'Stop'`, resolves paths after `param()` from `$MyInvocation.MyCommand.Path`, logs under `C:\qb-autopost\logs`, exit 0 or 1.
- It never prints a secret, and never changes `rules.json`, `ledger.json`, `clients.json`, jobs, the company file, nginx, firewall rules, machine variables or `DryRunDefault`.
- Tasks tagged **[server]** end `ready-for-human`: a person verifies on the QuickBooks server.

## Review Focus

- **First deploy:** no `app`, no `app.old`, no `dashboard`. Nothing to stop or restore, and it must still complete. Test in Task 2.
- **App folder locked at the swap** (antivirus, a slow exit): the old version must be put back and started, not left down. Test in Task 2.
- **Wrong account,** e.g. a different admin over Remote Desktop: refused before anything changes. Test in Task 2.
- **The scheduled task is missing:** discovered at preflight, not after the API is already stopped. Test in Task 2.
- **Spoofed `X-Forwarded-For`** from anything but loopback: ignored. Test in Task 1.

---

### Task 1: T-1601 Real client addresses behind a proxy

**Files:**
- Modify: `src/QbAutopost.Api/Configuration/AppSettings.cs` (`ApiSettings`, beside `AllowInsecureRemote`)
- Modify: `src/QbAutopost.Api/Program.cs` (before `if (TransportGuard.UseHsts(settings.Api))`)
- Modify: `src/QbAutopost.Api/appsettings.json`
- Test: `tests/QbAutopost.Api.Tests/Hardening/ForwardedHeadersTests.cs`

**Interfaces:**
- Produces: setting `Api:TrustForwardedHeaders` (`bool`, default `false`), used by Task 3's checklist.

- [ ] **Step 1: Write the failing tests**

Follow `AuthBrakeTests.cs` for the host and the brake limits. The test host has no real peer address, so add an `IStartupFilter` in the test that sets `context.Connection.RemoteIpAddress` to a chosen peer before the pipeline. Each test sends bad keys up to `FailedAuthPerMinute`, carrying `X-Forwarded-For: 10.0.0.1`, then one more with `X-Forwarded-For: 10.0.0.2`:

```csharp
Should_BlockOnlyTheGuessingClient_When_TrustIsOnAndPeerIsLoopback   // peer 127.0.0.1, setting true:  second client 401
Should_IgnoreTheHeader_When_TrustIsOff                              // peer 127.0.0.1, setting false: second client 429
Should_IgnoreTheHeader_When_PeerIsNotLoopback                       // peer 10.9.9.9,  setting true:  second client 429
```

- [ ] **Step 2: Run to verify the first test fails**

Run: `dotnet test tests/QbAutopost.Api.Tests --filter ForwardedHeadersTests`
Expected: `Should_BlockOnlyTheGuessingClient_...` FAILS with 429; the other two pass.

- [ ] **Step 3: Implement**

Add `public bool TrustForwardedHeaders { get; set; }` and `"TrustForwardedHeaders": false` in `appsettings.json`. In `Program.cs`, first in the pipeline, when the setting is on: `app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto })`. Keep the framework's default known proxies (loopback only). Comment with `T-1601` in the file's style.

- [ ] **Step 4: Run all tests**

Run: `dotnet build -warnaserror` then `dotnet test`
Expected: 0 warnings, all pass.

- [ ] **Step 5: Commit** `T-1601: Api:TrustForwardedHeaders for a loopback proxy`

### Task 2: T-1602 `deploy/deploy.ps1` [server]

**Files:**
- Create: `deploy/deploy.ps1`
- Test: `tests/QbAutopost.Core.Tests/Architecture/WindowsDeployScriptTests.cs`

**Interfaces:**
- Consumes: `apply-migrations.ps1`, `install-dashboard.ps1 -Source <dist>` and `-Rollback`, `backup-postgres.ps1 -BackupFolder <path>`, scheduled task `QbAutopost`; each script reports through exit code (`$LASTEXITCODE`).
- Produces: `.\deploy\deploy.ps1 [-Runtime win-x64|win-x86] [-Branch main] [-Rollback] [-Yes] [-WhatIf]`, plus overrides `-Root` (default `C:\qb-autopost`), `-ApiBaseUrl` (default `http://127.0.0.1:5080`), `-ReadyWaitSeconds` (default `180`).

- [ ] **Step 1: Write the failing static tests**

Follow `DashboardDeployScriptTests.cs` (`Read("deploy", "deploy.ps1")`; static checks only, the real run is the server checklist):

```csharp
Should_SupportWhatIfAndBeAsciiOnly_When_ReadingDeploy        // SupportsShouldProcess; every byte < 0x80
Should_PullFastForwardOnly_When_ReadingDeploy                // contains "--ff-only"
Should_BuildBeforeStoppingTheApi_When_ReadingDeploy          // index order: "npm", "dotnet", "publish" before "Stop-Process"
Should_BackUpBeforeMigrating_When_ReadingDeploy              // index order: "Stop-Process" < "backup-postgres.ps1" < "apply-migrations.ps1" < "app.old" swap
Should_StartThroughTheScheduledTask_When_ReadingDeploy       // contains "Start-ScheduledTask"; does not contain "Start-Process"
Should_RefuseTheWrongAccount_When_ReadingDeploy              // contains "Principal.UserId" and "Refused"
Should_CheckTheTaskExists_When_ReadingDeploy                 // "Get-ScheduledTask" before "Stop-Process"
Should_RestoreTheOldVersion_When_TheSwapOrStartFails         // "Restore-Previous" called from both the swap failure and the failed readiness path
Should_HandleAFirstDeploy_When_ReadingDeploy                 // Test-Path guards on the live app and on app.old before they are used
[Theory] Should_NotTouchServerState_When_ReadingDeploy       // no "DryRunDefault", "New-NetFirewallRule", "SetEnvironmentVariable", "ledger.json", "rules.json", "Register-ScheduledTask"
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/QbAutopost.Core.Tests --filter WindowsDeployScriptTests`
Expected: FAIL, `deploy.ps1` not found.

- [ ] **Step 3: Implement `deploy/deploy.ps1`**

Follow steps 1 to 9 of spec section 8, with these decisions:

- **Paths:** `$apiRepo = $Root\src\qbautopost`, `$webRepo = $Root\src\qbpost_dashboard`, `$liveSettings = $Root\appsettings.Production.json`.
- **Functions:** `Say($Message, $Level)` (use `Write-LogLine -Tag 'deploy'` from `pg-common.ps1`), `Test-Preflight`, `Invoke-Native($Exe, $Arguments, $WorkingDirectory)` (throws on a non-zero exit code; `npm` is called as `npm.cmd`), `Stop-Api`, `Start-Api`, `Wait-Ready`, `Restore-Previous`.
- **Preflight:** everything in spec step 1, plus scheduled task `QbAutopost` exists and its `Principal.UserId` matches the current user. Do not check `SESSIONNAME`: the owner may reach that same account's session over Remote Desktop, where it reads `RDP-Tcp#n`. On failure, `Refused: ...` and exit 1. Under `-WhatIf`, report each failure as `WRN` and go on, as `start-all.ps1` does in a dry run.
- **Cleanup:** remove a leftover `app.new` before publishing.
- **Backup folder:** read `QuickBooks.BackupFolder` from `appsettings.Production.json` and pass it as `-BackupFolder`. `backup-postgres.ps1`'s own lookup reads `app\appsettings.json`, which does not hold it. Empty is a preflight refusal.
- **Stop-Api:** `Stop-Process` on `QbAutopost.Api`, wait up to 30 s for exit, then throw.
- **Swap:** in a `try`; on any failure call `Restore-Previous`.
- **Wait-Ready:** poll `/api/v1/health/ready` every 5 s up to `-ReadyWaitSeconds`; ready means 200 and a `database` check with `ok: true`.
- **Restore-Previous:** stop the API if running, put `app.old` back as `app` when it exists, run `install-dashboard.ps1 -Rollback` when `dashboard.old` exists, start, wait, and say that a migration is not undone and the dump from this deploy is the way back. With no previous version (first deploy), say so and exit 1 with the API stopped.
- **Step 6 failure:** nothing was swapped, so only `Start-Api`, then exit 1.
- **`-Rollback`:** `Stop-Api`, then `Restore-Previous`. Refuse when neither `.old` exists.
- **Self-update:** after the pull, if `deploy/deploy.ps1` itself changed, stop with exit 1 and say to run it again. PowerShell is already running the old copy.
- **Confirmation prompt:** `Read-Host` unless `-Yes`, naming both old and new commits, and saying to deploy when nobody is posting.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/QbAutopost.Core.Tests --filter WindowsDeployScriptTests`
Expected: PASS.

- [ ] **Step 5: Check that it parses and dry-runs on Windows PowerShell 5.1**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File deploy\deploy.ps1 -WhatIf`
Expected: exit 0, every step printed, preflight failures shown as `WRN`, nothing changed.

- [ ] **Step 6: Commit** `T-1602: deploy.ps1, one-command release on the server`

### Task 3: T-1603 First-install runbook, nginx contract, server checklist [server]

**Files:**
- Modify: `docs/runbook.md` (new section "14. First install and releases", after the last section; section 11 gets a one-line pointer for running behind nginx; add `Api:TrustForwardedHeaders` to the 3.1 table)
- Create: `docs/testing/T-1603.server-checklist.md`
- Modify: `docs/tracker.md` (M16 row in Milestones, M16 section with T-1601 to T-1603, Session log lines; Q-row for `QbXmlVersion` on Enterprise 24)
- Test: `tests/QbAutopost.Core.Tests/Architecture/WindowsDeployScriptTests.cs`

**Interfaces:**
- Consumes: Task 1's setting, Task 2's command line.

- [ ] **Step 1: Write the failing tests**

```csharp
Should_DocumentFirstInstall_When_ReadingRunbook   // contains "deploy.ps1", "TrustForwardedHeaders", "appsettings.Production.json",
                                                  // "Extraction__Enabled", "proxy_read_timeout 600s", "X-Forwarded-For $remote_addr"
Should_WarnThatPostAlwaysPosts_When_ReadingRunbook // contains "Post button always posts"
Should_ListTheServerChecks_When_ReadingChecklist  // T-1603 checklist contains "Rollback", "appsettings.Production.json", "Secure", "QbXmlVersion"
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/QbAutopost.Core.Tests --filter WindowsDeployScriptTests`
Expected: the three new tests FAIL.

- [ ] **Step 3: Write the docs**

Runbook section 14 carries spec section 9's 12 steps in order, section 6's nginx block and the release command, and states the T-609 decision and its safeguards. The checklist's items, each with what to see:

1. The startup log shows values from `appsettings.Production.json` (proves the file is read; nothing has confirmed this on a real install).
2. A sign-in from an accountant's PC survives a reload (the `Secure` cookie over nginx).
3. Audit shows the PC's address, not `127.0.0.1`.
4. A 10 MB PDF uploads and extracts (`T-1215` checklist).
5. A post of a small statement appears in QuickBooks. If it fails on version, set `QbXmlVersion` to `16.0` and record it.
6. A second deploy with no changes completes. A locked `app` folder (hold a file open) puts the old version back. `-Rollback` restores the previous version.

- [ ] **Step 4: Run all tests**

Run: `dotnet build -warnaserror` then `dotnet test`
Expected: 0 warnings, all pass.

- [ ] **Step 5: Set the tracker rows and commit**

T-1601 `done`, with the test counts. T-1602 and T-1603 `ready-for-human`. Commit `T-1603: first-install runbook, nginx contract and server checklist`.
