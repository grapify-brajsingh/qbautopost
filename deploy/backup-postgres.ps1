#Requires -Version 5.1
<#
.SYNOPSIS
  Dumps the QbAutopost database to <QuickBooks:BackupFolder>\postgres\qbautopost-yyyyMMdd.dump and keeps the newest 30 (spec 5.4, T-1016).

.DESCRIPTION
  Run daily by the Task Scheduler task QbAutopostPostgresBackup that deploy\install-postgres.ps1 registers. It is not an app
  timer. The dump is custom format (pg_dump -Fc), written to a .partial file, checked with pg_restore --list, then renamed,
  so a failed run never leaves a file that looks like a backup. A second run on the same day replaces that day's dump.
  The owner password comes from QBAUTOPOST_MIGRATION_CONNECTION (machine environment variable) and is never printed.
  Use -WhatIf for a dry run: it prints what it would do and changes nothing.

  Exit codes: 0 dump written and checked; 1 failed (the previous dumps are untouched).

.EXAMPLE
  .\deploy\backup-postgres.ps1 -BackupFolder D:\qb-backups -WhatIf
  .\deploy\backup-postgres.ps1 -BackupFolder D:\qb-backups
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $BackupFolder = '',
    [int] $Keep = 30,
    [string] $OwnerConnection = $env:QBAUTOPOST_MIGRATION_CONNECTION,
    [string] $PgBin = '',
    [string] $AppSettingsPath = 'C:\qb-autopost\app\appsettings.json',
    [string] $LogDir = 'C:\qb-autopost\logs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'pg-common.ps1')

$logFile = ''
if (-not $WhatIfPreference) {
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
        $logFile = Join-Path $LogDir ('backup-postgres-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    }
    catch { $logFile = '' }
}
function Say([string] $Message, [string] $Level = 'INF') { Write-LogLine -Tag 'backup-postgres' -Message $Message -Level $Level -LogFile $logFile }

# The backup folder is QuickBooks:BackupFolder: the argument, else the environment variable, else appsettings.json.
if (-not $BackupFolder) { $BackupFolder = $env:QBAUTOPOST__QuickBooks__BackupFolder }
if (-not $BackupFolder -and (Test-Path -LiteralPath $AppSettingsPath)) {
    try {
        $settings = Get-Content -Raw -LiteralPath $AppSettingsPath | ConvertFrom-Json
        $BackupFolder = [string] $settings.QuickBooks.BackupFolder
    }
    catch { $BackupFolder = '' }
}
if ([string]::IsNullOrWhiteSpace($BackupFolder)) {
    Say 'No backup folder: pass -BackupFolder or set QuickBooks:BackupFolder.' 'ERR'
    exit 1
}
if ($Keep -lt 1) {
    Say '-Keep must be at least 1.' 'ERR'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($OwnerConnection)) {
    Say 'QBAUTOPOST_MIGRATION_CONNECTION is not set (deploy\install-postgres.ps1 sets it as a machine variable).' 'ERR'
    exit 1
}

$target = Join-Path $BackupFolder 'postgres'
$name = 'qbautopost-' + (Get-Date -Format 'yyyyMMdd') + '.dump'
$final = Join-Path $target $name
$partial = $final + '.partial'

if (-not $PSCmdlet.ShouldProcess($final, 'pg_dump -Fc, check, keep the newest ' + $Keep)) {
    Say "Dry run: would write '$final' (through '$partial') and keep the newest $Keep qbautopost-*.dump files in '$target'."
    exit 0
}

try {
    $conn = ConvertFrom-NpgsqlConnection -ConnectionString $OwnerConnection
    $pgDump = Find-PgTool -Name 'pg_dump' -PgBin $PgBin
    $pgRestore = Find-PgTool -Name 'pg_restore' -PgBin $PgBin
    if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }

    Say "Dumping $($conn.Database) to $partial."
    Invoke-Pg -Exe $pgDump -Connection $conn -Arguments @('-Fc', '--no-password', '-f', $partial) | Out-Null
    $size = (Get-Item -LiteralPath $partial).Length
    if ($size -le 0) { throw 'The dump is empty.' }
    Invoke-Pg -Exe $pgRestore -Connection $conn -Arguments @('--list', $partial) | Out-Null

    if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Force }
    Move-Item -LiteralPath $partial -Destination $final
    Say ('Wrote {0} ({1} bytes).' -f $final, $size)

    $old = @(Get-ChildItem -LiteralPath $target -Filter 'qbautopost-*.dump' | Sort-Object Name -Descending | Select-Object -Skip $Keep)
    foreach ($file in $old) {
        Remove-Item -LiteralPath $file.FullName -Force
        Say "Removed old dump $($file.Name)."
    }
    exit 0
}
catch {
    Say $_.Exception.Message 'ERR'
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue }
    exit 1
}
