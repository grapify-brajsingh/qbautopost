#Requires -Version 5.1
<#
.SYNOPSIS
  Applies deploy\migrations.sql and deploy\grants.sql to the QbAutopost database (saved-requests spec 5.5, T-1016).

.DESCRIPTION
  The app never migrates by itself: a schema change is a deploy step. This script
    1. refuses when the API is running (a half-migrated schema under a live app is the one thing to avoid);
    2. runs the idempotent script deploy\migrations.sql as the OWNER role (QBAUTOPOST_MIGRATION_CONNECTION);
    3. re-runs deploy\grants.sql, because a GRANT covers only the tables that exist when it runs;
    4. checks that every migration in the script is recorded in "__EFMigrationsHistory".
  Safe to run again: the script skips migrations already applied.
  The owner password is read from the environment variable and handed to psql through PGPASSWORD; it is never printed.
  Use -WhatIf for a dry run: it prints every step and changes nothing.

  Exit codes: 0 applied and current; 1 refused or failed.

.EXAMPLE
  .\deploy\apply-migrations.ps1 -WhatIf
  .\deploy\apply-migrations.ps1
  .\deploy\apply-migrations.ps1 -PgBin 'D:\PostgreSQL\16\bin'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ScriptPath = '',
    [string] $GrantsPath = '',
    [string] $OwnerConnection = $env:QBAUTOPOST_MIGRATION_CONNECTION,
    [string] $PgBin = '',
    [string] $ApiProcessName = 'QbAutopost.Api',
    [string] $LogDir = 'C:\qb-autopost\logs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'pg-common.ps1')
if (-not $ScriptPath) { $ScriptPath = Join-Path $here 'migrations.sql' }
if (-not $GrantsPath) { $GrantsPath = Join-Path $here 'grants.sql' }

$logFile = ''
if (-not $WhatIfPreference) {
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
        $logFile = Join-Path $LogDir ('apply-migrations-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    }
    catch { $logFile = '' }
}
function Say([string] $Message, [string] $Level = 'INF') { Write-LogLine -Tag 'apply-migrations' -Message $Message -Level $Level -LogFile $logFile }

foreach ($file in @($ScriptPath, $GrantsPath)) {
    if (-not (Test-Path -LiteralPath $file)) {
        Say "'$file' not found." 'ERR'
        exit 1
    }
}
if ([string]::IsNullOrWhiteSpace($OwnerConnection)) {
    Say 'QBAUTOPOST_MIGRATION_CONNECTION is not set (deploy\install-postgres.ps1 sets it). Pass -OwnerConnection or open a new PowerShell window.' 'ERR'
    exit 1
}

$owner = ConvertFrom-NpgsqlConnection -ConnectionString $OwnerConnection
if ($owner.Username -ne 'qbautopost_owner') {
    Say "The migration connection must be the owner role qbautopost_owner, not '$($owner.Username)'." 'ERR'
    exit 1
}
$expected = @(Get-MigrationIds -ScriptPath $ScriptPath)
Say ('Target {0}:{1}/{2} as {3}; {4} migration(s) in {5}.' -f $owner.Host, $owner.Port, $owner.Database, $owner.Username, $expected.Count, $ScriptPath)

$running = @(Get-Process -Name $ApiProcessName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say ('The API is running (pid {0}). Stop it first (close its window or Stop-Process -Name {1}), run this again, then start-all.ps1.' -f (($running | ForEach-Object { $_.Id }) -join ', '), $ApiProcessName) 'ERR'
    exit 1
}

if (-not $PSCmdlet.ShouldProcess("$($owner.Database) on $($owner.Host):$($owner.Port)", 'Apply migrations.sql, then grants.sql')) {
    Say 'Dry run: would run migrations.sql, then grants.sql, then compare "__EFMigrationsHistory" with the script. Nothing was changed.'
    exit 0
}

try {
    $psql = Find-PgTool -Name 'psql' -PgBin $PgBin
    Say 'Running migrations.sql.'
    Invoke-Pg -Exe $psql -Connection $owner -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', $ScriptPath) | Out-Null
    Say 'Running grants.sql.'
    Invoke-Pg -Exe $psql -Connection $owner -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', $GrantsPath) | Out-Null

    $applied = @(Invoke-Pg -Exe $psql -Connection $owner -Arguments @('-X', '-q', '-t', '-A', '-c', 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"'))
    $missing = @($expected | Where-Object { $applied -notcontains $_ })
    if ($missing.Count -gt 0) {
        Say ('Not recorded after the run: ' + ($missing -join ', ')) 'ERR'
        exit 1
    }
    Say ('Schema is current: {0} migration(s) applied, latest {1}.' -f $applied.Count, $applied[$applied.Count - 1])
    Say 'Start the API (deploy\start-all.ps1). If the dashboard admin was not created because the database was down, restart the app once.'
    exit 0
}
catch {
    Say $_.Exception.Message 'ERR'
    exit 1
}
