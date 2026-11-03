# Windows server deployment: design

Date: 2026-10-30. Status: draft for review. Covers this API repo and the dashboard repo (`qbpost_dashboard`).

## 1. Why

The API and dashboard work together in Docker with a simulated QuickBooks. They now need to run on the real Windows
server, against the real QuickBooks Desktop company file, for accountants on their own PCs. The owner can sign in to
GitHub on the server and wants the lowest-effort way to get from there to a running system, and to release again later.

## 2. Goals, non-goals, anti-goals

**Goals**
- One command on the server releases the current code of both repos: `.\deploy\deploy.ps1`.
- A one-time first-install checklist that says exactly what to do once, in order.
- A failed build changes nothing that is live. A failed start returns to the previous version.

**Non-goals**
- No CI/CD, self-hosted runner, webhook or automatic deploy. A person runs the command.
- n8n is not part of this rollout (owner's decision). Connecting it later follows runbook 10.6 and 11.5.
- No Docker on the server (the runbook already removed it). No IIS site: IIS stays installed and unused.
- nginx, the domain and the certificate are the owner's work. This design only states what they must do (section 6).

**Anti-goals: a failure even if it technically works**
- Real posting without a recent backup or by more than the one trusted person, until the first posts are checked. The
  owner chose to run on the real company file from day one, so the first real post is also the first real-QuickBooks test
  (the runbook's T-609 on a copy is knowingly skipped). `DryRunDefault` does not protect this: the dashboard's Post button
  always posts (`SyncPostRunner`). The safeguards are `QuickBooks:BackupFolder` (posting is refused without a `.QBB` newer
  than `BackupMaxAgeHours`), a backup taken by hand before the first post, and one account with posting rights.
- A deploy that loses a post, posts twice, or overwrites the server's own settings or data.
- The app reachable from the internet (runbook 11.2: never a router port forward).
- A secret in the repo, a script, or the console.

## 3. Facts this rests on

- The API serves the dashboard's static files from `Dashboard:Path` at `/` (runbook 11). One origin, no CORS.
- The API must run in the auto-logon account's interactive session through the scheduled task `QbAutopost`, because the
  QuickBooks SDK needs a desktop. It is never a service and cannot be hosted in IIS (runbook 2.1, 2.4).
- Existing scripts do the hard parts: `install-postgres.ps1`, `apply-migrations.ps1` (refuses while the API runs),
  `install-dashboard.ps1` (keeps `dashboard.old`, has `-Rollback`), `install-task.ps1`, `start-all.ps1`,
  `backup-postgres.ps1`.
- On the server (owner's word): QuickBooks Desktop with the company file, IIS, and .NET.
- The server runs QuickBooks Enterprise Solutions 24.0 R16p, 64-bit, so the build is `win-x64` and `-Runtime` stays at its
  default. Enterprise is not in runbook 2.1's list of tested editions (Pro, Premier, Accountant); the first post (section
  9) is what proves it, and `QuickBooks:QbXmlVersion` may need `16.0` (runbook 3.1).

## 4. Decided by the owner

- Runs on the Windows server that has QuickBooks installed.
- Accountants use their own PCs.
- The owner provides a domain and sets up nginx in front of the app.
- Code reaches the server from GitHub.
- QuickBooks stays open on the server on the company file, and only the server uses that file. No auto-start step and no
  multi-user handling are needed; the checklist says to leave it open.

## 5. Precondition: GitHub must hold the code

As of this design, neither repo's `main` can be deployed:
- API: `feature/m15-qb-lists-page` is on no remote branch and is 30 commits ahead of `origin/main`.
- Dashboard: `feature/error-handling-audit` is pushed, but about 50 files are uncommitted, including the lists page.

Both must be committed, pushed and merged to `main`, and `deploy.ps1` deploys `main` of each repo. This work is the owner's
decision and follows each repo's commit rules; it is not done by the script.

## 6. Architecture

```
Accountant PC ──HTTPS──> nginx (same server) ──HTTP──> API on 127.0.0.1:5080 ──COM──> QuickBooks Desktop
                                                        ├─ serves C:\qb-autopost\dashboard at /
                                                        └─ PostgreSQL 16 on 127.0.0.1
```

**Contract for the owner's nginx.** nginx runs on the same server, since the API stays bound to loopback. It proxies
everything to `http://127.0.0.1:5080`, with no static-file split:

```nginx
client_max_body_size 11m;              # one PDF upload is at most 10 MB
location / {
    proxy_pass http://127.0.0.1:5080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;   # overwrite: a client cannot supply its own
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 600s;           # runbook 11.4: at least 10 minutes, a post must never be cut off
    proxy_send_timeout 600s;
    proxy_buffering off;
}
```

HTTPS is mandatory: the refresh cookie is `Secure` and browsers drop it on plain HTTP, so sign-in would not survive a
reload. The server's firewall opens nginx's port to the office subnet only. A public DNS name is fine; a public route to
the server is not. The API's own `Api:Tls`, port 5443 and `Api:Bind` settings of runbook 11.2 are not used.

## 7. API change: real client addresses

Behind nginx every request's `RemoteIpAddress` is `127.0.0.1`. The failed-authentication brake (`AuthBrake`), the
address-partitioned rate limits and the audit log all use that value, so ten failed attempts by one person would block
every caller, n8n included, for five minutes, and the audit would lose addresses.

Add a setting `Api:TrustForwardedHeaders` (default `false`). When `true`, the host uses ASP.NET Core's
`UseForwardedHeaders` for `X-Forwarded-For` and `X-Forwarded-Proto`, first in the pipeline, trusting loopback proxies
only (the framework default), so the header is ignored from anywhere else. `Microsoft.AspNetCore.HttpOverrides` ships in
the shared framework: no new NuGet package. Default off keeps existing installs unchanged.

## 8. `deploy/deploy.ps1`

Style matches the other `deploy\*.ps1`: `#Requires -Version 5.1`, `SupportsShouldProcess` (`-WhatIf` prints and changes
nothing), strict mode, a log under `C:\qb-autopost\logs`, exit 0 or 1, never prints a secret.

**Layout on the server**

| Path | Holds |
|---|---|
| `C:\qb-autopost\src\qbautopost` | clone of this repo |
| `C:\qb-autopost\src\qbpost_dashboard` | clone of the dashboard repo |
| `C:\qb-autopost\appsettings.Production.json` | the server's own settings, outside the swapped folders |
| `C:\qb-autopost\app`, `app.new`, `app.old` | live, staged and previous program |
| `C:\qb-autopost\app.old.prev` | the older `app.old`, during a release only; a leftover means an interrupted run |
| `C:\qb-autopost\dashboard`, `dashboard.old` | as `install-dashboard.ps1` |

**Parameters**: `-Runtime win-x64|win-x86` (default `win-x64`), `-Branch` (default `main`, for both repos), `-Rollback`,
`-Yes` (skip the confirmation), and path overrides.

**Steps, in order**

1. **Preflight, changes nothing.** The script runs as the auto-logon account, at the desktop or over Remote Desktop (my
   call: the task, the API and the clones then share one account, with no elevation), and refuses any other account. The
   scheduled task `QbAutopost` must exist.
   `git`, `node` 22 or newer, `npm` and the .NET 8 SDK are present; the PostgreSQL service exists;
   `appsettings.Production.json` exists and its `Paths:Clients` is an absolute path (otherwise a release moves the API
   callers' key hashes into `app.old`); `QBAUTOPOST__Database__ConnectionString` and
   `QBAUTOPOST_MIGRATION_CONNECTION` are set; both clones have a clean tree. A leftover `app.old.prev` is noted, not
   refused: after the confirmation of step 4 a release deletes it when `app.old` exists, and otherwise renames it back to
   `app.old`.
2. **Fetch.** `git pull --ff-only` on `-Branch` in both clones. Print the old and new commit of each.
3. **Build, API still live.** In the dashboard: `npm ci`, `npm run build`. In the API:
   `dotnet publish src/QbAutopost.Api -c Release -r <Runtime> --self-contained false -o C:\qb-autopost\app.new`, delete
   `appsettings.Development.json`, copy in `appsettings.Production.json`. Any failure stops here; nothing live changed.
4. **Confirm.** Print what will change and ask, unless `-Yes`. Stopping the API during a post is what this protects:
   deploy when nobody is posting. Recovery on the next start marks an interrupted post `unknown`, never posts it again.
5. **Stop the API.** Stop the `QbAutopost.Api` process and wait for it to exit.
6. **Back up and migrate.** Run `backup-postgres.ps1 -BackupFolder <QuickBooks:BackupFolder from
   appsettings.Production.json>`, then `apply-migrations.ps1`. Failure restarts the old program,
   untouched, and exits 1.
7. **Swap.** `app.old` becomes `app.old.prev`, `app` becomes `app.old`, `app.new` becomes `app`, then
   `install-dashboard.ps1 -Source <dist>`. The rename of `app` is the one a locked file fails, so the only rollback
   target is set aside, not deleted.
8. **Start and check.** `Start-ScheduledTask -TaskName QbAutopost`, so the API runs in the desktop session, then wait for
   `GET /api/v1/health/ready` = 200 and for its `database` check to be ok. On a 503 the message names every failed check
   of severity `error`. Once ready, delete `app.old.prev`; a failure of that delete is only a warning.
9. **On failure at 7 or 8,** restore `app.old` and `install-dashboard.ps1 -Rollback`, rename `app.old.prev` back to
   `app.old`, start again, report it, exit 1. A
   migration is not undone: migrations must stay compatible with the previous release, or the rollback needs a restore
   from the backup of step 6, which the message says.

`-Rollback` alone does steps 5, 9 and 8 for the previous version. It refuses unless `app.old` exists; a dashboard-only
rollback is `install-dashboard.ps1 -Rollback`.

The script never touches `rules.json`, `ledger.json`, `clients.json`, the jobs, the company file, nginx, the firewall or
any machine variable.

## 9. First install, once

Added to the runbook as a new section, in this order:

1. Install Git, Node 22 or newer and the .NET 8 SDK; sign in to GitHub; clone both repos into `C:\qb-autopost\src`.
2. Leave QuickBooks open on the company file, and keep it that way.
3. Run `install-postgres.ps1` (section 10.2): it creates the database, roles, connection variables, JWT signing key and the
   daily backup task.
4. Create `C:\qb-autopost\appsettings.Production.json`: `Company:Name` and `Company:FilePath` for the real company (the
   defaults are the Tropicana sample), `Api:TrustForwardedHeaders: true`, `Paths:AllowedJobRoots`, `QuickBooks:BackupFolder`,
   and `Paths:Clients` as `C:\qb-autopost\clients.json` (its default is relative to the program folder, which the swap
   replaces). Leave `DryRunDefault` alone.
5. Copy `samples/rules.json` to `C:\qb-autopost\rules.json` and edit it.
6. Set the secrets as environment variables: `QBAUTOPOST__Api__ApiKey`, the bootstrap admin email and password,
   `OpenRouter__ApiKey`, `OpenRouter__Enabled=true` and `Extraction__Enabled=true`. Upload is on from day one by the
   owner's decision, knowing statement text goes to OpenRouter; without n8n it is the only way statements get in. Remove
   the bootstrap password after first sign-in (runbook 10.4). Verify upload with `docs/testing/T-1215.server-checklist.md`.
7. Set up auto-logon for the account and run `install-task.ps1` from an administrator's elevated PowerShell, with
   `-UserId '<MACHINE>\<account>'` when the account is not an administrator.
8. Run `deploy.ps1` for the first version; because the dashboard folder is new, it starts the API after installing it.
9. Approve the QuickBooks certificate dialog on the server's desktop, as runbook section 4 sets out (QuickBooks Admin
   login, then `scripts\qb-server-check.ps1`).
10. Set up nginx and the domain per section 6. Sign in from an accountant's PC.
11. Make a QuickBooks backup into `QuickBooks:BackupFolder`. Give posting rights to one trusted user; other accounts stay
    view-only until step 12 passes.
12. That user posts one small statement and checks it in QuickBooks. Only then widen posting rights.

## 10. Testing

- `Api:TrustForwardedHeaders`: integration tests that the address comes from the header when on and from a loopback
  proxy, is ignored when off, and that an existing install's behaviour is unchanged. `dotnet test` stays green on Linux.
- `deploy.ps1`: run `-WhatIf` and the failure paths on a Windows dev machine with `-Runtime win-x64`, with a fake
  scheduled task where needed. It cannot be tested against real QuickBooks off the server, so it gets a
  `docs/testing/<task>.server-checklist.md` and its tracker row ends `ready-for-human`.

## 11. Left to the builder

Task ids and tracker rows; how the old program is restarted when step 6 fails; how long to wait for the process to exit;
how the build output of the two repos is passed between steps; where the failure paths are simulated.
