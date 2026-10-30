#Requires -Version 5.1
<#
.SYNOPSIS
  Releases the API and the dashboard on the Windows server with one command (docs/windows-deploy.md section 8, T-1602).

.DESCRIPTION
  Run it from a PowerShell window of the auto-logon account, at the desktop or over Remote Desktop. No elevation. In order:
    1. Preflight, changes nothing. Any failure is 'Refused: ...' and exit 1.
    2. git pull --ff-only of -Branch in both clones. If this script itself changed, it stops: run it again.
    3. Build while the API stays live: the dashboard, then the API into app.new. A failure stops here; nothing live changed.
    4. Confirm, unless -Yes. Deploy when nobody is posting: an interrupted post is marked unknown on the next start.
    5. Stop the API.
    6. Back up the database, then migrate. A failure starts the old program again, untouched, and exits 1.
    7. Swap: app becomes app.old, app.new becomes app, then the dashboard is installed.
    8. Start through the scheduled task QbAutopost (the API must run in the desktop session), then wait for
       /api/v1/health/ready to answer 200 with its database check ok.
    9. A failure at 7 or 8 puts the previous version back. A migration is not undone: the message names the dump.
  -Rollback does steps 5, 9 and 8 for the previous version, after the same confirmation.
  The script changes only the app and dashboard folders, the two clones and the logs. It prints no secret.
  Use -WhatIf for a dry run: preflight failures are warnings, every step is printed and nothing is changed.

  Exit codes: 0 released (or rolled back) and ready, or a dry run; 1 anything else.

.EXAMPLE
  .\deploy\deploy.ps1 -WhatIf
  .\deploy\deploy.ps1
  .\deploy\deploy.ps1 -Runtime win-x86 -Yes
  .\deploy\deploy.ps1 -Rollback
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('win-x64', 'win-x86')]
    [string] $Runtime = 'win-x64',
    [string] $Branch = 'main',
    [switch] $Rollback,
    [switch] $Yes,
    [string] $Root = 'C:\qb-autopost',
    [string] $ApiBaseUrl = 'http://127.0.0.1:5080',
    [int] $ReadyWaitSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'pg-common.ps1')

$apiRepo = Join-Path $Root 'src\qbautopost'
$webRepo = Join-Path $Root 'src\qbpost_dashboard'
$liveSettings = Join-Path $Root 'appsettings.Production.json'
$appLive = Join-Path $Root 'app'
$appNew = Join-Path $Root 'app.new'
$appOld = Join-Path $Root 'app.old'
$appFailed = Join-Path $Root 'app.failed'
$dashboardLive = Join-Path $Root 'dashboard'
$dashboardOld = Join-Path $Root 'dashboard.old'
$dist = Join-Path $webRepo 'dist'
$logDir = Join-Path $Root 'logs'
# The helper scripts run from the clone, so a release uses the migrations it just pulled.
$scriptDir = Join-Path $apiRepo 'deploy'
$taskName = 'QbAutopost'
$apiProcess = 'QbAutopost.Api'
$postgresService = 'postgresql-x64-16'

# What this run has replaced so far: Restore-Previous undoes these and nothing else.
$script:AppMoved = $false
$script:NewAppLive = $false
$script:DashboardInstalled = $false
$script:BackupFolder = ''
$script:Refusals = 0

$logFile = ''
if (-not $WhatIfPreference) {
    try {
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        $logFile = Join-Path $logDir ('deploy-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    }
    catch { $logFile = '' }
}
function Say([string] $Message, [string] $Level = 'INF') { Write-LogLine -Tag 'deploy' -Message $Message -Level $Level -LogFile $logFile }

function Invoke-Native {
    # Runs one program, shows and logs its output, returns its standard output lines, throws on a non-zero exit code.
    param([string] $Exe, [string[]] $Arguments, [string] $WorkingDirectory)
    # A missing program is not an exit code: it must fail here, while errors still stop the script.
    Get-Command $Exe | Out-Null
    $stdout = New-Object System.Collections.Generic.List[string]
    Push-Location -LiteralPath $WorkingDirectory
    try {
        # git and npm write progress to stderr. Under 'Stop', 2>&1 on Windows PowerShell 5.1 would turn that into a failure.
        $ErrorActionPreference = 'Continue'
        & $Exe @Arguments 2>&1 | ForEach-Object {
            Say ([string] $_)
            if ($_ -isnot [System.Management.Automation.ErrorRecord]) { $stdout.Add([string] $_) }
        }
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($code -ne 0) { throw ('{0} {1} failed with exit code {2}.' -f $Exe, ($Arguments -join ' '), $code) }
    return $stdout.ToArray()
}

function Invoke-DeployScript {
    # Runs a sibling script. It reports through its exit code, or throws when its Write-Error stops it.
    param([string] $Name, [hashtable] $Parameters)
    $global:LASTEXITCODE = 0
    & (Join-Path $scriptDir $Name) @Parameters | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('{0} failed with exit code {1}.' -f $Name, $LASTEXITCODE) }
}

function Add-Refusal([string] $Reason) {
    if ($WhatIfPreference) {
        Say "Would refuse: $Reason" 'WRN'
    }
    else {
        Say "Refused: $Reason" 'ERR'
    }
    $script:Refusals++
}

function Test-Preflight {
    # Changes nothing. Reports every failure at once, so one run shows all that needs fixing.
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Add-Refusal "the scheduled task '$taskName' does not exist (deploy\install-task.ps1)."
    }
    else {
        # Compared by SID: the task may hold 'user' where the session has 'MACHINE\user'.
        $taskUser = [string] $task.Principal.UserId
        try {
            $owner = (New-Object System.Security.Principal.NTAccount($taskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
            $me = [System.Security.Principal.WindowsIdentity]::GetCurrent()
            if ($owner -ne $me.User.Value) {
                Add-Refusal "this is account '$($me.Name)'; the task '$taskName' runs as '$taskUser'. Run this as that account."
            }
        }
        catch {
            Add-Refusal "could not resolve the task's account '$taskUser': $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $liveSettings) {
        try {
            $settings = Get-Content -Raw -LiteralPath $liveSettings | ConvertFrom-Json
            $script:BackupFolder = [string] $settings.QuickBooks.BackupFolder
        }
        catch { $script:BackupFolder = '' }
    }

    if ($Rollback) {
        if (-not (Test-Path -LiteralPath $appOld) -and -not (Test-Path -LiteralPath $dashboardOld)) {
            Add-Refusal "there is no previous version: neither '$appOld' nor '$dashboardOld' exists."
        }
    }
    else {
        foreach ($tool in 'git', 'node', 'npm.cmd', 'dotnet') {
            if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Add-Refusal "$tool is not on the PATH." }
        }
        if (Get-Command 'node' -ErrorAction SilentlyContinue) {
            try {
                $nodeVersion = [string] (@(Invoke-Native 'node' @('--version') $here)[0])
                if ($nodeVersion -notmatch '^v22\.') { Add-Refusal "node 22 is required; this is '$nodeVersion'." }
            }
            catch { Add-Refusal $_.Exception.Message }
        }
        if (Get-Command 'dotnet' -ErrorAction SilentlyContinue) {
            try {
                $sdks = @(Invoke-Native 'dotnet' @('--list-sdks') $here)
                if (-not ($sdks | Where-Object { $_ -match '^8\.' })) { Add-Refusal 'the .NET 8 SDK is not installed.' }
            }
            catch { Add-Refusal $_.Exception.Message }
        }
        if (-not (Get-Service -Name $postgresService -ErrorAction SilentlyContinue)) {
            Add-Refusal "the PostgreSQL service '$postgresService' does not exist (deploy\install-postgres.ps1)."
        }
        if (-not (Test-Path -LiteralPath $liveSettings)) {
            Add-Refusal "'$liveSettings' does not exist."
        }
        elseif ([string]::IsNullOrWhiteSpace($script:BackupFolder)) {
            Add-Refusal "QuickBooks.BackupFolder is empty or unreadable in '$liveSettings'."
        }
        foreach ($variable in 'QBAUTOPOST__Database__ConnectionString', 'QBAUTOPOST_MIGRATION_CONNECTION') {
            if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variable))) {
                Add-Refusal "$variable is not set (deploy\install-postgres.ps1). If it was set just now, open a new PowerShell window."
            }
        }
        foreach ($repo in $apiRepo, $webRepo) {
            if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
                Add-Refusal "'$repo' is not a clone."
            }
            elseif (Get-Command 'git' -ErrorAction SilentlyContinue) {
                try {
                    $dirty = @(Invoke-Native 'git' @('status', '--porcelain') $repo)
                    if ($dirty.Count -gt 0) { Add-Refusal "'$repo' has uncommitted changes." }
                }
                catch { Add-Refusal $_.Exception.Message }
            }
        }
    }

    if ($script:Refusals -gt 0 -and -not $WhatIfPreference) { exit 1 }
    if ($script:Refusals -eq 0) { Say 'Preflight passed.' }
}

function Stop-Api {
    $running = @(Get-Process -Name $apiProcess -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        Say 'The API is not running.'
        return
    }
    Say ('Stopping the API (pid {0}).' -f (($running | ForEach-Object { $_.Id }) -join ', '))
    # A failure to stop shows up in the wait below.
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Name $apiProcess -ErrorAction SilentlyContinue).Count -eq 0) {
            Say 'The API has stopped.'
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "The API did not exit within 30 s."
}

function Start-Api {
    # The task ignores a new start while an earlier one still runs, and its start-all.ps1 can outlast our wait for a
    # program that never got ready. The API is stopped by now, so that earlier run is only polling: clear it.
    $task = Get-ScheduledTask -TaskName $taskName
    if ($task.State -eq 'Running') {
        Say "Task '$taskName' is still running from an earlier start; stopping it." 'WRN'
        Stop-ScheduledTask -TaskName $taskName | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
            if ((Get-Date) -ge $deadline) { throw "Task '$taskName' did not stop within 15 s." }
            Start-Sleep -Seconds 1
        }
    }
    Say "Starting the API through the scheduled task '$taskName'."
    Start-ScheduledTask -TaskName $taskName | Out-Null
}

function Wait-Ready {
    # Ready means 200 and a database check with ok: true.
    $url = $ApiBaseUrl.TrimEnd('/') + '/api/v1/health/ready'
    $deadline = (Get-Date).AddSeconds($ReadyWaitSeconds)
    $last = 'no answer'
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            $check = @(($response.Content | ConvertFrom-Json).checks | Where-Object { $_.name -eq 'database' })
            if ($check.Count -eq 0) {
                $last = 'the answer has no database check'
            }
            elseif ($response.StatusCode -eq 200 -and $check[0].ok) {
                Say "GET $url -> 200, database ok."
                return $true
            }
            else {
                $last = "HTTP $($response.StatusCode), database: $($check[0].message)"
            }
        }
        catch {
            # Windows PowerShell throws on a 503 as well, so a not-ready answer lands here too.
            $last = $_.Exception.Message
        }
        Start-Sleep -Seconds 5
    }
    Say "GET $url not ready within $ReadyWaitSeconds s (last: $last)." 'ERR'
    return $false
}

function Write-MigrationWarning {
    $dumps = 'the dump folder'
    if ($script:BackupFolder) { $dumps = "'" + (Join-Path $script:BackupFolder 'postgres') + "'" }
    Say "A migration is not undone. If the previous version cannot work with the current schema, restore this deploy's dump from $dumps." 'WRN'
}

function Start-Unchanged {
    # Step 5 or 6 failed: nothing was swapped, so only the start is left. Returns nothing: the deploy has failed either way.
    if (-not (Test-Path -LiteralPath $appLive)) {
        Say "There is no '$appLive' yet (first deploy): nothing to start. Fix the failure and run this again." 'ERR'
        return
    }
    try {
        Start-Api
        if (Wait-Ready) {
            Say 'The old program runs again; its folders were not touched.' 'WRN'
        }
        else {
            Say 'The old program did not become ready.' 'ERR'
        }
    }
    catch {
        Say "Could not start the old program: $($_.Exception.Message)" 'ERR'
    }
}

function Restore-Previous {
    # Puts back what this run replaced and starts it. Returns $true when the previous version is ready.
    try {
        Stop-Api
        $replaced = $script:AppMoved -or $script:NewAppLive -or -not (Test-Path -LiteralPath $appLive)
        if ($replaced) {
            if (-not (Test-Path -LiteralPath $appOld)) {
                Say "No previous version at '$appOld' (first deploy): nothing to go back to. The API stays stopped." 'ERR'
                return $false
            }
            if (Test-Path -LiteralPath $appFailed) { Remove-Item -LiteralPath $appFailed -Recurse -Force }
            if (Test-Path -LiteralPath $appLive) { Rename-Item -LiteralPath $appLive -NewName 'app.failed' }
            Rename-Item -LiteralPath $appOld -NewName 'app'
            Say "Restored the previous program from '$appOld'."
        }
    }
    catch {
        Say "Could not restore the program: $($_.Exception.Message). Check '$appLive' and '$appOld' by hand." 'ERR'
        return $false
    }

    # Only when this run installed it: otherwise dashboard.old is older than the live one. A failure here must not
    # keep the program down.
    if ($script:DashboardInstalled -and (Test-Path -LiteralPath $dashboardOld)) {
        try {
            Invoke-DeployScript 'install-dashboard.ps1' @{ Rollback = $true; Target = $dashboardLive }
            Say "Restored the previous dashboard from '$dashboardOld'."
        }
        catch {
            Say "Could not restore the dashboard: $($_.Exception.Message)" 'ERR'
        }
    }

    Write-MigrationWarning
    try {
        Start-Api
        return (Wait-Ready)
    }
    catch {
        Say "Could not start the previous version: $($_.Exception.Message)" 'ERR'
        return $false
    }
}

function Confirm-Release([string] $Question) {
    if ($Yes) { return $true }
    return ((Read-Host "$Question Type yes to go on") -eq 'yes')
}

function Invoke-Rollback {
    # Returns the exit code.
    $script:AppMoved = Test-Path -LiteralPath $appOld
    $script:DashboardInstalled = Test-Path -LiteralPath $dashboardOld
    if (-not (Confirm-Release 'This stops the API and goes back to the previous version. Do it when nobody is posting.')) {
        Say 'Cancelled. Nothing was changed.'
        return 1
    }
    try {
        Stop-Api
    }
    catch {
        Say $_.Exception.Message 'ERR'
        return 1
    }
    if (Restore-Previous) {
        Say 'Rolled back: the previous version is ready.'
        return 0
    }
    Say 'The rollback did not end ready. Read the lines above and the logs.' 'ERR'
    return 1
}

function Show-Plan {
    if ($Rollback) {
        Say "Dry run: would stop the API, put '$appOld' back as '$appLive' and roll the dashboard back when '$dashboardOld' exists."
    }
    else {
        Say "Dry run: 2. would pull '$Branch' with --ff-only in '$apiRepo' and '$webRepo', and stop if this script changed."
        Say "Dry run: 3. would build the dashboard into '$dist' and publish the API for $Runtime into '$appNew', removing any leftover first."
        Say 'Dry run: 4. would ask for confirmation, unless -Yes.'
        Say "Dry run: 5. would stop $apiProcess and wait up to 30 s."
        Say "Dry run: 6. would back up the database to '$($script:BackupFolder)', then apply the migrations."
        Say "Dry run: 7. would swap '$appLive' to '$appOld', '$appNew' to '$appLive', and install the dashboard."
    }
    Say "Dry run: 8. would start task '$taskName' and wait up to $ReadyWaitSeconds s for $ApiBaseUrl/api/v1/health/ready and its database check."
    if (-not $Rollback) { Say 'Dry run: 9. on a failure at 7 or 8, would put the previous version back and start it.' }
    Say 'Dry run: nothing was changed.'
}

# Main flow
if (-not ([Uri] $ApiBaseUrl).IsLoopback) {
    Say "Refused: ApiBaseUrl '$ApiBaseUrl' is not a loopback address; the API binds to 127.0.0.1." 'ERR'
    exit 1
}
Say "Root=$Root Runtime=$Runtime Branch=$Branch Rollback=$Rollback"

Say 'Step 1: preflight.'
Test-Preflight

if (-not $PSCmdlet.ShouldProcess($Root, 'Release the API and the dashboard')) {
    Show-Plan
    exit 0
}

if ($Rollback) { exit (Invoke-Rollback) }

try {
    Say 'Step 2: pulling.'
    $commits = @{}
    foreach ($repo in $apiRepo, $webRepo) {
        $old = @(Invoke-Native 'git' @('rev-parse', '--short', 'HEAD') $repo)[0]
        Invoke-Native 'git' @('checkout', $Branch) $repo | Out-Null
        Invoke-Native 'git' @('pull', '--ff-only', 'origin', $Branch) $repo | Out-Null
        $new = @(Invoke-Native 'git' @('rev-parse', '--short', 'HEAD') $repo)[0]
        $commits[$repo] = "$old -> $new"
        Say "$repo : $old -> $new"
        if ($repo -eq $apiRepo -and $old -ne $new) {
            # PowerShell is already running the old copy of this file.
            if (@(Invoke-Native 'git' @('diff', '--name-only', $old, $new, '--', 'deploy/deploy.ps1') $repo).Count -gt 0) {
                Say 'deploy.ps1 itself changed in this pull. Nothing live changed: run it again.' 'ERR'
                exit 1
            }
        }
    }

    Say 'Step 3: building; the API stays live.'
    Invoke-Native 'npm.cmd' @('ci') $webRepo | Out-Null
    Invoke-Native 'npm.cmd' @('run', 'build') $webRepo | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html'))) { throw "The dashboard build left no index.html in '$dist'." }

    if (Test-Path -LiteralPath $appNew) { Remove-Item -LiteralPath $appNew -Recurse -Force }
    Invoke-Native 'dotnet' @('publish', 'src/QbAutopost.Api', '-c', 'Release', '-r', $Runtime, '--self-contained', 'false', '-o', $appNew) $apiRepo | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $appNew 'QbAutopost.Api.exe'))) { throw "The publish left no QbAutopost.Api.exe in '$appNew'." }
    $development = Join-Path $appNew 'appsettings.Development.json'
    if (Test-Path -LiteralPath $development) { Remove-Item -LiteralPath $development -Force }
    Copy-Item -LiteralPath $liveSettings -Destination $appNew -Force

    Say 'Step 4: confirmation.'
    $question = "API $($commits[$apiRepo]), dashboard $($commits[$webRepo]). Deploy when nobody is posting: this stops the API."
    if (-not (Confirm-Release $question)) {
        Say 'Cancelled. Nothing live changed.' 'WRN'
        exit 1
    }
}
catch {
    Say "$($_.Exception.Message) Nothing live changed." 'ERR'
    exit 1
}

try {
    Say 'Step 5: stopping the API.'
    Stop-Api
    Say 'Step 6: backing up, then migrating.'
    Invoke-DeployScript 'backup-postgres.ps1' @{ BackupFolder = $script:BackupFolder; LogDir = $logDir }
    Invoke-DeployScript 'apply-migrations.ps1' @{ LogDir = $logDir }
}
catch {
    Say $_.Exception.Message 'ERR'
    Start-Unchanged
    exit 1
}

try {
    Say 'Step 7: swapping.'
    if (Test-Path -LiteralPath $appLive) {
        if (Test-Path -LiteralPath $appOld) { Remove-Item -LiteralPath $appOld -Recurse -Force }
        Rename-Item -LiteralPath $appLive -NewName 'app.old'
        $script:AppMoved = $true
    }
    Rename-Item -LiteralPath $appNew -NewName 'app'
    $script:NewAppLive = $true
    Invoke-DeployScript 'install-dashboard.ps1' @{ Source = $dist; Target = $dashboardLive }
    $script:DashboardInstalled = $true
}
catch {
    Say "The swap failed: $($_.Exception.Message)" 'ERR'
    [void] (Restore-Previous)
    exit 1
}

$ready = $false
try {
    Say 'Step 8: starting.'
    Start-Api
    $ready = Wait-Ready
}
catch {
    Say $_.Exception.Message 'ERR'
}
if (-not $ready) {
    Say 'The new version did not become ready; going back to the previous one.' 'ERR'
    [void] (Restore-Previous)
    exit 1
}

Say "Released: API $($commits[$apiRepo]), dashboard $($commits[$webRepo])."
exit 0
